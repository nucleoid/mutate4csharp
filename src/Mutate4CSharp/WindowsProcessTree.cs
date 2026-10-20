using System.Collections;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Mutate4CSharp;

// A private, creation-owned boundary. Root exit and a returned kill request are
// observations, not evidence that descendants or redirected streams have settled.
internal sealed class WindowsProcessTree
{
    private static readonly ConcurrentBag<WindowsProcessTree> Unsettled = [];
    private static readonly TimeSpan LegacyCleanupLimit = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan GraphCleanupLimit = TimeSpan.FromSeconds(60);
    internal const int MaximumEnvironmentVariables = 4096;
    internal const int MaximumEnvironmentVariableCharacters = 32767;
    internal const int MaximumEnvironmentCharacters = 4 * 1024 * 1024;
    private readonly long _started;
    private readonly TimeSpan _timeout;
    private readonly TimeSpan _whole;
    private readonly ResolvedGraphBudget? _originalBudget;
    private readonly CancellationToken _caller;
    private readonly List<CloseRow> _resources = [];
    private readonly List<string> _directories = [];
    private readonly CancellationTokenSource _drainCancellation = new();
    private KernelHandle? _job, _process, _thread;
    private Task<string>? _output, _error;
    private long? _cleanupStarted;
    private bool _created, _assigned, _settled;

    private WindowsProcessTree(long started, TimeSpan timeout, CancellationToken caller,
        string directory, ResolvedGraphBudget? originalBudget)
    {
        _started = started; _timeout = timeout; _whole = timeout + LegacyCleanupLimit;
        _originalBudget = originalBudget;
        _caller = caller;
        _directories.Add(Path.GetFullPath(directory));
        Own(_drainCancellation);
    }

    internal static void AssertDirectoryMayBeRemoved(string directory)
    {
        var root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        foreach (var owner in Unsettled)
            foreach (var path in owner._directories)
                if (path.Equals(root.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase) ||
                    path.StartsWith(root, StringComparison.OrdinalIgnoreCase) ||
                    root.StartsWith(path.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                        StringComparison.OrdinalIgnoreCase))
                    throw new SnapshotCleanupException(new IOException(
                        "An original Windows process boundary is unsettled; its owned directory is retained."));
    }

    internal static async Task<ProcessTreeResult> RunAsync(string executable,
        IReadOnlyList<string> arguments, string directory, TimeSpan timeout,
        CancellationToken caller, IReadOnlyDictionary<string, string?>? environment, long maxOutputBytes,
        ResolvedGraphBudget? originalBudget = null)
    {
        var started = Stopwatch.GetTimestamp(); // Before environment/preflight/native acquisition.
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.MaxValue - LegacyCleanupLimit) throw new ArgumentOutOfRangeException(nameof(timeout));
        if (maxOutputBytes < 1) throw new ArgumentOutOfRangeException(nameof(maxOutputBytes));
        var owner = new WindowsProcessTree(started, timeout, caller, directory, originalBudget);
        Exception? primary = null;
        var timedOut = false;
        var exitCode = -1;
        try
        {
            owner.CheckWork();
            var environmentBlock = EnvironmentBlock(environment, owner.CheckWork, out var packageRoot);
            if (!string.IsNullOrWhiteSpace(packageRoot)) owner._directories.Add(Path.GetFullPath(packageRoot, Path.GetFullPath(directory)));
            owner.CheckWork();
            owner.Start(executable, arguments, directory, environmentBlock, maxOutputBytes);
            while (!owner.RootExited())
            {
                owner.CheckWork();
                if (owner._output!.IsFaulted || owner._output.IsCanceled) await owner._output;
                if (owner._error!.IsFaulted || owner._error.IsCanceled) await owner._error;
                await Task.Delay(TimeSpan.FromMilliseconds(10), caller);
            }
            owner.CheckWork();
            if (!GetExitCodeProcess(owner._process!, out var actualExit)) throw Native("root-exit-code");
            exitCode = unchecked((int)actualExit);
        }
        catch (OperationCanceledException failure)
        {
            // A linked graph work timer must not masquerade as user cancellation.
            // This observation never supplies more work or cleanup authority.
            timedOut = originalBudget?.WorkEndpointExpired == true;
            primary = timedOut ? new TimeoutException("Actual graph original work endpoint expired.", failure) : failure;
        }
        catch (TimeoutException failure)
        {
            timedOut = originalBudget?.WorkEndpointExpired == true || !caller.IsCancellationRequested;
            primary = timedOut ? failure : new OperationCanceledException(caller);
        }
        catch (Exception failure) { primary = failure; }

        if (primary is not null) originalBudget?.BeginFailureCleanup();
        Exception? cleanupFailure = null;
        try { await owner.SettleAsync(); }
        catch (Exception failure) { cleanupFailure = failure; }
        if (!owner._settled || cleanupFailure is not null)
        {
            // Retain the same graph, including actual handles/tasks and first-close
            // records. There is no PID adoption, retry, or unknown-to-success path.
            Unsettled.Add(owner);
            throw primary is null
                ? new SnapshotCleanupException(cleanupFailure ?? new IOException("Windows process boundary is unsettled."))
                : new SnapshotCleanupException(primary, cleanupFailure ?? new IOException("Windows process boundary is unsettled."));
        }
        if (primary is not null && !timedOut) ExceptionDispatchInfo.Capture(primary).Throw();
        return new(exitCode, timedOut, owner._output is null ? "" : await owner._output,
            owner._error is null ? "" : await owner._error, Stopwatch.GetElapsedTime(owner._started));
    }

    private void CheckWork()
    {
        _originalBudget?.CheckWork();
        _caller.ThrowIfCancellationRequested();
        if (Stopwatch.GetElapsedTime(_started) >= _timeout)
            throw new TimeoutException($"Process exceeded its {_timeout.TotalSeconds:g}-second timeout.");
    }

    private void CheckCleanup()
    {
        _cleanupStarted ??= Stopwatch.GetTimestamp();
        var limit = _originalBudget is null ? LegacyCleanupLimit : GraphCleanupLimit;
        if (Stopwatch.GetElapsedTime(_cleanupStarted.Value) >= limit ||
            _originalBudget is null && Stopwatch.GetElapsedTime(_started) >= _whole)
            throw new TimeoutException("Original Windows process cleanup endpoint expired; no deadline reset.");
        _originalBudget?.CheckDisposition();
    }

    private TimeSpan CleanupRemaining()
    {
        CheckCleanup();
        var limit = _originalBudget is null ? LegacyCleanupLimit : GraphCleanupLimit;
        var left = limit - Stopwatch.GetElapsedTime(_cleanupStarted!.Value);
        var whole = _originalBudget?.RemainingDisposition ?? _whole - Stopwatch.GetElapsedTime(_started);
        if (whole < left) left = whole;
        if (left <= TimeSpan.Zero) throw new TimeoutException("SAME original Windows process disposition allowance expired.");
        return left;
    }

    private void Start(string executable, IReadOnlyList<string> arguments, string directory,
        string environmentBlock, long maxOutputBytes)
    {
        _job = Own(CreateJobObjectW(IntPtr.Zero, null));
        if (_job.IsInvalid) throw Native("create-private-job");
        CheckWork();
        var limits = new ExtendedLimits { BasicLimitInformation = new() { LimitFlags = 0x2000 } };
        if (!SetInformationJobObject(_job, 9, ref limits, (uint)Marshal.SizeOf<ExtendedLimits>())) throw Native("kill-on-job-close");
        CheckWork();
        var (outRead, outWrite) = Pipe();
        CheckWork();
        var (errRead, errWrite) = Pipe();
        CheckWork();
        var (inRead, inWrite) = Pipe(parentReads: false);
        CheckWork();
        if (executable.Length >= 32767) throw new ArgumentException("Oversized Windows executable name.");
        var command = new StringBuilder(Quote(executable));
        foreach (var argument in arguments)
        {
            if (argument.Contains('\0') || argument.Length >= 32767) throw new ArgumentException("Invalid or oversized process argument.");
            command.Append(' ').Append(Quote(argument));
            if (command.Length >= 32767) throw new ArgumentException("Windows command line exceeds32766 characters.");
        }
        if (command.Length >= 32767) throw new ArgumentException("Windows command line exceeds32766 characters.");
        var attributes = Own(new HandleList(inRead, outWrite, errWrite));
        var startup = new StartupInfoEx
        {
            StartupInfo = new() { Cb = (uint)Marshal.SizeOf<StartupInfoEx>(), Flags = 0x100,
                StandardInput = inRead.DangerousGetHandle(), StandardOutput = outWrite.DangerousGetHandle(),
                StandardError = errWrite.DangerousGetHandle() },
            AttributeList = attributes.Pointer
        };
        CheckWork();
        // No target instruction can run before successful assignment to this Job.
        var created = CreateProcessW(null, command, IntPtr.Zero, IntPtr.Zero, true,
            0x4 | 0x08000000 | 0x400 | 0x80000, environmentBlock,
            Path.GetFullPath(directory), ref startup, out var actual);
        if (!created) throw Native("create-suspended");
        _created = true;
        _process = Own(new KernelHandle(actual.Process));
        _thread = Own(new KernelHandle(actual.Thread));
        CheckWork();
        if (!AssignProcessToJobObject(_job, _process)) throw Native("assign-before-resume");
        _assigned = true;
        CheckWork();
        Close(attributes);
        Close(outWrite); Close(errWrite); Close(inRead); Close(inWrite);
        // Transfer each retained read handle to exactly one retained stream owner.
        var stdout = TransferToStream(outRead);
        var stderr = TransferToStream(errRead);
        _output = ProcessTree.ReadBoundedAsync(stdout, maxOutputBytes, _drainCancellation.Token);
        _error = ProcessTree.ReadBoundedAsync(stderr, maxOutputBytes, _drainCancellation.Token);
        CheckWork();
        if (ResumeThread(_thread) == uint.MaxValue) throw Native("resume-owned-root");
        Close(_thread);
        CheckWork();
    }

    private bool RootExited()
    {
        if (!_created) return true;
        var wait = WaitForSingleObject(_process!, 0);
        return wait == 0 ? true : wait == 258 ? false : throw Native("observe-original-root");
    }

    private async Task SettleAsync()
    {
        CheckCleanup();
        if (_created)
        {
            // Root exit does not suppress descendant termination. A private Job
            // query, not process enumeration or EOF, establishes group emptiness.
            if (_assigned)
            {
                if (!QueryInformationJobObject(_job!, 1, out var accounting,
                        (uint)Marshal.SizeOf<BasicAccounting>(), IntPtr.Zero)) throw Native("observe-original-job");
                if (accounting.ActiveProcesses != 0 && !TerminateJobObject(_job!, unchecked((uint)-1))) throw Native("terminate-original-job");
            }
            else if (!RootExited() && !TerminateProcess(_process!, unchecked((uint)-1))) throw Native("terminate-unresumed-root");
            while (true)
            {
                CheckCleanup();
                var rootExited = RootExited();
                var active = 0U;
                if (_assigned)
                {
                    if (!QueryInformationJobObject(_job!, 1, out var accounting,
                            (uint)Marshal.SizeOf<BasicAccounting>(), IntPtr.Zero)) throw Native("read-original-job-empty");
                    active = accounting.ActiveProcesses;
                }
                if (rootExited && active == 0) break;
                await Task.Delay(TimeSpan.FromMilliseconds(10));
            }
        }
        Exception? captureFailure = null;
        if (_output is not null && _error is not null)
        {
            try { await Task.WhenAll(_output, _error).WaitAsync(CleanupRemaining()); }
            catch (Exception failure) { captureFailure = failure; }
        }
        // A bounded observer can fail before the underlying reads are terminal.
        // The independent deadline is not a substitute for this actual-task join.
        // Retain every original stream/CTS/native owner on that UNKNOWN path.
        if (_output is { IsCompleted: false } || _error is { IsCompleted: false })
            throw new IOException("Original Windows output reads are nonterminal; dependent first-close is refused.", captureFailure);
        // Faulted capture is never erased by physical closure. First dispositions
        // are independent; each is attempted once within the SAME cleanup deadline.
        List<Exception> failures = [];
        if (captureFailure is not null) failures.Add(captureFailure);
        foreach (var row in _resources.Where(item => item.TransferFailed))
            failures.Add(new IOException("Original pipe-to-stream construction did not return a known owner."));
        foreach (var row in _resources.AsEnumerable().Reverse())
        {
            if (row.Transferred || row.Closed) continue;
            try { CheckCleanup(); Close(row.Resource); CheckCleanup(); }
            catch (Exception failure) { failures.Add(failure); }
        }
        if (failures.Count > 0) throw new AggregateException("Original Windows capture/first-close failed.", failures);
        _settled = true;
    }

    private T Own<T>(T resource) where T : IDisposable { _resources.Add(new(resource)); return resource; }
    private void Close(IDisposable resource)
    {
        var row = _resources.Single(item => ReferenceEquals(item.Resource, resource));
        if (row.Entered) { if (!row.Closed) throw new IOException("Original resource first-close did not complete.", row.Failure); return; }
        row.Entered = true;
        try
        {
            resource.Dispose();
            if (resource is KernelHandle handle && !handle.IsInvalid && !handle.NativeClosed)
                throw new IOException("Original native CloseHandle did not report success.");
            row.Closed = true;
        }
        catch (Exception failure) { row.Failure = failure; throw; }
    }
    private FileStream TransferToStream(SafeFileHandle handle)
    {
        var row = _resources.Single(item => ReferenceEquals(item.Resource, handle));
        // A refused/throwing constructor is conservatively retained, not retried.
        row.Transferred = true;
        row.TransferFailed = true;
        var stream = Own(new FileStream(handle, FileAccess.Read, 4096, isAsync: false));
        row.TransferFailed = false;
        return stream;
    }
    private (SafeFileHandle Read, SafeFileHandle Write) Pipe(bool parentReads = true)
    {
        var security = new SecurityAttributes { Length = (uint)Marshal.SizeOf<SecurityAttributes>(), InheritHandle = 1 };
        if (!CreatePipe(out var read, out var write, ref security, 0)) throw Native("create-stdio-pipe");
        Own(read); Own(write);
        CheckWork();
        if (!SetHandleInformation(parentReads ? read : write, 1, 0)) throw Native("parent-pipe-not-inheritable");
        return (read, write);
    }
    private sealed class CloseRow(IDisposable resource)
    {
        internal IDisposable Resource { get; } = resource;
        internal bool Entered, Closed, Transferred, TransferFailed;
        internal Exception? Failure;
    }
    private static string Quote(string text)
    {
        if (text.Contains('\0')) throw new ArgumentException("NUL in Windows command.");
        // Match ProcessStartInfo.ArgumentList: cmd.exe switches must remain unquoted.
        if (text.Length != 0 && !text.Any(character => char.IsWhiteSpace(character) || character == '"'))
            return text;
        var value = new StringBuilder("\""); var slashes = 0;
        foreach (var character in text)
        {
            if (character == '\\') { slashes++; continue; }
            if (character == '"') value.Append('\\', slashes * 2 + 1).Append(character);
            else value.Append('\\', slashes).Append(character);
            slashes = 0;
        }
        return value.Append('\\', slashes * 2).Append('"').ToString();
    }
    private static string EnvironmentBlock(IReadOnlyDictionary<string, string?>? additions, Action check, out string? packageRoot)
    {
        // Windows permits a block above32Ki characters. This explicit resource
        // profile caps controlled copying at4Mi UTF16 characters (~8MiB), each
        // key/value at32767, and4096 entries before Job/pipe/process acquisition.
        // The framework's inherited OS snapshot precedes this bounded copy.
        check();
        var overrides = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        long overrideCharacters = 1; var overrideRows = 0;
        foreach (var item in additions ?? new Dictionary<string, string?>())
        {
            check(); Validate(item.Key, item.Value);
            if (++overrideRows > MaximumEnvironmentVariables ||
                (overrideCharacters += item.Key.Length + (item.Value?.Length ?? 0) + 2) > MaximumEnvironmentCharacters)
                throw new ExecutionEnvironmentUnavailableException("Finite Windows environment override profile refused before native birth.");
            overrides[item.Key] = item.Value;
        }
        var values = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        long total = 1;
        foreach (DictionaryEntry item in Environment.GetEnvironmentVariables())
        {
            check(); var key = (string)item.Key;
            if (!overrides.ContainsKey(key)) Add(key, (string)item.Value!);
        }
        foreach (var item in overrides)
            if (item.Value is not null) Add(item.Key, item.Value);
        values.TryGetValue("NUGET_PACKAGES", out packageRoot);
        if (values.Count == 0) total = 2;
        check(); var block = new StringBuilder(checked((int)total));
        foreach (var item in values) { check(); block.Append(item.Key).Append('=').Append(item.Value).Append('\0'); }
        if (values.Count == 0) block.Append('\0');
        block.Append('\0'); check(); return block.ToString();

        static void Validate(string key, string? value)
        {
            if (string.IsNullOrEmpty(key) || key.Contains('=') || key.Contains('\0') || value?.Contains('\0') == true ||
                key.Length > MaximumEnvironmentVariableCharacters || (value?.Length ?? 0) > MaximumEnvironmentVariableCharacters)
                throw new ExecutionEnvironmentUnavailableException("Finite Windows environment variable profile refused before native birth.");
        }
        void Add(string key, string value)
        {
            check(); Validate(key, value);
            if (values.Count >= MaximumEnvironmentVariables || (long)key.Length + value.Length + 2 > MaximumEnvironmentCharacters - total)
                throw new ExecutionEnvironmentUnavailableException("Finite Windows environment block profile refused before native birth.");
            total += key.Length + value.Length + 2; values.Add(key, value);
        }
    }
    private static Win32Exception Native(string stage) => new(Marshal.GetLastWin32Error(), stage);
    private sealed class KernelHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public KernelHandle() : base(true) { }
        internal KernelHandle(IntPtr pointer) : base(true) => SetHandle(pointer);
        internal bool NativeClosed;
        protected override bool ReleaseHandle() => NativeClosed = CloseHandle(handle);
    }
    private sealed class HandleList : IDisposable
    {
        private IntPtr _values;
        private bool _initialized;
        internal IntPtr Pointer { get; private set; }
        internal HandleList(params SafeFileHandle[] handles)
        {
            try
            {
                nuint size = 0; InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
                if (size == 0 || size > 65536) throw Native("attribute-size");
                Pointer = Marshal.AllocHGlobal(checked((int)size));
                if (!InitializeProcThreadAttributeList(Pointer, 1, 0, ref size)) throw Native("attribute-init");
                _initialized = true; _values = Marshal.AllocHGlobal(IntPtr.Size * handles.Length);
                for (var index = 0; index < handles.Length; index++) Marshal.WriteIntPtr(_values, index * IntPtr.Size, handles[index].DangerousGetHandle());
                if (!UpdateProcThreadAttribute(Pointer, 0, (nuint)0x20002, _values, (nuint)(IntPtr.Size * handles.Length), IntPtr.Zero, IntPtr.Zero)) throw Native("stdio-handle-list");
            }
            catch { Dispose(); throw; }
        }
        public void Dispose()
        {
            if (_initialized) { DeleteProcThreadAttributeList(Pointer); _initialized = false; }
            if (Pointer != IntPtr.Zero) { Marshal.FreeHGlobal(Pointer); Pointer = IntPtr.Zero; }
            if (_values != IntPtr.Zero) { Marshal.FreeHGlobal(_values); _values = IntPtr.Zero; }
        }
    }
    [StructLayout(LayoutKind.Sequential)] private struct SecurityAttributes { public uint Length; public IntPtr Descriptor; public int InheritHandle; }
    [StructLayout(LayoutKind.Sequential)] private struct StartupInfo { public uint Cb; public IntPtr Reserved, Desktop, Title; public uint X, Y, XSize, YSize, XChars, YChars, FillAttribute, Flags; public ushort Show, ReservedSize; public IntPtr Reserved2, StandardInput, StandardOutput, StandardError; }
    [StructLayout(LayoutKind.Sequential)] private struct StartupInfoEx { public StartupInfo StartupInfo; public IntPtr AttributeList; }
    [StructLayout(LayoutKind.Sequential)] private struct ProcessInformation { public IntPtr Process, Thread; public uint ProcessId, ThreadId; }
    [StructLayout(LayoutKind.Sequential)] private struct BasicLimits { public long ProcessTime, JobTime; public uint LimitFlags; public nuint MinimumWorkingSet, MaximumWorkingSet; public uint ActiveProcessLimit; public nuint Affinity; public uint Priority, Scheduling; }
    [StructLayout(LayoutKind.Sequential)] private struct IoCounters { public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes; }
    [StructLayout(LayoutKind.Sequential)] private struct ExtendedLimits { public BasicLimits BasicLimitInformation; public IoCounters Io; public nuint ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory; }
    [StructLayout(LayoutKind.Sequential)] private struct BasicAccounting { public long User, Kernel, PeriodUser, PeriodKernel; public uint Faults, TotalProcesses, ActiveProcesses, TerminatedProcesses; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern KernelHandle CreateJobObjectW(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetInformationJobObject(KernelHandle job, int type, ref ExtendedLimits limits, uint length);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool QueryInformationJobObject(KernelHandle job, int type, out BasicAccounting data, uint length, IntPtr returned);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool AssignProcessToJobObject(KernelHandle job, KernelHandle process);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool TerminateJobObject(KernelHandle job, uint code);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool TerminateProcess(KernelHandle process, uint code);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(KernelHandle process, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetExitCodeProcess(KernelHandle process, out uint code);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint ResumeThread(KernelHandle thread);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, ref SecurityAttributes attributes, uint size);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetHandleInformation(SafeFileHandle handle, uint mask, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool InitializeProcThreadAttributeList(IntPtr pointer, int count, uint flags, ref nuint size);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool UpdateProcThreadAttribute(IntPtr pointer, uint flags, nuint attribute, IntPtr value, nuint size, IntPtr previous, IntPtr returned);
    [DllImport("kernel32.dll")] private static extern void DeleteProcThreadAttributeList(IntPtr pointer);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CreateProcessW(string? executable, StringBuilder command, IntPtr processAttributes, IntPtr threadAttributes, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint flags, string environment, string directory, ref StartupInfoEx startup, out ProcessInformation process);
}
