using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace Mutate4CSharp;

internal sealed record CompileEvidence(bool IsCompileInvalid, IReadOnlyList<string> Diagnostics,
    IReadOnlyList<string> ClassificationDiagnostics);

internal static partial class CompilerEvidence
{
    public const int MaxDiagnostics = 20;
    public const int MaxDiagnosticLength = 512;

    public static CompileEvidence Evaluate(TestRunResult mutated, TestRunResult control, string targetPath,
        string workingDirectory)
        => Evaluate(mutated, control, targetPath, workingDirectory, null);

    internal static CompileEvidence Evaluate(TestRunResult mutated, TestRunResult control, string targetPath,
        string workingDirectory, Func<string, string>? expandExistingWindowsPath)
    {
        var controlHealthy = !control.TimedOut && control.ExitCode == 0 && control.TrxValid &&
            control.TestsDiscovered && !control.HasFailedTests && !control.HasRunErrors;
        return Evaluate(mutated, controlHealthy, targetPath, workingDirectory, expandExistingWindowsPath);
    }

    internal static CompileEvidence Evaluate(TestRunResult mutated, bool controlHealthy, string targetPath,
        string workingDirectory) => Evaluate(mutated, controlHealthy, targetPath, workingDirectory, null);

    internal static CompileEvidence Evaluate(TestRunResult mutated, bool controlHealthy, string targetPath,
        string workingDirectory, Func<string, string>? expandExistingWindowsPath)
    {
        if (!controlHealthy) return new(false, [], ["classifier-stage=control-unhealthy"]);
        var mutantFailedToBuild = !mutated.TimedOut && mutated.ExitCode != 0 && !mutated.TrxValid &&
            !mutated.TestsDiscovered && !mutated.HasFailedTests;
        if (!mutantFailedToBuild) return new(false, [], ["classifier-stage=not-failed-build"]);

        var output = mutated.StandardOutput + "\n" + mutated.StandardError;
        var normalizedOutput = AnsiEscapePattern().Replace(output, string.Empty);
        var target = NormalizeFullPath(targetPath, workingDirectory, expandExistingWindowsPath);
        var matches = DiagnosticPattern().Matches(normalizedOutput);
        var candidates = matches
            .Select(match => new
            {
                Path = TryNormalizeFullPath(match.Groups["path"].Value, workingDirectory,
                    expandExistingWindowsPath),
                Match = match
            })
            .ToArray();
        var exact = candidates.Where(item => item.Path is not null &&
            string.Equals(item.Path, target, PathComparison(target))).ToArray();
        var diagnostics = exact
            .Select(item => FormatDiagnostic(item.Match))
            .Distinct(StringComparer.Ordinal)
            .Take(MaxDiagnostics)
            .ToArray();
        var targetName = FileName(target);
        var classification = new[]
        {
            "classifier-stage=failed-build",
            $"classifier-compiler-lines={CompilerLinePattern().Matches(normalizedOutput).Count}",
            $"classifier-location-matches={matches.Count}",
            $"classifier-normalized-paths={candidates.Count(item => item.Path is not null)}",
            $"classifier-target-name-matches={candidates.Count(item => item.Path is not null && string.Equals(FileName(item.Path), targetName, PathComparison(target)))}",
            $"classifier-exact-path-matches={exact.Length}",
            $"classifier-node-prefix={NodePrefixPattern().IsMatch(normalizedOutput).ToString().ToLowerInvariant()}",
            $"classifier-space-before-colon={SpaceBeforeColonPattern().IsMatch(normalizedOutput).ToString().ToLowerInvariant()}",
            $"classifier-ansi={(!string.Equals(output, normalizedOutput, StringComparison.Ordinal)).ToString().ToLowerInvariant()}",
            $"classifier-decoding-replacement={normalizedOutput.Contains('\uFFFD').ToString().ToLowerInvariant()}"
        };
        return new(diagnostics.Length > 0, diagnostics, classification);
    }

    private static string? TryNormalizeFullPath(string value, string workingDirectory,
        Func<string, string>? expandExistingWindowsPath)
    {
        try { return NormalizeFullPath(value, workingDirectory, expandExistingWindowsPath); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        { return null; }
    }

    private static string FileName(string path)
    {
        var separator = path.LastIndexOf('/');
        return separator < 0 ? path : path[(separator + 1)..];
    }

    private static string FormatDiagnostic(Match match)
    {
        var location = $"line={match.Groups["line"].Value},column={match.Groups["column"].Value}";
        if (match.Groups["endLine"].Success)
            location += $",end-line={match.Groups["endLine"].Value},end-column={match.Groups["endColumn"].Value}";
        return EvaluationTextBounds.Prefix(
            $"{match.Groups["code"].Value.ToUpperInvariant()} at target source ({location}).",
            MaxDiagnosticLength);
    }

    private static string NormalizeFullPath(string value, string workingDirectory,
        Func<string, string>? expandExistingWindowsPath)
    {
        var normalized = value.Trim().Replace('\\', '/');
        if (!WindowsPathPattern().IsMatch(normalized))
        {
            normalized = Path.GetFullPath(normalized, workingDirectory).Replace('\\', '/').TrimEnd('/');
            if (!WindowsPathPattern().IsMatch(normalized)) return normalized;
        }
        normalized = char.ToUpperInvariant(normalized[0]) + normalized[1..].TrimEnd('/');
        var expanded = expandExistingWindowsPath is not null
            ? expandExistingWindowsPath(normalized)
            : ExpandExistingWindowsPath(normalized);
        expanded = expanded.Trim().Replace('\\', '/').TrimEnd('/');
        return WindowsPathPattern().IsMatch(expanded)
            ? char.ToUpperInvariant(expanded[0]) + expanded[1..]
            : normalized;
    }

    private static string ExpandExistingWindowsPath(string path)
    {
        if (!OperatingSystem.IsWindows()) return path;
        var nativePath = path.Replace('/', '\\');
        var capacity = 512;
        while (capacity <= 32_768)
        {
            var buffer = new StringBuilder(capacity);
            var length = GetLongPathName(nativePath, buffer, (uint)buffer.Capacity);
            if (length == 0) return path;
            if (length < buffer.Capacity) return buffer.ToString();
            if (length >= 32_768) return path;
            capacity = checked((int)length + 1);
        }
        return path;
    }

    private static StringComparison PathComparison(string path) =>
        WindowsPathPattern().IsMatch(path) || OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
    [GeneratedRegex(@"(?m)^[ \t]*(?:\d+>[ \t]*)?(?<path>(?:[A-Za-z]:)?[^\r\n(]+)\((?<line>\d+),(?<column>\d+)(?:,(?<endLine>\d+),(?<endColumn>\d+))?\)[ \t]*:[ \t]*error[ \t]+(?<code>CS\d{4})[ \t]*:[^\r\n]*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DiagnosticPattern();

    [GeneratedRegex(@"(?im)error[ \t]+CS\d{4}[ \t]*:", RegexOptions.CultureInvariant)]
    private static partial Regex CompilerLinePattern();

    [GeneratedRegex(@"(?m)^[ \t]*\d+>[ \t]*(?:[A-Za-z]:)?[^\r\n(]+\(\d+,\d+", RegexOptions.CultureInvariant)]
    private static partial Regex NodePrefixPattern();

    [GeneratedRegex(@"\)[ \t]+:", RegexOptions.CultureInvariant)]
    private static partial Regex SpaceBeforeColonPattern();

    [GeneratedRegex("\\x1B(?:\\[[0-?]*[ -/]*[@-~]|\\][^\\x07\\x1B\\r\\n]{0,256}(?:\\x07|\\x1B\\\\))",
        RegexOptions.CultureInvariant, 1000)]
    private static partial Regex AnsiEscapePattern();

    [GeneratedRegex(@"^[A-Za-z]:/", RegexOptions.CultureInvariant)]
    private static partial Regex WindowsPathPattern();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetLongPathName(string shortPath, StringBuilder longPath,
        uint bufferLength);
}

internal static class StrictTestEvidence
{
    public static bool IsCleanKill(TrxEvidence evidence) =>
        evidence.Valid && evidence.TestsExecuted && evidence.HasFailedTests && !evidence.HasRunErrors;
}
