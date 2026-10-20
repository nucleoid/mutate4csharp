using System.Diagnostics;

namespace Mutate4CSharp.Tests;

// Genuine local shell controls when admitted on Linux; no SDK/model process.
public sealed class LinuxGraphProcessBudgetTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "mutate-linux-budget-" + Guid.NewGuid().ToString("N"));
    private bool retainRoot;
    public LinuxGraphProcessBudgetTests() => Directory.CreateDirectory(root);

    [Theory]
    [InlineData("closing")]
    [InlineData("cancelled")]
    [InlineData("expired")]
    public async Task Closed_same_original_refuses_before_sets_id_probe_or_target_birth(string mode)
    {
        Assert.SkipWhen(!OperatingSystem.IsLinux(), "Actual Linux process admission control.");
        using var cancellation = new CancellationTokenSource();
        var budget = new ResolvedGraphBudget(mode == "expired" ? TimeSpan.FromMilliseconds(1) : TimeSpan.FromSeconds(30), cancellation.Token);
        if (mode == "closing") budget.BeginFailureCleanup();
        if (mode == "cancelled") cancellation.Cancel();
        if (mode == "expired") await Task.Delay(10, TestContext.Current.CancellationToken);
        var failure = await Assert.ThrowsAnyAsync<Exception>(() => ProcessTree.RunAsync("/bin/sh",
            ["-c", "printf born > must-stay-absent"], root, TimeSpan.FromSeconds(30),
            CancellationToken.None, requireLinuxSessionIsolation: true, originalBudget: budget));
        if (mode == "closing") Assert.IsType<EvaluationContractException>(failure);
        else if (mode == "cancelled") Assert.IsAssignableFrom<OperationCanceledException>(failure);
        else Assert.IsType<TimeoutException>(failure);
        Assert.Empty(Directory.EnumerateFileSystemEntries(root));
    }

    [Fact]
    public async Task Dependency_sdk_boundary_cannot_probe_or_write_after_shared_work_closes()
    {
        Assert.SkipWhen(!OperatingSystem.IsLinux(), "Actual Linux dependency-boundary admission control.");
        var budget = new ResolvedGraphBudget(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        budget.BeginFailureCleanup();
        await Assert.ThrowsAsync<EvaluationContractException>(() =>
            ExecutionEnvironment.CreateExecutionBoundaryAsync(root, root, CancellationToken.None, budget));
        Assert.Empty(Directory.EnumerateFileSystemEntries(root));
    }

    [Fact(Timeout = 10_000)]
    public async Task Expired_whole_retains_actual_exited_process_handle_and_observer_without_late_close_or_retry()
    {
        Assert.SkipWhen(!OperatingSystem.IsLinux(), "Actual Linux retained root-handle control; no containment claim.");
        var working = OwnedDirectory.Create(root, "native-working");
        var packages = OwnedDirectory.Create(root, "native-packages");
        var process = new Process { StartInfo = new ProcessStartInfo("/bin/sleep") { WorkingDirectory = working.Root, ArgumentList = { "0.1" }, UseShellExecute = false } };
        process.StartInfo.Environment["NUGET_PACKAGES"] = packages.Root;
        var budget = new ResolvedGraphBudget(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
        var owner = new ProcessTree.LinuxOriginalProcessFrame(process, budget);
        budget.CheckWork(); Assert.True(owner.Start());
        var actualHandle = process.SafeHandle;
        var exit = process.WaitForExitAsync(TestContext.Current.CancellationToken); owner.Track(exit);
        await exit; Assert.True(process.HasExited);
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); owner.Track(pending.Task);
        await Task.Delay(TimeSpan.FromSeconds(1.05), TestContext.Current.CancellationToken);
        Assert.Throws<SnapshotCleanupException>(owner.CloseSettled);
        owner.Dispose(); // Retains the SAME process, actual observers and original; never calls Dispose on Process.
        Assert.Throws<SnapshotCleanupException>(() => ProcessTree.LinuxOriginalProcessFrame.AssertDirectoryMayBeRemoved(root));
        Assert.Throws<SnapshotCleanupException>(() => ProcessTree.LinuxOriginalProcessFrame.AssertDirectoryMayBeRemoved(packages.Root));
        retainRoot = true; // Intentional unknown owner's namespace is retained as well as its handle.
        await Assert.ThrowsAsync<SnapshotCleanupException>(() => working.DisposeAsync().AsTask());
        await Assert.ThrowsAsync<SnapshotCleanupException>(() => packages.DisposeAsync().AsTask());
        Assert.True(Directory.Exists(working.Root)); Assert.True(Directory.Exists(packages.Root));
        Assert.False(actualHandle.IsClosed); Assert.Same(actualHandle, process.SafeHandle);
        Assert.Throws<EvaluationContractException>(owner.CloseSettled);
        pending.SetResult(); // Later completion does not renew first-close authority.
        Assert.Throws<EvaluationContractException>(owner.CloseSettled);
        Assert.False(actualHandle.IsClosed);
    }

    [Fact]
    public void Refusal_before_actual_birth_first_closes_controls_without_retaining_a_native_namespace()
    {
        Assert.SkipWhen(!OperatingSystem.IsLinux(), "Linux pre-birth control, without invoking a native child.");
        var original = new ResolvedGraphBudget(TimeSpan.FromSeconds(30), CancellationToken.None);
        var retained = false; var settled = false;
        var process = new Process { StartInfo = new ProcessStartInfo("/bin/sh") { WorkingDirectory = root } };
        var owner = new ProcessTree.LinuxOriginalProcessFrame(process, original, () => retained = true, () => settled = true);
        original.BeginFailureCleanup();
        Assert.Throws<EvaluationContractException>(() => owner.Start());
        Assert.False(retained);
        owner.Dispose(); Assert.True(settled);
        ProcessTree.LinuxOriginalProcessFrame.AssertDirectoryMayBeRemoved(root);
        Assert.Throws<EvaluationContractException>(() => owner.Start());
    }

    [Theory(Timeout = 10_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Original_observer_join_waits_actual_task_before_first_close_and_preserves_faults(bool fault)
    {
        Assert.SkipWhen(!OperatingSystem.IsLinux(), "Actual exited-process handle plus retained managed observer lifetime control.");
        var original = new ResolvedGraphBudget(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        var retainedStatus = false;
        var process = new Process { StartInfo = new ProcessStartInfo("/bin/sleep") { WorkingDirectory = root, ArgumentList = { "0.1" }, UseShellExecute = false } };
        using var owner = new ProcessTree.LinuxOriginalProcessFrame(process, original, () => retainedStatus = true, () => retainedStatus = false);
        Assert.True(owner.Start()); var handle = process.SafeHandle;
        var exit = process.WaitForExitAsync(TestContext.Current.CancellationToken); owner.Track(exit);
        await exit; Assert.True(process.HasExited); Assert.True(retainedStatus);
        // This additional real pending Task deterministically exercises the
        // observer-continuation delay; it is not a native-containment receipt.
        var delayed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); owner.Track(delayed.Task);
        var joined = owner.JoinObserversAsync(); Assert.False(joined.IsCompleted); Assert.False(handle.IsClosed);
        var observedFailure = new IOException("original retained observer failure");
        if (fault) delayed.SetException(observedFailure); else delayed.SetResult();
        await joined; owner.CloseSettled();
        Assert.True(handle.IsClosed); Assert.False(retainedStatus);
        ProcessTree.LinuxOriginalProcessFrame.AssertDirectoryMayBeRemoved(root);
        if (fault)
        {
            var primary = new IOException("preserved primary failure"); owner.PreserveObserverFailures(primary);
            var failures = Assert.IsType<Exception[]>(primary.Data["ProcessTree.OriginalObserverFailures"]);
            var retainedFailure = Assert.IsType<AggregateException>(Assert.Single(failures));
            Assert.Same(observedFailure, Assert.Single(retainedFailure.InnerExceptions));
            Assert.Throws<AggregateException>(owner.ThrowObserverFailures);
        }
        else owner.ThrowObserverFailures();
        Assert.Throws<EvaluationContractException>(() => owner.JoinObserversAsync().GetAwaiter().GetResult());
    }

    [Fact(Timeout = 30_000)]
    public async Task Actual_output_bound_failure_reaps_sigterm_ignoring_child_and_releases_its_known_closed_namespace()
    {
        Assert.SkipWhen(!OperatingSystem.IsLinux(), "Actual Linux output-failure and native child settlement control.");
        var packages = Path.Combine(root, "packages"); Directory.CreateDirectory(packages);
        var original = new ResolvedGraphBudget(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var run = ProcessTree.RunAsync("/bin/sh",
            ["-c", "trap '' TERM; echo $$ > failure-child.pid.tmp; /bin/mv failure-child.pid.tmp failure-child.pid; /bin/sleep 0.3; printf '%0128d' 0; exec /bin/sleep 60"], root,
            TimeSpan.FromSeconds(10), cancellation.Token,
            new Dictionary<string, string?> { ["NUGET_PACKAGES"] = packages }, 64,
            requireLinuxSessionIsolation: true, originalBudget: original);
        Process? actualChild = null;
        try
        {
            var watch = Stopwatch.StartNew();
            var marker = Path.Combine(root, "failure-child.pid");
            while (!File.Exists(marker))
            {
                if (run.IsCompleted) await run;
                Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3));
                await Task.Delay(5, TestContext.Current.CancellationToken);
            }
            actualChild = Process.GetProcessById(int.Parse(await File.ReadAllTextAsync(marker, TestContext.Current.CancellationToken)));
            _ = actualChild.SafeHandle; Assert.False(actualChild.HasExited);
            var failure = await Assert.ThrowsAsync<IOException>(() => run);
            Assert.Contains("output exceeded", failure.Message);
            Assert.True(actualChild.HasExited); Assert.NotNull(original.Observe().Cleanup);
            ProcessTree.LinuxOriginalProcessFrame.AssertDirectoryMayBeRemoved(root);
            ProcessTree.LinuxOriginalProcessFrame.AssertDirectoryMayBeRemoved(packages);
        }
        finally
        {
            if (!run.IsCompleted) cancellation.Cancel();
            try { await run; } catch when (!run.IsCompletedSuccessfully) { }
            actualChild?.Dispose();
        }
    }

    [Fact(Timeout = 45_000)]
    public async Task Actual_success_then_original_work_expiry_requires_real_descendant_exit_inside_same_whole()
    {
        Assert.SkipWhen(!OperatingSystem.IsLinux(), "Genuine Linux native shell/setsid control.");
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var budget = new ResolvedGraphBudget(TimeSpan.FromSeconds(30), cancellation.Token);
        var origin = budget.Observe();
        var positive = await ProcessTree.RunAsync("/bin/sh", ["-c", "printf genuine-linux-output"], root,
            TimeSpan.FromSeconds(5), cancellation.Token, requireLinuxSessionIsolation: true, originalBudget: budget);
        Assert.Equal(0, positive.ExitCode); Assert.False(positive.TimedOut);
        Assert.Equal("genuine-linux-output", positive.StandardOutput);
        Assert.Null(budget.Observe().Cleanup); budget.CheckWork();

        var marker = Path.Combine(root, "original-child.pid");
        using var workTimer = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
        workTimer.CancelAfter(budget.RemainingWork);
        var run = ProcessTree.RunAsync("/bin/sh", ["-c", "echo $$ > original-child.pid.tmp; /bin/mv original-child.pid.tmp original-child.pid; exec /bin/sleep 60"], root,
            TimeSpan.FromSeconds(30), workTimer.Token, requireLinuxSessionIsolation: true, originalBudget: budget);
        Process? actualChild = null;
        try
        {
            var watch = Stopwatch.StartNew();
            while (!File.Exists(marker))
            {
                Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3), "Actual Linux child did not publish within its existing work allowance.");
                if (run.IsCompleted) await run;
                await Task.Delay(5, TestContext.Current.CancellationToken);
            }
            actualChild = Process.GetProcessById(int.Parse(await File.ReadAllTextAsync(marker, TestContext.Current.CancellationToken)));
            _ = actualChild.SafeHandle; _ = actualChild.StartTime;
            Assert.False(actualChild.HasExited);
            var timedOut = await run;
            Assert.True(timedOut.TimedOut); Assert.True(actualChild.HasExited);
            Assert.Equal(origin.Original, budget.Observe().Original);
            Assert.Equal(origin.WholeTicks, budget.Observe().WholeTicks);
            Assert.True(Stopwatch.GetElapsedTime(origin.Original) >= TimeSpan.FromTicks(origin.WorkTicks));
            Assert.NotNull(budget.Observe().Cleanup);
            Assert.True(budget.RemainingDisposition > TimeSpan.Zero);
            Assert.Throws<EvaluationContractException>(budget.CheckWork);
        }
        finally
        {
            if (!run.IsCompleted) cancellation.Cancel();
            try { await run; } catch when (!run.IsCompletedSuccessfully) { }
            actualChild?.Dispose();
        }
    }

    public void Dispose() { if (!retainRoot) Directory.Delete(root, recursive: true); }
}
