using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Mutate4CSharp;

internal sealed record ProcessTreeResult(int ExitCode, bool TimedOut, string StandardOutput,
    string StandardError, TimeSpan Duration);

internal readonly record struct ProcessPlatformCapabilities(bool IsLinux)
{
    public static ProcessPlatformCapabilities Current => new(OperatingSystem.IsLinux());
}

internal static class ProcessTree
{
    private static readonly TimeSpan LinuxDescendantReapingTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan LinuxObservationInitialDelay = TimeSpan.FromMilliseconds(10);
    private static readonly TimeSpan LinuxObservationMaximumDelay = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan LinuxGroupScanInitialDelay = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan LinuxGroupScanMaximumDelay = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan OutputDrainTimeout = TimeSpan.FromSeconds(5);
    private static readonly ConcurrentDictionary<string, byte> VerifiedLinuxSetSidExecutables =
        new(StringComparer.Ordinal);
    private const long DefaultMaxOutputBytes = 32L * 1024 * 1024;

    public static Task<ProcessTreeResult> RunAsync(string executable, IReadOnlyList<string> arguments,
        string workingDirectory, TimeSpan timeout, CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string?>? environment = null, long maxOutputBytes = DefaultMaxOutputBytes,
        bool requireLinuxSessionIsolation = false, ResolvedGraphBudget? originalBudget = null) =>
        RunAsync(ProcessPlatformCapabilities.Current, executable, arguments, workingDirectory, timeout,
            cancellationToken, environment, maxOutputBytes, requireLinuxSessionIsolation, originalBudget);

    internal static async Task<ProcessTreeResult> RunAsync(ProcessPlatformCapabilities platform,
        string executable, IReadOnlyList<string> arguments, string workingDirectory, TimeSpan timeout,
        CancellationToken cancellationToken, IReadOnlyDictionary<string, string?>? environment = null,
        long maxOutputBytes = DefaultMaxOutputBytes, bool requireLinuxSessionIsolation = false,
        ResolvedGraphBudget? originalBudget = null)
    {
        if (OperatingSystem.IsWindows())
            return await WindowsProcessTree.RunAsync(executable, arguments, workingDirectory, timeout,
                cancellationToken, environment, maxOutputBytes, originalBudget);
        CheckWork();
        using var strictLaunch = platform.IsLinux && requireLinuxSessionIsolation
            ? LinuxStrictLaunch.Create()
            : null;
        var useStrictLinuxBoundary = strictLaunch is not null;
        var launchExecutable = executable;
        var launchArguments = arguments;
        if (strictLaunch is not null)
        {
            launchExecutable = ResolveLinuxSetSidForOriginal(originalBudget, cancellationToken);
            launchArguments = ["--wait", "/bin/sh", "-c", LinuxStrictLaunch.WrapperScript,
                "mutate4csharp-boundary", strictLaunch.StatusPath, executable, .. arguments];
        }
        var info = new ProcessStartInfo(launchExecutable)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in launchArguments) info.ArgumentList.Add(argument);
        if (environment is not null)
            foreach (var item in environment)
            {
                foreach (var inherited in info.Environment.Keys.Where(key =>
                             key.Equals(item.Key, StringComparison.OrdinalIgnoreCase)).ToArray())
                    info.Environment.Remove(inherited);
                if (item.Value is not null) info.Environment[item.Key] = item.Value;
            }
        using var legacyProcess = originalBudget is null ? new Process { StartInfo = info } : null;
        using var originalProcess = originalBudget is null ? null : new LinuxOriginalProcessFrame(
            new Process { StartInfo = info }, originalBudget,
            () => strictLaunch?.RetainUnsettled(), () => strictLaunch?.MarkSettled());
        var process = legacyProcess ?? originalProcess!.Process;
        var watch = Stopwatch.StartNew();
        CheckWork();
        var workTimeout = originalBudget is null || timeout < originalBudget.RemainingWork
            ? timeout : originalBudget.RemainingWork;
        if (originalBudget is not null && workTimeout <= TimeSpan.Zero)
            throw new TimeoutException("Actual graph original work endpoint expired before process birth.");
        if (!(originalProcess?.Start() ?? process.Start())) throw new InvalidOperationException($"Could not start {executable}.");
        var linuxIdentity = platform.IsLinux && TryReadLinuxIdentity(process.Id, out var launched)
            ? launched
            : (LinuxProcessIdentity?)null;
        var linuxBoundary = new LinuxProcessBoundary(linuxIdentity, useStrictLinuxBoundary);
        if (originalProcess is not null) originalProcess.Boundary = linuxBoundary;
        using var legacyDrain = originalBudget is null ? new CancellationTokenSource() : null;
        var drainCancellation = legacyDrain ?? originalProcess!.Own(new CancellationTokenSource());
        var outputTask = ReadBoundedAsync(process.StandardOutput.BaseStream, maxOutputBytes, drainCancellation.Token);
        var errorTask = ReadBoundedAsync(process.StandardError.BaseStream, maxOutputBytes, drainCancellation.Token);
        originalProcess?.Track(outputTask); originalProcess?.Track(errorTask);
        using var legacyTimeout = originalBudget is null ? new CancellationTokenSource(workTimeout) : null;
        var timeoutSource = legacyTimeout ?? originalProcess!.Own(new CancellationTokenSource(workTimeout));
        using var legacyLinked = originalBudget is null ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token) : null;
        var linked = legacyLinked ?? originalProcess!.Own(CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token));
        var timedOut = false;
        ResolvedGraphWorkExpiredException? workExpiry = null;
        int? strictExitCode = null;
        Task? completionTask = null;
        try
        {
            CheckWork();
            var processTask = process.WaitForExitAsync(linked.Token);
            originalProcess?.Track(processTask);
            Task<int>? strictCompletionTask = strictLaunch is null
                ? null
                : WaitForStrictCompletionAsync(strictLaunch.StatusPath, processTask, linked.Token);
            completionTask = strictCompletionTask ?? processTask;
            if (strictCompletionTask is not null) originalProcess?.Track(strictCompletionTask);
            var pending = new List<Task> { completionTask, outputTask, errorTask };
            var observationDelay = LinuxObservationInitialDelay;
            var groupScanDelay = LinuxGroupScanInitialDelay;
            var nextGroupScan = groupScanDelay;
            while (!completionTask.IsCompleted)
            {
                CheckWork();
                var includeGroupScan = watch.Elapsed >= nextGroupScan;
                CaptureLinuxOwnedProcesses(linuxBoundary, includeGroupScan);
                if (includeGroupScan)
                {
                    groupScanDelay = TimeSpan.FromMilliseconds(Math.Min(
                        LinuxGroupScanMaximumDelay.TotalMilliseconds, groupScanDelay.TotalMilliseconds * 2));
                    nextGroupScan = watch.Elapsed + groupScanDelay;
                }
                var observation = Task.Delay(observationDelay, CancellationToken.None);
                var completed = await Task.WhenAny(pending.Append(observation));
                if (completed == observation)
                {
                    observationDelay = TimeSpan.FromMilliseconds(Math.Min(
                        LinuxObservationMaximumDelay.TotalMilliseconds, observationDelay.TotalMilliseconds * 2));
                    continue;
                }
                if (completed == completionTask) break;
                pending.Remove(completed);
                await completed;
            }
            if (strictCompletionTask is null) await processTask;
            else strictExitCode = await strictCompletionTask;
            CheckWork();
            if (useStrictLinuxBoundary) CaptureLinuxOwnedProcesses(linuxBoundary);
        }
        catch (Exception interruption) when (interruption is OperationCanceledException ||
            interruption is TimeoutException && originalBudget is not null)
        {
            if (originalBudget?.TryBeginWorkExpiry() == true)
                workExpiry = interruption as ResolvedGraphWorkExpiredException ?? new ResolvedGraphWorkExpiredException();
            timedOut = workExpiry is not null || interruption is TimeoutException ||
                timeoutSource.IsCancellationRequested && !cancellationToken.IsCancellationRequested;
            Exception primaryFailure = workExpiry is not null ? workExpiry : timedOut
                ? interruption is TimeoutException ? interruption : new TimeoutException(originalBudget is null
                    ? $"Process exceeded its {timeout.TotalSeconds:g}-second timeout."
                    : "Process exceeded its original or per-call work endpoint.", interruption)
                : interruption;
            // The scheduler's per-mutant timeout is a subordinate caller token,
            // not the aggregate owner's cancellation. Retire known local stops
            // without poisoning the SAME original; unknown retention below is terminal.
            if (originalBudget is not null && (originalBudget.CancellationRequested || workExpiry is not null))
                originalBudget.BeginFailureCleanup();
            try { await StopRootForCleanup(); }
            catch (Exception cleanup) { throw CreateCleanupFailure(primaryFailure, cleanup); }
            await CompleteCancellationCleanupAsync(primaryFailure,
                async () => { _ = await AwaitOutputsAsync(outputTask, errorTask, drainCancellation, originalBudget, originalProcess); },
                () => linuxBoundary.Isolated
                    ? TerminateAndReapLinuxBoundaryAsync(linuxBoundary, originalBudget, originalProcess)
                    : Task.CompletedTask);
            if (!timedOut)
            {
                try
                {
                    if (originalProcess is not null) await originalProcess.JoinObserversAsync();
                    originalProcess?.CloseSettled();
                    originalProcess?.PreserveObserverFailures(primaryFailure);
                    // Local cancellation can cross the original WORK endpoint
                    // during known retirement. Actual capture/first-close faults
                    // must remain failures, never disappear behind typed expiry.
                    if (originalBudget?.TryBeginWorkExpiry() == true)
                    {
                        originalProcess?.ThrowObserverFailures();
                        strictLaunch?.MarkSettled();
                        workExpiry = new ResolvedGraphWorkExpiredException();
                    }
                }
                catch (Exception cleanup) { throw CreateCleanupFailure(primaryFailure, cleanup); }
                if (workExpiry is not null) throw workExpiry;
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(interruption).Throw();
            }
        }
        catch (Exception primaryFailure)
        {
            originalBudget?.BeginFailureCleanup();
            try { await StopRootForCleanup(); }
            catch (Exception cleanup) { throw CreateCleanupFailure(primaryFailure, cleanup); }
            CancelAndObserveOutputs(outputTask, errorTask, drainCancellation);
            try
            {
                if (linuxBoundary.Isolated) await TerminateAndReapLinuxBoundaryAsync(linuxBoundary, originalBudget, originalProcess);
                if (originalProcess is not null)
                {
                    // Cancellation requests are not task-completion observations.
                    // A capture fault remains the primary failure, but terminal
                    // faulted reads may be first-closed after native settlement.
                    await originalProcess.JoinObserversAsync();
                    originalProcess.CloseSettled();
                    originalProcess.PreserveObserverFailures(primaryFailure);
                }
            }
            catch (Exception cleanupFailure) { throw CreateCleanupFailure(primaryFailure, cleanupFailure); }
            throw;
        }
        if (useStrictLinuxBoundary)
        {
            var strictOutput = await CompleteStrictSuccessAsync(process, linuxBoundary, outputTask, errorTask,
                drainCancellation, originalBudget, originalProcess);
            if (originalProcess is not null) await originalProcess.JoinObserversAsync();
            originalProcess?.CloseSettled();
            originalProcess?.ThrowObserverFailures();
            strictLaunch!.MarkSettled();
            if (originalBudget?.TryBeginWorkExpiry() == true)
                throw workExpiry ?? new ResolvedGraphWorkExpiredException();
            return new(strictExitCode ?? -1, timedOut, strictOutput.Output, strictOutput.Error, watch.Elapsed);
        }
        var (output, error) = await AwaitOutputsAsync(outputTask, errorTask, drainCancellation, originalBudget, originalProcess);
        var result = new ProcessTreeResult(process.HasExited ? process.ExitCode : -1, timedOut, output, error, watch.Elapsed);
        if (originalProcess is not null) await originalProcess.JoinObserversAsync();
        originalProcess?.CloseSettled();
        originalProcess?.ThrowObserverFailures();
        if (originalBudget?.TryBeginWorkExpiry() == true)
            throw workExpiry ?? new ResolvedGraphWorkExpiredException();
        return result;

        void CheckWork() { originalBudget?.CheckWork(); cancellationToken.ThrowIfCancellationRequested(); }
        async Task WaitForRootCleanup()
        {
            _ = originalProcess?.RemainingRetirement;
            originalBudget?.CheckDisposition();
            var exit = process.WaitForExitAsync(CancellationToken.None);
            originalProcess?.Track(exit);
            if (originalBudget is null) await exit;
            else await exit.WaitAsync(originalProcess!.RemainingRetirement);
            _ = originalProcess?.RemainingRetirement;
            originalBudget?.CheckDisposition();
        }
        async Task StopRootForCleanup()
        {
            // One local first-retirement endpoint, clipped to the SAME original.
            _ = originalProcess?.RemainingRetirement;
            originalBudget?.CheckDisposition();
            linked.Cancel(); // The original completion observer must also become terminal.
            KillLinuxBoundaryForFailure(linuxBoundary, originalBudget, originalProcess);
            originalBudget?.CheckDisposition();
            if (originalProcess is null)
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            }
            else
            {
                var exited = process.HasExited;
                _ = originalProcess.RemainingRetirement;
                originalBudget!.CheckDisposition();
                if (!exited)
                {
                    // Endpoint checks stay outside the legacy best-effort catch.
                    try { process.Kill(entireProcessTree: true); } catch { }
                }
                _ = originalProcess.RemainingRetirement;
            }
            if (originalBudget is null) { try { await WaitForRootCleanup(); } catch { } }
            else await WaitForRootCleanup();
            if (completionTask is not null)
            {
                try { await completionTask.WaitAsync(originalProcess?.RemainingRetirement ?? OutputDrainTimeout); }
                catch (OperationCanceledException) { } // Cancellation is an observed terminal observer state.
                _ = originalProcess?.RemainingRetirement;
                originalBudget?.CheckDisposition();
            }
        }
    }

    private static async Task<int> WaitForStrictCompletionAsync(string statusPath, Task processTask,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (File.Exists(statusPath))
                {
                    var value = await File.ReadAllTextAsync(statusPath, cancellationToken);
                    if (int.TryParse(value.Trim(), NumberStyles.None, CultureInfo.InvariantCulture,
                            out var exitCode) && exitCode is >= 0 and <= 255)
                        return exitCode;
                    throw new IOException("The strict Linux execution wrapper published an invalid exit code.");
                }
            }
            catch (FileNotFoundException) { }
            if (processTask.IsCompleted)
            {
                await processTask;
                throw new IOException("The strict Linux execution wrapper exited without publishing an exit code.");
            }
            await Task.Delay(LinuxObservationInitialDelay, cancellationToken);
        }
    }

    private static void KillLinuxBoundaryForFailure(LinuxProcessBoundary boundary, ResolvedGraphBudget? originalBudget = null,
        LinuxOriginalProcessFrame? originalProcess = null)
    {
        _ = originalProcess?.RemainingRetirement;
        originalBudget?.CheckDisposition();
        if (boundary.Isolated)
        {
            CaptureLinuxOwnedProcesses(boundary);
            _ = originalProcess?.RemainingRetirement;
            originalBudget?.CheckDisposition();
            TryKillLinuxProcessGroups(boundary, originalBudget, originalProcess);
            TryKillLinuxProcesses(boundary.Owned, originalBudget, originalProcess);
            return;
        }
        if (boundary.Launched is not { } launched) return;
        var same = IsSameLinuxProcess(launched);
        _ = originalProcess?.RemainingRetirement;
        originalBudget?.CheckDisposition();
        if (!same) return;
        var descendants = CaptureLinuxDescendants(launched.Pid);
        _ = originalProcess?.RemainingRetirement;
        originalBudget?.CheckDisposition();
        TryKillLinuxProcesses(descendants, originalBudget, originalProcess);
    }

    private static async Task<(string Output, string Error)> CompleteStrictSuccessAsync(Process process,
        LinuxProcessBoundary boundary, Task<string> output, Task<string> error,
        CancellationTokenSource drainCancellation, ResolvedGraphBudget? originalBudget = null,
        LinuxOriginalProcessFrame? originalProcess = null)
    {
        var failures = new List<Exception>();
        try { await TerminateAndReapLinuxBoundaryAsync(boundary, originalBudget, originalProcess); }
        catch (Exception ex) { failures.Add(ex); }
        try
        {
            _ = originalProcess?.RemainingRetirement;
            originalBudget?.CheckDisposition();
            var exit = process.WaitForExitAsync(CancellationToken.None);
            if (originalBudget is null) await exit;
            else await exit.WaitAsync(originalProcess?.RemainingRetirement ?? originalBudget.RemainingDisposition);
            _ = originalProcess?.RemainingRetirement;
            originalBudget?.CheckDisposition();
        }
        catch (Exception ex) { failures.Add(ex); }
        (string Output, string Error) drained = default;
        try { drained = await AwaitOutputsAsync(output, error, drainCancellation, originalBudget, originalProcess); }
        catch (Exception ex) { failures.Add(ex); }
        if (failures.Count == 0) return drained;

        CancelAndObserveOutputs(output, error, drainCancellation);
        try { await TerminateAndReapLinuxBoundaryAsync(boundary, originalBudget, originalProcess); }
        catch (Exception ex) { failures.Add(ex); }
        var cleanupFailure = failures.Count == 1
            ? failures[0]
            : new AggregateException("Strict Linux process completion had multiple cleanup failures.", failures);
        throw CreateCleanupFailure(
            new IOException("The process exited, but its strict Linux execution boundary was not clean and drained."),
            cleanupFailure);
    }

    private static SnapshotCleanupException CreateCleanupFailure(Exception primaryFailure,
        Exception cleanupFailure) => new(primaryFailure, cleanupFailure);

    private static bool SetSidSupportsWait(string help) =>
        Regex.IsMatch(help, @"(?m)(?:^|\s)-w(?:\s*,\s*|\s+)--wait(?:\s|$)", RegexOptions.CultureInvariant);

    private static async Task CompleteCancellationCleanupAsync(Exception primaryFailure,
        Func<Task> drainOutput, Func<Task> reapDescendants)
    {
        List<Exception>? failures = null;
        try { await drainOutput(); }
        catch (Exception ex) { (failures ??= []).Add(ex); }
        try { await reapDescendants(); }
        catch (Exception ex) { (failures ??= []).Add(ex); }
        if (failures is null) return;
        var cleanupFailure = failures.Count == 1
            ? failures[0]
            : new AggregateException("Multiple process cleanup steps failed.", failures);
        throw CreateCleanupFailure(primaryFailure, cleanupFailure);
    }

    private static string ResolveLinuxSetSid() => ResolveLinuxSetSidForOriginal(null, CancellationToken.None);

    private static string ResolveLinuxSetSidForOriginal(ResolvedGraphBudget? originalBudget, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); originalBudget?.CheckWork();
        var executable = FindLinuxSetSid();
        if (executable is null)
            throw new InvalidOperationException("Linux process cleanup requires util-linux setsid with --wait support.");
        if (VerifiedLinuxSetSidExecutables.ContainsKey(executable)) return executable;
        string help;
        bool waitWorks;
        try
        {
            help = ReadSetSidHelp(executable, originalBudget, token);
            waitWorks = SetSidSupportsWait(help) && ProbeSetSidWait(executable, originalBudget, token);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or
                                   InvalidOperationException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                $"Linux process cleanup could not start or inspect setsid candidate {BoundDiagnostic(executable, 192)}.", ex);
        }
        if (!waitWorks)
            throw new InvalidOperationException(
                $"Linux process cleanup requires util-linux setsid with working --wait support; " +
                $"{BoundDiagnostic(executable, 192)} is incompatible.");
        VerifiedLinuxSetSidExecutables.TryAdd(executable, 0);
        return executable;
    }

    private static string? FindLinuxSetSid()
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate;
            try { candidate = Path.GetFullPath(Path.Combine(directory, "setsid")); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            { continue; }
            if (IsExecutableFile(candidate)) return candidate;
        }
        foreach (var candidate in new[] { "/usr/bin/setsid", "/bin/setsid" })
            if (IsExecutableFile(candidate)) return candidate;
        return null;
    }

    private static bool IsExecutableFile(string path)
    {
        if (!OperatingSystem.IsLinux() || !File.Exists(path)) return false;
        try
        {
            var mode = File.GetUnixFileMode(path);
            return (mode & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private static string BoundDiagnostic(string value, int maximum) =>
        EvaluationTextBounds.Prefix(value, maximum);

    private static string ReadSetSidHelp(string executable, ResolvedGraphBudget? originalBudget, CancellationToken token)
    {
        var result = RunSetSidProbe(executable, ["--help"], originalBudget, token);
        if (result.ExitCode != 0)
            throw new InvalidOperationException(
                $"Could not inspect {executable} for --wait support (exit {result.ExitCode}).");
        return result.Output + "\n" + result.Error;
    }

    private static bool ProbeSetSidWait(string executable, ResolvedGraphBudget? originalBudget, CancellationToken token)
    {
        var result = RunSetSidProbe(executable, ["--wait", "/bin/sh", "-c", "exit 23"], originalBudget, token);
        return result.ExitCode == 23;
    }

    private static (int ExitCode, string Output, string Error) RunSetSidProbe(
        string executable, IReadOnlyList<string> arguments, ResolvedGraphBudget? originalBudget, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); originalBudget?.CheckWork();
        var info = new ProcessStartInfo(executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        token.ThrowIfCancellationRequested(); originalBudget?.CheckWork();
        var work = TimeSpan.FromSeconds(2);
        if (originalBudget is not null && originalBudget.RemainingWork < work) work = originalBudget.RemainingWork;
        if (work <= TimeSpan.Zero) throw new TimeoutException("Actual graph original work endpoint expired before probe birth.");
        using var legacyProbe = originalBudget is null ? new Process { StartInfo = info } : null;
        using var originalProbe = originalBudget is null ? null : new LinuxOriginalProcessFrame(new Process { StartInfo = info }, originalBudget);
        var probe = legacyProbe ?? originalProbe!.Process;
        token.ThrowIfCancellationRequested(); originalBudget?.CheckWork();
        if (!(originalProbe?.Start() ?? probe.Start())) throw new InvalidOperationException($"Could not inspect {executable}.");
        var output = ReadBoundedAsync(probe.StandardOutput.BaseStream, 64 * 1024, CancellationToken.None);
        var error = ReadBoundedAsync(probe.StandardError.BaseStream, 64 * 1024, CancellationToken.None);
        originalProbe?.Track(output); originalProbe?.Track(error);
        try
        {
            token.ThrowIfCancellationRequested(); originalBudget?.CheckWork();
            var exit = probe.WaitForExitAsync(); originalProbe?.Track(exit);
            Task.WhenAll(exit, output, error).WaitAsync(work, token)
                .GetAwaiter().GetResult();
            token.ThrowIfCancellationRequested(); originalBudget?.CheckWork();
        }
        catch (Exception ex)
        {
            originalBudget?.BeginFailureCleanup();
            try
            {
                originalBudget?.CheckDisposition();
                try { probe.Kill(entireProcessTree: true); } catch { }
                var exit = probe.WaitForExitAsync(CancellationToken.None);
                originalProbe?.Track(exit);
                var cleanup = TimeSpan.FromSeconds(2);
                if (originalBudget is not null && originalBudget.RemainingDisposition < cleanup)
                    cleanup = originalBudget.RemainingDisposition;
                try { Task.WhenAll(exit, output, error).WaitAsync(cleanup).GetAwaiter().GetResult(); }
                catch when (exit.IsCompletedSuccessfully && output.IsCompleted && error.IsCompleted) { }
                originalBudget?.CheckDisposition();
                originalProbe?.JoinObserversAsync().GetAwaiter().GetResult();
                originalProbe?.CloseSettled();
                originalProbe?.PreserveObserverFailures(ex);
            }
            catch (Exception cleanup) { throw CreateCleanupFailure(ex, cleanup); }
            if (ex is not TimeoutException) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex).Throw();
            throw new InvalidOperationException(
                $"Timed out inspecting {executable} for --wait support.", ex);
        }
        var result = (probe.ExitCode, output.GetAwaiter().GetResult(), error.GetAwaiter().GetResult());
        originalProbe?.JoinObserversAsync().GetAwaiter().GetResult();
        originalProbe?.CloseSettled();
        originalProbe?.ThrowObserverFailures();
        return result;
    }

    private static async Task<(string Output, string Error)> AwaitOutputsAsync(Task<string> output,
        Task<string> error, CancellationTokenSource drainCancellation, ResolvedGraphBudget? originalBudget = null,
        LinuxOriginalProcessFrame? originalProcess = null)
    {
        try
        {
            originalBudget?.CheckDisposition();
            var remaining = OutputDrainTimeout;
            if (originalBudget is not null)
            {
                var existing = originalProcess?.RemainingRetirement ?? originalBudget.RemainingDisposition;
                if (existing < remaining) remaining = existing;
            }
            await Task.WhenAll(output, error).WaitAsync(remaining);
            _ = originalProcess?.RemainingRetirement;
            originalBudget?.CheckDisposition();
            return (await output, await error);
        }
        catch (TimeoutException ex)
        {
            CancelAndObserveOutputs(output, error, drainCancellation);
            throw new IOException(
                $"Timed out after {OutputDrainTimeout.TotalSeconds:g} seconds draining process output.", ex);
        }
    }

    private static void CancelAndObserveOutputs(Task<string> output, Task<string> error,
        CancellationTokenSource drainCancellation)
    {
        drainCancellation.Cancel();
        _ = Task.WhenAll(output, error).ContinueWith(task => _ = task.Exception,
            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
    }

    internal static async Task<string> ReadBoundedAsync(Stream stream, long maxBytes,
        CancellationToken cancellationToken)
    {
        if (maxBytes < 1) throw new ArgumentOutOfRangeException(nameof(maxBytes));
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        long total = 0;
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
        {
            checked { total += read; }
            if (total > maxBytes)
                throw new IOException($"Process output exceeded the {maxBytes}-byte limit.");
            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken);
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static IReadOnlyList<LinuxProcessIdentity> CaptureLinuxDescendants(int rootPid)
    {
        if (!OperatingSystem.IsLinux()) return [];
        var descendants = new List<LinuxProcessIdentity>();
        var pending = new Queue<int>();
        var seen = new HashSet<int> { rootPid };
        pending.Enqueue(rootPid);
        while (pending.TryDequeue(out var parentPid))
        {
            string children;
            try { children = File.ReadAllText($"/proc/{parentPid}/task/{parentPid}/children"); }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }
            foreach (var value in children.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            {
                if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var childPid) ||
                    !seen.Add(childPid) || !TryReadLinuxIdentity(childPid, out var identity) ||
                    identity.ParentPid != parentPid) continue;
                descendants.Add(identity);
                pending.Enqueue(childPid);
            }
        }
        return descendants;
    }

    private static IReadOnlyList<LinuxProcessIdentity> CaptureLinuxProcessGroup(int processGroupId)
    {
        if (!OperatingSystem.IsLinux() || !Directory.Exists("/proc")) return [];
        var members = new List<LinuxProcessIdentity>();
        foreach (var path in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(path), NumberStyles.None, CultureInfo.InvariantCulture, out var pid) ||
                !TryReadLinuxIdentity(pid, out var identity) ||
                identity.ProcessGroupId != processGroupId && identity.SessionId != processGroupId) continue;
            members.Add(identity);
        }
        return members;
    }

    private static void CaptureLinuxOwnedProcesses(LinuxProcessBoundary boundary, bool includeGroupScan = true)
    {
        if (!OperatingSystem.IsLinux() || boundary.Launched is null || !boundary.Isolated) return;
        if (includeGroupScan && boundary.Isolated && IsIsolationBoundaryPinned(boundary))
            foreach (var member in CaptureLinuxProcessGroup(boundary.ProcessGroupId)) boundary.Add(member);
        while (true)
        {
            var added = false;
            foreach (var owned in boundary.Owned.ToArray())
            {
                if (!TryReadLinuxIdentity(owned.Pid, out var current) || current.IsZombie ||
                    !HasContinuousLinuxIdentity(owned, current)) continue;
                boundary.Add(current);
                foreach (var descendant in CaptureLinuxDescendants(current.Pid))
                    added |= boundary.Add(descendant);
            }
            if (!added) return;
        }
    }

    private static bool IsIsolationBoundaryPinned(LinuxProcessBoundary boundary)
    {
        if (!boundary.Isolated) return false;
        foreach (var expected in boundary.Owned)
        {
            if (!TryReadLinuxIdentity(expected.Pid, out var current) || current.IsZombie ||
                !HasContinuousLinuxIdentity(expected, current)) continue;
            if (current.ProcessGroupId == boundary.ProcessGroupId || current.SessionId == boundary.ProcessGroupId)
                return true;
        }
        return false;
    }

    private static void TryKillLinuxProcesses(IEnumerable<LinuxProcessIdentity> processes, ResolvedGraphBudget? originalBudget = null,
        LinuxOriginalProcessFrame? originalProcess = null)
    {
        foreach (var identity in processes)
        {
            _ = originalProcess?.RemainingRetirement;
            originalBudget?.CheckDisposition();
            var same = IsSameLinuxProcess(identity);
            _ = originalProcess?.RemainingRetirement;
            originalBudget?.CheckDisposition();
            if (!same) continue;
            try
            {
                using var process = Process.GetProcessById(identity.Pid);
                _ = originalProcess?.RemainingRetirement;
                originalBudget?.CheckDisposition();
                process.Kill(entireProcessTree: true);
            }
            catch (ArgumentException) { }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
            catch (NotSupportedException) { }
            _ = originalProcess?.RemainingRetirement;
            originalBudget?.CheckDisposition();
        }
    }

    private static async Task TerminateAndReapLinuxBoundaryAsync(LinuxProcessBoundary boundary, ResolvedGraphBudget? originalBudget = null,
        LinuxOriginalProcessFrame? originalProcess = null)
    {
        if (!OperatingSystem.IsLinux() || boundary.Launched is null) return;
        var watch = Stopwatch.StartNew();
        var groupScanDelay = LinuxGroupScanInitialDelay;
        var nextGroupScan = TimeSpan.Zero;
        while (true)
        {
            _ = originalProcess?.RemainingRetirement;
            originalBudget?.CheckDisposition();
            var includeGroupScan = watch.Elapsed >= nextGroupScan;
            CaptureLinuxOwnedProcesses(boundary, includeGroupScan);
            _ = originalProcess?.RemainingRetirement;
            originalBudget?.CheckDisposition();
            if (includeGroupScan)
            {
                groupScanDelay = TimeSpan.FromMilliseconds(Math.Min(
                    LinuxGroupScanMaximumDelay.TotalMilliseconds, groupScanDelay.TotalMilliseconds * 2));
                nextGroupScan = watch.Elapsed + groupScanDelay;
            }
            var remaining = boundary.Owned.Where(identity => identity.Pid != Environment.ProcessId)
                .Where(IsSameLinuxProcess).ToArray();
            _ = originalProcess?.RemainingRetirement;
            originalBudget?.CheckDisposition();
            if (remaining.Length == 0) return;
            TryKillLinuxProcessGroups(boundary, originalBudget, originalProcess);
            TryKillLinuxProcesses(remaining, originalBudget, originalProcess);
            if (watch.Elapsed >= LinuxDescendantReapingTimeout)
                throw new IOException("Timed out waiting for the owned Linux process boundary to become empty: " +
                    string.Join(", ", remaining.Select(item => item.Pid)));
            var delay = LinuxObservationInitialDelay;
            if (originalBudget is not null)
            {
                var existing = originalProcess?.RemainingRetirement ?? originalBudget.RemainingDisposition;
                if (existing < delay) delay = existing;
            }
            await Task.Delay(delay, CancellationToken.None);
        }
    }

    private static bool IsSameLinuxProcess(LinuxProcessIdentity expected) =>
        TryReadLinuxIdentity(expected.Pid, out var current) && !current.IsZombie &&
        HasContinuousLinuxIdentity(expected, current);

    private static bool HasContinuousLinuxIdentity(LinuxProcessIdentity expected, LinuxProcessIdentity current) =>
        current.Pid == expected.Pid && current.StartTime == expected.StartTime;

    private static bool TryReadLinuxIdentity(int pid, out LinuxProcessIdentity identity)
    {
        identity = default;
        string stat;
        try { stat = File.ReadAllText($"/proc/{pid}/stat"); }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
        var commandEnd = stat.LastIndexOf(')');
        if (commandEnd < 0 || commandEnd + 2 >= stat.Length) return false;
        var fields = stat[(commandEnd + 2)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length <= 19 ||
            !int.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out var parentPid) ||
            !ulong.TryParse(fields[19], NumberStyles.None, CultureInfo.InvariantCulture, out var startTime))
            return false;
        if (!int.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out var processGroupId) ||
            !int.TryParse(fields[3], NumberStyles.None, CultureInfo.InvariantCulture, out var sessionId))
            return false;
        identity = new(pid, parentPid, processGroupId, sessionId, startTime, fields[0] == "Z");
        return true;
    }

    private static void TryKillLinuxProcessGroups(LinuxProcessBoundary boundary, ResolvedGraphBudget? originalBudget = null,
        LinuxOriginalProcessFrame? originalProcess = null)
    {
        if (!OperatingSystem.IsLinux() || !boundary.Isolated) return;
        var groups = new HashSet<int>();
        foreach (var expected in boundary.Owned)
        {
            _ = originalProcess?.RemainingRetirement;
            originalBudget?.CheckDisposition();
            var observed = TryReadLinuxIdentity(expected.Pid, out var current);
            _ = originalProcess?.RemainingRetirement;
            originalBudget?.CheckDisposition();
            if (!observed || current.IsZombie || !HasContinuousLinuxIdentity(expected, current)) continue;
            if (current.ProcessGroupId == boundary.ProcessGroupId)
                groups.Add(boundary.ProcessGroupId);
            if (current.Pid == current.ProcessGroupId && current.Pid == current.SessionId)
                groups.Add(current.Pid);
        }
        foreach (var group in groups)
        {
            _ = originalProcess?.RemainingRetirement;
            originalBudget?.CheckDisposition();
            try { _ = Kill(-group, 9); }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }
            catch (BadImageFormatException) { }
            catch (MarshalDirectiveException) { }
            _ = originalProcess?.RemainingRetirement;
            originalBudget?.CheckDisposition();
        }
    }

    [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static extern int Kill(int pid, int signal);

    private readonly record struct LinuxProcessIdentity(int Pid, int ParentPid, int ProcessGroupId,
        int SessionId, ulong StartTime, bool IsZombie);

    // Original-budget calls never let a language-level using silently close an
    // actual process after failed/expired settlement. This retains that SAME
    // Process, observers, controls and status owner; it grants no Linux containment.
    internal sealed class LinuxOriginalProcessFrame : IDisposable
    {
        private static readonly ConcurrentBag<LinuxOriginalProcessFrame> Unsettled = [];
        private readonly ResolvedGraphBudget budget;
        private readonly List<IDisposable> resources = [];
        private readonly List<Task> observers = [];
        private readonly List<string> directories = [];
        private readonly Action? retain;
        private readonly Action? settled;
        private bool birthEntered, birthReturned, born, closeRequested, closeReturned, retained;
        private Exception? closeFailure;
        private long? retirementStarted;
        internal Process Process { get; }
        internal object? Boundary { get; set; } // Retain observed identities only; never adopt or reconstruct them.
        internal LinuxOriginalProcessFrame(Process process, ResolvedGraphBudget original,
            Action? retainStatus = null, Action? settledStatus = null)
        {
            original.Resources.AdmitFrame();
            Process = process; budget = original; resources.Add(process);
            settled = settledStatus; retain = retainStatus;
            var directory = Path.GetFullPath(string.IsNullOrWhiteSpace(process.StartInfo.WorkingDirectory)
                ? Environment.CurrentDirectory : process.StartInfo.WorkingDirectory);
            directories.Add(directory);
            if (process.StartInfo.Environment.TryGetValue("NUGET_PACKAGES", out var packages) && !string.IsNullOrWhiteSpace(packages))
                directories.Add(Path.GetFullPath(packages, directory));
        }
        internal bool Start()
        {
            if (birthEntered || closeRequested) throw new EvaluationContractException("Original process birth cannot retry or follow first-close.");
            budget.CheckWork();
            // Start throwing is not proof that no child was born. Retain the
            // exact status owner on that path, but not before Start was entered.
            birthEntered = true; retain?.Invoke();
            born = Process.Start(); birthReturned = true;
            return born;
        }
        internal static void AssertDirectoryMayBeRemoved(string directory)
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
            foreach (var owner in Unsettled)
                if (owner.birthEntered && (!owner.birthReturned || owner.born))
                    foreach (var path in owner.directories)
                        if (Overlaps(root, Path.TrimEndingDirectorySeparator(path)))
                            throw new SnapshotCleanupException(new IOException(
                                "An original native process boundary is unsettled; its exact working/package directories are retained."));

            static bool Overlaps(string left, string right) => left == right ||
                left.StartsWith(right + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
                right.StartsWith(left + Path.DirectorySeparatorChar, StringComparison.Ordinal);
        }
        internal TimeSpan RemainingRetirement
        {
            get
            {
                // This is only a restriction on this already-owned process,
                // never another aggregate cleanup interval or a native grant.
                retirementStarted ??= Stopwatch.GetTimestamp();
                if (budget.CancellationRequested) budget.BeginFailureCleanup();
                else _ = budget.TryBeginWorkExpiry();
                var local = TimeSpan.FromSeconds(60) - Stopwatch.GetElapsedTime(retirementStarted.Value);
                if (local <= TimeSpan.Zero)
                {
                    budget.BeginFailureCleanup();
                    throw new TimeoutException("Original Linux process local retirement endpoint expired; no extension.");
                }
                var original = budget.RemainingDisposition;
                return local < original ? local : original;
            }
        }
        internal T Own<T>(T resource) where T : IDisposable { resources.Add(resource); return resource; }
        internal void Track(Task observer) => observers.Add(observer);
        internal async Task JoinObserversAsync()
        {
            if (closeRequested) throw new EvaluationContractException("Original observer join cannot follow first-close.");
            _ = RemainingRetirement;
            try { await Task.WhenAll(observers).WaitAsync(RemainingRetirement).ConfigureAwait(false); }
            catch when (observers.All(task => task.IsCompleted)) { } // Faults remain on these SAME tasks.
            catch { budget.BeginFailureCleanup(); throw; }
            _ = RemainingRetirement;
        }
        private Exception[] ObserverFailures() => observers.Where(task => task.IsFaulted).Select(task => (Exception)task.Exception!).ToArray();
        internal void PreserveObserverFailures(Exception primary)
        {
            var failures = ObserverFailures();
            if (failures.Length != 0) primary.Data["ProcessTree.OriginalObserverFailures"] = failures;
        }
        internal void ThrowObserverFailures()
        {
            var failures = ObserverFailures();
            if (failures.Length != 0) throw new AggregateException("Actual original process observers faulted despite known physical settlement.", failures);
        }
        internal void CloseSettled()
        {
            if (closeRequested) throw new EvaluationContractException("Original Linux process first-close cannot retry.");
            closeRequested = true;
            try
            {
                _ = RemainingRetirement;
                if (birthEntered && !birthReturned || observers.Any(task => !task.IsCompleted) || born && !Process.HasExited)
                    throw new IOException("Actual Linux process/observers remain unsettled; retain original handles.");
                foreach (var resource in resources.AsEnumerable().Reverse())
                {
                    _ = RemainingRetirement;
                    resource.Dispose();
                    _ = RemainingRetirement;
                }
                closeReturned = true;
                _ = RemainingRetirement; settled?.Invoke();
            }
            catch (Exception failure)
            {
                closeFailure = failure;
                Retain();
                throw new SnapshotCleanupException(failure);
            }
        }
        public void Dispose()
        {
            // A constructor/pre-birth refusal or a returned false Start has no
            // native child. Dispose its controls once under the same allowance.
            if (!closeRequested && (!birthEntered || birthReturned && !born)) CloseSettled();
            // No late close, retry or Task replacement after unknown disposition.
            if (!closeReturned || closeFailure is not null) Retain();
        }
        private void Retain() { budget.BeginFailureCleanup(); if (!retained) { retained = true; Unsettled.Add(this); } }
    }

    private sealed class LinuxStrictLaunch : IDisposable
    {
        internal const string WrapperScript =
            "status=$1; shift; \"$@\" & child=$!; wait \"$child\"; code=$?; " +
            "temporary=\"${status}.$$\"; (umask 077; printf '%s\\n' \"$code\" > \"$temporary\") || exit 125; " +
            "/bin/mv -f -- \"$temporary\" \"$status\" || exit 125; " +
            "while :; do /bin/sleep 3600 & wait $!; done";

        private LinuxStrictLaunch(string root)
        {
            Root = root;
            StatusPath = Path.Combine(root, "exit-code");
        }

        private string Root { get; }
        public string StatusPath { get; }
        private bool retainedUnsettled;
        public void RetainUnsettled() => retainedUnsettled = true;
        public void MarkSettled() => retainedUnsettled = false;

        public static LinuxStrictLaunch Create()
        {
            var root = Directory.CreateTempSubdirectory("mutate4csharp-process-").FullName;
            return new(root);
        }

        public void Dispose()
        {
            if (retainedUnsettled) return;
            try { Directory.Delete(Root, true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private sealed class LinuxProcessBoundary
    {
        private readonly Dictionary<(int Pid, ulong StartTime), LinuxProcessIdentity> _owned = [];

        public LinuxProcessBoundary(LinuxProcessIdentity? launched, bool isolated)
        {
            Launched = launched;
            Isolated = isolated;
            if (launched is { } identity) Add(identity);
        }

        public LinuxProcessIdentity? Launched { get; }
        public bool Isolated { get; }
        public int ProcessGroupId => Launched?.Pid ?? 0;
        public IReadOnlyCollection<LinuxProcessIdentity> Owned => _owned.Values;

        public bool Add(LinuxProcessIdentity identity)
        {
            if (identity.Pid == Environment.ProcessId) return false;
            var key = (identity.Pid, identity.StartTime);
            var added = !_owned.ContainsKey(key);
            _owned[key] = identity;
            return added;
        }
    }
}

internal sealed record DependencyPreparationOptions(TimeSpan Timeout, int MaxFiles, long MaxBytes)
{
    public static DependencyPreparationOptions Default { get; } = new(TimeSpan.FromMinutes(10), 200_000,
        4L * 1024 * 1024 * 1024);
}

internal sealed class FrozenExecutionEnvironment(OwnedDirectory owner, string packageRoot,
    string graphRoot, string fingerprint, string packageFingerprint, string? configRelativePath,
    string? generatedConfigPath, string sdkVersion) : IAsyncDisposable
{
    public string PackageRoot { get; } = packageRoot;
    public string GraphRoot { get; } = graphRoot;
    public string Fingerprint { get; } = fingerprint;
    public string PackageFingerprint { get; } = packageFingerprint;
    public string SdkVersion { get; } = sdkVersion;

    public async Task<OwnedPackageCache> CreateWorkerPackageCacheAsync(string purpose,
        CancellationToken cancellationToken, ResolvedGraphBudget? originalBudget = null)
    {
        originalBudget?.CheckWork(); cancellationToken.ThrowIfCancellationRequested();
        var cacheOwner = OwnedDirectory.Create(OwnedDirectory.PrivateParent("package-caches"), purpose);
        try
        {
            originalBudget?.CheckWork();
            var root = Path.Combine(cacheOwner.Root, "packages");
            Directory.CreateDirectory(root);
            originalBudget?.CheckWork();
            await ExecutionEnvironment.CopyTreeAsync(PackageRoot, root, cancellationToken, originalBudget);
            originalBudget?.CheckWork();
            return new(cacheOwner, root, Fingerprint);
        }
        catch (Exception cacheFailure)
        {
            try { await cacheOwner.DisposeAsync(); }
            catch (Exception cleanupFailure)
            {
                throw new SnapshotCleanupException(cacheFailure, cleanupFailure);
            }
            throw;
        }
    }

    public async Task RestoreWorkerAsync(SnapshotClone worker, string projectPath, OwnedPackageCache packages,
        TimeSpan timeout, CancellationToken cancellationToken, string? framework = null,
        string? configuration = null, bool restoreAuthoredFrameworkGraph = false, ResolvedGraphBudget? originalBudget = null)
    {
        originalBudget?.CheckWork();
        foreach (var file in Directory.EnumerateFiles(GraphRoot, "packages.lock.json", SearchOption.AllDirectories))
        {
            originalBudget?.CheckWork();
            var destination = Path.Combine(worker.Root, Path.GetRelativePath(GraphRoot, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            if (File.Exists(destination) && !File.ReadAllBytes(destination).SequenceEqual(File.ReadAllBytes(file)))
                throw new ExecutionBoundaryIntegrityException(
                    "Captured package lock differs from the prepared dependency lock.");
            if (!File.Exists(destination)) await SnapshotWorkspace.CopyFileAsync(file, destination, cancellationToken);
        }
        ExecutionEnvironment.ValidateExecutionAncestors(worker.Root);
        ExecutionEnvironment.ValidateExecutionBoundary(worker.Root);
        var project = Path.GetFullPath(projectPath.Replace('/', Path.DirectorySeparatorChar), worker.Root);
        var config = configRelativePath is null ? generatedConfigPath! :
            Path.GetFullPath(configRelativePath.Replace('/', Path.DirectorySeparatorChar), worker.Root);
        var dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
        if ((framework is null) != (configuration is null))
            throw new ArgumentException("Framework and configuration must be supplied together.");
        var arguments = new List<string> { "restore", project, "-m:1", "--locked-mode", "--no-cache",
            "--packages", packages.Root, "--configfile", config, "-nodeReuse:false",
            "-p:UseSharedCompilation=false", "-p:RestoreFallbackFolders=",
            "-p:RestoreAdditionalProjectFallbackFolders=", $"-p:NuGetPackageRoot={packages.Root}" };
        if (framework is not null)
        {
            // Graph restore must retain authored child frameworks, not inherit a synthetic root override.
            if (!restoreAuthoredFrameworkGraph) arguments.Add("-p:TargetFramework=" + framework);
            arguments.Add("-p:Configuration=" + configuration);
        }
        var run = await ProcessTree.RunAsync(dotnet, arguments, worker.Root, timeout, cancellationToken,
            ExecutionEnvironment.ProcessEnvironment(packages.Root), requireLinuxSessionIsolation: true, originalBudget: originalBudget);
        if (run.TimedOut)
            throw new TimeoutException($"Frozen worker restore exceeded its {timeout.TotalSeconds:g}-second timeout.");
        if (run.ExitCode != 0)
            throw CreateRestoreFailure(run, project, framework, configuration, worker.Root);
        originalBudget?.CheckWork();
        ExecutionEnvironment.ValidateResolvedPackageRoots(worker.Root, packages.Root, config);
        if (!string.Equals(PackageFingerprint,
                ExecutionEnvironment.FingerprintPackages(packages.Root, cancellationToken: cancellationToken, originalBudget: originalBudget),
                StringComparison.Ordinal))
            throw new ExecutionBoundaryIntegrityException(
                "Worker restore changed its private frozen package-cache copy.");
    }

    internal static SnapshotCaptureException CreateRestoreFailure(ProcessTreeResult actualReturn,
        string? requestedProject = null, string? framework = null, string? configuration = null, string? workerRoot = null)
    {
        // Already returned bounded DATA only; no file reads, evaluation or restore retry.
        if (actualReturn.ExitCode == 0 || actualReturn.TimedOut)
            throw new ArgumentException("Only the original nonzero, non-timeout restore return may issue this diagnostic.");
        static object Digest(string value) => new
        {
            Characters = value.Length,
            Sha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant()
        };
        static string Scalar(string? value) => value is null ? "[unspecified]" : value is { Length: > 0 and <= 32 } &&
            value.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-') ? value : "[value-withheld]";
        // Captured diagnostic text is not trusted path syntax. Formatting must not
        // replace the original restore failure with a path-normalization failure.
        static string? Normalized(string? path)
        {
            if (path is null || path.Length is 0 or > 4096 || path.IndexOf('\0') >= 0) return null;
            try { return Path.IsPathFullyQualified(path) ? Path.GetFullPath(path) : null; }
            catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
            { return null; }
        }
        var normalizedRoot = Normalized(workerRoot);
        var normalizedRequested = Normalized(requestedProject);
        string Label(string? path)
        {
            var normalized = Normalized(path);
            if (normalized is null || normalizedRoot is null) return "[unparsed]";
            try
            {
                var relative = Path.GetRelativePath(normalizedRoot, normalized).Replace('\\', '/');
                if (relative.StartsWith("../", StringComparison.Ordinal) || relative == ".." || Path.IsPathFullyQualified(relative))
                    return "[outside-worker-root]";
                return relative is { Length: > 0 and <= 96 } && relative.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' or '/')
                    ? relative : "[worker-project]";
            }
            catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
            { return "[unparsed]"; }
        }
        const string prefix = "Frozen worker restore failed: ";
        string? diagnostic = null, reportedProject = null, selectedRole = null;
        var projectOpen = -1;
        foreach (var (role, stream) in new[] { ("stderr", actualReturn.StandardError), ("stdout", actualReturn.StandardOutput) })
        {
            var error = stream.IndexOf(": error ", StringComparison.OrdinalIgnoreCase);
            if (error < 0) continue;
            var start = error + 2;
            var lineEnd = stream.IndexOfAny(['\r', '\n'], start);
            if (lineEnd < 0) lineEnd = stream.Length;
            var complete = stream.AsSpan(start, lineEnd - start);
            // Withhold malformed/qualified bracket suffixes before parsing identity.
            projectOpen = complete.IndexOf('[');
            var close = complete.LastIndexOf(']');
            var open = projectOpen;
            if (open >= 0 && close - open is > 1 and <= 4097)
            {
                var candidate = complete[(open + 1)..close];
                // Keep only bounded local digest material. Unsupported syntax is
                // deliberately not interpreted as the requested project.
                if (close == complete.Length - 1 && candidate.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
                    reportedProject = candidate.ToString();
            }
            var maximum = EvaluationEvidence.MaxDiagnosticLength - prefix.Length;
            var take = Math.Min(complete.Length, maximum);
            // On contextual calls cut the private project suffix BEFORE any prefix truncation.
            var line = (workerRoot is not null || requestedProject is not null) && projectOpen >= 0
                ? complete[..Math.Min(projectOpen, take)].ToString() + "[reported-project]"
                : complete[..take].ToString();
            line = Regex.Replace(line, @"https?://\S+", "[source-uri]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            diagnostic = $"exit={actualReturn.ExitCode}; source={role}; {line}";
            selectedRole = role;
            break;
        }
        diagnostic ??= $"exit={actualReturn.ExitCode}; no explicit error line; stdoutChars={actualReturn.StandardOutput.Length}; stderrChars={actualReturn.StandardError.Length}; raw streams withheld";
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var normalizedReported = Normalized(reportedProject);
        bool? matches = normalizedReported is null || normalizedRequested is null ? null :
            string.Equals(normalizedReported, normalizedRequested, comparison);
        var context = requestedProject is null ? "" : "requested=" + Label(requestedProject) + "; tfm=" + Scalar(framework) +
            "; configuration=" + Scalar(configuration) + "; errorProject=" + Label(reportedProject) + "; projectMatches=" +
            (matches?.ToString() ?? "Unknown") + "; ";
        var failure = new SnapshotCaptureException(prefix + EvaluationTextBounds.Prefix(context + diagnostic,
            EvaluationEvidence.MaxDiagnosticLength - prefix.Length));
        failure.Data["FrozenWorkerRestore.ActualReturn"] = actualReturn;
        if (requestedProject is not null)
            failure.Data["FrozenWorkerRestore.SelectionContext"] = JsonSerializer.Serialize(new
            {
                Version = 1,
                DiagnosticOnly = true,
                RequestedProjectRole = Label(requestedProject),
                RequestedProject = Digest(requestedProject),
                RequestedFramework = Scalar(framework),
                FrameworkDigest = framework is null ? null : Digest(framework),
                RequestedConfiguration = Scalar(configuration),
                ConfigurationDigest = configuration is null ? null : Digest(configuration),
                ErrorStream = selectedRole,
                ReportedProjectRole = Label(reportedProject),
                ReportedProject = reportedProject is null ? null : Digest(reportedProject),
                ReportedProjectMatchesRequested = matches
            });
        return failure;
    }


    public ValueTask DisposeAsync() => owner.DisposeAsync();
}

internal sealed class OwnedPackageCache(OwnedDirectory owner, string root, string fingerprint) : IAsyncDisposable
{
    public string Root { get; } = root;
    public string Fingerprint { get; } = fingerprint;
    public ValueTask DisposeAsync() => owner.DisposeAsync();
}

internal static class ExecutionEnvironment
{
    internal static IReadOnlyList<string> StrictSdkResolverEnvironmentVariableNames { get; } =
    [
        "DOTNET_MSBUILD_SDK_RESOLVER_SDKS_DIR", "DOTNET_MSBUILD_SDK_RESOLVER_SDKS_VER",
        "DOTNET_MSBUILD_SDK_RESOLVER_CLI_DIR", "MSBUILDADDITIONALSDKRESOLVERSFOLDER"
    ];
    internal static IReadOnlyList<string> StrictSemanticPropertyEnvironmentVariableNames { get; } =
    [
        "TargetFramework", "TargetFrameworks", "Configuration", "Nullable", "LangVersion", "DefineConstants",
        "ImplicitUsings", "OutputType", "EnableDefaultItems", "EnableDefaultCompileItems",
        "DefaultItemExcludes", "DisableImplicitFrameworkDefines", "DisableImplicitConfigurationDefines",
        "DisableDiagnosticTracing", "CheckForOverflowUnderflow", "AllowUnsafeBlocks"
    ];

    internal static async Task CreateExecutionBoundaryAsync(string ownedRoot, string sdkWorkingDirectory,
        CancellationToken cancellationToken, ResolvedGraphBudget? originalBudget = null)
    {
        var dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
        var sdk = await ProcessTree.RunAsync(dotnet, ["--version"], sdkWorkingDirectory,
            TimeSpan.FromSeconds(30), cancellationToken, requireLinuxSessionIsolation: true, originalBudget: originalBudget);
        var version = sdk.StandardOutput.Trim();
        if (sdk.TimedOut || sdk.ExitCode != 0 || version.Length == 0 ||
            version.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('.' or '-' or '+')))
            throw new ExecutionEnvironmentUnavailableException(
                "Could not pin the .NET SDK for the private execution boundary.");
        originalBudget?.CheckWork();
        WriteBoundary(Path.Combine(ownedRoot, "Directory.Build.props"), "<Project />\n");
        WriteBoundary(Path.Combine(ownedRoot, "Directory.Build.targets"), "<Project />\n");
        WriteBoundary(Path.Combine(ownedRoot, "Directory.Packages.props"), "<Project />\n");
        WriteBoundary(Path.Combine(ownedRoot, "Directory.Build.rsp"), string.Empty);
        WriteBoundary(Path.Combine(ownedRoot, "Directory.Solution.props"), "<Project />\n");
        WriteBoundary(Path.Combine(ownedRoot, "Directory.Solution.targets"), "<Project />\n");
        WriteBoundary(Path.Combine(ownedRoot, ".editorconfig"), "root = true\n");
        WriteBoundary(Path.Combine(ownedRoot, "global.json"),
            $"{{\n  \"sdk\": {{ \"version\": \"{version}\", \"rollForward\": \"disable\" }}\n}}\n");
    }

    internal static void ValidateExecutionBoundary(string executionRoot)
    {
        var ownedRoot = Directory.GetParent(Path.GetFullPath(executionRoot))?.FullName ??
            throw new ExecutionBoundaryIntegrityException(
                "Snapshot execution root has no owned parent.");
        if (!OperatingSystem.IsWindows())
        {
            var mode = File.GetUnixFileMode(ownedRoot);
            var forbidden = UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
                            UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;
            if ((mode & forbidden) != 0)
                throw new ExecutionBoundaryIntegrityException(
                    "Snapshot execution parent is not private.");
        }
        foreach (var boundary in new Dictionary<string, string>
        {
            ["Directory.Build.props"] = "<Project />\n",
            ["Directory.Build.targets"] = "<Project />\n",
            ["Directory.Packages.props"] = "<Project />\n",
            ["Directory.Build.rsp"] = string.Empty,
            ["Directory.Solution.props"] = "<Project />\n",
            ["Directory.Solution.targets"] = "<Project />\n",
            [".editorconfig"] = "root = true\n"
        })
        {
            var path = Path.Combine(ownedRoot, boundary.Key);
            if (!File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new ExecutionBoundaryIntegrityException(
                    $"Private execution boundary is missing or unsafe: {boundary.Key}");
            if (!string.Equals(File.ReadAllText(path), boundary.Value, StringComparison.Ordinal))
                throw new ExecutionBoundaryIntegrityException(
                    $"Private execution boundary was modified: {boundary.Key}");
        }
        var global = Path.Combine(ownedRoot, "global.json");
        if (!File.Exists(global) || (File.GetAttributes(global) & FileAttributes.ReparsePoint) != 0)
            throw new ExecutionBoundaryIntegrityException(
                "Private execution boundary is missing or unsafe: global.json");
        for (var ancestor = Directory.GetParent(ownedRoot); ancestor is not null; ancestor = ancestor.Parent)
            if (File.Exists(Path.Combine(ancestor.FullName, ".globalconfig")))
                throw new ExecutionBoundaryIntegrityException(
                    $"Private execution boundary cannot stop inherited .globalconfig: {ancestor.FullName}");
    }

    public static async Task<FrozenExecutionEnvironment> PrepareDependenciesAsync(InputSnapshot snapshot,
        IReadOnlyList<string> projectPaths, DependencyPreparationOptions options, CancellationToken cancellationToken,
        ResolvedGraphBudget? originalBudget = null)
    {
        originalBudget?.CheckWork();
        if (projectPaths.Count == 0) throw new ArgumentException("At least one project or solution is required.");
        if (options.Timeout <= TimeSpan.Zero || options.MaxFiles < 1 || options.MaxBytes < 1)
            throw new ArgumentOutOfRangeException(nameof(options));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(options.Timeout);
        OwnedDirectory? owner = null;
        try
        {
            owner = OwnedDirectory.Create(OwnedDirectory.PrivateParent("environments"), "dependencies");
            var packages = Path.Combine(owner.Root, "packages");
            var graphs = Path.Combine(owner.Root, "graphs");
            Directory.CreateDirectory(packages);
            Directory.CreateDirectory(graphs);
            await using var preparation = await SnapshotWorkspace.CreateCloneAsync(snapshot,
                "dependency-preparation", deadline.Token, originalBudget);
            ValidateExecutionAncestors(preparation.Root);
            ValidateExecutionBoundary(preparation.Root);
            var dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
            var configRelativePath = SelectApplicableNuGetConfig(snapshot, projectPaths);
            var config = Path.Combine(preparation.Root,
                configRelativePath.Replace('/', Path.DirectorySeparatorChar));
            foreach (var projectPath in projectPaths.OrderBy(path => path, StringComparer.Ordinal))
            {
                originalBudget?.CheckWork();
                var relative = projectPath.Replace('\\', '/');
                var project = Path.GetFullPath(relative.Replace('/', Path.DirectorySeparatorChar), preparation.Root);
                EnsureWithin(preparation.Root, project);
                if (!File.Exists(project)) throw new SnapshotCaptureException($"Dependency entry is absent from snapshot: {relative}");
                var arguments = new List<string> { "restore", project, "-m:1", "--packages", packages,
                    "--configfile", config, "-nodeReuse:false", "-p:UseSharedCompilation=false",
                    "-p:RestoreFallbackFolders=", "-p:RestoreAdditionalProjectFallbackFolders=",
                    $"-p:NuGetPackageRoot={packages}" };
                if (File.Exists(Path.Combine(Path.GetDirectoryName(project)!, "packages.lock.json"))) arguments.Add("--locked-mode");
                else arguments.Add("--use-lock-file");
                var remaining = options.Timeout;
                var run = await ProcessTree.RunAsync(dotnet, arguments, preparation.Root, remaining, deadline.Token,
                    ProcessEnvironment(packages), requireLinuxSessionIsolation: true, originalBudget: originalBudget);
                if (run.TimedOut) throw new SnapshotCaptureException("Dependency preparation exceeded its deadline.");
                if (run.ExitCode != 0)
                    throw new SnapshotCaptureException($"Dependency preparation failed: {Tail(run.StandardError, run.StandardOutput)}");
                originalBudget?.CheckWork();
            }
            var assets = Directory.EnumerateFiles(preparation.Root, "project.assets.json", SearchOption.AllDirectories)
                .Where(path => path.Split(Path.DirectorySeparatorChar).Contains("obj", StringComparer.OrdinalIgnoreCase))
                .OrderBy(path => path, StringComparer.Ordinal).ToArray();
            if (assets.Length == 0) throw new SnapshotCaptureException("Dependency preparation produced no resolved project.assets.json graph.");
            ValidateResolvedPackageRoots(preparation.Root, packages, config);
            var locks = Directory.EnumerateFiles(preparation.Root, "packages.lock.json", SearchOption.AllDirectories)
                .OrderBy(path => Path.GetRelativePath(preparation.Root, path), StringComparer.Ordinal).ToArray();
            if (locks.Length == 0) throw new SnapshotCaptureException("Dependency preparation produced no frozen package locks.");
            foreach (var packageLock in locks)
            {
                var destination = Path.Combine(graphs, Path.GetRelativePath(preparation.Root, packageLock));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                await SnapshotWorkspace.CopyFileAsync(packageLock, destination, deadline.Token);
            }
            var packageFingerprint = FingerprintPackages(packages, options.MaxFiles, options.MaxBytes,
                deadline.Token, originalBudget);
            var graphFingerprint = FingerprintTreeBounded(graphs, options.MaxFiles, options.MaxBytes,
                deadline.Token, originalBudget: originalBudget);
            var sdk = await ProcessTree.RunAsync(dotnet, ["--version"], preparation.Root,
                TimeSpan.FromSeconds(30), deadline.Token, requireLinuxSessionIsolation: true, originalBudget: originalBudget);
            if (sdk.ExitCode != 0 || sdk.TimedOut)
                throw new ExecutionEnvironmentUnavailableException(
                    "Could not identify the resolved .NET SDK.");
            originalBudget?.CheckWork();
            var identity = HashStrings("dependencies-v1", snapshot.Identity.CaptureId, sdk.StandardOutput.Trim(),
                packageFingerprint, graphFingerprint);
            return new(owner, packages, graphs, identity, packageFingerprint,
                configRelativePath, null, sdk.StandardOutput.Trim());
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            var timeoutFailure = new SnapshotCaptureException(
                "Dependency preparation exceeded its overall deadline.", ex);
            if (owner is not null)
            {
                try { await owner.DisposeAsync(); }
                catch (Exception cleanupFailure)
                {
                    throw new SnapshotCleanupException(timeoutFailure, cleanupFailure);
                }
            }
            throw timeoutFailure;
        }
        catch (Exception preparationFailure)
        {
            if (owner is not null)
            {
                try { await owner.DisposeAsync(); }
                catch (Exception cleanupFailure)
                {
                    throw new SnapshotCleanupException(preparationFailure, cleanupFailure);
                }
            }
            throw;
        }
    }

    private static string SelectApplicableNuGetConfig(InputSnapshot snapshot,
        IReadOnlyList<string> projectPaths)
    {
        var configs = snapshot.Files
            .Where(file => file.Exists && Path.GetFileName(file.RelativePath)
                .Equals("NuGet.Config", StringComparison.OrdinalIgnoreCase))
            .Select(file => file.RelativePath)
            .ToDictionary(path => path, path => path,
                OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var selected = new HashSet<string>(OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var projectPath in projectPaths)
        {
            var requested = projectPath.Replace('\\', '/');
            var fullProject = Path.GetFullPath(requested.Replace('/', Path.DirectorySeparatorChar),
                snapshot.CaptureRoot);
            EnsureWithin(snapshot.CaptureRoot, fullProject);
            var normalized = Path.GetRelativePath(snapshot.CaptureRoot, fullProject).Replace('\\', '/');
            var relativeDirectory = Path.GetDirectoryName(normalized)?.Replace('\\', '/') ?? string.Empty;
            var applicable = new List<string>();
            for (var directory = relativeDirectory; ;)
            {
                var candidate = string.IsNullOrEmpty(directory)
                    ? "NuGet.Config"
                    : directory + "/NuGet.Config";
                var captured = configs.Keys.FirstOrDefault(path =>
                    string.Equals(path, candidate, StringComparison.OrdinalIgnoreCase));
                if (captured is not null) applicable.Add(captured);
                if (string.IsNullOrEmpty(directory)) break;
                directory = Path.GetDirectoryName(directory.Replace('/', Path.DirectorySeparatorChar))?
                    .Replace('\\', '/') ?? string.Empty;
            }
            if (applicable.Count == 0)
                throw new SnapshotCaptureException(
                    $"Dependency preparation refused because no captured applicable NuGet.Config exists on the ancestor path for {normalized}.");
            if (applicable.Count > 1)
                throw new SnapshotCaptureException(
                    $"Dependency preparation refused because multiple applicable NuGet.Config files would be merged for {normalized}.");
            selected.Add(applicable.Single());
        }
        if (selected.Count != 1)
            throw new SnapshotCaptureException(
                "Dependency preparation refused because projects resolve to different applicable NuGet.Config files.");
        var selectedPath = selected.Single();
        var document = XDocument.Load(Path.Combine(snapshot.CaptureRoot,
            selectedPath.Replace('/', Path.DirectorySeparatorChar)), LoadOptions.PreserveWhitespace);
        var packageSources = document.Descendants().FirstOrDefault(element =>
            element.Name.LocalName.Equals("packageSources", StringComparison.OrdinalIgnoreCase));
        if (packageSources is null || !packageSources.Elements().Any(element =>
                element.Name.LocalName.Equals("clear", StringComparison.OrdinalIgnoreCase)))
            throw new SnapshotCaptureException(
                $"Dependency preparation refused because {selectedPath} does not clear inherited packageSources.");
        return selectedPath;
    }

    public static string FingerprintTree(string root) =>
        FingerprintTreeBounded(root, int.MaxValue, long.MaxValue, CancellationToken.None);

    internal static string FingerprintPackages(string root, int maxFiles = int.MaxValue,
        long maxBytes = long.MaxValue, CancellationToken cancellationToken = default, ResolvedGraphBudget? originalBudget = null) =>
        FingerprintTreeBounded(root, maxFiles, maxBytes, cancellationToken,
            path => !Path.GetFileName(path).Equals(".nupkg.metadata", StringComparison.OrdinalIgnoreCase), originalBudget);

    internal static void ValidateResolvedPackageRoots(string workspaceRoot, string expectedPackageRoot,
        string? selectedConfigPath = null)
    {
        var expected = Path.GetFullPath(expectedPackageRoot).TrimEnd(Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        selectedConfigPath ??= Directory.EnumerateFiles(workspaceRoot, "*", SearchOption.AllDirectories)
            .Where(path => Path.GetFileName(path).Equals("NuGet.Config", StringComparison.OrdinalIgnoreCase))
            .Take(2).ToArray() is [var sole] ? sole : null;
        var allowedSources = selectedConfigPath is null ? null : ReadConfiguredPackageSources(selectedConfigPath);
        foreach (var assetsPath in Directory.EnumerateFiles(workspaceRoot, "project.assets.json",
                     SearchOption.AllDirectories).Where(path => path.Split(Path.DirectorySeparatorChar)
                     .Contains("obj", StringComparer.OrdinalIgnoreCase)))
        {
            using var assets = JsonDocument.Parse(File.ReadAllBytes(assetsPath));
            if (!assets.RootElement.TryGetProperty("packageFolders", out var packageFolders))
                throw new ExecutionBoundaryIntegrityException(
                    "Resolved dependency graph did not declare package folders.");
            var foundExpected = false;
            foreach (var folder in packageFolders.EnumerateObject())
            {
                var actual = Path.GetFullPath(folder.Name, workspaceRoot).TrimEnd(Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar);
                if (!string.Equals(actual, expected, OperatingSystem.IsWindows()
                        ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                    throw new ExecutionBoundaryIntegrityException(
                        $"Resolved dependency graph used an unfrozen package folder: {folder.Name}");
                foundExpected = true;
            }
            if (!foundExpected)
                throw new ExecutionBoundaryIntegrityException(
                    "Resolved dependency graph did not use the private package folder.");
            if (allowedSources is null) continue;
            if (!assets.RootElement.TryGetProperty("project", out var project) ||
                !project.TryGetProperty("restore", out var restore) ||
                !restore.TryGetProperty("sources", out var sources) ||
                sources.ValueKind != JsonValueKind.Object)
            {
                var hasResolvedPackages = assets.RootElement.TryGetProperty("libraries", out var libraries) &&
                    libraries.ValueKind == JsonValueKind.Object && libraries.EnumerateObject().Any(library =>
                        library.Value.TryGetProperty("type", out var type) && type.GetString() == "package");
                var hasPackageDownloads = assets.RootElement.TryGetProperty("project", out var packageDownloadProject) &&
                    packageDownloadProject.TryGetProperty("frameworks", out var frameworks) &&
                    frameworks.ValueKind == JsonValueKind.Object && frameworks.EnumerateObject().Any(framework =>
                        framework.Value.TryGetProperty("downloadDependencies", out var downloads) &&
                        downloads.ValueKind is JsonValueKind.Array or JsonValueKind.Object &&
                        (downloads.ValueKind == JsonValueKind.Object || downloads.GetArrayLength() > 0));
                if (hasResolvedPackages || hasPackageDownloads)
                    throw new ExecutionBoundaryIntegrityException(
                        "Resolved dependency graph did not declare restore sources.");
                continue;
            }
            foreach (var source in sources.EnumerateObject())
            {
                var normalized = NormalizePackageSource(source.Name, workspaceRoot);
                if (!allowedSources.Contains(normalized) && !IsImplicitRuntimeLibraryPacksSource(normalized))
                    throw new ExecutionBoundaryIntegrityException(
                        $"Resolved dependency graph used a source outside the selected NuGet.Config: {source.Name}");
            }
        }
    }

    private static IReadOnlySet<string> ReadConfiguredPackageSources(string configPath)
    {
        var document = XDocument.Load(configPath, LoadOptions.PreserveWhitespace);
        var packageSources = document.Descendants().FirstOrDefault(element =>
            element.Name.LocalName.Equals("packageSources", StringComparison.OrdinalIgnoreCase)) ??
            throw new SnapshotCaptureException("Selected NuGet.Config does not declare packageSources.");
        var configDirectory = Path.GetDirectoryName(Path.GetFullPath(configPath))!;
        return packageSources.Elements()
            .Where(element => element.Name.LocalName.Equals("add", StringComparison.OrdinalIgnoreCase))
            .Select(element => element.Attributes().FirstOrDefault(attribute =>
                attribute.Name.LocalName.Equals("value", StringComparison.OrdinalIgnoreCase))?.Value)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => NormalizePackageSource(value!, configDirectory))
            .ToHashSet(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    }

    private static string NormalizePackageSource(string value, string relativeTo)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && !uri.IsFile)
            return uri.AbsoluteUri.TrimEnd('/');
        var path = uri?.IsFile == true ? uri.LocalPath : value;
        return Path.GetFullPath(path, relativeTo).TrimEnd(Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
    }

    private static bool IsImplicitRuntimeLibraryPacksSource(string normalizedSource)
    {
        var runtimeLibraryPacks = Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(),
            "..", "..", "..", "library-packs")).TrimEnd(Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        return string.Equals(normalizedSource, runtimeLibraryPacks, OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    internal static async Task CopyTreeAsync(string source, string destination, CancellationToken cancellationToken,
        ResolvedGraphBudget? originalBudget = null)
    {
        Check();
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories)
                     .Select(path => { Check(); return path; }).OrderBy(path => path, StringComparer.Ordinal))
        {
            Check();
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            Check();
            if (originalBudget is null)
                await SnapshotWorkspace.CopyFileAsync(file, target, cancellationToken);
            else
            {
                // Same existing file-copy operation, now cooperatively bounded
                // at each actual chunk. No child allowance or retry is created.
                await using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 81920,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                Check();
                await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
                Check();
                var buffer = new byte[81920];
                while (true)
                {
                    Check(); var read = await input.ReadAsync(buffer, cancellationToken); Check();
                    if (read == 0) break;
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken); Check();
                }
                await output.FlushAsync(cancellationToken); Check();
            }
            Check();
        }
        Check();
        void Check() { originalBudget?.CheckWork(); cancellationToken.ThrowIfCancellationRequested(); }
    }

    private static string FingerprintTreeBounded(string root, int maxFiles, long maxBytes,
        CancellationToken cancellationToken, Func<string, bool>? include = null, ResolvedGraphBudget? originalBudget = null)
    {
        Check();
        var fullRoot = Path.GetFullPath(root);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var count = 0;
        long total = 0;
        var buffer = new byte[81920];
        Check();
        foreach (var file in Directory.EnumerateFiles(fullRoot, "*", SearchOption.AllDirectories)
                     .Select(path => { Check(); return path; })
                     .Where(path => include?.Invoke(path) != false)
                     .OrderBy(path => Path.GetRelativePath(fullRoot, path), StringComparer.Ordinal))
        {
            Check();
            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
                throw new ExecutionBoundaryIntegrityException(
                    "Resolved dependency content contains a link or reparse point.");
            if (++count > maxFiles) throw new SnapshotLimitException("Resolved dependency file count exceeds its bound.");
            Append(hash, Path.GetRelativePath(fullRoot, file).Replace('\\', '/'));
            using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, buffer.Length,
                FileOptions.SequentialScan);
            int read;
            while (true)
            {
                Check(); read = input.Read(buffer, 0, buffer.Length); Check();
                if (read == 0) break;
                checked { total += read; }
                if (total > maxBytes)
                    throw new SnapshotLimitException("Resolved dependency bytes exceed their bound.");
                hash.AppendData(buffer, 0, read);
                Check();
            }
        }
        Check();
        var identity = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        Check(); return identity;
        void Check() { originalBudget?.CheckWork(); cancellationToken.ThrowIfCancellationRequested(); }
    }

    private static string HashStrings(params string[] values)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var value in values) Append(hash, value);
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static void Append(IncrementalHash hash, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        hash.AppendData(BitConverter.GetBytes(bytes.Length));
        hash.AppendData(bytes);
    }

    private static void EnsureWithin(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        if (relative is ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) || Path.IsPathRooted(relative))
            throw new SnapshotCaptureException("Dependency entry escapes the snapshot.");
    }

    internal static void ValidateExecutionAncestors(string executionRoot)
    {
        // Boundary files terminate every supported upward MSBuild/editor/SDK search. Restore always
        // receives an explicit --configfile and the private Directory.Build.rsp terminates inherited response lookup,
        // so configurations
        // above the marker-validated private parent are not reachable inputs and must not create a
        // shared-ancestor denial of service.
        ValidateExecutionBoundary(executionRoot);
    }

    internal static IReadOnlyDictionary<string, string?> ProcessEnvironment(string packages)
    {
        var environment = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (System.Collections.DictionaryEntry variable in Environment.GetEnvironmentVariables())
        {
            var name = variable.Key as string;
            if (name is not null && IsStrictChildRedirectionVariable(name))
                environment[name] = null;
        }
        SanitizeStrictChildEnvironment(environment);
        SetEnvironmentOverride(environment, "NUGET_PACKAGES", packages);
        SetEnvironmentOverride(environment, "NUGET_FALLBACK_PACKAGES", null);
        SetEnvironmentOverride(environment, "MSBuildUserExtensionsPath", Path.Combine(
            Path.GetDirectoryName(Path.GetFullPath(packages))!, "msbuild-user-extensions"));
        SetEnvironmentOverride(environment, "MSBuildExtensionsPath", null);
        SetEnvironmentOverride(environment, "MSBUILDDISABLENODEREUSE", "1");
        SetEnvironmentOverride(environment, "DOTNET_CLI_USE_MSBUILD_SERVER", "0");
        return environment;
    }

    internal static void SanitizeStrictChildEnvironment(IDictionary<string, string?> environment)
    {
        foreach (var name in SnapshotCapture.RestoreOrImportRedirectionPropertyNames)
            SetEnvironmentOverride(environment, name, null);
        foreach (var name in StrictSdkResolverEnvironmentVariableNames)
            SetEnvironmentOverride(environment, name, null);
        foreach (var name in StrictSemanticPropertyEnvironmentVariableNames)
            SetEnvironmentOverride(environment, name, null);
    }

    private static bool IsStrictChildRedirectionVariable(string name) =>
        SnapshotCapture.IsRestoreOrImportRedirectionProperty(name) ||
        StrictSdkResolverEnvironmentVariableNames.Any(candidate =>
            name.Equals(candidate, StringComparison.OrdinalIgnoreCase)) ||
        StrictSemanticPropertyEnvironmentVariableNames.Any(candidate =>
            name.Equals(candidate, StringComparison.OrdinalIgnoreCase));

    private static void SetEnvironmentOverride(IDictionary<string, string?> environment, string name,
        string? value)
    {
        foreach (var existing in environment.Keys.Where(key =>
                     key.Equals(name, StringComparison.OrdinalIgnoreCase)).ToArray())
            environment.Remove(existing);
        environment[name] = value;
    }

    private static void WriteBoundary(string path, string content)
    {
        File.WriteAllText(path, content, new UTF8Encoding(false));
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    private static string Tail(params string[] values)
    {
        var lines = string.Join('\n', values).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        const string prefix = "Dependency preparation failed: ";
        return EvaluationTextBounds.Suffix(string.Join(" | ", lines.TakeLast(4)).Trim(),
            EvaluationReason.MaxMessageLength - prefix.Length);
    }
}
