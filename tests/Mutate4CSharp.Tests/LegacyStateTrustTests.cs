using System.Text;
using System.Text.Json;

namespace Mutate4CSharp.Tests;

public sealed class LegacyStateTrustTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "mutate4csharp-sidecar-red",
        Guid.NewGuid().ToString("N"));

    public LegacyStateTrustTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public async Task StrictCheckPublishesDiscoveryButNeverProvenStateInMvp()
    {
        using var repository = CreateRepository();
        var report = Path.Combine(_directory, "strict.json");
        var previous = Environment.CurrentDirectory;
        Environment.CurrentDirectory = repository.Root;
        try
        {
            var code = await Program.Main(["check", "--base", "HEAD", "--report", report]);

            Assert.Equal(4, code);
        }
        finally { Environment.CurrentDirectory = previous; }

        var discoveries = Directory.GetFiles(Path.Combine(repository.Root, ".mutate4csharp", "discovery"),
            "*.json", SearchOption.TopDirectoryOnly);
        var discovery = Assert.Single(discoveries);
        using var document = JsonDocument.Parse(File.ReadAllBytes(discovery));
        Assert.Equal("1", document.RootElement.GetProperty("schemaVersion").GetString());
        Assert.Equal("DISCOVERY", document.RootElement.GetProperty("recordKind").GetString());
        Assert.Equal("INCOMPLETE", document.RootElement.GetProperty("evaluationOutcome").GetString());
        Assert.False(Directory.Exists(Path.Combine(repository.Root, ".mutate4csharp", "proven")));
    }

    [Fact]
    public async Task NoStateStillEvaluatesAndPublishesOnlyTheRequestedReport()
    {
        using var repository = CreateRepository();
        var report = Path.Combine(_directory, "no-state.json");
        var previous = Environment.CurrentDirectory;
        Environment.CurrentDirectory = repository.Root;
        int code;
        try
        {
            code = await Program.Main(["check", "--base", "HEAD", "--no-state", "--report", report]);
        }
        finally { Environment.CurrentDirectory = previous; }

        Assert.Equal(4, code);
        Assert.True(File.Exists(report));
        Assert.False(Directory.Exists(Path.Combine(repository.Root, ".mutate4csharp", "discovery")));
        Assert.False(Directory.Exists(Path.Combine(repository.Root, ".mutate4csharp", "proven")));
    }

    [Fact]
    public async Task LegacyManifestCannotSuppressStrictWorkOrChangeSourceIndexOrStatus()
    {
        using var repository = CreateRepository();
        var sourcePath = Path.Combine(repository.Root, "src", "A.cs");
        File.AppendAllText(sourcePath, "// <mutate4csharp-manifest>{\"version\":1}</mutate4csharp-manifest>\n",
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        repository.Git("add", "src/A.cs");
        var bytesBefore = File.ReadAllBytes(sourcePath);
        var indexBefore = repository.Git("diff", "--cached", "--binary");
        var statusBefore = repository.Git("status", "--porcelain=v1", "-z");
        var report = Path.Combine(_directory, "legacy.json");
        var previous = Environment.CurrentDirectory;
        Environment.CurrentDirectory = repository.Root;
        try
        {
            var result = await new EvaluationCoordinator().RunAsync(
                new(false, "HEAD", [], report, "legacy-state-red"), CancellationToken.None);

            Assert.Equal(4, result.Report.ExitCode);
            Assert.Contains(result.Report.Reasons, reason => reason.Code == "ENUMERATION_NOT_IMPLEMENTED");
        }
        finally { Environment.CurrentDirectory = previous; }

        Assert.Equal(bytesBefore, File.ReadAllBytes(sourcePath));
        Assert.Equal(indexBefore, repository.Git("diff", "--cached", "--binary"));
        Assert.Equal(statusBefore, repository.Git("status", "--porcelain=v1", "-z"));
        Assert.False(Directory.Exists(Path.Combine(repository.Root, ".mutate4csharp", "proven")));
    }

    [Fact]
    public async Task MalformedProvenStateIsNeverReadAsAnExecutionCache()
    {
        using var repository = CreateRepository();
        var proven = Path.Combine(repository.Root, ".mutate4csharp", "proven");
        Directory.CreateDirectory(proven);
        var poison = Path.Combine(proven, "poison.json");
        File.WriteAllText(poison, "{\"schemaVersion\":\"1\",\"recordKind\":\"PROVEN\",\"outcome\":\"PASS\"}");
        var report = Path.Combine(_directory, "cache-read.json");
        var previous = Environment.CurrentDirectory;
        Environment.CurrentDirectory = repository.Root;
        try
        {
            var result = await new EvaluationCoordinator().RunAsync(
                new(false, "HEAD", [], report, "no-cache-read"), CancellationToken.None);

            Assert.Equal(EvaluationOutcome.Incomplete, result.Report.Outcome);
            Assert.Contains(result.Report.Reasons, reason => reason.Code == "ENUMERATION_NOT_IMPLEMENTED");
        }
        finally { Environment.CurrentDirectory = previous; }

        Assert.Equal("{\"schemaVersion\":\"1\",\"recordKind\":\"PROVEN\",\"outcome\":\"PASS\"}",
            File.ReadAllText(poison));
    }

    [Fact]
    public async Task StatePublicationFailureIsRecordedAndCannotLeavePassEvidence()
    {
        using var repository = CreateRepository();
        File.WriteAllText(Path.Combine(repository.Root, ".mutate4csharp"), "user-owned collision");
        var report = Path.Combine(_directory, "state-failure.json");
        var previous = Environment.CurrentDirectory;
        Environment.CurrentDirectory = repository.Root;
        try
        {
            var result = await new EvaluationCoordinator().RunAsync(
                new(false, "HEAD", [], report, "state-failure"), CancellationToken.None);

            Assert.Equal(EvaluationOutcome.Incomplete, result.Report.Outcome);
            Assert.Contains(result.Report.IncompleteConditions, reason => reason.Code == "SIDECAR_WRITE_FAILED");
            Assert.Contains(result.Report.Evidence, evidence => evidence.Kind == "SIDECAR_PUBLICATION_FAILURE");
            using var published = JsonDocument.Parse(File.ReadAllBytes(report));
            Assert.Equal("INCOMPLETE", published.RootElement.GetProperty("outcome").GetString());
            Assert.Contains(published.RootElement.GetProperty("incompleteConditions").EnumerateArray(),
                reason => reason.GetProperty("code").GetString() == "SIDECAR_WRITE_FAILED");
        }
        finally { Environment.CurrentDirectory = previous; }

        Assert.Equal("user-owned collision", File.ReadAllText(Path.Combine(repository.Root, ".mutate4csharp")));
    }

    [Fact]
    public async Task ReportPublicationFailureCannotLeaveDiscoveryPointingAtAnAbsentReport()
    {
        using var repository = CreateRepository();
        var report = Path.Combine(_directory, "report-collision.json");
        Directory.CreateDirectory(report);
        var previous = Environment.CurrentDirectory;
        Environment.CurrentDirectory = repository.Root;
        try
        {
            await Assert.ThrowsAsync<ArgumentException>(() => new EvaluationCoordinator().RunAsync(
                new(false, "HEAD", [], report, "report-first"), CancellationToken.None));
        }
        finally { Environment.CurrentDirectory = previous; }

        Assert.False(Directory.Exists(Path.Combine(repository.Root, ".mutate4csharp", "discovery")));
    }

    [Theory]
    [InlineData(true, false, "PLAN_ONLY")]
    [InlineData(false, true, "ENUMERATION_NOT_IMPLEMENTED")]
    public async Task FingerprintIdentityResolutionCannotAffectPlanOrNoState(
        bool plan, bool noState, string expectedReason)
    {
        using var repository = CreateRepository();
        var report = Path.Combine(_directory, $"lazy-fingerprint-{plan}-{noState}.json");
        var previousDirectory = Environment.CurrentDirectory;
        var previousHost = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        Environment.CurrentDirectory = repository.Root;
        Environment.SetEnvironmentVariable("DOTNET_HOST_PATH", Path.Combine(_directory, "missing-dotnet"));
        try
        {
            var result = await new EvaluationCoordinator().RunAsync(
                new(plan, "HEAD", [], report, "lazy-fingerprint", noState), CancellationToken.None);

            Assert.Contains(result.Report.Reasons, reason => reason.Code == expectedReason);
            Assert.DoesNotContain(result.Report.IncompleteConditions,
                reason => reason.Code == "SIDECAR_WRITE_FAILED");
        }
        finally
        {
            Environment.SetEnvironmentVariable("DOTNET_HOST_PATH", previousHost);
            Environment.CurrentDirectory = previousDirectory;
        }
    }

    [Fact]
    public async Task FingerprintIdentityFailureUsesStatePublicationFailurePath()
    {
        using var repository = CreateRepository();
        var report = Path.Combine(_directory, "fingerprint-failure.json");
        var previousDirectory = Environment.CurrentDirectory;
        var previousHost = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        Environment.CurrentDirectory = repository.Root;
        Environment.SetEnvironmentVariable("DOTNET_HOST_PATH", Path.Combine(_directory, "missing-dotnet"));
        try
        {
            var result = await new EvaluationCoordinator().RunAsync(
                new(false, "HEAD", [], report, "fingerprint-failure"), CancellationToken.None);

            Assert.Contains(result.Report.IncompleteConditions,
                reason => reason.Code == "SIDECAR_WRITE_FAILED");
            Assert.Contains(result.Report.Reasons,
                reason => reason.Code == "ENUMERATION_NOT_IMPLEMENTED");
        }
        finally
        {
            Environment.SetEnvironmentVariable("DOTNET_HOST_PATH", previousHost);
            Environment.CurrentDirectory = previousDirectory;
        }
    }

    [Fact]
    public async Task KnownFingerprintFailureIsAppliedBeforeTheOnlyReportPublication()
    {
        using var repository = CreateRepository();
        var report = Path.Combine(_directory, "single-fingerprint-failure.json");
        var reportName = Path.GetFileName(report);
        var temporaryWrites = 0;
        using var watcher = new FileSystemWatcher(_directory)
        {
            Filter = $".{reportName}.*.tmp",
            NotifyFilter = NotifyFilters.FileName,
            EnableRaisingEvents = true
        };
        watcher.Created += (_, _) => Interlocked.Increment(ref temporaryWrites);
        var previousDirectory = Environment.CurrentDirectory;
        var previousHost = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        Environment.CurrentDirectory = repository.Root;
        Environment.SetEnvironmentVariable("DOTNET_HOST_PATH", Path.Combine(_directory, "missing-dotnet-once"));
        try
        {
            var result = await new EvaluationCoordinator().RunAsync(
                new(false, "HEAD", [], report, "single-fingerprint-failure"), CancellationToken.None);

            Assert.Contains(result.Report.IncompleteConditions,
                reason => reason.Code == "SIDECAR_WRITE_FAILED");
            using var published = JsonDocument.Parse(File.ReadAllBytes(report));
            Assert.Contains(published.RootElement.GetProperty("incompleteConditions").EnumerateArray(),
                reason => reason.GetProperty("code").GetString() == "SIDECAR_WRITE_FAILED");
            SpinWait.SpinUntil(() => Volatile.Read(ref temporaryWrites) >= 2, TimeSpan.FromSeconds(1));
            Assert.Equal(1, Volatile.Read(ref temporaryWrites));
        }
        finally
        {
            Environment.SetEnvironmentVariable("DOTNET_HOST_PATH", previousHost);
            Environment.CurrentDirectory = previousDirectory;
        }
    }

    [Theory]
    [InlineData(true, false, "HEAD")]
    [InlineData(false, true, "HEAD")]
    [InlineData(false, false, "missing-base-revision")]
    public async Task EveryDefaultReportPathEnsuresTheOwnedIgnoreContract(
        bool plan, bool noState, string baseRevision)
    {
        using var repository = CreateRepository(includeRootIgnore: false);
        var previous = Environment.CurrentDirectory;
        Environment.CurrentDirectory = repository.Root;
        try
        {
            var result = await new EvaluationCoordinator().RunAsync(
                new(plan, baseRevision, [], null, $"default-report-{plan}-{noState}", noState),
                CancellationToken.None);

            Assert.True(File.Exists(result.ReportPath));
        }
        finally { Environment.CurrentDirectory = previous; }

        Assert.Equal("*\n", File.ReadAllText(Path.Combine(repository.Root, ".mutate4csharp", ".gitignore")));
        Assert.Equal(string.Empty, repository.Git("status", "--porcelain=v1", "-z"));
    }

    [Fact]
    public async Task SdkIdentityIsResolvedInsideTheImmutableCapture()
    {
        if (OperatingSystem.IsWindows()) return;
        using var repository = CreateRepository();
        repository.WriteText("global.json", "{ \"sdk\": { \"version\": \"10.0.103\" } }\n");
        repository.Git("add", "global.json");
        repository.Git("commit", "-m", "sdk fixture");
        var host = Path.Combine(_directory, "captured-sdk-host.sh");
        var originalRoot = repository.Root.Replace("'", "'\\''", StringComparison.Ordinal);
        File.WriteAllText(host, $"#!/bin/sh\nif [ \"$PWD\" = '{originalRoot}' ]; then exit 23; fi\nprintf '10.0.103\\n'\n");
        File.SetUnixFileMode(host, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var report = Path.Combine(_directory, "captured-sdk.json");
        var previousDirectory = Environment.CurrentDirectory;
        var previousHost = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        Environment.CurrentDirectory = repository.Root;
        Environment.SetEnvironmentVariable("DOTNET_HOST_PATH", host);
        try
        {
            var result = await new EvaluationCoordinator().RunAsync(
                new(false, "HEAD", [], report, "captured-sdk"), CancellationToken.None);

            Assert.DoesNotContain(result.Report.IncompleteConditions,
                reason => reason.Code == "SIDECAR_WRITE_FAILED");
            Assert.Single(Directory.GetFiles(Path.Combine(repository.Root, ".mutate4csharp", "discovery"),
                "*.json", SearchOption.TopDirectoryOnly));
        }
        finally
        {
            Environment.SetEnvironmentVariable("DOTNET_HOST_PATH", previousHost);
            Environment.CurrentDirectory = previousDirectory;
        }
    }

    [Fact]
    public async Task SdkIdentityRefusesInheritedGlobalJsonAboveThePrivateCaptureRoot()
    {
        using var repository = CreateRepository();
        var privateCaptureParent = OwnedDirectory.PrivateParent("snapshots");
        Directory.CreateDirectory(privateCaptureParent);
        var inheritedGlobalJson = Path.Combine(privateCaptureParent, "global.json");
        File.WriteAllText(inheritedGlobalJson, "{ \"sdk\": { \"version\": \"0.0.0\" } }\n");
        var report = Path.Combine(_directory, "inherited-capture-sdk.json");
        var previousDirectory = Environment.CurrentDirectory;
        var previousHost = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        Environment.CurrentDirectory = repository.Root;
        Environment.SetEnvironmentVariable("DOTNET_HOST_PATH", Path.Combine(_directory, "must-not-run-dotnet"));
        try
        {
            var result = await new EvaluationCoordinator().RunAsync(
                new(false, "HEAD", [], report, "inherited-capture-sdk"), CancellationToken.None);

            Assert.Contains(result.Report.IncompleteConditions,
                reason => reason.Code == "SIDECAR_WRITE_FAILED");
            Assert.Contains(result.Report.Evidence, evidence => evidence.Kind == "SIDECAR_PUBLICATION_FAILURE" &&
                evidence.Summary.Contains("global.json", StringComparison.Ordinal));
            Assert.False(Directory.Exists(Path.Combine(repository.Root, ".mutate4csharp", "discovery")));
        }
        finally
        {
            Environment.SetEnvironmentVariable("DOTNET_HOST_PATH", previousHost);
            Environment.CurrentDirectory = previousDirectory;
            File.Delete(inheritedGlobalJson);
        }
    }

    [Fact]
    public async Task DefaultReportRefusesASpecialOwnedReportDirectory()
    {
        if (OperatingSystem.IsWindows()) return;
        using var repository = CreateRepository(includeRootIgnore: false);
        var stateRoot = Path.Combine(repository.Root, ".mutate4csharp");
        var outside = Path.Combine(_directory, "outside-reports");
        Directory.CreateDirectory(stateRoot);
        Directory.CreateDirectory(outside);
        Directory.CreateSymbolicLink(Path.Combine(stateRoot, "reports"), outside);
        var previous = Environment.CurrentDirectory;
        Environment.CurrentDirectory = repository.Root;
        try
        {
            await Assert.ThrowsAsync<IOException>(() => new EvaluationCoordinator().RunAsync(
                new(true, "HEAD", [], null, "special-default-report"), CancellationToken.None));
        }
        finally { Environment.CurrentDirectory = previous; }

        Assert.Empty(Directory.EnumerateFileSystemEntries(outside));
    }

    [Fact]
    public async Task CancellationAfterReportPublicationRewritesThenRethrows()
    {
        if (OperatingSystem.IsWindows()) return;
        using var repository = CreateRepository();
        var host = Path.Combine(_directory, "cancelled-sdk-host.sh");
        File.WriteAllText(host, "#!/bin/sh\nsleep 30\n");
        File.SetUnixFileMode(host, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var report = Path.Combine(_directory, "cancelled-publication.json");
        var previousDirectory = Environment.CurrentDirectory;
        var previousHost = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        Environment.CurrentDirectory = repository.Root;
        Environment.SetEnvironmentVariable("DOTNET_HOST_PATH", host);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new EvaluationCoordinator().RunAsync(
                new(false, "HEAD", [], report, "cancelled-publication"), cancellation.Token));

            using var published = JsonDocument.Parse(File.ReadAllBytes(report));
            Assert.Contains(published.RootElement.GetProperty("incompleteConditions").EnumerateArray(),
                reason => reason.GetProperty("code").GetString() == "SIDECAR_WRITE_FAILED");
        }
        finally
        {
            Environment.SetEnvironmentVariable("DOTNET_HOST_PATH", previousHost);
            Environment.CurrentDirectory = previousDirectory;
        }
    }

    private static SnapshotTestRepository CreateRepository(bool includeRootIgnore = true)
    {
        var repository = new SnapshotTestRepository();
        repository.WriteText("src/A.cs", "class A { int Value() => 1; }\n");
        if (includeRootIgnore) repository.WriteText(".gitignore", ".mutate4csharp/\n");
        repository.Git("add", ".");
        repository.Git("commit", "-m", "fixture");
        return repository;
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch { }
    }
}
