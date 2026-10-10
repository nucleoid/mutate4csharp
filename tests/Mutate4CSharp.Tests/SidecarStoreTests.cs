using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mutate4CSharp.Tests;

public sealed class SidecarStoreTests : IDisposable
{
    private const string CompleteRunnerIdentity = "runner=vstest;suites=sha256:" +
        "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc;" +
        "collector=coverlet-opencover-v1;coverage=sha256:" +
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa;" +
        "plan=sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "mutate4csharp-sidecar",
        Guid.NewGuid().ToString("N"));

    public SidecarStoreTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public void DiscoveryAndProvenStateUseSeparateOwnedLocations()
    {
        var store = new SidecarStore(_directory);
        var record = Discovery();

        var path = store.PublishDiscovery(record);

        Assert.Equal(Path.Combine(_directory, ".mutate4csharp", "discovery"), Path.GetDirectoryName(path));
        Assert.True(File.Exists(path));
        Assert.True(File.Exists(store.LockPath("discovery", Path.GetFileNameWithoutExtension(path))));
        Assert.False(Directory.Exists(Path.Combine(_directory, ".mutate4csharp", "proven")));
    }

    [Fact]
    public void ConcurrentWriterCannotPublishAndUnrelatedFilesRemainUntouched()
    {
        var store = new SidecarStore(_directory);
        var seed = Discovery() with { RunId = "seed" };
        store.PublishDiscovery(seed);
        var record = Discovery();
        var path = store.DiscoveryPath(record);
        var key = Path.GetFileNameWithoutExtension(path);
        var unrelated = Path.Combine(_directory, ".mutate4csharp", "discovery", "user-note.txt");
        File.WriteAllText(unrelated, "keep");
        using var held = new FileStream(store.LockPath("discovery", key), FileMode.OpenOrCreate,
            FileAccess.Write, FileShare.None);

        Assert.Throws<IOException>(() => store.PublishDiscovery(record));

        Assert.False(File.Exists(path));
        Assert.Equal("keep", File.ReadAllText(unrelated));
    }

    [Fact]
    public void ConflictingEvidenceForTheSameOwnedKeyIsRejected()
    {
        var store = new SidecarStore(_directory);
        var record = Discovery();
        var path = store.PublishDiscovery(record);

        Assert.Throws<IOException>(() => store.PublishDiscovery(record with
        {
            EvaluationOutcome = EvaluationOutcome.Fail
        }));

        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        Assert.Equal("INCOMPLETE", document.RootElement.GetProperty("evaluationOutcome").GetString());
    }

    [Theory]
    [InlineData("{not-json")]
    [InlineData("{\"schemaVersion\":\"99\",\"recordKind\":\"PROVEN\"}")]
    public void MalformedAndUnknownProvenRecordsFailClosed(string content)
    {
        var store = new SidecarStore(_directory);
        var fingerprint = Fingerprint();
        var path = store.ProvenPath(fingerprint);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);

        var result = store.ReadProvenForInspection(fingerprint);

        Assert.False(result.IsValid);
        Assert.Null(result.Record);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
    }

    [Fact]
    public void OversizedAndPartialProvenRecordsFailClosed()
    {
        var store = new SidecarStore(_directory);
        var fingerprint = Fingerprint();
        var path = store.ProvenPath(fingerprint);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[SidecarStore.MaxRecordBytes + 1]);
        Assert.False(store.ReadProvenForInspection(fingerprint).IsValid);

        File.WriteAllText(path, "{\"schemaVersion\":\"1\",\"recordKind\":\"PROVEN\"}");
        Assert.False(store.ReadProvenForInspection(fingerprint).IsValid);
    }

    [Theory]
    [InlineData("missing-fingerprint")]
    [InlineData("null-fingerprint")]
    [InlineData("missing-counts")]
    [InlineData("null-coverage")]
    [InlineData("unknown-property")]
    [InlineData("unknown-coverage-property")]
    [InlineData("integer-record-kind")]
    [InlineData("integer-baseline")]
    [InlineData("null-coverage-item")]
    [InlineData("lowercase-record-kind")]
    [InlineData("capitalized-record-kind")]
    [InlineData("lowercase-baseline")]
    [InlineData("uppercase-report-hash")]
    [InlineData("coverage-run-mismatch")]
    public void SchemaInvalidProvenRecordsFailClosedWithoutThrowing(string mutation)
    {
        var (store, report, record, material, plan) = ValidProven();
        var path = store.PublishProven(record, report, material, plan);
        var json = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        switch (mutation)
        {
            case "missing-fingerprint": json.Remove("evaluationFingerprint"); break;
            case "null-fingerprint": json["evaluationFingerprint"] = null; break;
            case "missing-counts": json.Remove("counts"); break;
            case "null-coverage": json["coverage"] = null; break;
            case "unknown-property": json["unexpected"] = true; break;
            case "unknown-coverage-property": json["coverage"]![0]!["unexpected"] = true; break;
            case "integer-record-kind": json["recordKind"] = 1; break;
            case "integer-baseline": json["coverage"]![0]!["baseline"] = 0; break;
            case "null-coverage-item": json["coverage"]![0] = null; break;
            case "lowercase-record-kind": json["recordKind"] = "proven"; break;
            case "capitalized-record-kind": json["recordKind"] = "Proven"; break;
            case "lowercase-baseline": json["coverage"]![0]!["baseline"] = "green"; break;
            case "uppercase-report-hash":
                json["reportSha256"] = record.ReportSha256.ToUpperInvariant();
                break;
            case "coverage-run-mismatch": json["coverage"]![0]!["runId"] = "another-run"; break;
        }
        File.WriteAllText(path, json.ToJsonString());

        var result = store.ReadProvenForInspection(record.EvaluationFingerprint);

        Assert.False(result.IsValid);
        Assert.Null(result.Record);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
    }

    [Fact]
    public void ASecondFreshEligiblePassSupersedesTheSameFingerprintProof()
    {
        var (store, report, record, material, plan) = ValidProven();
        store.PublishProven(record, report, material, plan);
        var nextReport = report with { RunId = "next-fresh-run", GeneratedAtUtc = report.GeneratedAtUtc.AddSeconds(1) };
        var bytes = ReportWriter.Serialize(nextReport);
        var nextRecord = record with
        {
            RunId = nextReport.RunId,
            GeneratedAtUtc = nextReport.GeneratedAtUtc,
            ReportSha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
            ReportLength = bytes.LongLength,
            Coverage = record.Coverage.Select(item => item with { RunId = nextReport.RunId }).ToArray()
        };

        store.PublishProven(nextRecord, nextReport, material, plan);

        var inspected = store.ReadProvenForInspection(record.EvaluationFingerprint);
        Assert.True(inspected.IsValid, inspected.Error);
        Assert.Equal(nextReport.RunId, inspected.Record!.RunId);
    }

    [Fact]
    public void FullyBoundPassPublishesAndReadsBack()
    {
        var (store, report, record, material, plan) = ValidProven();

        var path = store.PublishProven(record, report, material, plan);
        var inspected = store.ReadProvenForInspection(record.EvaluationFingerprint);

        Assert.True(File.Exists(path));
        Assert.True(inspected.IsValid);
        Assert.Equal(record.EvaluationFingerprint, inspected.Record!.EvaluationFingerprint);
        Assert.Equal(record.SnapshotId, inspected.Record.SnapshotId);
        Assert.Equal(record.RunId, inspected.Record.RunId);
        Assert.Equal(record.Coverage, inspected.Record.Coverage);
    }

    [Fact]
    public void ProvenPublicationRejectsSnapshotSuiteAndCoverageLengthMismatches()
    {
        var (store, report, record, material, plan) = ValidProven();

        Assert.Throws<EvaluationContractException>(() => store.PublishProven(
            record with { SnapshotId = Digest("other-snapshot"), Coverage =
            [record.Coverage[0] with { SnapshotId = Digest("other-snapshot") }] }, report, material, plan));
        Assert.Throws<EvaluationContractException>(() => store.PublishProven(
            record with { Coverage = [record.Coverage[0] with { SuiteId = "other-suite" }] }, report, material, plan));
        Assert.Throws<EvaluationContractException>(() => store.PublishProven(
            record with { Coverage = [record.Coverage[0] with { CoverageReportLength = 0 }] }, report, material, plan));
        Assert.Throws<EvaluationContractException>(() => store.PublishProven(
            record with { Coverage = [record.Coverage[0] with { RunId = "another-run" }] }, report, material, plan));
        Assert.Throws<EvaluationContractException>(() => store.PublishProven(
            record with { Coverage = [record.Coverage[0] with { RunnerIdentity = "other-runner" }] }, report, material, plan));

        var twoSuiteReport = report with
        {
            Suites = report.Suites.Append(ValidSuite("suite-b")).ToArray(),
            Evidence = report.Evidence.Where(item => item.Kind != "CHECK_CONFIGURATION")
                .Append(ReportWriter.ConfigurationSuiteEvidence(["suite-a", "suite-b"])).ToArray()
        };
        var twoSuiteBytes = ReportWriter.Serialize(twoSuiteReport);
        var oneCoverageRecord = record with
        {
            ReportSha256 = Convert.ToHexString(SHA256.HashData(twoSuiteBytes)).ToLowerInvariant(),
            ReportLength = twoSuiteBytes.LongLength
        };
        Assert.Throws<EvaluationContractException>(() => store.PublishProven(
            oneCoverageRecord, twoSuiteReport, material, plan));
        Assert.Throws<EvaluationContractException>(() => store.PublishProven(
            record, report, material with { ProvenanceComplete = false, RunnerIdentity = "runner:not-executed" }, plan));
    }

    [Fact]
    public void ProvenPublicationRejectsMaterialScopeAndSnapshotMismatches()
    {
        var (store, report, record, material, plan) = ValidProven();

        Assert.Throws<EvaluationContractException>(() => store.PublishProven(record, report,
            material with { Scope = JsonSerializer.Serialize(ScopePlan.Empty("base", ".", null)) }, plan));
        Assert.Throws<EvaluationContractException>(() => store.PublishProven(record, report,
            material with { SnapshotId = Digest("different-snapshot") }, plan));
    }

    [Fact]
    public void MalformedOwnedStateTypesFailClosedAsIoErrors()
    {
        var store = new SidecarStore(_directory);
        var record = Discovery();
        var path = store.DiscoveryPath(record);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{\"schemaVersion\":1,\"recordKind\":[]}");

        Assert.Throws<IOException>(() => store.PublishDiscovery(record));
    }

    [Theory]
    [InlineData((int)EvaluationOutcome.Incomplete)]
    [InlineData((int)EvaluationOutcome.Fail)]
    [InlineData((int)EvaluationOutcome.NotApplicable)]
    [InlineData((int)EvaluationOutcome.Pass)]
    public void IncompleteOrUnprovenReportsCannotPublishReusableSuccess(int outcomeValue)
    {
        var store = new SidecarStore(_directory);
        var outcome = (EvaluationOutcome)outcomeValue;
        var report = EvaluationReport.CreateSynthetic(outcome, "proven-fixture", "TEST_FIXTURE");
        var bytes = ReportWriter.Serialize(report);
        var material = ProvenMaterial();
        var fingerprint = EvaluationFingerprint.ComputeForProven(material);
        var coverage = new CoverageProvenance("1", report.RunId, "suite", fingerprint, Digest("snapshot"),
            BaselineStatus.Green, Digest("coverage"), 8, "path-map-v1", CompleteRunnerIdentity, true);
        var record = new ProvenEvaluationSidecar("1", SidecarRecordKind.Proven, report.RunId,
            DateTimeOffset.UtcNow, fingerprint, Digest("snapshot"),
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), bytes.LongLength,
            true, [coverage], report.Counts);
        var candidate = Candidate();
        var plan = MutationSelection.Plan(MutationSelection.Bind([candidate], material), material);

        Assert.Throws<EvaluationContractException>(() => store.PublishProven(record, report, material, plan));
        Assert.False(File.Exists(store.ProvenPath(fingerprint)));
    }

    [Fact]
    public void CoverageMustBeFreshGreenAndExactInputCompatible()
    {
        var fingerprint = Fingerprint();
        var valid = new CoverageProvenance("1", "run", "suite", fingerprint, Digest("snapshot"),
            BaselineStatus.Green, Digest("coverage"), 8, "path-map-v1", CompleteRunnerIdentity, true);

        valid.Validate(fingerprint, valid.SnapshotId, valid.RunId, valid.SuiteId);
        Assert.Throws<EvaluationContractException>(() => valid.Validate(Fingerprint("other"), valid.SnapshotId,
            valid.RunId, valid.SuiteId));
        Assert.Throws<EvaluationContractException>(() => (valid with { CollectedFresh = false }).Validate(fingerprint,
            valid.SnapshotId, valid.RunId, valid.SuiteId));
        Assert.Throws<EvaluationContractException>(() => (valid with { Baseline = BaselineStatus.Red }).Validate(fingerprint,
            valid.SnapshotId, valid.RunId, valid.SuiteId));
        Assert.Throws<EvaluationContractException>(() => (valid with { CoverageReportSha256 = Digest("changed") })
            .Validate(Fingerprint("other"), valid.SnapshotId, valid.RunId, valid.SuiteId));
    }

    [Fact]
    public void AbsentIgnoreContractKeepsAnOtherwiseCleanRepositoryClean()
    {
        using var repository = new SnapshotTestRepository();
        repository.WriteText("src/A.cs", "class A { }\n");
        repository.Git("add", ".");
        repository.Git("commit", "-m", "fixture");

        new SidecarStore(repository.Root).PublishDiscovery(Discovery());

        Assert.Equal(string.Empty, repository.Git("status", "--porcelain=v1", "-z"));
        Assert.Equal("*\n", File.ReadAllText(Path.Combine(repository.Root, ".mutate4csharp", ".gitignore")));
    }

    [Fact]
    public void ExistingIgnoreContractIsPreservedAndConflictingOrSpecialContractsAreRefused()
    {
        var stateRoot = Path.Combine(_directory, ".mutate4csharp");
        Directory.CreateDirectory(stateRoot);
        var ignore = Path.Combine(stateRoot, ".gitignore");
        File.WriteAllText(ignore, "# user note\n*\n");
        new SidecarStore(_directory).PublishDiscovery(Discovery());
        Assert.Equal("# user note\n*\n", File.ReadAllText(ignore));

        using var conflicting = new SnapshotTestRepository();
        Directory.CreateDirectory(Path.Combine(conflicting.Root, ".mutate4csharp"));
        conflicting.WriteText(".mutate4csharp/.gitignore", "reports/\n");
        Assert.Throws<IOException>(() => new SidecarStore(conflicting.Root).PublishDiscovery(Discovery()));
        Assert.Equal("reports/\n", File.ReadAllText(Path.Combine(conflicting.Root, ".mutate4csharp", ".gitignore")));

        using var leadingSpace = new SnapshotTestRepository();
        Directory.CreateDirectory(Path.Combine(leadingSpace.Root, ".mutate4csharp"));
        leadingSpace.WriteText(".mutate4csharp/.gitignore", " *\n");
        Assert.Throws<IOException>(() => new SidecarStore(leadingSpace.Root).PublishDiscovery(Discovery()));

        if (!OperatingSystem.IsWindows())
        {
            using var special = new SnapshotTestRepository();
            Directory.CreateDirectory(Path.Combine(special.Root, ".mutate4csharp"));
            File.CreateSymbolicLink(Path.Combine(special.Root, ".mutate4csharp", ".gitignore"), "/dev/null");
            Assert.Throws<IOException>(() => new SidecarStore(special.Root).PublishDiscovery(Discovery()));

            using var dangling = new SnapshotTestRepository();
            Directory.CreateDirectory(Path.Combine(dangling.Root, ".mutate4csharp"));
            File.CreateSymbolicLink(Path.Combine(dangling.Root, ".mutate4csharp", ".gitignore"),
                Path.Combine(dangling.Root, "missing-target"));
            Assert.Throws<IOException>(() => new SidecarStore(dangling.Root).PublishDiscovery(Discovery()));
            Assert.False(File.Exists(Path.Combine(dangling.Root, "missing-target")));
        }
    }

    [Theory]
    [InlineData("*\t\n")]
    [InlineData("*\u00a0\n")]
    [InlineData("foo\r*\n")]
    [InlineData("*\r\r\n")]
    public void NonSpaceTrailingCharactersDoNotSatisfyTheOwnedIgnoreContract(string contents)
    {
        using var repository = new SnapshotTestRepository();
        Directory.CreateDirectory(Path.Combine(repository.Root, ".mutate4csharp"));
        repository.WriteText(".mutate4csharp/.gitignore", contents);
        var expected = Encoding.UTF8.GetBytes(contents);

        Assert.Throws<IOException>(() => new SidecarStore(repository.Root).PublishDiscovery(Discovery()));
        Assert.Equal(expected, File.ReadAllBytes(Path.Combine(repository.Root, ".mutate4csharp", ".gitignore")));
    }

    public static TheoryData<byte[]> NonUtf8IgnoreContracts => new()
    {
        new byte[] { 0xff, 0xfe, 0x2a, 0x00, 0x0a, 0x00 },
        new byte[] { 0xfe, 0xff, 0x00, 0x2a, 0x00, 0x0a },
        new byte[] { 0xff, 0xfe, 0x00, 0x00, 0x2a, 0x00, 0x00, 0x00, 0x0a, 0x00, 0x00, 0x00 },
        new byte[] { 0x00, 0x00, 0xfe, 0xff, 0x00, 0x00, 0x00, 0x2a, 0x00, 0x00, 0x00, 0x0a }
    };

    [Theory]
    [MemberData(nameof(NonUtf8IgnoreContracts))]
    public void Utf16AndUtf32IgnoreContractsAreRefusedWithoutChangingTheirBytes(byte[] contents)
    {
        using var repository = new SnapshotTestRepository();
        Directory.CreateDirectory(Path.Combine(repository.Root, ".mutate4csharp"));
        repository.Write(".mutate4csharp/.gitignore", contents);

        Assert.Throws<IOException>(() => new SidecarStore(repository.Root).PublishDiscovery(Discovery()));
        Assert.Equal(contents, File.ReadAllBytes(Path.Combine(repository.Root, ".mutate4csharp", ".gitignore")));
    }

    [Fact]
    public void Utf8BomIgnoreContractIsAcceptedWithoutChangingItsBytes()
    {
        using var repository = new SnapshotTestRepository();
        Directory.CreateDirectory(Path.Combine(repository.Root, ".mutate4csharp"));
        byte[] contents = [0xef, 0xbb, 0xbf, 0x2a, 0x0a];
        repository.Write(".mutate4csharp/.gitignore", contents);

        new SidecarStore(repository.Root).PublishDiscovery(Discovery());

        Assert.Equal(contents, File.ReadAllBytes(Path.Combine(repository.Root, ".mutate4csharp", ".gitignore")));
    }

    [Theory]
    [InlineData("*\n")]
    [InlineData("*\r\n")]
    [InlineData("*   \n")]
    [InlineData("*   \r\n")]
    public void GitLineEndingsAndTrailingAsciiSpacesSatisfyTheOwnedIgnoreContract(string contents)
    {
        using var repository = new SnapshotTestRepository();
        Directory.CreateDirectory(Path.Combine(repository.Root, ".mutate4csharp"));
        repository.WriteText(".mutate4csharp/.gitignore", contents);
        var expected = Encoding.UTF8.GetBytes(contents);

        new SidecarStore(repository.Root).PublishDiscovery(Discovery());

        Assert.Equal(expected, File.ReadAllBytes(Path.Combine(repository.Root, ".mutate4csharp", ".gitignore")));
    }

    private static DiscoverySidecar Discovery()
    {
        var report = EvaluationReport.CreateSynthetic(EvaluationOutcome.Incomplete, "discovery", "TEST_FIXTURE");
        var bytes = ReportWriter.Serialize(report);
        return new("1", SidecarRecordKind.Discovery, report.RunId, DateTimeOffset.UnixEpoch,
            Fingerprint(), Digest("snapshot"), report.Outcome, false, ["src/A.cs:SCOPE_UNAVAILABLE"],
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), bytes.LongLength);
    }

    private (SidecarStore Store, EvaluationReport Report, ProvenEvaluationSidecar Record,
        EvaluationFingerprintMaterial Material, MutationSelectionPlan Plan) ValidProven()
    {
        var snapshotId = Digest("snapshot");
        var material = ProvenMaterial(snapshotId, ScopePlan.Empty("inputs", ".", null));
        var candidate = Candidate();
        var plan = MutationSelection.Plan(MutationSelection.Bind([candidate], material), material);
        var unit = plan.Reduce(plan.CreatePendingLedger().Single(),
            [new(1, UnitDisposition.Killed,
                [new("MUTANT_KILLED", "Fresh suite killed the mutation.")])]);
        var facts = new EvaluationFacts(BaselineStatus.Green, 1, [unit], false, []);
        var decision = EvaluationReducer.Reduce(facts);
        var report = new EvaluationReport("1", "valid-pass-run", DateTimeOffset.UnixEpoch, "check",
            new("inputs", null, ["src/A.cs"]), ScopePlan.Empty("inputs", ".", null),
            EvaluationReport.DefaultPolicy, BaselineStatus.Green,
            [ValidSuite("suite-a")],
            [unit], decision.Counts, [], decision.Reasons,
            decision.Evidence.Concat([
                new EvaluationEvidence("INPUT_SNAPSHOT", "Frozen input snapshot.",
                    [$"captureId={snapshotId}"]),
                ReportWriter.ConfigurationSuiteEvidence(["suite-a"])
            ]).ToArray(), decision.Outcome, decision.ExitCode);
        var bytes = ReportWriter.Serialize(report);
        var fingerprint = EvaluationFingerprint.ComputeForProven(material);
        var coverage = new CoverageProvenance("1", report.RunId, "suite-a", fingerprint, snapshotId,
            BaselineStatus.Green, Digest("coverage"), 8, "baseline-clone-to-snapshot-v1", CompleteRunnerIdentity, true);
        var record = new ProvenEvaluationSidecar("1", SidecarRecordKind.Proven, report.RunId,
            DateTimeOffset.UnixEpoch, fingerprint, snapshotId,
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), bytes.LongLength,
            true, [coverage], report.Counts);
        return (new SidecarStore(_directory), report, record, material, plan);
    }

    private static SuiteEvidence ValidSuite(string identity) => new(identity, BaselineStatus.Green,
        [new("SUITE_BASELINE", "Fixture baseline with complete coverage and member accounting.",
            ["disposition=Passed", "tests=1", "accountedMembers=Tests.dll", "expectedMembers=Tests.dll",
             "coverageSha256=" + Digest("coverage"), "coverageLength=8",
             "pathMap=baseline-clone-to-snapshot-v1"])]);

    private static MutationCandidate Candidate()
    {
        var identityMaterial = new MutationIdentityMaterial("src/A.cs", "Type.M", "site/1",
            "test.operator", "1", "replacement");
        var mutationId = MutationIdentity.Compute(identityMaterial);
        const string project = "src/App.csproj";
        const string target = "net10.0";
        const string parse = "symbols=A";
        return new(mutationId, EvaluationUnitIdentity.Compute(new(mutationId, project, target, parse)),
            identityMaterial, project, target, parse);
    }

    private static EvaluationFingerprintMaterial ProvenMaterial(string? snapshotId = null, ScopePlan? scopePlan = null) => new(
        [
            EvaluationFingerprint.FromBytes("source", "src/A.cs", "source"u8),
            EvaluationFingerprint.FromBytes("dependency", "packages.lock.json", "dependency"u8)
        ],
        snapshotId ?? Digest("snapshot"),
        ReportWriter.SerializeCanonicalScope(scopePlan ?? ScopePlan.Empty("inputs", ".", null)),
        "configuration-v1", "tool-v1", "operator-v1", "sdk-v1", "runtime-v1",
        CompleteRunnerIdentity, EvaluationReport.DefaultPolicy, ProvenanceComplete: true);

    private static string Fingerprint(string value = "fingerprint") => "sha256:" + Digest(value);
    private static string Digest(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))
        .ToLowerInvariant();

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch { }
    }
}
