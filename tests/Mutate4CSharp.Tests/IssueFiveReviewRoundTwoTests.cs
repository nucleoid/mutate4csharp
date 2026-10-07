using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Json.Schema;

namespace Mutate4CSharp.Tests;

public sealed class IssueFiveReviewRoundTwoTests : IDisposable
{
    private static readonly JsonSchema ConfigurationSchema = ConfigurationSchemaLoader.Load(File.ReadAllText(
        Path.Combine(Path.GetFullPath("../../../../../", AppContext.BaseDirectory),
            "docs/contracts/check-config-v1.schema.json")));
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mutate4csharp-issue5-r2",
        Guid.NewGuid().ToString("N"));

    public IssueFiveReviewRoundTwoTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void CoverageResultsExposeAnOwnedDisposableLifetime()
    {
        Assert.True(typeof(IAsyncDisposable).IsAssignableFrom(typeof(SuiteRunResult)));
    }

    [Fact]
    public void NullSuiteEvidenceFailsClosed()
    {
        IReadOnlyList<SuiteMutationEvidence> evidence = [null!];

        var result = SuiteCoordinator.AggregateMutant("mutation:v1:" + new string('a', 64),
            ["unit"], evidence);

        Assert.Equal(UnitDisposition.Error, result.Disposition);
        Assert.Contains(result.Evidence, item => item.Kind == "SUITE_EVIDENCE_INVALID");
    }

    [Fact]
    public async Task NullExecutorResultRetainsEarlierSuiteEvidenceAndFailsClosed()
    {
        var calls = 0;
        var executor = new DelegateSuiteExecutor((suite, _, _) =>
        {
            calls++;
            return Task.FromResult<SuiteRunResult>(suite.Path == "A.csproj"
                ? Green()
                : null!);
        });

        var result = await new SuiteCoordinator(executor, TimeProvider.System).RunBaselinesAsync(
            "snapshot", [Suite("one", "A.csproj"), Suite("two", "B.csproj")],
            TimeSpan.FromSeconds(5), DateTimeOffset.UtcNow.AddMinutes(1), CancellationToken.None);

        Assert.Equal(2, calls);
        Assert.Equal(2, result.Executions.Count);
        Assert.Equal(SuiteRunDisposition.Passed, result.Executions[0].Result.Disposition);
        Assert.Equal(SuiteRunDisposition.Error, result.Executions[1].Result.Disposition);
        Assert.Equal(BaselineStatus.Unknown, result.Status);
        Assert.Contains(result.IncompleteConditions, item => item.Code == "BASELINE_INCONCLUSIVE");
    }

    [Fact]
    public async Task FinalMutationCancellationIsNeverLost()
    {
        using var cancellation = new CancellationTokenSource();
        var mutation = new ScheduledMutation("mutation:v1:" + new string('b', 64),
            "evaluation:v1:" + new string('c', 64), ["unit"]);
        var executor = new DelegateMutationExecutor((item, _, _) =>
        {
            cancellation.Cancel();
            return Task.FromResult(new ScheduledMutationResult(item.EvaluationUnitId,
                UnitDisposition.Killed, [new("KILLED", "The final assigned mutant was killed.")]));
        });

        var result = await new EvaluationScheduler(executor, TimeProvider.System).RunAsync([mutation], 1,
            TimeSpan.FromSeconds(5), DateTimeOffset.UtcNow.AddMinutes(1), cancellation.Token);

        Assert.Contains(result.IncompleteConditions, item => item.Code == "EXECUTION_CANCELLED");
    }

    [Fact]
    public async Task FinalBaselineCancellationIsNeverLost()
    {
        using var cancellation = new CancellationTokenSource();
        var executor = new DelegateSuiteExecutor((_, _, _) =>
        {
            cancellation.Cancel();
            return Task.FromResult(Green());
        });

        var result = await new SuiteCoordinator(executor, TimeProvider.System).RunBaselinesAsync(
            "snapshot", [Suite("unit", "App.Tests.csproj")], TimeSpan.FromSeconds(5),
            DateTimeOffset.UtcNow.AddMinutes(1), cancellation.Token);

        Assert.Equal(BaselineStatus.Unknown, result.Status);
        Assert.Contains(result.IncompleteConditions, item => item.Code == "EXECUTION_CANCELLED");
    }

    [Theory]
    [MemberData(nameof(ConfigurationParityCases))]
    public void RuntimeAndSchemaHaveTheSameStringAndVersionSemantics(string name, string json,
        bool expectedValid)
    {
        using var document = JsonDocument.Parse(json);
        var schemaValid = ConfigurationSchema.Evaluate(document.RootElement).IsValid;
        var runtimeValid = true;
        try { _ = CheckConfiguration.Load(_root, Encoding.UTF8.GetBytes(json)); }
        catch (ArgumentException) { runtimeValid = false; }

        Assert.Equal(expectedValid, schemaValid);
        Assert.Equal(expectedValid, runtimeValid);
        Assert.Equal(schemaValid, runtimeValid);
        _ = name;
    }

    public static IEnumerable<object[]> ConfigurationParityCases()
    {
        yield return ["leading project whitespace",
            ValidConfiguration.Replace("src/App/App.csproj", " src/App/App.csproj", StringComparison.Ordinal), false];
        yield return ["trailing source whitespace",
            ValidConfiguration.Replace("src/App/**/*.cs", "src/App/**/*.cs ", StringComparison.Ordinal), false];
        yield return ["expected member maximum",
            ValidConfiguration.Replace("App.Tests.dll", new string('a', 257), StringComparison.Ordinal), false];
        yield return ["dot member", ValidConfiguration.Replace("App.Tests.dll", ".", StringComparison.Ordinal), false];
        yield return ["dot-dot member", ValidConfiguration.Replace("App.Tests.dll", "..", StringComparison.Ordinal), false];
        yield return ["parse-context control", ValidConfiguration.Replace("\"default\"", "\"bad\\u0001\"",
            StringComparison.Ordinal), false];
        yield return ["reason control", ValidConfiguration.Replace("\"generated\"", "\"bad\\u0001\"",
            StringComparison.Ordinal), false];
        yield return ["numeric version equivalence", ValidConfiguration.Replace("\"version\": 1",
            "\"version\": 1.0", StringComparison.Ordinal), true];
    }

    [Fact]
    public async Task StrictExclusionIsAppliedToPlanAndPublishesItsRequiredReason()
    {
        using var repository = new SnapshotTestRepository();
        repository.WriteText("src/App/App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />\n");
        repository.WriteText("tests/App.Tests/App.Tests.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />\n");
        repository.WriteText("src/App/Excluded.cs", "class Excluded { int Value() => 1; }\n");
        repository.WriteText("mutate4csharp.json", ValidConfiguration.Replace("generated/**/*.cs",
            "src/App/Excluded.cs", StringComparison.Ordinal).Replace("generated", "third-party generated",
                StringComparison.Ordinal));
        repository.Git("add", ".");
        repository.Git("commit", "-m", "base");
        repository.WriteText("src/App/Excluded.cs", "class Excluded { int Value() => 2; }\n");
        await using var snapshot = await SnapshotCapture.CaptureAsync(repository.Root, "HEAD", [],
            SnapshotCaptureOptions.Default, CancellationToken.None);

        var plan = await ScopePlanner.PlanGitAsync(snapshot, CancellationToken.None);

        var exclusion = Assert.Single(plan.Exclusions);
        Assert.Equal("src/App/Excluded.cs", exclusion.Path);
        Assert.Equal("CONFIGURED_EXCLUSION", exclusion.ReasonCode);
        Assert.Contains("third-party generated", exclusion.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(plan.Files, item => item.Path == exclusion.Path);
        var canonical = ReportWriter.SerializeCanonicalScope(plan);
        Assert.Contains("third-party generated", canonical, StringComparison.Ordinal);
    }

    [Fact(Timeout = 420_000)]
    public async Task RealVstestCoverletFixtureHonorsConfigurationOwnsCoverageAndCleansProcesses()
    {
        using var repository = new SnapshotTestRepository();
        var marker = Path.Combine(_root, "child.pid");
        WriteRealFixture(repository, marker);
        await using var snapshot = await SnapshotCapture.CaptureAsync(repository.Root, "HEAD", [],
            SnapshotCaptureOptions.Default, CancellationToken.None);
        await using var environment = await ExecutionEnvironment.PrepareDependenciesAsync(snapshot,
            ["tests/Fixture.Tests/Fixture.Tests.csproj"],
            DependencyPreparationOptions.Default with { Timeout = TimeSpan.FromMinutes(2) },
            CancellationToken.None);
        var executor = new VstestSuiteExecutor(snapshot, environment);

        var passing = await executor.RunBaselineAsync(Suite("fixture",
            "tests/Fixture.Tests/Fixture.Tests.csproj", "Release"),
            TimeSpan.FromMinutes(1), CancellationToken.None);
        Assert.True(passing.Disposition == SuiteRunDisposition.Passed,
            $"Expected passing fixture, got {passing.Disposition}: {string.Join(" | ", passing.Diagnostics)}");
        Assert.Equal(["Fixture.Tests.dll"], passing.AccountedMembers, StringComparer.OrdinalIgnoreCase);
        Assert.True(passing.CoverageReports.Count > 0,
            $"No preserved coverage ({passing.CoverageReports.Count}): {string.Join(" | ", passing.Diagnostics)}");
        var coveragePaths = passing.CoverageReports.ToArray();
        var coverageRoot = Path.GetDirectoryName(coveragePaths[0])!;
        Assert.All(coveragePaths, path =>
            Assert.Equal("CoverageSession", XDocument.Load(path).Root?.Name.LocalName));
        await ((IAsyncDisposable)(object)passing).DisposeAsync();
        Assert.All(coveragePaths, path => Assert.False(File.Exists(path)));
        Assert.False(Directory.Exists(coverageRoot));

        var timeoutBudget = TimeSpan.FromSeconds(60);
        var timeoutWatch = Stopwatch.StartNew();
        var timeoutTask = executor.RunBaselineAsync(Suite("fixture-timeout",
            "tests/Fixture.Tests/Fixture.Tests.csproj", "Slow"),
            timeoutBudget, CancellationToken.None);
        await WaitForFileAsync(marker, RemainingMarkerBudget(timeoutBudget, timeoutWatch));
        var timeoutPid = ReadPid(marker);
        var timedOut = await timeoutTask;
        Assert.True(timedOut.Disposition == SuiteRunDisposition.TimedOut,
            $"Expected timeout, got {timedOut.Disposition}: {string.Join(" | ", timedOut.Diagnostics)}");
        AssertProcessGone(timeoutPid);
        await ((IAsyncDisposable)(object)timedOut).DisposeAsync();

        File.Delete(marker);
        using var cancellation = new CancellationTokenSource();
        var cancellationBudget = TimeSpan.FromSeconds(90);
        var cancellationWatch = Stopwatch.StartNew();
        var cancelledTask = executor.RunBaselineAsync(Suite("fixture-cancel",
            "tests/Fixture.Tests/Fixture.Tests.csproj", "Slow"),
            cancellationBudget, cancellation.Token);
        await WaitForFileAsync(marker, RemainingMarkerBudget(cancellationBudget, cancellationWatch));
        var cancelPid = ReadPid(marker);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelledTask);
        AssertProcessGone(cancelPid);
    }

    private static void WriteRealFixture(SnapshotTestRepository repository, string marker)
    {
        repository.WriteText("src/Fixture/Fixture.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
              </PropertyGroup>
              <ItemGroup><Compile Include="Subject.cs" /></ItemGroup>
            </Project>
            """);
        repository.WriteText("src/Fixture/Subject.cs", "public static class Subject { public static bool Value() => true; }\n");
        repository.WriteText("tests/Fixture.Tests/Fixture.Tests.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <IsTestProject>true</IsTestProject>
                <AssemblyName>Fixture.Tests</AssemblyName>
                <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
              </PropertyGroup>
              <PropertyGroup Condition="'$(Configuration)' == 'Slow'">
                <DefineConstants>$(DefineConstants);SLOW_BASELINE</DefineConstants>
              </PropertyGroup>
              <ItemGroup>
                <Compile Include="FixtureTests.cs" />
                <ProjectReference Include="../../src/Fixture/Fixture.csproj" />
                <PackageReference Include="Microsoft.NET.Test.Sdk" Version="18.3.0" />
                <PackageReference Include="xunit.v3" Version="3.2.2" />
                <PackageReference Include="xunit.runner.visualstudio" Version="3.1.5" />
                <PackageReference Include="coverlet.collector" Version="6.0.4" />
              </ItemGroup>
            </Project>
            """);
        var markerLiteral = JsonSerializer.Serialize(marker);
        repository.WriteText("tests/Fixture.Tests/FixtureTests.cs", $$"""
            using System.Diagnostics;
            using System.IO;
            using System.Threading;
            using Xunit;

            public sealed class FixtureTests
            {
                [Fact]
                public void Baseline()
                {
            #if SLOW_BASELINE
                    var process = System.OperatingSystem.IsWindows()
                        ? Process.Start(new ProcessStartInfo("cmd.exe", "/c ping -n 300 127.0.0.1 > nul") { UseShellExecute = false })!
                        : Process.Start(new ProcessStartInfo("/bin/sh", "-c \"sleep 300\"") { UseShellExecute = false })!;
                    var markerTemporary = {{markerLiteral}} + "." + process.Id.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".tmp";
                    File.WriteAllText(markerTemporary, process.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    File.Move(markerTemporary, {{markerLiteral}}, true);
                    Thread.Sleep(System.TimeSpan.FromMinutes(5));
            #else
                    Assert.True(Subject.Value());
            #endif
                }
            }
            """);
        repository.WriteText("NuGet.Config",
            "<configuration><packageSources><clear /><add key=\"fixture\" value=\"local-packages\" /></packageSources></configuration>\n");
        CopyRestoredPackages(Path.Combine(repository.Root, "local-packages"));
        repository.Git("add", ".");
        repository.Git("commit", "-m", "real vstest fixture");
    }

    private static void CopyRestoredPackages(string destination)
    {
        var assetsPath = Path.GetFullPath("../../../obj/project.assets.json", AppContext.BaseDirectory);
        using var assets = JsonDocument.Parse(File.ReadAllBytes(assetsPath));
        var roots = assets.RootElement.GetProperty("packageFolders").EnumerateObject()
            .Select(item => Path.GetFullPath(item.Name)).ToArray();
        Directory.CreateDirectory(destination);
        foreach (var library in assets.RootElement.GetProperty("libraries").EnumerateObject())
        {
            if (library.Value.GetProperty("type").GetString() != "package") continue;
            var separator = library.Name.LastIndexOf('/');
            var id = library.Name[..separator].ToLowerInvariant();
            var version = library.Name[(separator + 1)..].ToLowerInvariant();
            var relative = library.Value.TryGetProperty("path", out var path)
                ? path.GetString()?.Replace('/', Path.DirectorySeparatorChar)
                : Path.Combine(id, version);
            if (string.IsNullOrWhiteSpace(relative)) throw new InvalidOperationException(library.Name);
            var archiveName = $"{id}.{version}.nupkg";
            var archive = roots.Select(root => Path.Combine(root, relative, archiveName)).Single(File.Exists);
            File.Copy(archive, Path.Combine(destination, Path.GetFileName(archive)));
        }
    }

    private static async Task WaitForFileAsync(string path, TimeSpan timeout)
    {
        var watch = Stopwatch.StartNew();
        while (!TryReadPublishedPid(path, out _))
        {
            if (watch.Elapsed >= timeout) throw new TimeoutException("Fixture child process did not start.");
            await Task.Delay(50);
        }
    }

    private static TimeSpan RemainingMarkerBudget(TimeSpan phaseBudget, Stopwatch phaseWatch)
    {
        var remaining = phaseBudget - phaseWatch.Elapsed;
        if (remaining <= TimeSpan.Zero)
            throw new TimeoutException("The timed phase exhausted its budget before the fixture marker wait began.");
        return remaining;
    }

    private static int ReadPid(string path) => TryReadPublishedPid(path, out var pid)
        ? pid
        : throw new FormatException("Fixture child PID marker was not published atomically with valid content.");

    private static bool TryReadPublishedPid(string path, out int pid)
    {
        pid = 0;
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length == 0) return false;
            return int.TryParse(File.ReadAllText(path), System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out pid) && pid > 0;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private static void AssertProcessGone(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            var exited = process.HasExited;
            if (!exited)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                try { process.WaitForExit(5_000); } catch { }
            }
            Assert.True(exited, $"Fixture descendant process {pid} is still running.");
        }
        catch (ArgumentException) { }
    }

    private static SuiteExecution Suite(string id, string path, string configuration = "Release") =>
        new(id, [id], path, "vstest", "net10.0", configuration, ["Fixture.Tests.dll"]);

    private static SuiteRunResult Green() => new(SuiteRunDisposition.Passed, TimeSpan.Zero,
        ["Fixture.Tests.dll"], [], [], ["coverage.xml"]);

    private sealed class DelegateSuiteExecutor(
        Func<SuiteExecution, TimeSpan, CancellationToken, Task<SuiteRunResult>> execute) : ISuiteExecutor
    {
        public Task<SuiteRunResult> RunBaselineAsync(SuiteExecution suite, TimeSpan timeout,
            CancellationToken cancellationToken) => execute(suite, timeout, cancellationToken);
    }

    private sealed class DelegateMutationExecutor(
        Func<ScheduledMutation, TimeSpan, CancellationToken, Task<ScheduledMutationResult>> execute) :
        IIsolatedMutationExecutor
    {
        public Task<ScheduledMutationResult> ExecuteAsync(ScheduledMutation mutation, TimeSpan timeout,
            CancellationToken cancellationToken) => execute(mutation, timeout, cancellationToken);
    }

    private const string ValidConfiguration = """
        {
          "version": 1,
          "projects": [{
            "id": "app", "project": "src/App/App.csproj", "targetFramework": "net10.0",
            "parseContext": "default", "sources": ["src/App/**/*.cs"], "testSuites": ["unit"]
          }],
          "testSuites": [{
            "id": "unit", "path": "tests/App.Tests/App.Tests.csproj", "runner": "vstest",
            "framework": "net10.0", "configuration": "Release", "expectedMembers": ["App.Tests.dll"]
          }],
          "exclusions": [{ "path": "generated/**/*.cs", "reason": "generated" }]
        }
        """;

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }
}
