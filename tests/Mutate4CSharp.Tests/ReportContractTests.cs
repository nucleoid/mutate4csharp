using System.Text.Json;
using Json.Schema;

namespace Mutate4CSharp.Tests;

public sealed class ReportContractTests : IDisposable
{
    private static readonly JsonSchema ReportSchema = JsonSchema.FromText(File.ReadAllText(
        Path.GetFullPath("../../../../../docs/contracts/evaluation-report-v1.schema.json", AppContext.BaseDirectory)));
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "mutate4csharp-report-tests", Guid.NewGuid().ToString("N"));

    public ReportContractTests() => Directory.CreateDirectory(_directory);

    [Theory]
    [InlineData((int)EvaluationOutcome.Pass)]
    [InlineData((int)EvaluationOutcome.Fail)]
    [InlineData((int)EvaluationOutcome.Incomplete)]
    [InlineData((int)EvaluationOutcome.NotApplicable)]
    public void SerializesEveryVersionOneOutcomeWithStableEnvelope(int outcomeValue)
    {
        var outcome = (EvaluationOutcome)outcomeValue;
        var report = EvaluationReport.CreateSynthetic(outcome, $"test-{outcome}", "TEST_FIXTURE");
        var json = System.Text.Encoding.UTF8.GetString(ReportWriter.Serialize(report));
        using var document = JsonDocument.Parse(json);

        Assert.Equal("1", document.RootElement.GetProperty("schemaVersion").GetString());
        var expected = outcome == EvaluationOutcome.NotApplicable ? "NOT_APPLICABLE" : outcome.ToString().ToUpperInvariant();
        Assert.Equal(expected, document.RootElement.GetProperty("outcome").GetString());
        Assert.NotEqual(JsonValueKind.Undefined, document.RootElement.GetProperty("evidence").ValueKind);
        Assert.DoesNotContain(Path.GetTempPath(), json, StringComparison.Ordinal);

        var schemaResult = ReportSchema.Evaluate(document.RootElement, new EvaluationOptions
        {
            OutputFormat = OutputFormat.List,
            RequireFormatValidation = true
        });
        Assert.True(schemaResult.IsValid, schemaResult.ToString());
    }

    [Fact]
    public void AtomicWriterLeavesExistingReportUntouchedWhenDestinationIsActivelyLocked()
    {
        var path = Path.Combine(_directory, "report.json");
        File.WriteAllText(path, "old");
        var lockPath = ReportWriter.LockPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);
        using var heldLock = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None);

        Assert.Throws<IOException>(() => ReportWriter.Write(path,
            EvaluationReport.CreateSynthetic(EvaluationOutcome.Incomplete, "locked", "TEST_FIXTURE")));

        Assert.Equal("old", File.ReadAllText(path));
        Assert.Empty(Directory.EnumerateFiles(_directory, ".report.json.*.tmp"));
    }

    [Fact]
    public void AtomicWriterProducesOnlyCompleteJson()
    {
        var path = Path.Combine(_directory, "report.json");
        var report = EvaluationReport.CreateSynthetic(EvaluationOutcome.Incomplete, "atomic", "TEST_FIXTURE");

        ReportWriter.Write(path, report);

        using var parsed = JsonDocument.Parse(File.ReadAllBytes(path));
        Assert.Equal("atomic", parsed.RootElement.GetProperty("runId").GetString());
        Assert.True(File.Exists(ReportWriter.LockPath(path)));
        Assert.Empty(Directory.EnumerateFiles(_directory, ".report.json.*.tmp"));
    }

    [Fact]
    public void LeftoverUnlockedLockFileDoesNotBlockAWrite()
    {
        var path = Path.Combine(_directory, "recover.json");
        var lockPath = ReportWriter.LockPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);
        File.WriteAllText(lockPath, "stale metadata");

        ReportWriter.Write(path,
            EvaluationReport.CreateSynthetic(EvaluationOutcome.Incomplete, "recovered", "TEST_FIXTURE"));

        using var parsed = JsonDocument.Parse(File.ReadAllBytes(path));
        Assert.Equal("recovered", parsed.RootElement.GetProperty("runId").GetString());
    }

    [Fact]
    public void ExistingReportFromWriterCanBeAtomicallyReplaced()
    {
        var path = Path.Combine(_directory, "replace.json");
        ReportWriter.Write(path,
            EvaluationReport.CreateSynthetic(EvaluationOutcome.Incomplete, "first", "TEST_FIXTURE"));

        ReportWriter.Write(path,
            EvaluationReport.CreateSynthetic(EvaluationOutcome.Incomplete, "second", "TEST_FIXTURE"));

        using var parsed = JsonDocument.Parse(File.ReadAllBytes(path));
        Assert.Equal("second", parsed.RootElement.GetProperty("runId").GetString());
    }

    [Fact]
    public void ValidatorEnforcesLedgerEvidenceAndPublishedBounds()
    {
        var valid = EvaluationReport.CreateSynthetic(EvaluationOutcome.Incomplete, "validation", "TEST_FIXTURE");
        var unit = valid.Units[0] with { Evidence = [] };
        var badCounts = valid.Counts with { Selected = 1, Executed = 1, Killed = 1 };
        var missingEvidence = valid with { Units = [unit], Counts = badCounts };
        Assert.Throws<EvaluationContractException>(() => ReportWriter.Serialize(missingEvidence));

        var oversized = valid with { Reasons = [new("TEST_REASON", new string('x', 1025))] };
        Assert.Throws<EvaluationContractException>(() => ReportWriter.Serialize(oversized));
    }

    [Fact]
    public void ValidatorRejectsOutcomeThatContradictsFactsOrPlanMode()
    {
        var pass = EvaluationReport.CreateSynthetic(EvaluationOutcome.Pass, "pass", "TEST_FIXTURE");

        Assert.Throws<EvaluationContractException>(() => ReportWriter.Serialize(pass with
        {
            Baseline = BaselineStatus.Red
        }));
        Assert.Throws<EvaluationContractException>(() => ReportWriter.Serialize(pass with { Mode = "plan" }));
    }

    [Fact]
    public void SchemaRejectsSemanticallyContradictoryPass()
    {
        var pass = EvaluationReport.CreateSynthetic(EvaluationOutcome.Pass, "schema-pass", "TEST_FIXTURE");
        var json = System.Text.Encoding.UTF8.GetString(ReportWriter.Serialize(pass));
        using var document = JsonDocument.Parse(json.Replace("\"baseline\": \"GREEN\"",
            "\"baseline\": \"UNKNOWN\"", StringComparison.Ordinal));

        var result = ReportSchema.Evaluate(document.RootElement);

        Assert.False(result.IsValid);
    }

    [Fact]
    public void UnstableDispositionIsSchemaValidButAlwaysIncomplete()
    {
        var unit = StabilityEvidence.Reduce("mutation:v1:" + new string('a', 64),
            "evaluation:v1:" + new string('b', 64),
        [
            new(1, UnitDisposition.Killed, [new("TEST_RUN", "Attempt one killed the mutant.")]),
            new(2, UnitDisposition.Survived, [new("TEST_RUN", "Attempt two survived.")])
        ], EvaluationReport.DefaultPolicy with { StabilityRepetitions = 2 });
        var facts = new EvaluationFacts(BaselineStatus.Green, 1, [unit], false, []);
        var decision = EvaluationReducer.Reduce(facts);
        var report = EvaluationReport.CreateSynthetic(EvaluationOutcome.Incomplete, "unstable", "TEST_FIXTURE") with
        {
            Baseline = facts.Baseline,
            Units = facts.Units,
            Counts = decision.Counts,
            IncompleteConditions = facts.IncompleteConditions,
            Reasons = decision.Reasons,
            Evidence = decision.Evidence,
            Outcome = decision.Outcome,
            ExitCode = decision.ExitCode
        };
        using var document = JsonDocument.Parse(ReportWriter.Serialize(report));

        Assert.Equal("UNSTABLE", document.RootElement.GetProperty("units")[0]
            .GetProperty("disposition").GetString());
        Assert.Equal("INCOMPLETE", document.RootElement.GetProperty("outcome").GetString());
        Assert.True(ReportSchema.Evaluate(document.RootElement).IsValid);
    }

    [Fact]
    public void WriterRejectsInvalidExecutionPolicyValues()
    {
        var report = EvaluationReport.CreateSynthetic(EvaluationOutcome.Incomplete, "bad-policy", "TEST_FIXTURE")
            with
        { Policy = EvaluationReport.DefaultPolicy with { MutationCap = 0 } };

        Assert.Throws<EvaluationContractException>(() => ReportWriter.Serialize(report));
    }

    [Fact]
    public void ReportCanBeReplacedAfterLockDeletion()
    {
        var path = Path.Combine(_directory, "deleted-lock.json");
        ReportWriter.Write(path, EvaluationReport.CreateSynthetic(EvaluationOutcome.Incomplete, "first", "TEST_FIXTURE"));
        File.Delete(ReportWriter.LockPath(path));
        ReportWriter.Write(path, EvaluationReport.CreateSynthetic(EvaluationOutcome.Incomplete, "second", "TEST_FIXTURE"));
        using var parsed = JsonDocument.Parse(File.ReadAllBytes(path));
        Assert.Equal("second", parsed.RootElement.GetProperty("runId").GetString());
    }

    [Fact]
    public void LockIsAdjacentToReportRatherThanSharedAcrossUsers()
    {
        var path = Path.Combine(_directory, "adjacent.json");
        Assert.Equal(_directory, Path.GetDirectoryName(ReportWriter.LockPath(path)));
    }

    [Theory]
    [InlineData((int)EvaluationOutcome.Fail, 0)]
    [InlineData((int)EvaluationOutcome.Incomplete, 3)]
    [InlineData((int)EvaluationOutcome.NotApplicable, 3)]
    public void SchemaAndWriterRejectContradictoryOutcomeExit(int value, int exit)
    {
        var report = EvaluationReport.CreateSynthetic((EvaluationOutcome)value, "negative", "TEST_FIXTURE");
        Assert.Throws<EvaluationContractException>(() => ReportWriter.Serialize(report with { ExitCode = exit }));
        var node = System.Text.Json.Nodes.JsonNode.Parse(ReportWriter.Serialize(report))!;
        node["exitCode"] = exit;
        using var document = JsonDocument.Parse(node.ToJsonString());
        Assert.False(ReportSchema.Evaluate(document.RootElement).IsValid);
    }

    [Fact]
    public async Task SeparateProcessWritersFailVisiblyWhileLockHeldAndRecover()
    {
        var path = Path.Combine(_directory, "process.json");
        ReportWriter.Write(path, EvaluationReport.CreateSynthetic(EvaluationOutcome.Incomplete, "original", "TEST_FIXTURE"));
        var original = File.ReadAllBytes(path);
        using (var held = new FileStream(ReportWriter.LockPath(path), FileMode.Open, FileAccess.Write, FileShare.None))
        {
            var codes = await Task.WhenAll(RunCheck(path), RunCheck(path));
            Assert.All(codes, code => Assert.Equal(4, code));
            Assert.Equal(original, File.ReadAllBytes(path));
        }
        Assert.Equal(4, await RunCheck(path));
        using var parsed = JsonDocument.Parse(File.ReadAllBytes(path));
        Assert.NotEqual("original", parsed.RootElement.GetProperty("runId").GetString());
        Assert.Empty(Directory.EnumerateFiles(_directory, ".process.json.*.tmp"));
    }

    [Fact]
    public async Task UnusableLockDestinationFailsClosedWithoutChangingReport()
    {
        var path = Path.Combine(_directory, "unusable.json");
        ReportWriter.Write(path, EvaluationReport.CreateSynthetic(EvaluationOutcome.Incomplete, "original", "TEST_FIXTURE"));
        var original = File.ReadAllBytes(path);
        File.Delete(ReportWriter.LockPath(path));
        Directory.CreateDirectory(ReportWriter.LockPath(path));
        Assert.Equal(4, await RunCheck(path));
        Assert.Equal(original, File.ReadAllBytes(path));
    }

    [Fact]
    public async Task ConcurrentFirstProcessWritersLeaveACompleteReportAndPermitRerun()
    {
        var path = Path.Combine(_directory, "first-process.json");
        var codes = await Task.WhenAll(RunCheck(path), RunCheck(path));
        Assert.All(codes, code => Assert.Equal(4, code));
        using var parsed = JsonDocument.Parse(File.ReadAllBytes(path));
        Assert.True(ReportSchema.Evaluate(parsed.RootElement).IsValid);
        var firstRun = parsed.RootElement.GetProperty("runId").GetString();
        Assert.Equal(4, await RunCheck(path));
        using var rerun = JsonDocument.Parse(File.ReadAllBytes(path));
        Assert.NotEqual(firstRun, rerun.RootElement.GetProperty("runId").GetString());
    }

    [Fact]
    public async Task NonWritableAdjacentLockFailsClosed()
    {
        if (OperatingSystem.IsWindows()) return; // Windows ACL behavior is covered by CI's unusable-destination case.
        var path = Path.Combine(_directory, "permissions.json");
        ReportWriter.Write(path, EvaluationReport.CreateSynthetic(EvaluationOutcome.Incomplete, "original", "TEST_FIXTURE"));
        var original = File.ReadAllBytes(path);
        var lockPath = ReportWriter.LockPath(path);
        File.SetUnixFileMode(lockPath, UnixFileMode.UserRead);
        try
        {
            Assert.Throws<UnauthorizedAccessException>(() => ReportWriter.Write(path,
                EvaluationReport.CreateSynthetic(EvaluationOutcome.Incomplete, "refused", "TEST_FIXTURE")));
            Assert.Equal(4, await RunCheck(path));
            Assert.Equal(original, File.ReadAllBytes(path));
        }
        finally { File.SetUnixFileMode(lockPath, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
    }

    private static async Task<int> RunCheck(string path)
    {
        var start = new System.Diagnostics.ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        start.ArgumentList.Add(typeof(Program).Assembly.Location);
        start.ArgumentList.Add("check"); start.ArgumentList.Add("--base"); start.ArgumentList.Add("HEAD");
        start.ArgumentList.Add("--report"); start.ArgumentList.Add(path);
        using var process = System.Diagnostics.Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        await Task.WhenAll(stdout, stderr);
        return process.ExitCode;
    }

    public void Dispose() { try { Directory.Delete(_directory, true); } catch { } }
}
