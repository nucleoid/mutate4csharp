using System.Text.Json;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace Mutate4CSharp.Tests;

public sealed class StrictMutationExecutorTests : IDisposable
{
    private readonly SnapshotTestRepository _repository = new();

    [Fact(Timeout = 420_000)]
    public async Task ExecutesOneExactMutationInAFreshFrozenWorker()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        WriteFixture();
        _repository.Git("add", ".");
        _repository.Git("commit", "-m", "fixture");
        await using var snapshot = await SnapshotCapture.CaptureAsync(_repository.Root, "HEAD", [],
            SnapshotCaptureOptions.Default, cancellationToken);
        await using var environment = await ExecutionEnvironment.PrepareDependenciesAsync(snapshot,
            ["tests/App.Tests/App.Tests.csproj"],
            DependencyPreparationOptions.Default with { Timeout = TimeSpan.FromMinutes(2) },
            cancellationToken);
        var suite = new SuiteExecution("suite-identity", ["unit"],
            "tests/App.Tests/App.Tests.csproj", "vstest", "net10.0", "Release",
            ["App.Tests.dll"]);
        await using var baseline = await new VstestSuiteExecutor(snapshot, environment)
            .RunBaselineAsync(suite, TimeSpan.FromMinutes(2), cancellationToken);
        Assert.Equal(SuiteRunDisposition.Passed, baseline.Disposition);

        var source = File.ReadAllText(Path.Combine(_repository.Root, "src/App/Flag.cs"));
        var tree = CSharpSyntaxTree.ParseText(SourceText.From(source), path: "src/App/Flag.cs",
            cancellationToken: cancellationToken);
        var root = tree.GetRoot(cancellationToken);
        var token = root.DescendantTokens().Single(item => item.ValueText == "true");
        var lineSpan = tree.GetLineSpan(token.Span, cancellationToken);
        Assert.Equal(CoverageState.Unknown, baseline.CoverageMap!.GetState("src/App/Flag.cs",
            lineSpan.StartLinePosition.Line + 1, lineSpan.StartLinePosition.Character + 1,
            lineSpan.EndLinePosition.Line + 1, lineSpan.EndLinePosition.Character + 1));
        var mutation = MutationIdentity.Create("src/App/Flag.cs", root, token.Span,
            "literal.boolean", MutationIdentity.OperatorContractVersion, "false");
        var evaluationId = EvaluationUnitIdentity.Compute(new(mutation.MutationId,
            "src/App/App.csproj", "net10.0", "net10-csharp14"));
        var candidate = new MutationCandidate(mutation.MutationId, evaluationId, mutation.Material,
            "src/App/App.csproj", "net10.0", "net10-csharp14", token.SpanStart, token.Span.Length,
            token.Text, 1);
        var executor = new StrictMutationExecutor(snapshot, environment, [candidate], [suite],
            new Dictionary<string, bool>(StringComparer.Ordinal) { [suite.Identity] = true });

        var result = await executor.ExecuteAsync(new ScheduledMutation(mutation.MutationId,
            evaluationId, [suite.Identity]), TimeSpan.FromMinutes(2), cancellationToken);

        Assert.Equal(UnitDisposition.Killed, result.Disposition);
        Assert.Contains(result.Evidence, item => item.Kind == "SUITE_MUTANT_RESULT");
        Assert.Contains(result.Evidence.SelectMany(item => item.Diagnostics ?? []),
            item => item.Contains("FlagTests.ValueIsTrue", StringComparison.Ordinal));
        Assert.Equal(source, File.ReadAllText(Path.Combine(_repository.Root, "src/App/Flag.cs")));
        Assert.False(Directory.Exists(Path.Combine(_repository.Root, "obj")));
        Assert.False(Directory.Exists(Path.Combine(_repository.Root, "bin")));
    }

    [Fact(Timeout = 420_000)]
    public async Task RealProcessDistinguishesSurvivedAndCompileInvalidMutants()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        WriteFixture();
        _repository.WriteText("tests/App.Tests/FlagTests.cs", """
            using Xunit;
            public sealed class FlagTests
            {
                [Fact] public void WeakTest() => Assert.True(true);
            }
            """);
        _repository.Git("add", ".");
        _repository.Git("commit", "-m", "weak-fixture");
        await using var snapshot = await SnapshotCapture.CaptureAsync(_repository.Root, "HEAD", [],
            SnapshotCaptureOptions.Default, cancellationToken);
        await using var environment = await ExecutionEnvironment.PrepareDependenciesAsync(snapshot,
            ["tests/App.Tests/App.Tests.csproj"],
            DependencyPreparationOptions.Default with { Timeout = TimeSpan.FromMinutes(2) },
            cancellationToken);
        var suite = new SuiteExecution("suite-identity", ["unit"],
            "tests/App.Tests/App.Tests.csproj", "vstest", "net10.0", "Release",
            ["App.Tests.dll"]);
        await using var baseline = await new VstestSuiteExecutor(snapshot, environment)
            .RunBaselineAsync(suite, TimeSpan.FromMinutes(2), cancellationToken);
        Assert.Equal(SuiteRunDisposition.Passed, baseline.Disposition);

        var source = File.ReadAllText(Path.Combine(_repository.Root, "src/App/Flag.cs"));
        var tree = CSharpSyntaxTree.ParseText(SourceText.From(source), path: "src/App/Flag.cs",
            cancellationToken: cancellationToken);
        var root = tree.GetRoot(cancellationToken);
        var token = root.DescendantTokens().Single(item => item.ValueText == "true");
        MutationCandidate Candidate(string replacement)
        {
            var mutation = MutationIdentity.Create("src/App/Flag.cs", root, token.Span,
                "literal.boolean", MutationIdentity.OperatorContractVersion, replacement);
            var evaluationId = EvaluationUnitIdentity.Compute(new(mutation.MutationId,
                "src/App/App.csproj", "net10.0", "net10-csharp14"));
            return new(mutation.MutationId, evaluationId, mutation.Material,
                "src/App/App.csproj", "net10.0", "net10-csharp14", token.SpanStart,
                token.Span.Length, token.Text, 1);
        }
        var survived = Candidate("false");
        var invalid = Candidate("42");
        var executor = new StrictMutationExecutor(snapshot, environment, [survived, invalid], [suite],
            new Dictionary<string, bool>(StringComparer.Ordinal) { [suite.Identity] = true });

        var survivedResult = await executor.ExecuteAsync(new(survived.MutationId,
            survived.EvaluationUnitId, [suite.Identity]), TimeSpan.FromMinutes(2), cancellationToken);
        var invalidResult = await executor.ExecuteAsync(new(invalid.MutationId,
            invalid.EvaluationUnitId, [suite.Identity]), TimeSpan.FromMinutes(2), cancellationToken);

        Assert.Equal(UnitDisposition.Survived, survivedResult.Disposition);
        var safeClassificationEvidence = SafeClassificationEvidence(invalidResult.Evidence);
        Assert.True(invalidResult.Disposition == UnitDisposition.CompileInvalid,
            $"Expected CompileInvalid but received {invalidResult.Disposition}. Safe evidence: {safeClassificationEvidence}");
        Assert.Contains(invalidResult.Evidence, item => item.Kind == "SUITE_MUTANT_RESULT" &&
            item.Diagnostics?.Any(value => value == "compile-invalid=true") == true);
        Assert.DoesNotContain(invalidResult.Evidence.SelectMany(item => item.Diagnostics ?? []),
            value => value.Contains(Path.GetTempPath(),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));
        Assert.Equal(source, File.ReadAllText(Path.Combine(_repository.Root, "src/App/Flag.cs")));
    }

    [Fact(Timeout = 420_000)]
    public async Task FailedBuildMissingMemberRetainsClassifierProcessTrxAndOmissionEvidence()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        WriteFixture();
        _repository.Git("add", ".");
        _repository.Git("commit", "-m", "failed-build-fixture");
        await using var snapshot = await SnapshotCapture.CaptureAsync(_repository.Root, "HEAD", [],
            SnapshotCaptureOptions.Default, cancellationToken);
        await using var environment = await ExecutionEnvironment.PrepareDependenciesAsync(snapshot,
            ["tests/App.Tests/App.Tests.csproj"],
            DependencyPreparationOptions.Default with { Timeout = TimeSpan.FromMinutes(2) },
            cancellationToken);
        var suite = new SuiteExecution("suite-identity", ["unit"],
            "tests/App.Tests/App.Tests.csproj", "vstest", "net10.0", "Release",
            ["App.Tests.dll"]);
        await using var baseline = await new VstestSuiteExecutor(snapshot, environment)
            .RunBaselineAsync(suite, TimeSpan.FromMinutes(2), cancellationToken);
        Assert.Equal(SuiteRunDisposition.Passed, baseline.Disposition);

        var source = File.ReadAllText(Path.Combine(_repository.Root, "src/App/Flag.cs"));
        var tree = CSharpSyntaxTree.ParseText(SourceText.From(source), path: "src/App/Flag.cs",
            cancellationToken: cancellationToken);
        var root = tree.GetRoot(cancellationToken);
        var token = root.DescendantTokens().Single(item => item.ValueText == "true");
        var mutation = MutationIdentity.Create("src/App/Flag.cs", root, token.Span,
            "literal.boolean", MutationIdentity.OperatorContractVersion, "42");
        var evaluationId = EvaluationUnitIdentity.Compute(new(mutation.MutationId,
            "src/App/App.csproj", "net10.0", "net10-csharp14"));
        var candidate = new MutationCandidate(mutation.MutationId, evaluationId, mutation.Material,
            "src/App/App.csproj", "net10.0", "net10-csharp14", token.SpanStart, token.Span.Length,
            token.Text, 1);
        var executor = new StrictMutationExecutor(snapshot, environment, [candidate], [suite],
            new Dictionary<string, bool>(StringComparer.Ordinal) { [suite.Identity] = true },
            compilerEvidenceEvaluator: (_, _, _, _) =>
                new(false, [], ["classifier-stage=regex-timeout"]));

        var result = await executor.ExecuteAsync(new(candidate.MutationId,
            candidate.EvaluationUnitId, [suite.Identity]), TimeSpan.FromMinutes(2), cancellationToken);
        var diagnostics = Assert.Single(result.Evidence, item => item.Kind == "SUITE_MUTANT_RESULT").Diagnostics!;

        Assert.Equal(UnitDisposition.Error, result.Disposition);
        Assert.Contains("failed-count=0", diagnostics);
        Assert.Contains("diagnostic-count=6", diagnostics);
        Assert.Contains("diagnostic=classifier-stage=regex-timeout", diagnostics);
        Assert.Contains(diagnostics, value => value.StartsWith("diagnostic=process-exit=", StringComparison.Ordinal));
        Assert.Contains("diagnostic=trx-valid=false", diagnostics);
        Assert.Contains("diagnostic=tests-discovered=false", diagnostics);
        Assert.Contains("diagnostic=run-errors=false", diagnostics);
        Assert.Contains(diagnostics, value => value.Contains("Mutant TRX omitted expected member(s): App.Tests.dll.",
            StringComparison.Ordinal));
        Assert.Equal(source, File.ReadAllText(Path.Combine(_repository.Root, "src/App/Flag.cs")));
    }

    [Fact]
    public void SafeFailureEvidenceExposesWrappedClassifierAndProcessFlags()
    {
        var mutationId = "mutation:v1:" + new string('a', 64);
        var aggregate = SuiteCoordinator.AggregateMutant(mutationId, ["suite"],
        [
            new("suite", SuiteRunDisposition.Error, [], [], PriorityDiagnostics:
            [
                "classifier-stage=failed-build",
                "classifier-location-matches=0",
                "process-exit=1",
                "trx-valid=false",
                "tests-discovered=false",
                "run-errors=false"
            ])
        ]);

        var message = SafeClassificationEvidence(aggregate.Evidence);

        Assert.Contains("classifier-location-matches=0", message, StringComparison.Ordinal);
        Assert.Contains("process-exit=1", message, StringComparison.Ordinal);
        Assert.Contains("trx-valid=false", message, StringComparison.Ordinal);
        Assert.Contains("tests-discovered=false", message, StringComparison.Ordinal);
        Assert.Contains("run-errors=false", message, StringComparison.Ordinal);
    }

    [Fact]
    public void ClassifierAndProcessFlagsSurviveTwentyRunDiagnosticsWithinEvidenceLimit()
    {
        var run = new TestRunResult(1, TimeSpan.Zero, false, false, false, false,
            string.Empty, string.Empty, [], true, [],
            Enumerable.Range(1, 20).Select(index => $"run-diagnostic-{index}").ToArray());
        var compile = new CompileEvidence(false, [], ["classifier-stage=not-failed-build"]);
        var method = typeof(StrictMutationExecutor).GetMethod("ToSuiteEvidence",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;

        var suite = Assert.IsType<SuiteMutationEvidence>(method.Invoke(null, ["suite", run, compile]));
        var aggregate = SuiteCoordinator.AggregateMutant("mutation:v1:" + new string('b', 64),
            ["suite"], [suite]);
        var diagnostics = Assert.Single(aggregate.Evidence).Diagnostics!;

        Assert.True(diagnostics.Count <= EvaluationEvidence.MaxDiagnostics);
        Assert.Contains("diagnostic=classifier-stage=not-failed-build", diagnostics);
        Assert.Contains("diagnostic=process-exit=1", diagnostics);
        Assert.Contains("diagnostic=trx-valid=false", diagnostics);
        Assert.Contains("diagnostic=tests-discovered=false", diagnostics);
        Assert.Contains("diagnostic=run-errors=true", diagnostics);
    }

    [Fact]
    public void ErrorAggregationRetainsPriorityFactsAndUsefulFailedIdsWithinEvidenceLimit()
    {
        var failed = Enumerable.Range(1, 25).Select(index => $"Suite.Test{index:D2}").ToArray();
        var aggregate = SuiteCoordinator.AggregateMutant("mutation:v1:" + new string('c', 64),
            ["suite"],
            [new("suite", SuiteRunDisposition.Error, failed, [], PriorityDiagnostics:
            [
                "classifier-stage=failed-build",
                "classifier-location-matches=0",
                "process-exit=1",
                "trx-valid=true",
                "tests-discovered=true",
                "run-errors=true"
            ])]);

        var diagnostics = Assert.Single(aggregate.Evidence).Diagnostics!;

        Assert.True(diagnostics.Count <= EvaluationEvidence.MaxDiagnostics);
        Assert.Contains("diagnostic=classifier-stage=failed-build", diagnostics);
        Assert.Contains("diagnostic=classifier-location-matches=0", diagnostics);
        Assert.Contains("diagnostic=process-exit=1", diagnostics);
        Assert.Contains("diagnostic=trx-valid=true", diagnostics);
        Assert.Contains("diagnostic=tests-discovered=true", diagnostics);
        Assert.Contains("diagnostic=run-errors=true", diagnostics);
        Assert.Contains("failed=Suite.Test01", diagnostics);
        Assert.Contains(diagnostics, value => value.StartsWith("diagnostics-truncated=", StringComparison.Ordinal));
    }

    [Fact]
    public void ErrorAggregationDoesNotPromoteTestControlledDiagnosticPrefixes()
    {
        var aggregate = SuiteCoordinator.AggregateMutant("mutation:v1:" + new string('e', 64),
            ["suite"],
            [new("suite", SuiteRunDisposition.Error, ["Suite.RealFailure"],
                ["process-exit=0"], PriorityDiagnostics: ["process-exit=1", "trx-valid=true"])]);

        var diagnostics = Assert.Single(aggregate.Evidence).Diagnostics!.ToList();
        var failedIndex = diagnostics.IndexOf("failed=Suite.RealFailure");
        var spoofedIndex = diagnostics.IndexOf("diagnostic=process-exit=0");

        Assert.Contains("diagnostic=process-exit=1", diagnostics);
        Assert.True(failedIndex >= 0 && spoofedIndex > failedIndex,
            "Test-controlled TRX text must not receive trusted priority ordering.");
    }

    [Fact]
    public void ExecutorTruncationReportsRealOmittedAndDiagnosticCounts()
    {
        var run = new TestRunResult(1, TimeSpan.Zero, false, false, false, false,
            string.Empty, string.Empty, [], true,
            Enumerable.Range(1, 50).Select(index => $"failed-{index:D2}").ToArray(),
            Enumerable.Range(1, 20).Select(index => $"run-diagnostic-{index}").ToArray(),
            FailedTestCount: 80, DiagnosticCount: 30);
        var compile = new CompileEvidence(false, [], ["classifier-stage=not-failed-build"]);
        var method = typeof(StrictMutationExecutor).GetMethod("ToSuiteEvidence",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;

        var suite = Assert.IsType<SuiteMutationEvidence>(method.Invoke(null, ["suite", run, compile]));
        var aggregate = SuiteCoordinator.AggregateMutant("mutation:v1:" + new string('d', 64),
            ["suite"], [suite]);
        var diagnostics = Assert.Single(aggregate.Evidence).Diagnostics!;

        Assert.Equal(EvaluationEvidence.MaxDiagnostics, suite.Diagnostics.Count);
        Assert.Contains("diagnostics-truncated=11", suite.Diagnostics);
        Assert.Contains("failed-count=80", diagnostics);
        Assert.Contains("diagnostic-count=35", diagnostics);
        Assert.Contains("diagnostic=classifier-stage=not-failed-build", diagnostics);
        Assert.Contains("diagnostic=process-exit=1", diagnostics);
        Assert.Contains("diagnostics-truncated=99", diagnostics);
    }

    [Fact]
    public void PartialBaselineAccountingRetainsCompletedSuitesWithoutForgingMissingOnes()
    {
        var one = new SuiteExecution("one", ["one"], "One.csproj", "vstest", "net10.0",
            "Release", ["One.Tests.dll"]);
        var two = new SuiteExecution("two", ["two"], "Two.csproj", "vstest", "net10.0",
            "Release", ["Two.Tests.dll"]);
        var completed = new SuiteBaselineExecution("snapshot-key", ["one"],
            new(SuiteRunDisposition.Passed, TimeSpan.Zero, ["One.Tests.dll"], [], [], []));

        var mapped = StrictExecutionPipeline.MapBaselineExecutions([one, two], [completed]);

        Assert.Same(completed, mapped["one"]);
        Assert.False(mapped.ContainsKey("two"));
    }

    [Fact]
    public void BaselineSuiteEvidencePublishesBoundedRunAndMemberDiagnostics()
    {
        var suite = new SuiteExecution("one", ["one"], "One.csproj", "vstest", "net10.0",
            "Release", ["One.Tests.dll"]);
        var run = new SuiteRunResult(SuiteRunDisposition.Error, TimeSpan.Zero, [], [],
            Enumerable.Range(1, 25).Select(index => $"baseline-read-error-{index}").ToArray(), []);
        var execution = new SuiteBaselineExecution("snapshot-key", ["one"], run);
        var method = typeof(StrictExecutionPipeline).GetMethod("ToSuiteEvidence",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;

        var suites = Assert.IsAssignableFrom<IReadOnlyList<SuiteEvidence>>(method.Invoke(null,
            [new[] { suite }, new Dictionary<string, SuiteBaselineExecution>(StringComparer.Ordinal)
                { [suite.Identity] = execution }]))!;

        var evidence = Assert.Single(Assert.Single(suites).Evidence);
        Assert.True(evidence.Diagnostics!.Count <= EvaluationEvidence.MaxDiagnostics);
        Assert.Contains(evidence.Diagnostics, item => item == "accountedMembers=<none>");
        Assert.Contains(evidence.Diagnostics, item => item == "expectedMembers=One.Tests.dll");
        Assert.Contains(evidence.Diagnostics, item => item.StartsWith("baseline-read-error-",
            StringComparison.Ordinal));
        Assert.Contains(evidence.Diagnostics, item => item.StartsWith("diagnostics-truncated=",
            StringComparison.Ordinal));
    }

    [Fact]
    public void StrictExecutionRemainsSerialUntilParallelIsolationIsVerified() =>
        Assert.Equal(1, StrictExecutionPipeline.MaxStrictWorkers);

    [Fact(Timeout = 420_000)]
    public async Task CoordinatorFinalizesRealAllKilledRunAsPass()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        WriteFixture();
        _repository.WriteText("src/App/Flag.cs", """
            namespace App;
            public static class Flag { public static bool Value() => false; }
            """);
        _repository.Git("add", ".");
        _repository.Git("commit", "-m", "baseline");
        _repository.WriteText("src/App/Flag.cs", """
            namespace App;
            public static class Flag { public static bool Value() => true; }
            """);
        var original = File.ReadAllBytes(Path.Combine(_repository.Root, "src/App/Flag.cs"));
        var reportPath = Path.Combine(Path.GetTempPath(), $"strict-execution-{Guid.NewGuid():N}.json");
        var previous = Environment.CurrentDirectory;
        Environment.CurrentDirectory = _repository.Root;
        try
        {
            var result = await new EvaluationCoordinator().RunAsync(
                new(false, "HEAD", [], reportPath, "strict-execution", NoState: true),
                cancellationToken);

            Assert.True(result.Report.Outcome == EvaluationOutcome.Pass,
                string.Join("; ", result.Report.Reasons.Select(item => item.Code + ": " + item.Message)
                    .Concat(result.Report.Evidence.Select(item => item.Kind + ": " + item.Summary))
                    .Concat(result.Report.Suites.SelectMany(suite => suite.Evidence)
                        .SelectMany(item => item.Diagnostics ?? []).Select(item => item
                            .Replace(_repository.Root, "<fixture>", StringComparison.OrdinalIgnoreCase)
                            .Replace(Path.GetTempPath(), "<temp>/", StringComparison.OrdinalIgnoreCase)))));
            Assert.Equal(0, result.Report.ExitCode);
            Assert.Equal(BaselineStatus.Green, result.Report.Baseline);
            Assert.NotNull(result.Report.Counts.Enumerated);
            Assert.True(result.Report.Counts.Executed > 0);
            Assert.True(result.Report.Counts.Killed > 0);
            Assert.Contains(result.Report.Units, item => item.Disposition == UnitDisposition.Killed);
            Assert.Empty(result.Report.IncompleteConditions);
            Assert.DoesNotContain(result.Report.IncompleteConditions,
                item => item.Code == "EXECUTION_NOT_IMPLEMENTED");
            Assert.Single(result.Report.Suites);
            Assert.Equal(BaselineStatus.Green, result.Report.Suites[0].Baseline);
            var snapshotEvidence = Assert.Single(result.Report.Evidence,
                item => item.Kind == "INPUT_SNAPSHOT");
            Assert.Contains(snapshotEvidence.Diagnostics ?? [], value =>
                value.StartsWith("dependencyFingerprint=sha256:", StringComparison.Ordinal));
            Assert.DoesNotContain(snapshotEvidence.Diagnostics ?? [], value =>
                value == "dependencyFingerprint=not-prepared");
            Assert.Equal(original, File.ReadAllBytes(Path.Combine(_repository.Root, "src/App/Flag.cs")));
            Assert.False(Directory.Exists(Path.Combine(_repository.Root, "obj")));
            Assert.False(Directory.Exists(Path.Combine(_repository.Root, "bin")));
            Assert.False(Directory.Exists(Path.Combine(_repository.Root, ".mutate4csharp")));
        }
        finally
        {
            Environment.CurrentDirectory = previous;
            try { File.Delete(reportPath); } catch { }
            try { File.Delete(ReportWriter.LockPath(reportPath)); } catch { }
        }
    }

    [Theory(Timeout = 420_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CoordinatorPublishesDiscoveryThenProvenForEligibleRealPass(bool failProvenPublication)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        WriteFixture();
        _repository.WriteText("src/App/Flag.cs", """
            namespace App;
            public static class Flag { public static bool Value() => false; }
            """);
        _repository.Git("add", ".");
        _repository.Git("commit", "-m", "baseline");
        _repository.WriteText("src/App/Flag.cs", """
            namespace App;
            public static class Flag { public static bool Value() => true; }
            """);
        var reportPath = Path.Combine(Path.GetTempPath(), $"strict-proven-{Guid.NewGuid():N}.json");
        var previous = Environment.CurrentDirectory;
        Environment.CurrentDirectory = _repository.Root;
        try
        {
            var result = await new EvaluationCoordinator(beforePublication: phase =>
            {
                if (failProvenPublication && phase == EvaluationPublicationPhase.Proven)
                    throw new IOException("Injected real coordinator proof publication failure.");
            }).RunAsync(
                new(false, "HEAD", [], reportPath, "strict-proven"), cancellationToken);

            Assert.Equal(failProvenPublication ? EvaluationOutcome.Incomplete : EvaluationOutcome.Pass,
                result.Report.Outcome);
            Assert.True(result.Report.Counts.Executed > 0);
            Assert.True(result.Report.Counts.Killed > 0);
            var fingerprint = Assert.Single(result.Report.Evidence,
                    item => item.Kind == "MUTATION_PLAN").Diagnostics!
                .Single(value => value.StartsWith("evaluationFingerprint=", StringComparison.Ordinal))[22..];
            var store = new SidecarStore(_repository.Root);
            var proven = store.ReadProvenForInspection(fingerprint);
            Assert.Equal(!failProvenPublication, proven.IsValid);
            if (!failProvenPublication) Assert.Equal(result.Report.RunId, proven.Record!.RunId);
            Assert.Equal(!failProvenPublication, File.Exists(store.ProvenPath(fingerprint)));
            Assert.Single(Directory.EnumerateFiles(
                Path.Combine(_repository.Root, ".mutate4csharp", "discovery"), "*.json"));
        }
        finally
        {
            Environment.CurrentDirectory = previous;
            try { File.Delete(reportPath); } catch { }
            try { File.Delete(ReportWriter.LockPath(reportPath)); } catch { }
        }
    }

    [Fact(Timeout = 420_000)]
    public async Task CoordinatorFinalizesRealSurvivorRunAsFail()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        WriteFixture();
        _repository.WriteText("tests/App.Tests/FlagTests.cs", """
            using Xunit;
            public sealed class FlagTests
            {
                [Fact] public void WeakTest() => Assert.True(true);
            }
            """);
        _repository.WriteText("src/App/Flag.cs", """
            namespace App;
            public static class Flag { public static bool Value() => false; }
            """);
        _repository.Git("add", ".");
        _repository.Git("commit", "-m", "baseline");
        _repository.WriteText("src/App/Flag.cs", """
            namespace App;
            public static class Flag { public static bool Value() => true; }
            """);
        var reportPath = Path.Combine(Path.GetTempPath(), $"strict-survivor-{Guid.NewGuid():N}.json");
        var previous = Environment.CurrentDirectory;
        Environment.CurrentDirectory = _repository.Root;
        try
        {
            var result = await new EvaluationCoordinator().RunAsync(
                new(false, "HEAD", [], reportPath, "strict-survivor", NoState: true),
                cancellationToken);

            Assert.Equal(EvaluationOutcome.Fail, result.Report.Outcome);
            Assert.Equal(3, result.Report.ExitCode);
            Assert.Equal(BaselineStatus.Green, result.Report.Baseline);
            Assert.NotNull(result.Report.Counts.Enumerated);
            Assert.True(result.Report.Counts.Executed > 0);
            Assert.True(result.Report.Counts.Survived > 0);
            Assert.Contains(result.Report.Units, item => item.Disposition == UnitDisposition.Survived);
            Assert.Empty(result.Report.IncompleteConditions);
        }
        finally
        {
            Environment.CurrentDirectory = previous;
            try { File.Delete(reportPath); } catch { }
            try { File.Delete(ReportWriter.LockPath(reportPath)); } catch { }
        }
    }

    [Theory(Timeout = 420_000)]
    [InlineData("uncovered")]
    [InlineData("compile-invalid")]
    [InlineData("zero-site")]
    public async Task CoordinatorAccountsForRealUncoveredCompileInvalidAndZeroSiteWork(string scenario)
    {
        WriteFixture();
        _repository.Git("add", ".");
        _repository.Git("commit", "-m", "baseline");
        var source = scenario switch
        {
            "uncovered" => """
                namespace App;
                public static class Flag
                {
                    public static bool Covered(bool initial) => initial;
                    public static bool Value() => true;
                }
                """,
            "compile-invalid" => """
                namespace App;
                public static class Flag
                {
                    public static bool Value(bool initial)
                    {
                        bool value;
                        return initial && (value = initial) ? value : initial;
                    }
                }
                """,
            _ => """
                namespace App;
                public static class Flag { public static bool Value(bool initial) => initial; }
                """
        };
        _repository.WriteText("src/App/Flag.cs", source);
        _repository.WriteText("tests/App.Tests/FlagTests.cs", scenario == "uncovered" ? """
            using App;
            using Xunit;
            public sealed class FlagTests { [Fact] public void CoversOnlyOtherMethod() => Assert.True(Flag.Covered(true)); }
            """ : """
            using App;
            using Xunit;
            public sealed class FlagTests
            {
                [Fact] public void TrueInput() => Assert.True(Flag.Value(true));
                [Fact] public void FalseInput() => Assert.False(Flag.Value(false));
            }
            """);
        var report = Path.Combine(Path.GetTempPath(), $"strict-{scenario}-{Guid.NewGuid():N}.json");
        var previous = Environment.CurrentDirectory;
        Environment.CurrentDirectory = _repository.Root;
        try
        {
            var result = await new EvaluationCoordinator().RunAsync(
                new(false, "HEAD", [], report, scenario, NoState: true), TestContext.Current.CancellationToken);
            Assert.Equal(BaselineStatus.Green, result.Report.Baseline);
            Assert.Empty(result.Report.IncompleteConditions);
            if (scenario == "uncovered")
            {
                Assert.Equal(EvaluationOutcome.Fail, result.Report.Outcome);
                Assert.Equal(3, result.Report.ExitCode);
                Assert.True(result.Report.Counts.FreshUncovered == 1,
                    string.Join("; ", result.Report.Suites.SelectMany(item => item.Evidence)
                        .SelectMany(item => item.Diagnostics ?? [])));
                Assert.Equal(0, result.Report.Counts.Executed);
            }
            else
            {
                Assert.Equal(EvaluationOutcome.NotApplicable, result.Report.Outcome);
                Assert.Equal(5, result.Report.ExitCode);
                Assert.Equal(0, result.Report.Counts.Killed);
                Assert.Equal(scenario == "compile-invalid" ? 1 : 0, result.Report.Counts.CompileInvalid);
                Assert.Equal(scenario == "compile-invalid" ? 1 : 0, result.Report.Counts.Enumerated);
            }
            Assert.False(Directory.Exists(Path.Combine(_repository.Root, ".mutate4csharp")));
        }
        finally
        {
            Environment.CurrentDirectory = previous;
            File.Delete(report);
            File.Delete(ReportWriter.LockPath(report));
        }
    }

    [Fact(Timeout = 420_000)]
    public async Task CoordinatorPublishesRedBaselineExitTwoAndOmitsEverySelectedMutation()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        WriteFixture();
        _repository.Git("add", ".");
        _repository.Git("commit", "-m", "baseline");
        _repository.WriteText("src/App/Flag.cs", """
            namespace App;
            public static class Flag { public static bool Value() => false; }
            """);
        var reportPath = Path.Combine(Path.GetTempPath(), $"strict-red-{Guid.NewGuid():N}.json");
        var previous = Environment.CurrentDirectory;
        Environment.CurrentDirectory = _repository.Root;
        try
        {
            var result = await new EvaluationCoordinator().RunAsync(
                new(false, "HEAD", [], reportPath, "strict-red", NoState: true), cancellationToken);

            Assert.Equal(EvaluationOutcome.Incomplete, result.Report.Outcome);
            Assert.Equal(2, result.Report.ExitCode);
            Assert.Equal(BaselineStatus.Red, result.Report.Baseline);
            Assert.NotEmpty(result.Report.Units);
            Assert.All(result.Report.Units, unit =>
            {
                Assert.Equal(UnitDisposition.Omitted, unit.Disposition);
                Assert.Contains(unit.Evidence, item => item.Kind == "BASELINE_NOT_GREEN");
            });
            Assert.Empty(result.Report.IncompleteConditions);
        }
        finally
        {
            Environment.CurrentDirectory = previous;
            try { File.Delete(reportPath); } catch { }
            try { File.Delete(ReportWriter.LockPath(reportPath)); } catch { }
        }
    }

    public void Dispose() => _repository.Dispose();

    private void WriteFixture()
    {
        _repository.WriteText("src/App/App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework><Nullable>enable</Nullable></PropertyGroup>
            </Project>
            """);
        _repository.WriteText("src/App/Flag.cs", """
            namespace App;
            public static class Flag { public static bool Value() => true; }
            """);
        _repository.WriteText("tests/App.Tests/App.Tests.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework><IsTestProject>true</IsTestProject></PropertyGroup>
              <ItemGroup><ProjectReference Include="../../src/App/App.csproj" /></ItemGroup>
              <ItemGroup>
                <PackageReference Include="Microsoft.NET.Test.Sdk" Version="18.3.0" />
                <PackageReference Include="xunit.v3" Version="3.2.2" />
                <PackageReference Include="xunit.runner.visualstudio" Version="3.1.5" />
                <PackageReference Include="coverlet.collector" Version="6.0.4" />
              </ItemGroup>
            </Project>
            """);
        _repository.WriteText("tests/App.Tests/FlagTests.cs", """
            using App;
            using Xunit;
            public sealed class FlagTests
            {
                [Fact] public void ValueIsTrue() => Assert.True(Flag.Value());
            }
            """);
        _repository.WriteText("NuGet.Config", """
            <configuration><packageSources><clear /><add key="fixture" value="local-packages" /></packageSources></configuration>
            """);
        _repository.WriteText("mutate4csharp.json", """
            {
              "version": 1,
              "projects": [{
                "id": "app",
                "project": "src/App/App.csproj",
                "targetFramework": "net10.0",
                "parseContext": "net10-csharp14",
                "languageVersion": "14.0",
                "nullable": "enable",
                "defineConstants": [],
                "sources": ["src/App/**/*.cs"],
                "testSuites": ["unit"]
              }],
              "testSuites": [{
                "id": "unit",
                "path": "tests/App.Tests/App.Tests.csproj",
                "runner": "vstest",
                "framework": "net10.0",
                "configuration": "Release",
                "expectedMembers": ["App.Tests.dll"]
              }],
              "policy": {
                "maxWorkers": 1,
                "mutationCap": 100,
                "baselineTimeoutSeconds": 120,
                "mutantTimeoutSeconds": 120,
                "overallDeadlineSeconds": 300,
                "allowNotApplicable": false,
                "stabilityRepetitions": 1
              }
            }
            """);
        CopyRestoredTestPackages(Path.Combine(_repository.Root, "local-packages"));
    }

    private static void CopyRestoredTestPackages(string destination)
    {
        var assetsPath = Path.GetFullPath("../../../obj/project.assets.json", AppContext.BaseDirectory);
        using var assets = JsonDocument.Parse(File.ReadAllBytes(assetsPath));
        var packageRoots = assets.RootElement.GetProperty("packageFolders").EnumerateObject()
            .Select(folder => Path.GetFullPath(folder.Name)).ToArray();
        Directory.CreateDirectory(destination);
        foreach (var library in assets.RootElement.GetProperty("libraries").EnumerateObject())
        {
            if (library.Value.GetProperty("type").GetString() != "package") continue;
            var separator = library.Name.LastIndexOf('/');
            var id = library.Name[..separator].ToLowerInvariant();
            var version = library.Name[(separator + 1)..].ToLowerInvariant();
            var relativePackagePath = library.Value.TryGetProperty("path", out var path)
                ? path.GetString()?.Replace('/', Path.DirectorySeparatorChar)
                : Path.Combine(id, version);
            if (string.IsNullOrWhiteSpace(relativePackagePath))
                throw new InvalidOperationException($"Restored package {library.Name} did not declare a path.");
            var archive = $"{id}.{version}.nupkg";
            var matches = packageRoots.Select(root => Path.Combine(root, relativePackagePath, archive))
                .Where(File.Exists).ToArray();
            if (matches.Length != 1)
                throw new InvalidOperationException($"Expected one restored archive for {library.Name}.");
            File.Copy(matches[0], Path.Combine(destination, archive));
        }
    }

    private static string SafeClassificationEvidence(IReadOnlyList<EvaluationEvidence> evidence) =>
        string.Join("; ", evidence.Select(item =>
            $"{item.Kind}[{string.Join(",", (item.Diagnostics ?? [])
                .Select(value => value.StartsWith("diagnostic=", StringComparison.Ordinal)
                    ? value["diagnostic=".Length..]
                    : value)
                .Where(value =>
                value.StartsWith("failed-count=", StringComparison.Ordinal) ||
                value.StartsWith("diagnostic-count=", StringComparison.Ordinal) ||
                value.StartsWith("compile-invalid=", StringComparison.Ordinal) ||
                value.StartsWith("classifier-", StringComparison.Ordinal) ||
                value.StartsWith("process-exit=", StringComparison.Ordinal) ||
                value.StartsWith("trx-valid=", StringComparison.Ordinal) ||
                value.StartsWith("tests-discovered=", StringComparison.Ordinal) ||
                value.StartsWith("run-errors=", StringComparison.Ordinal)))}]"));
}
