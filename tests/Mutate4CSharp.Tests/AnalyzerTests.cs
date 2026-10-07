namespace Mutate4CSharp.Tests;

public sealed class AnalyzerTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "mutate4csharp-tests", Guid.NewGuid().ToString("N"));
    public AnalyzerTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public void DiscoversRequiredMutationsAndExcludesStringConcat()
    {
        var file = Write("Sample.cs", """
#nullable enable
class Sample {
  object? F = new object();
  bool M(int x, bool a, bool b) {
    var s = "a" + x;
    var n = 1 + x * 1;
    return !a && b || (x >= 0 && x != 1);
  }
}
""");
        var result = SourceAnalyzer.Analyze(file, _directory);
        var descriptions = result.Sites.Select(x => x.Description).ToList();
        Assert.Contains("replace + with -", descriptions);
        Assert.Single(descriptions, x => x == "replace + with -");
        Assert.Contains("replace * with /", descriptions);
        Assert.Contains("replace >= with >", descriptions);
        Assert.Contains("replace != with ==", descriptions);
        Assert.Contains("remove unary !", descriptions);
        Assert.Contains(descriptions, x => x.StartsWith("replace rvalue", StringComparison.Ordinal));
        Assert.All(result.Sites, site =>
        {
            Assert.NotEqual("legacy.unknown", site.OperatorId);
            Assert.Equal(MutationIdentity.OperatorContractVersion, site.OperatorVersion);
        });
    }

    [Fact]
    public void IgnoresCommentsStringsAndGenericAngles()
    {
        var file = Write("Sample.cs", "class C { System.Collections.Generic.List<int> X = new(); string S = \"true == false\"; /* 0 < 1 */ }");
        var result = SourceAnalyzer.Analyze(file, _directory);
        Assert.DoesNotContain(result.Sites, x => x.Description == "replace == with !=");
        Assert.DoesNotContain(result.Sites, x => x.Description == "replace < with <=");
    }

    [Fact]
    public void ManifestRoundTripsAndIsStrippedFromAnalysis()
    {
        var file = Write("Sample.cs", "class C { bool M() => true; }\n");
        var first = SourceAnalyzer.Analyze(file, _directory);
        var composed = ManifestStore.Compose(first, "context");
        File.WriteAllText(file, composed);
        var manifest = ManifestStore.Read(composed, out var malformed);
        var second = SourceAnalyzer.Analyze(file, _directory);
        Assert.False(malformed); Assert.NotNull(manifest); Assert.Equal(first.Sites.Count, second.Sites.Count);
        Assert.DoesNotContain("manifest", second.SourceWithoutManifest, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BinaryZeroAndOneProduceValidIntegerReplacements()
    {
        var file = Write("Sample.cs", "class C { int Zero = 0b0; uint One = 0b1u; }");
        var result = SourceAnalyzer.Analyze(file, _directory);
        var zero = Assert.Single(result.Sites, x => x.Description == "replace 0 with 1");
        var one = Assert.Single(result.Sites, x => x.Description == "replace 1 with 0");
        Assert.Equal("1", zero.Replacement);
        Assert.Equal("0u", one.Replacement);
    }

    [Fact]
    public void CoverageFileIdsAreScopedPerOpenCoverModule()
    {
        var target = Write("Target.cs", "class Target { }");
        var other = Write("Other.cs", "class Other { }");
        var report = Write("coverage.xml", $"""
<CoverageSession><Modules>
  <Module><Files><File uid="1" fullPath="{System.Security.SecurityElement.Escape(other)}"/></Files>
    <Classes><Class><Methods><Method><FileRef uid="1"/><SequencePoints><SequencePoint vc="0" sl="7"/></SequencePoints></Method></Methods></Class></Classes>
  </Module>
  <Module><Files><File uid="1" fullPath="{System.Security.SecurityElement.Escape(target)}"/></Files>
    <Classes><Class><Methods><Method><FileRef uid="1"/><SequencePoints><SequencePoint vc="1" sl="7"/></SequencePoints></Method></Methods></Class></Classes>
  </Module>
</Modules></CoverageSession>
""");
        var coverage = CoverageMap.Load(report, _directory);
        Assert.NotNull(coverage);
        Assert.True(coverage.IsCovered(target, 7));
        Assert.False(coverage.IsCovered(other, 7));
    }

    [Theory]
    [InlineData(".ssh/id_ed25519")]
    [InlineData(".AWS/credentials")]
    [InlineData("nested/.env.production")]
    [InlineData("nested/signing.PFX")]
    public void SecretFilesAndDirectoriesAreRecognized(string relativePath)
    {
        Assert.True(ProjectLocator.IsSecret(Path.Combine(_directory, relativePath.Replace('/', Path.DirectorySeparatorChar))));
    }

    [Fact]
    public void WindowsStyleExternalProjectReferenceIsRejectedOnEveryPlatform()
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
        File.WriteAllText(library, "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup><ProjectReference Include=\"..\\..\\Outside\\Outside.csproj\" /></ItemGroup></Project>");
        File.WriteAllText(tests, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><IsTestProject>true</IsTestProject></PropertyGroup></Project>");
        var options = Cli.Parse([target, "--project", library, "--test-project", tests, "--root", root]).Options!;

        var error = Assert.Throws<ArgumentException>(() => ProjectLocator.Resolve(options));

        Assert.Contains("outside --root", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    private string Write(string name, string text) { var path = Path.Combine(_directory, name); File.WriteAllText(path, text); return path; }
    public void Dispose() { try { Directory.Delete(_directory, true); } catch { } }
}
