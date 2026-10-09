namespace Mutate4CSharp;

internal sealed class StrictMutationExecutor : IIsolatedMutationExecutor
{
    private readonly InputSnapshot _snapshot;
    private readonly FrozenExecutionEnvironment _environment;
    private readonly IReadOnlyDictionary<string, MutationCandidate> _candidates;
    private readonly IReadOnlyDictionary<string, SuiteExecution> _suites;
    private readonly IReadOnlyDictionary<string, bool> _healthyControls;
    private readonly TimeProvider _timeProvider;

    public StrictMutationExecutor(InputSnapshot snapshot, FrozenExecutionEnvironment environment,
        IReadOnlyList<MutationCandidate> candidates, IReadOnlyList<SuiteExecution> suites,
        IReadOnlyDictionary<string, bool> healthyControls, TimeProvider? timeProvider = null)
    {
        _snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
        _environment = environment ?? throw new ArgumentNullException(nameof(environment));
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(suites);
        ArgumentNullException.ThrowIfNull(healthyControls);
        _candidates = candidates.ToDictionary(item => item.EvaluationUnitId, StringComparer.Ordinal);
        _suites = suites.ToDictionary(item => item.Identity, StringComparer.Ordinal);
        _healthyControls = healthyControls;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<ScheduledMutationResult> ExecuteAsync(ScheduledMutation mutation, TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (!_candidates.TryGetValue(mutation.EvaluationUnitId, out var candidate) ||
            !string.Equals(candidate.MutationId, mutation.MutationId, StringComparison.Ordinal))
            throw new EvaluationContractException("Scheduled mutation is absent from the frozen candidate plan.");
        if (candidate.SourceStart < 0 || candidate.SourceLength <= 0 || candidate.SourceLine <= 0 ||
            candidate.Original is null || candidate.Original.Length != candidate.SourceLength)
            throw new EvaluationContractException("Frozen mutation candidate lacks an exact source span.");
        var mapped = mutation.SuiteIds.Select(id => _suites.TryGetValue(id, out var suite)
                ? suite
                : throw new EvaluationContractException($"Scheduled mutation maps unknown suite identity {id}."))
            .ToArray();
        if (mapped.Length == 0 || mapped.Select(item => item.Identity).Distinct(StringComparer.Ordinal).Count() !=
            mapped.Length)
            throw new EvaluationContractException("Scheduled mutation requires distinct mapped suite identities.");

        var started = _timeProvider.GetUtcNow();
        SnapshotClone? worker = null;
        OwnedPackageCache? packages = null;
        ScheduledMutationResult? completed = null;
        Exception? failure = null;
        try
        {
            worker = await SnapshotWorkspace.CreateCloneAsync(_snapshot,
                "strict-mutant-" + candidate.EvaluationUnitId[^12..], cancellationToken);
            packages = await _environment.CreateWorkerPackageCacheAsync(
                "strict-mutant-" + candidate.EvaluationUnitId[^12..], cancellationToken);
            completed = await ExecuteInOwnedResourcesAsync(worker, packages);
        }
        catch (Exception ex) { failure = ex; }

        try { await AsyncDisposal.DisposeAllAsync([packages, worker]); }
        catch (Exception cleanupFailure)
        {
            failure = AsyncDisposal.CombineFailure(failure, cleanupFailure);
        }
        if (failure is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        return completed ?? throw new EvaluationContractException("Mutant execution produced no result.");

        async Task<ScheduledMutationResult> ExecuteInOwnedResourcesAsync(
            SnapshotClone ownedWorker, OwnedPackageCache ownedPackages)
        {
            ValidateOriginalSpan(ownedWorker, candidate);
            ownedWorker.WriteTextMutation(candidate.Material.RepositoryPath, candidate.SourceStart,
                candidate.SourceLength, candidate.Material.Replacement);

            var evidence = new List<SuiteMutationEvidence>();
            for (var index = 0; index < mapped.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var suite = mapped[index];
                var remaining = timeout - (_timeProvider.GetUtcNow() - started);
                if (remaining <= TimeSpan.Zero)
                    return Timeout(mutation, suite.Identity);
                await _environment.RestoreWorkerAsync(ownedWorker, suite.Path, ownedPackages, remaining,
                    cancellationToken, suite.Framework, suite.Configuration);
                remaining = timeout - (_timeProvider.GetUtcNow() - started);
                if (remaining <= TimeSpan.Zero)
                    return Timeout(mutation, suite.Identity);
                var results = Path.Combine(ownedWorker.OwnedRoot, $"mutant-results-{index:D4}");
                var testPath = Path.Combine(ownedWorker.Root,
                    suite.Path.Replace('/', Path.DirectorySeparatorChar));
                var run = await TestRunner.RunAsync(ownedWorker.Root, testPath, results, remaining,
                    collectCoverage: false, cancellationToken,
                    ExecutionEnvironment.ProcessEnvironment(ownedPackages.Root), noRestore: true,
                    requireExecutionBoundary: true, framework: suite.Framework,
                    configuration: suite.Configuration);
                if (!string.Equals(_environment.PackageFingerprint,
                        ExecutionEnvironment.FingerprintPackages(ownedPackages.Root), StringComparison.Ordinal))
                    throw new ExecutionBoundaryIntegrityException(
                        "Mutant execution changed its private frozen package-cache copy.");
                var compile = CompilerEvidence.Evaluate(run,
                    _healthyControls.TryGetValue(suite.Identity, out var healthy) && healthy,
                    Path.Combine(ownedWorker.Root, candidate.Material.RepositoryPath.Replace('/',
                        Path.DirectorySeparatorChar)), ownedWorker.Root);
                if (compile.IsCompileInvalid)
                {
                    evidence.Add(ToSuiteEvidence(suite.Identity, run, compile));
                    continue;
                }
                var accounted = VstestSuiteExecutor.AccountedMembers(run.TrxPaths);
                var missing = suite.ExpectedMembers.Except(accounted,
                    StringComparer.OrdinalIgnoreCase).ToArray();
                if (missing.Length > 0)
                {
                    evidence.Add(new(suite.Identity, SuiteRunDisposition.Error, [],
                        [$"Mutant TRX omitted expected member(s): {string.Join(", ", missing.Take(20))}."]));
                    continue;
                }
                evidence.Add(ToSuiteEvidence(suite.Identity, run, compile));
            }
            var aggregate = SuiteCoordinator.AggregateMutant(candidate.MutationId,
                mapped.Select(item => item.Identity).ToArray(), evidence);
            return new(candidate.EvaluationUnitId, aggregate.Disposition, aggregate.Evidence);
        }
    }

    private static void ValidateOriginalSpan(SnapshotClone worker, MutationCandidate candidate)
    {
        var path = Path.Combine(worker.Root,
            candidate.Material.RepositoryPath.Replace('/', Path.DirectorySeparatorChar));
        var source = File.ReadAllText(path);
        if (candidate.SourceStart + candidate.SourceLength > source.Length ||
            !source.AsSpan(candidate.SourceStart, candidate.SourceLength).SequenceEqual(candidate.Original))
            throw new SnapshotDivergedException(
                "Frozen mutation span no longer matches canonical enumeration bytes.");
    }

    private static SuiteMutationEvidence ToSuiteEvidence(string suiteId, TestRunResult run,
        CompileEvidence compile)
    {
        SuiteRunDisposition disposition;
        if (compile.IsCompileInvalid) disposition = SuiteRunDisposition.Error;
        else if (run.TimedOut) disposition = SuiteRunDisposition.TimedOut;
        else if (!run.TrxValid || run.HasRunErrors || !run.TestsDiscovered)
            disposition = SuiteRunDisposition.Error;
        else if (run.ExitCode != 0 && run.HasFailedTests) disposition = SuiteRunDisposition.Killed;
        else if (run.ExitCode == 0 && !run.HasFailedTests) disposition = SuiteRunDisposition.Survived;
        else disposition = SuiteRunDisposition.Error;
        var diagnostics = compile.IsCompileInvalid
            ? compile.Diagnostics
            : (run.Diagnostics ?? []).Concat(disposition == SuiteRunDisposition.Error
                ? [Bound(run.StandardError + " " + run.StandardOutput)] : []).Take(20).ToArray();
        return new(suiteId, disposition, run.FailedTestIds ?? [], diagnostics,
            compile.IsCompileInvalid);
    }

    private static ScheduledMutationResult Timeout(ScheduledMutation mutation, string suiteId) =>
        new(mutation.EvaluationUnitId, UnitDisposition.Error,
            [new("MUTANT_TIMEOUT", $"The isolated mutant exhausted its bounded timeout before suite {suiteId}.")]);

    private static string Bound(string value)
    {
        var sanitized = string.Join(' ', value.Split((char[]?)null,
            StringSplitOptions.RemoveEmptyEntries));
        return sanitized.Length <= 512 ? sanitized : sanitized[..512];
    }
}
