namespace Mutate4CSharp;

internal static class MutationExecutor
{
    public static async Task<IReadOnlyList<MutantResult>> ExecuteFromSnapshotAsync(InputSnapshot snapshot,
        string targetRelativePath, string testRelativePath, IReadOnlyList<MutationSite> sites,
        TimeSpan timeout, int maxWorkers, FrozenExecutionEnvironment? environment, bool verbose,
        CancellationToken cancellationToken)
    {
        var results = sites.Select(site => new MutantResult(site, MutantStatus.Error, TimeSpan.Zero,
            "not executed")).ToArray();
        if (environment is null)
        {
            for (var index = 0; index < results.Length; index++)
                results[index] = results[index] with { Detail = "frozen dependency environment is required" };
            return results;
        }
        using var gate = new SemaphoreSlim(maxWorkers, maxWorkers);
        var tasks = sites.Select((site, index) => Task.Run(async () =>
        {
            var entered = false;
            try
            {
                await gate.WaitAsync(cancellationToken);
                entered = true;
                await using var worker = await SnapshotWorkspace.CreateCloneAsync(snapshot, $"mutant-{site.Index}", cancellationToken);
                await using var packages = await environment.CreateWorkerPackageCacheAsync(
                    $"mutant-{site.Index}", cancellationToken);
                worker.WriteTextMutation(targetRelativePath, site.Start, site.Length, site.Replacement);
                var test = Path.Combine(worker.Root, testRelativePath.Replace('/', Path.DirectorySeparatorChar));
                var resultDirectory = Path.Combine(worker.OwnedRoot, "results");
                try
                {
                    if (verbose) Console.WriteLine($"Worker start: {site.Index} line {site.Line}");
                    var variables = ExecutionEnvironment.ProcessEnvironment(packages.Root);
                    await environment.RestoreWorkerAsync(worker, testRelativePath, packages, timeout, cancellationToken);
                    var run = await TestRunner.RunAsync(worker.Root, test, resultDirectory, timeout, false,
                        cancellationToken, variables, noRestore: true, requireExecutionBoundary: true);
                    if (!string.Equals(environment.PackageFingerprint,
                            ExecutionEnvironment.FingerprintPackages(packages.Root,
                                cancellationToken: cancellationToken), StringComparison.Ordinal))
                        throw new ExecutionBoundaryIntegrityException(
                            "Worker execution changed its private frozen package-cache copy.");
                    var status = Classify(run);
                    var detail = status == MutantStatus.Error
                        ? Tail(run.StandardError + Environment.NewLine + run.StandardOutput)
                        : null;
                    results[index] = new(site, status, run.Duration, detail);
                    if (verbose) Console.WriteLine($"Worker finish: {site.Index} {status.ToString().ToUpperInvariant()}");
                }
                finally
                {
                    try { if (Directory.Exists(resultDirectory)) Directory.Delete(resultDirectory, true); } catch { }
                }
            }
            catch (OperationCanceledException) { results[index] = new(site, MutantStatus.Error, TimeSpan.Zero, "cancelled"); }
            catch (Exception ex) { results[index] = new(site, MutantStatus.Error, TimeSpan.Zero, ex.Message); }
            finally { if (entered) gate.Release(); }
        }, CancellationToken.None)).ToArray();
        try { await Task.WhenAll(tasks); } catch (OperationCanceledException) { }
        return results;
    }

    public static async Task<IReadOnlyList<MutantResult>> ExecuteAsync(ProjectContext context,
        string source, IReadOnlyList<MutationSite> sites, TimeSpan timeout, int maxWorkers,
        bool verbose, CancellationToken cancellationToken)
    {
        var results = sites.Select(site => new MutantResult(site, MutantStatus.Error, TimeSpan.Zero,
            "not executed")).ToArray();
        using var gate = new SemaphoreSlim(maxWorkers, maxWorkers);
        var tasks = sites.Select((site, index) => Task.Run(async () =>
        {
            var entered = false;
            try
            {
                await gate.WaitAsync(cancellationToken);
                entered = true;
                results[index] = await RunOne(context, source, site, timeout, verbose, cancellationToken);
            }
            catch (OperationCanceledException) { results[index] = new(site, MutantStatus.Error, TimeSpan.Zero, "cancelled"); }
            catch (Exception ex) { results[index] = new(site, MutantStatus.Error, TimeSpan.Zero, ex.Message); }
            finally { if (entered) gate.Release(); }
        }, CancellationToken.None)).ToArray();
        try { await Task.WhenAll(tasks); } catch (OperationCanceledException) { }
        return results;
    }

    private static async Task<MutantResult> RunOne(ProjectContext context, string source, MutationSite site,
        TimeSpan timeout, bool verbose, CancellationToken cancellationToken)
    {
        var runRoot = Path.Combine(Path.GetTempPath(), "mutate4csharp", $"run-{Guid.NewGuid():N}");
        var worker = Path.Combine(runRoot, "worker");
        try
        {
            CopyTree(context.Root, worker);
            var target = Path.Combine(worker, context.TargetRelativePath);
            File.WriteAllText(target, site.Apply(source));
            var test = Path.Combine(worker, Path.GetRelativePath(context.Root, context.TestProject));
            var results = Path.Combine(runRoot, "results");
            if (verbose) Console.WriteLine($"Worker start: {site.Index} line {site.Line}");
            var run = await TestRunner.RunAsync(worker, test, results, timeout, false, cancellationToken);
            var status = Classify(run);
            string? detail = status == MutantStatus.Error
                ? Tail(run.StandardError + Environment.NewLine + run.StandardOutput)
                : null;
            if (verbose) Console.WriteLine($"Worker finish: {site.Index} {status.ToString().ToUpperInvariant()}");
            return new(site, status, run.Duration, detail);
        }
        finally
        {
            try { if (Directory.Exists(runRoot)) Directory.Delete(runRoot, recursive: true); } catch { }
        }
    }

    internal static MutantStatus Classify(TestRunResult run)
    {
        if (run.TimedOut) return MutantStatus.Timeout;
        if (LooksLikeCompileFailure(run)) return MutantStatus.CompileError;
        if (!run.TrxValid || !run.TestsDiscovered) return MutantStatus.Error;
        if (run.ExitCode != 0 && run.HasFailedTests) return MutantStatus.Killed;
        if (run.ExitCode == 0 && !run.HasFailedTests) return MutantStatus.Survived;
        return MutantStatus.Error;
    }

    private static bool LooksLikeCompileFailure(TestRunResult run)
    {
        var text = run.StandardOutput + "\n" + run.StandardError;
        // A generic failed-build banner can also describe restore, SDK, workload, or
        // infrastructure failures. Only a C# compiler diagnostic is strong enough
        // evidence that this mutant failed to compile.
        return text.Contains(": error CS", StringComparison.OrdinalIgnoreCase);
    }

    private static string Tail(string value)
    {
        var lines = value.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(" | ", lines.TakeLast(3)).Trim();
    }

    internal static void CopyTree(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var directory in ProjectLocator.EnumerateDirectoriesSafe(source))
        {
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        }
        foreach (var file in ProjectLocator.EnumerateFilesSafe(source))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: false);
        }
    }
}
