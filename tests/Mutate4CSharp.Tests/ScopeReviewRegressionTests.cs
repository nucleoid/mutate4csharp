using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;

namespace Mutate4CSharp.Tests;

public sealed class ScopeReviewRegressionTests : IDisposable
{
    private readonly SnapshotTestRepository _repository = new();

    [Theory]
    [InlineData("\"a b\"", "\"a  b\"")]
    [InlineData("' '", "'\\t'")]
    [InlineData("@\"a b\"", "@\"a  b\"")]
    public void LiteralWhitespaceChangesRemainBehaviorVisible(string beforeLiteral, string afterLiteral)
    {
        var before = Encoding.UTF8.GetBytes($"class A {{ object M() => {beforeLiteral}; }}");
        var after = Encoding.UTF8.GetBytes($"class A {{ object M() => {afterLiteral}; }}");

        var comparison = DeclarationCatalog.Compare("src/A.cs", before, after);

        Assert.Contains(comparison.Declarations, declaration => declaration.DisplayName == "method M");
    }

    [Theory]
    [InlineData("enum Status { Ready, Done }")]
    [InlineData("interface IMarker { }")]
    [InlineData("record Point(int X, int Y);")]
    [InlineData("global using System.Text;")]
    [InlineData("[assembly: System.Reflection.AssemblyTitle(\"A\")]")]
    public void AddedFilesWithoutExecutableDeclarationsRequireConservativeExpansion(string source)
    {
        var comparison = DeclarationCatalog.Compare("src/New.cs", null, Encoding.UTF8.GetBytes(source));

        Assert.True(comparison.RequiresProjectExpansion);
        Assert.Contains(comparison.Reasons, reason => reason.Code == "COMPILE_ITEM_ADDED");
    }

    [Fact]
    public async Task GitComparisonHonorsConfiguredCrLfNormalization()
    {
        _repository.WriteText(".gitattributes", "*.cs text eol=crlf\n");
        _repository.WriteText("src/A.cs", "class A { }\n");
        _repository.Git("add", ".");
        _repository.Git("commit", "-m", "base");
        File.Delete(Path.Combine(_repository.Root, "src/A.cs"));
        _repository.Git("checkout", "--", "src/A.cs");
        Assert.Contains((byte)'\r', _repository.Read("src/A.cs"));

        await using var snapshot = await SnapshotCapture.CaptureAsync(_repository.Root, "HEAD", [],
            SnapshotCaptureOptions.Default, CancellationToken.None);
        var changes = await GitChangePlanner.DiscoverAsync(snapshot, CancellationToken.None);

        Assert.Empty(changes.Files);
    }

    [Fact]
    public async Task GitComparisonSanitizesCallerGitEnvironment()
    {
        _repository.WriteText("src/A.cs", "class A { }\n");
        _repository.Git("add", ".");
        _repository.Git("commit", "-m", "base");
        await using var snapshot = await SnapshotCapture.CaptureAsync(_repository.Root, "HEAD", [],
            SnapshotCaptureOptions.Default, CancellationToken.None);
        var prior = Environment.GetEnvironmentVariable("GIT_DIR");
        Environment.SetEnvironmentVariable("GIT_DIR", Path.Combine(_repository.Root, "missing.git"));
        try
        {
            var changes = await GitChangePlanner.DiscoverAsync(snapshot, CancellationToken.None);
            Assert.Empty(changes.Files);
        }
        finally { Environment.SetEnvironmentVariable("GIT_DIR", prior); }
    }

    [Fact]
    [SupportedOSPlatform("linux")]
    public async Task GitComparisonUsesCapturedTimeoutAndTerminatesTheProcessTree()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "POSIX shell timeout fixture; Windows is verified by CI.");
        _repository.WriteText("src/A.cs", "class A { }\n");
        var wrapper = Path.Combine(_repository.Root, "git-wrapper.sh");
        _repository.WriteText("git-wrapper.sh", "#!/bin/sh\nif [ \"$1\" = \"ls-tree\" ]; then sleep 5; fi\nexec git \"$@\"\n");
        File.SetUnixFileMode(wrapper, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        _repository.Git("add", ".");
        _repository.Git("commit", "-m", "base");
        var options = SnapshotCaptureOptions.Default with
            { GitExecutable = wrapper, GitTimeout = TimeSpan.FromMilliseconds(250) };
        await using var snapshot = await SnapshotCapture.CaptureAsync(_repository.Root, "HEAD", [], options,
            CancellationToken.None);
        var timer = Stopwatch.StartNew();

        var error = await Assert.ThrowsAsync<SnapshotCaptureException>(() =>
            GitChangePlanner.DiscoverAsync(snapshot, CancellationToken.None));

        Assert.Contains("timeout", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(3), timer.Elapsed.ToString());
    }

    [Fact]
    [SupportedOSPlatform("linux")]
    public async Task GitComparisonUsesCapturedOutputLimitWithoutDeadlock()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "POSIX shell output-limit fixture; Windows is verified by CI.");
        _repository.WriteText("src/A.cs", "class A { }\n");
        var wrapper = Path.Combine(_repository.Root, "git-wrapper.sh");
        _repository.WriteText("git-wrapper.sh", "#!/bin/sh\nif [ \"$1\" = \"ls-tree\" ]; then head -c 4096 /dev/zero; exit 0; fi\nexec git \"$@\"\n");
        File.SetUnixFileMode(wrapper, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        _repository.Git("add", ".");
        _repository.Git("commit", "-m", "base");
        var options = SnapshotCaptureOptions.Default with
            { GitExecutable = wrapper, MaxGitOutputBytes = 512 };
        await using var snapshot = await SnapshotCapture.CaptureAsync(_repository.Root, "HEAD", [], options,
            CancellationToken.None);

        var error = await Assert.ThrowsAsync<SnapshotLimitException>(() =>
            GitChangePlanner.DiscoverAsync(snapshot, CancellationToken.None));

        Assert.Contains("Git output exceeded", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void LongTopLevelStatementsUseBoundedStableIds()
    {
        var statement = $"Console.WriteLine(\"{new string('x', 900)}\");";
        var comparison = DeclarationCatalog.Compare("Program.cs", null, Encoding.UTF8.GetBytes(statement));

        var declaration = Assert.Single(comparison.Declarations);
        Assert.True(declaration.Id.Length <= 512, declaration.Id);
        Assert.DoesNotContain(new string('x', 100), declaration.Id, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LongScopeMessagesRemainPublishableWithinSchemaBounds()
    {
        WriteConfiguredProject("src/App/App.csproj", "tests/App.Tests/App.Tests.csproj");
        var segments = Enumerable.Range(0, 9).Select(index => $"d{index}-{new string('x', 116)}");
        var relative = "src/App/" + string.Join('/', segments) + "/Added.cs";
        _repository.WriteText(relative, "enum Added { Value }\n");
        _repository.WriteText("mutate4csharp.json", """
            { "version": 1, "projects": [
              { "project": "src/App/App.csproj", "tests": ["tests/App.Tests/App.Tests.csproj"] }
            ] }
            """);

        var plan = (await ScopePlanner.PlanExplicitAsync([Path.Combine(_repository.Root,
            relative.Replace('/', Path.DirectorySeparatorChar))], _repository.Root, CancellationToken.None)).Plan;
        var report = EvaluationReport.CreateSynthetic(EvaluationOutcome.Incomplete, "bounded-scope", "TEST_FIXTURE")
            with { ScopePlan = plan, Selection = new("inputs", null, [relative]) };

        Assert.All(plan.Files.SelectMany(file => file.Reasons), reason => Assert.True(reason.Message.Length <= 1024));
        Assert.NotEmpty(ReportWriter.Serialize(report));
    }

    [Fact]
    public async Task EveryUnsupportedChangedPathIsVisibleAndBlocking()
    {
        await PrepareConfiguredRepositoryAsync();
        _repository.WriteText("src/App/View.razor", "<p>changed</p>\n");

        await using var snapshot = await SnapshotCapture.CaptureAsync(_repository.Root, "HEAD", [],
            SnapshotCaptureOptions.Default, CancellationToken.None);
        var plan = await ScopePlanner.PlanGitAsync(snapshot, CancellationToken.None);

        Assert.Contains(plan.Exclusions, exclusion => exclusion.Path == "src/App/View.razor" &&
            exclusion.ReasonCode == "UNSUPPORTED_CHANGED_INPUT");
        Assert.False(plan.IsComplete);
        Assert.DoesNotContain(plan.Reasons, reason => reason.Code == "NO_APPLICABLE_PRODUCTION_CHANGES");
    }

    [Fact]
    public async Task UnmappedProjectChangesAreBlocking()
    {
        _repository.WriteText("src/App/App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />\n");
        _repository.Git("add", ".");
        _repository.Git("commit", "-m", "base");
        _repository.WriteText("src/App/App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup /></Project>\n");

        await using var snapshot = await SnapshotCapture.CaptureAsync(_repository.Root, "HEAD", [],
            SnapshotCaptureOptions.Default, CancellationToken.None);
        var plan = await ScopePlanner.PlanGitAsync(snapshot, CancellationToken.None);

        Assert.Contains(plan.Reasons, reason => reason.Code == "UNMAPPED_PROJECT");
        Assert.False(plan.IsComplete);
    }

    [Fact]
    public async Task ExactCrossProjectRenameScopesBothRemovalAndAdditionOwners()
    {
        WriteConfiguredProject("src/A/A.csproj", "tests/A.Tests/A.Tests.csproj");
        WriteConfiguredProject("src/B/B.csproj", "tests/B.Tests/B.Tests.csproj");
        _repository.WriteText("src/A/Moved.cs", "class Moved { int M() => 1; }\n");
        _repository.WriteText("mutate4csharp.json", """
            { "version": 1, "projects": [
              { "project": "src/A/A.csproj", "tests": ["tests/A.Tests/A.Tests.csproj"] },
              { "project": "src/B/B.csproj", "tests": ["tests/B.Tests/B.Tests.csproj"] }
            ] }
            """);
        _repository.Git("add", ".");
        _repository.Git("commit", "-m", "base");
        _repository.Git("mv", "src/A/Moved.cs", "src/B/Moved.cs");

        await using var snapshot = await SnapshotCapture.CaptureAsync(_repository.Root, "HEAD", [],
            SnapshotCaptureOptions.Default, CancellationToken.None);
        var changes = await GitChangePlanner.DiscoverAsync(snapshot, CancellationToken.None);
        Assert.Contains(changes.Files, change => change.Kind == ChangeKind.Renamed &&
            change.BasePath == "src/A/Moved.cs" && change.CurrentPath == "src/B/Moved.cs");
        var plan = await ScopePlanner.PlanGitAsync(snapshot, CancellationToken.None);

        Assert.Contains(plan.ProjectUnits, unit => unit.Project == "src/A/A.csproj" && unit.Expansion == "FULL_PROJECT");
        Assert.Contains(plan.ProjectUnits, unit => unit.Project == "src/B/B.csproj" && unit.Expansion == "FULL_PROJECT");
        Assert.Contains(plan.Reasons, reason => reason.Code == "COMPILE_ITEM_REMOVED");
    }

    [Fact]
    public async Task CapturedExplicitInputsRejectNonCSharpAndDeduplicateEquivalentPaths()
    {
        _repository.WriteText("src/A.cs", "class A { }\n");
        _repository.WriteText("README.md", "text\n");
        _repository.Git("add", ".");
        _repository.Git("commit", "-m", "base");
        var source = Path.Combine(_repository.Root, "src/A.cs");
        var readme = Path.Combine(_repository.Root, "README.md");
        await using var snapshot = await SnapshotCapture.CaptureAsync(_repository.Root, "HEAD", [source, readme],
            SnapshotCaptureOptions.Default, CancellationToken.None);

        Assert.Throws<ArgumentException>(() => ScopePlanner.PlanCapturedExplicit(snapshot, [readme]));
        var plan = ScopePlanner.PlanCapturedExplicit(snapshot, [source, Path.Combine(_repository.Root, "src", ".", "A.cs")]);
        Assert.Single(plan.Files);
    }

    [Fact]
    public async Task GeneratedHeaderMustBeLeadingTriviaAndOverridesAreReportedTruthfully()
    {
        _repository.WriteText("src/Literal.cs", "class Literal { const string Header = \"<auto-generated\"; }\n");
        var literal = (await ScopePlanner.PlanExplicitAsync([Path.Combine(_repository.Root, "src/Literal.cs")],
            _repository.Root, CancellationToken.None)).Plan;
        Assert.DoesNotContain(literal.Exclusions, item => item.ReasonCode == "GENERATED_SOURCE");

        _repository.WriteText("src/Generated.g.cs", "class Generated { }\n");
        _repository.WriteText("mutate4csharp.json", "{ \"version\": 1, \"includeGenerated\": true, \"projects\": [] }");
        var overridden = (await ScopePlanner.PlanExplicitAsync([Path.Combine(_repository.Root, "src/Generated.g.cs")],
            _repository.Root, CancellationToken.None)).Plan;
        Assert.Contains(overridden.Exclusions, item => item.Path == "src/Generated.g.cs" && item.Overridden);
    }

    [Fact]
    public async Task ConfiguredTestProjectRootsClassifyHelperSourcesAsTests()
    {
        WriteConfiguredProject("src/App/App.csproj", "src/App.Specs/App.Specs.csproj");
        _repository.WriteText("src/App.Specs/Helper.cs", "class Helper { }\n");
        _repository.WriteText("mutate4csharp.json", """
            { "version": 1, "projects": [
              { "project": "src/App/App.csproj", "tests": ["src/App.Specs/App.Specs.csproj"] }
            ] }
            """);

        var plan = (await ScopePlanner.PlanExplicitAsync([Path.Combine(_repository.Root, "src/App.Specs/Helper.cs")],
            _repository.Root, CancellationToken.None)).Plan;

        Assert.Contains(plan.Exclusions, item => item.Path == "src/App.Specs/Helper.cs" &&
            item.ReasonCode == "TEST_SOURCE");
    }

    [Fact]
    public async Task BaseTreeUsesTheSameProjectOutputExclusionsAsCapture()
    {
        _repository.WriteText("src/App/App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />\n");
        _repository.WriteText("src/App/obj/Generated.cs", "class Generated { }\n");
        _repository.WriteText("ignored.txt", "captured exclusion\n");
        _repository.Git("add", "-f", ".");
        _repository.Git("commit", "-m", "base");

        var options = SnapshotCaptureOptions.Default with
            { ExcludedRelativePaths = new HashSet<string>(["ignored.txt"], StringComparer.Ordinal) };
        await using var snapshot = await SnapshotCapture.CaptureAsync(_repository.Root, "HEAD", [],
            options, CancellationToken.None);
        var changes = await GitChangePlanner.DiscoverAsync(snapshot, CancellationToken.None);

        Assert.DoesNotContain(changes.Files, change =>
            (change.CurrentPath ?? change.BasePath) == "src/App/obj/Generated.cs");
        Assert.DoesNotContain(changes.Files, change =>
            (change.CurrentPath ?? change.BasePath) == "ignored.txt");
    }

    [Fact]
    public async Task InvalidSourceEncodingHasAStableSpecificIncompleteReason()
    {
        WriteConfiguredProject("src/App/App.csproj", "tests/App.Tests/App.Tests.csproj");
        _repository.WriteText("src/App/A.cs", "class A { }\n");
        _repository.WriteText("mutate4csharp.json", """
            { "version": 1, "projects": [
              { "project": "src/App/App.csproj", "tests": ["tests/App.Tests/App.Tests.csproj"] }
            ] }
            """);
        _repository.Git("add", ".");
        _repository.Git("commit", "-m", "base");
        _repository.Write("src/App/A.cs", [0xff, 0xfe, 0xfd]);
        var report = Path.Combine(_repository.Root, "encoding-report.json");
        var prior = Environment.CurrentDirectory;
        Environment.CurrentDirectory = _repository.Root;
        try
        {
            var result = await new EvaluationCoordinator().RunAsync(
                new(false, "HEAD", [], report, "encoding-specific"), CancellationToken.None);
            Assert.Contains(result.Report.Reasons, reason => reason.Code == "SCOPE_UNSUPPORTED_ENCODING");
            Assert.False(result.Report.ScopePlan.IsComplete);
        }
        finally { Environment.CurrentDirectory = prior; }
    }

    private async Task PrepareConfiguredRepositoryAsync()
    {
        WriteConfiguredProject("src/App/App.csproj", "tests/App.Tests/App.Tests.csproj");
        _repository.WriteText("src/App/View.razor", "<p>base</p>\n");
        _repository.WriteText("mutate4csharp.json", """
            { "version": 1, "projects": [
              { "project": "src/App/App.csproj", "tests": ["tests/App.Tests/App.Tests.csproj"] }
            ] }
            """);
        _repository.Git("add", ".");
        _repository.Git("commit", "-m", "base");
        await Task.CompletedTask;
    }

    private void WriteConfiguredProject(string project, string test)
    {
        _repository.WriteText(project, "<Project Sdk=\"Microsoft.NET.Sdk\" />\n");
        _repository.WriteText(test, "<Project Sdk=\"Microsoft.NET.Sdk\" />\n");
    }

    public void Dispose() => _repository.Dispose();
}
