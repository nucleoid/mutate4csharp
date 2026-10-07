using System.Text;

namespace Mutate4CSharp.Tests;

public sealed class IntegrationTests : IDisposable
{
    private readonly List<string> _directories = [];

    [Fact]
    public async Task RealRunReportsKilledAndDoesNotChangeOriginalForPartialSelection()
    {
        var fixture = CreateFixture("public static class Subject { public static bool IsZero(int x) => x == 0; }",
            "[Fact] public void BothPaths() { Assert.True(Subject.IsZero(0)); Assert.False(Subject.IsZero(2)); }");
        var before = File.ReadAllText(fixture.Target);
        var result = await Invoke(fixture, "--lines", "1");
        Assert.Equal(0, result.Code); Assert.Contains("KILLED", result.Output);
        Assert.Equal(before, File.ReadAllText(fixture.Target));
    }

    [Fact]
    public async Task RealRunReportsSurvivorAndKeepsOriginal()
    {
        var fixture = CreateFixture("public static class Subject { public static int Value() => 1; }",
            "[Fact] public void Weak() { Assert.True(Subject.Value() >= 0); }");
        var before = File.ReadAllText(fixture.Target);
        var result = await Invoke(fixture, "--mutate-all");
        Assert.Equal(3, result.Code); Assert.Contains("SURVIVED", result.Output);
        Assert.Equal(before, File.ReadAllText(fixture.Target));
    }

    [Fact]
    public async Task FreshCoverletOpenCoverBaselineFiltersAndKills()
    {
        var fixture = CreateFixture("public static class Subject { public static bool IsZero(int x) => x == 0; }",
            "[Fact] public void BothPaths() { Assert.True(Subject.IsZero(0)); Assert.False(Subject.IsZero(2)); }");
        var result = await Capture(() => Program.Main([fixture.Target, "--project", fixture.LibraryProject,
            "--test-project", fixture.TestProject, "--root", fixture.Root, "--lines", "1", "--max-workers", "1"]));
        Assert.Equal(0, result.Code); Assert.Contains("Covered mutation sites:", result.Output); Assert.Contains("KILLED", result.Output);
        Assert.DoesNotContain("Coverage is unavailable", result.Output);
    }

    [Fact]
    public async Task RedOrEmptyBaselineStopsBeforeMutation()
    {
        var fixture = CreateFixture("public static class Subject { public static bool Value() => true; }",
            "[Fact] public void Red() { Assert.True(false); }");
        var result = await Invoke(fixture, "--mutate-all");
        Assert.Equal(2, result.Code); Assert.Contains("Baseline", result.Error);
        Assert.DoesNotContain("KILLED", result.Output);
    }

    [Fact]
    public async Task TimedOutMutantIsDistinctAndDoesNotAdvanceManifest()
    {
        var fixture = CreateFixture("public static class Subject { public static int Value() { if (true) return 1; while (true) { } } }",
            "[Fact] public void FastNormally() { Assert.Equal(1, Subject.Value()); }");
        var before = File.ReadAllText(fixture.Target);
        var result = await Invoke(fixture, "--lines", "1", "--timeout-factor", "0.1");
        Assert.Equal(4, result.Code); Assert.Contains("TIMEOUT", result.Output);
        Assert.Equal(before, File.ReadAllText(fixture.Target));
    }

    [Fact]
    public async Task CompileFailureIsDistinguishedFromKilledTest()
    {
        var fixture = CreateFixture("public static class Subject { public static Number Add(Number a, Number b) => a + b; }",
            "[Fact] public void Adds() { Assert.Equal(3, Subject.Add(new(1), new(2)).Value); }");
        File.WriteAllText(Path.Combine(fixture.Root, "Lib", "Number.cs"),
            "public readonly record struct Number(int Value) { public static Number operator +(Number a, Number b) => new(a.Value + b.Value); }");
        var result = await Invoke(fixture, "--lines", "1");
        Assert.Equal(4, result.Code); Assert.Contains("COMPILE_ERROR", result.Output);
    }

    [Fact]
    public async Task ExactUncoveredLineIsReportedAndSkipped()
    {
        var fixture = CreateFixture("public static class Subject { public static bool Value() => true; }",
            "[Fact] public void Green() { Assert.True(Subject.Value()); }");
        var before = File.ReadAllText(fixture.Target);
        File.WriteAllText(fixture.Coverage, File.ReadAllText(fixture.Coverage).Replace("vc=\"1\"", "vc=\"0\"", StringComparison.Ordinal));
        var result = await Invoke(fixture, "--mutate-all");
        Assert.Equal(0, result.Code); Assert.Contains("UNCOVERED", result.Output); Assert.Contains("0 total", result.Output);
        Assert.Contains("Manifest not advanced", result.Output);
        Assert.Equal(before, File.ReadAllText(fixture.Target));
    }

    [Fact]
    public async Task EmptyBaselineIsRejected()
    {
        var fixture = CreateFixture("public static class Subject { public static bool Value() => true; }",
            "public void NotATest() { Assert.True(Subject.Value()); }");
        var result = await Invoke(fixture, "--mutate-all");
        Assert.Equal(2, result.Code); Assert.Contains("no tests were discovered", result.Error);
    }

    [Fact]
    public async Task ScanWritesNothingAndManualUpdateRunsNoTests()
    {
        var fixture = CreateFixture("public static class Subject { public static bool Value() => true; }",
            "[Fact] public void Red() { Assert.True(false); }");
        var before = File.ReadAllText(fixture.Target);
        var scan = await Capture(() => Program.Main([fixture.Target, "--scan"]));
        Assert.Equal(0, scan.Code); Assert.Equal(before, File.ReadAllText(fixture.Target));
        var update = await Capture(() => Program.Main([fixture.Target, "--update-manifest", "--project", fixture.LibraryProject,
            "--test-project", fixture.TestProject, "--root", fixture.Root]));
        Assert.Equal(0, update.Code); Assert.Contains("mutate4csharp-manifest", File.ReadAllText(fixture.Target));
        Assert.DoesNotContain("Baseline", update.Output + update.Error);
    }

    [Fact]
    public void TestAndConfigurationChangesInvalidateContextFingerprint()
    {
        var fixture = CreateFixture("public static class Subject { public static bool Value() => true; }",
            "[Fact] public void Green() { Assert.True(Subject.Value()); }");
        var options = Cli.Parse([fixture.Target, "--project", fixture.LibraryProject, "--test-project", fixture.TestProject, "--root", fixture.Root]).Options!;
        var context = ProjectLocator.Resolve(options);
        var first = ProjectLocator.ContextFingerprint(context);
        File.AppendAllText(Path.Combine(fixture.Root, "Tests", "SubjectTests.cs"), "\n// changed");
        var second = ProjectLocator.ContextFingerprint(context);
        Assert.NotEqual(first, second);
    }

    private Fixture CreateFixture(string subject, string testBody)
    {
        var root = Path.Combine(Path.GetTempPath(), "mutate4csharp integration with spaces", Guid.NewGuid().ToString("N"));
        _directories.Add(root); Directory.CreateDirectory(Path.Combine(root, "Lib")); Directory.CreateDirectory(Path.Combine(root, "Tests"));
        var library = Path.Combine(root, "Lib", "Lib.csproj");
        var tests = Path.Combine(root, "Tests", "Tests.csproj");
        var target = Path.Combine(root, "Lib", "Subject.cs");
        File.WriteAllText(library, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        File.WriteAllText(tests, """
<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework><IsTestProject>true</IsTestProject></PropertyGroup>
<ItemGroup><PackageReference Include="Microsoft.NET.Test.Sdk" Version="18.3.0"/><PackageReference Include="xunit.v3" Version="3.2.2"/><PackageReference Include="xunit.runner.visualstudio" Version="3.1.5"/><PackageReference Include="coverlet.collector" Version="6.0.4"/><ProjectReference Include="../Lib/Lib.csproj"/></ItemGroup></Project>
""");
        File.WriteAllText(target, subject + Environment.NewLine);
        File.WriteAllText(Path.Combine(root, "Tests", "SubjectTests.cs"), "using Xunit; public sealed class SubjectTests { " + testBody + " }");
        var coverage = Path.Combine(root, "coverage.xml");
        File.WriteAllText(coverage, $"<CoverageSession><Modules><Module><Files><File uid=\"1\" fullPath=\"{System.Security.SecurityElement.Escape(target)}\"/></Files><Classes><Class><Methods><Method><FileRef uid=\"1\"/><SequencePoints><SequencePoint vc=\"1\" sl=\"1\"/></SequencePoints></Method></Methods></Class></Classes></Module></Modules></CoverageSession>");
        return new(root, library, tests, target, coverage);
    }

    private static Task<CaptureResult> Invoke(Fixture fixture, params string[] additional) => Capture(() => Program.Main([
        fixture.Target, "--project", fixture.LibraryProject, "--test-project", fixture.TestProject, "--root", fixture.Root,
        "--coverage-report", fixture.Coverage, "--max-workers", "1", .. additional]));

    private static async Task<CaptureResult> Capture(Func<Task<int>> action)
    {
        var output = new StringWriter(); var error = new StringWriter();
        var oldOut = Console.Out; var oldError = Console.Error;
        Console.SetOut(output); Console.SetError(error);
        try { return new(await action(), output.ToString(), error.ToString()); }
        finally { Console.SetOut(oldOut); Console.SetError(oldError); }
    }

    public void Dispose() { foreach (var directory in _directories) try { Directory.Delete(directory, true); } catch { } }
    private sealed record Fixture(string Root, string LibraryProject, string TestProject, string Target, string Coverage);
    private sealed record CaptureResult(int Code, string Output, string Error);
}
