using System.Text;

namespace Mutate4CSharp.Tests;

public sealed class IssueFiveReviewRoundTwelveTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mutate4csharp-issue5-r12",
        Guid.NewGuid().ToString("N"));

    public IssueFiveReviewRoundTwelveTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void CrLfAliasesProjectOneExistingRunnablePathIntoScopeAndOwnership()
    {
        WriteProject("TESTS/App.Tests/App.Tests.csproj");
        var bytes = Encoding.UTF8.GetBytes(AliasedConfiguration.ReplaceLineEndings("\r\n"));

        var check = CheckConfiguration.Load(_root, bytes);
        Assert.Equal("TESTS/App.Tests/App.Tests.csproj", Assert.Single(check.ExecutionSuites).Path);

        var scope = ScopeConfiguration.Load(_root, bytes);
        var project = Assert.Single(scope.Projects);
        Assert.Equal(["TESTS/App.Tests/App.Tests.csproj"], project.Tests);
        var unit = Assert.Single(ProjectOwnershipResolver.Resolve(scope, ["src/App/A.cs"]).Units);
        Assert.Equal(["TESTS/App.Tests/App.Tests.csproj"], unit.Tests);
    }

    [Fact]
    public void CaseEquivalentAliasesForDistinctExistingProjectsFailClosedOnCaseSensitiveHosts()
    {
        if (OperatingSystem.IsWindows()) return;
        WriteProject("tests/App.Tests/App.Tests.csproj");
        WriteProject("TESTS/App.Tests/App.Tests.csproj");

        var error = Assert.Throws<CheckConfigurationException>(() => ScopeConfiguration.Load(_root,
            Encoding.UTF8.GetBytes(AliasedConfiguration)));

        Assert.Contains("distinct existing", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CaseEquivalentAliasesStillRefuseDifferentExecutionSettings()
    {
        WriteProject("tests/App.Tests/App.Tests.csproj");
        var conflict = AliasedConfiguration.Replace(
            "\"configuration\": \"Release\", \"expectedMembers\": [\"App.Tests.dll\"] }],",
            "\"configuration\": \"Debug\", \"expectedMembers\": [\"App.Tests.dll\"] }],",
            StringComparison.Ordinal);

        var error = Assert.Throws<CheckConfigurationException>(() => ScopeConfiguration.Load(_root,
            Encoding.UTF8.GetBytes(conflict)));

        Assert.Contains("conflict", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    private void WriteProject(string relativePath)
    {
        var path = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "<Project Sdk=\"Microsoft.NET.Sdk\" />\n");
    }

    private const string AliasedConfiguration = """
        {
          "version": 1,
          "projects": [{
            "id": "app", "project": "src/App/App.csproj", "targetFramework": "net10.0",
            "parseContext": "default", "sources": ["src/App/**/*.cs"],
            "testSuites": ["unit", "unit-alias"]
          }],
          "testSuites": [
            { "id": "unit", "path": "tests/App.Tests/App.Tests.csproj", "runner": "vstest",
              "framework": "net10.0", "configuration": "Release", "expectedMembers": ["App.Tests.dll"] },
            { "id": "unit-alias", "path": "TESTS/App.Tests/App.Tests.csproj", "runner": "vstest",
              "framework": "net10.0", "configuration": "Release", "expectedMembers": ["App.Tests.dll"] }],
          "policy": { "maxWorkers": 2 }
        }
        """;

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }
}
