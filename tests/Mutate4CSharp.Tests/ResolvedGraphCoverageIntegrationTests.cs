using System.Xml.Linq;

namespace Mutate4CSharp.Tests;

// Actual selected401 compiler, collector and tests. Requires the same explicit
// owned SDK/logger deployment as the other resolved-graph behavioral fixtures.
public sealed partial class ResolvedGraphInheritedCompilerFixtureTests
{
    [Fact(Timeout = 1_100_000)]
    public async Task Actual_resolved_graph_baseline_retains_real_covered_modules()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "test-data", "resolved-graph"))) directory = directory.Parent;
        Assert.NotNull(directory);
        var fixture = Path.Combine(directory.FullName, "test-data", "resolved-graph");
        foreach (var path in Directory.EnumerateFiles(fixture, "*", SearchOption.AllDirectories))
            _repository.Write(Path.GetRelativePath(fixture, path), File.ReadAllBytes(path));
        // -getProperty without an explicit target only evaluates, even with the
        // dotnet build verb. Require the genuine baseline clone to execute Build,
        // independently of outputs from the earlier graph-capture clone.
        const string projectPath = "tests/Production.Tests/Production.Tests.csproj";
        const string markerRelative = "tests/Production.Tests/obj/actual-coverage-build.control";
        var authoredProject = XDocument.Parse(System.Text.Encoding.UTF8.GetString(_repository.Read(projectPath)));
        authoredProject.Root!.Add(new XElement("Target", new XAttribute("Name", "ObserveActualCoverageBuild"),
            new XAttribute("BeforeTargets", "Build"), new XElement("WriteLinesToFile",
                new XAttribute("File", "$(MSBuildProjectDirectory)/obj/actual-coverage-build.control"),
                new XAttribute("Lines", "actual coverage Build target executed"), new XAttribute("Overwrite", "true"))));
        _repository.WriteText(projectPath, authoredProject.ToString());
        _repository.Git("add", "."); _repository.Git("commit", "-m", "actual owned graph coverage fixture");
        var token = TestContext.Current.CancellationToken;
        var original = new ResolvedGraphBudget(TimeSpan.FromSeconds(1100), token);
        await using var snapshot = await SnapshotCapture.CaptureAsync(_repository.Root, "HEAD", [], SnapshotCaptureOptions.Default, token);
        using var inputs = new ResolvedGraphInputs(original);
        var deployment = ResolvedGraphDeployment.Load(inputs);
        var configuration = CheckConfiguration.Load(_repository.Root, _repository.Read("mutate4csharp.json"));
        await using var environment = await ExecutionEnvironment.PrepareDependenciesAsync(snapshot,
            ["src/Production/Production.csproj", "tests/Production.Tests/Production.Tests.csproj"],
            DependencyPreparationOptions.Default with { Timeout = original.RemainingWork }, token, original);
        await using var capture = await ResolvedGraphCapture.CreateAsync(snapshot, configuration, environment,
            original.RemainingWork, token, deployment.Logger, original);
        Assert.Equal(3, capture.Projects.Count);
        Assert.False(File.Exists(Path.Combine(snapshot.CaptureRoot, markerRelative)));
        Assert.All(capture.Projects, project => Assert.True(project.FirstCsc!.Compiled!.PathMap.Values.All(value =>
            value is ResolvedGraphPathProfile.Source or ResolvedGraphPathProfile.Packages)));
        var actualBaselineBuildObserved = false;
        await using var baseline = await new VstestSuiteExecutor(snapshot, environment, beforeCoverageParsing: (worker, _) =>
            {
                Assert.NotEqual(capture.Root, worker.Root);
                Assert.Equal("actual coverage Build target executed", File.ReadAllText(Path.Combine(worker.Root, markerRelative)).Trim());
                actualBaselineBuildObserved = true;
            }, resolvedGraph: true, originalBudget: original)
            .RunBaselineAsync(Assert.Single(configuration.ExecutionSuites), original.RemainingWork, token);
        Assert.True(baseline.Disposition == SuiteRunDisposition.Passed, string.Join('\n', baseline.Diagnostics));
        Assert.True(actualBaselineBuildObserved);
        Assert.NotEmpty(baseline.CoverageReports);
        var modules = baseline.CoverageReports.SelectMany(path => XDocument.Load(path).Descendants()
            .Where(node => node.Name.LocalName == "ModuleName").Select(node => node.Value)).ToArray();
        Assert.Contains("Production", modules); Assert.Contains("Dependency", modules);
        Assert.NotNull(baseline.CoverageMap);
        Assert.Equal(CoverageState.Covered, baseline.CoverageMap.GetState("src/Production/Subject.cs", 5));
        Assert.Equal(CoverageState.Covered, baseline.CoverageMap.GetState("src/Production/ReleaseOnly.cs", 3));
        Assert.True(baseline.HealthyControl);
        capture.ValidateAuthoredInputs(); await snapshot.ValidateOriginalAsync(token);
        original.CheckWork(); Assert.Null(original.Observe().Cleanup);
    }
}
