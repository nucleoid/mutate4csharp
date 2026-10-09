namespace Mutate4CSharp;

internal enum EvaluationOutcome { Pass, Fail, Incomplete, NotApplicable }
internal enum BaselineStatus { Green, Red, Empty, Unknown }
internal enum UnitDisposition
{
    Killed,
    Survived,
    Uncovered,
    CompileInvalid,
    Error,
    Omitted,
    Unstable,
    Pending
}

internal sealed class EvaluationContractException : Exception
{
    public EvaluationContractException(string message) : base(message) { }
    public EvaluationContractException(string message, Exception innerException) : base(message, innerException) { }
}

internal static class EvaluationReducer
{
    public static EvaluationDecision Reduce(EvaluationFacts facts)
    {
        Validate(facts);
        var reasons = new List<EvaluationReason>(facts.IncompleteConditions);
        var evidence = facts.Units.SelectMany(unit => unit.Evidence).ToList();
        var counts = EvaluationCounts.From(facts.EnumerationCount, facts.Units);
        var targetedDiagnostic = facts.DiagnosticPartial;
        if (targetedDiagnostic)
            AddReason(reasons, MutationSelection.TargetedDiagnosticCode,
                "Targeted reruns are diagnostic and cannot prove full-scope success.");

        AddBaselineReason(facts.Baseline, reasons);
        if (facts.EnumerationCount is null)
            AddReason(reasons, "ENUMERATION_INCOMPLETE", "The mutation-site total is unknown.");

        foreach (var unit in facts.Units)
        {
            switch (unit.Disposition)
            {
                case UnitDisposition.Survived:
                    reasons.Add(new("SURVIVED_MUTATION", $"Evaluation unit {unit.UnitId} survived."));
                    break;
                case UnitDisposition.Uncovered:
                    reasons.Add(new("UNCOVERED_MUTATION", $"Evaluation unit {unit.UnitId} is known to be uncovered."));
                    break;
                case UnitDisposition.Error:
                    reasons.Add(new("UNIT_ERROR", $"Evaluation unit {unit.UnitId} did not produce conclusive evidence."));
                    break;
                case UnitDisposition.Omitted:
                    reasons.Add(new("UNIT_OMITTED", $"Evaluation unit {unit.UnitId} was omitted."));
                    break;
                case UnitDisposition.Unstable:
                    reasons.Add(new("UNSTABLE_EVIDENCE",
                        $"Evaluation unit {unit.UnitId} produced incompatible repeated evidence."));
                    break;
            }
        }

        var baselineIncomplete = facts.Baseline is not BaselineStatus.Green;
        var otherIncomplete = targetedDiagnostic || facts.IncompleteConditions.Count > 0 ||
            facts.EnumerationCount is null || facts.Units.Any(unit =>
            unit.Disposition is UnitDisposition.Error or UnitDisposition.Omitted or UnitDisposition.Unstable);
        var policyFailed = facts.Units.Any(unit =>
            unit.Disposition is UnitDisposition.Survived or UnitDisposition.Uncovered);
        var effectiveCandidates = facts.Units.Count(unit =>
            unit.Disposition is not (UnitDisposition.CompileInvalid or UnitDisposition.Omitted));

        EvaluationOutcome outcome;
        int exitCode;
        if (baselineIncomplete || otherIncomplete)
        {
            outcome = EvaluationOutcome.Incomplete;
            exitCode = facts.Baseline is BaselineStatus.Red or BaselineStatus.Empty ? 2 : 4;
        }
        else if (policyFailed)
        {
            outcome = EvaluationOutcome.Fail;
            exitCode = 3;
        }
        else if (effectiveCandidates == 0)
        {
            outcome = EvaluationOutcome.NotApplicable;
            exitCode = facts.AllowNotApplicable ? 0 : 5;
            AddReason(reasons, "NO_EFFECTIVE_CANDIDATES", "No effective valid mutation candidates were available.");
        }
        else
        {
            outcome = EvaluationOutcome.Pass;
            exitCode = 0;
        }

        if (evidence.Count == 0)
            evidence.Add(new("SYNTHETIC_OUTCOME", $"Reducer produced {outcome.ToString().ToUpperInvariant()} from the recorded ledger."));
        return new(outcome, exitCode, counts, reasons, evidence);
    }

    private static void Validate(EvaluationFacts facts)
    {
        if (facts.EnumerationCount is < 0)
            throw new EvaluationContractException("Enumeration count cannot be negative.");
        if (facts.EnumerationCount is int count && count != facts.Units.Count)
            throw new EvaluationContractException($"Ledger count {facts.Units.Count} does not match enumerated count {count}.");
        var duplicate = facts.Units.GroupBy(unit => unit.EvaluationUnitId, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
            throw new EvaluationContractException($"Duplicate evaluation unit ID: {duplicate.Key}.");
        if (facts.Units.Any(unit => unit.Disposition == UnitDisposition.Pending))
            throw new EvaluationContractException("Final reports cannot contain pending units.");
        if (facts.Units.Any(unit => !Enum.IsDefined(unit.Disposition)))
            throw new EvaluationContractException("Final reports contain an unknown unit disposition.");
        if (facts.Units.Any(unit => string.IsNullOrWhiteSpace(unit.UnitId) || string.IsNullOrWhiteSpace(unit.EvaluationUnitId)))
            throw new EvaluationContractException("Every ledger entry requires mutation and evaluation unit IDs.");
        if (facts.Units.Any(unit => !MutationIdentity.IsMutationId(unit.UnitId) ||
                                    !EvaluationUnitIdentity.IsEvaluationUnitId(unit.EvaluationUnitId)))
            throw new EvaluationContractException("Every ledger entry requires canonical mutation and evaluation-unit IDs.");
        if (facts.Units.Any(unit => unit.UnitId.Length > 512 || unit.EvaluationUnitId.Length > 512))
            throw new EvaluationContractException("Evaluation unit IDs cannot exceed 512 characters.");
        if (facts.Units.Any(unit => unit.Evidence is null || unit.Evidence.Count == 0 ||
                                    unit.Evidence.Any(item => item is null)))
            throw new EvaluationContractException("Every ledger entry requires evidence.");
    }

    private static void AddBaselineReason(BaselineStatus status, List<EvaluationReason> reasons)
    {
        if (status != BaselineStatus.Green)
            AddReason(reasons, $"BASELINE_{status.ToString().ToUpperInvariant()}",
                $"The baseline status is {status.ToString().ToUpperInvariant()}.");
    }

    private static void AddReason(List<EvaluationReason> reasons, string code, string message)
    {
        if (reasons.All(reason => reason.Code != code)) reasons.Add(new(code, message));
    }
}
