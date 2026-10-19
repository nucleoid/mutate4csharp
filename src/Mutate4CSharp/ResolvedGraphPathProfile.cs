using System.Collections.Immutable;
using Microsoft.CodeAnalysis;

namespace Mutate4CSharp;

// Explicit versioned producer options, observed on the real Csc argv. This is
// not a heuristic replacement of arbitrary external paths or binary hashes.
internal static class ResolvedGraphPathProfile
{
    internal const string Source = "/_/mutate-source";
    internal const string Packages = "/_/mutate-packages";
    internal static string Argument(string source, string packages)
    {
        foreach (var path in new[] { source, packages })
            if (!Path.IsPathFullyQualified(path) || path.IndexOfAny(['=', ',', ';', '\r', '\n']) >= 0)
                throw new EvaluationContractException("Finite unambiguous owned path-map roots required.");
        // MSBuild consumes %2C as a literal property comma before the Csc task;
        // the actual task argv is independently parsed and joined below.
        return "-p:PathMap=" + source + "=" + Source + "%2C" + packages + "=" + Packages;
    }
    internal static IReadOnlyDictionary<string, string> Require(ImmutableArray<KeyValuePair<string, string>> map,
        string source, string packages)
    {
        if (map.Length != 2 || !map.Any(p => Same(p.Key, source) && p.Value == Source) ||
            !map.Any(p => Same(p.Key, packages) && p.Value == Packages))
            throw new EvaluationContractException("Actual compiler path mapping differs from the explicit owned-root profile.");
        return map.ToDictionary(p => Path.GetFullPath(p.Key).TrimEnd(Path.DirectorySeparatorChar), p => p.Value, PathComparer);
    }
    internal static string Map(string path, IReadOnlyDictionary<string, string> map)
    {
        path = Path.GetFullPath(path);
        foreach (var row in map.OrderByDescending(p => p.Key.Length))
        {
            var relative = Path.GetRelativePath(row.Key, path);
            if (!Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                return row.Value + "/" + relative.Replace('\\', '/');
        }
        return path;
    }
    internal static string UnmapSource(string path, string sourceRoot)
    {
        if (!path.StartsWith(Source + "/", StringComparison.Ordinal)) return path;
        var relative = SnapshotInputPolicy.NormalizeRelative(path[(Source.Length + 1)..]);
        return Path.GetFullPath(relative, sourceRoot);
    }
    internal static string UnmapPackages(string path, string packageRoot)
    {
        if (!path.StartsWith(Packages + "/", StringComparison.Ordinal)) return path;
        var relative = SnapshotInputPolicy.NormalizeRelative(path[(Packages.Length + 1)..]);
        return Path.GetFullPath(relative, packageRoot);
    }
    private static bool Same(string a, string b) => PathComparer.Equals(Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar), Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar));
    private static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}

// Preserve actual /pathmap CallerFilePath semantics without opening #load or
// arbitrary source resolver files during replay. Source membership is supplied
// only by the complete original task/source closure.
internal sealed class ResolvedGraphSourceResolver(IReadOnlyDictionary<string, string> map, string directory) : SourceReferenceResolver
{
    public override string? NormalizePath(string path, string? baseFilePath) =>
        ResolvedGraphPathProfile.Map(Path.GetFullPath(path, baseFilePath is null ? directory : Path.GetDirectoryName(Path.GetFullPath(baseFilePath, directory))!), map);
    public override string? ResolveReference(string path, string? baseFilePath) => null;
    public override Stream OpenRead(string resolvedPath) => throw new EvaluationContractException("Unenrolled source resolver IO refused.");
    public override bool Equals(object? other) => ReferenceEquals(this, other);
    public override int GetHashCode() => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this);
}
