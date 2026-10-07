using System.Text.RegularExpressions;

namespace Mutate4CSharp;

internal sealed record CompileEvidence(bool IsCompileInvalid, IReadOnlyList<string> Diagnostics);

internal static partial class CompilerEvidence
{
    public const int MaxDiagnostics = 20;
    public const int MaxDiagnosticLength = 512;

    public static CompileEvidence Evaluate(TestRunResult mutated, TestRunResult control, string targetPath,
        string workingDirectory)
    {
        var controlHealthy = !control.TimedOut && control.ExitCode == 0 && control.TrxValid &&
            control.TestsDiscovered && !control.HasFailedTests && !control.HasRunErrors;
        if (!controlHealthy) return new(false, []);
        var mutantFailedToBuild = !mutated.TimedOut && mutated.ExitCode != 0 && !mutated.TrxValid &&
            !mutated.TestsDiscovered && !mutated.HasFailedTests;
        if (!mutantFailedToBuild) return new(false, []);

        var target = NormalizeFullPath(targetPath, workingDirectory);
        var diagnostics = DiagnosticPattern().Matches(mutated.StandardOutput + "\n" + mutated.StandardError)
            .Select(match => new { Path = NormalizeFullPath(match.Groups["path"].Value, workingDirectory), Text = Sanitize(match.Value) })
            .Where(item => string.Equals(item.Path, target, PathComparison(target)))
            .Select(item => item.Text.Length <= MaxDiagnosticLength ? item.Text : item.Text[..MaxDiagnosticLength])
            .Distinct(StringComparer.Ordinal)
            .Take(MaxDiagnostics)
            .ToArray();
        return new(diagnostics.Length > 0, diagnostics);
    }

    private static string NormalizeFullPath(string value, string workingDirectory)
    {
        var normalized = value.Trim().Replace('\\', '/');
        if (WindowsPathPattern().IsMatch(normalized))
            return char.ToUpperInvariant(normalized[0]) + normalized[1..].TrimEnd('/');
        return Path.GetFullPath(normalized, workingDirectory).Replace('\\', '/').TrimEnd('/');
    }

    private static StringComparison PathComparison(string path) =>
        WindowsPathPattern().IsMatch(path) || OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
    private static string Sanitize(string value) => string.Join(' ', value.Split((char[]?)null,
        StringSplitOptions.RemoveEmptyEntries));

    [GeneratedRegex(@"(?m)^(?<path>(?:[A-Za-z]:)?[^\r\n(]+)\(\d+,\d+\):\s*error\s+CS\d{4}\s*:[^\r\n]*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DiagnosticPattern();

    [GeneratedRegex(@"^[A-Za-z]:/", RegexOptions.CultureInvariant)]
    private static partial Regex WindowsPathPattern();
}

internal static class StrictTestEvidence
{
    public static bool IsCleanKill(TrxEvidence evidence) =>
        evidence.Valid && evidence.TestsExecuted && evidence.HasFailedTests && !evidence.HasRunErrors;
}
