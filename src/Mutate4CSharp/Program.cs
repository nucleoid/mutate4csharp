using System.Runtime.InteropServices;

namespace Mutate4CSharp;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args is ["--version"])
        {
            Console.WriteLine(typeof(Program).Assembly.GetCustomAttributes(
                    typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
                .Cast<System.Reflection.AssemblyInformationalVersionAttribute>()
                .Single().InformationalVersion);
            return 0;
        }
        var (options, error) = Cli.Parse(args);
        if (error is not null) { Console.Error.WriteLine($"Error: {error}"); Console.Error.WriteLine("Use --help for usage."); return (int)ExitCode.Usage; }
        if (options!.Help) { Console.WriteLine(Cli.HelpText); return 0; }
        if (options.StrictCheck is not null)
        {
            using var cancel = new CancellationTokenSource();
            var signalCount = 0;
            ConsoleCancelEventHandler handler = (_, eventArgs) =>
            {
                if (Interlocked.Increment(ref signalCount) == 1)
                {
                    eventArgs.Cancel = true;
                    cancel.Cancel();
                }
                else eventArgs.Cancel = false;
            };
            Console.CancelKeyPress += handler;
            using var terminate = OperatingSystem.IsWindows() ? null : PosixSignalRegistration.Create(
                PosixSignal.SIGTERM, context =>
                {
                    if (Interlocked.Increment(ref signalCount) == 1)
                    {
                        context.Cancel = true;
                        cancel.Cancel();
                    }
                    else context.Cancel = false;
                });
            try
            {
                var result = await new EvaluationCoordinator().RunAsync(options.StrictCheck, cancel.Token);
                Console.WriteLine(options.StrictCheck.Plan
                    ? $"Scope plan v{result.Report.ScopePlan.SchemaVersion}: {result.Report.ScopePlan.Files.Count} file(s), " +
                      $"{result.Report.ScopePlan.ProjectUnits.Count} project unit(s), " +
                      $"{result.Report.ScopePlan.Exclusions.Count} exclusion(s). No tests ran. " +
                      $"Run ID: {options.StrictCheck.RunId}. Report: {result.ReportPath}"
                    : $"Strict evaluation is incomplete; immutable snapshot {result.SnapshotId ?? "refused"}. Run ID: {options.StrictCheck.RunId}. Report: {result.ReportPath}");
                return result.Report.ExitCode;
            }
            catch (ArgumentException ex)
            {
                Console.Error.WriteLine($"Run ID: {options.StrictCheck.RunId}");
                Console.Error.WriteLine($"Error: {ex.Message}");
                return (int)ExitCode.Usage;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or EvaluationContractException)
            {
                Console.Error.WriteLine($"Run ID: {options.StrictCheck.RunId}");
                Console.Error.WriteLine($"Strict report failure: {ex.Message}");
                return (int)ExitCode.Infrastructure;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Run ID: {options.StrictCheck.RunId}");
                Console.Error.WriteLine($"Strict evaluation failure: {ex.Message}");
                return (int)ExitCode.Infrastructure;
            }
            finally { Console.CancelKeyPress -= handler; }
        }
        try { return (int)await Run(options); }
        catch (ArgumentException ex) { Console.Error.WriteLine($"Error: {ex.Message}"); return (int)ExitCode.Usage; }
        catch (Exception ex) { Console.Error.WriteLine($"Infrastructure error: {ex.Message}"); return (int)ExitCode.Infrastructure; }
    }

    private static async Task<ExitCode> Run(Options options)
    {
        var target = Path.GetFullPath(options.Target!);
        if (!File.Exists(target) || !target.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Target must be one existing .cs file.");
        if (target.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(x => x.Equals("test", StringComparison.OrdinalIgnoreCase) || x.Equals("tests", StringComparison.OrdinalIgnoreCase)) ||
            Path.GetFileNameWithoutExtension(target).EndsWith("Tests", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Test source files cannot be mutation targets.");

        // Scan is deliberately project-independent: no discovery commands and no writes.
        if (options.Scan) return Scan(target);

        var context = ProjectLocator.Resolve(options, requireTestProject: !options.UpdateManifest);
        var analysis = SourceAnalyzer.Analyze(target, Path.GetDirectoryName(context.Project));
        var fingerprint = ProjectLocator.ContextFingerprint(context, options);
        var manifest = ManifestStore.Read(analysis.OriginalSource, out var malformed);

        if (options.UpdateManifest)
        {
            ManifestStore.AtomicWriteExpected(target, analysis.OriginalSource, ManifestStore.Compose(analysis, fingerprint));
            Console.WriteLine($"Updated mutation manifest for {context.DisplayPath}.");
            return ExitCode.Success;
        }

        using var cancel = new CancellationTokenSource();
        ConsoleCancelEventHandler handler = (_, e) => { e.Cancel = true; cancel.Cancel(); };
        Console.CancelKeyPress += handler;
        try
        {
            var baselineDirectory = Path.Combine(Path.GetTempPath(), "mutate4csharp", $"baseline-{Guid.NewGuid():N}");
            TestRunResult baseline;
            IReadOnlyList<string> coverageReports = options.CoverageReport is null
                ? (options.ReuseCoverage ? FindReusableCoverage(context.Root) : [])
                : [Path.GetFullPath(options.CoverageReport)];
            try
            {
                baseline = await TestRunner.RunAsync(context.Root, context.TestProject, baselineDirectory,
                    TimeSpan.FromMinutes(10), collectCoverage: !options.ReuseCoverage, cancel.Token);
                if (baseline.TimedOut || baseline.ExitCode != 0 || !baseline.TrxValid || !baseline.TestsDiscovered || baseline.HasFailedTests)
                {
                    Console.Error.WriteLine(!baseline.TestsDiscovered
                        ? "Baseline failed: no tests were discovered."
                        : $"Baseline tests failed (exit {baseline.ExitCode}).");
                    return ExitCode.BaselineFailed;
                }
                Console.WriteLine($"Baseline tests passed in {(long)baseline.Duration.TotalMilliseconds} ms.");
                if (!options.ReuseCoverage) coverageReports = TestRunner.FindCoverage(baselineDirectory);
                else Console.WriteLine("WARNING: Reusing coverage; classifications may be stale.");
                var coverage = CoverageMap.Load(coverageReports, context.Root);
                if (coverage is null) Console.WriteLine("WARNING: Coverage is unavailable or invalid; executing sites with unknown coverage.");

                var contextChanged = manifest is not null && !string.Equals(manifest.ContextFingerprint, fingerprint, StringComparison.Ordinal);
                var oldScopes = manifest?.Scopes.ToDictionary(x => x.Id, StringComparer.Ordinal) ?? new();
                var scopeHashChanged = manifest is not null && (analysis.Scopes.Any(x => !oldScopes.TryGetValue(x.Id, out var old) || old.Hash != x.Hash) ||
                    manifest.Scopes.Any(x => analysis.Scopes.All(current => current.Id != x.Id)));
                var sourceHashChangedOutsideScopes = manifest is not null && manifest.SourceHash != SourceAnalyzer.Hash(analysis.SourceWithoutManifest) && !scopeHashChanged;
                bool Changed(MutationSite site) => malformed || manifest is null || contextChanged || sourceHashChangedOutsideScopes ||
                    !oldScopes.TryGetValue(site.ScopeId, out var old) || !string.Equals(old.Hash, site.ScopeHash, StringComparison.Ordinal);
                var allChanged = analysis.Sites.Where(Changed).ToList();
                IEnumerable<MutationSite> selected = analysis.Sites;
                if (options.Lines is not null) selected = selected.Where(x => options.Lines.Contains(x.Line));
                else if (options.SinceLastRun) selected = selected.Where(Changed);
                else if (!options.MutateAll && manifest is not null && !malformed) selected = selected.Where(Changed);
                var selectedList = selected.ToList();
                var uncovered = coverage is null ? [] : selectedList.Where(x => coverage.GetState(target, x.Line) == CoverageState.Uncovered).ToList();
                var unknownCoverage = coverage is null ? selectedList : selectedList.Where(x => coverage.GetState(target, x.Line) == CoverageState.Unknown).ToList();
                var covered = selectedList.Except(uncovered).ToList();
                if (unknownCoverage.Count > 0) Console.WriteLine($"WARNING: Coverage is unknown for {unknownCoverage.Count} selected site(s); executing them.");

                Console.WriteLine($"Total mutation sites: {analysis.Sites.Count}");
                Console.WriteLine($"Covered mutation sites: {covered.Count}");
                Console.WriteLine($"Uncovered mutation sites: {uncovered.Count}");
                Console.WriteLine($"Changed mutation sites: {allChanged.Count}");
                Console.WriteLine($"Manifest exists: {(manifest is not null).ToString().ToLowerInvariant()}");
                Console.WriteLine($"Context/source changed: {(contextChanged || allChanged.Count > 0 || malformed).ToString().ToLowerInvariant()}");
                Console.WriteLine($"Differential surface area: {selectedList.Count(x => !oldScopes.ContainsKey(x.ScopeId))}");
                Console.WriteLine($"Manifest-violating surface area: {selectedList.Count(x => oldScopes.TryGetValue(x.ScopeId, out var old) && old.Hash != x.ScopeHash)}");
                if (malformed) Console.WriteLine("WARNING: Embedded manifest is malformed; all scopes will rerun.");
                if (covered.Count > options.MutationWarning) Console.WriteLine($"WARNING: Found {covered.Count} mutations. Consider splitting this file.");
                foreach (var site in uncovered) Print(new(site, MutantStatus.Uncovered, TimeSpan.Zero), context.DisplayPath);

                var timeoutMs = Math.Max(1000, baseline.Duration.TotalMilliseconds * options.TimeoutFactor);
                var results = await MutationExecutor.ExecuteAsync(context, analysis.SourceWithoutManifest, covered,
                    TimeSpan.FromMilliseconds(timeoutMs), options.MaxWorkers, options.Verbose, cancel.Token);
                foreach (var result in results.OrderBy(x => x.Site.Index)) Print(result, context.DisplayPath);

                var killed = results.Count(x => x.Status == MutantStatus.Killed);
                var survived = results.Count(x => x.Status == MutantStatus.Survived);
                var bad = results.Count(x => x.Status is MutantStatus.Timeout or MutantStatus.CompileError or MutantStatus.Error);
                var executedSiteIndexes = results.Select(x => x.Site.Index).ToHashSet();
                var changedSitesLeftUnrun = allChanged.Any(x => !executedSiteIndexes.Contains(x.Index));
                Console.WriteLine($"Coverage: {uncovered.Count} uncovered sites skipped.");
                Console.WriteLine($"Summary: {killed} killed, {survived} survived, {results.Count} total.");
                if (changedSitesLeftUnrun)
                    Console.WriteLine("Manifest not advanced: one or more changed mutation sites were not executed.");

                // Line-filtered runs are intentionally non-advancing: even when today's sites all
                // happen to share selected lines, the selection is an explicitly partial contract.
                var explicitPartial = options.Lines is not null;
                if (!cancel.IsCancellationRequested && survived == 0 && bad == 0 && !explicitPartial && !changedSitesLeftUnrun)
                {
                    var finalFingerprint = ProjectLocator.ContextFingerprint(context, options);
                    if (!string.Equals(finalFingerprint, fingerprint, StringComparison.Ordinal))
                        throw new IOException("Project or test context changed during the run; manifest was not written.");
                    ManifestStore.AtomicWriteExpected(target, analysis.OriginalSource, ManifestStore.Compose(analysis, fingerprint));
                }
                if (survived > 0) return ExitCode.Survived;
                if (bad > 0 || cancel.IsCancellationRequested) return ExitCode.Infrastructure;
                return ExitCode.Success;
            }
            finally
            {
                try { if (Directory.Exists(baselineDirectory)) Directory.Delete(baselineDirectory, true); } catch { }
            }
        }
        finally { Console.CancelKeyPress -= handler; }
    }

    private static ExitCode Scan(string target)
    {
        var analysis = SourceAnalyzer.Analyze(target);
        var manifest = ManifestStore.Read(analysis.OriginalSource, out var malformed);
        var old = manifest?.Scopes.ToDictionary(x => x.Id, StringComparer.Ordinal) ?? new();
        bool Changed(MutationSite x) => malformed || manifest is null || !old.TryGetValue(x.ScopeId, out var scope) || scope.Hash != x.ScopeHash;
        Console.WriteLine($"Scan: {analysis.Sites.Count} mutation sites in {target}");
        foreach (var site in analysis.Sites) Console.WriteLine($"{(manifest is not null && Changed(site) ? "*" : " ")} {target}:{site.Line} {site.Description}");
        if (manifest is not null) Console.WriteLine("* indicates a scope that differs from the embedded manifest.");
        return ExitCode.Success;
    }

    private static IReadOnlyList<string> FindReusableCoverage(string root) => ProjectLocator.EnumerateFilesSafe(root, "*.xml")
        .Where(x => Path.GetFileName(x).Contains("coverage", StringComparison.OrdinalIgnoreCase))
        .OrderBy(x => x, StringComparer.Ordinal).ToArray();

    private static void Print(MutantResult result, string display)
    {
        var status = result.Status switch
        {
            MutantStatus.CompileError => "COMPILE_ERROR", _ => result.Status.ToString().ToUpperInvariant()
        };
        var duration = result.Status == MutantStatus.Uncovered ? string.Empty : $" ({(long)result.Duration.TotalMilliseconds} ms)";
        var detail = string.IsNullOrWhiteSpace(result.Detail) ? string.Empty : $" [{result.Detail}]";
        Console.WriteLine($"{status} {display}:{result.Site.Line} {result.Site.Description}{duration}{detail}");
    }
}
