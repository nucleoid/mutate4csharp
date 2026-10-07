namespace Mutate4CSharp.Tests;

public sealed class StabilityEvidenceTests
{
    [Fact]
    public void CompatibleAttemptsPreserveEveryAttemptAndConcludeNormally()
    {
        var result = StabilityEvidence.Reduce(MutationId, EvaluationId,
        [
            Attempt(1, UnitDisposition.Killed),
            Attempt(2, UnitDisposition.Killed)
        ], Policy(2));

        Assert.Equal(UnitDisposition.Killed, result.Disposition);
        Assert.Equal(2, result.Evidence.Count(item => item.Kind == "STABILITY_ATTEMPT"));
    }

    [Fact]
    public void AlternatingOutcomeIsUnstableAndForcesIncomplete()
    {
        var unit = StabilityEvidence.Reduce(MutationId, EvaluationId,
        [
            Attempt(1, UnitDisposition.Killed),
            Attempt(2, UnitDisposition.Survived),
            Attempt(3, UnitDisposition.Killed)
        ], Policy(3));
        var decision = EvaluationReducer.Reduce(new(BaselineStatus.Green, 1, [unit], false, []));

        Assert.Equal(UnitDisposition.Unstable, unit.Disposition);
        Assert.Equal(3, unit.Evidence.Count(item => item.Kind == "STABILITY_ATTEMPT"));
        Assert.Equal(EvaluationOutcome.Incomplete, decision.Outcome);
        Assert.Contains(decision.Reasons, reason => reason.Code == "UNSTABLE_EVIDENCE");
    }

    [Fact]
    public void RefusesMissingDuplicateOrNonTerminalAttempts()
    {
        Assert.Throws<EvaluationContractException>(() => StabilityEvidence.Reduce(MutationId, EvaluationId, [], Policy(1)));
        Assert.Throws<EvaluationContractException>(() => StabilityEvidence.Reduce(MutationId, EvaluationId,
            [Attempt(1, UnitDisposition.Killed), Attempt(1, UnitDisposition.Killed)], Policy(2)));
        Assert.Throws<EvaluationContractException>(() => StabilityEvidence.Reduce(MutationId, EvaluationId,
            [Attempt(1, UnitDisposition.Pending)], Policy(1)));
    }

    private static StabilityAttempt Attempt(int number, UnitDisposition disposition) =>
        new(number, disposition, [new("TEST_RUN", $"Attempt {number} observed {disposition}.")]);

    private static EvaluationPolicy Policy(int repetitions) =>
        EvaluationReport.DefaultPolicy with { StabilityRepetitions = repetitions };

    private static string MutationId => "mutation:v1:" + new string('a', 64);
    private static string EvaluationId => "evaluation:v1:" + new string('b', 64);
}
