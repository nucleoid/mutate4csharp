using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mutate4CSharp.Tests;

// All new graph-profile cases are UNEXECUTED until Root admits real validation.
// Configuration parsing is DATA validation, never graph/compiler provenance.
public sealed class GraphProfileConfigurationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mutate-graph-config-" + Guid.NewGuid().ToString("N"));
    public GraphProfileConfigurationTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void Legacy_positive_precedes_explicit_graph_profile_and_independent_configurations()
    {
        var graph = JsonNode.Parse(Valid)!;
        var legacy = graph.DeepClone().AsObject();
        legacy["version"] = 1; legacy.Remove("semanticProfile");
        legacy["projects"]![0]!.AsObject().Remove("configuration");
        var old = Load(legacy.ToJsonString());
        Assert.False(old.IsResolvedGraph); Assert.Equal("1", old.SchemaVersion);
        Assert.Null(Assert.Single(old.Projects).Configuration);
        var actual = Load(Valid);
        Assert.True(actual.IsResolvedGraph);
        Assert.Equal("resolved-project-graph-v1", actual.SemanticProfile);
        Assert.Equal("Debug", Assert.Single(actual.Projects).Configuration);
        Assert.Equal("Release", Assert.Single(actual.ExecutionSuites).Configuration);
        Assert.Null(actual.Projects[0].Nullable); Assert.Null(actual.Projects[0].DefineConstants);
        Assert.False(actual.Projects[0].HasLanguageVersionAssertion);
        Assert.Equal(old.ExecutionSuites[0].Identity, actual.ExecutionSuites[0].Identity);
        Assert.Single(ScopeConfiguration.Load(_root, Encoding.UTF8.GetBytes(Valid)).Projects);
    }

    [Fact]
    public void Explicit_graph_schema_and_loader_agree_without_widening_v1_schema()
    {
        var repository = Path.GetFullPath("../../../../../", AppContext.BaseDirectory);
        var graphSchema = ConfigurationSchemaLoader.Load(File.ReadAllText(Path.Combine(repository,
            "docs/contracts/check-config-v2.schema.json")));
        var legacySchema = ConfigurationSchemaLoader.Load(File.ReadAllText(Path.Combine(repository,
            "docs/contracts/check-config-v1.schema.json")));
        using var document = JsonDocument.Parse(Valid);
        Assert.True(graphSchema.Evaluate(document.RootElement).IsValid);
        Assert.False(legacySchema.Evaluate(document.RootElement).IsValid);
        Assert.True(Load(Valid).IsResolvedGraph);
    }

    [Theory]
    [InlineData("missing-profile")]
    [InlineData("unknown-profile")]
    [InlineData("profile-case-alias")]
    [InlineData("missing-configuration")]
    [InlineData("configuration-path")]
    [InlineData("v1-profile")]
    [InlineData("v1-configuration")]
    [InlineData("unknown-version")]
    [InlineData("missing-suites")]
    [InlineData("null-profile")]
    [InlineData("unknown-setting")]
    [InlineData("duplicate-project-path")]
    public void Explicit_profile_positive_precedes_each_strict_refusal_and_no_legacy_fallback(string corruption)
    {
        Assert.True(Load(Valid).IsResolvedGraph);
        var root = JsonNode.Parse(Valid)!.AsObject();
        switch (corruption)
        {
            case "missing-profile": root.Remove("semanticProfile"); break;
            case "unknown-profile": root["semanticProfile"] = "trust-caller-graph"; break;
            case "profile-case-alias": root.Remove("semanticProfile"); root["SemanticProfile"] = "resolved-project-graph-v1"; break;
            case "missing-configuration": root["projects"]![0]!.AsObject().Remove("configuration"); break;
            case "configuration-path": root["projects"]![0]!["configuration"] = "../Release"; break;
            case "v1-profile": root["version"] = 1; root["projects"]![0]!.AsObject().Remove("configuration"); break;
            case "v1-configuration": root["version"] = 1; root.Remove("semanticProfile"); break;
            case "unknown-version": root["version"] = 3; break;
            case "missing-suites": root.Remove("testSuites"); break;
            case "null-profile": root["semanticProfile"] = null; break;
            case "unknown-setting": root["allowUntrackedImports"] = true; break;
            case "duplicate-project-path":
                var other = root["projects"]![0]!.DeepClone(); other["id"] = "other";
                other["configuration"] = "Release"; other["parseContext"] = "independent";
                root["projects"]!.AsArray().Add(other); break;
        }
        var bytes = Encoding.UTF8.GetBytes(root.ToJsonString());
        Assert.ThrowsAny<ArgumentException>(() => CheckConfiguration.Load(_root, bytes));
        Assert.Throws<CheckConfigurationException>(() => ScopeConfiguration.Load(_root, bytes));
    }

    [Theory]
    [InlineData("\"version\":2,\"version\":2", "\"version\":2")]
    [InlineData("\"version\":2.5", "\"version\":2")]
    [InlineData("\"semanticProfile\":\"resolved-project-graph-v1\",\"semanticProfile\":\"resolved-project-graph-v1\"", "\"semanticProfile\":\"resolved-project-graph-v1\"")]
    public void Duplicate_or_fractional_versioned_wire_cannot_downgrade_to_legacy(string replacement, string anchor)
    {
        var compact = JsonNode.Parse(Valid)!.ToJsonString();
        Assert.Contains(anchor, compact, StringComparison.Ordinal);
        var bytes = Encoding.UTF8.GetBytes(compact.Replace(anchor, replacement, StringComparison.Ordinal));
        Assert.ThrowsAny<ArgumentException>(() => CheckConfiguration.Load(_root, bytes));
        Assert.Throws<CheckConfigurationException>(() => ScopeConfiguration.Load(_root, bytes));
    }

    private CheckConfiguration Load(string json) => CheckConfiguration.Load(_root, Encoding.UTF8.GetBytes(json));
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Only_explicit_graph_scope_defers_inherited_syntax_to_the_actual_compiler(bool graphProfile)
    {
        Directory.CreateDirectory(Path.Combine(_root, "src/App"));
        Directory.CreateDirectory(Path.Combine(_root, "tests/App.Tests"));
        await File.WriteAllTextAsync(Path.Combine(_root, "src/App/App.csproj"), "<Project/>");
        await File.WriteAllTextAsync(Path.Combine(_root, "tests/App.Tests/App.Tests.csproj"), "<Project/>");
        await File.WriteAllTextAsync(Path.Combine(_root, "src/App/App.cs"), """
            #if !INHERITED_FIXTURE
            #error requires actual inherited compiler defines
            #endif
            public static class App { public static int Value() => 1; }
            """);
        var configuration = JsonNode.Parse(Valid)!.AsObject();
        if (!graphProfile)
        {
            configuration["version"] = 1;
            configuration.Remove("semanticProfile");
            configuration["projects"]![0]!.AsObject().Remove("configuration");
        }
        await File.WriteAllTextAsync(Path.Combine(_root, "mutate4csharp.json"), configuration.ToJsonString());
        var scope = await ScopePlanner.PlanExplicitAsync(["src/App/App.cs"], _root,
            TestContext.Current.CancellationToken);
        Assert.Equal(graphProfile, scope.Plan.IsComplete);
        Assert.Equal("FULL_PROJECT", Assert.Single(scope.Plan.ProjectUnits).Expansion);
        Assert.Equal(graphProfile, ScopeConfiguration.Load(_root).IsResolvedGraph);
        if (graphProfile)
        {
            Assert.Empty(Assert.Single(scope.Plan.Files).Declarations);
            Assert.Contains(Assert.Single(scope.Plan.Files).Reasons,
                reason => reason.Code == "RESOLVED_GRAPH_SCOPE_EXPANSION");
            Assert.DoesNotContain(scope.Plan.Reasons, reason => reason.Code == "UNSUPPORTED_SYNTAX");
        }
        else Assert.Contains(scope.Plan.Reasons, reason => reason.Code == "UNSUPPORTED_SYNTAX");
        // Planning DATA only: this test does not claim successful compilation.
    }

    private const string Valid = """
        {
          "version":2,"semanticProfile":"resolved-project-graph-v1",
          "projects":[{"id":"app","project":"src/App/App.csproj","targetFramework":"net10.0",
            "configuration":"Debug","parseContext":"default","sources":["src/App/**/*.cs"],"testSuites":["unit"]}],
          "testSuites":[{"id":"unit","path":"tests/App.Tests/App.Tests.csproj","runner":"vstest",
            "framework":"net10.0","configuration":"Release","expectedMembers":["App.Tests.dll"]}]
        }
        """;
    public void Dispose() => Directory.Delete(_root, recursive: true);
}
