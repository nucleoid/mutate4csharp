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
        Assert.Throws<EvaluationContractException>(() => new ResolvedGraphBudget(
            actual with { WorkTicks = actual.WholeTicks }, CancellationToken.None));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(30)]
    [InlineData(600)]
    [InlineData(1200)]
    public void Cleanup_is_reserved_inside_unchanged_original_whole_and_shared_with_the_worker(int seconds)
    {
        var whole = TimeSpan.FromSeconds(seconds);
        var parent = new ResolvedGraphBudget(whole, CancellationToken.None);
        var actual = parent.Observe();
        Assert.Equal(whole.Ticks, actual.WholeTicks);
        Assert.Equal(Math.Min(TimeSpan.FromSeconds(60).Ticks, whole.Ticks / 10), actual.WholeTicks - actual.WorkTicks);
        Assert.InRange(actual.WorkTicks, 1, actual.WholeTicks - 1);
        var worker = new ResolvedGraphBudget(actual, CancellationToken.None);
        Assert.Equal(actual, worker.Observe());
        Assert.True(worker.RemainingDisposition > worker.RemainingWork);
        Assert.Throws<ArgumentOutOfRangeException>(() => new ResolvedGraphBudget(TimeSpan.FromTicks(1), CancellationToken.None));
    }

    [Fact]
    public async Task Original_work_expiry_still_admits_actual_first_close_and_refuses_new_input_and_worker()
    {
        var path = Path.Combine(Path.GetTempPath(), "mutate-budget-close-" + Guid.NewGuid().ToString("N"));
        var owner = new ResolvedGraphBudget(TimeSpan.FromSeconds(2), CancellationToken.None);
        var observation = owner.Observe();
        using var inputs = new ResolvedGraphInputs(owner);
        FileStream? stream = null;
        try
        {
            // Actual file ownership exists before work expires.
            stream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read);
            stream.WriteByte(42); stream.Flush(true);
            while (System.Diagnostics.Stopwatch.GetElapsedTime(observation.Original).Ticks < observation.WorkTicks)
                await Task.Delay(5, TestContext.Current.CancellationToken);
            Assert.Throws<TimeoutException>(owner.CheckWork);
            Assert.Throws<TimeoutException>(() => inputs.Read(path + "-never-opened", "source"));
            Assert.Throws<TimeoutException>(() => new ResolvedGraphBudget(observation, CancellationToken.None));
            owner.BeginFailureCleanup();
            owner.CheckDisposition();
            Assert.InRange(owner.RemainingDisposition.Ticks, 1, observation.WholeTicks - observation.WorkTicks);
            stream.Dispose(); stream = null;
            owner.CheckDisposition();
            using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            Assert.Equal(42, exclusive.ReadByte());
            Assert.False(File.Exists(path + "-never-opened"));
        }
        finally { stream?.Dispose(); File.Delete(path); }
    }

    [Fact]
    public async Task Expired_whole_has_no_new_disposition_interval_and_earliest_cleanup_cannot_be_renewed()
    {
        // No native owner is created for the already-expired negative control.
        var shortOwner = new ResolvedGraphBudget(TimeSpan.FromMilliseconds(1), CancellationToken.None);
        await Task.Delay(10, TestContext.Current.CancellationToken);
        shortOwner.BeginFailureCleanup();
        Assert.Throws<TimeoutException>(shortOwner.CheckDisposition);
        Assert.Throws<TimeoutException>(() => shortOwner.RemainingDisposition);

        var owner = new ResolvedGraphBudget(TimeSpan.FromSeconds(120), CancellationToken.None);
        var observation = owner.Observe();
        var restrictive = observation with {
            Original = System.Diagnostics.Stopwatch.GetTimestamp() - 61 * System.Diagnostics.Stopwatch.Frequency
        };
        var view = new ResolvedGraphBudget(restrictive, CancellationToken.None);
        view.ImportEarlierCleanup(restrictive with { Cleanup = restrictive.Original });
        view.BeginFailureCleanup();
        Assert.Equal(restrictive.Original, view.Observe().Cleanup);
        Assert.Throws<TimeoutException>(view.CheckDisposition);
        Assert.Throws<TimeoutException>(() => view.RemainingDisposition);
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
