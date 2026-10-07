using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace Mutate4CSharp.Tests;

public sealed class AdversarialRoundSevenTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "mutate4csharp-round-seven",
        Guid.NewGuid().ToString("N"));

    public AdversarialRoundSevenTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public void LegacyCapAboveRecordedPolicyIsRefused()
    {
        var material = Material("cap-mismatch", mutationCap: 1);
        var bound = MutationSelection.Bind([Candidate("A"), Candidate("B")], material);

        Assert.Throws<EvaluationContractException>(() => MutationSelection.Plan(bound, 2, material));
    }

    [Fact]
    public void FullPlanHasAPolicyDerivedFactory()
    {
        var factory = typeof(MutationSelection).GetMethods(BindingFlags.Static | BindingFlags.Public |
                BindingFlags.NonPublic)
            .SingleOrDefault(method => method.Name == nameof(MutationSelection.Plan) &&
                method.GetParameters() is
                [
                    { ParameterType: var plan },
                    { ParameterType: var material }
                ] &&
                plan == typeof(BoundMutationPlan) && material == typeof(EvaluationFingerprintMaterial));

        Assert.NotNull(factory);
        var fingerprintMaterial = Material("policy-derived", mutationCap: 1);
        var bound = MutationSelection.Bind([Candidate("A"), Candidate("B")], fingerprintMaterial);
        var selection = Assert.IsType<MutationSelectionPlan>(factory.Invoke(null, [bound, fingerprintMaterial]));

        Assert.Single(selection.Selected);
        Assert.Single(selection.Omitted);
        Assert.False(selection.CanAdvanceFullScopeSuccess);
    }

    [Fact]
    public void DirectCompletionCannotFinalizeOrPublishProvenState()
    {
        var material = Material("direct-completion");
        var plan = MutationSelection.Plan(MutationSelection.Bind([Candidate("A")], material), material);
        var completed = plan.CreatePendingLedger().Single().Complete(UnitDisposition.Killed,
            [new("MUTANT_KILLED", "Caller asserted completion directly.")]);

        Assert.Throws<EvaluationContractException>(() =>
            plan.FinalizeFacts(BaselineStatus.Green, [completed], false));

        var (report, record, fingerprint) = ProvenEnvelope(material, [completed]);
        var store = new SidecarStore(_directory);
        Assert.Throws<EvaluationContractException>(() =>
            store.PublishProven(record, report, material, plan));
        Assert.False(File.Exists(store.ProvenPath(fingerprint)));
    }

    [Fact]
    public void WrongPolicyReductionCannotFinalizeOrPublishProvenState()
    {
        var material = Material("wrong-policy", stabilityRepetitions: 3);
        var plan = MutationSelection.Plan(MutationSelection.Bind([Candidate("A")], material), material);
        var pending = plan.CreatePendingLedger().Single();
        Assert.DoesNotContain(typeof(StabilityEvidence).GetMethods(BindingFlags.Static | BindingFlags.Public |
            BindingFlags.NonPublic), method => method.Name == nameof(StabilityEvidence.Reduce) &&
                method.GetParameters().FirstOrDefault()?.ParameterType == typeof(EvaluationUnitResult));
        Assert.Throws<EvaluationContractException>(() => plan.Reduce(pending,
            [new(1, UnitDisposition.Killed, [new("MUTANT_KILLED", "Only one attempt ran.")])]));
        Assert.False(Directory.Exists(Path.Combine(_directory, ".mutate4csharp", "proven")));
    }

    [Fact]
    public void CorrectPolicyStabilityReductionCanFinalizeAndPublish()
    {
        var material = Material("correct-policy", stabilityRepetitions: 2);
        var plan = MutationSelection.Plan(MutationSelection.Bind([Candidate("A")], material), material);
        var reduced = plan.Reduce(plan.CreatePendingLedger().Single(),
        [
            new(1, UnitDisposition.Killed, [new("MUTANT_KILLED", "Attempt one killed the mutation.")]),
            new(2, UnitDisposition.Killed, [new("MUTANT_KILLED", "Attempt two killed the mutation.")])
        ]);

        var facts = plan.FinalizeFacts(BaselineStatus.Green, [reduced], false);
        Assert.Equal(EvaluationOutcome.Pass, EvaluationReducer.Reduce(facts).Outcome);
        var (report, record, _) = ProvenEnvelope(material, [reduced]);
        var path = new SidecarStore(_directory).PublishProven(record, report, material, plan);

        Assert.True(File.Exists(path));
        Assert.Equal(Path.Combine(_directory, ".mutate4csharp", "proven"), Path.GetDirectoryName(path));
    }

    private static (EvaluationReport Report, ProvenEvaluationSidecar Record, string Fingerprint) ProvenEnvelope(
        EvaluationFingerprintMaterial material, IReadOnlyList<EvaluationUnitResult> units)
    {
        var facts = new EvaluationFacts(BaselineStatus.Green, units.Count, units, false, []);
        var decision = EvaluationReducer.Reduce(facts);
        var report = new EvaluationReport("1", "round-seven", DateTimeOffset.UnixEpoch, "check",
            new("inputs", null, ["src/A.cs"]), ScopePlan.Empty("inputs", ".", null), material.Policy,
            BaselineStatus.Green,
            [new("suite-a", BaselineStatus.Green, [new("BASELINE_GREEN", "Fresh baseline passed.")])],
            units, decision.Counts, [], decision.Reasons,
            decision.Evidence.Concat([new EvaluationEvidence("INPUT_SNAPSHOT", "Frozen input snapshot.",
                [$"captureId={material.SnapshotId}"])]).ToArray(), decision.Outcome, decision.ExitCode);
        var bytes = ReportWriter.Serialize(report);
        var fingerprint = EvaluationFingerprint.ComputeForProven(material);
        var coverage = new CoverageProvenance("1", report.RunId, "suite-a", fingerprint, material.SnapshotId,
            BaselineStatus.Green, Digest("coverage"), 8, "path-map-v1", "vstest-v1", true);
        var record = new ProvenEvaluationSidecar("1", SidecarRecordKind.Proven, report.RunId,
            DateTimeOffset.UnixEpoch, fingerprint, material.SnapshotId,
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), bytes.LongLength,
            true, [coverage], report.Counts);
        return (report, record, fingerprint);
    }

    private static MutationCandidate Candidate(string suffix)
    {
        var identity = new MutationIdentityMaterial($"src/{suffix}.cs", $"Type{suffix}.M", "site/1",
            "binary.equal", "1", "!=");
        var mutationId = MutationIdentity.Compute(identity);
        const string project = "src/App.csproj";
        const string target = "net10.0";
        var parse = $"symbols={suffix}";
        return new(mutationId, EvaluationUnitIdentity.Compute(new(mutationId, project, target, parse)),
            identity, project, target, parse);
    }

    private static EvaluationFingerprintMaterial Material(string value, int mutationCap = 100,
        int stabilityRepetitions = 1)
    {
        var scope = ScopePlan.Empty("inputs", ".", null);
        var policy = EvaluationReport.DefaultPolicy with
        {
            MutationCap = mutationCap,
            StabilityRepetitions = stabilityRepetitions
        };
        return new([
                EvaluationFingerprint.FromBytes("source", "src/A.cs", Encoding.UTF8.GetBytes(value)),
                EvaluationFingerprint.FromBytes("dependency", "packages.lock.json", "dependency"u8)
            ], Digest(value), ReportWriter.SerializeCanonicalScope(scope), "configuration-v1", "tool-v1",
            "operator-v1", "sdk-v1", "runtime-v1", "vstest-v1", policy, ProvenanceComplete: true);
    }

    private static string Digest(string value) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch { }
    }
}
