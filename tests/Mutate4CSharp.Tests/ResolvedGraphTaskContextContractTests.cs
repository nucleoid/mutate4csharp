using System.Text.Json;

namespace Mutate4CSharp.Tests;

// Strict DATA reader controls only: these never certify actual MSBuild events.
// Real producer coverage is in ResolvedGraphInheritedCompilerFixtureTests.
public sealed class ResolvedGraphTaskContextContractTests
{
    [Theory]
    [InlineData("framework", true)]
    [InlineData("rid", true)]
    [InlineData("all", true)]
    [InlineData("configuration-assignment", true)]
    [InlineData("missing-context", false)]
    [InlineData("removed-present", false)]
    [InlineData("lost-global", false)]
    [InlineData("extra-global", false)]
    [InlineData("altered-global", false)]
    [InlineData("platform", false)]
    [InlineData("unknown", false)]
    [InlineData("duplicate", false)]
    [InlineData("partial-rid", false)]
    [InlineData("empty-piece", false)]
    [InlineData("trailing", false)]
    [InlineData("escaped", false)]
    [InlineData("oversized", false)]
    [InlineData("ambiguous", false)]
    [InlineData("global-alias", false)]
    [InlineData("assignment-conflict", false)]
    public void Data_edge_removal_requires_literal_profile_and_complete_observed_child_globals(string mode, bool accepted)
    {
        // Complete query + observed-context DATA only, never producer credit.
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "mutate-removal-contract-data"));
        var parentPath = Path.Combine(root, "Parent.csproj");
        var childPath = Path.Combine(root, "Child.csproj");
        var parentGlobals = new Dictionary<string, string> {
            ["Configuration"] = "Release", ["TargetFramework"] = "net10.0", ["Feature"] = "preserved" };
        if (mode is "rid" or "all")
        {
            parentGlobals["RuntimeIdentifier"] = "linux-x64";
            parentGlobals["SelfContained"] = "false";
        }
        var childGlobals = new Dictionary<string, string>(parentGlobals);
        if (mode != "rid") childGlobals.Remove("TargetFramework");
        childGlobals.Remove("RuntimeIdentifier");
        childGlobals.Remove("SelfContained");
        var raw = mode switch {
            "rid" => ";RuntimeIdentifier;SelfContained", "all" => ";TargetFramework;RuntimeIdentifier;SelfContained",
            "platform" => ";Platform", "unknown" => ";CredentialMustNotLeak",
            "duplicate" => ";TargetFramework;TargetFramework", "partial-rid" => ";RuntimeIdentifier",
            "empty-piece" => ";;TargetFramework", "trailing" => ";TargetFramework;",
            "escaped" => ";TargetFramework%3bRuntimeIdentifier", "oversized" => new string('x', 257),
            _ => ";TargetFramework" };
        if (mode == "removed-present") childGlobals["TargetFramework"] = "net10.0";
        if (mode == "lost-global") childGlobals.Remove("Feature");
        if (mode == "extra-global") childGlobals["Unadmitted"] = "extra";
        if (mode == "altered-global") childGlobals["Feature"] = "different";
        if (mode == "global-alias") childGlobals["configuration"] = "Release";
        var childConfiguration = mode == "configuration-assignment" ? "Debug" : "Release";
        if (mode == "configuration-assignment") childGlobals["Configuration"] = childConfiguration;
        ResolvedGraphObservedTask TaskRecord(string path, int instance, Dictionary<string, string> globals,
            string framework, string configuration) => new("DATA_ONLY_" + instance, path, globals,
                "DATA_ONLY_TASK_IMAGE", "DATA_ONLY_TARGET", ["/define:DATA_ONLY"], false) {
                ProjectInstanceId = instance, EvaluationId = -1,
                EvaluatedProperties = new Dictionary<string, string> {
                    ["TargetFramework"] = framework, ["Configuration"] = configuration, ["MSBuildProjectFullPath"] = path } };
        var parent = TaskRecord(parentPath, 10, parentGlobals, "net10.0", "Release");
        var child = TaskRecord(childPath, 11, childGlobals, mode == "rid" ? "net10.0" : "netstandard2.1", childConfiguration);
        var contexts = new List<ResolvedGraphObservedTask> { parent, child };
        if (mode == "ambiguous") contexts.Add(child with { ProjectInstanceId = 12 });
        var properties = ResolvedProjectCapture.PropertyNames.ToDictionary(name => name, _ => "", StringComparer.Ordinal);
        properties["MSBuildProjectFullPath"] = parentPath;
        properties["TargetFramework"] = "net10.0"; properties["Configuration"] = "Release";
        properties["UseSharedCompilation"] = "false"; properties["ProvideCommandLineArgs"] = "true";
        var items = ResolvedProjectCapture.ItemNames.ToDictionary(name => name,
            _ => Array.Empty<Dictionary<string, string>>(), StringComparer.Ordinal);
        items["Compile"] = [new() { ["Identity"] = "Parent.cs" }];
        items["CscCommandLineArgs"] = [new() { ["Identity"] = "/define:DATA_ONLY" }];
        items["ReferencePathWithRefAssemblies"] = [new() { ["Identity"] = "reference.dll" }];
        var edge = new Dictionary<string, string> { ["Identity"] = "Child.csproj", ["UndefineProperties"] = raw };
        if (mode == "configuration-assignment") edge["SetConfiguration"] = "Configuration=Debug";
        if (mode == "assignment-conflict") edge["SetTargetFramework"] = "TargetFramework=netstandard2.1";
        items["_MSBuildProjectReferenceExistent"] = [edge];
        var json = JsonSerializer.Serialize(new { Properties = properties, Items = items });
        ResolvedProjectCapture Read() => ResolvedProjectCapture.ReadActual(root, new("Parent.csproj", "net10.0", "Release"),
            json, CancellationToken.None, mode == "missing-context" ? null : parent,
            mode == "missing-context" ? null : contexts);
        if (accepted)
        {
            var result = Assert.Single(Read().Edges);
            Assert.Equal(raw, result.ActualMetadata["UndefineProperties"]);
            Assert.Equal(child.EvaluatedProperties["TargetFramework"], result.Selection.Framework);
            Assert.Equal(childConfiguration, result.Selection.Configuration);
            Assert.Equal("net10.0", parentGlobals["TargetFramework"]);
            Assert.Equal(-1, child.EvaluationId);
        }
        else
        {
            var failure = Assert.Throws<EvaluationContractException>(() => Read());
            if (mode != "ambiguous")
            {
                Assert.Contains("ValueSha256=", failure.Message);
                Assert.Contains("ValueLength=" + raw.Length, failure.Message);
                Assert.DoesNotContain("CredentialMustNotLeak", failure.Message);
                Assert.True(failure.Message.Length <= 512);
            }
            else Assert.Contains("Exactly one actual evaluated project-reference Csc instance", failure.Message);
        }
    }

    [Theory]
    [InlineData("complete")]
    [InlineData("missing-framework")]
    [InlineData("case-alias")]
    [InlineData("foreign-path")]
    [InlineData("instance-mismatch")]
    [InlineData("missing-instance")]
    [InlineData("ambiguous")]
    public async Task Data_context_requires_evaluated_fields_exact_instance_and_unique_selection(string mode)
    {
        await using var owner = OwnedDirectory.Create(OwnedDirectory.PrivateParent("context-contract"), "data");
        var original = new ResolvedGraphBudget(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        using var inputs = new ResolvedGraphInputs(original);
        var path = Path.Combine(owner.Root, "Dependency.csproj");
        var evaluated = new Dictionary<string, string> {
            ["TargetFramework"] = "netstandard2.1", ["Configuration"] = "Debug", ["MSBuildProjectFullPath"] = path };
        if (mode == "missing-framework") evaluated.Remove("TargetFramework");
        if (mode == "case-alias") evaluated["targetframework"] = "net10.0";
        if (mode == "foreign-path") evaluated["MSBuildProjectFullPath"] = Path.Combine(owner.Root, "Foreign.csproj");
        Dictionary<string, object> Row(int instance) => new()
        {
            ["Identity"] = new { Project = new { Submission = 0, Node = 1, ProjectContext = instance, ProjectInstance = instance }, Target = 1, Task = 1 },
            ["Project"] = new Dictionary<string, object> {
                ["ProjectFile"] = path,
                ["GlobalProperties"] = new Dictionary<string, string> { ["Configuration"] = "Debug" },
                ["ProjectInstanceId"] = mode == "instance-mismatch" ? instance + 1 : instance,
                ["EvaluationId"] = 7, ["EvaluatedProperties"] = evaluated },
            ["TaskAssemblyLocation"] = Path.Combine(owner.Root, "Microsoft.Build.Tasks.CodeAnalysis.dll"),
            ["TaskFile"] = Path.Combine(owner.Root, "Microsoft.CSharp.Core.targets"),
            ["Tokens"] = new[] { "/define:DATA_ONLY" },
            ["Switches"] = new { ProvideCommandLineArgs = true, UseSharedCompilation = false, SkipCompilerExecution = false }
        };
        var first = Row(11);
        if (mode == "missing-instance") ((Dictionary<string, object>)first["Project"]).Remove("ProjectInstanceId");
        var rows = mode == "ambiguous" ? new[] { first, Row(12) } : new[] { first };
        var digest = new string('a', 64);
        var file = Path.Combine(owner.Root, "data-events.json");
        inputs.WriteNewResult(file, JsonSerializer.SerializeToUtf8Bytes(new { Version = 3, RequestSha256 = digest, Tasks = rows }));
        if (mode is not ("complete" or "ambiguous"))
        {
            Assert.Throws<EvaluationContractException>(() => ResolvedGraphTaskCapture.Read(file, digest, inputs));
            return;
        }
        var read = ResolvedGraphTaskCapture.Read(file, digest, inputs);
        if (mode == "ambiguous")
            Assert.Throws<EvaluationContractException>(() => read.Select(owner.Root, new("Dependency.csproj", "netstandard2.1", "Debug")));
        else
        {
            var selected = read.Select(owner.Root, new("Dependency.csproj", "netstandard2.1", "Debug"));
            Assert.Equal(11, selected.ProjectInstanceId);
            Assert.Equal(7, selected.EvaluationId);
            Assert.False(selected.GlobalProperties.ContainsKey("TargetFramework"));
            Assert.Equal("netstandard2.1", selected.EvaluatedProperties["TargetFramework"]);
            Assert.Throws<EvaluationContractException>(() => read.Select(owner.Root, new("Dependency.csproj", "net10.0", "Debug")));
        }
    }
}
