namespace Mutate4CSharp;

internal enum SnapshotCaptureStage
{
    AfterInventory,
    AfterFileCopied,
    BeforeCaptureValidation,
    BeforeOriginalFileHashed
}

internal sealed record SnapshotCaptureOptions(int MaxFiles, long MaxTotalBytes, long MaxFileBytes,
    int MaxDepth, Action<SnapshotCaptureStage, string?>? Hook = null,
    IReadOnlySet<string>? ExcludedRelativePaths = null, string GitExecutable = "git",
    TimeSpan? GitTimeout = null, long MaxGitOutputBytes = 32L * 1024 * 1024)
{
    public static SnapshotCaptureOptions Default { get; } = new(100_000, 1024L * 1024 * 1024,
        256L * 1024 * 1024, 128);
}

internal static class SnapshotInputPolicy
{
    private static readonly string[] ExcludedDirectories =
        [".git", ".fire-off", "TestResults", ".mutate4csharp", ".vs", ".idea"];
    private static readonly string[] SecretDirectories = [".ssh", ".aws", ".azure", ".gnupg", ".kube"];

    public static bool IsExcluded(string relativePath, IReadOnlySet<string>? projectDirectories = null)
    {
        var parts = Parts(relativePath);
        if (parts.Any(part => ExcludedDirectories.Contains(part, StringComparer.OrdinalIgnoreCase))) return true;
        return IsProjectOutput(relativePath, projectDirectories);
    }

    public static bool IsProjectOutput(string relativePath, IReadOnlySet<string>? projectDirectories)
    {
        var parts = Parts(relativePath);
        if (parts.Any(part => part.Equals("TestResults", StringComparison.OrdinalIgnoreCase))) return true;
        for (var index = 0; index < parts.Length; index++)
        {
            if (!parts[index].Equals("bin", StringComparison.OrdinalIgnoreCase) &&
                !parts[index].Equals("obj", StringComparison.OrdinalIgnoreCase)) continue;
            var parent = string.Join('/', parts.Take(index));
            if (projectDirectories?.Contains(parent) == true) return true;
        }
        return false;
    }

    public static bool IsSecret(string relativePath)
    {
        var parts = Parts(relativePath);
        if (parts.Any(part => SecretDirectories.Contains(part, StringComparer.OrdinalIgnoreCase))) return true;
        var name = parts.LastOrDefault() ?? string.Empty;
        return name.Equals(".env", StringComparison.OrdinalIgnoreCase) ||
               name.StartsWith(".env.", StringComparison.OrdinalIgnoreCase) ||
               name.EndsWith(".pfx", StringComparison.OrdinalIgnoreCase) ||
               name.EndsWith(".p12", StringComparison.OrdinalIgnoreCase) ||
               name.EndsWith(".pem", StringComparison.OrdinalIgnoreCase) ||
               name.EndsWith(".key", StringComparison.OrdinalIgnoreCase) ||
               name.EndsWith(".snk", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("id_rsa", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("id_ed25519", StringComparison.OrdinalIgnoreCase) ||
               name.Equals(".netrc", StringComparison.OrdinalIgnoreCase) ||
               name.Equals(".npmrc", StringComparison.OrdinalIgnoreCase) ||
               name.Equals(".git-credentials", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("secrets.json", StringComparison.OrdinalIgnoreCase) ||
               name.EndsWith(".user", StringComparison.OrdinalIgnoreCase);
    }

    public static string NormalizeRelative(string path)
    {
        var normalized = path.Replace('\\', '/');
        if (string.IsNullOrWhiteSpace(normalized) || normalized.StartsWith("/", StringComparison.Ordinal) ||
            Path.IsPathRooted(normalized) || normalized.Split('/').Any(part => part is "" or "." or ".."))
            throw new SnapshotCaptureException($"Unsafe snapshot path: {path}");
        return normalized;
    }

    public static int Depth(string relativePath) => Parts(relativePath).Length;

    public static bool IsAdditionalExcluded(string relativePath, SnapshotCaptureOptions options) =>
        options.ExcludedRelativePaths?.Contains(relativePath) == true;

    private static string[] Parts(string path) => path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
}
