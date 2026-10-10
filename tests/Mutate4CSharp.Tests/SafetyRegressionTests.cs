using System.Text;

namespace Mutate4CSharp.Tests;

public sealed class SafetyRegressionTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "mutate4csharp-safety", Guid.NewGuid().ToString("N"));
    public SafetyRegressionTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public void MutantClassifierRequiresFailedTestcaseEvidence()
    {
        Assert.Equal(MutantStatus.Error, MutationExecutor.Classify(Run(exit: 1, executed: true, failed: false)));
        Assert.Equal(MutantStatus.Killed, MutationExecutor.Classify(Run(exit: 1, executed: true, failed: true)));
        Assert.Equal(MutantStatus.Error, MutationExecutor.Classify(Run(exit: 0, executed: false, failed: false)));
        Assert.Equal(MutantStatus.Error, MutationExecutor.Classify(Run(exit: 0, executed: true, failed: false, valid: false)));
    }

    [Fact]
    public void CompilerFailureIsNeverKilled()
    {
        var run = Run(exit: 1, executed: true, failed: true) with { StandardOutput = "Build FAILED: error CS1002" };
        Assert.Equal(MutantStatus.CompileError, MutationExecutor.Classify(run));
    }

    [Fact]
    public void TrxAggregationFindsEarlierFailureAcrossAssemblies()
    {
        var results = Path.Combine(_directory, "results"); Directory.CreateDirectory(results);
        WriteTrx(Path.Combine(results, "a.trx"), "Failed");
        WriteTrx(Path.Combine(results, "z.trx"), "Passed");
        File.SetLastWriteTimeUtc(Path.Combine(results, "z.trx"), DateTime.UtcNow.AddMinutes(1));

        var evidence = TestRunner.AnalyzeTrx(results);

        Assert.True(evidence.Valid); Assert.True(evidence.TestsExecuted); Assert.True(evidence.HasFailedTests);
        Assert.Equal(2, evidence.Paths.Count);
    }

    [Fact]
    public void TrxSkippedOnlyAndMissingAreNotHealthy()
    {
        var skipped = Path.Combine(_directory, "skipped"); Directory.CreateDirectory(skipped);
        WriteTrx(Path.Combine(skipped, "only.trx"), "NotExecuted");
        var skippedEvidence = TestRunner.AnalyzeTrx(skipped);
        Assert.True(skippedEvidence.Valid); Assert.False(skippedEvidence.TestsExecuted);
        Assert.False(TestRunner.AnalyzeTrx(Path.Combine(_directory, "missing")).Valid);
    }

    [Fact]
    public void TrxCounterAndResultConflictFailsClosed()
    {
        var results = Path.Combine(_directory, "conflict"); Directory.CreateDirectory(results);
        WriteTrx(Path.Combine(results, "bad.trx"), "Passed", failedCounter: 1);
        Assert.False(TestRunner.AnalyzeTrx(results).Valid);
    }

    [Fact]
    public void CoverageRequiresObservedOpenCoverPoint()
    {
        var target = Write("Target.cs", "class Target {}");
        var unrelated = Write("Other.cs", "class Other {}");
        var unrelatedReport = Write("unrelated.xml", OpenCover(unrelated, 10, 0));
        var missingLine = Write("missing-line.xml", OpenCover(target, 11, 0));
        var nonOpenCover = Write("cobertura.xml", "<coverage><packages /></coverage>");

        Assert.Equal(CoverageState.Unknown, CoverageMap.Load(unrelatedReport, _directory)!.GetState(target, 10));
        Assert.Equal(CoverageState.Unknown, CoverageMap.Load(missingLine, _directory)!.GetState(target, 10));
        Assert.Equal(CoverageState.Unknown, CoverageMap.Load(nonOpenCover, _directory)!.GetState(target, 10));
    }

    [Fact]
    public void CoverageKnownZeroIsUncoveredButHiddenPointIsUnknown()
    {
        var target = Write("Target.cs", "class Target {}");
        var zero = Write("zero.xml", OpenCover(target, 10, 0));
        var hidden = Write("hidden.xml", OpenCover(target, 0xFEEFEE, 0));
        Assert.Equal(CoverageState.Uncovered, CoverageMap.Load(zero, _directory)!.GetState(target, 10));
        Assert.Equal(CoverageState.Unknown, CoverageMap.Load(hidden, _directory)!.GetState(target, 0xFEEFEE));
    }

    [Fact]
    public void CoverageMergesReportsAndScopesFileIdsPerModule()
    {
        var target = Write("Target.cs", "class Target {}");
        var other = Write("Other.cs", "class Other {}");
        var first = Write("coverage-a.xml", OpenCover(other, 10, 0));
        var second = Write("coverage-b.xml", OpenCover(target, 10, 3));
        var map = CoverageMap.Load([first, second], _directory)!;
        Assert.Equal(CoverageState.Covered, map.GetState(target, 10));
        Assert.Equal(CoverageState.Uncovered, map.GetState(other, 10));
    }

    [Fact]
    public void IncompleteCoverageCannotProveUncovered()
    {
        var target = Write("Target.cs", "class Target {}");
        var zero = Write("coverage-zero.xml", OpenCover(target, 10, 0));
        var malformed = Write("coverage-malformed.xml", "<CoverageSession>");
        Assert.Equal(CoverageState.Unknown, CoverageMap.Load([zero, malformed], _directory)!.GetState(target, 10));
    }

    [Fact]
    public void StrictCoverageUsesCompleteSequencePointSpansConservatively()
    {
        var target = Write("Target.cs", "class Target {}\n");
        var report = Write("coverage-spans.xml", $"""
            <CoverageSession><Modules><Module><Files><File uid="1" fullPath="{System.Security.SecurityElement.Escape(target)}"/></Files>
            <Classes><Class><Methods><Method><FileRef uid="1"/><SequencePoints>
            <SequencePoint vc="1" sl="10" sc="5" el="12" ec="30"/>
            <SequencePoint vc="0" sl="11" sc="15" el="11" ec="20"/>
            </SequencePoints></Method></Methods></Class></Classes>
            </Module></Modules></CoverageSession>
            """);
        var map = CoverageMap.Load(report, _directory)!;

        Assert.Equal(CoverageState.Covered, map.GetState(target, 11, 16, 11, 18));
        Assert.Equal(CoverageState.Unknown, map.GetState(target, 12, 30, 13, 2));

        var onlyZero = Write("coverage-zero-span.xml", $"""
            <CoverageSession><Modules><Module><Files><File uid="1" fullPath="{System.Security.SecurityElement.Escape(target)}"/></Files>
            <Classes><Class><Methods><Method><FileRef uid="1"/><SequencePoints>
            <SequencePoint vc="0" sl="20" sc="5" el="22" ec="30"/>
            </SequencePoints></Method></Methods></Class></Classes>
            </Module></Modules></CoverageSession>
            """);
        Assert.Equal(CoverageState.Uncovered,
            CoverageMap.Load(onlyZero, _directory)!.GetState(target, 21, 8, 21, 12));

        var lineOnly = Write("coverage-line-only.xml", OpenCover(target, 30, 0));
        Assert.Equal(CoverageState.Unknown,
            CoverageMap.Load(lineOnly, _directory)!.GetState(target, 30, 2, 30, 8));

        var placeholder = Write("coverage-placeholder.xml", $"""
            <CoverageSession><Modules><Module><Files><File uid="1" fullPath="{System.Security.SecurityElement.Escape(target)}"/></Files>
            <Classes><Class><Methods><Method><FileRef uid="1"/><SequencePoints>
            <SequencePoint vc="0" sl="40" sc="1" el="40" ec="2"/>
            </SequencePoints></Method></Methods></Class></Classes>
            </Module></Modules></CoverageSession>
            """);
        Assert.Equal(CoverageState.Unknown,
            CoverageMap.Load(placeholder, _directory)!.GetState(target, 40, 1, 40, 2));
    }

    [Fact]
    public void SafeEnumerationPrunesSecretsExcludedTreesAndSymlinks()
    {
        var visible = Write("src/visible.cs", "ok");
        Write(".ssh/secret.cs", "secret");
        Write("obj/generated.cs", "generated");
        var outside = Path.Combine(_directory, "outside"); Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "escaped.cs"), "escape");
        var link = Path.Combine(_directory, "link");
        try { Directory.CreateSymbolicLink(link, outside); } catch (Exception) { link = string.Empty; }

        var files = ProjectLocator.EnumerateFilesSafe(_directory).ToArray();

        Assert.Contains(visible, files);
        Assert.DoesNotContain(files, x => x.Contains("secret.cs", StringComparison.Ordinal) || x.Contains("generated.cs", StringComparison.Ordinal));
        if (link.Length > 0)
        {
            Assert.DoesNotContain(files, x => x.StartsWith(link, StringComparison.Ordinal));
            Assert.Empty(ProjectLocator.EnumerateFilesSafe(link));
        }
    }

    [Fact]
    public void ContextFingerprintIncludesChosenSuiteAndOptions()
    {
        var root = Path.Combine(_directory, "context"); Directory.CreateDirectory(root);
        var target = WriteAt(root, "Target.cs", "class Target {}");
        var project = WriteAt(root, "Lib.csproj", "<Project />");
        var testA = WriteAt(root, "A.Tests.csproj", "<Project />");
        var testB = WriteAt(root, "B.Tests.csproj", "<Project />");
        var contextA = new ProjectContext(root, project, testA, "Target.cs", target);
        var contextB = contextA with { TestProject = testB };
        var options = new Options(target, false, false, false, false, null, false, false, 50, 1, 2, false, project, testA, root, null);
        Assert.NotEqual(ProjectLocator.ContextFingerprint(contextA, options), ProjectLocator.ContextFingerprint(contextB, options));
        Assert.NotEqual(ProjectLocator.ContextFingerprint(contextA, options), ProjectLocator.ContextFingerprint(contextA, options with { TimeoutFactor = 3 }));
        var coverage = WriteAt(root, "coverage.xml", "first");
        var reuse = options with { ReuseCoverage = true, CoverageReport = coverage };
        var before = ProjectLocator.ContextFingerprint(contextA, reuse);
        File.WriteAllText(coverage, "second");
        Assert.NotEqual(before, ProjectLocator.ContextFingerprint(contextA, reuse));
    }

    [Fact]
    public void SourceAnalysisDoesNotTraverseSymlinkedProjectDirectory()
    {
        var project = Path.Combine(_directory, "analysis"); Directory.CreateDirectory(project);
        var target = WriteAt(project, "Target.cs", "class Target { External Value() => new External(); }");
        var outside = Path.Combine(_directory, "outside-analysis"); Directory.CreateDirectory(outside);
        WriteAt(outside, "External.cs", "class External { }");
        try { Directory.CreateSymbolicLink(Path.Combine(project, "linked"), outside); }
        catch (Exception) { return; }

        var control = SourceAnalyzer.Analyze(target, outside);
        var analysis = SourceAnalyzer.Analyze(target, project);

        Assert.Contains(control.Sites, site => site.Replacement == "null");
        Assert.DoesNotContain(analysis.Sites, site => site.Replacement == "null");
    }

    [Fact]
    public void AtomicManifestWritePreservesUtf8Bom()
    {
        var path = Path.Combine(_directory, "bom.cs");
        var encoding = new UTF8Encoding(true);
        File.WriteAllText(path, "class C {}\n", encoding);
        ManifestStore.AtomicWriteExpected(path, "class C {}\n", "class C {}\n// manifest\n");
        Assert.True(File.ReadAllBytes(path).AsSpan().StartsWith(Encoding.UTF8.GetPreamble()));
    }

    private static TestRunResult Run(int exit, bool executed, bool failed, bool valid = true) =>
        new(exit, TimeSpan.Zero, false, executed, failed, valid, string.Empty, string.Empty, ["results.trx"]);

    private void WriteTrx(string path, string outcome, int? failedCounter = null)
    {
        var executed = outcome is "Passed" or "Failed" ? 1 : 0;
        var passed = outcome == "Passed" ? 1 : 0;
        var failed = failedCounter ?? (outcome == "Failed" ? 1 : 0);
        File.WriteAllText(path, $"""
<TestRun><Results><UnitTestResult testName="test" outcome="{outcome}" /></Results>
<ResultSummary outcome="{(failed > 0 ? "Failed" : "Completed")}"><Counters total="1" executed="{executed}" passed="{passed}" failed="{failed}" /></ResultSummary></TestRun>
""");
    }

    private static string OpenCover(string path, int line, int visits) => $"""
<CoverageSession><Modules><Module><Files><File uid="1" fullPath="{System.Security.SecurityElement.Escape(path)}"/></Files>
<Classes><Class><Methods><Method><FileRef uid="1"/><SequencePoints><SequencePoint vc="{visits}" sl="{line}"/></SequencePoints></Method></Methods></Class></Classes>
</Module></Modules></CoverageSession>
""";

    private string Write(string relative, string text) => WriteAt(_directory, relative, text);
    private static string WriteAt(string root, string relative, string text)
    {
        var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, text); return path;
    }

    public void Dispose() { try { Directory.Delete(_directory, true); } catch { } }
}
