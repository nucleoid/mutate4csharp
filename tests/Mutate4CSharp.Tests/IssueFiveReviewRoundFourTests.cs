using System.Reflection;
using System.Text;
using System.Text.Json;
using Json.Schema;

namespace Mutate4CSharp.Tests;

public sealed class IssueFiveReviewRoundFourTests : IDisposable
{
    private static readonly JsonSchema Schema = ConfigurationSchemaLoader.Load(File.ReadAllText(
        Path.Combine(Path.GetFullPath("../../../../../", AppContext.BaseDirectory), "docs/contracts/check-config-v1.schema.json")));
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mutate4csharp-issue5-r4", Guid.NewGuid().ToString("N"));
    public IssueFiveReviewRoundFourTests() => Directory.CreateDirectory(_root);

    [Theory, MemberData(nameof(SchemaRuntimeCases))]
    public void SchemaAndRuntimeAgreeOnFinalControlsSlashesAndLineSeparators(string json, bool expected)
    {
        using var document = JsonDocument.Parse(json);
        Assert.Equal(expected, Schema.Evaluate(document.RootElement).IsValid);
        Assert.Equal(expected, RuntimeAccepts(json));
    }

    public static IEnumerable<object[]> SchemaRuntimeCases()
    {
        yield return [Valid.Replace("\"id\": \"app\"", "\"id\": \"app\\n\"", StringComparison.Ordinal), false];
        yield return [Valid.Replace("\"framework\": \"net10.0\"", "\"framework\": \"net10.0\\n\"", StringComparison.Ordinal), false];
        yield return [Valid.Replace("\"configuration\": \"Release\"", "\"configuration\": \"Release\\n\"", StringComparison.Ordinal), false];
        yield return [Valid.Replace("src/App/**/*.cs", "src/App/", StringComparison.Ordinal), false];
        yield return [Valid.Replace("generated/**/*.cs", "generated/", StringComparison.Ordinal), false];
        yield return [Valid.Replace("src/App/**/*.cs", "src/Line\u2028Separator.cs", StringComparison.Ordinal), true];
        yield return [Valid.Replace("src/App/**/*.cs", "src/Paragraph\u2029Separator.cs", StringComparison.Ordinal), true];
    }

    [Fact]
    public void RuntimeUsesUnicodeScalarLengthEvenWhenJsonSchemaNetCountsGraphemes()
    {
        var reason = string.Concat(Enumerable.Repeat("e\u0301", 300));
        var json = Valid.Replace("generated", reason, StringComparison.Ordinal);
        using var document = JsonDocument.Parse(json);
        Assert.Equal(600, reason.EnumerateRunes().Count());
        Assert.False(RuntimeAccepts(json));
        Assert.True(Schema.Evaluate(document.RootElement).IsValid); // Known library drift; normative JSON Schema counts scalars.
    }

    [Theory]
    [InlineData("1.00000000000000000000000000000001", false)]
    [InlineData("2e0", true)]
    [InlineData("20e-1", true)]
    [InlineData("2.00000000000000000000000000000000", true)]
    [InlineData("2147483648", false)]
    public void PolicyNumbersMustBeMathematicallyExactInt32(string token, bool expected) =>
        Assert.Equal(expected, RuntimeAccepts(Valid.Replace("\"maxWorkers\": 2", $"\"maxWorkers\": {token}", StringComparison.Ordinal)));

    [Theory]
    [InlineData("0.99999999999999999999999999999999", false)]
    [InlineData("1e0", true)]
    [InlineData("10e-1", true)]
    [InlineData("1.00000000000000000000000000000000", true)]
    public void VersionMustBeMathematicallyExactOne(string token, bool expected) =>
        Assert.Equal(expected, RuntimeAccepts(Valid.Replace("\"version\": 1", $"\"version\": {token}", StringComparison.Ordinal)));

    [Fact]
    public void AggregateRejectsUndefinedDisposition()
    {
        var result = SuiteCoordinator.AggregateMutant(MutationId, ["one", "two"],
            [new("one", SuiteRunDisposition.Killed, [], []), new("two", (SuiteRunDisposition)42, [], [])]);
        Assert.Equal(UnitDisposition.Error, result.Disposition);
        Assert.Contains(result.Evidence, item => item.Kind == "SUITE_EVIDENCE_INVALID");
    }

    [Fact]
    public void AggregateBoundsRetainedEvidenceButReportsExactAccounting()
    {
        var suites = Enumerable.Range(0, 10_000).Select(_ =>
            new SuiteMutationEvidence("one", SuiteRunDisposition.Killed, [], [])).ToArray();
        var result = SuiteCoordinator.AggregateMutant(MutationId, ["one"], suites);
        Assert.Equal(UnitDisposition.Error, result.Disposition);
        Assert.InRange(result.Evidence.Count, 1, 101);
        var accounting = Assert.Single(result.Evidence, item => item.Kind == "SUITE_EVIDENCE_MISSING");
        Assert.Contains(accounting.Diagnostics!, item => item == "received=10000");
        Assert.Contains(accounting.Diagnostics!, item => item == "duplicate-entries=9999");
        Assert.Contains(accounting.Diagnostics!, item => item.StartsWith("retained=", StringComparison.Ordinal));
    }

    [Fact]
    public void SurvivorGuidanceIsStructuredArgvAndCannotBecomeAShellCommand()
    {
        var hostile = "src/$USER/`touch nope`/line\nbreak.cs";
        var diagnostic = Assert.Single(SuiteCoordinator.SurvivorEvidence(MutationId, hostile, 7, "a", "b", Fingerprint).Diagnostics!);
        Assert.StartsWith("rerun-argv-json=", diagnostic, StringComparison.Ordinal);
        var argv = JsonSerializer.Deserialize<string[]>(diagnostic["rerun-argv-json=".Length..]);
        Assert.NotNull(argv);
        Assert.Equal(hostile, argv![Array.IndexOf(argv, "--input") + 1]);
        Assert.DoesNotContain('\n', diagnostic);
    }

    [Fact]
    public void CleanupFailureWrapperPreservesThePrimaryFailure()
    {
        var method = typeof(ProcessTree).GetMethod("CreateCleanupFailure", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        var primary = new IOException("output exceeded");
        var cleanup = new IOException("reaping timed out");
        var wrapped = Assert.IsType<SnapshotCleanupException>(method!.Invoke(null, [primary, cleanup]));
        Assert.Same(primary, wrapped.OriginalFailure);
        Assert.Same(cleanup, wrapped.CleanupFailure);
    }

    [Theory]
    [InlineData("Usage: setsid [options] program\n -w, --wait wait program exit", true)]
    [InlineData("BusyBox v1.36.1 multi-call binary.\nUsage: setsid [-cf] PROG ARGS", false)]
    public void SetSidCapabilityDetectionRequiresWaitSemantics(string help, bool expected)
    {
        var method = typeof(ProcessTree).GetMethod("SetSidSupportsWait", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        Assert.Equal(expected, method!.Invoke(null, [help]));
    }

    [Fact]
    public void LinuxSetSidDependencyIsDocumentedAccurately()
    {
        var docs = File.ReadAllText(Path.Combine(Path.GetFullPath("../../../../../", AppContext.BaseDirectory), "docs/check-configuration.md"));
        Assert.Contains("util-linux", docs, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("setsid --wait", docs, StringComparison.Ordinal);
    }

    private bool RuntimeAccepts(string json)
    {
        try { _ = CheckConfiguration.Load(_root, Encoding.UTF8.GetBytes(json)); return true; }
        catch (ArgumentException) { return false; }
    }

    private const string MutationId = "mutation:v1:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Fingerprint = "sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string Valid = """
        { "version": 1,
          "projects": [{ "id": "app", "project": "src/App/App.csproj", "targetFramework": "net10.0",
            "parseContext": "default", "sources": ["src/App/**/*.cs"], "testSuites": ["unit"] }],
          "testSuites": [{ "id": "unit", "path": "tests/App.Tests/App.Tests.csproj", "runner": "vstest",
            "framework": "net10.0", "configuration": "Release", "expectedMembers": ["App.Tests.dll"] }],
          "exclusions": [{ "path": "generated/**/*.cs", "reason": "generated" }],
          "policy": { "maxWorkers": 2 } }
        """;
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }
}
