using System.Reflection;
using System.Text;
using System.Text.Json;
using Json.Schema;

namespace Mutate4CSharp.Tests;

public sealed class IssueFiveReviewRoundOneTests : IDisposable
{
    private static readonly JsonSchema ConfigurationSchema = ConfigurationSchemaLoader.Load(File.ReadAllText(Path.Combine(
        Path.GetFullPath("../../../../../", AppContext.BaseDirectory),
        "docs/contracts/check-config-v1.schema.json")));
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mutate4csharp-issue5-r1",
        Guid.NewGuid().ToString("N"));

    public IssueFiveReviewRoundOneTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task MultiMemberSuiteLoadsDeduplicatesAndRunsOnce()
    {
        var configuration = Load(ValidConfiguration.Replace("[\"App.Tests.dll\"]",
            "[\"App.Tests.dll\", \"Shared.Tests.dll\"]", StringComparison.Ordinal));
        Assert.Equal(2, Assert.Single(configuration.ExecutionSuites).ExpectedMembers.Count);

        var calls = 0;
        var executor = new DelegateSuiteExecutor((suite, _, _) =>
        {
            calls++;
            return Task.FromResult(new SuiteRunResult(SuiteRunDisposition.Passed, TimeSpan.Zero,
                suite.ExpectedMembers, [], [], ["coverage.xml"]));
        });
        var result = await new SuiteCoordinator(executor, TimeProvider.System).RunBaselinesAsync(
            "snapshot", configuration.ExecutionSuites, TimeSpan.FromSeconds(5),
            DateTimeOffset.UtcNow.AddMinutes(1), CancellationToken.None);

        Assert.Equal(1, calls);
        Assert.Equal(BaselineStatus.Green, result.Status);
    }

    [Fact]
    public void MultiMemberIdentityIsUnambiguous()
    {
        var first = Load(ValidConfiguration.Replace("[\"App.Tests.dll\"]",
            "[\"A\", \"B\"]", StringComparison.Ordinal));
        var second = Load(ValidConfiguration.Replace("[\"App.Tests.dll\"]",
            "[\"AB\", \"C\"]", StringComparison.Ordinal));

        Assert.NotEqual(Assert.Single(first.ExecutionSuites).Identity,
            Assert.Single(second.ExecutionSuites).Identity);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AggregationRequiresExactMappedSuiteAccounting(bool unknownEvidence)
    {
        var method = typeof(SuiteCoordinator).GetMethod(nameof(SuiteCoordinator.AggregateMutant),
            BindingFlags.Public | BindingFlags.Static, [typeof(string), typeof(IReadOnlyList<string>),
                typeof(IReadOnlyList<SuiteMutationEvidence>)]);
        Assert.NotNull(method);
        var mapped = new[] { "one", "two" };
        var evidence = new[]
        {
            new SuiteMutationEvidence(unknownEvidence ? "other" : "one", SuiteRunDisposition.Killed, [], [])
        };

        var result = Assert.IsType<AggregatedMutation>(method!.Invoke(null,
            ["mutation:v1:" + new string('a', 64), mapped, evidence]));

        Assert.Equal(UnitDisposition.Error, result.Disposition);
        Assert.Contains(result.Evidence, item => item.Kind == "SUITE_EVIDENCE_MISSING");
    }

    [Fact]
    public void AggregationRejectsNullEvidenceCollections()
    {
        var evidence = new[]
        {
            new SuiteMutationEvidence("one", SuiteRunDisposition.Killed, null!, []),
            new SuiteMutationEvidence("two", SuiteRunDisposition.Survived, [], null!)
        };

        var result = InvokeAggregate(["one", "two"], evidence);

        Assert.Equal(UnitDisposition.Error, result.Disposition);
        Assert.Contains(result.Evidence, item => item.Kind == "SUITE_EVIDENCE_INVALID");
    }

    [Theory]
    [InlineData("net8.0", "Release")]
    [InlineData("net10.0", "Release AnyCPU")]
    public void RejectsUnsupportedFrameworkAndInvalidConfiguration(string framework, string configuration)
    {
        var json = ValidConfiguration.Replace("\"framework\": \"net10.0\"",
                $"\"framework\": \"{framework}\"", StringComparison.Ordinal)
            .Replace("\"configuration\": \"Release\"", $"\"configuration\": \"{configuration}\"",
                StringComparison.Ordinal);

        Assert.Throws<ArgumentException>(() => Load(json));
    }

    [Fact]
    public void RejectsMembersThatDifferOnlyByCase()
    {
        var json = ValidConfiguration.Replace("[\"App.Tests.dll\"]",
            "[\"App.Tests.dll\", \"app.tests.dll\"]", StringComparison.Ordinal);

        Assert.Throws<ArgumentException>(() => Load(json));
    }

    [Theory]
    [InlineData("projects", "[null]")]
    [InlineData("testSuites", "[null]")]
    [InlineData("exclusions", "[null]")]
    public void NullArrayMembersAreConfigurationErrors(string property, string replacement)
    {
        var json = ReplacePropertyArray(ValidConfiguration, property, replacement);

        Assert.Throws<CheckConfigurationException>(() => ScopeConfiguration.Load(_root,
            Encoding.UTF8.GetBytes(json)));
    }

    [Theory]
    [InlineData("C:/x.csproj")]
    [InlineData("C:x.csproj")]
    [InlineData("../x.csproj")]
    [InlineData("..")]
    [InlineData("src/..")]
    [InlineData("src/App/")]
    [InlineData("src/App.txt")]
    public void RuntimeAndSchemaRejectInvalidProjectPaths(string path)
    {
        var json = ValidConfiguration.Replace("src/App/App.csproj", path, StringComparison.Ordinal);
        using var document = JsonDocument.Parse(json);
        Assert.False(ConfigurationSchema.Evaluate(document.RootElement).IsValid);
        Assert.Throws<ArgumentException>(() => Load(json));
    }

    [Fact]
    public async Task TrxMemberAccountingIsCaseInsensitive()
    {
        var suite = Assert.Single(Load(ValidConfiguration).ExecutionSuites);
        var executor = new DelegateSuiteExecutor((_, _, _) => Task.FromResult(new SuiteRunResult(
            SuiteRunDisposition.Passed, TimeSpan.Zero, ["app.tests.dll"], [], [], ["coverage.xml"])));

        var result = await new SuiteCoordinator(executor, TimeProvider.System).RunBaselinesAsync(
            "snapshot", [suite], TimeSpan.FromSeconds(5), DateTimeOffset.UtcNow.AddMinutes(1),
            CancellationToken.None);

        Assert.Equal(BaselineStatus.Green, result.Status);
    }

    [Fact]
    public async Task CoverageDiscoveryAcceptsOnlyNonemptyOpenCoverAndPreservesItWithOwnedLifetime()
    {
        var source = Path.Combine(_root, "coverage-source");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "cobertura.coverage.xml"), "<coverage />");
        File.WriteAllText(Path.Combine(source, "empty.coverage.xml"), "<CoverageSession><Modules /></CoverageSession>");
        File.WriteAllText(Path.Combine(source, "valid.coverage.xml"),
            "<CoverageSession><Modules><Module /></Modules></CoverageSession>");

        var method = typeof(VstestSuiteExecutor).GetMethod("PreserveOpenCoverReportsAsync",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        var task = Assert.IsAssignableFrom<Task<OwnedCoverageReports?>>(method!.Invoke(null,
            [TestRunner.FindCoverage(source), "fixture"]));
        var owned = Assert.IsType<OwnedCoverageReports>(await task);
        Directory.Delete(source, true);

        var report = Assert.Single(owned.Reports);
        Assert.True(File.Exists(report));
        Assert.Contains("CoverageSession", File.ReadAllText(report), StringComparison.Ordinal);
        await owned.DisposeAsync();
        Assert.False(File.Exists(report));
    }

    [Fact]
    public void VstestArgumentsCarryFrameworkAndConfigurationAsSeparateValues()
    {
        var method = typeof(TestRunner).GetMethod("BuildArguments", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        var arguments = Assert.IsAssignableFrom<IReadOnlyList<string>>(method!.Invoke(null,
            ["tests/App.Tests/App.Tests.csproj", "results", false, true, false, "net10.0", "Release"]));

        Assert.Contains(["-f", "net10.0"], arguments.Chunk(2));
        Assert.Contains(["-c", "Release"], arguments.Chunk(2));
    }

    [Fact]
    public void CaseVariantPathAliasesConflictOnEveryPlatform()
    {
        var alias = Suite.Replace("\"id\": \"unit\"", "\"id\": \"alias\"", StringComparison.Ordinal)
            .Replace("tests/App.Tests/App.Tests.csproj", "Tests/App.Tests/App.Tests.csproj",
                StringComparison.Ordinal)
            .Replace("\"configuration\": \"Release\"", "\"configuration\": \"Debug\"",
                StringComparison.Ordinal);
        var json = ValidConfiguration.Replace("\"testSuites\": [", "\"testSuites\": [" + alias + ",",
                StringComparison.Ordinal)
            .Replace("\"testSuites\": [\"unit\"]", "\"testSuites\": [\"unit\", \"alias\"]",
                StringComparison.Ordinal);

        Assert.Throws<ArgumentException>(() => Load(json));
    }

    [Fact]
    public void ScopeLoaderStrictlyValidatesCaseVariantTestSuitesKey()
    {
        var invalid = ValidConfiguration.Replace("\"testSuites\": [", "\"TestSuites\": [",
                StringComparison.Ordinal)
            .Replace("\"runner\": \"vstest\"", "\"runner\": \"mtp\"", StringComparison.Ordinal);

        Assert.Throws<CheckConfigurationException>(() => ScopeConfiguration.Load(_root,
            Encoding.UTF8.GetBytes(invalid)));
    }

    [Fact]
    public async Task SchedulerTurnsLateConclusiveResultIntoMutantTimeout()
    {
        var time = new AdjustableTimeProvider(DateTimeOffset.UtcNow);
        var mutation = new ScheduledMutation("mutation:v1:" + new string('b', 64),
            "evaluation:v1:" + new string('c', 64), ["unit"]);
        var executor = new DelegateMutationExecutor((work, _, _) =>
        {
            time.Advance(TimeSpan.FromSeconds(11));
            return Task.FromResult(new ScheduledMutationResult(work.EvaluationUnitId, UnitDisposition.Killed,
                [new("KILLED", "late result")]));
        });

        var result = await new EvaluationScheduler(executor, time).RunAsync([mutation], 1,
            TimeSpan.FromSeconds(10), time.GetUtcNow().AddMinutes(1), CancellationToken.None);

        Assert.Equal(UnitDisposition.Error, Assert.Single(result.Results).Disposition);
        Assert.Contains(Assert.Single(result.Results).Evidence, item => item.Kind == "MUTANT_TIMEOUT");
        Assert.DoesNotContain(result.IncompleteConditions, item => item.Code == "OVERALL_DEADLINE_EXCEEDED");
    }

    [Fact]
    public async Task ExactIdRequestIsDiagnosticOnlyAndDoesNotPublishDiscovery()
    {
        using var repository = new SnapshotTestRepository();
        repository.WriteText("src/A.cs", "class A { int M() => 1; }\n");
        repository.Git("add", ".");
        repository.Git("commit", "-m", "fixture");
        var report = Path.Combine(_root, "exact.json");
        var mutation = "mutation:v1:" + new string('d', 64);
        var fingerprint = "sha256:" + new string('e', 64);

        var result = await new EvaluationCoordinator().RunAsync(new(false, null,
            [Path.Combine(repository.Root, "src/A.cs")], report, "exact-id", false, [mutation], fingerprint),
            CancellationToken.None);

        Assert.Equal(EvaluationOutcome.Incomplete, result.Report.Outcome);
        Assert.Contains(result.Report.Reasons, item => item.Code == "EXACT_ID_RERUN_UNAVAILABLE");
        Assert.Contains(result.Report.Evidence, item => item.Kind == "EXACT_ID_REQUEST");
        var discovery = Path.Combine(repository.Root, ".mutate4csharp", "discovery");
        Assert.False(Directory.Exists(discovery) && Directory.EnumerateFiles(discovery, "*.json").Any());
    }

    private AggregatedMutation InvokeAggregate(IReadOnlyList<string> mapped,
        IReadOnlyList<SuiteMutationEvidence> evidence)
    {
        var method = typeof(SuiteCoordinator).GetMethod(nameof(SuiteCoordinator.AggregateMutant),
            BindingFlags.Public | BindingFlags.Static, [typeof(string), typeof(IReadOnlyList<string>),
                typeof(IReadOnlyList<SuiteMutationEvidence>)]);
        Assert.NotNull(method);
        return Assert.IsType<AggregatedMutation>(method!.Invoke(null,
            ["mutation:v1:" + new string('f', 64), mapped, evidence]));
    }

    private CheckConfiguration Load(string json) => CheckConfiguration.Load(_root, Encoding.UTF8.GetBytes(json));

    private static string ReplacePropertyArray(string json, string property, string replacement)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement.EnumerateObject().ToDictionary(item => item.Name,
            item => item.Value.GetRawText(), StringComparer.Ordinal);
        root[property] = replacement;
        return "{" + string.Join(',', root.Select(item => JsonSerializer.Serialize(item.Key) + ":" + item.Value)) + "}";
    }

    private const string Suite = """
        {
          "id": "unit", "path": "tests/App.Tests/App.Tests.csproj", "runner": "vstest",
          "framework": "net10.0", "configuration": "Release", "expectedMembers": ["App.Tests.dll"]
        }
        """;

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

    private sealed class DelegateSuiteExecutor(
        Func<SuiteExecution, TimeSpan, CancellationToken, Task<SuiteRunResult>> run) : ISuiteExecutor
    {
        public Task<SuiteRunResult> RunBaselineAsync(SuiteExecution suite, TimeSpan timeout,
            CancellationToken cancellationToken) => run(suite, timeout, cancellationToken);
    }

    private sealed class DelegateMutationExecutor(
        Func<ScheduledMutation, TimeSpan, CancellationToken, Task<ScheduledMutationResult>> run) :
        IIsolatedMutationExecutor
    {
        public Task<ScheduledMutationResult> ExecuteAsync(ScheduledMutation mutation, TimeSpan timeout,
            CancellationToken cancellationToken) => run(mutation, timeout, cancellationToken);
    }

    private sealed class AdjustableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }
}
