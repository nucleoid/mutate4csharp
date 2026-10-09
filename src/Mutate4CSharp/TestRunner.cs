using System.Globalization;
using System.Xml.Linq;

namespace Mutate4CSharp;

internal sealed record TrxEvidence(bool Valid, bool TestsExecuted, bool HasFailedTests,
    IReadOnlyList<string> Paths, bool HasRunErrors, IReadOnlyList<string> FailedTestIds,
    IReadOnlyList<string> Diagnostics);

internal static class TestRunner
{
    public static async Task<TestRunResult> RunAsync(string root, string testPath, string resultsDirectory,
        TimeSpan timeout, bool collectCoverage, CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string?>? environment = null, bool noRestore = false,
        bool requireExecutionBoundary = false, string? dotnetExecutable = null,
        string? framework = null, string? configuration = null)
    {
        if (requireExecutionBoundary) ExecutionEnvironment.ValidateExecutionAncestors(root);
        Directory.CreateDirectory(resultsDirectory);
        var dotnet = dotnetExecutable ?? Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
        var processEnvironment = environment is null
            ? new Dictionary<string, string?>()
            : new Dictionary<string, string?>(environment);
        var arguments = BuildArguments(testPath, resultsDirectory, collectCoverage, noRestore,
            requireExecutionBoundary, framework, configuration).ToList();
        if (requireExecutionBoundary)
        {
            ExecutionEnvironment.SanitizeStrictChildEnvironment(processEnvironment);
            var packageRoot = processEnvironment.LastOrDefault(item =>
                item.Key.Equals("NUGET_PACKAGES", StringComparison.OrdinalIgnoreCase)).Value;
            if (string.IsNullOrWhiteSpace(packageRoot))
                throw new SnapshotCaptureException("Strict test execution requires a private package root.");
            processEnvironment["MSBUILDDISABLENODEREUSE"] = "1";
            processEnvironment["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0";
            processEnvironment["MSBuildUserExtensionsPath"] = Path.Combine(
                Directory.GetParent(Path.GetFullPath(root))!.FullName, "msbuild-user-extensions");
            processEnvironment["MSBuildExtensionsPath"] = null;
            var separator = arguments.IndexOf("--");
            var insertion = separator < 0 ? arguments.Count : separator;
            arguments.Insert(insertion++, "-nodeReuse:false");
            arguments.Insert(insertion++, "-p:UseSharedCompilation=false");
            arguments.Insert(insertion, $"-p:NuGetPackageRoot={Path.GetFullPath(packageRoot).TrimEnd(
                Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)}{Path.DirectorySeparatorChar}");
        }
        var run = await ProcessTree.RunAsync(dotnet, arguments, root, timeout, cancellationToken,
            processEnvironment, requireLinuxSessionIsolation: requireExecutionBoundary);
        var evidence = AnalyzeTrx(resultsDirectory);
        if (run.TimedOut)
        {
            return new(-1, run.Duration, true, evidence.TestsExecuted, evidence.HasFailedTests, evidence.Valid,
                run.StandardOutput, run.StandardError, evidence.Paths, evidence.HasRunErrors, evidence.FailedTestIds,
                evidence.Diagnostics);
        }
        return new(run.ExitCode, run.Duration, false, evidence.TestsExecuted, evidence.HasFailedTests, evidence.Valid,
            run.StandardOutput, run.StandardError, evidence.Paths, evidence.HasRunErrors, evidence.FailedTestIds,
            evidence.Diagnostics);
    }

    private static IReadOnlyList<string> BuildArguments(string testPath, string resultsDirectory,
        bool collectCoverage, bool noRestore, bool requireExecutionBoundary, string? framework,
        string? configuration)
    {
        if ((framework is null) != (configuration is null))
            throw new ArgumentException("Framework and configuration must be supplied together.");
        var arguments = new List<string> { "test", testPath };
        if (framework is not null)
        {
            arguments.Add("-f"); arguments.Add(framework);
            arguments.Add("-c"); arguments.Add(configuration!);
        }
        arguments.Add("-m:1");
        arguments.Add("--logger"); arguments.Add("trx");
        if (noRestore) arguments.Add("--no-restore");
        arguments.Add("--results-directory"); arguments.Add(resultsDirectory);
        if (collectCoverage)
        {
            arguments.Add("--collect:XPlat Code Coverage");
            arguments.Add("--");
            arguments.Add("DataCollectionRunSettings.DataCollectors.DataCollector.Configuration.Format=opencover");
        }
        _ = requireExecutionBoundary;
        return arguments;
    }

    internal static TrxEvidence AnalyzeTrx(string directory)
    {
        var paths = Directory.Exists(directory)
            ? ProjectLocator.EnumerateFilesSafe(directory, "*.trx").OrderBy(x => x, StringComparer.Ordinal).ToArray()
            : [];
        if (paths.Length == 0) return new(false, false, false, paths, false, [], []);

        var anyExecuted = false;
        var anyFailed = false;
        var anyRunErrors = false;
        var failedTestIds = new List<string>();
        var diagnostics = new List<string>();
        foreach (var path in paths)
        {
            try
            {
                var doc = XDocument.Load(path);
                if (doc.Root?.Name.LocalName != "TestRun") return Invalid();
                var counters = doc.Descendants().FirstOrDefault(x => x.Name.LocalName == "Counters");
                if (counters is null || !TryCounter(counters, "total", out var total) ||
                    !TryCounter(counters, "executed", out var executed) ||
                    !TryCounter(counters, "passed", out var passed) ||
                    !TryCounter(counters, "failed", out var failed))
                    return Invalid();

                var results = doc.Descendants().Where(x => x.Name.LocalName == "UnitTestResult").ToArray();
                var actualPassed = results.Count(x => IsOutcome(x, "Passed"));
                var actualFailed = results.Count(x => IsOutcome(x, "Failed"));
                var actualExecuted = actualPassed + actualFailed;
                if (total < executed || executed != passed + failed || executed != actualExecuted ||
                    passed != actualPassed || failed != actualFailed)
                    return Invalid();

                var summary = doc.Descendants().FirstOrDefault(x => x.Name.LocalName == "ResultSummary");
                var summaryFailed = string.Equals(summary?.Attribute("outcome")?.Value, "Failed", StringComparison.OrdinalIgnoreCase);
                var hasRunError = summary?.Descendants().Any(x => x.Name.LocalName == "ErrorInfo") == true;
                anyRunErrors |= hasRunError;
                foreach (var failedResult in results.Where(x => IsOutcome(x, "Failed")))
                {
                    var id = Bound(failedResult.Attribute("testName")?.Value ?? "<unknown-test>", 256);
                    if (!failedTestIds.Contains(id, StringComparer.Ordinal) && failedTestIds.Count < 50)
                        failedTestIds.Add(id);
                }
                foreach (var message in summary?.Descendants().Where(x => x.Name.LocalName is "Message" or "StackTrace") ?? [])
                {
                    var diagnostic = Bound(string.Join(' ', message.Value.Split((char[]?)null,
                        StringSplitOptions.RemoveEmptyEntries)), 512);
                    if (diagnostic.Length > 0 && diagnostics.Count < 20) diagnostics.Add(diagnostic);
                }
                if ((summaryFailed || hasRunError) && actualFailed == 0)
                    return Invalid();

                anyExecuted |= actualExecuted > 0;
                anyFailed |= actualFailed > 0;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
            {
                return Invalid();
            }
        }
        return new(true, anyExecuted, anyFailed, paths, anyRunErrors, failedTestIds, diagnostics);

        TrxEvidence Invalid() => new(false, anyExecuted, anyFailed, paths, anyRunErrors,
            failedTestIds, diagnostics);
    }

    private static bool TryCounter(XElement counters, string name, out int value) =>
        int.TryParse(counters.Attribute(name)?.Value, NumberStyles.None, CultureInfo.InvariantCulture, out value) && value >= 0;

    private static bool IsOutcome(XElement result, string expected) =>
        string.Equals(result.Attribute("outcome")?.Value, expected, StringComparison.OrdinalIgnoreCase);

    private static string Bound(string value, int maximum) => value.Length <= maximum ? value : value[..maximum];

    public static IReadOnlyList<string> FindCoverage(string directory) => Directory.Exists(directory)
        ? ProjectLocator.EnumerateFilesSafe(directory, "*.xml")
            .Where(x => Path.GetFileName(x).Contains("coverage", StringComparison.OrdinalIgnoreCase))
            .Where(IsNonemptyOpenCover)
            .OrderBy(x => x, StringComparer.Ordinal).ToArray()
        : [];

    private static bool IsNonemptyOpenCover(string path)
    {
        try
        {
            var document = XDocument.Load(path);
            return document.Root?.Name.LocalName == "CoverageSession" &&
                document.Root.Descendants().Any(item => item.Name.LocalName == "Module");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            return false;
        }
    }
}

internal enum CoverageState { Unknown, Covered, Uncovered }

internal sealed class CoverageMap
{
    private readonly Dictionary<string, Dictionary<int, bool>> _points;
    private readonly Dictionary<string, List<CoveragePoint>> _spans;
    private readonly string _root;
    private readonly bool _incomplete;
    private readonly bool _spanIncomplete;

    private CoverageMap(Dictionary<string, Dictionary<int, bool>> points,
        Dictionary<string, List<CoveragePoint>> spans, string root, bool incomplete,
        bool spanIncomplete)
    {
        _points = points;
        _spans = spans;
        _root = root;
        _incomplete = incomplete;
        _spanIncomplete = spanIncomplete;
    }

    public static CoverageMap? Load(string? report, string root) =>
        report is null ? null : Load([report], root);

    public static CoverageMap? Load(IEnumerable<string> reports, string root)
        => Load(reports, root, root, null);

    public static CoverageMap? Load(IEnumerable<string> reports, SnapshotClone clone)
        => Load(reports, clone.Root, clone.ToCanonicalPath(clone.Root), clone.ToCanonicalPath);

    private static CoverageMap? Load(IEnumerable<string> reports, string parseRoot, string canonicalRoot,
        Func<string, string>? canonicalize)
    {
        var paths = reports.Where(File.Exists).Distinct(StringComparer.Ordinal).ToArray();
        if (paths.Length == 0) return null;
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var points = new Dictionary<string, Dictionary<int, bool>>(comparer);
        var spans = new Dictionary<string, List<CoveragePoint>>(comparer);
        var incomplete = false;
        var spanIncomplete = false;

        foreach (var report in paths)
        {
            try
            {
                var doc = XDocument.Load(report);
                if (doc.Root?.Name.LocalName != "CoverageSession") { incomplete = true; continue; }
                var modules = doc.Root.Descendants().Where(x => x.Name.LocalName == "Module").ToArray();
                if (modules.Length == 0) { incomplete = true; continue; }
                foreach (var module in modules)
                {
                    var files = new Dictionary<string, string>(StringComparer.Ordinal);
                    foreach (var fileElement in module.Descendants().Where(x => x.Name.LocalName == "File"))
                    {
                        var uid = fileElement.Attribute("uid")?.Value;
                        var fullPath = fileElement.Attribute("fullPath")?.Value;
                        if (string.IsNullOrWhiteSpace(uid) || string.IsNullOrWhiteSpace(fullPath) || files.ContainsKey(uid))
                        { incomplete = true; continue; }
                        try
                        {
                            var normalized = Normalize(fullPath, parseRoot);
                            files.Add(uid, canonicalize is null ? normalized : canonicalize(normalized));
                        }
                        catch { incomplete = true; }
                    }
                    foreach (var method in module.Descendants().Where(x => x.Name.LocalName == "Method"))
                    {
                        var methodFileId = method.Descendants().FirstOrDefault(x => x.Name.LocalName == "FileRef")?.Attribute("uid")?.Value;
                        foreach (var point in method.Descendants().Where(x => x.Name.LocalName == "SequencePoint"))
                        {
                            var fileId = point.Attribute("fileid")?.Value ?? methodFileId;
                            if (fileId is null || !files.TryGetValue(fileId, out var file) ||
                                !int.TryParse(point.Attribute("vc")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var visits) || visits < 0 ||
                                !int.TryParse(point.Attribute("sl")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var line) || line <= 0)
                            { incomplete = true; continue; }
                            if (line == 0xFEEFEE || string.Equals(point.Attribute("hidden")?.Value, "true", StringComparison.OrdinalIgnoreCase))
                                continue;
                            if (!points.TryGetValue(file, out var lines)) points[file] = lines = [];
                            lines[line] = lines.TryGetValue(line, out var covered) ? covered || visits > 0 : visits > 0;
                            if (!TryPosition(point, "sc", out var startColumn) ||
                                !TryPosition(point, "el", out var endLine) ||
                                !TryPosition(point, "ec", out var endColumn) || endLine < line ||
                                endLine == line && endColumn <= startColumn)
                            {
                                spanIncomplete = true;
                                continue;
                            }
                            if (!spans.TryGetValue(file, out var fileSpans)) spans[file] = fileSpans = [];
                            fileSpans.Add(new(line, startColumn, endLine, endColumn, visits > 0));
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException or UriFormatException)
            {
                incomplete = true;
            }
        }
        return new(points, spans, Path.GetFullPath(canonicalRoot), incomplete, spanIncomplete);

        static bool TryPosition(XElement point, string name, out int value) =>
            int.TryParse(point.Attribute(name)?.Value, NumberStyles.Integer,
                CultureInfo.InvariantCulture, out value) && value > 0;
    }

    public CoverageState GetState(string path, int line)
    {
        if (!_points.TryGetValue(Normalize(path, _root), out var lines) || !lines.TryGetValue(line, out var covered))
            return CoverageState.Unknown;
        if (covered) return CoverageState.Covered;
        return _incomplete ? CoverageState.Unknown : CoverageState.Uncovered;
    }

    public bool IsCovered(string path, int line) => GetState(path, line) == CoverageState.Covered;

    public CoverageState GetState(string path, int startLine, int startColumn,
        int endLine, int endColumn)
    {
        if (startLine <= 0 || startColumn <= 0 || endLine < startLine || endColumn <= 0 ||
            endLine == startLine && endColumn <= startColumn)
            return CoverageState.Unknown;
        if (!_spans.TryGetValue(Normalize(path, _root), out var points))
            return CoverageState.Unknown;
        var start = new SourcePosition(startLine, startColumn);
        var end = new SourcePosition(endLine, endColumn);
        var overlapping = points.Where(point => point.Start.CompareTo(end) < 0 &&
            start.CompareTo(point.End) < 0).ToArray();
        if (overlapping.Any(point => point.Covered)) return CoverageState.Covered;
        if (_incomplete || _spanIncomplete) return CoverageState.Unknown;
        return overlapping.Any(point => point.Start.CompareTo(start) <= 0 &&
                point.End.CompareTo(end) >= 0)
            ? CoverageState.Uncovered
            : CoverageState.Unknown;
    }

    private static string Normalize(string path, string root)
    {
        var unescaped = Uri.UnescapeDataString(path.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar));
        return Path.GetFullPath(unescaped, root).TrimEnd(Path.DirectorySeparatorChar);
    }

    private readonly record struct SourcePosition(int Line, int Column) : IComparable<SourcePosition>
    {
        public int CompareTo(SourcePosition other)
        {
            var line = Line.CompareTo(other.Line);
            return line != 0 ? line : Column.CompareTo(other.Column);
        }
    }

    private sealed record CoveragePoint(int StartLine, int StartColumn, int EndLine, int EndColumn,
        bool Covered)
    {
        public SourcePosition Start => new(StartLine, StartColumn);
        public SourcePosition End => new(EndLine, EndColumn);
    }
}
