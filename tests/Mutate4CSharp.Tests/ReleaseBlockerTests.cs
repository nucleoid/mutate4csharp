using System.Text;

namespace Mutate4CSharp.Tests;

public sealed class ReleaseBlockerTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "mutate4csharp-blockers", Guid.NewGuid().ToString("N"));

    public ReleaseBlockerTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public void NonzeroInfrastructureExitWithDiscoveredTestsIsErrorNotKilled()
    {
        var run = new TestRunResult(1, TimeSpan.Zero, false, true, false, true,
            "test host disconnected", string.Empty, []);

        Assert.Equal(MutantStatus.Error, MutationExecutor.Classify(run));
    }

    [Fact]
    public void FailedTestsRequireValidTrxEvidenceToBeKilled()
    {
        var invalid = new TestRunResult(1, TimeSpan.Zero, false, true, true, false,
            string.Empty, string.Empty, []);
        var valid = invalid with { TrxValid = true };

        Assert.Equal(MutantStatus.Error, MutationExecutor.Classify(invalid));
        Assert.Equal(MutantStatus.Killed, MutationExecutor.Classify(valid));
    }

    [Fact]
    public void GenericFailedBuildBannerIsInfrastructureErrorWithoutCompilerDiagnostic()
    {
        var run = new TestRunResult(1, TimeSpan.Zero, false, false, false, false,
            "Build FAILED. The build failed.", string.Empty, []);

        Assert.Equal(MutantStatus.Error, MutationExecutor.Classify(run));
    }

    [Fact]
    public void CopyTreePrunesExcludedDirectorySymlinks()
    {
        if (OperatingSystem.IsWindows()) return;
        var source = Path.Combine(_directory, "source");
        var destination = Path.Combine(_directory, "copy");
        var outside = Path.Combine(_directory, "outside");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(source, "kept.txt"), "kept");
        File.WriteAllText(Path.Combine(outside, "escaped.txt"), "must not copy");
        Directory.CreateSymbolicLink(Path.Combine(source, ".git"), outside);

        MutationExecutor.CopyTree(source, destination);

        Assert.True(File.Exists(Path.Combine(destination, "kept.txt")));
        Assert.False(Directory.Exists(Path.Combine(destination, ".git")));
        Assert.False(File.Exists(Path.Combine(destination, "escaped.txt")));
    }

    [Fact]
    public void AtomicWriteRechecksExpectedContentImmediatelyBeforeReplace()
    {
        var path = Path.Combine(_directory, "Subject.cs");
        const string original = "class Subject { }";
        const string concurrent = "class Subject { /* concurrent */ }";
        File.WriteAllText(path, original);

        var error = Assert.Throws<IOException>(() => ManifestStore.AtomicWriteExpected(
            path, original, "replacement", () => File.WriteAllText(path, concurrent)));

        Assert.Contains("changed during the run", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(concurrent, File.ReadAllText(path));
        Assert.Empty(Directory.EnumerateFiles(_directory, ".Subject.cs.*.tmp"));
    }

    [Fact]
    public void ManifestWritePreservesUtf8BomAndLfBehavior()
    {
        var path = Path.Combine(_directory, "Bom.cs");
        const string original = "class Bom {\n    bool Value() => true;\n}";
        File.WriteAllText(path, original, new UTF8Encoding(true));
        var analysis = SourceAnalyzer.Analyze(path, _directory);

        ManifestStore.AtomicWriteExpected(path, analysis.OriginalSource, ManifestStore.Compose(analysis, "context"));

        var bytes = File.ReadAllBytes(path);
        Assert.True(bytes.AsSpan().StartsWith(Encoding.UTF8.GetPreamble()));
        var written = File.ReadAllText(path);
        Assert.DoesNotContain("\r\n", written, StringComparison.Ordinal);
        Assert.False(written.EndsWith('\n'));
        Assert.Equal(original, ManifestStore.Strip(written));
    }

    [Fact]
    public void ManifestWritePreservesUtf32Bom()
    {
        var path = Path.Combine(_directory, "Utf32.cs");
        const string original = "class Utf32 { }\n";
        var encoding = new UTF32Encoding(false, true);
        File.WriteAllText(path, original, encoding);

        ManifestStore.AtomicWriteExpected(path, original, original + "// manifest\n");

        Assert.True(File.ReadAllBytes(path).AsSpan().StartsWith(encoding.GetPreamble()));
        Assert.Equal(original + "// manifest\n", File.ReadAllText(path, encoding));
    }

    [Fact]
    public void ManifestComposeAndStripPreserveLegacyCarriageReturnNewlines()
    {
        var path = Path.Combine(_directory, "Legacy.cs");
        const string original = "class Legacy {\r    bool Value() => true;\r}";
        File.WriteAllText(path, original);
        var analysis = SourceAnalyzer.Analyze(path, _directory);

        var composed = ManifestStore.Compose(analysis, "context");

        Assert.DoesNotContain('\n', composed);
        Assert.Equal(original, ManifestStore.Strip(composed));
    }

    [Fact]
    public void WindowsStyleInternalProjectReferenceIsAcceptedOnLinux()
    {
        var root = Path.Combine(_directory, "root");
        var libraryDirectory = Path.Combine(root, "Lib");
        var testsDirectory = Path.Combine(root, "Tests");
        Directory.CreateDirectory(libraryDirectory);
        Directory.CreateDirectory(testsDirectory);
        var target = Path.Combine(libraryDirectory, "Subject.cs");
        var library = Path.Combine(libraryDirectory, "Lib.csproj");
        var tests = Path.Combine(testsDirectory, "Tests.csproj");
        File.WriteAllText(target, "public static class Subject { }");
        File.WriteAllText(library, "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        File.WriteAllText(tests, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><IsTestProject>true</IsTestProject></PropertyGroup><ItemGroup><ProjectReference Include=\"..\\Lib\\Lib.csproj\" /></ItemGroup></Project>");
        var options = Cli.Parse([target, "--project", library, "--test-project", tests, "--root", root]).Options!;

        var context = ProjectLocator.Resolve(options);

        Assert.Equal(Path.GetFullPath(tests), context.TestProject);
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, true); } catch { }
    }
}
