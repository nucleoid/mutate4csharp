using System.Security.Cryptography;
using System.Text;

namespace Mutate4CSharp;

internal sealed record GitFileChange(ChangeKind Kind, string? BasePath, string? CurrentPath,
    byte[]? BaseBytes, byte[]? CurrentBytes)
{
    public string? BaseObjectId { get; init; }
    public string? CurrentObjectId { get; init; }
}
internal sealed record GitChangeSet(string BaseCommit, IReadOnlyList<GitFileChange> Files);

internal static class GitChangePlanner
{
    private const int MaxChangedPaths = 100_000;

    public static async Task<GitChangeSet> DiscoverAsync(InputSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        var baseTree = ParseBaseTree(await SnapshotCapture.RunGitBytesAsync(snapshot.OriginalRoot,
            ["ls-tree", "-r", "-z", snapshot.Identity.BaseCommit], snapshot.Options, cancellationToken),
            snapshot.Options);
        var captured = snapshot.Files.Where(file => file.Exists)
            .ToDictionary(file => file.RelativePath, StringComparer.Ordinal);
        var paths = baseTree.Keys.Concat(captured.Keys).Distinct(StringComparer.Ordinal)
            .OrderBy(path => path, StringComparer.Ordinal).ToArray();

        var changes = new List<GitFileChange>();
        foreach (var path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            byte[]? current = null;
            if (captured.TryGetValue(path, out _))
                current = await File.ReadAllBytesAsync(Path.Combine(snapshot.CaptureRoot,
                    path.Replace('/', Path.DirectorySeparatorChar)), cancellationToken);
            var hasBase = baseTree.TryGetValue(path, out var baseObject);
            string? currentObject = null;
            if (current is not null)
            {
                var rawObject = hasBase ? GitBlobHash(current, baseObject!.Length) : null;
                currentObject = hasBase && rawObject == baseObject ? rawObject :
                    await HashCapturedBlobAsync(snapshot, path, current, cancellationToken);
                if (hasBase && currentObject == baseObject) continue;
            }
            var kind = !hasBase ? ChangeKind.Added : current is null ? ChangeKind.Deleted : ChangeKind.Modified;
            byte[]? before = null;
            if (kind != ChangeKind.Added)
                before = await ReadBaseBlobAsync(snapshot, baseObject!, cancellationToken);
            changes.Add(new(kind, kind == ChangeKind.Added ? null : path,
                kind == ChangeKind.Deleted ? null : path, before, current)
                { BaseObjectId = hasBase ? baseObject : null, CurrentObjectId = currentObject });
            if (changes.Count > MaxChangedPaths)
                throw new SnapshotLimitException($"Git change planning exceeded {MaxChangedPaths} changed paths.");
        }

        PairExactRenames(changes);
        return new(snapshot.Identity.BaseCommit, changes.OrderBy(change => change.CurrentPath ?? change.BasePath,
            StringComparer.Ordinal).ToArray());
    }

    private static void PairExactRenames(List<GitFileChange> changes)
    {
        var deleted = changes.Where(change => change.Kind == ChangeKind.Deleted && change.BaseBytes is not null)
            .GroupBy(change => change.BaseObjectId ?? Hash(change.BaseBytes!), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var added = changes.Where(change => change.Kind == ChangeKind.Added && change.CurrentBytes is not null)
            .GroupBy(change => change.CurrentObjectId ?? Hash(change.CurrentBytes!), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var replacements = new List<(GitFileChange Deleted, GitFileChange Added, GitFileChange Renamed)>();
        foreach (var hash in deleted.Keys.Intersect(added.Keys, StringComparer.Ordinal).OrderBy(value => value,
                     StringComparer.Ordinal))
        {
            if (deleted[hash].Length != 1 || added[hash].Length != 1) continue;
            var oldFile = deleted[hash][0];
            var newFile = added[hash][0];
            if (!IsCSharp(oldFile.BasePath!) || !IsCSharp(newFile.CurrentPath!)) continue;
            replacements.Add((oldFile, newFile, new(ChangeKind.Renamed, oldFile.BasePath,
                newFile.CurrentPath, oldFile.BaseBytes, newFile.CurrentBytes)
                { BaseObjectId = oldFile.BaseObjectId, CurrentObjectId = newFile.CurrentObjectId }));
        }
        foreach (var replacement in replacements)
        {
            changes.Remove(replacement.Deleted);
            changes.Remove(replacement.Added);
            changes.Add(replacement.Renamed);
        }
    }

    private static Dictionary<string, string> ParseBaseTree(byte[] bytes, SnapshotCaptureOptions options)
    {
        var records = new List<(string Path, string ObjectId)>();
        foreach (var field in SplitNull(bytes))
        {
            var tab = field.IndexOf('\t');
            if (tab <= 0) throw new SnapshotCaptureException("Git base-tree inventory was malformed.");
            var metadata = field[..tab].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (metadata.Length != 3 || metadata[1] != "blob") continue;
            var path = field[(tab + 1)..];
            var normalized = SnapshotInputPolicy.NormalizeRelative(path);
            records.Add((normalized, metadata[2]));
        }
        var projectDirectories = records.Where(item => item.Path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            .Select(item => item.Path.Contains('/') ? item.Path[..item.Path.LastIndexOf('/')] : string.Empty)
            .ToHashSet(StringComparer.Ordinal);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var record in records)
            if (!SnapshotInputPolicy.IsExcluded(record.Path, projectDirectories) &&
                !SnapshotInputPolicy.IsSecret(record.Path) &&
                !SnapshotInputPolicy.IsAdditionalExcluded(record.Path, options))
                result[record.Path] = record.ObjectId;
        return result;
    }

    private static IReadOnlyList<string> SplitNull(byte[] bytes)
    {
        var result = new List<string>();
        var start = 0;
        for (var index = 0; index < bytes.Length; index++)
        {
            if (bytes[index] != 0) continue;
            if (index > start) result.Add(new UTF8Encoding(false, true).GetString(bytes, start, index - start));
            start = index + 1;
        }
        if (start != bytes.Length) throw new SnapshotCaptureException("Git returned a non-NUL-terminated inventory.");
        return result;
    }

    private static Task<byte[]> ReadBaseBlobAsync(InputSnapshot snapshot, string objectId,
        CancellationToken cancellationToken) => SnapshotCapture.RunGitBytesAsync(snapshot.OriginalRoot,
        ["cat-file", "blob", objectId], snapshot.Options, cancellationToken);

    private static async Task<string> HashCapturedBlobAsync(InputSnapshot snapshot, string path, byte[] bytes,
        CancellationToken cancellationToken)
    {
        var output = await SnapshotCapture.RunGitBytesAsync(snapshot.OriginalRoot,
            ["hash-object", $"--path={path}", "--stdin"], snapshot.Options, cancellationToken, bytes);
        var objectId = Encoding.ASCII.GetString(output).Trim();
        if (objectId.Length is not (40 or 64) || objectId.Any(character => !Uri.IsHexDigit(character)))
            throw new SnapshotCaptureException("Git returned an invalid normalized object ID.");
        return objectId.ToLowerInvariant();
    }

    private static bool IsCSharp(string path) => path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private static string GitBlobHash(byte[] bytes, int objectIdLength)
    {
        using var hash = objectIdLength switch
        {
            40 => IncrementalHash.CreateHash(HashAlgorithmName.SHA1),
            64 => IncrementalHash.CreateHash(HashAlgorithmName.SHA256),
            _ => throw new SnapshotCaptureException("Git returned an unsupported object ID format.")
        };
        hash.AppendData(Encoding.ASCII.GetBytes($"blob {bytes.Length}\0"));
        hash.AppendData(bytes);
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }
}
