namespace Mutate4CSharp.Tests;

public sealed class IssueFiveReviewRoundThirteenTests
{
    [Fact]
    public void WindowsEquivalentAliasesPublishStableOrdinalSpellingWhenInputIsAliasIdOrdered()
    {
        CheckTestSuite[] aliases =
        [
            Suite("unit", "tests/App.Tests/App.Tests.csproj"),
            Suite("unit-alias", "TESTS/App.Tests/App.Tests.csproj")
        ];

        var selected = CheckConfiguration.SelectRunnableSuitePath(aliases,
            aliases.Select(alias => alias.Path).ToArray(), StringComparer.OrdinalIgnoreCase);

        Assert.Equal("TESTS/App.Tests/App.Tests.csproj", selected);
    }

    [Fact]
    public void CaseSensitiveDistinctExistingAliasPathsRemainAmbiguous()
    {
        CheckTestSuite[] aliases =
        [
            Suite("unit", "tests/App.Tests/App.Tests.csproj"),
            Suite("unit-alias", "TESTS/App.Tests/App.Tests.csproj")
        ];

        var error = Assert.Throws<ArgumentException>(() => CheckConfiguration.SelectRunnableSuitePath(aliases,
            aliases.Select(alias => alias.Path).ToArray(), StringComparer.Ordinal));

        Assert.Contains("distinct existing paths", error.Message, StringComparison.Ordinal);
    }

    private static CheckTestSuite Suite(string id, string path) =>
        new(id, path, "vstest", "net10.0", "Release", ["App.Tests.dll"]);
}
