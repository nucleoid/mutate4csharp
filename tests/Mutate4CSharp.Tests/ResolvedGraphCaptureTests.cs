using System.Text.Json;
using Xunit;

namespace Mutate4CSharp.Tests;

// Query DATA contract tests; these do not certify a real MSBuild invocation.
public sealed class ResolvedGraphCaptureTests
{
    [Fact]
    public void Complete_actual_context_retains_independent_reference_configuration_and_aliases()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "mutate-graph-query-data"));
        var query = Query(root);
        query.Items["_MSBuildProjectReferenceExistent"] = [new Dictionary<string, string> {
            ["Identity"] = "../Dependency/Dependency.csproj", ["SetConfiguration"] = "Configuration=Debug",
            ["SetTargetFramework"] = "TargetFramework=net10.0", ["Aliases"] = "dependencyAlias" }];
        var captured = Read(root, query);
        Assert.Equal("Release", captured.Selection.Configuration);
        var edge = Assert.Single(captured.Edges);
        Assert.Equal("src/Dependency/Dependency.csproj", edge.Selection.Project);
        Assert.Equal("Debug", edge.Selection.Configuration);
        Assert.Equal("dependencyAlias", edge.ActualMetadata["Aliases"]);
        Assert.Equal("/define:PRODUCTION", captured.Items["CscCommandLineArgs"][0].Identity);
    }

    [Fact]
    public void Uncaptured_edge_transform_is_refused_instead_of_using_parent_defaults()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "mutate-graph-query-data"));
        var query = Query(root);
        query.Items["_MSBuildProjectReferenceExistent"] = [new Dictionary<string, string> {
            ["Identity"] = "../Dependency/Dependency.csproj", ["AdditionalProperties"] = "Feature=true" }];
        Assert.Throws<EvaluationContractException>(() => Read(root, query));
    }

    [Theory]
    [InlineData("Configuration", "Debug")]
    [InlineData("TargetFramework", "net9.0")]
    [InlineData("UseSharedCompilation", "true")]
    [InlineData("ProvideCommandLineArgs", "false")]
    [InlineData("SkipCompilerExecution", "true")]
    public void Foreign_context_or_effectful_capture_mode_is_not_an_actual_requested_compiler_context(string property, string value)
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "mutate-graph-query-data"));
        var query = Query(root); query.Properties[property] = value;
        Assert.Throws<EvaluationContractException>(() => Read(root, query));
    }

    [Fact]
    public void Missing_complete_reference_category_is_not_a_partial_graph_success()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "mutate-graph-query-data"));
        var query = Query(root); query.Items.Remove("Analyzer");
        Assert.Throws<EvaluationContractException>(() => Read(root, query));
    }

    private static ResolvedProjectCapture Read(string root, QueryData query) =>
        ResolvedProjectCapture.ReadActual(root, new("src/Production/Production.csproj", "net10.0", "Release"),
            JsonSerializer.Serialize(query), CancellationToken.None);

    private static QueryData Query(string root)
    {
        var properties = ResolvedProjectCapture.PropertyNames.ToDictionary(n => n, _ => "", StringComparer.Ordinal);
        properties["MSBuildProjectFullPath"] = Path.Combine(root, "src", "Production", "Production.csproj");
        properties["TargetFramework"] = "net10.0";
        properties["Configuration"] = "Release";
        properties["UseSharedCompilation"] = "false";
        properties["ProvideCommandLineArgs"] = "true";
        var items = ResolvedProjectCapture.ItemNames.ToDictionary(n => n,
            _ => Array.Empty<Dictionary<string, string>>(), StringComparer.Ordinal);
        items["Compile"] = [new() { ["Identity"] = "Production.cs" }];
        items["CscCommandLineArgs"] = [new() { ["Identity"] = "/define:PRODUCTION" }];
        items["ReferencePathWithRefAssemblies"] = [new() { ["Identity"] = "reference.dll", ["Aliases"] = "global" }];
        return new(properties, items);
    }

    private sealed record QueryData(Dictionary<string, string> Properties,
        Dictionary<string, Dictionary<string, string>[]> Items);
}