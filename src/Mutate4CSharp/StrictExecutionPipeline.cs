namespace Mutate4CSharp;

internal sealed record StrictExecutionOutcome(
    EvaluationFingerprintMaterial FingerprintMaterial,
    MutationSelectionPlan FinalizingPlan,
    string SemanticContextIdentity,
    string SdkVersion,
    int EnumerationCount,
    IReadOnlyList<EvaluationUnitResult> Units,
    BaselineStatus Baseline,
    IReadOnlyList<SuiteEvidence> Suites,
    IReadOnlyList<CoverageProvenance> Coverage,
    IReadOnlyList<EvaluationReason> IncompleteConditions,
    EvaluationReason? Reason,
    IReadOnlyList<EvaluationEvidence> Evidence);

internal sealed record ContiguousAttemptBatch(
    IReadOnlyDictionary<string, IReadOnlyList<ScheduledMutationResult>> Attempts,
    IReadOnlyList<EvaluationReason> IncompleteConditions);

internal static class StrictExecutionPipeline
{
    private static readonly TimeProvider Clock = TimeProvider.System;
    internal const int MaxStrictWorkers = 1;

    public static async Task<StrictExecutionOutcome> RunAsync(InputSnapshot snapshot, string snapshotId, string runId,
        ScopePlan scopePlan, CheckConfiguration configuration, IReadOnlyList<string> exactMutationIds,
        string? expectedPlanFingerprint, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(scopePlan);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(exactMutationIds);
        var policy = configuration.Policy;
        var deadline = Clock.GetUtcNow().AddSeconds(policy.OverallDeadlineSeconds);
        var dependencyPaths = configuration.Projects.Select(item => item.Project)
            .Concat(configuration.ExecutionSuites.Select(item => item.Path))
            .Distinct(StringComparer.Ordinal).OrderBy(item => item, StringComparer.Ordinal).ToArray();
        var preparationTimeout = Remaining(deadline);
        if (preparationTimeout > DependencyPreparationOptions.Default.Timeout)
            preparationTimeout = DependencyPreparationOptions.Default.Timeout;

        FrozenExecutionEnvironment environment;
        try
        {
            environment = await ExecutionEnvironment.PrepareDependenciesAsync(snapshot,
                dependencyPaths, DependencyPreparationOptions.Default with { Timeout = preparationTimeout },
                cancellationToken);
        }
        catch (SnapshotCleanupException) { throw; }
        catch (SnapshotDivergedException) { throw; }
        catch (SnapshotLimitException) { throw; }
        catch (SnapshotEnvironmentException) { throw; }
        catch (ExecutionBoundaryIntegrityException) { throw; }
        catch (OperationCanceledException) { throw; }
        catch (ExecutionEnvironmentUnavailableException ex)
        {
            throw new StrictExecutionRefusalException(new("EXECUTION_ENVIRONMENT_UNAVAILABLE",
                "The configured .NET execution environment could not be started."),
                [new("EXECUTION_ENVIRONMENT_UNAVAILABLE", ex.Message)]);
        }
        catch (SnapshotCaptureException ex)
        {
            throw new StrictExecutionRefusalException(new("DEPENDENCY_INPUT_UNAVAILABLE",
                "Strict dependency preparation could not establish one frozen package graph."),
                [new("DEPENDENCY_INPUT_UNAVAILABLE", ex.Message)]);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new StrictExecutionRefusalException(new("EXECUTION_ENVIRONMENT_UNAVAILABLE",
                "The configured .NET execution environment could not be started."),
                [new("EXECUTION_ENVIRONMENT_UNAVAILABLE", ex.Message)]);
        }
        SuiteBaselineResult? baselines = null;
        StrictExecutionOutcome? outcome = null;
        Exception? failure = null;
        try
        {
        var enumeration = StrictMutationEnumerator.Enumerate(snapshot, configuration, scopePlan,
            cancellationToken, environment.SdkVersion);
        if (!enumeration.IsComplete)
            throw new StrictExecutionRefusalException(enumeration.Reasons.FirstOrDefault() ??
                new EvaluationReason("ENUMERATION_CONTEXT_UNSUPPORTED",
                    "Strict semantic enumeration could not prove one complete candidate set."),
                enumeration.Reasons);
        var semanticContext = enumeration.SemanticContextIdentity ??
            throw new EvaluationContractException("Complete semantic enumeration requires one context identity.");
        baselines = await new SuiteCoordinator(new VstestSuiteExecutor(snapshot, environment),
            Clock).RunBaselinesAsync(snapshotId, configuration.ExecutionSuites,
            TimeSpan.FromSeconds(policy.BaselineTimeoutSeconds), deadline, cancellationToken);
        var aliases = MapBaselineExecutions(configuration.ExecutionSuites, baselines.Executions);
        var suiteEvidence = ToSuiteEvidence(configuration.ExecutionSuites, aliases);
        var material = EvaluationCoordinator.BuildEvaluationFingerprintMaterial(snapshot, snapshotId,
            scopePlan, policy, semanticContext, environment.SdkVersion, environment,
            configuration.ExecutionSuites, aliases, enumeration.Candidates);
        BoundMutationPlan bound;
        try { bound = MutationSelection.Bind(enumeration.Candidates, material); }
        catch (Exception ex) when (ex is EvaluationContractException or ArgumentException)
        {
            throw new StrictExecutionRefusalException(new("ENUMERATION_FINGERPRINT_INVALID",
                "Fresh semantic enumeration could not bind to the current evaluation fingerprint."),
                [new("ENUMERATION_FINGERPRINT_INVALID", ex.Message)]);
        }
        var targeted = exactMutationIds.Count > 0;
        MutationSelectionPlan plan;
        try
        {
            plan = targeted
                ? MutationSelection.PlanTargeted(bound, exactMutationIds,
                    expectedPlanFingerprint ?? string.Empty, material)
                : MutationSelection.Plan(bound, material);
        }
        catch (EvaluationContractException ex) when (targeted)
        {
            throw new StrictExecutionRefusalException(new("TARGET_SELECTION_INVALID",
                "The exact-ID diagnostic request did not match the freshly bound mutation plan."),
                [new("TARGET_SELECTION_INVALID", ex.Message)]);
        }
        var issued = plan.CreatePendingLedger();
        var pending = issued.Where(item => item.Disposition == UnitDisposition.Pending)
            .ToDictionary(item => item.EvaluationUnitId, StringComparer.Ordinal);
        var completed = issued.Where(item => item.Disposition != UnitDisposition.Pending).ToList();

        var evidence = new List<EvaluationEvidence>
        {
            new("DEPENDENCY_INPUT", "Prepared one frozen package graph and run-private worker caches.",
                [$"dependencyFingerprint={environment.Fingerprint}",
                 $"packageFingerprint={environment.PackageFingerprint}",
                 $"sdk={environment.SdkVersion}"]),
            new("MUTATION_PLAN",
                $"Bound {enumeration.Candidates.Count} evaluation unit(s); selected {plan.Selected.Count} " +
                $"and omitted {plan.Omitted.Count}.",
                [$"evaluationFingerprint={bound.EvaluationFingerprint}",
                 $"planFingerprint={bound.PlanFingerprint}",
                 $"selectionFingerprint={plan.PlanFingerprint}",
                 $"configuredWorkers={policy.MaxWorkers};effectiveWorkers={MaxStrictWorkers}"])
        };
        var conditions = plan.IncompleteConditions.Concat(baselines.IncompleteConditions).ToList();
        if (baselines.Status == BaselineStatus.Green && !material.ProvenanceComplete)
            conditions.Add(new("COVERAGE_PROVENANCE_INCOMPLETE",
                "Every configured suite must provide fresh valid coverage and exact baseline member accounting."));

        if (baselines.Status != BaselineStatus.Green)
        {
            foreach (var candidate in plan.Selected)
                completed.Add(plan.CompleteWithoutExecution(pending[candidate.EvaluationUnitId],
                    UnitDisposition.Omitted,
                    [new("BASELINE_NOT_GREEN",
                        $"Mutation execution was not started because baseline status was {baselines.Status}.")]));
        }
        else
        {
            if (aliases.Count != configuration.ExecutionSuites.Count)
                throw new StrictExecutionRefusalException(new("BASELINE_ACCOUNTING_INCOMPLETE",
                    "A green baseline omitted one or more configured suite executions."),
                    [new("BASELINE_ACCOUNTING_INCOMPLETE",
                        $"completed={aliases.Count};required={configuration.ExecutionSuites.Count}")]);
            var mappedSuites = plan.Selected.ToDictionary(item => item.EvaluationUnitId,
                item => MapCandidateSuites(configuration, item), StringComparer.Ordinal);
            var scheduled = new List<ScheduledMutation>();
            foreach (var candidate in plan.Selected)
            {
                var suites = mappedSuites[candidate.EvaluationUnitId];
                var coverage = suites.Select(item => aliases[item.Identity].Result.CoverageMap?
                        .GetState(candidate.Material.RepositoryPath, candidate.SourceLine,
                            candidate.SourceColumn, candidate.SourceEndLine, candidate.SourceEndColumn) ??
                    CoverageState.Unknown)
                    .ToArray();
                if (coverage.All(item => item == CoverageState.Uncovered))
                {
                    completed.Add(plan.CompleteWithoutExecution(pending[candidate.EvaluationUnitId],
                        UnitDisposition.Uncovered,
                        [new("FRESH_COVERAGE_UNCOVERED",
                            "Every mapped fresh baseline conclusively reported the mutation line uncovered.",
                            suites.Select((suite, index) =>
                                $"suite={suite.Identity};state={coverage[index]}").ToArray())]));
                    continue;
                }
                scheduled.Add(new(candidate.MutationId, candidate.EvaluationUnitId,
                    suites.Select(item => item.Identity).ToArray()));
            }

            var controls = configuration.ExecutionSuites.ToDictionary(item => item.Identity,
                item => aliases[item.Identity].Result.HealthyControl, StringComparer.Ordinal);
            var executor = new StrictMutationExecutor(snapshot, environment, plan.Selected,
                configuration.ExecutionSuites, controls, Clock);
            var batch = await RunContiguousAttemptsAsync(executor, scheduled,
                policy.StabilityRepetitions, TimeSpan.FromSeconds(policy.MutantTimeoutSeconds), deadline,
                Clock, cancellationToken);
            conditions.AddRange(batch.IncompleteConditions);
            foreach (var mutation in scheduled)
            {
                var results = batch.Attempts[mutation.EvaluationUnitId];
                if (results.Any(item => item.Disposition == UnitDisposition.Omitted))
                {
                    completed.Add(plan.CompleteWithoutExecution(pending[mutation.EvaluationUnitId],
                        UnitDisposition.Omitted, BuildPartialEvidence(results)));
                    conditions.Add(new("MUTATION_ATTEMPT_OMITTED",
                        "At least one configured stability attempt was not assigned."));
                    continue;
                }
                completed.Add(plan.Reduce(pending[mutation.EvaluationUnitId],
                    results.Select((item, index) => new StabilityAttempt(index + 1,
                        item.Disposition, item.Evidence)).ToArray()));
            }
        }

        var coverageProvenance = ToCoverageProvenance(runId, snapshotId, material,
            configuration.ExecutionSuites, aliases);
        outcome = new(material, plan, semanticContext, environment.SdkVersion, issued.Count,
            plan.OrderResults(completed), baselines.Status, suiteEvidence, coverageProvenance,
            conditions.Distinct().ToArray(), null, evidence);
        }
        catch (Exception ex) { failure = ex; }
        await AsyncDisposal.DisposeAllPreservingFailureAsync([baselines, environment], failure);
        return outcome ?? throw new EvaluationContractException(
            "Strict execution completed without an outcome or preserved failure.");
    }

    internal static IReadOnlyList<EvaluationEvidence> BuildPartialEvidence(
        IReadOnlyList<ScheduledMutationResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);
        if (results.Count == 0 || results.Any(item => item.Evidence is null) ||
            results.All(item => item.Disposition != UnitDisposition.Omitted))
            throw new EvaluationContractException(
                "Partial stability evidence requires at least one omitted attempt with evidence.");
        var bounded = StabilityEvidence.BoundTotal(results.SelectMany(item => item.Evidence).ToArray(),
            StabilityEvidence.MaxUnitEvidence - 1);
        bounded.Add(new("PARTIAL_STABILITY_EVIDENCE",
            "The unit retained every completed stability attempt before execution stopped.",
            BoundAttemptDiagnostics(results)));
        return bounded;
    }

    private static IReadOnlyList<string> BoundAttemptDiagnostics(
        IReadOnlyList<ScheduledMutationResult> results) => EvaluationEvidence.BoundDiagnostics(
        results.Select((item, index) =>
            $"attempt={index + 1};disposition={item.Disposition}"),
        truncationLabel: "attempts-truncated");

    internal static async Task<ContiguousAttemptBatch> RunContiguousAttemptsAsync(
        IIsolatedMutationExecutor executor, IReadOnlyList<ScheduledMutation> scheduled, int repetitions,
        TimeSpan mutantTimeout, DateTimeOffset deadline, TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(executor);
        ArgumentNullException.ThrowIfNull(scheduled);
        ArgumentNullException.ThrowIfNull(timeProvider);
        if (repetitions < 1) throw new ArgumentOutOfRangeException(nameof(repetitions));
        if (mutantTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(mutantTimeout));

        var ordered = scheduled.OrderBy(item => item.EvaluationUnitId, StringComparer.Ordinal).ToArray();
        var duplicate = ordered.GroupBy(item => item.EvaluationUnitId, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
            throw new EvaluationContractException(
                $"Duplicate scheduled evaluation unit: {duplicate.Key}.");

        var attempts = ordered.ToDictionary(item => item.EvaluationUnitId,
            _ => new List<ScheduledMutationResult>(), StringComparer.Ordinal);
        var conditions = new List<EvaluationReason>();
        var scheduler = new EvaluationScheduler(executor, timeProvider);
        foreach (var mutation in ordered)
        {
            for (var repetition = 1; repetition <= repetitions; repetition++)
            {
                var run = await scheduler.RunAsync([mutation], MaxStrictWorkers, mutantTimeout,
                    deadline, cancellationToken);
                conditions.AddRange(run.IncompleteConditions);
                var result = AssertSingleResult(run.Results, mutation.EvaluationUnitId);
                attempts[mutation.EvaluationUnitId].Add(result);
                if (result.Disposition == UnitDisposition.Omitted) break;
            }
        }
        return new(attempts.ToDictionary(item => item.Key,
                item => (IReadOnlyList<ScheduledMutationResult>)item.Value.ToArray(), StringComparer.Ordinal),
            conditions.Distinct().ToArray());
    }

    private static ScheduledMutationResult AssertSingleResult(
        IReadOnlyList<ScheduledMutationResult> results, string evaluationUnitId)
    {
        if (results.Count != 1 ||
            !string.Equals(results[0].EvaluationUnitId, evaluationUnitId, StringComparison.Ordinal))
            throw new EvaluationContractException(
                "Contiguous stability scheduling returned invalid evaluation-unit accounting.");
        return results[0];
    }

    private static TimeSpan Remaining(DateTimeOffset deadline)
    {
        var remaining = deadline - Clock.GetUtcNow();
        if (remaining <= TimeSpan.Zero)
            throw new StrictExecutionRefusalException(new("OVERALL_DEADLINE_EXCEEDED",
                "The overall deadline expired before strict execution could start."), []);
        return remaining;
    }

    internal static IReadOnlyDictionary<string, SuiteBaselineExecution> MapBaselineExecutions(
        IReadOnlyList<SuiteExecution> suites, IReadOnlyList<SuiteBaselineExecution> baselines)
    {
        var result = new Dictionary<string, SuiteBaselineExecution>(StringComparer.Ordinal);
        foreach (var suite in suites)
        {
            var matches = baselines.Where(item =>
                item.SuiteIds.Intersect(suite.Aliases, StringComparer.Ordinal).Any()).ToArray();
            if (matches.Length > 1)
                throw new EvaluationContractException(
                    $"Suite {suite.Identity} has duplicate baseline execution identity mappings.");
            if (matches.Length == 1) result.Add(suite.Identity, matches[0]);
        }
        return result;
    }

    private static IReadOnlyList<SuiteExecution> MapCandidateSuites(CheckConfiguration configuration,
        MutationCandidate candidate)
    {
        var project = configuration.Projects.SingleOrDefault(item =>
            item.Project.Equals(candidate.ProjectPath, StringComparison.Ordinal)) ??
            throw new EvaluationContractException(
                $"Mutation project {candidate.ProjectPath} has no exact suite mapping.");
        var aliases = project.TestSuites.ToHashSet(StringComparer.Ordinal);
        var suites = configuration.ExecutionSuites.Where(item => item.Aliases.Any(aliases.Contains))
            .OrderBy(item => item.Identity, StringComparer.Ordinal).ToArray();
        if (suites.Length == 0)
            throw new EvaluationContractException(
                $"Mutation project {candidate.ProjectPath} maps no distinct suite execution.");
        return suites;
    }

    private static IReadOnlyList<SuiteEvidence> ToSuiteEvidence(IReadOnlyList<SuiteExecution> suites,
        IReadOnlyDictionary<string, SuiteBaselineExecution> baselines) => suites
        .OrderBy(item => item.Identity, StringComparer.Ordinal).Select(suite =>
        {
            if (!baselines.TryGetValue(suite.Identity, out var execution))
                return new SuiteEvidence(suite.Identity, BaselineStatus.Unknown,
                    [new("SUITE_BASELINE_NOT_RUN",
                        "The suite did not start before baseline cancellation or deadline exhaustion.")]);
            var run = execution.Result;
            var baseline = run.Disposition switch
            {
                SuiteRunDisposition.Passed => BaselineStatus.Green,
                SuiteRunDisposition.Failed => BaselineStatus.Red,
                SuiteRunDisposition.Empty => BaselineStatus.Empty,
                _ => BaselineStatus.Unknown
            };
            var details = new List<string>
            {
                $"disposition={run.Disposition}", $"tests={run.AccountedMembers.Count}",
                "accountedMembers=" + (run.AccountedMembers.Count == 0
                    ? "<none>" : string.Join(',', run.AccountedMembers)),
                "expectedMembers=" + string.Join(',', suite.ExpectedMembers)
            };
            if (run.CoverageSha256 is not null)
            {
                details.Add($"coverageSha256={run.CoverageSha256}");
                details.Add($"coverageLength={run.CoverageLength}");
                details.Add("pathMap=baseline-clone-to-snapshot-v1");
            }
            return new SuiteEvidence(suite.Identity, baseline,
                [new("SUITE_BASELINE", $"Fresh baseline classified suite as {baseline}.",
                    EvaluationEvidence.BoundDiagnostics(run.Diagnostics, details,
                        "diagnostics-truncated"))]);
        }).ToArray();

    private static IReadOnlyList<CoverageProvenance> ToCoverageProvenance(string runId, string snapshotId,
        EvaluationFingerprintMaterial material, IReadOnlyList<SuiteExecution> suites,
        IReadOnlyDictionary<string, SuiteBaselineExecution> baselines)
    {
        var fingerprint = EvaluationFingerprint.Compute(material);
        return suites.OrderBy(item => item.Identity, StringComparer.Ordinal)
            .Where(suite => baselines.TryGetValue(suite.Identity, out var execution) &&
                            execution.Result.CoverageSha256 is not null &&
                            execution.Result.CoverageLength > 0)
            .Select(suite =>
            {
                var run = baselines[suite.Identity].Result;
                return new CoverageProvenance("1", runId, suite.Identity, fingerprint, snapshotId,
                    run.Disposition == SuiteRunDisposition.Passed ? BaselineStatus.Green : BaselineStatus.Unknown,
                    run.CoverageSha256!, run.CoverageLength, "baseline-clone-to-snapshot-v1",
                    material.RunnerIdentity, CollectedFresh: true);
            }).ToArray();
    }
}

internal sealed class StrictExecutionRefusalException(EvaluationReason reason,
    IReadOnlyList<EvaluationReason> reasons) : Exception(reason.Message)
{
    public EvaluationReason Reason { get; } = reason;
    public IReadOnlyList<EvaluationReason> Reasons { get; } = reasons.Count == 0 ? [reason] : reasons;
}
