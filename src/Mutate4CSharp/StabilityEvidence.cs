namespace Mutate4CSharp;

internal sealed record StabilityAttempt(int Attempt, UnitDisposition Disposition,
    IReadOnlyList<EvaluationEvidence> Evidence);

internal static class StabilityEvidence
{
    public const int MaxUnitEvidence = 200;

    public static EvaluationUnitResult Reduce(string mutationId, string evaluationUnitId,
        IReadOnlyList<StabilityAttempt> attempts, EvaluationPolicy policy)
        => ReduceCore(mutationId, evaluationUnitId, attempts, policy);

    internal static EvaluationUnitResult ReduceForPlan(string mutationId, string evaluationUnitId,
        IReadOnlyList<StabilityAttempt> attempts, EvaluationPolicy policy) =>
        ReduceCore(mutationId, evaluationUnitId, attempts, policy);

    private static EvaluationUnitResult ReduceCore(string mutationId, string evaluationUnitId,
        IReadOnlyList<StabilityAttempt> attempts, EvaluationPolicy policy)
    {
        if (!MutationIdentity.IsMutationId(mutationId) ||
            !EvaluationUnitIdentity.IsEvaluationUnitId(evaluationUnitId))
            throw new EvaluationContractException(
                "Stability evidence requires canonical mutation and evaluation-unit IDs.");
        ExecutionPolicy.ValidateContract(policy);
        if (attempts is null)
            throw new EvaluationContractException("Stability attempts are required.");
        if (attempts.Count != policy.StabilityRepetitions)
            throw new EvaluationContractException(
                $"Stability evidence requires exactly {policy.StabilityRepetitions} attempts.");
        if (attempts.Any(item => item is null || item.Evidence is null ||
                                 item.Evidence.Any(entry => entry is null)))
            throw new EvaluationContractException("Stability attempts and evidence cannot contain null entries.");
        if (attempts.Select(item => item.Attempt).Distinct().Count() != attempts.Count ||
            !attempts.OrderBy(item => item.Attempt).Select(item => item.Attempt)
                .SequenceEqual(Enumerable.Range(1, attempts.Count)))
            throw new EvaluationContractException("Stability attempts must be unique and contiguous from one.");
        if (attempts.Any(item => item.Disposition is UnitDisposition.Pending or UnitDisposition.Omitted or
                                 UnitDisposition.Unstable || item.Evidence.Count is 0 or > 20))
            throw new EvaluationContractException("Stability attempts require terminal executed evidence.");

        var dispositions = attempts.Select(item => item.Disposition).Distinct().ToArray();
        var disposition = dispositions.Length == 1 ? dispositions[0] : UnitDisposition.Unstable;
        var generated = attempts.OrderBy(item => item.Attempt).SelectMany(item => item.Evidence.Select(entry =>
        {
            var diagnostics = new List<string>
            {
                $"attempt={item.Attempt}",
                $"disposition={item.Disposition.ToString().ToUpperInvariant()}",
                Bound($"sourceKind={entry.Kind}", 512)
            };
            diagnostics.AddRange((entry.Diagnostics ?? []).Take(17).Select(value => Bound(value, 512)));
            return new EvaluationEvidence("STABILITY_ATTEMPT",
                Bound($"Attempt {item.Attempt} observed {item.Disposition.ToString().ToUpperInvariant()}: {entry.Summary}",
                    1024), diagnostics);
        })).ToArray();

        var reserve = disposition == UnitDisposition.Unstable ? 1 : 0;
        var evidence = BoundTotal(generated, MaxUnitEvidence - reserve);
        if (disposition == UnitDisposition.Unstable)
            evidence.Add(new("UNSTABLE_EVIDENCE",
                "Identical-input repetitions produced incompatible outcomes; the evidence is inconclusive."));
        return new(mutationId, evaluationUnitId, disposition, evidence);
    }

    private static List<EvaluationEvidence> BoundTotal(IReadOnlyList<EvaluationEvidence> evidence, int maximum)
    {
        if (evidence.Count <= maximum) return evidence.ToList();
        var retained = maximum - 1;
        var first = retained / 2;
        var last = retained - first;
        return evidence.Take(first).Concat([
                new EvaluationEvidence("STABILITY_EVIDENCE_TRUNCATED",
                    $"Retained {retained} of {evidence.Count} bounded stability evidence entries.",
                    [$"retained={retained}", $"omitted={evidence.Count - retained}"])
            ]).Concat(evidence.TakeLast(last)).ToList();
    }

    private static string Bound(string? value, int length)
    {
        if (string.IsNullOrWhiteSpace(value)) return "(empty)";
        return value.Length <= length ? value : value[..length];
    }
}
