using System.Diagnostics;
using System.Globalization;

namespace Mutate4CSharp.Tests;

public sealed class IssueFiveReviewRoundNineTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mutate4csharp-issue5-r9",
        Guid.NewGuid().ToString("N"));

    public IssueFiveReviewRoundNineTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task StrictFastExitPinsItsBoundaryWithoutTouchingAnUnrelatedProcess()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var unrelated = Process.Start("/bin/sleep", "300") ??
            throw new InvalidOperationException("Could not start unrelated process.");
        var unrelatedStart = unrelated.StartTime;
        var marker = Path.Combine(_root, "strict-child.pid");
        try
        {
            var result = await ProcessTree.RunAsync("/bin/sh",
                ["-c", $"sleep 300 & printf %s $! > {Shell(marker)}; exit 0"], _root,
                TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken,
                requireLinuxSessionIsolation: true);

            Assert.Equal(0, result.ExitCode);
            AssertProcessGone(ReadPid(marker));
            Assert.False(unrelated.HasExited, "Strict cleanup crossed its pinned execution boundary.");
            Assert.Equal(unrelatedStart, unrelated.StartTime);
        }
        finally
        {
            TryKill(unrelated, unrelatedStart);
        }
    }

    [Fact]
    public async Task LegacyTimeoutLeavesPreviouslyObservedDetachedDescendantAlive()
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/usr/bin/setsid")) return;
        var marker = Path.Combine(_root, "legacy-timeout.pid");
        var invocation = ProcessTree.RunAsync("/bin/sh", ["-c", LegacyCommand(marker)], _root,
            TimeSpan.FromMilliseconds(750), CancellationToken.None);

        await AssertDetachedSurvivesAsync(marker, invocation, result => Assert.True(result.TimedOut));
    }

    [Fact]
    public async Task LegacyCancellationLeavesPreviouslyObservedDetachedDescendantAlive()
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/usr/bin/setsid")) return;
        var marker = Path.Combine(_root, "legacy-cancel.pid");
        using var cancellation = new CancellationTokenSource();
        var invocation = ProcessTree.RunAsync("/bin/sh", ["-c", LegacyCommand(marker)], _root,
            TimeSpan.FromSeconds(15), cancellation.Token);
        await WaitForPidAsync(marker);
        await Task.Delay(250, TestContext.Current.CancellationToken);
        cancellation.Cancel();
        var pid = ReadPid(marker);
        using var detached = Process.GetProcessById(pid);
        var start = detached.StartTime;
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => invocation);
            Assert.False(detached.HasExited, $"Legacy cancellation killed detached process {pid}.");
        }
        finally
        {
            TryKill(detached, start);
        }
    }

    [Fact]
    public async Task LegacyOutputFailureLeavesPreviouslyObservedDetachedDescendantAlive()
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/usr/bin/setsid")) return;
        var marker = Path.Combine(_root, "legacy-exception.pid");
        var command = LegacyCommand(marker, "printf 123456789; sleep 300");
        var invocation = ProcessTree.RunAsync("/bin/sh", ["-c", command], _root,
            TimeSpan.FromSeconds(15), CancellationToken.None, maxOutputBytes: 4);
        await WaitForPidAsync(marker);
        var pid = ReadPid(marker);
        using var detached = Process.GetProcessById(pid);
        var start = detached.StartTime;
        try
        {
            await Assert.ThrowsAsync<IOException>(() => invocation);
            Assert.False(detached.HasExited, $"Legacy exception cleanup killed detached process {pid}.");
        }
        finally
        {
            TryKill(detached, start);
        }
    }

    private async Task AssertDetachedSurvivesAsync(string marker, Task<ProcessTreeResult> invocation,
        Action<ProcessTreeResult> assertResult)
    {
        await WaitForPidAsync(marker);
        var pid = ReadPid(marker);
        using var detached = Process.GetProcessById(pid);
        var start = detached.StartTime;
        try
        {
            assertResult(await invocation);
            Assert.False(detached.HasExited, $"Legacy timeout killed detached process {pid}.");
        }
        finally
        {
            TryKill(detached, start);
        }
    }

    private static string LegacyCommand(string marker, string tail = "sleep 300") =>
        $"( /usr/bin/setsid /bin/sh -c 'printf %s $$ > \"$1\"; exec /bin/sleep 300' child {Shell(marker)} " +
        $"</dev/null >/dev/null 2>&1 & ); while [ ! -s {Shell(marker)} ]; do sleep 0.01; done; {tail}";

    private static async Task WaitForPidAsync(string path)
    {
        var watch = Stopwatch.StartNew();
        while (!File.Exists(path) || new FileInfo(path).Length == 0)
        {
            if (watch.Elapsed >= TimeSpan.FromSeconds(10))
                throw new TimeoutException("Detached process did not publish its PID.");
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }
    }

    private static int ReadPid(string path) => int.Parse(File.ReadAllText(path),
        NumberStyles.None, CultureInfo.InvariantCulture);

    private static void AssertProcessGone(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            if (!process.HasExited)
            {
                var start = process.StartTime;
                TryKill(process, start);
                Assert.Fail($"Strict descendant {pid} remained alive.");
            }
        }
        catch (ArgumentException) { }
    }

    private static void TryKill(Process process, DateTime start)
    {
        try
        {
            if (!process.HasExited && process.StartTime == start)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5_000);
            }
        }
        catch (InvalidOperationException) { }
    }

    private static string Shell(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }
}
