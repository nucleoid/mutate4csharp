using System.Text;
using System.Text.Json;
using Json.Schema;

namespace Mutate4CSharp.Tests;

public sealed class CheckConfigurationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mutate4csharp-config", Guid.NewGuid().ToString("N"));

    public CheckConfigurationTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void LoadsVersionedProjectsSuitesExclusionsAndPolicy()
    {
        var configuration = Load(ValidConfiguration);

        Assert.Equal("1", configuration.SchemaVersion);
        Assert.Equal("app", Assert.Single(configuration.Projects).Id);
        Assert.Equal("disable", Assert.Single(configuration.Projects).Nullable);
        Assert.Equal("unit", Assert.Single(configuration.TestSuites).Id);
        Assert.Equal("generated code", Assert.Single(configuration.Exclusions).Reason);
        Assert.Equal(new EvaluationPolicy(2, 25, 90, 30, 300, true, 2), configuration.Policy);
    }

    [Theory]
    [InlineData("\"mystery\": true,", "unknown")]
    [InlineData("\"version\": 2,", "version 1")]
    [InlineData("\"version\": 1, \"projects\": [],", "production project")]
    public void RejectsUnknownUnsupportedAndEmptyDocuments(string replacement, string expected)
    {
        var json = replacement.StartsWith("\"version\": 1", StringComparison.Ordinal)
            ? "{" + replacement + "\"testSuites\": []}"
            : ValidConfiguration.Replace("\"version\": 1,", replacement, StringComparison.Ordinal);

        var error = Assert.Throws<ArgumentException>(() => Load(json));

        Assert.Contains(expected, error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("mtp", "runner")]
    [InlineData("vstest", "member")]
    public void RejectsUnsupportedRunnerAndEmptyExpectedMembers(string runner, string expected)
    {
        var members = expected == "member" ? "[]" : "[\"App.Tests.dll\"]";
        var json = ValidConfiguration.Replace("\"runner\": \"vstest\"", $"\"runner\": \"{runner}\"", StringComparison.Ordinal)
            .Replace("[\"App.Tests.dll\"]", members, StringComparison.Ordinal);

        var error = Assert.Throws<ArgumentException>(() => Load(json));

        Assert.Contains(expected, error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("net10.0-windows")]
    [InlineData("net10.0-linux")]
    public void RejectsPlatformSpecificTargetFrameworksUntilTheirSymbolsAreModelled(string framework)
    {
        var project = ValidConfiguration.Replace(
            "\"targetFramework\": \"net10.0\"", $"\"targetFramework\": \"{framework}\"",
            StringComparison.Ordinal);
        var projectError = Assert.Throws<ArgumentException>(() => Load(project));
        Assert.Contains("exact net10.0", projectError.Message, StringComparison.Ordinal);

        var suite = ValidConfiguration.Replace(
            "\"framework\": \"net10.0\"", $"\"framework\": \"{framework}\"",
            StringComparison.Ordinal);
        var suiteError = Assert.Throws<ArgumentException>(() => Load(suite));
        Assert.Contains("exact net10.0", suiteError.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("../outside.csproj")]
    [InlineData("/outside.csproj")]
    [InlineData("C:/outside.csproj")]
    [InlineData("C:outside.csproj")]
    [InlineData("//server/share.csproj")]
    [InlineData("src//App.csproj")]
    public void RejectsPathEscapesAndNonCanonicalPaths(string path)
    {
        var json = ValidConfiguration.Replace("src/App/App.csproj", path, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => Load(json));
    }

    [Fact]
    public void RejectsDuplicateIdsAndLoadsOverlappingSourcePatternsForOwnershipValidation()
    {
        var duplicate = ValidConfiguration.Replace("\"projects\": [", "\"projects\": [" + Project + ",", StringComparison.Ordinal);
        Assert.Contains("Duplicate", Assert.Throws<ArgumentException>(() => Load(duplicate)).Message,
            StringComparison.OrdinalIgnoreCase);

        var ambiguous = ValidConfiguration.Replace("\"projects\": [", "\"projects\": [" +
            Project.Replace("\"id\": \"app\"", "\"id\": \"other\"", StringComparison.Ordinal)
                .Replace("src/App/App.csproj", "src/Other/Other.csproj", StringComparison.Ordinal) + ",", StringComparison.Ordinal);
        var shared = Load(ambiguous);
        Assert.Equal(2, shared.Projects.Count(project =>
            project.Sources.Contains("src/App/**/*.cs", StringComparer.Ordinal)));
    }

    [Fact]
    public void SharedSourcePatternsMustAlsoBeCompileSourcePatterns()
    {
        var invalid = ValidConfiguration.Replace(
            "\"sources\": [\"src/App/**/*.cs\"]",
            "\"sources\": [\"src/App/**/*.cs\"], \"sharedSources\": [\"shared/**/*.cs\"]",
            StringComparison.Ordinal);

        var error = Assert.Throws<ArgumentException>(() => Load(invalid));

        Assert.Contains("sharedSources", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DeduplicatesExactSuiteAliasesButRefusesConflictingAliases()
    {
        var normalizedConfiguration = ValidConfiguration.ReplaceLineEndings("\n");
        var alias = Suite.ReplaceLineEndings("\n")
            .Replace("\"id\": \"unit\"", "\"id\": \"unit-alias\"", StringComparison.Ordinal);
        var json = normalizedConfiguration
            .Replace("\"testSuites\": [\n    {", "\"testSuites\": [" + alias + ",\n    {", StringComparison.Ordinal)
            .Replace("\"testSuites\": [\"unit\"]", "\"testSuites\": [\"unit\", \"unit-alias\"]", StringComparison.Ordinal);
        Assert.Contains("\"id\": \"unit-alias\"", json, StringComparison.Ordinal);
        var configuration = Load(json);

        Assert.Single(configuration.ExecutionSuites);
        Assert.Equal(["unit", "unit-alias"], configuration.ExecutionSuites[0].Aliases);

        var conflictingAlias = alias.Replace("\"configuration\": \"Release\"",
            "\"configuration\": \"Debug\"", StringComparison.Ordinal);
        var conflict = normalizedConfiguration
            .Replace("\"testSuites\": [\n    {", "\"testSuites\": [" + conflictingAlias + ",\n    {", StringComparison.Ordinal)
            .Replace("\"testSuites\": [\"unit\"]", "\"testSuites\": [\"unit\", \"unit-alias\"]", StringComparison.Ordinal);
        Assert.Contains("\"id\": \"unit-alias\"", conflict, StringComparison.Ordinal);
        Assert.Contains("conflict", Assert.Throws<ArgumentException>(() => Load(conflict)).Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RepositoryExampleMatchesSchemaAndRuntimeLoader()
    {
        var repository = Path.GetFullPath("../../../../../", AppContext.BaseDirectory);
        var bytes = File.ReadAllBytes(Path.Combine(repository, "mutate4csharp.json"));
        var schema = ConfigurationSchemaLoader.Load(File.ReadAllText(Path.Combine(repository,
            "docs/contracts/check-config-v1.schema.json")));
        using var document = JsonDocument.Parse(bytes);

        Assert.True(schema.Evaluate(document.RootElement).IsValid);
        var configuration = CheckConfiguration.Load(repository, bytes);
        Assert.Equal(ExecutionPolicy.Default, configuration.Policy);
        Assert.Single(configuration.ExecutionSuites);
    }

    [Fact]
    public void ConfigurationSchemaCanBeLoadedRepeatedlyInOneTestProcess()
    {
        var repository = Path.GetFullPath("../../../../../", AppContext.BaseDirectory);
        var text = File.ReadAllText(Path.Combine(repository, "docs/contracts/check-config-v1.schema.json"));

        _ = ConfigurationSchemaLoader.Load(text);
        _ = ConfigurationSchemaLoader.Load(text);
    }

    private CheckConfiguration Load(string json) =>
        CheckConfiguration.Load(_root, Encoding.UTF8.GetBytes(json));

    private const string Project = """
        {
          "id": "app", "project": "src/App/App.csproj", "targetFramework": "net10.0",
          "parseContext": "default", "sources": ["src/App/**/*.cs"], "testSuites": ["unit"]
        }
        """;

    private const string Suite = """
        {
          "id": "unit", "path": "tests/App.Tests/App.Tests.csproj", "runner": "vstest",
          "framework": "net10.0", "configuration": "Release", "expectedMembers": ["App.Tests.dll"]
        }
        """;

    private const string ValidConfiguration = """
        {
          "version": 1,
          "projects": [
            {
              "id": "app", "project": "src/App/App.csproj", "targetFramework": "net10.0",
              "parseContext": "default", "sources": ["src/App/**/*.cs"], "testSuites": ["unit"]
            }
          ],
          "testSuites": [
            {
              "id": "unit", "path": "tests/App.Tests/App.Tests.csproj", "runner": "vstest",
              "framework": "net10.0", "configuration": "Release", "expectedMembers": ["App.Tests.dll"]
            }
          ],
          "exclusions": [{ "path": "**/*.g.cs", "reason": "generated code" }],
          "policy": {
            "maxWorkers": 2, "mutationCap": 25, "baselineTimeoutSeconds": 90,
            "mutantTimeoutSeconds": 30, "overallDeadlineSeconds": 300,
            "allowNotApplicable": true, "stabilityRepetitions": 2
          }
        }
        """;

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }
}

internal static class ConfigurationSchemaLoader
{
    public static JsonSchema Load(string text) => JsonSchema.FromText(text,
        new BuildOptions { SchemaRegistry = new SchemaRegistry() });
}
