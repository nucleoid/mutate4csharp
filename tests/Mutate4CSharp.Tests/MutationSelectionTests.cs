namespace Mutate4CSharp.Tests;

public sealed class MutationSelectionTests
{
    [Fact]
    public void SelectionAndReportOrderAreCanonicalAcrossInputAndCompletionOrder()
    {
        var candidates = new[]
        {
            Candidate("src/z.cs", "Type.B", "site/2", "op", "eval-z"),
            Candidate("src/A.cs", "Type.A", "site/10", "op", "eval-a10"),
            Candidate("src/A.cs", "Type.A", "site/2", "op", "eval-a2")
        };

        var material = FingerprintMaterial("ordering");
        var forward = MutationSelection.Plan(MutationSelection.Bind(candidates, material), material);
        var reverse = MutationSelection.Plan(MutationSelection.Bind(candidates.Reverse(), material), material);

        Assert.Equal(forward.Selected.Select(item => item.EvaluationUnitId),
            reverse.Selected.Select(item => item.EvaluationUnitId));
        Assert.Equal(["src/A.cs", "src/A.cs", "src/z.cs"],
            forward.Selected.Select(item => item.Material.RepositoryPath));

        var completed = forward.CreatePendingLedger().Reverse()
            .Select(item => item with { Disposition = UnitDisposition.Killed });
        Assert.Equal(forward.Selected.Select(item => item.EvaluationUnitId),
            forward.OrderResults(completed).Select(item => item.EvaluationUnitId));
    }

    [Fact]
    public void BudgetIsDeterministicAndEveryOmissionIsExplicitlyIncomplete()
    {
        var candidates = Enumerable.Range(0, 102)
            .Select(index => Candidate($"src/{index:D3}.cs", "Type.M", "site/1", "op", $"eval-{index:D3}"))
            .Reverse();

        var material = FingerprintMaterial("budget");
        var plan = MutationSelection.Plan(MutationSelection.Bind(candidates, material), material);
        var ledger = plan.CreatePendingLedger();
        var final = ledger.Select(unit => unit.Disposition == UnitDisposition.Pending
            ? unit with { Disposition = UnitDisposition.Killed }
            : unit).ToArray();
        var decision = EvaluationReducer.Reduce(new(BaselineStatus.Green, final.Length, final, false, []));

        Assert.Equal(100, plan.Selected.Count);
        Assert.Equal(2, plan.Omitted.Count);
        Assert.All(plan.Omitted, item => Assert.Equal("BUDGET_OMITTED", item.ReasonCode));
        Assert.Equal(102, final.Length);
        Assert.Equal(EvaluationOutcome.Incomplete, decision.Outcome);
        Assert.Equal(2, decision.Counts.Omitted);
    }

    [Fact]
    public void TargetedRerunRequiresFreshMatchingFingerprintAndNeverAdvancesFullScope()
    {
        var fingerprintMaterial = FingerprintMaterial("targeted");
        var candidates = new[]
        {
            Candidate("src/A.cs", "Type.A", "site/1", "op", "eval-a"),
            Candidate("src/B.cs", "Type.B", "site/1", "op", "eval-b")
        };
        var target = candidates[1].MutationId;

        var bound = MutationSelection.Bind(candidates, fingerprintMaterial);
        var plan = MutationSelection.PlanTargeted(bound, [target], bound.PlanFingerprint, fingerprintMaterial);

        Assert.True(plan.IsDiagnosticPartial);
        Assert.False(plan.CanAdvanceFullScopeSuccess);
        Assert.Single(plan.Selected);
        Assert.Equal("src/B.cs", plan.Selected[0].Material.RepositoryPath);
        Assert.Single(plan.Omitted);
        Assert.Equal("TARGET_NOT_REQUESTED", plan.Omitted[0].ReasonCode);
        Assert.Throws<EvaluationContractException>(() => MutationSelection.PlanTargeted(bound, [target],
            "sha256:" + new string('b', 64), fingerprintMaterial));
        Assert.Throws<EvaluationContractException>(() => MutationSelection.PlanTargeted(bound,
            ["mutation:v1:" + new string('f', 64)], bound.PlanFingerprint, fingerprintMaterial));
    }

    [Fact]
    public void DuplicateEvaluationUnitsAreRefusedInsteadOfSilentlyMerged()
    {
        var first = Candidate("src/A.cs", "Type.A", "site/1", "op", "same");
        var second = first;

        var material = FingerprintMaterial("duplicate");
        Assert.Throws<EvaluationContractException>(() => MutationSelection.Bind([first, second], material));
    }

    [Fact]
    public void PathOrderingIsCaseSensitiveOrdinalRatherThanCultureSensitive()
    {
        var ascii = Candidate("src/I.cs", "Type.M", "site/1", "op", "ascii");
        var dotless = Candidate("src/ı.cs", "Type.M", "site/1", "op", "dotless");

        var material = FingerprintMaterial("path-ordering");
        var plan = MutationSelection.Plan(MutationSelection.Bind([dotless, ascii], material), material);

        Assert.Equal(["src/I.cs", "src/ı.cs"], plan.Selected.Select(item => item.Material.RepositoryPath));
    }

    private static MutationCandidate Candidate(string path, string declaration, string site,
        string op, string evaluationId)
    {
        var material = new MutationIdentityMaterial(path, declaration, site, op, "1", "replacement");
        var mutationId = MutationIdentity.Compute(material);
        const string project = "src/App.csproj";
        const string target = "net10.0";
        var parse = $"case={evaluationId}";
        return new(mutationId, EvaluationUnitIdentity.Compute(new(mutationId, project, target, parse)), material,
            project, target, parse);
    }

    private static EvaluationFingerprintMaterial FingerprintMaterial(string value) => new(
        [EvaluationFingerprint.FromBytes("source", "src/A.cs", System.Text.Encoding.UTF8.GetBytes(value))],
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(value))).ToLowerInvariant(),
        "scope", "configuration", "tool", "1", "sdk", "runtime", "runner",
        EvaluationReport.DefaultPolicy);

    private static EvaluationUnitResult Unit(MutationCandidate candidate, UnitDisposition disposition) =>
        new(candidate.MutationId, candidate.EvaluationUnitId, disposition,
            [new("ATTEMPT", "Completed test attempt.")]);
}
