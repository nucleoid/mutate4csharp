using System.Diagnostics;

namespace Mutate4CSharp.Tests;

// Actual monotonic-clock checks with restrictive DATA imports. No observation
// object is a native scope or producer authority. All cases remain UNEXECUTED.
public sealed class ResolvedGraphBudgetTests
{
    [Fact]
    public void Worker_view_retains_same_running_original_and_cannot_renew_it()
    {
        var owner = new ResolvedGraphBudget(TimeSpan.FromSeconds(30), CancellationToken.None);
        var actual = owner.Observe();
        var worker = new ResolvedGraphBudget(actual, CancellationToken.None);
        Assert.Equal(actual, worker.Observe());
        Assert.True(worker.RemainingWork < TimeSpan.FromTicks(actual.WorkTicks));
        Assert.Throws<EvaluationContractException>(() => new ResolvedGraphBudget(
            actual with { Frequency = actual.Frequency + 1 }, CancellationToken.None));
        Assert.Throws<EvaluationContractException>(() => new ResolvedGraphBudget(
            actual with { Original = long.MaxValue }, CancellationToken.None));
        Assert.Throws<EvaluationContractException>(() => new ResolvedGraphBudget(
            actual with { WorkTicks = actual.WholeTicks + 1 }, CancellationToken.None));
    }

    [Fact]
    public void Same_original_earliest_cleanup_is_sticky_and_later_local_latch_cannot_replace_it()
    {
        var owner = new ResolvedGraphBudget(TimeSpan.FromSeconds(30), CancellationToken.None);
        var actual = owner.Observe();
        var worker = new ResolvedGraphBudget(actual, CancellationToken.None);
        worker.ImportEarlierCleanup(actual with { Cleanup = actual.Original });
        worker.BeginFailureCleanup();
        Assert.Equal(actual.Original, worker.Observe().Cleanup);
        Assert.Throws<EvaluationContractException>(worker.CheckWork);
        worker.CheckDisposition();
        Assert.Throws<EvaluationContractException>(() => worker.ImportEarlierCleanup(
            actual with { Original = actual.Original - 1, Cleanup = actual.Original }));
        Assert.Equal(actual.Original, worker.Observe().Cleanup);
    }

    [Fact]
    public void Expired_original_work_refuses_before_worker_input_acquisition_and_cancel_does_not_veto_cleanup()
    {
        var owner = new ResolvedGraphBudget(TimeSpan.FromSeconds(30), CancellationToken.None);
        var actual = owner.Observe();
        Assert.Throws<TimeoutException>(() => new ResolvedGraphBudget(actual with {
            Original = Stopwatch.GetTimestamp() - Math.Max(1, Stopwatch.Frequency / 1000), WorkTicks = 1
        }, CancellationToken.None));
        using var cancellation = new CancellationTokenSource();
        var canceled = new ResolvedGraphBudget(actual, cancellation.Token);
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(canceled.CheckWork);
        canceled.CheckDisposition();
        Assert.NotNull(canceled.Observe().Cleanup);
    }
}
