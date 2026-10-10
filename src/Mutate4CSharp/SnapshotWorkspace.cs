using System.Security.Cryptography;
using System.Text;

namespace Mutate4CSharp;

internal sealed class OwnedDirectory : IAsyncDisposable
{
    private const string MarkerName = ".mutate4csharp-owner";
    private const string RootMarkerName = ".mutate4csharp-root-owner";
    private static readonly object ProcessRootGate = new();
    private static readonly Lazy<(string Path, string Token)> ProcessPrivateRoot = new(CreateProcessPrivateRoot);
    private static int _activeProcessDirectories;
    private readonly string _parent;
    private readonly string _token;
    private readonly bool _processManaged;
    private bool _disposed;

    private OwnedDirectory(string parent, string root, string token, bool processManaged)
    {
        _parent = parent;
        Root = root;
        _token = token;
        _processManaged = processManaged;
    }

    public string Root { get; }

    internal static string PrivateParent(string category)
    {
        var safeCategory = Sanitize(category, "owned");
        lock (ProcessRootGate)
        {
            EnsureProcessRoot();
            return Path.Combine(ProcessPrivateRoot.Value.Path, safeCategory);
        }
    }

    private static (string Path, string Token) CreateProcessPrivateRoot()
    {
        var identity = $"{Environment.UserName}\0{Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)}";
        var user = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..16].ToLowerInvariant();
        var root = Path.Combine(Path.GetTempPath(), $"mutate4csharp-{user}-{Guid.NewGuid():N}");
        return (root, Guid.NewGuid().ToString("N"));
    }

    private static void EnsureProcessRoot()
    {
        var (root, token) = ProcessPrivateRoot.Value;
        Directory.CreateDirectory(root);
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Private temporary root cannot be a link or reparse point.");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var marker = Path.Combine(root, RootMarkerName);
        if (File.Exists(marker))
        {
            if (!string.Equals(File.ReadAllText(marker), token, StringComparison.Ordinal))
                throw new IOException("Private temporary root ownership marker does not match this process.");
        }
        else
        {
            File.WriteAllText(marker, token, new UTF8Encoding(false));
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(marker, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    public static OwnedDirectory Create(string parent, string prefix)
    {
        var token = Guid.NewGuid().ToString("N");
        var safePrefix = Sanitize(prefix, "owned");
        var fullParent = Path.GetFullPath(parent);
        var processManaged = false;
        lock (ProcessRootGate)
        {
            var processRoot = Path.GetFullPath(ProcessPrivateRoot.Value.Path);
            var relativeParent = Path.GetRelativePath(processRoot, fullParent);
            processManaged = relativeParent is not ("" or "." or "..") &&
                             !relativeParent.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
                             !Path.IsPathRooted(relativeParent) &&
                             !relativeParent.Contains(Path.DirectorySeparatorChar);
            if (processManaged)
            {
                EnsureProcessRoot();
                _activeProcessDirectories++;
            }
        }
        try
        {
            var tempRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
            if (string.Equals(fullParent.TrimEnd(Path.DirectorySeparatorChar), tempRoot,
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new IOException("Owned temporary directories require a dedicated private parent.");
            Directory.CreateDirectory(fullParent);
            if ((File.GetAttributes(fullParent) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Owned temporary parent cannot be a link or reparse point.");
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(fullParent, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                var mode = File.GetUnixFileMode(fullParent);
                if (mode != (UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute))
                    throw new IOException("Owned temporary parent is not private.");
            }
            var root = Path.Combine(fullParent, $"{safePrefix}-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Owned temporary directory cannot be a link or reparse point.");
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var marker = Path.Combine(root, MarkerName);
            File.WriteAllText(marker, token, new UTF8Encoding(false));
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(marker, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            return new(fullParent, root, token, processManaged);
        }
        catch
        {
            if (processManaged)
            {
                lock (ProcessRootGate)
                {
                    _activeProcessDirectories--;
                    TryRemoveEmptyProcessRoots(fullParent);
                }
            }
            throw;
        }
    }

    private static string Sanitize(string value, string fallback)
    {
        var safe = new string(value.Where(character => char.IsAsciiLetterOrDigit(character) ||
            character is '-' or '_').ToArray());
        return safe.Length == 0 ? fallback : safe;
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true;
        Exception? failure = null;
        try
        {
            var fullRoot = Path.GetFullPath(Root);
            var relative = Path.GetRelativePath(_parent, fullRoot);
            if (relative is "" or "." or ".." ||
                relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
                Path.IsPathRooted(relative))
                throw new IOException("Refusing cleanup outside the owned parent.");
            var marker = Path.Combine(fullRoot, MarkerName);
            if (!File.Exists(marker) || !string.Equals(File.ReadAllText(marker), _token, StringComparison.Ordinal))
                throw new IOException("Refusing cleanup without the matching ownership marker.");
            Directory.Delete(fullRoot, recursive: true);
        }
        catch (Exception ex)
        {
            failure = ex;
        }
        finally
        {
            if (_processManaged)
            {
                lock (ProcessRootGate)
                {
                    _activeProcessDirectories--;
                    try { TryRemoveEmptyProcessRoots(_parent); }
                    catch (Exception ex)
                    {
                        failure = failure is null
                            ? ex
                            : new IOException("Owned-directory and process-root cleanup both failed.",
                                new AggregateException(failure, ex));
                    }
                }
            }
        }
        if (failure is not null)
            throw failure is SnapshotCleanupException ? failure : new SnapshotCleanupException(failure);
        return ValueTask.CompletedTask;
    }

    private static void TryRemoveEmptyProcessRoots(string parent)
    {
        var (processRoot, processToken) = ProcessPrivateRoot.Value;
        if (_activeProcessDirectories != 0 || !Directory.Exists(processRoot)) return;
        var rootMarker = Path.Combine(processRoot, RootMarkerName);
        if (!File.Exists(rootMarker) || !string.Equals(File.ReadAllText(rootMarker), processToken, StringComparison.Ordinal))
            return;
        if (Directory.Exists(parent) && !Directory.EnumerateFileSystemEntries(parent).Any())
            Directory.Delete(parent);
        if (Directory.EnumerateFileSystemEntries(processRoot)
            .All(path => string.Equals(Path.GetFullPath(path), Path.GetFullPath(rootMarker),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)))
        {
            File.Delete(rootMarker);
            Directory.Delete(processRoot);
        }
    }
}

internal static class SnapshotWorkspace
{
    public static async Task<SnapshotClone> CreateCloneAsync(InputSnapshot snapshot, string purpose,
        CancellationToken cancellationToken)
    {
        var parent = OwnedDirectory.PrivateParent("workspaces");
        var owner = OwnedDirectory.Create(parent, Sanitize(purpose));
        try
        {
            var root = Path.Combine(owner.Root, "root");
            Directory.CreateDirectory(root);
            await ExecutionEnvironment.CreateExecutionBoundaryAsync(owner.Root, snapshot.OriginalRoot, cancellationToken);
            foreach (var file in snapshot.Files.Where(file => file.Exists))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var source = Path.Combine(snapshot.CaptureRoot, file.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                var destination = Path.Combine(root, file.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                await CopyFileAsync(source, destination, cancellationToken);
                var copied = await File.ReadAllBytesAsync(destination, cancellationToken);
                if (copied.LongLength != file.Length || !CryptographicOperations.FixedTimeEquals(
                        SHA256.HashData(copied), Convert.FromHexString(file.Sha256)))
                    throw new SnapshotDivergedException($"Captured bytes changed before clone creation: {file.RelativePath}");
            }
            return new SnapshotClone(owner, root, snapshot.OriginalRoot, snapshot.Identity.CaptureId);
        }
        catch (Exception cloneFailure)
        {
            try { await owner.DisposeAsync(); }
            catch (Exception cleanupFailure)
            {
                throw new SnapshotCleanupException(cloneFailure, cleanupFailure);
            }
            throw;
        }
    }

    internal static async Task CopyFileAsync(string source, string destination, CancellationToken cancellationToken)
    {
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await input.CopyToAsync(output, cancellationToken);
        await output.FlushAsync(cancellationToken);
    }

    private static string Sanitize(string purpose)
    {
        var value = new string(purpose.Where(character => char.IsLetterOrDigit(character) || character is '-' or '_').ToArray());
        return string.IsNullOrEmpty(value) ? "workspace" : value;
    }
}

internal sealed class SnapshotClone(OwnedDirectory owner, string root, string originalRoot,
    string captureId) : IAsyncDisposable
{
    public string Root { get; } = root;
    public string OwnedRoot { get; } = owner.Root;
    public string CaptureId { get; } = captureId;

    public string ToCanonicalPath(string clonePath)
    {
        var full = Path.GetFullPath(clonePath);
        var relative = Path.GetRelativePath(Root, full);
        if (relative is ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            Path.IsPathRooted(relative))
            throw new ArgumentException("Coverage path is outside the snapshot clone.");
        return Path.GetFullPath(relative, originalRoot);
    }

    public void WriteTextMutation(string relativePath, int start, int length, string replacement)
    {
        var path = Path.GetFullPath(relativePath.Replace('/', Path.DirectorySeparatorChar), Root);
        var relative = Path.GetRelativePath(Root, path);
        if (relative is ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new ArgumentException("Mutation path is outside the snapshot clone.");
        var bytes = File.ReadAllBytes(path);
        var (encoding, preambleLength) = DetectEncoding(bytes);
        var text = encoding.GetString(bytes, preambleLength, bytes.Length - preambleLength);
        if (start < 0 || length < 0 || start + length > text.Length) throw new ArgumentOutOfRangeException(nameof(start));
        var changed = text[..start] + replacement + text[(start + length)..];
        var content = encoding.GetBytes(changed);
        var preamble = bytes.AsSpan(0, preambleLength).ToArray();
        File.WriteAllBytes(path, preamble.Concat(content).ToArray());
    }

    private static (Encoding Encoding, int PreambleLength) DetectEncoding(byte[] bytes)
    {
        if (bytes.AsSpan().StartsWith(Encoding.UTF8.GetPreamble())) return (new UTF8Encoding(false, true), 3);
        if (bytes.AsSpan().StartsWith(Encoding.Unicode.GetPreamble())) return (new UnicodeEncoding(false, false, true), 2);
        if (bytes.AsSpan().StartsWith(Encoding.BigEndianUnicode.GetPreamble())) return (new UnicodeEncoding(true, false, true), 2);
        return (new UTF8Encoding(false, true), 0);
    }

    public ValueTask DisposeAsync() => owner.DisposeAsync();
}
