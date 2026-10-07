using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis.CSharp;

namespace Mutate4CSharp.Tests;

public sealed class AdversarialRoundFiveTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "mutate4csharp-round-five",
        Guid.NewGuid().ToString("N"));

    public AdversarialRoundFiveTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public void FullPlanFingerprintChangesWithEvaluationMaterialAndRejectsTheStaleLedger()
    {
        var candidate = Candidate();
        var firstMaterial = FingerprintMaterial("first");
        var secondMaterial = FingerprintMaterial("second");
        var firstBound = MutationSelection.Bind([candidate], firstMaterial);
        var secondBound = MutationSelection.Bind([candidate], secondMaterial);
        var firstPlan = MutationSelection.Plan(firstBound, firstMaterial);
        var secondPlan = MutationSelection.Plan(secondBound, secondMaterial);
        var stale = Complete(firstPlan.CreatePendingLedger());

        Assert.NotEqual(firstPlan.PlanFingerprint, secondPlan.PlanFingerprint);
        Assert.Throws<EvaluationContractException>(() =>
            secondPlan.FinalizeFacts(BaselineStatus.Green, stale, false));
    }

    [Fact]
    public void FullPlanConstructionRequiresABoundPlanAndCurrentEvaluationMaterial()
    {
        var methods = typeof(MutationSelection).GetMethods()
            .Where(method => method.Name == nameof(MutationSelection.Plan)).ToArray();

        Assert.DoesNotContain(methods, method => method.GetParameters() is
            [{ ParameterType: var candidates }, { ParameterType: var cap }] &&
            candidates == typeof(IEnumerable<MutationCandidate>) && cap == typeof(int));
        Assert.Contains(methods, method => method.GetParameters() is
            [{ ParameterType: var plan }, { ParameterType: var cap }, { ParameterType: var material }] &&
            plan == typeof(BoundMutationPlan) && cap == typeof(int) &&
            material == typeof(EvaluationFingerprintMaterial));
    }

    [Fact]
    public void ProvenPublicationItselfRefusesARewrittenTargetedLedgerWithoutCreatingState()
    {
        var candidate = Candidate();
        var material = FingerprintMaterial("publication");
        var bound = MutationSelection.Bind([candidate], material);
        var targeted = MutationSelection.PlanTargeted(bound, [candidate.MutationId],
            bound.PlanFingerprint, material);
        var full = MutationSelection.Plan(bound, material);
        var targetedUnit = targeted.CreatePendingLedger().Single();
        EvaluationUnitResult[] rewritten =
        [
            new(targetedUnit.UnitId, targetedUnit.EvaluationUnitId, UnitDisposition.Killed,
                [new("MUTANT_KILLED", "Fresh suite killed the mutation.")])
        ];
        var facts = new EvaluationFacts(BaselineStatus.Green, rewritten.Length, rewritten, false, []);
        var decision = EvaluationReducer.Reduce(facts);
        var report = Report(facts, decision, material.SnapshotId);
        var reportBytes = ReportWriter.Serialize(report);
        var fingerprint = EvaluationFingerprint.ComputeForProven(material);
        var coverage = new CoverageProvenance("1", report.RunId, "suite-a", fingerprint,
            material.SnapshotId, BaselineStatus.Green, Digest("coverage"), 8,
            "path-map-v1", "vstest-v1", true);
        var record = new ProvenEvaluationSidecar("1", SidecarRecordKind.Proven, report.RunId,
            DateTimeOffset.UnixEpoch, fingerprint, material.SnapshotId,
            Convert.ToHexString(SHA256.HashData(reportBytes)).ToLowerInvariant(), reportBytes.LongLength,
            true, [coverage], report.Counts);
        var store = new SidecarStore(_directory);

        var exception = Assert.Throws<EvaluationContractException>(() =>
            store.PublishProven(record, report, material, full));

        Assert.Contains("finalizing mutation plan", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(store.ProvenPath(fingerprint)));
    }

    private static EvaluationUnitResult[] Complete(IEnumerable<EvaluationUnitResult> units) =>
        units.Select(unit => unit with
        {
            Disposition = UnitDisposition.Killed,
            Evidence = [new("MUTANT_KILLED", "Fresh suite killed the mutation.")]
        }).ToArray();

    private static EvaluationReport Report(EvaluationFacts facts, EvaluationDecision decision,
        string snapshotId) => new("1", "round-five-publication", DateTimeOffset.UnixEpoch, "check",
        new("inputs", null, ["src/A.cs"]), ScopePlan.Empty("inputs", ".", null),
        EvaluationReport.DefaultPolicy, facts.Baseline,
        [new("suite-a", BaselineStatus.Green, [new("BASELINE_GREEN", "Fresh baseline passed.")])],
        facts.Units, decision.Counts, facts.IncompleteConditions, decision.Reasons,
        decision.Evidence.Concat([new EvaluationEvidence("INPUT_SNAPSHOT", "Frozen input snapshot.",
            [$"captureId={snapshotId}"])]).ToArray(), decision.Outcome, decision.ExitCode);

    private static MutationCandidate Candidate()
    {
        const string source = "class C { bool M(int value) => value == 1; }";
        var tree = CSharpSyntaxTree.ParseText(source);
        var token = tree.GetRoot().DescendantTokens().Single(item => item.Text == "==");
        var mutation = MutationIdentity.Create("src/A.cs", tree.GetRoot(), token.Span,
            "binary.equal", "1", "!=");
        const string project = "src/App.csproj";
        const string target = "net10.0";
        const string parse = "symbols=A";
        return new(mutation.MutationId,
            EvaluationUnitIdentity.Compute(new(mutation.MutationId, project, target, parse)),
            mutation.Material, project, target, parse, mutation.SourceStart, mutation.SourceLength);
    }

    private static EvaluationFingerprintMaterial FingerprintMaterial(string value)
    {
        var scope = ScopePlan.Empty("inputs", ".", null);
        return new([
                EvaluationFingerprint.FromBytes("source", "src/A.cs", Encoding.UTF8.GetBytes(value)),
                EvaluationFingerprint.FromBytes("dependency", "packages.lock.json", "dependency"u8)
            ], Digest(value), ReportWriter.SerializeCanonicalScope(scope), "configuration-v1", "tool-v1",
            "operator-v1", "sdk-v1", "runtime-v1", "vstest-v1", EvaluationReport.DefaultPolicy,
            ProvenanceComplete: true);
    }

    private static string Digest(string value) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch { }
    }
}
