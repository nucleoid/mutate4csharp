using System.Security.Cryptography;
using System.Text;

namespace Mutate4CSharp.Tests;

public sealed class AdversarialRoundEightTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "mutate4csharp-round-eight",
        Guid.NewGuid().ToString("N"));

    public AdversarialRoundEightTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public void EvaluationUnitCannotBeReducedAgainToReplaceUnstableEvidenceWithAKill()
    {
        var material = Material("single-use", stabilityRepetitions: 3);
        var plan = MutationSelection.Plan(MutationSelection.Bind([Candidate("A")], material), material);
        var pending = plan.CreatePendingLedger().Single();
        var unstable = plan.Reduce(pending,
        [
            Attempt(1, UnitDisposition.Killed),
            Attempt(2, UnitDisposition.Survived),
            Attempt(3, UnitDisposition.Killed)
        ]);

        Assert.Throws<EvaluationContractException>(() => plan.Reduce(pending,
        [
            Attempt(1, UnitDisposition.Killed),
            Attempt(2, UnitDisposition.Killed),
            Attempt(3, UnitDisposition.Killed)
        ]));

        var facts = plan.FinalizeFacts(BaselineStatus.Green, [unstable], false);
        Assert.Equal(EvaluationOutcome.Incomplete, EvaluationReducer.Reduce(facts).Outcome);
    }

    [Fact]
    public void FailedFirstReductionPermanentlyConsumesTheEvaluationUnit()
    {
        var material = Material("failed-first-reduction", stabilityRepetitions: 3);
        var plan = MutationSelection.Plan(MutationSelection.Bind([Candidate("A")], material), material);
        var pending = plan.CreatePendingLedger().Single();

        Assert.Throws<EvaluationContractException>(() => plan.Reduce(pending,
            [Attempt(1, UnitDisposition.Killed)]));
        Assert.Throws<EvaluationContractException>(() => plan.Reduce(pending,
        [
            Attempt(1, UnitDisposition.Killed),
            Attempt(2, UnitDisposition.Killed),
            Attempt(3, UnitDisposition.Killed)
        ]));

        var forged = pending.Complete(UnitDisposition.Killed,
            [new("MUTANT_KILLED", "A caller cannot replace the consumed reduction slot.")]);
        AssertRejectedByCompletionAuthority(plan, material, [forged]);
    }

    [Fact]
    public async Task ConcurrentReductionIssuesExactlyOneCompletion()
    {
        var material = Material("concurrent-reduction");
        var plan = MutationSelection.Plan(MutationSelection.Bind([Candidate("A")], material), material);
        var pending = plan.CreatePendingLedger().Single();
        using var start = new ManualResetEventSlim();

        var reductions = Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            start.Wait();
            try
            {
                return (Result: plan.Reduce(pending, [Attempt(1, UnitDisposition.Killed)]), Error: (Exception?)null);
            }
            catch (Exception error)
            {
                return (Result: (EvaluationUnitResult?)null, Error: error);
            }
        })).ToArray();

        start.Set();
        var outcomes = await Task.WhenAll(reductions);

        Assert.Single(outcomes, outcome => outcome.Result is not null);
        Assert.Equal(7, outcomes.Count(outcome => outcome.Error is EvaluationContractException));
        var issued = outcomes.Single(outcome => outcome.Result is not null).Result!;
        _ = plan.FinalizeFacts(BaselineStatus.Green, [issued], false);
    }

    [Theory]
    [InlineData("disposition")]
    [InlineData("evidence")]
    public void ReducerIssuedRecordMutationCannotFinalizeOrPublish(string mutation)
    {
        var material = Material("record-mutation-" + mutation);
        var plan = MutationSelection.Plan(MutationSelection.Bind([Candidate("A")], material), material);
        var reduced = plan.Reduce(plan.CreatePendingLedger().Single(),
            [Attempt(1, UnitDisposition.Killed)]);
        var mutated = mutation switch
        {
            "disposition" => reduced with { Disposition = UnitDisposition.Survived },
            "evidence" => reduced with
            {
                Evidence = [new("MUTANT_SURVIVED", "Replacement evidence must not retain authority.")]
            },
            _ => throw new ArgumentOutOfRangeException(nameof(mutation))
        };

        AssertRejectedByCompletionAuthority(plan, material, [mutated]);
    }

    [Fact]
    public void SwappedMutationAndEvaluationUnitIdentitiesCannotFinalizeOrPublish()
    {
        var material = Material("identity-swap");
        var plan = MutationSelection.Plan(MutationSelection.Bind([Candidate("A"), Candidate("B")], material), material);
        var reduced = plan.CreatePendingLedger()
            .Select(unit => plan.Reduce(unit, [Attempt(1, UnitDisposition.Killed)]))
            .ToArray();
        var swapped = new[]
        {
            reduced[0] with
            {
                UnitId = reduced[1].UnitId,
                EvaluationUnitId = reduced[1].EvaluationUnitId
            },
            reduced[1] with
            {
                UnitId = reduced[0].UnitId,
                EvaluationUnitId = reduced[0].EvaluationUnitId
            }
        };

        AssertRejectedByCompletionAuthority(plan, material, plan.OrderResults(swapped));
    }

    [Theory]
    [InlineData("finalization")]
    [InlineData("publication")]
    public void NullToEmptyDiagnosticsMutationInvalidatesReducerAuthority(string boundary)
    {
        var material = Material("diagnostics-presence-" + boundary, stabilityRepetitions: 2);
        var plan = MutationSelection.Plan(MutationSelection.Bind([Candidate("A")], material), material);
        var reduced = plan.Reduce(plan.CreatePendingLedger().Single(),
        [
            Attempt(1, UnitDisposition.Killed),
            Attempt(2, UnitDisposition.Survived)
        ]);
        var evidence = reduced.Evidence.ToArray();
        var index = Array.FindIndex(evidence, item => item.Diagnostics is null);
        Assert.True(index >= 0);
        evidence[index] = evidence[index] with { Diagnostics = Array.Empty<string>() };
        var mutated = reduced with { Evidence = evidence };

        if (boundary == "finalization")
        {
            var error = Assert.Throws<EvaluationContractException>(() =>
                plan.FinalizeFacts(BaselineStatus.Green, [mutated], false));
            Assert.Contains("exact reducer-issued completion", error.Message, StringComparison.Ordinal);
            return;
        }

        var (report, record, fingerprint) = ProvenEnvelope(material, [mutated]);
        record = record with
        {
            Counts = new EvaluationCounts(1, 1, 1, 0, 0, 0, 1, 0, 0)
        };
        var store = new SidecarStore(_directory);
        var publicationError = Assert.Throws<EvaluationContractException>(() =>
            store.PublishProven(record, report, material, plan));
        Assert.Contains("Proven publication report provenance", publicationError.Message,
            StringComparison.Ordinal);
        Assert.NotNull(publicationError.InnerException);
        Assert.Contains("exact reducer-issued completion", publicationError.InnerException.Message,
            StringComparison.Ordinal);
        Assert.False(File.Exists(store.ProvenPath(fingerprint)));
    }

    [Fact]
    public void InPlaceMutableEvidenceMutationCannotFinalizeOrPublish()
    {
        var material = Material("mutable-evidence");
        var plan = MutationSelection.Plan(MutationSelection.Bind([Candidate("A")], material), material);
        var reduced = plan.Reduce(plan.CreatePendingLedger().Single(),
            [Attempt(1, UnitDisposition.Killed)]);
        var evidence = Assert.IsAssignableFrom<IList<EvaluationEvidence>>(reduced.Evidence);

        evidence[0] = new("MUTANT_SURVIVED", "The mutable evidence list was changed in place.");

        AssertRejectedByCompletionAuthority(plan, material, [reduced]);
    }

    [Fact]
    public void CanonicallyOrderedReportCanPublishProvenState()
    {
        var material = Material("canonical-order");
        var plan = MutationSelection.Plan(MutationSelection.Bind([Candidate("B"), Candidate("A")], material), material);
        var completed = plan.CreatePendingLedger()
            .Select(unit => plan.Reduce(unit, [Attempt(1, UnitDisposition.Killed)]))
            .ToArray();
        var canonical = plan.OrderResults(completed);
        var (report, record, _) = ProvenEnvelope(material, canonical);

        var path = new SidecarStore(_directory).PublishProven(record, report, material, plan);

        Assert.True(File.Exists(path));
    }

    [Fact]
    public void ReorderedEquivalentReportCannotPublishProvenState()
    {
        var material = Material("reordered-report");
        var plan = MutationSelection.Plan(MutationSelection.Bind([Candidate("B"), Candidate("A")], material), material);
        var completed = plan.CreatePendingLedger()
            .Select(unit => plan.Reduce(unit, [Attempt(1, UnitDisposition.Killed)]))
            .ToArray();
        var reordered = plan.OrderResults(completed).Reverse().ToArray();
        var (report, record, fingerprint) = ProvenEnvelope(material, reordered);
        var store = new SidecarStore(_directory);

        Assert.Throws<EvaluationContractException>(() =>
            store.PublishProven(record, report, material, plan));
        Assert.False(File.Exists(store.ProvenPath(fingerprint)));
    }

    private void AssertRejectedByCompletionAuthority(MutationSelectionPlan plan,
        EvaluationFingerprintMaterial material, IReadOnlyList<EvaluationUnitResult> units)
    {
        var finalizationError = Assert.Throws<EvaluationContractException>(() =>
            plan.FinalizeFacts(BaselineStatus.Green, units, false));
        Assert.Contains("exact reducer-issued completion", finalizationError.Message,
            StringComparison.Ordinal);

        var (report, record, fingerprint) = ProvenEnvelope(material, units);
        var store = new SidecarStore(_directory);
        var publicationError = Assert.Throws<EvaluationContractException>(() =>
            store.PublishProven(record, report, material, plan));
        Assert.Contains("Proven publication report provenance", publicationError.Message,
            StringComparison.Ordinal);
        Assert.NotNull(publicationError.InnerException);
        Assert.Contains("exact reducer-issued completion", publicationError.InnerException.Message,
            StringComparison.Ordinal);
        Assert.False(File.Exists(store.ProvenPath(fingerprint)));
    }

    private static StabilityAttempt Attempt(int number, UnitDisposition disposition) =>
        new(number, disposition,
            [new(disposition == UnitDisposition.Killed ? "MUTANT_KILLED" : "MUTANT_SURVIVED",
                $"Attempt {number} observed {disposition}.")]);

    private static (EvaluationReport Report, ProvenEvaluationSidecar Record, string Fingerprint) ProvenEnvelope(
        EvaluationFingerprintMaterial material, IReadOnlyList<EvaluationUnitResult> units)
    {
        var facts = new EvaluationFacts(BaselineStatus.Green, units.Count, units, false, []);
        var decision = EvaluationReducer.Reduce(facts);
        var suite = SuiteAccountingFixture.Create("round-eight", material.SnapshotId, Digest("coverage"), 8);
        var report = new EvaluationReport(ReportWriter.SchemaVersion, "round-eight", DateTimeOffset.UnixEpoch, "check",
            new("inputs", null, ["src/A.cs"]), ScopePlan.Empty("inputs", ".", null), material.Policy,
            BaselineStatus.Green,
            [suite],
            units, decision.Counts, [], decision.Reasons,
            decision.Evidence.Concat([new EvaluationEvidence("INPUT_SNAPSHOT", "Frozen input snapshot.",
                [$"captureId={material.SnapshotId}"]), ReportWriter.ConfigurationSuiteEvidence([suite.SuiteId])]).ToArray(), decision.Outcome, decision.ExitCode);
        var bytes = ReportWriter.Serialize(report);
        var fingerprint = EvaluationFingerprint.ComputeForProven(material);
        var coverage = new CoverageProvenance("1", report.RunId, suite.SuiteId, fingerprint, material.SnapshotId,
            BaselineStatus.Green, Digest("coverage"), 8, "baseline-clone-to-snapshot-v1", material.RunnerIdentity, true);
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

    private static EvaluationFingerprintMaterial Material(string value, int stabilityRepetitions = 1)
    {
        var scope = ScopePlan.Empty("inputs", ".", null);
        var policy = EvaluationReport.DefaultPolicy with { StabilityRepetitions = stabilityRepetitions };
        return new([
                EvaluationFingerprint.FromBytes("source", "src/A.cs", Encoding.UTF8.GetBytes(value)),
                EvaluationFingerprint.FromBytes("dependency", "packages.lock.json", "dependency"u8)
            ], Digest(value), ReportWriter.SerializeCanonicalScope(scope), "configuration-v1", "tool-v1",
            "operator-v1", "sdk-v1", "runtime-v1", SuiteAccountingFixture.Runner, policy, ProvenanceComplete: true);
    }

    private static string Digest(string value) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch { }
    }
}
