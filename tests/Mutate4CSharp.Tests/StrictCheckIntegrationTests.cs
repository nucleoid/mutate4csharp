using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Mutate4CSharp.Tests;

public sealed class StrictCheckIntegrationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "mutate4csharp-strict", Guid.NewGuid().ToString("N"));

    public StrictCheckIntegrationTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public void ExecutionBoundaryIntegrityHasAnExplicitRunLevelReason()
    {
        var method = typeof(EvaluationCoordinator).GetMethod("SnapshotFailure",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);

        var reason = Assert.IsType<EvaluationReason>(method!.Invoke(null,
            [new ExecutionBoundaryIntegrityException("private package cache changed")]));

        Assert.Equal("EXECUTION_BOUNDARY_INTEGRITY", reason.Code);
    }

    [Fact]
    public void RequiresExactlyOneStrictSelectionMode()
    {
        Assert.NotNull(Cli.Parse(["check"]).Error);
        Assert.NotNull(Cli.Parse(["check", "--base", "HEAD", "--input", "src/A.cs"]).Error);
        Assert.Null(Cli.Parse(["check", "--base", "HEAD"]).Error);
        Assert.Null(Cli.Parse(["check", "--input", "src/A.cs", "--input", "src/B.cs"]).Error);
    }

    [Theory]
    [InlineData("--lines", "1")]
    [InlineData("--reuse-coverage", null)]
    [InlineData("--coverage-report", "coverage.xml")]
    [InlineData("--update-manifest", null)]
    public void RejectsLegacyProofFlagsInStrictMode(string flag, string? value)
    {
        var args = value is null
            ? new[] { "check", "--base", "HEAD", flag }
            : new[] { "check", "--base", "HEAD", flag, value };

        Assert.NotNull(Cli.Parse(args).Error);
    }

    [Fact]
    public async Task PublicStrictCheckFailsClosedUntilFrozenCaptureExists()
    {
        using var repository = new SnapshotTestRepository();
        repository.WriteText("src/A.cs", "class A { }\n");
        repository.Git("add", ".");
        repository.Git("commit", "-m", "fixture");
        var reportPath = Path.Combine(_directory, "strict.json");
        var oldOut = Console.Out;
        var output = new StringWriter();
        var previous = Environment.CurrentDirectory;
        Environment.CurrentDirectory = repository.Root;
        Console.SetOut(output);
        int code;
        try { code = await Program.Main(["check", "--base", "HEAD", "--report", reportPath]); }
        finally
        {
            Console.SetOut(oldOut);
            Environment.CurrentDirectory = previous;
        }

        Assert.Equal(4, code);
        using var report = JsonDocument.Parse(File.ReadAllBytes(reportPath));
        var runId = report.RootElement.GetProperty("runId").GetString();
        Assert.False(string.IsNullOrWhiteSpace(runId));
        Assert.Contains($"Run ID: {runId}", output.ToString(), StringComparison.Ordinal);
        Assert.Equal("INCOMPLETE", report.RootElement.GetProperty("outcome").GetString());
        Assert.Contains(report.RootElement.GetProperty("reasons").EnumerateArray(),
            reason => reason.GetProperty("code").GetString() is
                "ENUMERATION_SCOPE_INCOMPLETE" or "ENUMERATION_CONTEXT_UNSUPPORTED" or
                "ENUMERATION_CONFIGURATION_REQUIRED");
        Assert.DoesNotContain(report.RootElement.GetProperty("reasons").EnumerateArray(),
            reason => reason.GetProperty("code").GetString() == "ENUMERATION_NOT_IMPLEMENTED");
        var snapshot = report.RootElement.GetProperty("evidence").EnumerateArray()
            .Single(item => item.GetProperty("kind").GetString() == "INPUT_SNAPSHOT");
        Assert.Contains(snapshot.GetProperty("diagnostics").EnumerateArray(),
            item => item.GetString()?.StartsWith("captureId=", StringComparison.Ordinal) == true && item.GetString()!.Length == 74);
        Assert.NotEmpty(report.RootElement.GetProperty("evidence").EnumerateArray());
    }

    [Fact]
    public async Task SupportedCapturedScopePublishesBoundEnumerationAndBaselineEvidence()
    {
        using var repository = StrictEnumerationRepository("public int Value() => 0;");
        repository.Git("add", ".");
        repository.Git("commit", "-m", "baseline");
        repository.WriteText("src/App/Flag.cs", "public sealed class Flag { public int Value() => 1; }\n");
        var reportPath = Path.Combine(_directory, "enumerated.json");
        var previous = Environment.CurrentDirectory;
        Environment.CurrentDirectory = repository.Root;
        EvaluationRunResult result;
        try
        {
            result = await new EvaluationCoordinator().RunAsync(
                new(false, "HEAD", [], reportPath, "enumerated-run"), CancellationToken.None);
        }
        finally { Environment.CurrentDirectory = previous; }

        Assert.Equal(4, result.Report.ExitCode);
        Assert.Equal(EvaluationOutcome.Incomplete, result.Report.Outcome);
        Assert.Equal(1, result.Report.Counts.Enumerated);
        var unit = Assert.Single(result.Report.Units);
        Assert.Equal(UnitDisposition.Omitted, unit.Disposition);
        Assert.Equal(0, result.Report.Counts.Executed);
        Assert.Equal(0, result.Report.Counts.Errors);
        Assert.Equal(1, result.Report.Counts.Omitted);
        Assert.True(MutationIdentity.IsMutationId(unit.UnitId));
        Assert.True(EvaluationUnitIdentity.IsEvaluationUnitId(unit.EvaluationUnitId));
        Assert.Contains(result.Report.Reasons, reason => reason.Code == "FINALIZATION_PENDING");
        Assert.DoesNotContain(result.Report.Reasons, reason => reason.Code == "ENUMERATION_NOT_IMPLEMENTED");
        Assert.Contains(result.Report.Evidence, item => item.Kind == "MUTATION_PLAN" &&
            item.Diagnostics?.Any(value => value.StartsWith("planFingerprint=sha256:",
                StringComparison.Ordinal)) == true);
    }

    [Fact]
    public async Task ChangedNonProductionInputRemainsValidatedScopeIncompleteRatherThanIntegrityFailure()
    {
        using var repository = StrictEnumerationRepository("public int Value() => 0;");
        repository.WriteText("README.md", "baseline\n");
        repository.Git("add", ".");
        repository.Git("commit", "-m", "baseline");
        repository.WriteText("src/App/Flag.cs", "public sealed class Flag { public int Value() => 1; }\n");
        repository.WriteText("README.md", "changed\n");
        var reportPath = Path.Combine(_directory, "scope-incomplete.json");
        var previous = Environment.CurrentDirectory;
        Environment.CurrentDirectory = repository.Root;
        EvaluationRunResult result;
        try
        {
            result = await new EvaluationCoordinator().RunAsync(
                new(false, "HEAD", [], reportPath, "scope-incomplete-run"), CancellationToken.None);
        }
        finally { Environment.CurrentDirectory = previous; }

        Assert.Equal(4, result.Report.ExitCode);
        Assert.Equal(EvaluationOutcome.Incomplete, result.Report.Outcome);
        Assert.Null(result.Report.Counts.Enumerated);
        Assert.Contains(result.Report.IncompleteConditions,
            reason => reason.Code == "ENUMERATION_SCOPE_INCOMPLETE");
        Assert.Contains(result.Report.IncompleteConditions,
            reason => reason.Code == "UNSUPPORTED_CHANGED_INPUT");
    }

    [Fact]
    public async Task OriginalWorkspaceDriftAfterEnumerationInvalidatesTheBoundPlan()
    {
        using var repository = StrictEnumerationRepository("public int Value() => 0;");
        repository.Git("add", ".");
        repository.Git("commit", "-m", "baseline");
        repository.WriteText("src/App/Flag.cs",
            "public sealed class Flag { public int Value() => 1; }\n");
        var validationPass = 0;
        var captureOptions = SnapshotCaptureOptions.Default with
        {
            Hook = (stage, relativePath) =>
            {
                if (stage != SnapshotCaptureStage.BeforeOriginalFileHashed ||
                    relativePath != "src/App/Flag.cs" || ++validationPass != 2) return;
                repository.WriteText("src/App/Flag.cs",
                    "public sealed class Flag { public int Value() => 2; }\n");
            }
        };
        var reportPath = Path.Combine(_directory, "post-enumeration-drift.json");
        var previous = Environment.CurrentDirectory;
        Environment.CurrentDirectory = repository.Root;
        EvaluationRunResult result;
        try
        {
            result = await new EvaluationCoordinator(captureOptions).RunAsync(
                new(false, "HEAD", [], reportPath, "post-enumeration-drift"), CancellationToken.None);
        }
        finally { Environment.CurrentDirectory = previous; }

        Assert.Equal(4, result.Report.ExitCode);
        Assert.Equal(EvaluationOutcome.Incomplete, result.Report.Outcome);
        Assert.Null(result.SnapshotId);
        Assert.Null(result.Report.Counts.Enumerated);
        Assert.Empty(result.Report.Units);
        Assert.Contains(result.Report.Reasons, reason => reason.Code == "SNAPSHOT_DIVERGED");
        Assert.DoesNotContain(result.Report.Evidence, item => item.Kind == "MUTATION_PLAN");
    }

    [Fact]
    public async Task CompleteZeroSiteEnumerationIsNotReportedAsUnknown()
    {
        using var repository = StrictEnumerationRepository("public int Value() => 2;");
        repository.Git("add", ".");
        repository.Git("commit", "-m", "baseline");
        repository.WriteText("src/App/Flag.cs", "public sealed class Flag { public int Value() => 3; }\n");
        var reportPath = Path.Combine(_directory, "zero.json");
        var previous = Environment.CurrentDirectory;
        Environment.CurrentDirectory = repository.Root;
        EvaluationRunResult result;
        try
        {
            result = await new EvaluationCoordinator().RunAsync(
                new(false, "HEAD", [], reportPath, "zero-run"), CancellationToken.None);
        }
        finally { Environment.CurrentDirectory = previous; }

        Assert.Equal(0, result.Report.Counts.Enumerated);
        Assert.Empty(result.Report.Units);
        Assert.Contains(result.Report.Reasons, reason => reason.Code == "FINALIZATION_PENDING");
        Assert.DoesNotContain(result.Report.Reasons, reason => reason.Code == "ENUMERATION_INCOMPLETE");
    }

    [Fact]
    public async Task ExactIdRequestFreshlyRebindsTheCurrentPlanAsDiagnosticOnly()
    {
        using var repository = StrictEnumerationRepository("public int Value() => 0;");
        repository.Git("add", ".");
        repository.Git("commit", "-m", "baseline");
        repository.WriteText("src/App/Flag.cs", "public sealed class Flag { public int Value() => 1; }\n");
        var firstReport = Path.Combine(_directory, "full-plan.json");
        var targetedReport = Path.Combine(_directory, "targeted-plan.json");
        var staleReport = Path.Combine(_directory, "stale-targeted-plan.json");
        var previous = Environment.CurrentDirectory;
        Environment.CurrentDirectory = repository.Root;
        try
        {
            var coordinator = new EvaluationCoordinator();
            var full = await coordinator.RunAsync(
                new(false, "HEAD", [], firstReport, "full-plan"), CancellationToken.None);
            var mutation = Assert.Single(full.Report.Units).UnitId;
            var planFingerprint = Assert.Single(full.Report.Evidence,
                    item => item.Kind == "MUTATION_PLAN").Diagnostics!
                .Single(value => value.StartsWith("planFingerprint=", StringComparison.Ordinal))[16..];

            var targeted = await coordinator.RunAsync(new(false, "HEAD", [], targetedReport,
                "targeted-plan", MutationIds: [mutation], PlanFingerprint: planFingerprint),
                CancellationToken.None);

            Assert.True(targeted.Report.DiagnosticPartial);
            Assert.Contains(targeted.Report.IncompleteConditions,
                reason => reason.Code == MutationSelection.TargetedDiagnosticCode);
            var exactEvidence = Assert.Single(targeted.Report.Evidence,
                item => item.Kind == "EXACT_ID_REQUEST");
            Assert.DoesNotContain("Executed", exactEvidence.Summary, StringComparison.Ordinal);

            var stale = await coordinator.RunAsync(new(false, "HEAD", [], staleReport,
                "stale-targeted-plan", MutationIds: [mutation],
                PlanFingerprint: "sha256:" + new string('0', 64)), CancellationToken.None);
            Assert.Contains(stale.Report.IncompleteConditions,
                reason => reason.Code == "TARGET_SELECTION_INVALID");
            Assert.DoesNotContain(stale.Report.IncompleteConditions,
                reason => reason.Code == "SNAPSHOT_VALIDATION_FAILED");
        }
        finally { Environment.CurrentDirectory = previous; }
    }

    [Fact]
    public async Task RefusedReportWritePrintsCurrentRunIdAndCannotBeMistakenForStaleReport()
    {
        using var repository = new SnapshotTestRepository();
        repository.WriteText("src/A.cs", "class A { }\n");
        repository.Git("add", ".");
        repository.Git("commit", "-m", "fixture");
        var reportPath = Path.Combine(_directory, "stale.json");
        ReportWriter.Write(reportPath,
            EvaluationReport.CreateSynthetic(EvaluationOutcome.Incomplete, "stale-run", "TEST_FIXTURE"));
        using var held = new FileStream(ReportWriter.LockPath(reportPath), FileMode.Open,
            FileAccess.Write, FileShare.None);
        var oldError = Console.Error;
        var error = new StringWriter();
        var previous = Environment.CurrentDirectory;
        Environment.CurrentDirectory = repository.Root;
        Console.SetError(error);
        int code;
        try { code = await Program.Main(["check", "--base", "HEAD", "--report", reportPath]); }
        finally
        {
            Console.SetError(oldError);
            Environment.CurrentDirectory = previous;
        }

        Assert.Equal(4, code);
        var marker = "Run ID: ";
        var markerIndex = error.ToString().IndexOf(marker, StringComparison.Ordinal);
        Assert.True(markerIndex >= 0, error.ToString());
        var currentRunId = error.ToString()[(markerIndex + marker.Length)..]
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)[0].Trim();
        Assert.NotEqual("stale-run", currentRunId);
        using var stale = JsonDocument.Parse(File.ReadAllBytes(reportPath));
        Assert.Equal("stale-run", stale.RootElement.GetProperty("runId").GetString());
    }

    [Fact]
    public async Task SnapshotRefusalWritesCurrentIncompleteReport()
    {
        using var repository = new SnapshotTestRepository();
        repository.WriteText("src/A.cs", "class A { }\n");
        repository.Git("add", ".");
        repository.Git("commit", "-m", "fixture");
        repository.WriteText(".env", "secret\n");
        var reportPath = Path.Combine(_directory, "refused.json");
        var options = new StrictCheckOptions(false, null, [Path.Combine(repository.Root, ".env")],
            reportPath, "refused-current-run");

        var result = await new EvaluationCoordinator().RunAsync(options, CancellationToken.None);

        Assert.Equal(4, result.Report.ExitCode);
        Assert.Null(result.SnapshotId);
        using var report = JsonDocument.Parse(File.ReadAllBytes(reportPath));
        Assert.Equal("refused-current-run", report.RootElement.GetProperty("runId").GetString());
        Assert.Contains(report.RootElement.GetProperty("reasons").EnumerateArray(),
            reason => reason.GetProperty("code").GetString() == "SNAPSHOT_REFUSED");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LinuxFifoInputIsRefusedWithoutBlocking(bool explicitInput)
    {
        if (!OperatingSystem.IsLinux()) return;
        using var repository = new SnapshotTestRepository();
        repository.WriteText("src/A.cs", "class A { }\n");
        repository.Git("add", ".");
        repository.Git("commit", "-m", "fixture");
        var fifo = Path.Combine(repository.Root, "src/A.cs");
        File.Delete(fifo);
        Assert.Equal(0, MkFifo(fifo, 0x180));
        var reportPath = Path.Combine(_directory, $"fifo-{explicitInput}.json");
        var info = ChildProcess(repository.Root,
            explicitInput ? ["check", "--input", fifo, "--report", reportPath] :
                ["check", "--base", "HEAD", "--report", reportPath]);
        using var process = Process.Start(info)!;
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await process.WaitForExitAsync(deadline.Token);
            Assert.Equal(4, process.ExitCode);
            using var report = JsonDocument.Parse(File.ReadAllBytes(reportPath));
            Assert.Contains(report.RootElement.GetProperty("reasons").EnumerateArray(),
                reason => reason.GetProperty("code").GetString() == "SNAPSHOT_REFUSED");
        }
        finally
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
        }
    }

    [Fact]
    public async Task StagingCreationFailureReplacesStaleReportWithCurrentEnvironmentFailure()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var repository = new SnapshotTestRepository();
        repository.WriteText("src/A.cs", "class A { }\n");
        repository.Git("add", ".");
        repository.Git("commit", "-m", "fixture");
        var reportPath = Path.Combine(_directory, "environment-stale.json");
        ReportWriter.Write(reportPath,
            EvaluationReport.CreateSynthetic(EvaluationOutcome.Incomplete, "stale-run", "TEST_FIXTURE"));
        var unusableTemp = Path.Combine(_directory, "not-a-directory");
        File.WriteAllText(unusableTemp, "file");
        var info = ChildProcess(repository.Root,
            ["check", "--base", "HEAD", "--report", reportPath]);
        info.Environment["TMPDIR"] = unusableTemp;
        using var process = Process.Start(info)!;
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(4, process.ExitCode);
        using var report = JsonDocument.Parse(File.ReadAllBytes(reportPath));
        Assert.NotEqual("stale-run", report.RootElement.GetProperty("runId").GetString());
        Assert.Contains(report.RootElement.GetProperty("reasons").EnumerateArray(),
            reason => reason.GetProperty("code").GetString() == "SNAPSHOT_ENVIRONMENT");
    }

    [Fact]
    public async Task NormalStrictChildRemovesItsProcessPrivateTempTree()
    {
        using var repository = new SnapshotTestRepository();
        repository.WriteText("src/A.cs", "class A { }\n");
        repository.Git("add", ".");
        repository.Git("commit", "-m", "fixture");
        var reportPath = Path.Combine(_directory, "normal-child.json");
        var childTemp = Path.Combine(_directory, "normal-child-tmp");
        Directory.CreateDirectory(childTemp);
        var info = ChildProcess(repository.Root,
            ["check", "--base", "HEAD", "--report", reportPath]);
        info.Environment["TMPDIR"] = childTemp;
        using var process = Process.Start(info)!;
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(4, process.ExitCode);
        Assert.Empty(Directory.EnumerateFileSystemEntries(childTemp));
    }

    [Fact]
    public async Task GitStartupFailureReplacesStaleReportWithCurrentSnapshotRefusal()
    {
        using var repository = new SnapshotTestRepository();
        repository.WriteText("src/A.cs", "class A { }\n");
        repository.Git("add", ".");
        repository.Git("commit", "-m", "fixture");
        var reportPath = Path.Combine(_directory, "missing-git-stale.json");
        ReportWriter.Write(reportPath,
            EvaluationReport.CreateSynthetic(EvaluationOutcome.Incomplete, "stale-run", "TEST_FIXTURE"));
        var captureOptions = SnapshotCaptureOptions.Default with { GitExecutable = "git-that-does-not-exist" };
        var coordinator = new EvaluationCoordinator(captureOptions);
        var options = new StrictCheckOptions(false, "HEAD", [], reportPath, "missing-git-current-run");
        var previous = Environment.CurrentDirectory;
        Environment.CurrentDirectory = repository.Root;
        try
        {
            var result = await coordinator.RunAsync(options, CancellationToken.None);

            Assert.Equal(4, result.Report.ExitCode);
            Assert.Null(result.SnapshotId);
            using var report = JsonDocument.Parse(File.ReadAllBytes(reportPath));
            Assert.Equal("missing-git-current-run", report.RootElement.GetProperty("runId").GetString());
            Assert.Contains(report.RootElement.GetProperty("reasons").EnumerateArray(),
                reason => reason.GetProperty("code").GetString() == "SNAPSHOT_REFUSED");
        }
        finally { Environment.CurrentDirectory = previous; }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task UnexpectedNativeValidationFailureReplacesStaleReportWithCurrentFailure(
        int failingValidationPass)
    {
        using var repository = new SnapshotTestRepository();
        repository.WriteText("src/A.cs", "class A { }\n");
        repository.Git("add", ".");
        repository.Git("commit", "-m", "fixture");
        var reportPath = Path.Combine(_directory, $"native-validation-{failingValidationPass}-stale.json");
        ReportWriter.Write(reportPath,
            EvaluationReport.CreateSynthetic(EvaluationOutcome.Incomplete, "stale-run", "TEST_FIXTURE"));
        var validationPass = 0;
        var captureOptions = SnapshotCaptureOptions.Default with
        {
            Hook = (stage, _) =>
            {
                if (stage == SnapshotCaptureStage.BeforeOriginalFileHashed &&
                    ++validationPass == failingValidationPass)
                    throw new EntryPointNotFoundException("simulated missing fstat");
            }
        };
        var coordinator = new EvaluationCoordinator(captureOptions);
        var options = new StrictCheckOptions(false, "HEAD", [], reportPath, "native-failure-current-run");
        var previous = Environment.CurrentDirectory;
        Environment.CurrentDirectory = repository.Root;
        try
        {
            var result = await coordinator.RunAsync(options, CancellationToken.None);

            Assert.Equal(4, result.Report.ExitCode);
            Assert.Null(result.SnapshotId);
            using var report = JsonDocument.Parse(File.ReadAllBytes(reportPath));
            Assert.Equal("native-failure-current-run", report.RootElement.GetProperty("runId").GetString());
            Assert.Contains(report.RootElement.GetProperty("reasons").EnumerateArray(),
                reason => reason.GetProperty("code").GetString() == "SNAPSHOT_VALIDATION_FAILED");
            Assert.False(report.RootElement.GetProperty("scopePlan").GetProperty("isComplete").GetBoolean());
            Assert.Contains(report.RootElement.GetProperty("scopePlan").GetProperty("reasons").EnumerateArray(),
                reason => reason.GetProperty("code").GetString() == "SCOPE_UNAVAILABLE");
        }
        finally { Environment.CurrentDirectory = previous; }
    }

    [Fact]
    public async Task CaptureAndCleanupFailureWritesCurrentCleanupReportWithBothFailures()
    {
        using var repository = new SnapshotTestRepository();
        repository.WriteText("src/A.cs", "class A { }\n");
        repository.Git("add", ".");
        repository.Git("commit", "-m", "fixture");
        var reportPath = Path.Combine(_directory, "cleanup-failure.json");
        var parent = OwnedDirectory.PrivateParent("snapshots");
        var before = Directory.Exists(parent)
            ? Directory.EnumerateDirectories(parent).ToHashSet(StringComparer.Ordinal)
            : [];
        string? damagedRoot = null;
        var captureOptions = SnapshotCaptureOptions.Default with
        {
            Hook = (stage, _) =>
            {
                if (stage != SnapshotCaptureStage.BeforeCaptureValidation) return;
                damagedRoot = Directory.EnumerateDirectories(parent).Single(path => !before.Contains(path));
                File.WriteAllText(Path.Combine(damagedRoot, ".mutate4csharp-owner"), "wrong-owner");
                throw new SnapshotDivergedException("primary validation failure");
            }
        };
        var coordinator = new EvaluationCoordinator(captureOptions);
        var options = new StrictCheckOptions(false, "HEAD", [], reportPath, "cleanup-current-run");
        var previous = Environment.CurrentDirectory;
        Environment.CurrentDirectory = repository.Root;
        try
        {
            var result = await coordinator.RunAsync(options, CancellationToken.None);

            Assert.Equal(4, result.Report.ExitCode);
            Assert.Null(result.SnapshotId);
            using var report = JsonDocument.Parse(File.ReadAllBytes(reportPath));
            Assert.Equal("cleanup-current-run", report.RootElement.GetProperty("runId").GetString());
            Assert.Contains(report.RootElement.GetProperty("reasons").EnumerateArray(),
                reason => reason.GetProperty("code").GetString() == "SNAPSHOT_CLEANUP_FAILED");
            Assert.Contains(report.RootElement.GetProperty("incompleteConditions").EnumerateArray(),
                reason => reason.GetProperty("code").GetString() == "SNAPSHOT_DIVERGED");
            Assert.False(report.RootElement.GetProperty("scopePlan").GetProperty("isComplete").GetBoolean());
            Assert.Contains(report.RootElement.GetProperty("scopePlan").GetProperty("reasons").EnumerateArray(),
                reason => reason.GetProperty("code").GetString() == "SCOPE_UNAVAILABLE");
            var cleanup = report.RootElement.GetProperty("evidence").EnumerateArray()
                .Single(item => item.GetProperty("kind").GetString() == "SNAPSHOT_CLEANUP");
            Assert.Contains(report.RootElement.GetProperty("evidence").EnumerateArray(),
                item => item.GetProperty("kind").GetString() == "SNAPSHOT_DIVERGENCE");
            var summary = cleanup.GetProperty("summary").GetString();
            Assert.Contains("primary validation failure", summary, StringComparison.Ordinal);
            Assert.Contains("ownership marker", summary, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Environment.CurrentDirectory = previous;
            if (damagedRoot is not null && Directory.Exists(damagedRoot)) Directory.Delete(damagedRoot, true);
            var cleanupOwner = OwnedDirectory.Create(parent, "cleanup");
            await cleanupOwner.DisposeAsync();
        }
    }

    [Fact]
    public async Task CancelledStrictCaptureCleansOwnedDirectoryAndWritesCurrentIncompleteReport()
    {
        using var repository = new SnapshotTestRepository();
        repository.WriteText("src/A.cs", "class A { }\n");
        repository.Git("add", ".");
        repository.Git("commit", "-m", "fixture");
        var reportPath = Path.Combine(_directory, "cancelled-stale.json");
        ReportWriter.Write(reportPath,
            EvaluationReport.CreateSynthetic(EvaluationOutcome.Incomplete, "stale-run", "TEST_FIXTURE"));
        var parent = OwnedDirectory.PrivateParent("snapshots");
        var before = Directory.Exists(parent)
            ? Directory.EnumerateDirectories(parent).ToHashSet(StringComparer.Ordinal)
            : [];
        using var cancelled = new CancellationTokenSource();
        var captureOptions = SnapshotCaptureOptions.Default with
        {
            Hook = (stage, _) =>
            {
                if (stage == SnapshotCaptureStage.AfterFileCopied) cancelled.Cancel();
            }
        };
        var coordinator = new EvaluationCoordinator(captureOptions);
        var options = new StrictCheckOptions(false, "HEAD", [], reportPath, "cancelled-current-run");
        var previous = Environment.CurrentDirectory;
        Environment.CurrentDirectory = repository.Root;
        try
        {
            var result = await coordinator.RunAsync(options, cancelled.Token);

            Assert.Equal(4, result.Report.ExitCode);
            Assert.Null(result.SnapshotId);
            using var report = JsonDocument.Parse(File.ReadAllBytes(reportPath));
            Assert.Equal("cancelled-current-run", report.RootElement.GetProperty("runId").GetString());
            Assert.Contains(report.RootElement.GetProperty("reasons").EnumerateArray(),
                reason => reason.GetProperty("code").GetString() == "SNAPSHOT_CANCELLED");
            var after = Directory.Exists(parent)
                ? Directory.EnumerateDirectories(parent).ToHashSet(StringComparer.Ordinal)
                : [];
            Assert.Equal(before, after);
        }
        finally { Environment.CurrentDirectory = previous; }
    }

    [Fact]
    public async Task SigtermCancelsStrictGitCaptureCleansOwnedStateAndWritesCurrentReport()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var repository = new SnapshotTestRepository();
        repository.WriteText("src/A.cs", "class A { }\n");
        repository.Git("add", ".");
        repository.Git("commit", "-m", "fixture");
        var reportPath = Path.Combine(_directory, "sigterm.json");
        var wrapperDirectory = Path.Combine(_directory, "git-wrapper");
        Directory.CreateDirectory(wrapperDirectory);
        var wrapper = Path.Combine(wrapperDirectory, "git");
        var ready = Path.Combine(_directory, "git-ready");
        var count = Path.Combine(_directory, "git-count");
        var childTemp = Path.Combine(_directory, "child-tmp");
        Directory.CreateDirectory(childTemp);
        File.WriteAllText(wrapper, """
            #!/bin/sh
            if [ "$1" = "ls-files" ]; then
              current=0
              if [ -f "$SIGNAL_COUNT" ]; then current=$(cat "$SIGNAL_COUNT"); fi
              current=$((current + 1))
              printf '%s' "$current" > "$SIGNAL_COUNT"
              if [ "$current" -ge 5 ]; then
                : > "$SIGNAL_READY"
                sleep 60
              fi
            fi
            exec "$REAL_GIT" "$@"
            """);
        File.SetUnixFileMode(wrapper, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var info = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            WorkingDirectory = repository.Root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        info.ArgumentList.Add(typeof(Program).Assembly.Location);
        foreach (var argument in new[] { "check", "--base", "HEAD", "--report", reportPath })
            info.ArgumentList.Add(argument);
        info.Environment["PATH"] = wrapperDirectory + Path.PathSeparator +
                                   (Environment.GetEnvironmentVariable("PATH") ?? string.Empty);
        var realGit = File.Exists("/usr/bin/git") ? "/usr/bin/git" : "/bin/git";
        Assert.True(File.Exists(realGit), "A real Git executable is required for the SIGTERM fixture.");
        info.Environment["REAL_GIT"] = realGit;
        info.Environment["SIGNAL_READY"] = ready;
        info.Environment["SIGNAL_COUNT"] = count;
        info.Environment["TMPDIR"] = childTemp;
        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var error = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        try
        {
            using var readyDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (!File.Exists(ready)) await Task.Delay(25, readyDeadline.Token);
            Assert.Equal(0, Kill(process.Id, 15));
            using var exitDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await process.WaitForExitAsync(exitDeadline.Token);
            await Task.WhenAll(output, error);

            Assert.Equal(4, process.ExitCode);
            using var report = JsonDocument.Parse(File.ReadAllBytes(reportPath));
            Assert.Contains(report.RootElement.GetProperty("reasons").EnumerateArray(),
                reason => reason.GetProperty("code").GetString() == "SNAPSHOT_CANCELLED");
            Assert.Empty(Directory.EnumerateFileSystemEntries(childTemp));
        }
        finally
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
        }
    }

    [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static extern int Kill(int processId, int signal);

    [DllImport("libc", EntryPoint = "mkfifo", SetLastError = true)]
    private static extern int MkFifo(string path, int mode);

    private static ProcessStartInfo ChildProcess(string workingDirectory, IReadOnlyList<string> arguments)
    {
        var info = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        info.ArgumentList.Add(typeof(Program).Assembly.Location);
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        return info;
    }

    [Fact]
    public async Task MissingExplicitInputReplacesStaleReportWithCurrentSnapshotRefusal()
    {
        using var repository = new SnapshotTestRepository();
        repository.WriteText("src/A.cs", "class A { }\n");
        repository.Git("add", ".");
        repository.Git("commit", "-m", "fixture");
        var reportPath = Path.Combine(_directory, "missing-stale.json");
        ReportWriter.Write(reportPath,
            EvaluationReport.CreateSynthetic(EvaluationOutcome.Incomplete, "stale-run", "TEST_FIXTURE"));
        var options = new StrictCheckOptions(false, null, [Path.Combine(repository.Root, "src/Missing.cs")],
            reportPath, "missing-current-run");

        var result = await new EvaluationCoordinator().RunAsync(options, CancellationToken.None);

        Assert.Equal(4, result.Report.ExitCode);
        Assert.Null(result.SnapshotId);
        using var report = JsonDocument.Parse(File.ReadAllBytes(reportPath));
        Assert.Equal("missing-current-run", report.RootElement.GetProperty("runId").GetString());
        Assert.Contains(report.RootElement.GetProperty("reasons").EnumerateArray(),
            reason => reason.GetProperty("code").GetString() == "SNAPSHOT_REFUSED");
    }

    [Fact]
    public async Task InaccessibleExplicitInputWritesCurrentSnapshotRefusal()
    {
        if (OperatingSystem.IsWindows()) return;
        using var repository = new SnapshotTestRepository();
        repository.WriteText("src/A.cs", "class A { }\n");
        repository.Git("add", ".");
        repository.Git("commit", "-m", "fixture");
        var input = Path.Combine(repository.Root, "src/A.cs");
        var reportPath = Path.Combine(_directory, "inaccessible.json");
        File.SetUnixFileMode(input, UnixFileMode.None);
        try
        {
            var options = new StrictCheckOptions(false, null, [input], reportPath, "inaccessible-current-run");
            var result = await new EvaluationCoordinator().RunAsync(options, CancellationToken.None);

            Assert.Equal(4, result.Report.ExitCode);
            Assert.Null(result.SnapshotId);
            using var report = JsonDocument.Parse(File.ReadAllBytes(reportPath));
            Assert.Equal("inaccessible-current-run", report.RootElement.GetProperty("runId").GetString());
            Assert.Contains(report.RootElement.GetProperty("reasons").EnumerateArray(),
                reason => reason.GetProperty("code").GetString() == "SNAPSHOT_REFUSED");
        }
        finally
        {
            File.SetUnixFileMode(input, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    [Fact]
    public async Task PlanModeIsHumanOutputAndNeverReusableSuccess()
    {
        var reportPath = Path.Combine(_directory, "plan.json");
        var oldOut = Console.Out;
        var output = new StringWriter();
        Console.SetOut(output);
        try
        {
            var code = await Program.Main(["check", "--plan", "--input", "src/A.cs", "--report", reportPath]);
            Assert.Equal(4, code);
        }
        finally { Console.SetOut(oldOut); }

        Assert.Contains("plan", output.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("{\"schemaVersion\"", output.ToString(), StringComparison.Ordinal);
        Assert.True(File.Exists(reportPath));
    }

    [Fact]
    public async Task PlanModePublishesScopeWithoutChangingSourceOrIndexOrRunningBuilds()
    {
        using var repository = new SnapshotTestRepository();
        repository.WriteText("src/App/App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />\n");
        repository.WriteText("tests/App.Tests/App.Tests.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />\n");
        repository.WriteText("src/App/A.cs", "class A { int M() => 1; }\n");
        repository.WriteText("mutate4csharp.json", """
            { "version": 1, "projects": [
              { "project": "src/App/App.csproj", "tests": ["tests/App.Tests/App.Tests.csproj"] }
            ] }
            """);
        repository.Git("add", ".");
        repository.Git("commit", "-m", "base");
        var baseCommit = repository.Git("rev-parse", "HEAD").Trim();
        repository.WriteText("src/App/A.cs", "class A { int M() => 2; }\n");
        repository.Git("add", "src/App/A.cs");
        repository.WriteText("src/App/A.cs", "class A { int M() => 3; }\n");
        var sourceBefore = repository.Read("src/App/A.cs");
        var indexBefore = repository.Git("diff", "--cached", "--binary");
        var statusBefore = repository.Git("status", "--porcelain=v1", "-z");
        var reportPath = Path.Combine(_directory, "real-plan.json");
        var previous = Environment.CurrentDirectory;
        Environment.CurrentDirectory = repository.Root;
        try
        {
            var result = await new EvaluationCoordinator().RunAsync(
                new(true, baseCommit, [], reportPath, "plan-safety"), CancellationToken.None);

            Assert.Equal("plan", result.Report.Mode);
            var file = Assert.Single(result.Report.ScopePlan.Files);
            Assert.Equal("src/App/A.cs", file.Path);
            Assert.Contains(file.Declarations, declaration => declaration.DisplayName == "method M");
            Assert.Equal("src/App/App.csproj", Assert.Single(result.Report.ScopePlan.ProjectUnits).Project);
        }
        finally { Environment.CurrentDirectory = previous; }

        Assert.Equal(sourceBefore, repository.Read("src/App/A.cs"));
        Assert.Equal(indexBefore, repository.Git("diff", "--cached", "--binary"));
        Assert.Equal(statusBefore, repository.Git("status", "--porcelain=v1", "-z"));
        Assert.False(Directory.Exists(Path.Combine(repository.Root, "src/App/bin")));
        Assert.False(Directory.Exists(Path.Combine(repository.Root, "src/App/obj")));
    }

    [Fact]
    public async Task RejectsUnsafeReportDestinationsWithoutChangingThem()
    {
        var input = Path.Combine(_directory, "Subject.cs");
        File.WriteAllText(input, "class Subject { }");

        var samePath = await Program.Main(["check", "--input", input, "--report", input]);
        var nonJson = await Program.Main(["check", "--base", "HEAD", "--report",
            Path.Combine(_directory, "report.txt")]);
        var unrelatedJson = Path.Combine(_directory, "unrelated.json");
        File.WriteAllText(unrelatedJson, "{\"purpose\":\"user data\"}");
        var existing = await Program.Main(["check", "--base", "HEAD", "--report", unrelatedJson]);

        Assert.Equal(1, samePath);
        Assert.Equal(1, nonJson);
        Assert.Equal(1, existing);
        Assert.Equal("class Subject { }", File.ReadAllText(input));
        Assert.Equal("{\"purpose\":\"user data\"}", File.ReadAllText(unrelatedJson));
    }

    [Fact]
    public async Task InvalidReportPathIsAUsageErrorInsteadOfAnUnhandledCrash()
    {
        var code = await Program.Main(["check", "--base", "HEAD", "--report", "bad\0path.json"]);

        Assert.Equal(1, code);
    }

    [Fact]
    public async Task NonStringExistingReportEnvelopeIsAUsageError()
    {
        using var repository = new SnapshotTestRepository();
        repository.WriteText("src/A.cs", "class A { }\n");
        repository.Git("add", ".");
        repository.Git("commit", "-m", "fixture");
        var path = Path.Combine(_directory, "wrong-types.json");
        var previous = Environment.CurrentDirectory;
        Environment.CurrentDirectory = repository.Root;
        try
        {
            Assert.Equal(4, await Program.Main(["check", "--base", "HEAD", "--report", path]));
            File.WriteAllText(path, "{\"schemaVersion\":1,\"runId\":false,\"outcome\":[]}");

            var code = await Program.Main(["check", "--base", "HEAD", "--report", path]);

            Assert.Equal(1, code);
        }
        finally { Environment.CurrentDirectory = previous; }
    }

    private static SnapshotTestRepository StrictEnumerationRepository(string member)
    {
        var repository = new SnapshotTestRepository();
        repository.WriteText("src/App/App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework><Nullable>enable</Nullable></PropertyGroup>
            </Project>
            """);
        repository.WriteText("src/App/Flag.cs", $"public sealed class Flag {{ {member} }}\n");
        repository.WriteText("tests/App.Tests/App.Tests.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework><IsTestProject>true</IsTestProject></PropertyGroup>
            </Project>
            """);
        repository.WriteText("mutate4csharp.json", """
            {
              "version": 1,
              "projects": [{
                "id": "app",
                "project": "src/App/App.csproj",
                "targetFramework": "net10.0",
                "parseContext": "net10-csharp14",
                "languageVersion": "14.0",
                "nullable": "enable",
                "defineConstants": [],
                "sources": ["src/App/**/*.cs"],
                "testSuites": ["unit"]
              }],
              "testSuites": [{
                "id": "unit",
                "path": "tests/App.Tests/App.Tests.csproj",
                "runner": "vstest",
                "framework": "net10.0",
                "configuration": "Release",
                "expectedMembers": ["App.Tests.dll"]
              }]
            }
            """);
        repository.WriteText("NuGet.Config", """
            <configuration><packageSources><clear /></packageSources></configuration>
            """);
        return repository;
    }

    public void Dispose() { try { Directory.Delete(_directory, true); } catch { } }
}
