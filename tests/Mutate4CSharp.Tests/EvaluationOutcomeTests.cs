namespace Mutate4CSharp.Tests;

public sealed class EvaluationOutcomeTests
{
    [Theory]
    [InlineData((int)UnitDisposition.Killed, (int)EvaluationOutcome.Pass, 0)]
    [InlineData((int)UnitDisposition.Survived, (int)EvaluationOutcome.Fail, 3)]
    [InlineData((int)UnitDisposition.Uncovered, (int)EvaluationOutcome.Fail, 3)]
    [InlineData((int)UnitDisposition.Error, (int)EvaluationOutcome.Incomplete, 4)]
    [InlineData((int)UnitDisposition.Omitted, (int)EvaluationOutcome.Incomplete, 4)]
    public void ReducesCompletedAndIncompleteUnits(int dispositionValue, int expectedValue, int exitCode)
    {
        var disposition = (UnitDisposition)dispositionValue;
        var expected = (EvaluationOutcome)expectedValue;
        var decision = EvaluationReducer.Reduce(Facts([Unit("unit-1", disposition)]));

        Assert.Equal(expected, decision.Outcome);
        Assert.Equal(exitCode, decision.ExitCode);
        Assert.NotEmpty(decision.Evidence);
    }

    [Theory]
    [InlineData((int)BaselineStatus.Red)]
    [InlineData((int)BaselineStatus.Empty)]
    public void RedOrEmptyBaselineIsIncompleteWithDedicatedExit(int statusValue)
    {
        var status = (BaselineStatus)statusValue;
        var decision = EvaluationReducer.Reduce(Facts([Unit("unit-1", UnitDisposition.Survived)], status));

        Assert.Equal(EvaluationOutcome.Incomplete, decision.Outcome);
        Assert.Equal(2, decision.ExitCode);
        Assert.Contains(decision.Reasons, reason => reason.Code == $"BASELINE_{status.ToString().ToUpperInvariant()}");
        Assert.Contains(decision.Reasons, reason => reason.Code == "SURVIVED_MUTATION");
    }

    [Fact]
    public void IncompleteTakesPrecedenceButRetainsPolicyFailures()
    {
        var facts = Facts([
            Unit("survivor", UnitDisposition.Survived),
            Unit("error", UnitDisposition.Error)
        ]);

        var decision = EvaluationReducer.Reduce(facts);

        Assert.Equal(EvaluationOutcome.Incomplete, decision.Outcome);
        Assert.Equal(4, decision.ExitCode);
        Assert.Contains(decision.Reasons, reason => reason.Code == "SURVIVED_MUTATION");
        Assert.Contains(decision.Reasons, reason => reason.Code == "UNIT_ERROR");
    }

    [Fact]
    public void RetainsAReasonForEveryAffectedUnit()
    {
        var decision = EvaluationReducer.Reduce(Facts([
            Unit("survivor-1", UnitDisposition.Survived),
            Unit("survivor-2", UnitDisposition.Survived),
            Unit("error-1", UnitDisposition.Error),
            Unit("error-2", UnitDisposition.Error)
        ]));

        Assert.Equal(2, decision.Reasons.Count(reason => reason.Code == "SURVIVED_MUTATION"));
        Assert.Equal(2, decision.Reasons.Count(reason => reason.Code == "UNIT_ERROR"));
    }

    [Fact]
    public void ZeroSitesAndAllCompileInvalidAreNotApplicable()
    {
        var zero = EvaluationReducer.Reduce(Facts([], enumerationCount: 0));
        var invalid = EvaluationReducer.Reduce(Facts([Unit("invalid", UnitDisposition.CompileInvalid)]));

        Assert.Equal(EvaluationOutcome.NotApplicable, zero.Outcome);
        Assert.Equal(5, zero.ExitCode);
        Assert.Equal(EvaluationOutcome.NotApplicable, invalid.Outcome);
        Assert.Equal(0, EvaluationReducer.Reduce(Facts([], enumerationCount: 0, allowNotApplicable: true)).ExitCode);
    }

    [Fact]
    public void UnknownEnumerationNeverMeansZeroSites()
    {
        var decision = EvaluationReducer.Reduce(new(BaselineStatus.Green, null, [], false, []));

        Assert.Equal(EvaluationOutcome.Incomplete, decision.Outcome);
        Assert.Contains(decision.Reasons, reason => reason.Code == "ENUMERATION_INCOMPLETE");
    }

    [Fact]
    public void RunLevelIncompleteConditionOverridesOtherwiseCompletePass()
    {
        var facts = Facts([Unit("killed", UnitDisposition.Killed)]) with
        {
            IncompleteConditions = [new("SNAPSHOT_UNAVAILABLE", "Frozen input was unavailable.")]
        };

        var decision = EvaluationReducer.Reduce(facts);

        Assert.Equal(EvaluationOutcome.Incomplete, decision.Outcome);
        Assert.Equal(4, decision.ExitCode);
    }

    [Fact]
    public void RejectsOversizedStableIdentifiers()
    {
        Assert.Throws<EvaluationContractException>(() => EvaluationReducer.Reduce(Facts([
            Unit(new string('x', 513), UnitDisposition.Killed)
        ])));
    }

    [Fact]
    public void RejectsDuplicatePendingAndMisaccountedLedgers()
    {
        Assert.Throws<EvaluationContractException>(() => EvaluationReducer.Reduce(Facts([
            Unit("same", UnitDisposition.Killed), Unit("same", UnitDisposition.Killed)
        ])));
        Assert.Throws<EvaluationContractException>(() => EvaluationReducer.Reduce(Facts([
            Unit("pending", UnitDisposition.Pending)
        ])));
        Assert.Throws<EvaluationContractException>(() => EvaluationReducer.Reduce(Facts([
            Unit("one", UnitDisposition.Killed)
        ], enumerationCount: 2)));
    }

    private static EvaluationFacts Facts(IReadOnlyList<EvaluationUnitResult> units,
        BaselineStatus baseline = BaselineStatus.Green, int? enumerationCount = null,
        bool allowNotApplicable = false) => new(
            baseline,
            enumerationCount ?? (units.Count == 0 ? 0 : units.Count),
            units,
            allowNotApplicable,
            []);

    private static EvaluationUnitResult Unit(string id, UnitDisposition disposition)
    {
        if (id.Length > 512)
            return new(id, id, disposition, [new("SYNTHETIC", $"Evidence for {id}")]);
        var digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(id))).ToLowerInvariant();
        return new("mutation:v1:" + digest, "evaluation:v1:" + digest, disposition,
            [new("SYNTHETIC", $"Evidence for {id}")]);
    }
}
