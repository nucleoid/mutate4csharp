namespace Mutate4CSharp;

internal sealed record SnapshotFile(string RelativePath, long Length, string Sha256,
    bool IsTracked, bool Exists);

internal sealed record SnapshotIdentity(string CaptureId, string BaseCommit, string IndexFingerprint,
    int FileCount, long TotalBytes);

internal class SnapshotCaptureException(string message, Exception? inner = null) : IOException(message, inner);
internal sealed class SnapshotDivergedException(string message) : SnapshotCaptureException(message);
internal sealed class SnapshotLimitException(string message) : SnapshotCaptureException(message);
internal sealed class SnapshotEnvironmentException(string message, Exception? inner = null) :
    SnapshotCaptureException(message, inner);
internal sealed class ExecutionEnvironmentUnavailableException(string message, Exception? inner = null) :
    SnapshotCaptureException(message, inner);
internal sealed class ExecutionBoundaryIntegrityException(string message, Exception? inner = null) :
    SnapshotCaptureException(message, inner);
internal sealed class SnapshotCleanupException : SnapshotCaptureException
{
    public SnapshotCleanupException(Exception originalFailure, Exception cleanupFailure)
        : base($"Snapshot cleanup failed after {Describe(originalFailure)}; cleanup failure: {cleanupFailure.Message}",
            new AggregateException(originalFailure, cleanupFailure))
    {
        OriginalFailure = originalFailure;
        CleanupFailure = cleanupFailure;
    }

    public Exception OriginalFailure { get; }
    public Exception CleanupFailure { get; }

    private static string Describe(Exception exception) =>
        $"{exception.GetType().Name}: {exception.Message}";
}

internal sealed class InputSnapshot : IAsyncDisposable
{
    private readonly OwnedDirectory _owner;
    private readonly SnapshotCapture.Inventory _inventory;
    private readonly SnapshotCaptureOptions _options;
    private bool _disposed;

    internal InputSnapshot(string originalRoot, string captureRoot, SnapshotIdentity identity,
        IReadOnlyList<SnapshotFile> files, OwnedDirectory owner, SnapshotCapture.Inventory inventory,
        SnapshotCaptureOptions options)
    {
        OriginalRoot = originalRoot;
        CaptureRoot = captureRoot;
        Identity = identity;
        Files = files;
        _owner = owner;
        _inventory = inventory;
        _options = options;
    }

    public string OriginalRoot { get; }
    public string CaptureRoot { get; }
    public SnapshotIdentity Identity { get; }
    public IReadOnlyList<SnapshotFile> Files { get; }
    internal SnapshotCaptureOptions Options => _options;
    internal IReadOnlyList<string> ExcludedEntries => _inventory.ExcludedEntries;
    internal IReadOnlyList<SnapshotCapture.IgnoredEntry> IgnoredEntries => _inventory.IgnoredEntries;

    public async Task ValidateOriginalAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        try { await SnapshotCapture.ValidateOriginalAsync(this, _inventory, _options, cancellationToken); }
        catch (Exception ex) when (ex is (IOException or UnauthorizedAccessException) &&
                                   ex is not SnapshotCaptureException)
        {
            throw new SnapshotDivergedException($"Input became unavailable during completion validation: {ex.Message}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await _owner.DisposeAsync();
    }
}
