using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Json.Schema;

namespace Mutate4CSharp.Tests;

public sealed class IssueFiveReviewRoundThreeTests : IDisposable
{
    private static readonly JsonSchema ConfigurationSchema = ConfigurationSchemaLoader.Load(File.ReadAllText(
        Path.Combine(Path.GetFullPath("../../../../../", AppContext.BaseDirectory),
            "docs/contracts/check-config-v1.schema.json")));
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mutate4csharp-issue5-r3",
        Guid.NewGuid().ToString("N"));

    public IssueFiveReviewRoundThreeTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task ResultBoundingFailureDisposesPreservedCoverage()
    {
        var owner = OwnedDirectory.Create(Path.Combine(_root, "coverage"), "bounded");
        var owned = new OwnedCoverageReports(owner, [Path.Combine(owner.Root, "coverage.xml")]);
        var executor = new DelegateSuiteExecutor((_, _, _) => Task.FromResult(new SuiteRunResult(
            SuiteRunDisposition.Passed, TimeSpan.Zero, [null!], [], [], owned.Reports)
            { CoverageOwner = owned }));

        var result = await new SuiteCoordinator(executor, TimeProvider.System).RunBaselinesAsync(
            "snapshot", [Suite("unit")], TimeSpan.FromSeconds(5), DateTimeOffset.UtcNow.AddMinutes(1),
            CancellationToken.None);

        Assert.Equal(BaselineStatus.Unknown, result.Status);
        Assert.False(Directory.Exists(owner.Root));
    }

    [Fact]
    public async Task BaselineDisposalAttemptsEveryCoverageOwnerAndAggregatesFailures()
    {
        var firstOwner = OwnedDirectory.Create(Path.Combine(_root, "coverage"), "first");
        var secondOwner = OwnedDirectory.Create(Path.Combine(_root, "coverage"), "second");
        var thirdOwner = OwnedDirectory.Create(Path.Combine(_root, "coverage"), "third");
        File.Delete(Path.Combine(firstOwner.Root, ".mutate4csharp-owner"));
        File.Delete(Path.Combine(secondOwner.Root, ".mutate4csharp-owner"));
        var first = new SuiteRunResult(SuiteRunDisposition.Passed, TimeSpan.Zero, [], [], [], [])
            { CoverageOwner = new OwnedCoverageReports(firstOwner, []) };
        var second = new SuiteRunResult(SuiteRunDisposition.Passed, TimeSpan.Zero, [], [], [], [])
            { CoverageOwner = new OwnedCoverageReports(secondOwner, []) };
        var third = new SuiteRunResult(SuiteRunDisposition.Passed, TimeSpan.Zero, [], [], [], [])
            { CoverageOwner = new OwnedCoverageReports(thirdOwner, []) };
        var result = new SuiteBaselineResult(BaselineStatus.Green,
            [new("first", ["first"], first), new("second", ["second"], second),
                new("third", ["third"], third)], []);

        var error = await Assert.ThrowsAsync<AggregateException>(async () => await result.DisposeAsync());

        Assert.Equal(2, error.InnerExceptions.Count);
        Assert.False(Directory.Exists(thirdOwner.Root));
    }

    [Theory]
    [MemberData(nameof(ParityCases))]
    public void SchemaAndRuntimeAgreeOnUnicodeWhitespaceAndIntegralNumbers(string json, bool expectedValid)
    {
        using var document = JsonDocument.Parse(json);
        var schemaValid = ConfigurationSchema.Evaluate(document.RootElement).IsValid;
        var runtimeValid = true;
        try { _ = CheckConfiguration.Load(_root, Encoding.UTF8.GetBytes(json)); }
        catch (ArgumentException) { runtimeValid = false; }

        Assert.Equal(expectedValid, schemaValid);
        Assert.Equal(expectedValid, runtimeValid);
    }

    public static IEnumerable<object[]> ParityCases()
    {
        yield return [ValidConfiguration.Replace("generated", "a\u0085b", StringComparison.Ordinal), false];
        yield return [ValidConfiguration.Replace("generated", string.Concat(Enumerable.Repeat("😀", 512)),
            StringComparison.Ordinal), true];
        yield return [ValidConfiguration.Replace("generated", "\uFEFFgenerated", StringComparison.Ordinal), false];
        yield return [ValidConfiguration.Replace("\"maxWorkers\": 2", "\"maxWorkers\": 2.0",
            StringComparison.Ordinal), true];
        yield return [ValidConfiguration.Replace("\"maxWorkers\": 2", "\"maxWorkers\": 2e0",
            StringComparison.Ordinal), true];
    }

    [Fact]
    public void CaseEquivalentExpectedMembersAreRejected()
    {
        var json = ValidConfiguration.Replace("[\"App.Tests.dll\"]",
            "[\"App.Tests.dll\",\"app.tests.dll\"]", StringComparison.Ordinal);

        Assert.Throws<ArgumentException>(() => CheckConfiguration.Load(_root, Encoding.UTF8.GetBytes(json)));
    }

    [Fact]
    public void AggregateRetainsValidEvidenceWhileRefusingNullEntriesAndCollections()
    {
        IReadOnlyList<SuiteMutationEvidence> evidence =
        [
            new("one", SuiteRunDisposition.Killed, ["failed"], ["kept"]),
            null!,
            new("two", SuiteRunDisposition.Survived, null!, [])
        ];

        var result = SuiteCoordinator.AggregateMutant("mutation:v1:" + new string('a', 64),
            ["one", "two"], evidence);

        Assert.Equal(UnitDisposition.Error, result.Disposition);
        Assert.Contains(result.Evidence, item => item.Kind == "SUITE_MUTANT_RESULT" &&
            item.Summary.Contains("one", StringComparison.Ordinal));
        Assert.Contains(result.Evidence, item => item.Kind == "SUITE_EVIDENCE_INVALID");
    }

    [Fact]
    public void AggregateRejectsNullSuiteIdWithoutThrowingAndRetainsOtherEvidence()
    {
        var result = SuiteCoordinator.AggregateMutant("mutation:v1:" + new string('b', 64), ["one"],
            [new("one", SuiteRunDisposition.Killed, [], []), new(null!, SuiteRunDisposition.Survived, [], [])]);

        Assert.Equal(UnitDisposition.Error, result.Disposition);
        Assert.Contains(result.Evidence, item => item.Kind == "SUITE_MUTANT_RESULT");
        Assert.Contains(result.Evidence, item => item.Kind == "SUITE_EVIDENCE_INVALID");
    }

    [Fact]
    public async Task NormalLinuxLeaderExitKillsDescendantsThatHoldOutputOpen()
    {
        if (!OperatingSystem.IsLinux()) return;
        var pidFile = Path.Combine(_root, "descendant.pid");
        var script = $"sleep 60 & echo $! > '{pidFile}'; exit 0";

        var result = await ProcessTree.RunAsync("/bin/sh", ["-c", script], _root,
            TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken,
            requireLinuxSessionIsolation: true);

        Assert.Equal(0, result.ExitCode);
        var pid = int.Parse(File.ReadAllText(pidFile), System.Globalization.CultureInfo.InvariantCulture);
        AssertProcessGone(pid);
    }

    private static void AssertProcessGone(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                Assert.Fail($"Descendant process {pid} remained alive.");
            }
        }
        catch (ArgumentException) { }
    }

    private static SuiteExecution Suite(string id) => new(id, [id], "App.Tests.csproj", "vstest",
        "net10.0", "Release", ["App.Tests.dll"]);

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
          "exclusions": [{ "path": "generated/**/*.cs", "reason": "generated" }],
          "policy": { "maxWorkers": 2 }
        }
        """;

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    private sealed class DelegateSuiteExecutor(
        Func<SuiteExecution, TimeSpan, CancellationToken, Task<SuiteRunResult>> execute) : ISuiteExecutor
    {
        public Task<SuiteRunResult> RunBaselineAsync(SuiteExecution suite, TimeSpan timeout,
            CancellationToken cancellationToken) => execute(suite, timeout, cancellationToken);
    }
}
