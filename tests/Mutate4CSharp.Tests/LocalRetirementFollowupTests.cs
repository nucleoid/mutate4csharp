using System.Diagnostics;

namespace Mutate4CSharp.Tests;

public sealed class LocalRetirementFollowupTests
{
    [Fact]
    public async Task Actual_frame_observer_join_uses_one_declining_local_cap_without_ending_live_original()
    {
        var original = new ResolvedGraphBudget(TimeSpan.FromMinutes(3), CancellationToken.None);
        using var frame = new ProcessTree.LinuxOriginalProcessFrame(new Process
        {
            StartInfo = new ProcessStartInfo { WorkingDirectory = Path.Combine(Path.GetTempPath(), "mutate-managed-frame-" + Guid.NewGuid().ToString("N")) }
        }, original);
        var observer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        frame.Track(observer.Task);
        var first = frame.RemainingRetirement;
        Assert.InRange(first, TimeSpan.FromSeconds(55), TimeSpan.FromSeconds(60));
        Assert.True(first < original.RemainingDisposition);
        var join = frame.JoinObserversAsync();
        Assert.False(join.IsCompleted);
        await Task.Delay(20, TestContext.Current.CancellationToken);
        Assert.True(frame.RemainingRetirement < first);
        observer.SetResult();
        await join;
        frame.CloseSettled();
        original.CheckWork();
        Assert.Null(original.Observe().Cleanup);
    }

    [Fact]
    public async Task Actual_frame_retirement_clips_to_original_and_pending_observer_is_retained_terminal()
    {
        var original = new ResolvedGraphBudget(TimeSpan.FromSeconds(2), CancellationToken.None);
        var frame = new ProcessTree.LinuxOriginalProcessFrame(new Process
        {
            StartInfo = new ProcessStartInfo { WorkingDirectory = Path.Combine(Path.GetTempPath(), "mutate-managed-expired-frame-" + Guid.NewGuid().ToString("N")) }
        }, original);
        var observer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        frame.Track(observer.Task);
        Assert.True(frame.RemainingRetirement <= TimeSpan.FromSeconds(2));
        await Assert.ThrowsAsync<TimeoutException>(frame.JoinObserversAsync);
        Assert.Throws<SnapshotCleanupException>(frame.Dispose);
        Assert.NotNull(original.Observe().Cleanup);
        Assert.False(observer.Task.IsCompleted); // No replacement/cancellation/completion invented.
    }

    [Fact]
    public void Known_coverage_build_timeout_keeps_same_original_and_next_result_available()
    {
        var original = new ResolvedGraphBudget(TimeSpan.FromSeconds(30), CancellationToken.None);
        var before = original.Observe();
        var timedOut = new ProcessTreeResult(-1, true, "actual returned DATA", "", TimeSpan.Zero);
        var failure = Assert.ThrowsAny<TimeoutException>(() => ResolvedGraphCoverletMapping.RequireSuccessfulBuild(timedOut, original));
        Assert.Same(timedOut, failure.Data["ResolvedGraphCoverage.ActualBuild"]);
        original.CheckWork();
        ResolvedGraphCoverletMapping.RequireSuccessfulBuild(new(0, false, "next known returned DATA", "", TimeSpan.Zero), original);
        Assert.Equal(before, original.Observe());
        var nonzero = new ProcessTreeResult(1, false, "", "retained failure", TimeSpan.Zero);
        var bad = Assert.Throws<SnapshotCaptureException>(() => ResolvedGraphCoverletMapping.RequireSuccessfulBuild(nonzero, original));
        Assert.Same(nonzero, bad.Data["ResolvedGraphCoverage.ActualBuild"]);
    }

    [Fact]
    public async Task Scheduler_classifies_actual_owner_work_expiry_as_aggregate_deadline_and_stops_assignment()
    {
        var original = new ResolvedGraphBudget(TimeSpan.FromMilliseconds(100), CancellationToken.None);
        var expiry = original.Observe();
        while (Stopwatch.GetElapsedTime(expiry.Original).Ticks < expiry.WorkTicks)
            await Task.Delay(5, TestContext.Current.CancellationToken);
        var calls = 0;
        var executor = new DelegateMutationExecutor((mutation, _, _) =>
        {
            calls++;
            original.CheckWork();
            return Task.FromResult(new ScheduledMutationResult(mutation.EvaluationUnitId, UnitDisposition.Killed, [new("KILLED", "must not return")]));
        });
        ScheduledMutation[] mutations = [
            new("mutation:v1:" + new string('a', 64), "evaluation:v1:" + new string('a', 64), ["suite"]),
            new("mutation:v1:" + new string('b', 64), "evaluation:v1:" + new string('b', 64), ["suite"])
        ];
        var result = await new EvaluationScheduler(executor, TimeProvider.System).RunAsync(mutations, 1,
            TimeSpan.FromSeconds(10), DateTimeOffset.UtcNow.AddMinutes(1), CancellationToken.None);
        Assert.Equal(1, calls);
        Assert.Equal(UnitDisposition.Omitted, result.Results[0].Disposition);
        Assert.Contains(result.Results[0].Evidence, row => row.Kind == "OVERALL_DEADLINE_DURING_EXECUTION");
        Assert.DoesNotContain(result.Results.SelectMany(row => row.Evidence), row => row.Kind == "MUTANT_TIMEOUT");
        Assert.Contains(result.IncompleteConditions, row => row.Code == "OVERALL_DEADLINE_EXCEEDED");
        Assert.NotNull(original.Observe().Cleanup);
        Assert.Throws<EvaluationContractException>(original.CheckWork);
    }
    private sealed class DelegateMutationExecutor(
        Func<ScheduledMutation, TimeSpan, CancellationToken, Task<ScheduledMutationResult>> execute)
        : IIsolatedMutationExecutor
    {
        public Task<ScheduledMutationResult> ExecuteAsync(ScheduledMutation mutation, TimeSpan timeout,
            CancellationToken cancellationToken) => execute(mutation, timeout, cancellationToken);
    }
}
