using System.Text.Json;
using System.Xml.Linq;

namespace Mutate4CSharp.Tests;

public sealed class IssueEightReviewRoundFiveTests
{
    private static readonly string RepositoryRoot = Path.GetFullPath("../../../../../", AppContext.BaseDirectory);

    [Fact]
    public void ShippedExampleMapsTheToolOnlyToItsLocalFeed()
    {
        var configuration = XDocument.Load(Path.Combine(RepositoryRoot, "examples", "agent-gate", "NuGet.Config"));
        var sources = configuration.Descendants("packageSources").Elements("add")
            .ToDictionary(element => (string)element.Attribute("key")!, element => (string)element.Attribute("value")!);
        Assert.Equal("../mutate4csharp-local-feed", sources["mutate4csharp-local"]);
        Assert.Contains(sources, source => source.Value.Contains("nuget.org", StringComparison.OrdinalIgnoreCase));

        var mappings = configuration.Descendants("packageSourceMapping").Elements("packageSource")
            .ToDictionary(element => (string)element.Attribute("key")!,
                element => element.Elements("package").Select(package => (string)package.Attribute("pattern")!).ToArray());
        Assert.Equal(["mutate4csharp"], mappings["mutate4csharp-local"]);
        Assert.DoesNotContain("*", mappings["nuget.org"]);
    }

    [Fact]
    public void DocumentedWorkflowUsesExternalReceiptsAndReverifiesFailClosedIdentity()
    {
        var workflow = Read("docs/agent-workflow.md");
        Assert.Contains("set -euo pipefail", workflow, StringComparison.Ordinal);
        Assert.Contains("git status --porcelain=v1 --untracked-files=all", workflow,
            StringComparison.Ordinal);
        Assert.Contains("git rev-parse HEAD", workflow, StringComparison.Ordinal);
        Assert.Contains("complete payload SHA-256", workflow, StringComparison.Ordinal);
        Assert.Contains("TOOL_PARENT=$(mktemp -d)", workflow, StringComparison.Ordinal);
        Assert.Contains("TOOL_WORKSPACE=\"$TOOL_PARENT/workspace\"", workflow, StringComparison.Ordinal);
        Assert.Contains("EXPECTED_PACKAGE_SHA256", workflow, StringComparison.Ordinal);
        Assert.Contains("EXPECTED_PAYLOAD_SHA256", workflow, StringComparison.Ordinal);
        Assert.Contains("EXPECTED_TOOL_SOURCE_COMMIT", workflow, StringComparison.Ordinal);
        Assert.Contains("scripts/agent-gate.sh", workflow, StringComparison.Ordinal);
        Assert.Contains("REPORT_PARENT=$(mktemp -d)", workflow, StringComparison.Ordinal);
        Assert.Contains("MUTATE4CSHARP_REPORT=\"$REPORT_PARENT/report.json\"", workflow,
            StringComparison.Ordinal);
        Assert.DoesNotContain("source artifacts/local-tool.env", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("source \"$TOOL_RECEIPT\"", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("test \"$(git rev-parse HEAD)\" = \"$MUTATE4CSHARP_SOURCE_COMMIT\"", workflow,
            StringComparison.Ordinal);
    }

    [Fact]
    public void PackageInputsAreExplicitTrackedFilesWithoutDocumentationWildcards()
    {
        var project = Read("src/Mutate4CSharp/Mutate4CSharp.csproj");
        Assert.DoesNotContain("docs/*.md", project, StringComparison.Ordinal);
        Assert.DoesNotContain("docs/contracts/*.json", project, StringComparison.Ordinal);

        var trackedPackageContent = GitLines("ls-files", "README.md", "ATTRIBUTION.md", "NuGet.Config",
            "NuGet.local.config", "docs", "examples/agent-gate", "scripts");
        foreach (var path in trackedPackageContent)
            Assert.Contains($"../../{path}", project, StringComparison.Ordinal);
    }

    [Fact]
    public void ExampleDependenciesAreLockedAndContributorImpactIsDocumented()
    {
        var project = Read("examples/agent-gate/tests/Example.Tests/Example.Tests.csproj");
        Assert.Contains("<RestorePackagesWithLockFile>true</RestorePackagesWithLockFile>", project,
            StringComparison.Ordinal);
        var lockPath = Path.Combine(RepositoryRoot, "examples", "agent-gate", "tests", "Example.Tests",
            "packages.lock.json");
        Assert.True(File.Exists(lockPath), $"Missing tracked example dependency lock: {lockPath}");
        using var lockFile = JsonDocument.Parse(File.ReadAllBytes(lockPath));
        Assert.Equal(1, lockFile.RootElement.GetProperty("version").GetInt32());

        var workflow = Read("docs/agent-workflow.md");
        Assert.Contains("contributor", workflow, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("user and machine package sources", workflow, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("only after extraction", workflow, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("multiple applicable NuGet.Config", workflow, StringComparison.Ordinal);
    }

    private static string Read(string relativePath) => File.ReadAllText(Path.Combine(RepositoryRoot, relativePath));

    private static string[] GitLines(params string[] arguments)
    {
        var start = new System.Diagnostics.ProcessStartInfo("git")
        {
            WorkingDirectory = RepositoryRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = System.Diagnostics.Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, error);
        return output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
}
