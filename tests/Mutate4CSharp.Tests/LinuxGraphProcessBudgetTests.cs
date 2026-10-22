using System.Diagnostics;

namespace Mutate4CSharp.Tests;

// Genuine local shell controls when admitted on Linux; no SDK/model process.
public sealed class LinuxGraphProcessBudgetTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "mutate-linux-budget-" + Guid.NewGuid().ToString("N"));
    private bool retainRoot;
    public LinuxGraphProcessBudgetTests() => Directory.CreateDirectory(root);

    [Theory(Timeout = 30_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Known_local_timeout_or_scheduler_cancel_retires_then_runs_second_process_on_same_original(bool schedulerCancel)
    {
        Assert.SkipWhen(!OperatingSystem.IsLinux(), "Actual Linux per-call retirement and subsequent birth control.");
        var original = new ResolvedGraphBudget(TimeSpan.FromSeconds(25), TestContext.Current.CancellationToken);
        var before = original.Observe();
        using var localTimer = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var ready = Path.Combine(root, "first-local-ready.data");
        var first = ProcessTree.RunAsync("/bin/sh", ["-c", "printf actual-local-ready > first-local-ready.data.tmp; /bin/mv first-local-ready.data.tmp first-local-ready.data; printf first-local-process; exec /bin/sleep 60"], root,
            schedulerCancel ? TimeSpan.FromSeconds(10) : TimeSpan.FromMilliseconds(500), localTimer.Token,
            requireLinuxSessionIsolation: true, originalBudget: original);
        if (schedulerCancel)
        {
            try
            {
                // Actual target readiness, not a timer that can cancel setsid
                // preflight before the target exists. No new work allowance.
                while (!File.Exists(ready))
                {
                    original.CheckWork();
                    if (first.IsCompleted) await first;
                    await Task.Delay(10, TestContext.Current.CancellationToken);
                }
                Assert.Equal("actual-local-ready", await File.ReadAllTextAsync(ready, TestContext.Current.CancellationToken));
                localTimer.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
            }
            finally
            {
                if (!first.IsCompleted) localTimer.Cancel();
                try { await first; } catch when (!first.IsCompletedSuccessfully) { }
            }
        }
        else
        {
            var timeout = await first;
            Assert.True(timeout.TimedOut); Assert.Contains("first-local-process", timeout.StandardOutput, StringComparison.Ordinal);
        }
        Assert.Equal(before, original.Observe()); original.CheckWork();
        ProcessTree.LinuxOriginalProcessFrame.AssertDirectoryMayBeRemoved(root);
        var second = await ProcessTree.RunAsync("/bin/sh", ["-c", "printf second-local-process; exit 7"], root,
            TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken,
            requireLinuxSessionIsolation: true, originalBudget: original);
        Assert.Equal(7, second.ExitCode); Assert.False(second.TimedOut);
        Assert.Equal("second-local-process", second.StandardOutput);
        Assert.Equal(before, original.Observe()); original.CheckWork();
        Assert.True(original.RemainingWork < TimeSpan.FromTicks(before.WorkTicks));
        ProcessTree.LinuxOriginalProcessFrame.AssertDirectoryMayBeRemoved(root);
    }

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
        else Assert.IsType<ResolvedGraphWorkExpiredException>(failure);
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
            ["-c", "trap '' TERM; echo $$ > failure-child.pid.tmp; /bin/mv failure-child.pid.tmp failure-child.pid; while [ ! -f failure-release ]; do /bin/sleep 0.01; done; printf '%0128d' 0; exec /bin/sleep 60"], root,
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
            var originalChild = ReadOriginalLinuxIdentity(actualChild.Id);
            Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => AssertOriginalLinuxProcessTerminated(originalChild));
            original.CheckWork();
            await File.WriteAllTextAsync(Path.Combine(root, "failure-release"), "release", TestContext.Current.CancellationToken);
            var failure = await Assert.ThrowsAsync<IOException>(() => run);
            Assert.Contains("output exceeded", failure.Message);
            AssertOriginalLinuxProcessTerminated(originalChild);
            Assert.NotNull(original.Observe().Cleanup);
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
        // The product's SAME original budget owns its work timer. A second
        // CancelAfter can round down and request caller cancellation before the
        // original endpoint, legitimately producing cancellation, not timeout.
        var run = ProcessTree.RunAsync("/bin/sh", ["-c", "echo $$ > original-child.pid.tmp; /bin/mv original-child.pid.tmp original-child.pid; exec /bin/sleep 60"], root,
            TimeSpan.FromSeconds(30), cancellation.Token, requireLinuxSessionIsolation: true, originalBudget: budget);
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
            var originalChild = ReadOriginalLinuxIdentity(actualChild.Id);
            await Assert.ThrowsAsync<ResolvedGraphWorkExpiredException>(() => run);
            AssertOriginalLinuxProcessTerminated(originalChild);
            Assert.Equal(origin.Original, budget.Observe().Original);
            Assert.Equal(origin.WholeTicks, budget.Observe().WholeTicks);
            Assert.True(Stopwatch.GetElapsedTime(origin.Original) >= TimeSpan.FromTicks(origin.WorkTicks));
            Assert.NotNull(budget.Observe().Cleanup);
            Assert.True(budget.RemainingDisposition > TimeSpan.Zero);
            Assert.Throws<ResolvedGraphWorkExpiredException>(budget.CheckWork);
        }
        finally
        {
            if (!run.IsCompleted) cancellation.Cancel();
            try { await run; } catch when (!run.IsCompletedSuccessfully) { }
            actualChild?.Dispose();
        }
    }

    private static (int Pid, ulong StartTime) ReadOriginalLinuxIdentity(int pid)
    {
        var observation = ReadLinuxObservation(pid);
        var actual = observation ?? throw new Xunit.Sdk.XunitException("Original Linux child disappeared before identity capture.");
        Assert.True(actual.State is not ('Z' or 'X'), actual.Raw);
        return (pid, actual.StartTime);
    }

    private static void AssertOriginalLinuxProcessTerminated((int Pid, ulong StartTime) original)
    {
        // A non-parent Process handle need not observe waitpid retirement for
        // an orphan zombie. Independently inspect the SAME /proc identity;
        // absence or a non-executable terminal state proves no live child.
        var observation = ReadLinuxObservation(original.Pid);
        if (observation is null) return;
        Assert.Equal(original.StartTime, observation.Value.StartTime); // PID reuse is unknown, never success.
        Assert.True(observation.Value.State is 'Z' or 'X', observation.Value.Raw);
    }

    private static (char State, ulong StartTime, string Raw)? ReadLinuxObservation(int pid)
    {
        string stat;
        try
        {
            using var stream = new FileStream($"/proc/{pid}/stat", FileMode.Open,
                FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var bytes = new byte[16 * 1024 + 1];
            var count = 0;
            while (count < bytes.Length)
            {
                var read = stream.Read(bytes, count, bytes.Length - count);
                if (read == 0) break;
                count += read;
            }
            Assert.InRange(count, 1, bytes.Length - 1);
            stat = System.Text.Encoding.UTF8.GetString(bytes, 0, count);
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        // Unreadable, malformed or substituted identities refuse. Retain the
        // bounded kernel record in assertion diagnostics for an actual failure.
        Assert.InRange(stat.Length, 1, 16 * 1024);
        var commandEnd = stat.LastIndexOf(')');
        Assert.True(commandEnd >= 0 && commandEnd + 2 < stat.Length, stat);
        Assert.True(stat.StartsWith(pid.ToString(System.Globalization.CultureInfo.InvariantCulture) + " (", StringComparison.Ordinal), stat);
        var fields = stat[(commandEnd + 2)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Assert.True(fields.Length > 19 && fields[0].Length == 1, stat);
        Assert.True(ulong.TryParse(fields[19], System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out var startTime), stat);
        return (fields[0][0], startTime, stat);
    }

    public void Dispose() { if (!retainRoot) Directory.Delete(root, recursive: true); }
}
