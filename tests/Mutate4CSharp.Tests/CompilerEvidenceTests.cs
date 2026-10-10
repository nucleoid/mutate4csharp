namespace Mutate4CSharp.Tests;

public sealed class CompilerEvidenceTests
{
    [Fact]
    public void RequiresAttributableDiagnosticAndHealthySameInputControl()
    {
        var mutated = Run(1, "src/Subject.cs(4,12): error CS0019: Operator cannot be applied");
        var healthyControl = Run(0, string.Empty, discovered: true, valid: true);

        var evidence = CompilerEvidence.Evaluate(mutated, healthyControl, "src/Subject.cs", Environment.CurrentDirectory);

        Assert.True(evidence.IsCompileInvalid);
        Assert.Single(evidence.Diagnostics);
        Assert.Contains("CS0019", evidence.Diagnostics[0]);
    }

    [Theory]
    [InlineData("Build FAILED. The build failed.")]
    [InlineData("restore error: package CS1002 was not found")]
    [InlineData("warning: saw the text : error CS1002 without a file diagnostic")]
    public void MereCompilerLookingTextCannotExcludeAUnit(string output)
    {
        var evidence = CompilerEvidence.Evaluate(Run(1, output), Run(0, string.Empty, true, true),
            "src/Subject.cs", Environment.CurrentDirectory);

        Assert.False(evidence.IsCompileInvalid);
    }

    [Fact]
    public void UnhealthyControlCannotProveCompileInvalid()
    {
        var mutated = Run(1, "src/Subject.cs(1,1): error CS1002: ; expected");
        var control = Run(1, "host failed");

        Assert.False(CompilerEvidence.Evaluate(mutated, control, "src/Subject.cs",
            Environment.CurrentDirectory).IsCompileInvalid);
    }

    [Fact]
    public void DiagnosticsAreBoundedAndSanitized()
    {
        var output = string.Join('\n', Enumerable.Range(0, 100).Select(i => $"src/Subject.cs({i + 1},1): error CS1002: secret-{i}"));
        var evidence = CompilerEvidence.Evaluate(Run(1, output), Run(0, string.Empty, true, true),
            "src/Subject.cs", Environment.CurrentDirectory);

        Assert.InRange(evidence.Diagnostics.Count, 1, CompilerEvidence.MaxDiagnostics);
        Assert.All(evidence.Diagnostics, diagnostic => Assert.True(diagnostic.Length <= CompilerEvidence.MaxDiagnosticLength));
    }

    [Fact]
    public void TrxHostErrorAlongsideFailedTestIsNotCleanKillEvidence()
    {
        var directory = Path.Combine(Path.GetTempPath(), "mutate4csharp-trx", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, "results.trx"), """
<TestRun><Results><UnitTestResult testName="Suite.Fails" outcome="Failed" /></Results>
<ResultSummary outcome="Failed"><Counters total="1" executed="1" passed="0" failed="1" />
<Output><ErrorInfo><Message>test host crashed</Message></ErrorInfo></Output></ResultSummary></TestRun>
""");
            var evidence = TestRunner.AnalyzeTrx(directory);

            Assert.True(evidence.Valid);
            Assert.True(evidence.HasRunErrors);
            Assert.Contains("Suite.Fails", evidence.FailedTestIds);
            Assert.False(StrictTestEvidence.IsCleanKill(evidence));
            var legacy = new TestRunResult(1, TimeSpan.Zero, false, true, true, evidence.Valid,
                string.Empty, string.Empty, evidence.Paths, evidence.HasRunErrors,
                evidence.FailedTestIds, evidence.Diagnostics);
            Assert.Equal(MutantStatus.Killed, MutationExecutor.Classify(legacy));
        }
        finally { try { Directory.Delete(directory, true); } catch { } }
    }

    [Fact]
    public void TrxEvidenceCarriesOriginalTotalsBeyondRetainedBounds()
    {
        var directory = Path.Combine(Path.GetTempPath(), "mutate4csharp-trx", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var results = string.Join(string.Empty, Enumerable.Range(1, 60)
                .Select(index => $"<UnitTestResult testName=\"Suite.Fails{index:D2}\" outcome=\"Failed\" />"));
            var messages = string.Join(string.Empty, Enumerable.Range(1, 25)
                .Select(index => $"<Message>diagnostic-{index:D2}</Message>"));
            File.WriteAllText(Path.Combine(directory, "results.trx"), $"""
                <TestRun><Results>{results}</Results>
                <ResultSummary outcome="Failed"><Counters total="60" executed="60" passed="0" failed="60" />
                <Output>{messages}</Output></ResultSummary></TestRun>
                """);

            var evidence = TestRunner.AnalyzeTrx(directory);

            Assert.True(evidence.Valid);
            Assert.Equal(60, evidence.FailedTestCount);
            Assert.Equal(25, evidence.DiagnosticCount);
            Assert.Equal(50, evidence.FailedTestIds.Count);
            Assert.Equal(EvaluationEvidence.MaxDiagnostics, evidence.Diagnostics.Count);
            Assert.Contains("diagnostics-truncated=6", evidence.Diagnostics);
        }
        finally { try { Directory.Delete(directory, true); } catch { } }
    }

    [Fact]
    public void WindowsDiagnosticUsesExactFullPathAttribution()
    {
        var control = Run(0, string.Empty, true, true);
        var exact = Run(1, @"C:\repo\src\Subject.cs(4,12): error CS0019: bad operator");
        var wrongDirectory = Run(1, @"C:\repo\other\Subject.cs(4,12): error CS0019: bad operator");

        Assert.True(CompilerEvidence.Evaluate(exact, control, @"C:\repo\src\Subject.cs", @"C:\repo").IsCompileInvalid);
        Assert.False(CompilerEvidence.Evaluate(wrongDirectory, control, @"C:\repo\src\Subject.cs", @"C:\repo").IsCompileInvalid);
    }

    [Fact]
    public void CrLfTerminatedWindowsDiagnosticRetainsExactTargetAttribution()
    {
        var control = Run(0, string.Empty, true, true);
        var exact = Run(1,
            "C:\\repo\\src\\Subject.cs(4,12): error CS0019: bad operator\r\n");

        var evidence = CompilerEvidence.Evaluate(exact, control,
            @"C:\repo\src\Subject.cs", @"C:\repo");

        Assert.True(evidence.IsCompileInvalid);
        Assert.Single(evidence.Diagnostics);
    }

    [Fact]
    public void CrLfTerminatedNumericNodePrefixRetainsExactTargetAttribution()
    {
        var control = Run(0, string.Empty, true, true);
        var exact = Run(1,
            "1>C:\\repo\\src\\Subject.cs(4,12): error CS0019: bad operator\r\n");

        var evidence = CompilerEvidence.Evaluate(exact, control,
            @"C:\repo\src\Subject.cs", @"C:\repo");

        Assert.True(evidence.IsCompileInvalid);
        Assert.Single(evidence.Diagnostics);
    }

    [Fact]
    public void CrLfTerminatedSiblingDiagnosticDoesNotAttributeTarget()
    {
        var control = Run(0, string.Empty, true, true);
        var sibling = Run(1,
            "C:\\repo\\other\\Subject.cs(4,12): error CS0019: bad operator\r\n");

        var evidence = CompilerEvidence.Evaluate(sibling, control,
            @"C:\repo\src\Subject.cs", @"C:\repo");

        Assert.False(evidence.IsCompileInvalid);
        Assert.Empty(evidence.Diagnostics);
    }

    [Fact]
    public void WindowsRangedDiagnosticUsesExactFullPathAttribution()
    {
        var control = Run(0, string.Empty, true, true);
        var ranged = Run(1,
            @"C:\repo\src\Subject.cs(4,12,4,20): error CS0019: bad operator");

        var evidence = CompilerEvidence.Evaluate(ranged, control,
            @"C:\repo\src\Subject.cs", @"C:\repo");

        Assert.True(evidence.IsCompileInvalid);
        Assert.Single(evidence.Diagnostics);
        Assert.Contains("CS0019", evidence.Diagnostics[0]);
        Assert.DoesNotContain(@"C:\repo", evidence.Diagnostics[0], StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("bad operator", evidence.Diagnostics[0], StringComparison.Ordinal);
        Assert.Contains("classifier-exact-path-matches=1", evidence.ClassificationDiagnostics);
    }

    [Fact]
    public void UnsupportedCompilerLocationPublishesOnlySafeClassifierStages()
    {
        var control = Run(0, string.Empty, true, true);
        var output = @"C:\private\runner\src\Subject.cs(4): error CS0019: private message";

        var evidence = CompilerEvidence.Evaluate(Run(1, output), control,
            @"C:\private\runner\src\Subject.cs", @"C:\private\runner");

        Assert.False(evidence.IsCompileInvalid);
        Assert.Empty(evidence.Diagnostics);
        Assert.Contains("classifier-compiler-lines=1", evidence.ClassificationDiagnostics);
        Assert.Contains("classifier-location-matches=0", evidence.ClassificationDiagnostics);
        Assert.DoesNotContain(evidence.ClassificationDiagnostics,
            value => value.Contains("private", StringComparison.OrdinalIgnoreCase) ||
                     value.Contains("Subject.cs", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("1>C:\\repo\\src\\Subject.cs(4,12,4,20): error CS0019: bad operator")]
    [InlineData("C:\\repo\\src\\Subject.cs(4,12,4,20) : error CS0019: bad operator")]
    [InlineData("\u001b[31mC:\\repo\\src\\Subject.cs(4,12,4,20): error CS0019: bad operator\u001b[0m")]
    public void WindowsMsBuildDecorationsRetainExactTargetAttribution(string output)
    {
        var control = Run(0, string.Empty, true, true);

        var evidence = CompilerEvidence.Evaluate(Run(1, output), control,
            @"C:\repo\src\Subject.cs", @"C:\repo");

        Assert.True(evidence.IsCompileInvalid);
        Assert.Single(evidence.Diagnostics);
    }

    [Fact]
    public void SeparateStTerminatedOscSequencesDoNotConsumeInterveningDiagnostic()
    {
        var control = Run(0, string.Empty, true, true);
        var output = "\u001b]9;4;1;10\u001b\\\n" +
            @"C:\repo\src\Subject.cs(4,12): error CS0019: bad operator" + "\n" +
            "\u001b]9;4;0\u001b\\";

        var evidence = CompilerEvidence.Evaluate(Run(1, output), control,
            @"C:\repo\src\Subject.cs", @"C:\repo");

        Assert.True(evidence.IsCompileInvalid);
        Assert.Single(evidence.Diagnostics);
    }

    [Fact]
    public void UnterminatedOscDoesNotConsumeDiagnosticOnFollowingLine()
    {
        var control = Run(0, string.Empty, true, true);
        var output = "\u001b]9;4;1;10\n" +
            @"C:\repo\src\Subject.cs(4,12): error CS0019: bad operator";

        var evidence = CompilerEvidence.Evaluate(Run(1, output), control,
            @"C:\repo\src\Subject.cs", @"C:\repo");

        Assert.True(evidence.IsCompileInvalid);
        Assert.Single(evidence.Diagnostics);
    }

    [Fact]
    public void DiagnosticSeparatorsCannotCrossLineBoundaries()
    {
        var control = Run(0, string.Empty, true, true);
        var output = @"C:\repo\src\Subject.cs(4,12)" + "\n" +
            ": error CS0019: bad operator";

        var evidence = CompilerEvidence.Evaluate(Run(1, output), control,
            @"C:\repo\src\Subject.cs", @"C:\repo");

        Assert.False(evidence.IsCompileInvalid);
        Assert.Empty(evidence.Diagnostics);
    }

    [Fact]
    public void DiagnosticSeparatorsCannotCrossCrLfBoundaries()
    {
        var control = Run(0, string.Empty, true, true);
        var output = @"C:\repo\src\Subject.cs(4,12)" + "\r\n" +
            ": error CS0019: bad operator\r\n";

        var evidence = CompilerEvidence.Evaluate(Run(1, output), control,
            @"C:\repo\src\Subject.cs", @"C:\repo");

        Assert.False(evidence.IsCompileInvalid);
        Assert.Empty(evidence.Diagnostics);
    }

    [Fact]
    public void RegexTimeoutFailsClosedWithBoundedNonSensitiveEvidence()
    {
        var control = Run(0, string.Empty, true, true);
        var mutated = Run(1,
            @"C:\repo\src\Subject.cs(4,12): error CS0019: private compiler text");

        var evidence = CompilerEvidence.Evaluate(mutated, control,
            @"C:\repo\src\Subject.cs", @"C:\repo",
            _ => throw new System.Text.RegularExpressions.RegexMatchTimeoutException());

        Assert.False(evidence.IsCompileInvalid);
        Assert.Empty(evidence.Diagnostics);
        Assert.Equal(["classifier-stage=regex-timeout"], evidence.ClassificationDiagnostics);
    }

    [Fact]
    public void OscDecorationLongerThan256CharactersRemainsFailClosed()
    {
        var control = Run(0, string.Empty, true, true);
        var output = "\u001b]8;;file://" + new string('x', 257) + "\u0007" +
            @"C:\repo\src\Subject.cs(4,12): error CS0019: bad operator";

        var evidence = CompilerEvidence.Evaluate(Run(1, output), control,
            @"C:\repo\src\Subject.cs", @"C:\repo");

        Assert.False(evidence.IsCompileInvalid);
        Assert.Empty(evidence.Diagnostics);
        Assert.Contains("classifier-compiler-lines=1", evidence.ClassificationDiagnostics);
        Assert.Contains("classifier-exact-path-matches=0", evidence.ClassificationDiagnostics);
    }

    [Theory]
    [InlineData("1>C:\\repo\\other\\Subject.cs(4,12,4,20): error CS0019: bad operator")]
    [InlineData("\u001b[31mC:\\repo\\other\\Subject.cs(4,12,4,20): error CS0019: bad operator\u001b[0m")]
    public void WindowsMsBuildDecorationsDoNotAttributeSiblingPath(string output)
    {
        var control = Run(0, string.Empty, true, true);

        var evidence = CompilerEvidence.Evaluate(Run(1, output), control,
            @"C:\repo\src\Subject.cs", @"C:\repo");

        Assert.False(evidence.IsCompileInvalid);
    }

    [Theory]
    [InlineData(@"C:\Users\RUNNER~1\AppData\Local\Temp\mutate\root\src\Subject.cs",
        @"C:\Users\runneradmin\AppData\Local\Temp\mutate\root\src\Subject.cs")]
    [InlineData(@"C:\Users\runneradmin\AppData\Local\Temp\mutate\root\src\Subject.cs",
        @"C:\Users\RUNNER~1\AppData\Local\Temp\mutate\root\src\Subject.cs")]
    public void WindowsShortAndLongNamesRetainExactTargetAttribution(string diagnosticPath,
        string targetPath)
    {
        var control = Run(0, string.Empty, true, true);
        var mutated = Run(1, $"{diagnosticPath}(4,12,4,20): error CS0019: bad operator");

        var evidence = CompilerEvidence.Evaluate(mutated, control, targetPath,
            @"C:\Users\runneradmin", ExpandRunnerProfile);

        Assert.True(evidence.IsCompileInvalid);
        Assert.Single(evidence.Diagnostics);
    }

    [Fact]
    public void WindowsShortNameExpansionDoesNotAttributeSiblingPath()
    {
        var control = Run(0, string.Empty, true, true);
        var wrongSibling = Run(1,
            @"C:\Users\RUNNER~1\AppData\Local\Temp\mutate\root\other\Subject.cs(4,12,4,20): error CS0019: bad operator");

        var evidence = CompilerEvidence.Evaluate(wrongSibling, control,
            @"C:\Users\runneradmin\AppData\Local\Temp\mutate\root\src\Subject.cs",
            @"C:\Users\runneradmin", ExpandRunnerProfile);

        Assert.False(evidence.IsCompileInvalid);
    }

    [Fact]
    public void SuccessfulMutantCannotBeCompileInvalidFromConsoleText()
    {
        var successfulMutant = Run(0, "src/Subject.cs(1,1): error CS1002: printed text", true, true);
        var control = Run(0, string.Empty, true, true);

        Assert.False(CompilerEvidence.Evaluate(successfulMutant, control, "src/Subject.cs",
            Environment.CurrentDirectory).IsCompileInvalid);
    }

    private static TestRunResult Run(int exit, string output, bool discovered = false, bool valid = false) =>
        new(exit, TimeSpan.Zero, false, discovered, false, valid, output, string.Empty, []);

    private static string ExpandRunnerProfile(string path) => path.Replace(
        @"C:/Users/RUNNER~1/", @"C:/Users/runneradmin/", StringComparison.OrdinalIgnoreCase);
}
