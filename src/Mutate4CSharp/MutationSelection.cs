namespace Mutate4CSharp;

internal sealed record MutationCandidate(string MutationId, string EvaluationUnitId,
    MutationIdentityMaterial Material, string ProjectPath, string TargetFramework, string ParseContext,
    int SourceStart = -1, int SourceLength = -1, string? Original = null, int SourceLine = -1);

internal sealed record MutationOmission(MutationCandidate Candidate, string ReasonCode, string Message);

internal sealed class BoundMutationPlan
{
    private BoundMutationPlan(IReadOnlyList<MutationCandidate> candidates, string evaluationFingerprint,
        string planFingerprint)
    {
        Candidates = candidates;
        EvaluationFingerprint = evaluationFingerprint;
        PlanFingerprint = planFingerprint;
    }

    public IReadOnlyList<MutationCandidate> Candidates { get; }
    public string EvaluationFingerprint { get; }
    public string PlanFingerprint { get; }

    internal static BoundMutationPlan Create(IReadOnlyList<MutationCandidate> candidates,
        string evaluationFingerprint, string planFingerprint) =>
        new(candidates, evaluationFingerprint, planFingerprint);
}

internal sealed class MutationSelectionPlan
{
    private readonly object _provenanceToken = new();
    private readonly object _completionToken = new();
    private readonly string _boundPlanFingerprint;
    private readonly string _evaluationFingerprint;
    private readonly EvaluationPolicy _policy;
    private readonly object _reductionLock = new();
    private readonly Dictionary<string, string?> _issuedCompletionDigests = new(StringComparer.Ordinal);

    private MutationSelectionPlan(IReadOnlyList<MutationCandidate> selected,
        IReadOnlyList<MutationOmission> omitted, bool isDiagnosticPartial,
        bool canAdvanceFullScopeSuccess, string planFingerprint, string boundPlanFingerprint,
        string evaluationFingerprint, EvaluationPolicy policy,
        IReadOnlyList<EvaluationReason>? diagnosticIncompleteConditions = null)
    {
        Selected = selected;
        Omitted = omitted;
        IsDiagnosticPartial = isDiagnosticPartial;
        CanAdvanceFullScopeSuccess = canAdvanceFullScopeSuccess;
        PlanFingerprint = planFingerprint;
        _boundPlanFingerprint = boundPlanFingerprint;
        _evaluationFingerprint = evaluationFingerprint;
        _policy = policy;
        IncompleteConditions = diagnosticIncompleteConditions ?? [];
    }

    internal static MutationSelectionPlan CreateFull(BoundMutationPlan plan,
        EvaluationFingerprintMaterial currentMaterial)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var policy = ExecutionPolicy.ValidateContract(currentMaterial.Policy);
        var mutationCap = policy.MutationCap;
        var ordered = MutationSelection.ValidateCurrentBoundPlan(plan, currentMaterial);
        var selected = ordered.Take(mutationCap).ToArray();
        var omitted = ordered.Skip(mutationCap).Select(candidate => new MutationOmission(candidate,
            "BUDGET_OMITTED",
            $"Evaluation unit {candidate.EvaluationUnitId} exceeded the deterministic cap of {mutationCap}.")).ToArray();
        return new(selected, omitted, false, omitted.Length == 0,
            MutationSelection.ComputeSelectionPlanFingerprint("full", ordered, selected, omitted,
                plan.PlanFingerprint, policy),
            plan.PlanFingerprint, plan.EvaluationFingerprint, policy);
    }

    internal static MutationSelectionPlan CreateTargeted(BoundMutationPlan plan,
        IEnumerable<string> requestedMutationIds, string expectedPlanFingerprint,
        EvaluationFingerprintMaterial currentMaterial)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var policy = ExecutionPolicy.ValidateContract(currentMaterial.Policy);
        var ordered = MutationSelection.ValidateCurrentBoundPlan(plan, currentMaterial);
        var actualPlanFingerprint = plan.PlanFingerprint;
        if (!EvaluationFingerprint.IsFingerprint(expectedPlanFingerprint) ||
            !string.Equals(expectedPlanFingerprint, actualPlanFingerprint, StringComparison.Ordinal))
            throw new EvaluationContractException(
                "Targeted reruns require the exact fingerprint of a plan bound to the current evaluation material.");
        var requested = requestedMutationIds?.ToHashSet(StringComparer.Ordinal) ??
            throw new EvaluationContractException("Targeted reruns require a mutation ID collection.");
        if (requested.Count == 0 || requested.Any(id => !MutationIdentity.IsMutationId(id)))
            throw new EvaluationContractException("Targeted reruns require one or more exact mutation IDs.");
        var available = ordered.Select(item => item.MutationId).ToHashSet(StringComparer.Ordinal);
        var stale = requested.Where(id => !available.Contains(id)).OrderBy(id => id, StringComparer.Ordinal).ToArray();
        if (stale.Length > 0)
            throw new EvaluationContractException(
                $"Targeted mutation ID was not present in the fresh plan: {stale[0]}.");

        var selected = ordered.Where(item => requested.Contains(item.MutationId)).ToArray();
        var omitted = ordered.Where(item => !requested.Contains(item.MutationId))
            .Select(candidate => new MutationOmission(candidate, "TARGET_NOT_REQUESTED",
                $"Evaluation unit {candidate.EvaluationUnitId} was outside the diagnostic target set.")).ToArray();
        return new(selected, omitted, true, false,
            MutationSelection.ComputeSelectionPlanFingerprint("targeted", ordered, selected, omitted,
                actualPlanFingerprint, policy),
            actualPlanFingerprint, plan.EvaluationFingerprint, policy,
            [new(MutationSelection.TargetedDiagnosticCode,
                "Targeted reruns are diagnostic and cannot prove full-scope success.")]);
    }

    public IReadOnlyList<MutationCandidate> Selected { get; }
    public IReadOnlyList<MutationOmission> Omitted { get; }
    public bool IsDiagnosticPartial { get; }
    public bool CanAdvanceFullScopeSuccess { get; }
    public string PlanFingerprint { get; }
    public IReadOnlyList<EvaluationReason> IncompleteConditions { get; }

    public IReadOnlyList<EvaluationUnitResult> CreatePendingLedger() => OrderResults(
        Selected.Select(candidate => EvaluationUnitResult.Issue(candidate.MutationId, candidate.EvaluationUnitId,
                UnitDisposition.Pending, IsDiagnosticPartial
                    ? [new(MutationSelection.TargetedDiagnosticCode,
                        "Selected by an exact-ID diagnostic rerun; this evidence cannot prove full-scope success.")]
                    : [new("SELECTED_FOR_EXECUTION", "Selected by canonical mutation policy.")],
                IsDiagnosticPartial, PlanFingerprint, _provenanceToken, _completionToken, _policy))
            .Concat(Omitted.Select(item => EvaluationUnitResult.Issue(item.Candidate.MutationId,
                item.Candidate.EvaluationUnitId, UnitDisposition.Omitted,
                [new(item.ReasonCode, item.Message)], IsDiagnosticPartial, PlanFingerprint, _provenanceToken,
                _completionToken, _policy))));

    public IReadOnlyList<EvaluationUnitResult> OrderResults(IEnumerable<EvaluationUnitResult> results) =>
        MutationSelection.OrderResults(results, Selected.Concat(Omitted.Select(item => item.Candidate)),
            IsDiagnosticPartial, PlanFingerprint, _provenanceToken);

    public EvaluationUnitResult Reduce(EvaluationUnitResult plannedUnit,
        IReadOnlyList<StabilityAttempt> attempts)
    {
        ArgumentNullException.ThrowIfNull(plannedUnit);
        lock (_reductionLock)
        {
            _ = plannedUnit.RequireIssuedPolicy(_policy);
            if (!plannedUnit.WasIssuedBy(_provenanceToken) ||
                !string.Equals(plannedUnit.OriginatingPlanFingerprint, PlanFingerprint, StringComparison.Ordinal) ||
                plannedUnit.DiagnosticPartial != IsDiagnosticPartial ||
                !Selected.Any(candidate =>
                    string.Equals(candidate.MutationId, plannedUnit.UnitId, StringComparison.Ordinal) &&
                    string.Equals(candidate.EvaluationUnitId, plannedUnit.EvaluationUnitId, StringComparison.Ordinal)))
                throw new EvaluationContractException(
                    "Stability reduction unit does not belong to this mutation selection plan.");
            if (!_issuedCompletionDigests.TryAdd(plannedUnit.EvaluationUnitId, null))
                throw new EvaluationContractException(
                    "Each selected evaluation unit can be reduced only once by its mutation selection plan.");

            var reduced = StabilityEvidence.ReduceForPlan(plannedUnit.UnitId, plannedUnit.EvaluationUnitId,
                attempts, _policy);
            var completed = plannedUnit.CompleteFromStabilityReducer(reduced.Disposition, reduced.Evidence,
                _policy, _completionToken);
            _issuedCompletionDigests[plannedUnit.EvaluationUnitId] =
                completed.RequireTrustedCompletionDigest(_completionToken, _policy);
            return completed;
        }
    }

    public EvaluationUnitResult CompleteWithoutExecution(EvaluationUnitResult plannedUnit,
        UnitDisposition disposition, IReadOnlyList<EvaluationEvidence> evidence)
    {
        ArgumentNullException.ThrowIfNull(plannedUnit);
        if (disposition is not (UnitDisposition.Uncovered or UnitDisposition.Omitted) ||
            evidence is null || evidence.Count == 0 || evidence.Any(item => item is null))
            throw new EvaluationContractException(
                "Non-executed plan completion requires uncovered or omitted terminal evidence.");
        lock (_reductionLock)
        {
            _ = plannedUnit.RequireIssuedPolicy(_policy);
            if (!plannedUnit.WasIssuedBy(_provenanceToken) ||
                !string.Equals(plannedUnit.OriginatingPlanFingerprint, PlanFingerprint,
                    StringComparison.Ordinal) || plannedUnit.DiagnosticPartial != IsDiagnosticPartial ||
                !Selected.Any(candidate =>
                    string.Equals(candidate.MutationId, plannedUnit.UnitId, StringComparison.Ordinal) &&
                    string.Equals(candidate.EvaluationUnitId, plannedUnit.EvaluationUnitId,
                        StringComparison.Ordinal)))
                throw new EvaluationContractException(
                    "Non-executed completion unit does not belong to this mutation selection plan.");
            if (!_issuedCompletionDigests.TryAdd(plannedUnit.EvaluationUnitId, null))
                throw new EvaluationContractException(
                    "Each selected evaluation unit can be completed only once by its mutation selection plan.");
            var completed = plannedUnit.CompleteFromStabilityReducer(disposition, evidence, _policy,
                _completionToken);
            _issuedCompletionDigests[plannedUnit.EvaluationUnitId] =
                completed.RequireTrustedCompletionDigest(_completionToken, _policy);
            return completed;
        }
    }

    public EvaluationFacts FinalizeFacts(BaselineStatus baseline,
        IEnumerable<EvaluationUnitResult> results, bool allowNotApplicable,
        IReadOnlyList<EvaluationReason>? incompleteConditions = null)
    {
        var ordered = OrderResults(results);
        RequireTrustedCompletion(ordered);
        var conditions = (incompleteConditions ?? []).Concat(IncompleteConditions).Distinct().ToArray();
        return new(baseline, ordered.Count, ordered, allowNotApplicable, conditions);
    }

    public void RequireProvenPublicationEligibility(EvaluationReport report,
        EvaluationFingerprintMaterial currentMaterial)
    {
        if (IsDiagnosticPartial || !CanAdvanceFullScopeSuccess)
            throw new EvaluationContractException(
                "Diagnostic or incomplete mutation plans cannot publish proven full-scope state.");
        var currentPolicy = ExecutionPolicy.ValidateContract(currentMaterial.Policy);
        if (currentPolicy != _policy)
            throw new EvaluationContractException(
                "Proven publication policy does not match the finalizing mutation plan.");
        var candidates = Selected.Concat(Omitted.Select(item => item.Candidate)).ToArray();
        var currentEvaluationFingerprint = MutationSelection.ComputeEvaluationFingerprint(currentMaterial);
        var currentBoundPlanFingerprint = MutationSelection.ComputePlanFingerprint(candidates,
            currentEvaluationFingerprint);
        if (!string.Equals(_evaluationFingerprint, currentEvaluationFingerprint, StringComparison.Ordinal) ||
            !string.Equals(_boundPlanFingerprint, currentBoundPlanFingerprint, StringComparison.Ordinal))
            throw new EvaluationContractException(
                "Proven publication material does not match the finalizing mutation plan.");
        try
        {
            var ordered = OrderResults(report.Units);
            if (!report.Units.SequenceEqual(ordered, ReferenceEqualityComparer.Instance))
                throw new EvaluationContractException(
                    "Proven publication report units are not in canonical planned order.");
            RequireTrustedCompletion(ordered);
        }
        catch (EvaluationContractException ex)
        {
            throw new EvaluationContractException(
                "Proven publication report provenance does not match the finalizing mutation plan.", ex);
        }
    }

    private void RequireTrustedCompletion(IReadOnlyList<EvaluationUnitResult> results)
    {
        var selected = Selected.Select(item => item.EvaluationUnitId).ToHashSet(StringComparer.Ordinal);
        lock (_reductionLock)
        {
            foreach (var result in results)
            {
                if (selected.Contains(result.EvaluationUnitId))
                {
                    if (!_issuedCompletionDigests.TryGetValue(result.EvaluationUnitId, out var issuedDigest) ||
                        !result.HasTrustedCompletion(_completionToken, _policy, issuedDigest))
                        throw new EvaluationContractException(
                            "Selected evaluation units require their exact reducer-issued completion bound to the plan policy.");
                }
                else if (result.Disposition != UnitDisposition.Omitted)
                {
                    throw new EvaluationContractException(
                        "Omitted evaluation units cannot be rewritten as completed evidence.");
                }
            }
        }
    }
}

internal static class MutationSelection
{
    internal const string TargetedDiagnosticCode = "TARGETED_DIAGNOSTIC";

    public static MutationSelectionPlan Plan(BoundMutationPlan plan, int mutationCap,
        EvaluationFingerprintMaterial currentMaterial)
    {
        var policy = ExecutionPolicy.ValidateContract(currentMaterial.Policy);
        if (mutationCap != policy.MutationCap)
            throw new EvaluationContractException(
                "Legacy mutation cap must exactly match the recorded execution policy.");
        return MutationSelectionPlan.CreateFull(plan, currentMaterial);
    }

    public static MutationSelectionPlan Plan(BoundMutationPlan plan,
        EvaluationFingerprintMaterial currentMaterial) =>
        MutationSelectionPlan.CreateFull(plan, currentMaterial);

    public static BoundMutationPlan Bind(IEnumerable<MutationCandidate> candidates,
        EvaluationFingerprintMaterial currentMaterial)
    {
        var ordered = ValidateAndOrder(candidates);
        var evaluationFingerprint = ComputeEvaluationFingerprint(currentMaterial);
        return BoundMutationPlan.Create(ordered, evaluationFingerprint,
            ComputePlanFingerprint(ordered, evaluationFingerprint));
    }

    public static MutationSelectionPlan PlanTargeted(BoundMutationPlan plan,
        IEnumerable<string> requestedMutationIds, string expectedPlanFingerprint,
        EvaluationFingerprintMaterial currentMaterial) =>
        MutationSelectionPlan.CreateTargeted(plan, requestedMutationIds, expectedPlanFingerprint, currentMaterial);

    public static IReadOnlyList<EvaluationUnitResult> OrderResults(IEnumerable<EvaluationUnitResult> results) =>
        results.OrderBy(result => result, EvaluationResultComparer.Instance).ToArray();

    internal static IReadOnlyList<EvaluationUnitResult> OrderResults(IEnumerable<EvaluationUnitResult> results,
        IEnumerable<MutationCandidate> candidates, bool diagnosticPartial, string planFingerprint,
        object planToken)
    {
        var resultValues = results.ToArray();
        var duplicateResult = resultValues.GroupBy(item => item.EvaluationUnitId, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateResult is not null)
            throw new EvaluationContractException($"Duplicate evaluation unit ID: {duplicateResult.Key}.");
        var resultById = resultValues.ToDictionary(item => item.EvaluationUnitId, StringComparer.Ordinal);
        var orderedCandidates = candidates.OrderBy(item => item, MutationCandidateComparer.Instance).ToArray();
        if (resultById.Count != orderedCandidates.Length ||
            orderedCandidates.Any(item => !resultById.ContainsKey(item.EvaluationUnitId)))
            throw new EvaluationContractException("Result ledger does not match the planned evaluation units.");
        if (orderedCandidates.Any(item => !string.Equals(resultById[item.EvaluationUnitId].UnitId,
                item.MutationId, StringComparison.Ordinal)))
            throw new EvaluationContractException(
                "Result ledger mutation IDs do not match the planned evaluation units.");
        if (resultValues.Any(item => !item.WasIssuedBy(planToken) ||
                !string.Equals(item.OriginatingPlanFingerprint, planFingerprint, StringComparison.Ordinal)))
            throw new EvaluationContractException(
                "Result ledger provenance does not match the finalizing mutation plan.");
        if (resultValues.Any(item => item.DiagnosticPartial != diagnosticPartial))
            throw new EvaluationContractException(
                "Result ledger diagnostic state does not match the finalizing mutation plan.");
        return orderedCandidates.Select(item => resultById[item.EvaluationUnitId]).ToArray();
    }

    private static MutationCandidate[] ValidateAndOrder(IEnumerable<MutationCandidate> candidates)
    {
        if (candidates is null) throw new EvaluationContractException("Mutation candidates are required.");
        var values = candidates.ToArray();
        if (values.Any(item => item is null))
            throw new EvaluationContractException("Mutation candidates cannot contain null entries.");
        foreach (var context in values.GroupBy(item =>
                     (item.ProjectPath, item.TargetFramework, item.ParseContext)))
            MutationIdentity.RefuseCollisions(context.Select(item => new IdentifiedMutation(item.MutationId,
                item.Material, item.SourceStart, item.SourceLength)));
        foreach (var item in values)
        {
            string expected;
            try
            {
                expected = EvaluationUnitIdentity.Compute(new(item.MutationId, item.ProjectPath,
                    item.TargetFramework, item.ParseContext));
            }
            catch (ArgumentException ex)
            {
                throw new EvaluationContractException(
                    $"Evaluation-unit identity material is invalid: {ex.Message}", ex);
            }
            if (!EvaluationUnitIdentity.IsEvaluationUnitId(item.EvaluationUnitId) ||
                !string.Equals(item.EvaluationUnitId, expected, StringComparison.Ordinal))
                throw new EvaluationContractException(
                    "Evaluation-unit IDs must match their canonical project, target-framework, and parse material.");
        }
        var duplicate = values.GroupBy(item => item.EvaluationUnitId, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
            throw new EvaluationContractException($"Duplicate evaluation unit ID: {duplicate.Key}.");
        return values.OrderBy(item => item, MutationCandidateComparer.Instance).ToArray();
    }

    internal static string ComputeEvaluationFingerprint(EvaluationFingerprintMaterial material)
    {
        try { return EvaluationFingerprint.Compute(material); }
        catch (ArgumentException ex)
        {
            throw new EvaluationContractException(
                $"Evaluation fingerprint material is invalid: {ex.Message}", ex);
        }
    }

    internal static string ComputePlanFingerprint(IReadOnlyList<MutationCandidate> candidates,
        string evaluationFingerprint)
    {
        if (!EvaluationFingerprint.IsFingerprint(evaluationFingerprint))
            throw new EvaluationContractException("Mutation plans require a valid evaluation fingerprint.");
        var components = new List<(string Label, string Value)>
        {
            ("algorithm", MutationIdentity.Algorithm),
            ("plan-version", "1"),
            ("evaluation-fingerprint", evaluationFingerprint),
            ("candidate-count", candidates.Count.ToString(System.Globalization.CultureInfo.InvariantCulture))
        };
        for (var index = 0; index < candidates.Count; index++)
        {
            var candidate = candidates[index];
            var prefix = $"candidate-{index:D8}-";
            components.Add((prefix + "mutation", candidate.MutationId));
            components.Add((prefix + "evaluation", candidate.EvaluationUnitId));
            components.Add((prefix + "path", candidate.Material.RepositoryPath));
            components.Add((prefix + "declaration", candidate.Material.DeclarationIdentity));
            components.Add((prefix + "site", candidate.Material.StructuralSiteIdentity));
            components.Add((prefix + "operator", candidate.Material.OperatorId));
            components.Add((prefix + "operator-version", candidate.Material.OperatorVersion));
            components.Add((prefix + "replacement", candidate.Material.Replacement));
            components.Add((prefix + "project", candidate.ProjectPath));
            components.Add((prefix + "target-framework", candidate.TargetFramework));
            components.Add((prefix + "parse-context", candidate.ParseContext));
            components.Add((prefix + "source-start", candidate.SourceStart.ToString(
                System.Globalization.CultureInfo.InvariantCulture)));
            components.Add((prefix + "source-length", candidate.SourceLength.ToString(
                System.Globalization.CultureInfo.InvariantCulture)));
        }
        return "sha256:" + MutationIdentity.ComputeDigest("mutation-plan", components);
    }

    internal static MutationCandidate[] ValidateCurrentBoundPlan(BoundMutationPlan plan,
        EvaluationFingerprintMaterial currentMaterial)
    {
        var ordered = ValidateAndOrder(plan.Candidates);
        var currentEvaluationFingerprint = ComputeEvaluationFingerprint(currentMaterial);
        var currentPlanFingerprint = ComputePlanFingerprint(ordered, currentEvaluationFingerprint);
        if (!string.Equals(plan.EvaluationFingerprint, currentEvaluationFingerprint, StringComparison.Ordinal) ||
            !string.Equals(plan.PlanFingerprint, currentPlanFingerprint, StringComparison.Ordinal))
            throw new EvaluationContractException(
                "Mutation selection requires a plan bound to the current evaluation material.");
        return ordered;
    }

    internal static string ComputeSelectionPlanFingerprint(string kind,
        IReadOnlyList<MutationCandidate> candidates, IReadOnlyList<MutationCandidate> selected,
        IReadOnlyList<MutationOmission> omitted, string? boundPlanFingerprint, EvaluationPolicy policy)
    {
        var components = new List<(string Label, string Value)>
        {
            ("selection-version", "2"),
            ("kind", kind),
            ("bound-plan", boundPlanFingerprint ?? "none"),
            ("policy-max-workers", policy.MaxWorkers.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            ("policy-mutation-cap", policy.MutationCap.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            ("policy-baseline-timeout", policy.BaselineTimeoutSeconds.ToString(
                System.Globalization.CultureInfo.InvariantCulture)),
            ("policy-mutant-timeout", policy.MutantTimeoutSeconds.ToString(
                System.Globalization.CultureInfo.InvariantCulture)),
            ("policy-overall-deadline", policy.OverallDeadlineSeconds.ToString(
                System.Globalization.CultureInfo.InvariantCulture)),
            ("policy-allow-not-applicable", policy.AllowNotApplicable ? "true" : "false"),
            ("policy-stability-repetitions", policy.StabilityRepetitions.ToString(
                System.Globalization.CultureInfo.InvariantCulture)),
            ("candidate-count", candidates.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            ("selected-count", selected.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            ("omitted-count", omitted.Count.ToString(System.Globalization.CultureInfo.InvariantCulture))
        };
        for (var index = 0; index < candidates.Count; index++)
            components.Add(($"candidate-{index:D8}", candidates[index].EvaluationUnitId));
        for (var index = 0; index < selected.Count; index++)
            components.Add(($"selected-{index:D8}", selected[index].EvaluationUnitId));
        for (var index = 0; index < omitted.Count; index++)
        {
            components.Add(($"omitted-{index:D8}-unit", omitted[index].Candidate.EvaluationUnitId));
            components.Add(($"omitted-{index:D8}-reason", omitted[index].ReasonCode));
        }
        return "sha256:" + MutationIdentity.ComputeDigest("mutation-selection-plan", components);
    }

    private sealed class MutationCandidateComparer : IComparer<MutationCandidate>
    {
        public static MutationCandidateComparer Instance { get; } = new();

        public int Compare(MutationCandidate? x, MutationCandidate? y)
        {
            if (ReferenceEquals(x, y)) return 0;
            if (x is null) return -1;
            if (y is null) return 1;
            var result = Compare(x.Material.RepositoryPath, y.Material.RepositoryPath);
            if (result != 0) return result;
            result = Compare(x.Material.DeclarationIdentity, y.Material.DeclarationIdentity);
            if (result != 0) return result;
            result = Compare(x.Material.StructuralSiteIdentity, y.Material.StructuralSiteIdentity);
            if (result != 0) return result;
            result = Compare(x.Material.OperatorId, y.Material.OperatorId);
            if (result != 0) return result;
            result = Compare(x.Material.OperatorVersion, y.Material.OperatorVersion);
            if (result != 0) return result;
            result = Compare(x.Material.Replacement, y.Material.Replacement);
            if (result != 0) return result;
            result = Compare(x.ProjectPath, y.ProjectPath);
            if (result != 0) return result;
            result = Compare(x.TargetFramework, y.TargetFramework);
            if (result != 0) return result;
            result = Compare(x.ParseContext, y.ParseContext);
            if (result != 0) return result;
            result = Compare(x.MutationId, y.MutationId);
            return result != 0 ? result : Compare(x.EvaluationUnitId, y.EvaluationUnitId);
        }

        private static int Compare(string left, string right) => StringComparer.Ordinal.Compare(left, right);
    }

    private sealed class EvaluationResultComparer : IComparer<EvaluationUnitResult>
    {
        public static EvaluationResultComparer Instance { get; } = new();

        public int Compare(EvaluationUnitResult? x, EvaluationUnitResult? y)
        {
            if (ReferenceEquals(x, y)) return 0;
            if (x is null) return -1;
            if (y is null) return 1;
            return StringComparer.Ordinal.Compare(x.EvaluationUnitId, y.EvaluationUnitId);
        }
    }
}
