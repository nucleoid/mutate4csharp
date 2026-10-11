using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;
using Microsoft.CodeAnalysis.CSharp;

namespace Mutate4CSharp.Tests;

public sealed class AdversarialRoundThreeTests : IDisposable
{
    private static readonly JsonSchema ReportSchema = LoadReportSchema();
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "mutate4csharp-round-three",
        Guid.NewGuid().ToString("N"));

    public AdversarialRoundThreeTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public void TargetedStabilityPipelineCannotReachPassOrPublishProvenState()
    {
        var scope = ScopePlan.Empty("inputs", ".", null);
        var snapshotId = Digest("snapshot");
        var material = FingerprintMaterial(snapshotId, scope);
        var candidate = Candidate(Identity("class C { bool M(int value) => value == 1; }", "=="),
            "symbols=A", 34, 2);
        var bound = MutationSelection.Bind([candidate], material);
        var plan = MutationSelection.PlanTargeted(bound, [candidate.MutationId], bound.PlanFingerprint, material);
        var executed = plan.Reduce(plan.CreatePendingLedger().Single(),
            [new(1, UnitDisposition.Killed, [new("MUTANT_KILLED", "Fresh suite killed the mutation.")])]);
        var facts = plan.FinalizeFacts(BaselineStatus.Green, [executed], false, []);
        var decision = EvaluationReducer.Reduce(facts);

        Assert.Equal(EvaluationOutcome.Incomplete, decision.Outcome);
        Assert.Contains(decision.Reasons, reason => reason.Code == MutationSelection.TargetedDiagnosticCode);

        var report = Report(facts, decision, scope, snapshotId);
        using var document = JsonDocument.Parse(ReportWriter.Serialize(report));
        Assert.True(ReportSchema.Evaluate(document.RootElement).IsValid);

        var reportBytes = ReportWriter.Serialize(report);
        var fingerprint = EvaluationFingerprint.ComputeForProven(material);
        var coverage = new CoverageProvenance("1", report.RunId, "suite-a", fingerprint, snapshotId,
            BaselineStatus.Green, Digest("coverage"), 8, "path-map-v1", "vstest-v1", true);
        var record = new ProvenEvaluationSidecar("1", SidecarRecordKind.Proven, report.RunId,
            DateTimeOffset.UnixEpoch, fingerprint, snapshotId,
            Convert.ToHexString(SHA256.HashData(reportBytes)).ToLowerInvariant(), reportBytes.LongLength,
            true, [coverage], report.Counts);

        Assert.Throws<EvaluationContractException>(() =>
            new SidecarStore(_directory).PublishProven(record, report, material, plan));
    }

    [Fact]
    public void AllIdsTargetedReportIsSchemaValidWithoutCallerPreservedEvidenceKind()
    {
        var scope = ScopePlan.Empty("inputs", ".", null);
        var snapshotId = Digest("schema-snapshot");
        var material = FingerprintMaterial(snapshotId, scope);
        var candidate = Candidate(Identity("class C { bool M(int value) => value == 1; }", "=="),
            "symbols=A", 34, 2);
        var bound = MutationSelection.Bind([candidate], material);
        var plan = MutationSelection.PlanTargeted(bound, [candidate.MutationId], bound.PlanFingerprint, material);
        var executed = plan.Reduce(plan.CreatePendingLedger().Single(),
            [new(1, UnitDisposition.Killed, [new("MUTANT_KILLED", "Fresh suite killed the mutation.")])]);
        var facts = plan.FinalizeFacts(BaselineStatus.Green, [executed], false);
        var decision = EvaluationReducer.Reduce(facts);
        var report = Report(facts, decision, scope, snapshotId);

        using var document = JsonDocument.Parse(ReportWriter.Serialize(report));

        Assert.Equal("INCOMPLETE", document.RootElement.GetProperty("outcome").GetString());
        Assert.True(ReportSchema.Evaluate(document.RootElement).IsValid);
    }

    [Fact]
    public void ResultOrderingRejectsMutationIdentityThatDoesNotMatchThePlannedUnit()
    {
        var candidate = Candidate(Identity("class C { bool M(int value) => value == 1; }", "=="),
            "symbols=A", 34, 2);
        var material = FingerprintMaterial(Digest("identity"), ScopePlan.Empty("inputs", ".", null));
        var plan = MutationSelection.Plan(MutationSelection.Bind([candidate], material), material);
        var other = Identity("class C { bool M(int value) => value != 1; }", "!=");
        var result = new EvaluationUnitResult(other.MutationId, candidate.EvaluationUnitId,
            UnitDisposition.Killed, [new("MUTANT_KILLED", "Fresh suite killed the mutation.")]);

        Assert.Throws<EvaluationContractException>(() => plan.OrderResults([result]));
    }

    [Fact]
    public void SameMutationIdentityAcrossRealParseContextsIsAcceptedButSameContextCollisionIsRefused()
    {
        const string source = """
#if FIRST
class C { bool M(int value) => value == 1; }
#else

class C { bool M(int value) => value == 2; }
#endif
""";
        var first = Identity(source, "==", "FIRST");
        var second = Identity(source, "==", "SECOND");
        Assert.Equal(first.MutationId, second.MutationId);
        Assert.NotEqual(first.SourceStart, second.SourceStart);
        var firstCandidate = Candidate(first, "symbols=FIRST", first.SourceStart, first.SourceLength);
        var secondCandidate = Candidate(second, "symbols=SECOND", second.SourceStart, second.SourceLength);

        var material = FingerprintMaterial(Digest("contexts"), ScopePlan.Empty("inputs", ".", null));
        var bound = MutationSelection.Bind([secondCandidate, firstCandidate], material);
        var plan = MutationSelection.Plan(bound, material);
        var targeted = MutationSelection.PlanTargeted(bound, [first.MutationId], bound.PlanFingerprint, material);

        Assert.Equal(2, plan.Selected.Count);
        Assert.Equal(2, targeted.Selected.Count);
        Assert.Throws<EvaluationContractException>(() => MutationSelection.Bind(
            [firstCandidate, secondCandidate with { ParseContext = firstCandidate.ParseContext,
                EvaluationUnitId = firstCandidate.EvaluationUnitId }], material));
    }

    private static EvaluationReport Report(EvaluationFacts facts, EvaluationDecision decision,
        ScopePlan scope, string snapshotId) => new("1", "targeted-round-three", DateTimeOffset.UnixEpoch, "check",
        new("inputs", null, ["src/A.cs"]), scope, EvaluationReport.DefaultPolicy, facts.Baseline,
        [new("suite-a", BaselineStatus.Green, [new("BASELINE_GREEN", "Fresh baseline passed.")])],
        facts.Units, decision.Counts, facts.IncompleteConditions, decision.Reasons,
        decision.Evidence.Concat([new EvaluationEvidence("INPUT_SNAPSHOT", "Frozen input snapshot.",
            [$"captureId={snapshotId}"])]).ToArray(), decision.Outcome, decision.ExitCode);

    private static MutationCandidate Candidate(IdentifiedMutation mutation, string parseContext,
        int sourceStart, int sourceLength)
    {
        const string project = "src/App.csproj";
        const string target = "net10.0";
        var evaluationId = EvaluationUnitIdentity.Compute(new(mutation.MutationId, project, target, parseContext));
        return new(mutation.MutationId, evaluationId, mutation.Material, project, target, parseContext,
            sourceStart, sourceLength);
    }

    private static IdentifiedMutation Identity(string source, string token, params string[] symbols)
    {
        var options = new CSharpParseOptions(LanguageVersion.Preview, preprocessorSymbols: symbols);
        var tree = CSharpSyntaxTree.ParseText(source, options);
        var operatorToken = tree.GetRoot().DescendantTokens().Single(item => item.Text == token);
        return MutationIdentity.Create("src/A.cs", tree.GetRoot(), operatorToken.Span,
            "binary.equal", "1", "!=");
    }

    private static EvaluationFingerprintMaterial FingerprintMaterial(string snapshotId, ScopePlan scope) => new(
        [
            EvaluationFingerprint.FromBytes("source", "src/A.cs", "source"u8),
            EvaluationFingerprint.FromBytes("dependency", "packages.lock.json", "dependency"u8)
        ], snapshotId, ReportWriter.SerializeCanonicalScope(scope), "configuration-v1", "tool-v1", "operator-v1",
        "sdk-v1", "runtime-v1", "vstest-v1", EvaluationReport.DefaultPolicy, ProvenanceComplete: true);

    private static string Digest(string value) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static JsonSchema LoadReportSchema()
    {
        var path = Path.GetFullPath("../../../../../docs/contracts/evaluation-report-v2.schema.json",
            AppContext.BaseDirectory);
        var schema = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        schema.Remove("$id");
        return JsonSchema.FromText(schema.ToJsonString());
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch { }
    }
}
