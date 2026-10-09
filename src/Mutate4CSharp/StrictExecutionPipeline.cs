namespace Mutate4CSharp;

internal sealed record StrictExecutionOutcome(
    EvaluationFingerprintMaterial FingerprintMaterial,
    string SemanticContextIdentity,
    string SdkVersion,
    int EnumerationCount,
    IReadOnlyList<EvaluationUnitResult> Units,
    BaselineStatus Baseline,
    IReadOnlyList<SuiteEvidence> Suites,
    IReadOnlyList<EvaluationReason> IncompleteConditions,
    EvaluationReason Reason,
    IReadOnlyList<EvaluationEvidence> Evidence);

internal static class StrictExecutionPipeline
{
    private static readonly TimeProvider Clock = TimeProvider.System;

    public static async Task<StrictExecutionOutcome> RunAsync(InputSnapshot snapshot, string snapshotId,
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
        catch (OperationCanceledException) { throw; }
        catch (SnapshotCaptureException ex)
        {
            var code = ex.Message.Contains(".NET SDK", StringComparison.Ordinal) ||
                       ex.Message.Contains("dotnet", StringComparison.OrdinalIgnoreCase)
                ? "EXECUTION_ENVIRONMENT_UNAVAILABLE" : "DEPENDENCY_INPUT_UNAVAILABLE";
            var summary = code == "EXECUTION_ENVIRONMENT_UNAVAILABLE"
                ? "The configured .NET execution environment could not be started."
                : "Strict dependency preparation could not establish one frozen package graph.";
            throw new StrictExecutionRefusalException(new(code, summary), [new(code, ex.Message)]);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or
                                   InvalidOperationException)
        {
            throw new StrictExecutionRefusalException(new("EXECUTION_ENVIRONMENT_UNAVAILABLE",
                "The configured .NET execution environment could not be started."),
                [new("EXECUTION_ENVIRONMENT_UNAVAILABLE", ex.Message)]);
        }
        await using (environment)
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
        var material = EvaluationCoordinator.BuildEvaluationFingerprintMaterial(snapshot, snapshotId,
            scopePlan, policy, semanticContext, environment.SdkVersion, environment,
            configuration.ExecutionSuites);
        var bound = MutationSelection.Bind(enumeration.Candidates, material);
        var targeted = exactMutationIds.Count > 0;
        var plan = targeted
            ? MutationSelection.PlanTargeted(bound, exactMutationIds,
                expectedPlanFingerprint ?? string.Empty, material)
            : MutationSelection.Plan(bound, material);
        var issued = plan.CreatePendingLedger();
        var pending = issued.Where(item => item.Disposition == UnitDisposition.Pending)
            .ToDictionary(item => item.EvaluationUnitId, StringComparer.Ordinal);
        var completed = issued.Where(item => item.Disposition != UnitDisposition.Pending).ToList();

        await using var baselines = await new SuiteCoordinator(new VstestSuiteExecutor(snapshot, environment),
            Clock).RunBaselinesAsync(snapshotId, configuration.ExecutionSuites,
            TimeSpan.FromSeconds(policy.BaselineTimeoutSeconds), deadline, cancellationToken);
        var aliases = MapBaselineExecutions(configuration.ExecutionSuites, baselines.Executions);
        var suiteEvidence = ToSuiteEvidence(configuration.ExecutionSuites, aliases);
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
                 $"selectionFingerprint={plan.PlanFingerprint}"])
        };
        var conditions = plan.IncompleteConditions.Concat(baselines.IncompleteConditions).ToList();

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
            var mappedSuites = plan.Selected.ToDictionary(item => item.EvaluationUnitId,
                item => MapCandidateSuites(configuration, item), StringComparer.Ordinal);
            var scheduled = new List<ScheduledMutation>();
            foreach (var candidate in plan.Selected)
            {
                var suites = mappedSuites[candidate.EvaluationUnitId];
                var coverage = suites.Select(item => aliases[item.Identity].Result.CoverageMap?
                        .GetState(candidate.Material.RepositoryPath, candidate.SourceLine) ?? CoverageState.Unknown)
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
            var attempts = scheduled.ToDictionary(item => item.EvaluationUnitId,
                _ => new List<ScheduledMutationResult>(), StringComparer.Ordinal);
            for (var repetition = 1; repetition <= policy.StabilityRepetitions; repetition++)
            {
                var run = await new EvaluationScheduler(executor, Clock).RunAsync(scheduled,
                    policy.MaxWorkers, TimeSpan.FromSeconds(policy.MutantTimeoutSeconds), deadline,
                    cancellationToken);
                conditions.AddRange(run.IncompleteConditions);
                foreach (var result in run.Results) attempts[result.EvaluationUnitId].Add(result);
            }
            foreach (var mutation in scheduled)
            {
                var results = attempts[mutation.EvaluationUnitId];
                if (results.Any(item => item.Disposition == UnitDisposition.Omitted))
                {
                    completed.Add(plan.CompleteWithoutExecution(pending[mutation.EvaluationUnitId],
                        UnitDisposition.Omitted, results.SelectMany(item => item.Evidence).ToArray()));
                    conditions.Add(new("MUTATION_ATTEMPT_OMITTED",
                        "At least one configured stability attempt was not assigned."));
                    continue;
                }
                completed.Add(plan.Reduce(pending[mutation.EvaluationUnitId],
                    results.Select((item, index) => new StabilityAttempt(index + 1,
                        item.Disposition, item.Evidence)).ToArray()));
            }
        }

        var finalization = new EvaluationReason("FINALIZATION_PENDING",
            "Fresh strict execution evidence is available, but issue #4 must complete final verification " +
            "before strict PASS can be published.");
        conditions.Add(finalization);
        return new(material, semanticContext, environment.SdkVersion, issued.Count,
            plan.OrderResults(completed), baselines.Status, suiteEvidence,
            conditions.Distinct().ToArray(), finalization, evidence);
        }
    }

    private static TimeSpan Remaining(DateTimeOffset deadline)
    {
        var remaining = deadline - Clock.GetUtcNow();
        if (remaining <= TimeSpan.Zero)
            throw new StrictExecutionRefusalException(new("OVERALL_DEADLINE_EXCEEDED",
                "The overall deadline expired before strict execution could start."), []);
        return remaining;
    }

    private static IReadOnlyDictionary<string, SuiteBaselineExecution> MapBaselineExecutions(
        IReadOnlyList<SuiteExecution> suites, IReadOnlyList<SuiteBaselineExecution> baselines)
    {
        var result = new Dictionary<string, SuiteBaselineExecution>(StringComparer.Ordinal);
        foreach (var suite in suites)
        {
            var matches = baselines.Where(item =>
                item.SuiteIds.Intersect(suite.Aliases, StringComparer.Ordinal).Any()).ToArray();
            if (matches.Length != 1)
                throw new EvaluationContractException(
                    $"Suite {suite.Identity} has no exact baseline execution identity mapping.");
            result.Add(suite.Identity, matches[0]);
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
            var run = baselines[suite.Identity].Result;
            var baseline = run.Disposition switch
            {
                SuiteRunDisposition.Passed => BaselineStatus.Green,
                SuiteRunDisposition.Failed => BaselineStatus.Red,
                SuiteRunDisposition.Empty => BaselineStatus.Empty,
                _ => BaselineStatus.Unknown
            };
            var details = new List<string>
            {
                $"disposition={run.Disposition}", $"tests={run.AccountedMembers.Count}"
            };
            if (run.CoverageSha256 is not null)
            {
                details.Add($"coverageSha256={run.CoverageSha256}");
                details.Add($"coverageLength={run.CoverageLength}");
                details.Add("pathMap=baseline-clone-to-snapshot-v1");
            }
            return new SuiteEvidence(suite.Identity, baseline,
                [new("SUITE_BASELINE", $"Fresh baseline classified suite as {baseline}.", details)]);
        }).ToArray();
}

internal sealed class StrictExecutionRefusalException(EvaluationReason reason,
    IReadOnlyList<EvaluationReason> reasons) : Exception(reason.Message)
{
    public EvaluationReason Reason { get; } = reason;
    public IReadOnlyList<EvaluationReason> Reasons { get; } = reasons.Count == 0 ? [reason] : reasons;
}
