namespace Mutate4CSharp.Tests;

public sealed class ProjectOwnershipTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "mutate4csharp-ownership", Guid.NewGuid().ToString("N"));

    public ProjectOwnershipTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public void ResolvesConfiguredProductionAndTestOwnershipDeterministically()
    {
        Write("src/App/App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        Write("tests/App.Tests/App.Tests.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        Write("mutate4csharp.json", """
            {
              "version": 1,
              "projects": [
                { "project": "src/App/App.csproj", "tests": ["tests/App.Tests/App.Tests.csproj"] }
              ]
            }
            """);

        var configuration = ScopeConfiguration.Load(_directory);
        var ownership = ProjectOwnershipResolver.Resolve(configuration, ["src/App/Feature.cs"]);

        var unit = Assert.Single(ownership.Units);
        Assert.Equal("src/App/App.csproj", unit.Project);
        Assert.Equal(["tests/App.Tests/App.Tests.csproj"], unit.Tests);
        Assert.Empty(ownership.Reasons);
    }

    [Fact]
    public void AmbiguousNestedOwnershipIsActionableInsteadOfArbitrary()
    {
        Write("src/App/App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        Write("src/App/Feature/Feature.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        Write("mutate4csharp.json", """
            {
              "version": 1,
              "projects": [
                { "project": "src/App/App.csproj", "tests": ["tests/App.Tests.csproj"] },
                { "project": "src/App/Feature/Feature.csproj", "tests": ["tests/Feature.Tests.csproj"] }
              ]
            }
            """);

        var configuration = ScopeConfiguration.Load(_directory);
        var ownership = ProjectOwnershipResolver.Resolve(configuration, ["src/App/Feature/A.cs"]);

        Assert.Empty(ownership.Units);
        var reason = Assert.Single(ownership.Reasons);
        Assert.Equal("AMBIGUOUS_PROJECT_OWNERSHIP", reason.Code);
        Assert.Contains("src/App/App.csproj", reason.Message, StringComparison.Ordinal);
        Assert.Contains("src/App/Feature/Feature.csproj", reason.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ExplicitSourceGlobsResolveLinkedFilesAndExposeOverlaps()
    {
        Write("src/A/A.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        Write("src/B/B.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        Write("mutate4csharp.json", """
            {
              "version": 1,
              "projects": [
                { "project": "src/A/A.csproj", "tests": ["tests/A.Tests.csproj"],
                  "sources": ["shared/**/*.cs"] },
                { "project": "src/B/B.csproj", "tests": ["tests/B.Tests.csproj"],
                  "sources": ["src/B/**/*.cs"] }
              ]
            }
            """);
        var configuration = ScopeConfiguration.Load(_directory);

        var linked = ProjectOwnershipResolver.Resolve(configuration, ["shared/models/Linked.cs"]);

        Assert.Equal("src/A/A.csproj", Assert.Single(linked.Units).Project);
        Assert.Empty(linked.Reasons);

        Write("mutate4csharp.json", """
            {
              "version": 1,
              "projects": [
                { "project": "src/A/A.csproj", "tests": ["tests/A.Tests.csproj"],
                  "sources": ["shared/**/*.cs"] },
                { "project": "src/B/B.csproj", "tests": ["tests/B.Tests.csproj"],
                  "sources": ["shared/**/*.cs"] }
              ]
            }
            """);
        configuration = ScopeConfiguration.Load(_directory);
        var overlap = ProjectOwnershipResolver.Resolve(configuration, ["shared/models/Linked.cs"]);
        Assert.Equal(2, overlap.Units.Count);
        Assert.Empty(overlap.Reasons);

        Write("mutate4csharp.json", """
            {
              "version": 1,
              "projects": [
                { "project": "src/A/A.csproj", "tests": ["tests/A.Tests.csproj"],
                  "sources": ["shared/**/*.cs"] },
                { "project": "src/B/B.csproj", "tests": ["tests/B.Tests.csproj"],
                  "sources": ["**/*.cs"] }
              ]
            }
            """);
        configuration = ScopeConfiguration.Load(_directory);
        var ambiguous = ProjectOwnershipResolver.Resolve(configuration, ["shared/models/Linked.cs"]);
        Assert.Equal("AMBIGUOUS_PROJECT_OWNERSHIP", Assert.Single(ambiguous.Reasons).Code);
    }

    [Fact]
    public async Task ExplicitInputsWorkOutsideGitAndExposeExclusionOverrides()
    {
        Write("src/Generated.g.cs", "class Generated { bool M() => true; }");

        var excluded = (await ScopePlanner.PlanExplicitAsync([Path.Combine(_directory, "src/Generated.g.cs")],
            _directory, CancellationToken.None)).Plan;
        Assert.Contains(excluded.Exclusions, item => item.Path == "src/Generated.g.cs" &&
            item.ReasonCode == "GENERATED_SOURCE");

        Write("mutate4csharp.json", "{ \"version\": 1, \"includeGenerated\": true, \"projects\": [] }");
        var included = (await ScopePlanner.PlanExplicitAsync([Path.Combine(_directory, "src/Generated.g.cs")],
            _directory, CancellationToken.None)).Plan;
        Assert.DoesNotContain(included.Exclusions, item => item.Path == "src/Generated.g.cs" && !item.Overridden);
        Assert.Contains(included.Exclusions, item => item.Path == "src/Generated.g.cs" && item.Overridden);
        Assert.Contains(included.Reasons, item => item.Code == "UNMAPPED_PROJECT");
    }

    private void Write(string relative, string content)
    {
        var path = Path.Combine(_directory, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    public void Dispose() { try { Directory.Delete(_directory, true); } catch { } }
}
