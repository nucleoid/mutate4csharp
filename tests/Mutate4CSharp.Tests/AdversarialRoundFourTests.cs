using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;
using Microsoft.CodeAnalysis.CSharp;

namespace Mutate4CSharp.Tests;

public sealed class AdversarialRoundFourTests : IDisposable
{
    private static readonly JsonSchema CurrentSchema = LoadCurrentSchema();
    private static readonly JsonSchema PreDiagnosticVersionOneSchema = LoadPreDiagnosticVersionOneSchema();
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "mutate4csharp-round-four",
        Guid.NewGuid().ToString("N"));

    public AdversarialRoundFourTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public void TargetedLedgerCannotBeFinalizedByAFullPlan()
    {
        var context = TargetedContext();
        var completed = context.TargetedPlan.CreatePendingLedger()
            .Select(item => item with
            {
                Disposition = UnitDisposition.Killed,
                Evidence = [new("MUTANT_KILLED", "Fresh suite killed the mutation.")]
            }).ToArray();
        var fullPlan = MutationSelection.Plan(context.Bound, context.Material);

        Assert.Throws<EvaluationContractException>(() =>
            fullPlan.FinalizeFacts(BaselineStatus.Green, completed, false));
    }

    [Fact]
    public void ProvenPublicationRefusesTheTargetedLedgerToFullPlanPath()
    {
        var context = TargetedContext();

        PublishThroughFullPlan(context);
    }

    [Fact]
    public void StabilityReductionPreservesTargetedPlanProvenanceAndDiagnosticState()
    {
        var context = TargetedContext();
        var pending = context.TargetedPlan.CreatePendingLedger().Single();

        var reduced = context.TargetedPlan.Reduce(pending,
            [new(1, UnitDisposition.Killed, [new("MUTANT_KILLED", "Fresh suite killed the mutation.")])]);

        Assert.True(reduced.DiagnosticPartial);
        Assert.Equal(pending.OriginatingPlanFingerprint, reduced.OriginatingPlanFingerprint);
        var facts = context.TargetedPlan.FinalizeFacts(BaselineStatus.Green, [reduced], false);
        Assert.Equal(EvaluationOutcome.Incomplete, EvaluationReducer.Reduce(facts).Outcome);
    }

    [Fact]
    public void OldAndOrdinaryNewVersionOneReportsRemainSchemaCompatible()
    {
        var report = EvaluationReport.CreateSynthetic(EvaluationOutcome.Pass, "schema-compatibility", "FIXTURE");
        var currentNode = JsonNode.Parse(ReportWriter.Serialize(report))!.AsObject();
        var oldNode = currentNode.DeepClone().AsObject();
        oldNode.Remove("diagnosticPartial");
        foreach (var unit in oldNode["units"]!.AsArray().Select(item => item!.AsObject()))
            unit.Remove("diagnosticPartial");

        Assert.True(Evaluate(CurrentSchema, oldNode));
        Assert.True(Evaluate(PreDiagnosticVersionOneSchema, currentNode));
    }

    [Fact]
    public void SchemaAcceptsAConsistentTargetedDiagnosticReport()
    {
        var node = TargetedReportNode();

        Assert.True(Evaluate(CurrentSchema, node));
    }

    [Fact]
    public void SchemaRejectsDiagnosticTrueWithoutTargetedCondition()
    {
        var node = TargetedReportNode();
        node["incompleteConditions"] = new JsonArray();

        Assert.False(Evaluate(CurrentSchema, node));
    }

    [Fact]
    public void SchemaRejectsRootAndUnitDiagnosticDisagreement()
    {
        var rootFalse = TargetedReportNode();
        rootFalse["diagnosticPartial"] = false;
        Assert.False(Evaluate(CurrentSchema, rootFalse));

        var unitFalse = TargetedReportNode();
        unitFalse["units"]![0]!["diagnosticPartial"] = false;
        Assert.False(Evaluate(CurrentSchema, unitFalse));
    }

    [Theory]
    [InlineData((int)EvaluationOutcome.Pass)]
    [InlineData((int)EvaluationOutcome.Fail)]
    [InlineData((int)EvaluationOutcome.NotApplicable)]
    public void SchemaRejectsTerminalNonDiagnosticOutcomeContainingDiagnosticUnit(int outcomeValue)
    {
        var report = EvaluationReport.CreateSynthetic((EvaluationOutcome)outcomeValue,
            "schema-terminal", "FIXTURE");
        var node = JsonNode.Parse(ReportWriter.Serialize(report))!.AsObject();
        if (node["units"] is not JsonArray { Count: > 0 })
        {
            var pass = JsonNode.Parse(ReportWriter.Serialize(
                EvaluationReport.CreateSynthetic(EvaluationOutcome.Pass, "schema-unit", "FIXTURE")))!.AsObject();
            node["units"] = pass["units"]!.DeepClone();
        }
        node["units"]![0]!["diagnosticPartial"] = true;

        Assert.False(Evaluate(CurrentSchema, node));
    }

    private void PublishThroughFullPlan(TargetedTestContext context)
    {
        var completed = context.TargetedPlan.CreatePendingLedger()
            .Select(item => new EvaluationUnitResult(item.UnitId, item.EvaluationUnitId,
                UnitDisposition.Killed, [new("MUTANT_KILLED", "Attacker stripped the targeted flags and plan provenance.")]))
            .ToArray();
        var fullPlan = MutationSelection.Plan(context.Bound, context.Material);
        var facts = new EvaluationFacts(BaselineStatus.Green, completed.Length, completed, false, []);
        var decision = EvaluationReducer.Reduce(facts);
        var report = Report(facts, decision, context.Scope, context.SnapshotId);
        var reportBytes = ReportWriter.Serialize(report);
        var fingerprint = EvaluationFingerprint.ComputeForProven(context.Material);
        var coverage = new CoverageProvenance("1", report.RunId, report.Suites[0].SuiteId, fingerprint, context.SnapshotId,
            BaselineStatus.Green, Digest("coverage"), 8, "baseline-clone-to-snapshot-v1", SuiteAccountingFixture.Runner, true);
        var record = new ProvenEvaluationSidecar("2", SidecarRecordKind.Proven, report.RunId,
            DateTimeOffset.UnixEpoch, fingerprint, context.SnapshotId,
            Convert.ToHexString(SHA256.HashData(reportBytes)).ToLowerInvariant(), reportBytes.LongLength,
            true, [coverage], report.Counts);

        var failure = Assert.Throws<EvaluationContractException>(() =>
            new SidecarStore(_directory).PublishProven(record, report, context.Material, fullPlan));
        Assert.Contains("finalizing mutation plan", failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static JsonObject TargetedReportNode()
    {
        var context = TargetedContext();
        var completed = context.TargetedPlan.CreatePendingLedger()
            .Select(item => context.TargetedPlan.Reduce(item,
                [new(1, UnitDisposition.Killed,
                    [new("MUTANT_KILLED", "Fresh suite killed the mutation.")])]))
            .ToArray();
        var facts = context.TargetedPlan.FinalizeFacts(BaselineStatus.Green, completed, false);
        var decision = EvaluationReducer.Reduce(facts);
        return JsonNode.Parse(ReportWriter.Serialize(
            Report(facts, decision, context.Scope, context.SnapshotId)))!.AsObject();
    }

    private static TargetedTestContext TargetedContext()
    {
        var scope = ScopePlan.Empty("inputs", ".", null);
        var snapshotId = Digest("round-four-snapshot");
        var material = FingerprintMaterial(snapshotId, scope);
        var mutation = Identity("class C { bool M(int value) => value == 1; }", "==");
        const string project = "src/App.csproj";
        const string target = "net10.0";
        const string parseContext = "symbols=A";
        var candidate = new MutationCandidate(mutation.MutationId,
            EvaluationUnitIdentity.Compute(new(mutation.MutationId, project, target, parseContext)),
            mutation.Material, project, target, parseContext, mutation.SourceStart, mutation.SourceLength);
        var bound = MutationSelection.Bind([candidate], material);
        var targeted = MutationSelection.PlanTargeted(bound, [candidate.MutationId], bound.PlanFingerprint, material);
        return new(scope, snapshotId, material, bound, targeted);
    }

    private static EvaluationReport Report(EvaluationFacts facts, EvaluationDecision decision,
        ScopePlan scope, string snapshotId) => new(ReportWriter.SchemaVersion, "targeted-round-four", DateTimeOffset.UnixEpoch, "check",
        new("inputs", null, ["src/A.cs"]), scope, EvaluationReport.DefaultPolicy, facts.Baseline,
        [SuiteAccountingFixture.Create("targeted-round-four", snapshotId, Digest("coverage"), 8)],
        facts.Units, decision.Counts, facts.IncompleteConditions, decision.Reasons,
        decision.Evidence.Concat([new EvaluationEvidence("INPUT_SNAPSHOT", "Frozen input snapshot.",
            [$"captureId={snapshotId}"]), ReportWriter.ConfigurationSuiteEvidence([SuiteAccountingFixture.Create("targeted-round-four", snapshotId, Digest("coverage"), 8).SuiteId])]).ToArray(), decision.Outcome, decision.ExitCode);

    private static IdentifiedMutation Identity(string source, string token)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        var operatorToken = tree.GetRoot().DescendantTokens().Single(item => item.Text == token);
        return MutationIdentity.Create("src/A.cs", tree.GetRoot(), operatorToken.Span,
            "binary.equal", "1", "!=");
    }

    private static EvaluationFingerprintMaterial FingerprintMaterial(string snapshotId, ScopePlan scope) => new(
        [
            EvaluationFingerprint.FromBytes("source", "src/A.cs", "source"u8),
            EvaluationFingerprint.FromBytes("dependency", "packages.lock.json", "dependency"u8)
        ], snapshotId, ReportWriter.SerializeCanonicalScope(scope), "configuration-v1", "tool-v1", "operator-v1",
        "sdk-v1", "runtime-v1", SuiteAccountingFixture.Runner, EvaluationReport.DefaultPolicy, ProvenanceComplete: true);

    private static string Digest(string value) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static bool Evaluate(JsonSchema schema, JsonNode node)
    {
        using var document = JsonDocument.Parse(node.ToJsonString());
        return schema.Evaluate(document.RootElement).IsValid;
    }

    private static JsonSchema LoadCurrentSchema()
    {
        var schema = LoadSchemaNode();
        schema.Remove("$id");
        return JsonSchema.FromText(schema.ToJsonString());
    }

    private static JsonSchema LoadPreDiagnosticVersionOneSchema()
    {
        var schema = LoadSchemaNode();
        schema.Remove("$id");
        schema["required"] = new JsonArray(schema["required"]!.AsArray()
            .Where(item => item!.GetValue<string>() != "diagnosticPartial")
            .Select(item => item!.DeepClone()).ToArray());
        schema["properties"]!.AsObject().Remove("diagnosticPartial");
        var unit = schema["$defs"]!["unit"]!.AsObject();
        unit["required"] = new JsonArray(unit["required"]!.AsArray()
            .Where(item => item!.GetValue<string>() != "diagnosticPartial")
            .Select(item => item!.DeepClone()).ToArray());
        unit["properties"]!.AsObject().Remove("diagnosticPartial");
        return JsonSchema.FromText(schema.ToJsonString());
    }

    private static JsonObject LoadSchemaNode()
    {
        var path = Path.GetFullPath("../../../../../docs/contracts/evaluation-report-v2.schema.json",
            AppContext.BaseDirectory);
        return JsonNode.Parse(File.ReadAllText(path))!.AsObject();
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch { }
    }

    private sealed record TargetedTestContext(ScopePlan Scope, string SnapshotId,
        EvaluationFingerprintMaterial Material, BoundMutationPlan Bound, MutationSelectionPlan TargetedPlan);
}
