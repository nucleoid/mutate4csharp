namespace Mutate4CSharp.Tests;

public sealed class SuiteCoordinatorTests
{
    [Fact]
    public void CleanupCombinationPreservesIntegrityFailureAndTypesCleanupOnlyFailure()
    {
        var integrity = new SnapshotDivergedException("frozen input changed");
        var cleanup = new IOException("owned cleanup failed");

        var combined = Assert.IsType<SnapshotCleanupException>(
            AsyncDisposal.CombineFailure(integrity, cleanup));
        var cleanupOnly = Assert.IsType<SnapshotCleanupException>(
            AsyncDisposal.CombineFailure(null, cleanup));

        Assert.Same(integrity, combined.OriginalFailure);
        Assert.Same(cleanup, combined.CleanupFailure);
        Assert.Null(cleanupOnly.OriginalFailure);
        Assert.Same(cleanup, cleanupOnly.CleanupFailure);
    }

    [Fact]
    public async Task OwnedDisposalPreservesPrimaryIntegrityFailureWhenCleanupAlsoFails()
    {
        var integrity = new ExecutionBoundaryIntegrityException("private package root changed");
        var cleanup = new IOException("owned cleanup failed");

        var combined = await Assert.ThrowsAsync<SnapshotCleanupException>(async () =>
            await AsyncDisposal.DisposeAllPreservingFailureAsync(
                [new ThrowingOwner(cleanup)], integrity));

        Assert.Same(integrity, combined.OriginalFailure);
        Assert.Same(cleanup, combined.CleanupFailure);
    }

    [Fact]
    public async Task SharedSuiteAliasesRunOneFreshBaselineForTheSnapshot()
    {
        var calls = 0;
        var executor = new DelegateSuiteExecutor((_, _, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(Green());
        });
        var coordinator = new SuiteCoordinator(executor, TimeProvider.System);
        var suites = new[]
        {
            Suite("a", "tests/App.Tests/App.Tests.csproj"),
            Suite("alias", "tests/App.Tests/App.Tests.csproj")
        };

        var result = await coordinator.RunBaselinesAsync("snapshot:one", suites,
            TimeSpan.FromSeconds(10), DateTimeOffset.UtcNow.AddMinutes(1), CancellationToken.None);

        Assert.Equal(1, calls);
        Assert.Equal(BaselineStatus.Green, result.Status);
        Assert.Equal(["a", "alias"], Assert.Single(result.Executions).SuiteIds);
    }

    [Fact]
    public async Task BaselinesAreFreshAcrossSnapshotsAndAllDistinctSuitesMustBeGreen()
    {
        var calls = 0;
        var executor = new DelegateSuiteExecutor((suite, _, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(suite.Path.Contains("Bad", StringComparison.Ordinal) ? Red() : Green());
        });
        var coordinator = new SuiteCoordinator(executor, TimeProvider.System);
        var suites = new[] { Suite("good", "Good.csproj"), Suite("bad", "Bad.csproj") };

        var first = await coordinator.RunBaselinesAsync("snapshot:one", suites,
            TimeSpan.FromSeconds(10), DateTimeOffset.UtcNow.AddMinutes(1), CancellationToken.None);
        var second = await coordinator.RunBaselinesAsync("snapshot:two", [suites[0]],
            TimeSpan.FromSeconds(10), DateTimeOffset.UtcNow.AddMinutes(1), CancellationToken.None);

        Assert.Equal(3, calls);
        Assert.Equal(BaselineStatus.Red, first.Status);
        Assert.Equal(BaselineStatus.Green, second.Status);
        Assert.NotEqual(first.Executions[0].Identity, second.Executions[0].Identity);
    }

    [Fact]
    public async Task MissingExpectedTrxMembersFailsClosed()
    {
        var executor = new DelegateSuiteExecutor((_, _, _) => Task.FromResult(
            Green() with { AccountedMembers = ["Other.Tests.dll"] }));

        var result = await new SuiteCoordinator(executor, TimeProvider.System).RunBaselinesAsync(
            "snapshot", [Suite("unit", "App.Tests.csproj")], TimeSpan.FromSeconds(10),
            DateTimeOffset.UtcNow.AddMinutes(1), CancellationToken.None);

        Assert.Equal(BaselineStatus.Unknown, result.Status);
        Assert.Contains(result.IncompleteConditions, item => item.Code == "SUITE_MEMBERS_MISSING");
    }

    [Fact]
    public async Task BaselineSnapshotIntegrityFailuresEscapeSuiteClassification()
    {
        var executor = new DelegateSuiteExecutor((_, _, _) =>
            throw new SnapshotDivergedException("frozen package cache changed"));

        await Assert.ThrowsAsync<SnapshotDivergedException>(() =>
            new SuiteCoordinator(executor, TimeProvider.System).RunBaselinesAsync(
                "snapshot", [Suite("unit", "App.Tests.csproj")], TimeSpan.FromSeconds(10),
                DateTimeOffset.UtcNow.AddMinutes(1), CancellationToken.None));
    }

    [Fact]
    public async Task OrdinaryBaselineRestoreFailuresRemainInconclusiveEvidence()
    {
        var executor = new DelegateSuiteExecutor((_, _, _) =>
            throw new SnapshotCaptureException("Frozen worker restore failed: feed unavailable"));

        var result = await new SuiteCoordinator(executor, TimeProvider.System).RunBaselinesAsync(
            "snapshot", [Suite("unit", "App.Tests.csproj")], TimeSpan.FromSeconds(10),
            DateTimeOffset.UtcNow.AddMinutes(1), CancellationToken.None);

        Assert.Equal(BaselineStatus.Unknown, result.Status);
        Assert.Equal(SuiteRunDisposition.Error, Assert.Single(result.Executions).Result.Disposition);
        Assert.Contains(result.IncompleteConditions, item => item.Code == "BASELINE_INCONCLUSIVE");
    }

    [Theory]
    [InlineData(3, 0, 0)]
    [InlineData(4, 0, 1)]
    [InlineData(3, 6, 4)]
    [InlineData(3, 7, 4)]
    public void AggregateCannotHideIncompleteOrFailingSuites(int firstValue,
        int secondValue, int expectedValue)
    {
        var first = (SuiteRunDisposition)firstValue;
        var second = (SuiteRunDisposition)secondValue;
        var expected = (UnitDisposition)expectedValue;
        var result = SuiteCoordinator.AggregateMutant("mutation:v1:" + new string('a', 64),
            ["one", "two"], [Mutant("one", first), Mutant("two", second)]);

        Assert.Equal(expected, result.Disposition);
        Assert.Equal(2, result.Evidence.Count);
    }

    [Fact]
    public void CompileInvalidRequiresMatchingEvidenceFromEveryMappedSuite()
    {
        var mutationId = "mutation:v1:" + new string('d', 64);

        var complete = SuiteCoordinator.AggregateMutant(mutationId, ["one", "two"],
        [
            new("one", SuiteRunDisposition.Error, [], ["CS0029 at src/App/A.cs"], true),
            new("two", SuiteRunDisposition.Error, [], ["CS0029 at src/App/A.cs"], true)
        ]);
        var contradictory = SuiteCoordinator.AggregateMutant(mutationId, ["one", "two"],
        [
            new("one", SuiteRunDisposition.Error, [], ["CS0029 at src/App/A.cs"], true),
            Mutant("two", SuiteRunDisposition.Survived)
        ]);

        Assert.Equal(UnitDisposition.CompileInvalid, complete.Disposition);
        Assert.Equal(UnitDisposition.Error, contradictory.Disposition);
    }

    [Fact]
    public async Task DeadlineUsesRemainingTimeAndCancellationStopsLaterBaselines()
    {
        using var cancel = new CancellationTokenSource();
        var calls = 0;
        var executor = new DelegateSuiteExecutor((_, _, _) =>
        {
            if (Interlocked.Increment(ref calls) == 1) cancel.Cancel();
            return Task.FromResult(Green());
        });

        var result = await new SuiteCoordinator(executor, TimeProvider.System).RunBaselinesAsync("snapshot",
            [Suite("one", "One.csproj"), Suite("two", "Two.csproj")], TimeSpan.FromMinutes(10),
            DateTimeOffset.UtcNow.AddSeconds(30), cancel.Token);

        Assert.Equal(1, calls);
        Assert.Equal(BaselineStatus.Unknown, result.Status);
        Assert.Contains(result.IncompleteConditions, item => item.Code == "EXECUTION_CANCELLED");
        Assert.InRange(executor.ObservedTimeouts[0], TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void SurvivorEvidenceIsBoundedAndProvidesExactIdRerunArguments()
    {
        var mutationId = "mutation:v1:" + new string('b', 64);
        var evidence = SuiteCoordinator.SurvivorEvidence(mutationId, "src/App/A.cs", 12,
            "true", "false", "sha256:" + new string('c', 64));

        Assert.Equal("SURVIVOR", evidence.Kind);
        Assert.Contains("src/App/A.cs:12", evidence.Summary, StringComparison.Ordinal);
        var diagnostic = Assert.Single(evidence.Diagnostics!);
        Assert.StartsWith("rerun-argv-json=", diagnostic, StringComparison.Ordinal);
        Assert.Contains(mutationId, diagnostic, StringComparison.Ordinal);
        Assert.True(evidence.Summary.Length <= 512);
    }

    private static SuiteExecution Suite(string id, string path) => new(id, [id], path, "vstest", "net10.0",
        "Release", ["App.Tests.dll"]);

    private static SuiteRunResult Green() => new(SuiteRunDisposition.Passed, TimeSpan.FromMilliseconds(1),
        ["App.Tests.dll"], [], [], ["coverage.opencover.xml"]);
    private static SuiteRunResult Red() => new(SuiteRunDisposition.Failed, TimeSpan.FromMilliseconds(1),
        ["App.Tests.dll"], ["Example.Fails"], [], []);
    private static SuiteMutationEvidence Mutant(string id, SuiteRunDisposition disposition) =>
        new(id, disposition, disposition == SuiteRunDisposition.Failed ? ["Example.Fails"] : [], []);

    private sealed class DelegateSuiteExecutor : ISuiteExecutor
    {
        private readonly Func<SuiteExecution, TimeSpan, CancellationToken, Task<SuiteRunResult>> _run;
        public List<TimeSpan> ObservedTimeouts { get; } = [];

        public DelegateSuiteExecutor(Func<SuiteExecution, TimeSpan, CancellationToken, Task<SuiteRunResult>> run) =>
            _run = run;

        public Task<SuiteRunResult> RunBaselineAsync(SuiteExecution suite, TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            ObservedTimeouts.Add(timeout);
            return _run(suite, timeout, cancellationToken);
        }
    }

    private sealed class ThrowingOwner(Exception failure) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.FromException(failure);
    }
}
