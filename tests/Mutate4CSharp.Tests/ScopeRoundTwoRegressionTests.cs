using System.Text;

namespace Mutate4CSharp.Tests;

public sealed class ScopeRoundTwoRegressionTests : IDisposable
{
    private readonly SnapshotTestRepository _repository = new();

    [Theory]
    [InlineData(false, false, "GENERATED_SOURCE", false)]
    [InlineData(true, false, "TEST_SOURCE", false)]
    [InlineData(false, true, "GENERATED_SOURCE", false)]
    [InlineData(true, true, "TEST_SOURCE", true)]
    public async Task GeneratedTestOverlapPublishesOneTruthfulDisposition(bool includeGenerated,
        bool includeTests, string reasonCode, bool overridden)
    {
        WriteConfiguredProject("src/App/App.csproj", "tests/App.Tests/App.Tests.csproj");
        _repository.WriteText("tests/App.Tests/Properties/Resources.Designer.cs",
            "class Resources { int Value() => 1; }\n");
        WriteConfiguration("tests/App.Tests/App.Tests.csproj", includeGenerated, includeTests);

        var relative = "tests/App.Tests/Properties/Resources.Designer.cs";
        var plan = (await ScopePlanner.PlanExplicitAsync([Path.Combine(_repository.Root, relative)],
            _repository.Root, CancellationToken.None)).Plan;

        var disposition = Assert.Single(plan.Exclusions, item => item.Path == relative);
        Assert.Equal(reasonCode, disposition.ReasonCode);
        Assert.Equal(overridden, disposition.Overridden);
        if (!overridden)
            Assert.DoesNotContain(plan.Reasons, reason => reason.Code.EndsWith("_INCLUDED", StringComparison.Ordinal));
        var report = EvaluationReport.CreateSynthetic(EvaluationOutcome.Incomplete, "overlap", "TEST_FIXTURE")
            with { ScopePlan = plan, Selection = new("inputs", null, [relative]) };
        Assert.NotEmpty(ReportWriter.Serialize(report));
    }

    [Theory]
    [InlineData("App.sln", "src/App/Production.cs")]
    [InlineData("App.Tests.csproj", "src/App/Production.cs")]
    [InlineData("src/App.sln", "src/App/Production.cs")]
    public async Task BroadOrRootTestEntriesDoNotClassifyProductionSources(string testEntry,
        string productionSource)
    {
        WriteConfiguredProject("src/App/App.csproj", testEntry);
        _repository.WriteText(productionSource, "class Production { int Value() => 1; }\n");
        WriteConfiguration(testEntry);

        var plan = (await ScopePlanner.PlanExplicitAsync([Path.Combine(_repository.Root, productionSource)],
            _repository.Root, CancellationToken.None)).Plan;

        Assert.DoesNotContain(plan.Exclusions, item => item.Path == productionSource &&
            item.ReasonCode == "TEST_SOURCE");
        Assert.Contains(plan.Files, file => file.Path == productionSource);
        Assert.Contains(plan.ProjectUnits, unit => unit.Project == "src/App/App.csproj");
    }

    [Theory]
    [InlineData("Contests.cs")]
    [InlineData("Protests.cs")]
    [InlineData("Attests.cs")]
    public async Task OrdinaryProductionNamesEndingInTestsAreNotTestSources(string fileName)
    {
        WriteConfiguredProject("src/App/App.csproj", "tests/App.Tests/App.Tests.csproj");
        var relative = $"src/App/{fileName}";
        _repository.WriteText(relative, "class Production { int Value() => 1; }\n");
        WriteConfiguration("tests/App.Tests/App.Tests.csproj");

        var plan = (await ScopePlanner.PlanExplicitAsync([Path.Combine(_repository.Root, relative)],
            _repository.Root, CancellationToken.None)).Plan;

        Assert.DoesNotContain(plan.Exclusions, item => item.Path == relative &&
            item.ReasonCode == "TEST_SOURCE");
        Assert.Contains(plan.ProjectUnits, unit => unit.Project == "src/App/App.csproj");
    }

    [Fact]
    public async Task ExactDotTestsFileConventionRemainsVisible()
    {
        WriteConfiguredProject("src/App/App.csproj", "tests/App.Tests/App.Tests.csproj");
        const string relative = "src/App/Widget.Tests.cs";
        _repository.WriteText(relative, "class WidgetTests { }\n");
        WriteConfiguration("tests/App.Tests/App.Tests.csproj");

        var plan = (await ScopePlanner.PlanExplicitAsync([Path.Combine(_repository.Root, relative)],
            _repository.Root, CancellationToken.None)).Plan;

        Assert.Contains(plan.Exclusions, item => item.Path == relative && item.ReasonCode == "TEST_SOURCE");
    }

    [Fact]
    public async Task ChangedConfiguredTestSourceExpandsItsProductionProject()
    {
        PrepareGitRepositoryWithTestSource();
        _repository.WriteText("tests/App.Tests/Behavior.cs", "class Behavior { int Value() => 2; }\n");

        var plan = await PlanGitAsync();

        AssertTestExpansion(plan, "tests/App.Tests/Behavior.cs");
    }

    [Fact]
    public async Task DeletedConfiguredTestSourceExpandsItsProductionProject()
    {
        PrepareGitRepositoryWithTestSource();
        File.Delete(Path.Combine(_repository.Root, "tests/App.Tests/Behavior.cs"));

        var plan = await PlanGitAsync();

        AssertTestExpansion(plan, "tests/App.Tests/Behavior.cs");
    }

    [Fact]
    public async Task RenamedConfiguredTestSourceExpandsItsProductionProjectDeterministically()
    {
        PrepareGitRepositoryWithTestSource();
        _repository.Git("mv", "tests/App.Tests/Behavior.cs", "tests/App.Tests/RenamedBehavior.cs");

        var first = await PlanGitAsync();
        var second = await PlanGitAsync();

        AssertTestExpansion(first, "tests/App.Tests/RenamedBehavior.cs");
        Assert.Equal(first.Exclusions, second.Exclusions);
        Assert.Equal(first.ProjectUnits.Select(UnitValue), second.ProjectUnits.Select(UnitValue));
        Assert.Equal(first.Reasons, second.Reasons);

        static string UnitValue(ProjectScopeUnit unit) =>
            $"{unit.Project}|{string.Join(',', unit.Tests)}|{string.Join(',', unit.Inputs)}|{unit.Expansion}";
    }

    [Fact]
    public async Task UnmappedChangedTestSourceBlocksInsteadOfClaimingNoApplicableChanges()
    {
        _repository.WriteText("tests/Loose.cs", "class Loose { int Value() => 1; }\n");
        _repository.Git("add", ".");
        _repository.Git("commit", "-m", "base");
        _repository.WriteText("tests/Loose.cs", "class Loose { int Value() => 2; }\n");

        var plan = await PlanGitAsync();

        Assert.Contains(plan.Exclusions, item => item.Path == "tests/Loose.cs" &&
            item.ReasonCode == "TEST_SOURCE");
        Assert.Contains(plan.Reasons, reason => reason.Code == "UNMAPPED_PROJECT");
        Assert.DoesNotContain(plan.Reasons, reason => reason.Code == "NO_APPLICABLE_PRODUCTION_CHANGES");
        Assert.False(plan.IsComplete);
    }

    [Fact]
    public async Task LongDuplicateDeclarationIdsRemainUniqueBoundedAndPublishable()
    {
        WriteConfiguredProject("src/App/App.csproj", "tests/App.Tests/App.Tests.csproj");
        WriteConfiguration("tests/App.Tests/App.Tests.csproj");
        var type = "VeryLongParameterType" + new string('X', 540);
        var source = $$"""
            interface IFoo { int M({{type}} value); }
            interface IBar { int M({{type}} value); }
            class Subject : IFoo, IBar
            {
                int IFoo.M({{type}} value) => 1;
                int IBar.M({{type}} value) => 2;
            }
            """;
        const string relative = "src/App/Subject.cs";
        _repository.WriteText(relative, source);

        var plan = (await ScopePlanner.PlanExplicitAsync([Path.Combine(_repository.Root, relative)],
            _repository.Root, CancellationToken.None)).Plan;

        var declarations = Assert.Single(plan.Files).Declarations;
        Assert.Equal(declarations.Count, declarations.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.All(declarations, declaration => Assert.True(declaration.Id.Length <= 512, declaration.Id));
        var report = EvaluationReport.CreateSynthetic(EvaluationOutcome.Incomplete, "long-duplicates", "TEST_FIXTURE")
            with { ScopePlan = plan, Selection = new("inputs", null, [relative]) };
        Assert.NotEmpty(ReportWriter.Serialize(report));
    }

    private void PrepareGitRepositoryWithTestSource()
    {
        WriteConfiguredProject("src/App/App.csproj", "tests/App.Tests/App.Tests.csproj");
        _repository.WriteText("src/App/Production.cs", "class Production { int Value() => 1; }\n");
        _repository.WriteText("tests/App.Tests/Behavior.cs", "class Behavior { int Value() => 1; }\n");
        WriteConfiguration("tests/App.Tests/App.Tests.csproj");
        _repository.Git("add", ".");
        _repository.Git("commit", "-m", "base");
    }

    private async Task<ScopePlan> PlanGitAsync()
    {
        await using var snapshot = await SnapshotCapture.CaptureAsync(_repository.Root, "HEAD", [],
            SnapshotCaptureOptions.Default, CancellationToken.None);
        return await ScopePlanner.PlanGitAsync(snapshot, CancellationToken.None);
    }

    private static void AssertTestExpansion(ScopePlan plan, string path)
    {
        Assert.Contains(plan.Exclusions, item => item.Path == path && item.ReasonCode == "TEST_SOURCE");
        Assert.Contains(plan.ProjectUnits, unit => unit.Project == "src/App/App.csproj" &&
            unit.Expansion == "FULL_PROJECT");
        Assert.Contains(plan.Reasons, reason => reason.Code == "NON_PRODUCTION_CHANGE_EXPANSION");
        Assert.DoesNotContain(plan.Reasons, reason => reason.Code == "NO_APPLICABLE_PRODUCTION_CHANGES");
    }

    private void WriteConfiguredProject(string project, string test)
    {
        _repository.WriteText(project, "<Project Sdk=\"Microsoft.NET.Sdk\" />\n");
        _repository.WriteText(test, "<Project Sdk=\"Microsoft.NET.Sdk\" />\n");
    }

    private void WriteConfiguration(string test, bool includeGenerated = false, bool includeTests = false) =>
        _repository.WriteText("mutate4csharp.json", $$"""
            { "version": 1, "includeGenerated": {{includeGenerated.ToString().ToLowerInvariant()}},
              "includeTests": {{includeTests.ToString().ToLowerInvariant()}}, "projects": [
              { "project": "src/App/App.csproj", "tests": ["{{test}}"] }
            ] }
            """);

    public void Dispose() => _repository.Dispose();
}
