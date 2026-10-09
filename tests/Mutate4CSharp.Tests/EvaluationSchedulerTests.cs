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
}
