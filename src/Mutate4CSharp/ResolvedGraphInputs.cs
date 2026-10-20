using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace Mutate4CSharp;

// Lifetime for complete physical inputs of ONE graph analysis. No pathname, DTO
// or digest returned here is itself an accepted compiler/task provenance proof.
internal sealed class ResolvedGraphInputs : IDisposable
{
    private static readonly ConcurrentBag<ResolvedGraphInputs> Unsettled = [];
    private bool _retained;
    internal const long MaximumBytes = 512L * 1024 * 1024;
    internal const int MaximumFiles = 32_768;
    internal const int MaximumFileBytes = 32 * 1024 * 1024;
    private readonly List<Frame> _frames = [];
    private readonly List<DirectoryFrame> _directories = [];
    private readonly ResolvedGraphBudget _budget;
    private byte[]? _imageScratch;
    private bool _imageScratchBorrowed;
    private bool _closing;
    private bool _disposeReturned;
    private Exception? _disposeFailure;
    private Exception? _failure;

    internal ResolvedGraphInputs(TimeSpan remaining, CancellationToken token)
        : this(new ResolvedGraphBudget(remaining, token)) { }

    internal ResolvedGraphInputs(ResolvedGraphBudget actualBudget)
    {
        _budget = actualBudget ?? throw new ArgumentNullException(nameof(actualBudget));
    }
    internal void RequireOriginal(ResolvedGraphBudget original)
    {
        if (!ReferenceEquals(_budget, original)) throw new EvaluationContractException("Exact same graph input original is required.");
        Check();
    }
    internal ResolvedGraphFile Read(string path, string role, int maximumFileBytes = MaximumFileBytes)
    {
        Check();
        if (maximumFileBytes is <= 0 or > MaximumFileBytes)
            throw new ArgumentOutOfRangeException(nameof(maximumFileBytes), "A local read bound may only restrict the existing original cap.");
        if (role is not ("source" or "generated-source" or "reference" or "analyzer" or "config" or "import" or "compiler"))
            throw new EvaluationContractException("Closed graph input role required.");
        if (!Path.IsPathFullyQualified(path) || path.Length > 4096 || _frames.Count + _directories.Count >= MaximumFiles)
            throw new SnapshotLimitException("Complete graph input path/count limit exceeded.");
        path = Path.GetFullPath(path);
        // Enroll before the synchronous constructor; retained failure and first
        // disposition flags prevent any retry from being mistaken for that call.
        var frame = new Frame { Path = path, Role = role };
        _budget.Resources.AdmitFrame();
        _frames.Add(frame);
        try
        {
            for (var ancestor = path; ancestor is not null; ancestor = Path.GetDirectoryName(ancestor))
            {
                Check();
                if ((File.GetAttributes(ancestor) & FileAttributes.ReparsePoint) != 0)
                    throw new SnapshotCaptureException("Redirected compiler input refused: " + path);
                Check();
            }
            if ((File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.Device)) != 0)
                throw new SnapshotCaptureException("Compiler input must be an actual regular file: " + path);
            Check(); frame.AcquireEntered = true;
            frame.Stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            frame.AcquireReturned = true; Check();
            var length = frame.Stream.Length; Check();
            if (length < 0 || length > maximumFileBytes)
                throw new SnapshotLimitException("Complete graph input byte admission exceeded.");
            _budget.Resources.AdmitSemantic(length);
            _budget.Resources.AdmitIo(length + 1); // Complete extent and EOF observation, no refunds.
            Check(); frame.Bytes = new byte[(int)length]; Check();
            var offset = 0;
            while (offset < frame.Bytes.Length)
            {
                Check();
                var read = frame.Stream.Read(frame.Bytes, offset, Math.Min(65_536, frame.Bytes.Length - offset));
                Check();
                if (read <= 0) throw new SnapshotDivergedException("Compiler input ended before its admitted length: " + path);
                offset += read;
            }
            Check(); var eof = frame.Stream.ReadByte(); Check();
            var afterLength = frame.Stream.Length; Check();
            if (eof != -1 || afterLength != length)
                throw new SnapshotDivergedException("Compiler input length/EOF changed: " + path);
            Check(); frame.Sha256 = Convert.ToHexString(SHA256.HashData(frame.Bytes)).ToLowerInvariant(); Check();
        }
        catch (Exception primary) { frame.Primary = primary; _failure ??= primary; _budget.BeginFailureCleanup(); }
        try { CheckEndpoint(); }
        catch (Exception post) { frame.Post = post; _failure ??= post; _budget.BeginFailureCleanup(); }
        FirstClose(frame);
        ThrowFrame(frame);
        Check();
        return new(path, role, frame.Bytes!, frame.Sha256!);
    }

    // Separate fixed producer-output operation: retain the actual CreateNew,
    // synchronous write/flush and first-close, including their original failures.
    internal void WriteNewResult(string path, byte[] completeBytes)
    {
        Check();
        if (!Path.IsPathFullyQualified(path) || path.Length > 4096 || _frames.Count + _directories.Count >= MaximumFiles ||
            completeBytes.LongLength > MaximumFileBytes)
            throw new SnapshotLimitException("Complete worker result admission exceeded.");
        path = Path.GetFullPath(path);
        var frame = new Frame { Path = path, Bytes = completeBytes };
        _budget.Resources.AdmitFrame();
        _frames.Add(frame);
        _budget.Resources.AdmitSemantic(completeBytes.LongLength);
        _budget.Resources.AdmitIo(completeBytes.LongLength);
        try
        {
            // Parent must have created the exact owned destination directory.
            // Refuse every existing ancestor redirect before opening the new file.
            for (var ancestor = Path.GetDirectoryName(path); ancestor is not null; ancestor = Path.GetDirectoryName(ancestor))
            {
                Check();
                if ((File.GetAttributes(ancestor) & FileAttributes.ReparsePoint) != 0)
                    throw new SnapshotCaptureException("Redirected worker result directory refused.");
                Check();
            }
            Check(); frame.AcquireEntered = true;
            frame.Stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            frame.AcquireReturned = true; Check();
            frame.WriteEntered = true; frame.Stream.Write(completeBytes); frame.WriteReturned = true; Check();
            frame.FlushEntered = true; frame.Stream.Flush(true); frame.FlushReturned = true; Check();
        }
        catch (Exception primary) { frame.Primary = primary; _failure ??= primary; _budget.BeginFailureCleanup(); }
        try { CheckEndpoint(); }
        catch (Exception post) { frame.Post = post; _failure ??= post; _budget.BeginFailureCleanup(); }
        FirstClose(frame);
        ThrowFrame(frame);
        if (!frame.WriteReturned || !frame.FlushReturned)
            throw new EvaluationContractException("Original complete worker result write/flush did not return.");
        Check();
    }
    // Post-build Coverlet artifact only. Preserve the original complete bytes,
    // exclusively verify them again, and append a bounded producer suffix.
    // This does not replace a compiler input or adopt an arbitrary pathname.
    internal ResolvedGraphFile AppendKnownResult(ResolvedGraphFile expected, byte[] suffix, int maximumBytes)
    {
        Check();
        if (expected.Role != "config" || maximumBytes is <= 0 or > 65_536 || suffix.Length == 0 ||
            expected.Bytes.LongLength + suffix.LongLength > maximumBytes ||
            !_frames.Any(f => f.Path == expected.Path && f.Role == expected.Role && ReferenceEquals(f.Bytes, expected.Bytes)))
            throw new EvaluationContractException("Exact original bounded collector artifact required before append.");
        var frame = new Frame { Path = expected.Path, Role = expected.Role }; _budget.Resources.AdmitFrame(); _frames.Add(frame);
        _budget.Resources.AdmitSemantic(expected.Bytes.LongLength);
        _budget.Resources.AdmitIo(expected.Bytes.LongLength + 1 + suffix.LongLength);
        try
        {
            for (var ancestor = expected.Path; ancestor is not null; ancestor = Path.GetDirectoryName(ancestor))
            {
                Check(); var attributes = File.GetAttributes(ancestor); Check();
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new SnapshotCaptureException("Redirected collector artifact refused.");
            }
            Check(); frame.AcquireEntered = true;
            frame.Stream = new FileStream(expected.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            frame.AcquireReturned = true; Check();
            if (frame.Stream.Length != expected.Bytes.LongLength) throw new SnapshotDivergedException("Original collector extent changed.");
            frame.Bytes = new byte[expected.Bytes.Length]; var offset = 0;
            while (offset < frame.Bytes.Length)
            {
                Check(); var read = frame.Stream.Read(frame.Bytes, offset, frame.Bytes.Length - offset); Check();
                if (read == 0) throw new SnapshotDivergedException("Original collector artifact ended early.");
                offset += read;
            }
            Check(); var eof = frame.Stream.ReadByte(); Check();
            if (eof != -1 || frame.Stream.Length != expected.Bytes.LongLength || !frame.Bytes.AsSpan().SequenceEqual(expected.Bytes))
                throw new SnapshotDivergedException("Original collector bytes changed before append.");
            frame.WriteEntered = true; frame.Stream.Write(suffix); frame.WriteReturned = true; Check();
            frame.FlushEntered = true; frame.Stream.Flush(true); frame.FlushReturned = true; Check();
            if (frame.Stream.Length != expected.Bytes.LongLength + suffix.LongLength)
                throw new SnapshotDivergedException("Complete appended collector extent differs.");
        }
        catch (Exception failure) { frame.Primary = failure; _failure ??= failure; _budget.BeginFailureCleanup(); }
        try { CheckEndpoint(); }
        catch (Exception failure) { frame.Post = failure; _failure ??= failure; _budget.BeginFailureCleanup(); }
        FirstClose(frame); ThrowFrame(frame); Check();
        if (!frame.WriteReturned || !frame.FlushReturned)
            throw new EvaluationContractException("Original collector append did not complete and close.");
        var actual = Read(expected.Path, "config", maximumBytes);
        if (actual.Bytes.Length != expected.Bytes.Length + suffix.Length ||
            !actual.Bytes.AsSpan(0, expected.Bytes.Length).SequenceEqual(expected.Bytes) ||
            !actual.Bytes.AsSpan(expected.Bytes.Length).SequenceEqual(suffix))
        {
            var failure = new SnapshotDivergedException("Complete appended collector bytes changed.");
            _failure ??= failure; _budget.BeginFailureCleanup(); throw failure;
        }
        return actual;
    }

    internal bool GeneratedDirectoryPresent(string path)
    {
        Check();
        if (!Path.IsPathFullyQualified(path) || path.Length > 4096)
            throw new SnapshotLimitException("Generated directory path extent.");
        try
        {
            var attributes = File.GetAttributes(path); Check();
            if ((attributes & FileAttributes.ReparsePoint) != 0 || (attributes & FileAttributes.Directory) == 0)
                throw new SnapshotCaptureException("Generated root must be an actual unredirected directory.");
            return true;
        }
        catch (FileNotFoundException) { Check(); return false; }
        catch (DirectoryNotFoundException) { Check(); return false; }
    }

    internal IReadOnlyList<ResolvedGraphEntry> EnumerateGeneratedDirectory(string path)
    {
        Check();
        if (!Path.IsPathFullyQualified(path) || path.Length > 4096 || _frames.Count + _directories.Count >= MaximumFiles)
            throw new SnapshotLimitException("Generated directory admission limit.");
        var frame = new DirectoryFrame { Path = Path.GetFullPath(path) }; _budget.Resources.AdmitFrame(); _directories.Add(frame);
        var output = new List<ResolvedGraphEntry>();
        try
        {
            Check(); frame.FactoryEntered = true;
            frame.Enumerable = Directory.EnumerateFileSystemEntries(path); frame.FactoryReturned = true; Check();
            frame.AcquireEntered = true; frame.Enumerator = frame.Enumerable.GetEnumerator(); frame.AcquireReturned = true; Check();
            for (;;)
            {
                Check(); frame.StepEntered++;
                var more = frame.Enumerator.MoveNext(); frame.StepReturned++; Check();
                if (!more) break;
                var current = frame.Enumerator.Current; Check();
                if (current.Length > 4096 || output.Count == 8192)
                    throw new SnapshotLimitException("Complete generated directory string/count allocation admission.");
                _budget.Resources.AdmitSemantic(current.Length * 2L);
                var attributes = File.GetAttributes(current); Check();
                if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
                    throw new SnapshotCaptureException("Generated directory entry redirect/device refused.");
                output.Add(new(current, attributes)); Check();
            }
        }
        catch (Exception failure) { frame.Primary = failure; _failure ??= failure; _budget.BeginFailureCleanup(); }
        try { CheckEndpoint(); }
        catch (Exception failure) { frame.Post = failure; _failure ??= failure; _budget.BeginFailureCleanup(); }
        CloseDirectory(frame); RequireDirectory(frame); Check();
        return output;
    }

    private void CloseDirectory(DirectoryFrame frame)
    {
        if (!frame.AcquireReturned || frame.Enumerator is null || frame.CloseAttempted) return;
        frame.CloseAttempted = true;
        try { CheckEndpoint(); frame.CloseEntered = true; frame.Enumerator.Dispose(); frame.CloseReturned = true; }
        catch (Exception failure) { frame.CloseFailure = failure; _failure ??= failure; _budget.BeginFailureCleanup(); }
        try { CheckEndpoint(); }
        catch (Exception failure) { frame.ClosePost = failure; _failure ??= failure; _budget.BeginFailureCleanup(); }
    }

    private void RequireDirectory(DirectoryFrame frame)
    {
        if (Unknown(frame)) Retain();
        var failures = new[] { frame.Primary, frame.Post, frame.CloseFailure, frame.ClosePost }.OfType<Exception>().ToArray();
        if (failures.Length > 0) throw new AggregateException("Original generated enumerator primary/post/first-close retained.", failures);
        if (!frame.FactoryEntered || !frame.FactoryReturned || !frame.AcquireEntered || !frame.AcquireReturned ||
            frame.StepEntered != frame.StepReturned || !frame.CloseEntered || !frame.CloseReturned)
            throw new EvaluationContractException("Original generated enumerator remains unknown; no dependent release.");
    }
    internal void Recheck(ResolvedGraphFile expected, int maximumFileBytes = MaximumFileBytes)
    {
        var actual = Read(expected.Path, expected.Role, maximumFileBytes);
        if (actual.Bytes.LongLength != expected.Bytes.LongLength || actual.Sha256 != expected.Sha256)
        {
            var failure = new SnapshotDivergedException("Actual graph input changed across its compiler/semantic boundary: " + expected.Path);
            _failure ??= failure;
            _budget.BeginFailureCleanup();
            throw failure;
        }
    }

    internal void CheckWork() => Check();
    internal void BeginFailureCleanup() => _budget.BeginFailureCleanup();
    internal void CheckDisposition() => _budget.CheckDisposition();

    // Additional managed buffers belong to this SAME cumulative ledger. This
    // reserves before allocating and never refunds on success, failure or close.
    internal void ReserveAllocation(long bytes)
    {
        Check();
        _budget.Resources.AdmitSemantic(bytes);
        Check();
    }

    // Hash-only images retain a complete private acquisition/disposition frame,
    // not image bytes. EVERY call and recheck reads its entire actual file.
    internal ResolvedGraphImagePin PinImage(string path, string role = "compiler")
    {
        Check();
        if (role is not ("compiler" or "import") || !Path.IsPathFullyQualified(path) || path.Length > 4096)
            throw new EvaluationContractException("Exact closed hash-only image role/path required.");
        path = Path.GetFullPath(path);
        var frame = new Frame { Path = path, Role = role }; _budget.Resources.AdmitFrame(); _frames.Add(frame);
        long length = -1;
        try
        {
            if (_imageScratchBorrowed) throw new EvaluationContractException("Original image buffer is already borrowed.");
            if (_imageScratch is null)
            {
                _budget.Resources.AdmitSemantic(65_536); Check(); _imageScratch = new byte[65_536]; Check();
            }
            _imageScratchBorrowed = true;
            for (var ancestor = path; ancestor is not null; ancestor = Path.GetDirectoryName(ancestor))
            {
                Check(); var attributes = File.GetAttributes(ancestor); Check();
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw new SnapshotCaptureException("Redirected actual image refused.");
            }
            Check(); var fileAttributes = File.GetAttributes(path); Check();
            if ((fileAttributes & (FileAttributes.Directory | FileAttributes.Device)) != 0)
                throw new SnapshotCaptureException("Actual image must be a regular file.");
            Check(); frame.AcquireEntered = true;
            frame.Stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            frame.AcquireReturned = true; Check();
            length = frame.Stream.Length; Check();
            if (length is <= 0 or > MaximumFileBytes) throw new SnapshotLimitException("Complete actual image extent admission exceeded.");
            _budget.Resources.AdmitIo(length + 1); Check();
            frame.HashAcquireEntered = true; frame.Hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            frame.HashAcquireReturned = true; Check();
            long offset = 0;
            while (offset < length)
            {
                Check(); var count = frame.Stream.Read(_imageScratch, 0, (int)Math.Min(_imageScratch.Length, length - offset)); Check();
                if (count <= 0) throw new SnapshotDivergedException("Complete actual image ended early.");
                frame.Hash.AppendData(_imageScratch, 0, count); Check(); offset += count;
            }
            Check(); var eof = frame.Stream.ReadByte(); Check(); var secondLength = frame.Stream.Length; Check();
            if (eof != -1 || secondLength != length) throw new SnapshotDivergedException("Actual image EOF/length changed.");
            _budget.Resources.AdmitSemantic(512); Check(); // Digest + both bounded hex strings and fixed overhead.
            var digest = frame.Hash.GetHashAndReset(); Check();
            frame.Sha256 = Convert.ToHexString(digest).ToLowerInvariant(); Check();
        }
        catch (Exception primary) { frame.Primary = primary; _failure ??= primary; _budget.BeginFailureCleanup(); }
        try { CheckEndpoint(); }
        catch (Exception post) { frame.Post = post; _failure ??= post; _budget.BeginFailureCleanup(); }
        // First hash disposition and stream disposition are independent. A
        // failed close is retained and never retried by final Dispose.
        FirstCloseHash(frame); FirstClose(frame); _imageScratchBorrowed = false;
        ThrowFrame(frame); Check();
        return new(path, role, length, frame.Sha256!);
    }

    internal void Recheck(ResolvedGraphImagePin expected)
    {
        var actual = PinImage(expected.Path, expected.Role);
        if (actual.Bytes != expected.Bytes || actual.Sha256 != expected.Sha256)
        {
            var failure = new SnapshotDivergedException("Actual complete image changed across compiler boundary: " + expected.Path);
            _failure ??= failure; _budget.BeginFailureCleanup(); throw failure;
        }
    }

    private void FirstCloseHash(Frame frame)
    {
        if (!frame.HashAcquireReturned || frame.Hash is null || frame.HashCloseAttempted) return;
        frame.HashCloseAttempted = true;
        try { CheckEndpoint(); frame.HashCloseEntered = true; frame.Hash.Dispose(); frame.HashCloseReturned = true; }
        catch (Exception close) { frame.HashCloseFailure = close; _failure ??= close; _budget.BeginFailureCleanup(); }
        try { CheckEndpoint(); }
        catch (Exception post) { frame.HashClosePost = post; _failure ??= post; _budget.BeginFailureCleanup(); }
    }

    private void Check()
    {
        if (_closing || _failure is not null)
            throw new EvaluationContractException("Graph input owner is closing or has a retained failure.");
        _budget.CheckWork();
    }
    private void CheckEndpoint() => _budget.CheckDisposition();
    private void FirstClose(Frame frame)
    {
        if (!frame.AcquireReturned || frame.Stream is null || frame.DispositionAttempted) return;
        frame.DispositionAttempted = true;
        try
        {
            // This never grants an extra cleanup interval after expiry. The caller
            // must dispose under its existing shrinking original owner allowance.
            CheckEndpoint(); frame.CloseEntered = true; frame.Stream.Dispose(); frame.CloseReturned = true;
        }
        catch (Exception close) { frame.CloseFailure = close; _failure ??= close; _budget.BeginFailureCleanup(); }
        try { CheckEndpoint(); }
        catch (Exception post) { frame.ClosePost = post; _failure ??= post; _budget.BeginFailureCleanup(); }
    }
    private void ThrowFrame(Frame frame)
    {
        if (Unknown(frame)) Retain();
        var failures = new[] { frame.Primary, frame.Post, frame.HashCloseFailure, frame.HashClosePost, frame.CloseFailure, frame.ClosePost }
            .OfType<Exception>().ToArray();
        if (failures.Length > 0) throw new AggregateException("Actual graph input and independent first-close outcomes retained.", failures);
        if (frame.WriteEntered && !frame.WriteReturned || frame.FlushEntered && !frame.FlushReturned)
            throw new EvaluationContractException("Original worker result write/flush remains unknown.");
        if (frame.AcquireEntered && (!frame.AcquireReturned || !frame.CloseEntered || !frame.CloseReturned))
            throw new EvaluationContractException("Actual original graph input acquisition/disposition remains unknown.");
        if (frame.HashAcquireEntered && (!frame.HashAcquireReturned || !frame.HashCloseEntered || !frame.HashCloseReturned))
            throw new EvaluationContractException("Actual original image hash acquisition/disposition remains unknown.");
    }
    private void Retain() { if (!_retained) { _retained = true; Unsettled.Add(this); } }
    private static bool Unknown(Frame frame) =>
        frame.AcquireEntered && (!frame.AcquireReturned || !frame.CloseReturned || frame.CloseFailure is not null || frame.ClosePost is not null) ||
        frame.HashAcquireEntered && (!frame.HashAcquireReturned || !frame.HashCloseReturned || frame.HashCloseFailure is not null || frame.HashClosePost is not null);
    private static bool Unknown(DirectoryFrame frame) =>
        frame.FactoryEntered && !frame.FactoryReturned || frame.AcquireEntered &&
        (!frame.AcquireReturned || !frame.CloseReturned || frame.CloseFailure is not null || frame.ClosePost is not null);
    internal static void AssertDirectoryMayBeRemoved(string directory)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        foreach (var owner in Unsettled)
        {
            foreach (var frame in owner._frames)
                if (Unknown(frame) && frame.Path is { } path && Within(path)) Refuse();
            foreach (var frame in owner._directories)
                if (Unknown(frame) && frame.Path is { } path && Within(path)) Refuse();
        }
        bool Within(string path) => path.Equals(root, comparison) || path.StartsWith(root + Path.DirectorySeparatorChar, comparison);
        static void Refuse() => throw new SnapshotCleanupException(new IOException(
            "An actual graph input first-disposition is unsettled; its original namespace is retained."));
    }
    public void Dispose()
    {
        if (_closing)
        {
            if (_disposeFailure is not null) throw _disposeFailure;
            if (!_disposeReturned) throw new EvaluationContractException("Original graph input disposal is entered and unreturned; no retry.");
            return;
        }
        _closing = true;
        try
        {
            List<Exception> failures = [];
            for (var i = _directories.Count - 1; i >= 0; --i)
            {
                CloseDirectory(_directories[i]);
                try { RequireDirectory(_directories[i]); }
                catch (Exception failure) { failures.Add(failure); }
            }
            for (var i = _frames.Count - 1; i >= 0; --i)
            {
                FirstCloseHash(_frames[i]); FirstClose(_frames[i]);
                try { ThrowFrame(_frames[i]); }
                catch (Exception failure) { failures.Add(failure); }
            }
            if (failures.Count > 0)
                throw new AggregateException("Graph input originals remain retained after failed first dispositions.", failures);
            _disposeReturned = true;
        }
        catch (Exception failure) { _disposeFailure = failure; throw; }
    }
    private sealed class DirectoryFrame
    {
        internal string? Path;
        internal IEnumerable<string>? Enumerable;
        internal IEnumerator<string>? Enumerator;
        internal bool FactoryEntered, FactoryReturned, AcquireEntered, AcquireReturned;
        internal bool CloseAttempted, CloseEntered, CloseReturned;
        internal int StepEntered, StepReturned;
        internal Exception? Primary, Post, CloseFailure, ClosePost;
    }
    private sealed class Frame
    {
        internal FileStream? Stream;
        internal string? Path, Role;
        internal IncrementalHash? Hash;
        internal byte[]? Bytes;
        internal string? Sha256;
        internal bool AcquireEntered, AcquireReturned, DispositionAttempted, CloseEntered, CloseReturned;
        internal bool WriteEntered, WriteReturned, FlushEntered, FlushReturned;
        internal bool HashAcquireEntered, HashAcquireReturned, HashCloseAttempted, HashCloseEntered, HashCloseReturned;
        internal Exception? HashCloseFailure, HashClosePost;
        internal Exception? Primary, Post, CloseFailure, ClosePost;
    }
}

internal sealed record ResolvedGraphFile(string Path, string Role, byte[] Bytes, string Sha256);
internal sealed record ResolvedGraphImagePin(string Path, string Role, long Bytes, string Sha256);

internal sealed record ResolvedGraphEntry(string Path, FileAttributes Attributes);
