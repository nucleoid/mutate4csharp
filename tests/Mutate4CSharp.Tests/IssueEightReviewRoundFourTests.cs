using System.Xml.Linq;

namespace Mutate4CSharp.Tests;

public sealed class IssueEightReviewRoundFourTests
{
    private static readonly string RepositoryRoot = Path.GetFullPath("../../../../../", AppContext.BaseDirectory);

    [Fact]
    public void RepositoryNuGetConfigurationMapsTheToolOnlyToTheLocalFeed()
    {
        var configurationPath = Path.Combine(RepositoryRoot, "NuGet.Config");
        Assert.True(File.Exists(configurationPath), $"Missing repository NuGet configuration: {configurationPath}");

        var configuration = XDocument.Load(configurationPath);
        var sources = configuration.Descendants("packageSources").Elements("add")
            .ToDictionary(element => (string)element.Attribute("key")!, element => (string)element.Attribute("value")!);
        Assert.Equal("artifacts/local-feed", sources["mutate4csharp-local"]);
        Assert.Contains(sources, source => source.Value.Contains("nuget.org", StringComparison.OrdinalIgnoreCase));

        var mappings = configuration.Descendants("packageSourceMapping").Elements("packageSource")
            .ToDictionary(element => (string)element.Attribute("key")!,
                element => element.Elements("package").Select(package => (string)package.Attribute("pattern")!).ToArray());
        Assert.Equal(["mutate4csharp"], mappings["mutate4csharp-local"]);
        Assert.DoesNotContain("mutate4csharp", mappings.Single(mapping => mapping.Key != "mutate4csharp-local").Value);
    }

    [Fact]
    public void LocalCandidateWorkflowIsFailFastAndCarriesExactIdentityIntoLaterInvocations()
    {
        var readme = Read("README.md");
        var workflow = Read("docs/agent-workflow.md");
        var combined = string.Join('\n', readme, workflow);

        Assert.Contains("set -euo pipefail", combined, StringComparison.Ordinal);
        Assert.Contains("EXPECTED_TOOL_SOURCE_COMMIT", combined, StringComparison.Ordinal);
        Assert.Contains("complete-payload hashes", combined, StringComparison.Ordinal);
        Assert.DoesNotContain("pins version `0.1.0`", combined, StringComparison.Ordinal);
        Assert.Contains("EXPECTED_PACKAGE_SHA256", combined, StringComparison.Ordinal);
        Assert.Contains("EXPECTED_PAYLOAD_SHA256", combined, StringComparison.Ordinal);
        Assert.Contains("EXPECTED_TOOL_SOURCE_COMMIT", combined, StringComparison.Ordinal);
        Assert.Contains("receipt.tsv", combined, StringComparison.Ordinal);
        Assert.DoesNotContain("source \"$TOOL_RECEIPT\"", workflow, StringComparison.Ordinal);
        Assert.Contains("scripts/agent-gate.sh", workflow, StringComparison.Ordinal);
    }

    [Fact]
    public void ExamplePackageInventoryIsExplicitAndContainsEveryIntendedTrackedFile()
    {
        var project = Read("src/Mutate4CSharp/Mutate4CSharp.csproj");
        Assert.DoesNotContain("examples/agent-gate/**", project, StringComparison.Ordinal);

        foreach (var path in IntendedExampleFiles())
        {
            Assert.Contains($"../../{path}", project, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void PackagingTestSeedsHostileFilesOnlyInATemporaryRepositoryCopy()
    {
        var tests = Read("tests/Mutate4CSharp.Tests/ToolPackagingIntegrationTests.cs");
        Assert.Contains("hostile-intermediate.txt", tests, StringComparison.Ordinal);
        Assert.Contains("hostile-ignored.txt", tests, StringComparison.Ordinal);
        Assert.Contains("CopyTrackedRepository", tests, StringComparison.Ordinal);
        Assert.DoesNotContain("File.WriteAllText(control", tests, StringComparison.Ordinal);
        Assert.Contains("Assert.Equal(expectedExampleEntries, actualExampleEntries)", tests, StringComparison.Ordinal);
    }

    [Fact]
    public void ExampleFreezesDependenciesAndTheIntegrationBuildsProductionAndTests()
    {
        var exampleNuGet = Read("examples/agent-gate/NuGet.Config");
        Assert.Contains("<clear />", exampleNuGet, StringComparison.Ordinal);
        Assert.Contains("nuget.org", exampleNuGet, StringComparison.OrdinalIgnoreCase);

        var tests = Read("tests/Mutate4CSharp.Tests/ToolPackagingIntegrationTests.cs");
        Assert.Contains("tests/Example.Tests/Example.Tests.csproj", tests, StringComparison.Ordinal);
        Assert.Contains("exampleTestsRestore", tests, StringComparison.Ordinal);
        Assert.Contains("exampleTestsBuild", tests, StringComparison.Ordinal);
        Assert.Contains("dotnet test", Read("scripts/agent-gate.sh"), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AllowNotApplicableExitZeroIsClearlyFutureBehavior()
    {
        var workflow = Read("docs/agent-workflow.md");
        Assert.Contains("future validated `allowNotApplicable: true`", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("With validated\n  `allowNotApplicable: true`", workflow, StringComparison.Ordinal);
    }

    [Fact]
    public void LocalOnlyRestoreConfigurationStillClearsInheritedSources()
    {
        var localOnly = Read("NuGet.local.config");
        Assert.Contains("<clear />", localOnly, StringComparison.Ordinal);
        Assert.Contains("artifacts/local-feed", localOnly, StringComparison.Ordinal);
        Assert.DoesNotContain("nuget.org", localOnly, StringComparison.OrdinalIgnoreCase);
    }

    private static string[] IntendedExampleFiles() =>
    [
        "examples/agent-gate/NuGet.Config",
        "examples/agent-gate/.gitignore",
        "examples/agent-gate/mutate4csharp.json",
        "examples/agent-gate/src/Example/Example.csproj",
        "examples/agent-gate/src/Example/Flag.cs",
        "examples/agent-gate/src/Example/packages.lock.json",
        "examples/agent-gate/tests/Example.Tests/Example.Tests.csproj",
        "examples/agent-gate/tests/Example.Tests/FlagTests.cs",
        "examples/agent-gate/tests/Example.Tests/packages.lock.json"
    ];

    private static string Read(string relativePath) => File.ReadAllText(Path.Combine(RepositoryRoot, relativePath));
}
