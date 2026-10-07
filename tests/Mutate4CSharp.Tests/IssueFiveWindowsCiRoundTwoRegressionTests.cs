using System.Reflection;
using System.Text;

namespace Mutate4CSharp.Tests;

public sealed class IssueFiveWindowsCiRoundTwoRegressionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mutate4csharp-issue5-windows-r2",
        Guid.NewGuid().ToString("N"));

    public IssueFiveWindowsCiRoundTwoRegressionTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void CaseEquivalentSuitePathsCollapseBeforeProjectMappingsAreValidated()
    {
        var configuration = CheckConfiguration.Load(_root, Encoding.UTF8.GetBytes(AliasedConfiguration));

        var execution = Assert.Single(configuration.ExecutionSuites);
        Assert.Equal(["unit", "unit-alias"], execution.Aliases);

        var conflict = AliasedConfiguration.Replace(
            "\"configuration\": \"Release\", \"expectedMembers\": [\"App.Tests.dll\"] }],",
            "\"configuration\": \"Debug\", \"expectedMembers\": [\"App.Tests.dll\"] }],",
            StringComparison.Ordinal);
        var error = Assert.Throws<ArgumentException>(() =>
            CheckConfiguration.Load(_root, Encoding.UTF8.GetBytes(conflict)));
        Assert.Contains("conflict", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SuiteIdentityUsesWindowsEquivalentSlashBackslashAndCaseSemantics()
    {
        var method = typeof(CheckConfiguration).GetMethod("SuiteIdentity",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        var forward = new CheckTestSuite("unit", "tests/App.Tests/App.Tests.csproj", "vstest", "net10.0",
            "Release", ["App.Tests.dll"]);
        var windowsEquivalent = forward with { Id = "unit-alias", Path = "TESTS\\APP.TESTS\\APP.TESTS.CSPROJ" };
        var conflict = windowsEquivalent with { Configuration = "Debug" };

        var identity = Assert.IsType<string>(method!.Invoke(null, [forward]));
        Assert.Equal(identity, Assert.IsType<string>(method.Invoke(null, [windowsEquivalent])));
        Assert.NotEqual(identity, Assert.IsType<string>(method.Invoke(null, [conflict])));
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
