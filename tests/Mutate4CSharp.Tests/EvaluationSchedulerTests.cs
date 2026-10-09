namespace Mutate4CSharp.Tests;

public sealed class EvaluationSchedulerTests
{
    [Fact]
    public async Task SnapshotIntegrityFailuresEscapeUnitClassification()
    {
        var executor = new DelegateMutationExecutor((_, _, _) =>
            throw new SnapshotDivergedException("frozen input changed"));
        var scheduler = new EvaluationScheduler(executor, TimeProvider.System);

        await Assert.ThrowsAsync<SnapshotDivergedException>(() => scheduler.RunAsync(
            [new ScheduledMutation("mutation:v1:" + new string('1', 64),
                "evaluation:v1:" + new string('2', 64), ["unit"])],
            1, TimeSpan.FromSeconds(10), DateTimeOffset.UtcNow.AddMinutes(1),
            CancellationToken.None));
    }

    [Fact]
    public async Task OrdinaryWorkerRestoreFailuresRemainUnitErrors()
    {
        var executor = new DelegateMutationExecutor((_, _, _) =>
            throw new SnapshotCaptureException("Frozen worker restore failed: feed unavailable"));

        var result = await new EvaluationScheduler(executor, TimeProvider.System).RunAsync(
            [new ScheduledMutation("mutation:v1:" + new string('3', 64),
                "evaluation:v1:" + new string('4', 64), ["unit"])],
            1, TimeSpan.FromSeconds(10), DateTimeOffset.UtcNow.AddMinutes(1),
            CancellationToken.None);

        var unit = Assert.Single(result.Results);
        Assert.Equal(UnitDisposition.Error, unit.Disposition);
        Assert.Contains(unit.Evidence, item => item.Kind == "MUTANT_EXECUTION_INCOMPLETE");
    }

    [Fact]
    public async Task StrictStabilityAttemptsAreContiguousPerEvaluationUnit()
    {
        var calls = new List<string>();
        var executor = new DelegateMutationExecutor((work, _, _) =>
        {
            calls.Add(work.EvaluationUnitId);
            return Task.FromResult(new ScheduledMutationResult(work.EvaluationUnitId,
                UnitDisposition.Killed, [new("KILLED", "fixture")]));
        });
        var first = new ScheduledMutation("mutation:v1:" + new string('5', 64),
            "evaluation:v1:" + new string('5', 64), ["unit"]);
        var second = new ScheduledMutation("mutation:v1:" + new string('6', 64),
            "evaluation:v1:" + new string('6', 64), ["unit"]);

        var result = await StrictExecutionPipeline.RunContiguousAttemptsAsync(executor,
            [first, second], 2, TimeSpan.FromSeconds(10), DateTimeOffset.UtcNow.AddMinutes(1),
            TimeProvider.System, CancellationToken.None);

        Assert.Equal([first.EvaluationUnitId, first.EvaluationUnitId,
            second.EvaluationUnitId, second.EvaluationUnitId], calls);
        Assert.Equal(2, result.Attempts[first.EvaluationUnitId].Count);
        Assert.Equal(2, result.Attempts[second.EvaluationUnitId].Count);
    }

    [Fact]
    public async Task ContiguousAttemptsRetainCompletedEvidenceBeforeCancellationOmission()
    {
        using var cancel = new CancellationTokenSource();
        var executor = new DelegateMutationExecutor((work, _, _) =>
        {
            cancel.Cancel();
            return Task.FromResult(new ScheduledMutationResult(work.EvaluationUnitId,
                UnitDisposition.Killed, [new("KILLED", "completed before cancellation")]));
        });
        var first = new ScheduledMutation("mutation:v1:" + new string('7', 64),
            "evaluation:v1:" + new string('7', 64), ["unit"]);
        var second = new ScheduledMutation("mutation:v1:" + new string('8', 64),
            "evaluation:v1:" + new string('8', 64), ["unit"]);

        var result = await StrictExecutionPipeline.RunContiguousAttemptsAsync(executor,
            [first, second], 2, TimeSpan.FromSeconds(10), DateTimeOffset.UtcNow.AddMinutes(1),
            TimeProvider.System, cancel.Token);

        Assert.Equal([UnitDisposition.Killed, UnitDisposition.Omitted],
            result.Attempts[first.EvaluationUnitId].Select(item => item.Disposition));
        Assert.Equal(UnitDisposition.Omitted,
            Assert.Single(result.Attempts[second.EvaluationUnitId]).Disposition);
        Assert.Contains(result.IncompleteConditions, item => item.Code == "EXECUTION_CANCELLED");
    }

    [Fact]
    public async Task DeadlineClampedFinalRepetitionIsOmittedInsteadOfUnstable()
    {
        var time = new AdjustableTimeProvider(DateTimeOffset.UtcNow);
        var calls = 0;
        var mutation = new ScheduledMutation("mutation:v1:" + new string('9', 64),
            "evaluation:v1:" + new string('9', 64), ["unit"]);
        var executor = new DelegateMutationExecutor((work, _, _) =>
        {
            if (++calls == 2) time.Advance(TimeSpan.FromSeconds(6));
            return Task.FromResult(new ScheduledMutationResult(work.EvaluationUnitId,
                UnitDisposition.Killed, [new("KILLED", "fixture")]));
        });

        var result = await StrictExecutionPipeline.RunContiguousAttemptsAsync(executor,
            [mutation], 2, TimeSpan.FromSeconds(10), time.GetUtcNow().AddSeconds(5), time,
            CancellationToken.None);

        Assert.Equal([UnitDisposition.Killed, UnitDisposition.Omitted],
            result.Attempts[mutation.EvaluationUnitId].Select(item => item.Disposition));
        Assert.Contains(result.IncompleteConditions, item => item.Code == "OVERALL_DEADLINE_EXCEEDED");
    }

    [Fact]
    public async Task CancellationDuringAssignedAttemptIsOmittedWithRunCondition()
    {
        using var cancel = new CancellationTokenSource();
        var mutation = new ScheduledMutation("mutation:v1:" + new string('a', 64),
            "evaluation:v1:" + new string('a', 64), ["unit"]);
        var executor = new DelegateMutationExecutor((_, _, _) =>
        {
            cancel.Cancel();
            throw new OperationCanceledException(cancel.Token);
        });

        var result = await new EvaluationScheduler(executor, TimeProvider.System).RunAsync(
            [mutation], 1, TimeSpan.FromSeconds(10), DateTimeOffset.UtcNow.AddMinutes(1), cancel.Token);

        Assert.Equal(UnitDisposition.Omitted, Assert.Single(result.Results).Disposition);
        Assert.Contains(result.IncompleteConditions, item => item.Code == "EXECUTION_CANCELLED");
    }

    [Fact]
    public async Task WorkerTimeoutExceptionUsesMutantTimeoutEvidence()
    {
        var mutation = new ScheduledMutation("mutation:v1:" + new string('b', 64),
            "evaluation:v1:" + new string('b', 64), ["unit"]);
        var executor = new DelegateMutationExecutor((_, _, _) =>
            throw new TimeoutException("restore timed out"));

        var result = await new EvaluationScheduler(executor, TimeProvider.System).RunAsync(
            [mutation], 1, TimeSpan.FromSeconds(10), DateTimeOffset.UtcNow.AddMinutes(1),
            CancellationToken.None);

        var unit = Assert.Single(result.Results);
        Assert.Equal(UnitDisposition.Error, unit.Disposition);
        Assert.Contains(unit.Evidence, item => item.Kind == "MUTANT_TIMEOUT");
    }

    [Fact]
    public async Task SchedulerBoundsEvidenceToStabilityContractLimit()
    {
        var mutation = new ScheduledMutation("mutation:v1:" + new string('c', 64),
            "evaluation:v1:" + new string('c', 64), ["unit"]);
        var executor = new DelegateMutationExecutor((work, _, _) => Task.FromResult(
            new ScheduledMutationResult(work.EvaluationUnitId, UnitDisposition.Killed,
                Enumerable.Range(0, 25).Select(index =>
                    new EvaluationEvidence("EVIDENCE", $"entry {index}")).ToArray())));

        var result = await new EvaluationScheduler(executor, TimeProvider.System).RunAsync(
            [mutation], 1, TimeSpan.FromSeconds(10), DateTimeOffset.UtcNow.AddMinutes(1),
            CancellationToken.None);

        var evidence = Assert.Single(result.Results).Evidence;
        Assert.Equal(20, evidence.Count);
        Assert.Contains(evidence, item => item.Kind == "EVIDENCE_TRUNCATED");
    }

    [Fact]
    public void PartialAttemptEvidenceIsBoundedAndRetainsItsTerminalMarker()
    {
        var results = Enumerable.Range(0, 100).Select(attempt => new ScheduledMutationResult(
            "evaluation:v1:" + new string('d', 64),
            attempt == 99 ? UnitDisposition.Omitted : UnitDisposition.Killed,
            Enumerable.Range(0, 20).Select(index =>
                new EvaluationEvidence("EVIDENCE", $"attempt {attempt}; entry {index}")).ToArray()))
            .ToArray();

        var evidence = StrictExecutionPipeline.BuildPartialEvidence(results);

        Assert.Equal(StabilityEvidence.MaxUnitEvidence, evidence.Count);
        Assert.Contains(evidence, item => item.Kind == "STABILITY_EVIDENCE_TRUNCATED");
        Assert.Equal("PARTIAL_STABILITY_EVIDENCE", evidence[^1].Kind);
    }

    [Fact]
    public async Task SchedulerNeverExceedsWorkerBoundAndReturnsCanonicalOrder()
    {
        var active = 0;
        var maximum = 0;
        var executor = new DelegateMutationExecutor(async (work, _, token) =>
        {
            var now = Interlocked.Increment(ref active);
            maximum = Math.Max(maximum, now);
            await Task.Delay(20, token);
            Interlocked.Decrement(ref active);
            return new(work.EvaluationUnitId, UnitDisposition.Killed,
                [new("KILLED", "Fixture killed the mutant.")]);
        });
        var work = Enumerable.Range(0, 6).Reverse().Select(index =>
            new ScheduledMutation($"mutation:v1:{index:D64}", $"evaluation:v1:{index:D64}", ["unit"])).ToArray();

        var result = await new EvaluationScheduler(executor, TimeProvider.System).RunAsync(work, 2,
            TimeSpan.FromSeconds(10), DateTimeOffset.UtcNow.AddMinutes(1), CancellationToken.None);

        Assert.Equal(2, maximum);
        Assert.Equal(work.OrderBy(item => item.EvaluationUnitId, StringComparer.Ordinal)
            .Select(item => item.EvaluationUnitId), result.Results.Select(item => item.EvaluationUnitId));
        Assert.Empty(result.IncompleteConditions);
    }

    [Fact]
    public async Task CancellationStopsAssignmentAndAccountsEveryQueuedUnit()
    {
        using var cancel = new CancellationTokenSource();
        var calls = 0;
        var executor = new DelegateMutationExecutor((work, _, _) =>
        {
            if (Interlocked.Increment(ref calls) == 1) cancel.Cancel();
            return Task.FromResult(new ScheduledMutationResult(work.EvaluationUnitId, UnitDisposition.Killed,
                [new("KILLED", "Fixture killed the mutant.")]));
        });
        var work = Enumerable.Range(0, 5).Select(index =>
            new ScheduledMutation($"mutation:v1:{index:D64}", $"evaluation:v1:{index:D64}", ["unit"])).ToArray();

        var result = await new EvaluationScheduler(executor, TimeProvider.System).RunAsync(work, 1,
            TimeSpan.FromSeconds(10), DateTimeOffset.UtcNow.AddMinutes(1), cancel.Token);

        Assert.Equal(5, result.Results.Count);
        Assert.Contains(result.Results, item => item.Disposition == UnitDisposition.Omitted);
        Assert.Contains(result.IncompleteConditions, item => item.Code == "EXECUTION_CANCELLED");
    }

    private sealed class DelegateMutationExecutor : IIsolatedMutationExecutor
    {
        private readonly Func<ScheduledMutation, TimeSpan, CancellationToken, Task<ScheduledMutationResult>> _run;
        public DelegateMutationExecutor(
            Func<ScheduledMutation, TimeSpan, CancellationToken, Task<ScheduledMutationResult>> run) => _run = run;
        public Task<ScheduledMutationResult> ExecuteAsync(ScheduledMutation mutation, TimeSpan timeout,
            CancellationToken cancellationToken) => _run(mutation, timeout, cancellationToken);
    }

    private sealed class AdjustableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }
}
