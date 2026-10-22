using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Mutate4CSharp.Tests;

// Actual native fixtures when admitted on Windows. No SDK, model, credentials
// or external service is needed; source-only until the root grants its lane.
public sealed class WindowsProcessProfileTests
{
    private static string PowerShellExecutable()
    {
        // An explicitly selected missing/relative image remains a refusal. The
        // fallback is only the existing OS shell, never a PATH/cache/user scan.
        var selected = Environment.GetEnvironmentVariable("MUTATE4CSHARP_TEST_PWSH");
        if (selected is null)
        {
            var powerShell7 = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PowerShell", "7", "pwsh.exe");
            selected = File.Exists(powerShell7) ? powerShell7 : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        }
        Assert.True(Path.IsPathFullyQualified(selected) && File.Exists(selected), "An actual existing absolute selected PowerShell, installed PowerShell7, or OS Windows PowerShell executable is required.");
        return selected;
    }

    [Fact]
    public async Task Genuine_Windows_child_accepts_environment_above_32Ki_without_losing_values_or_clears()
    {
        if (!OperatingSystem.IsWindows()) return;
        await using var owned = OwnedDirectory.Create(OwnedDirectory.PrivateParent("windows-wide-env"), "fixture");
        var nonce = Guid.NewGuid().ToString("N");
        var environment = new Dictionary<string, string?>
        {
            ["MUTATE_WIDE_ENV_A"] = new('a', 20_000),
            ["MUTATE_WIDE_ENV_B"] = new('b', 20_000),
            ["MUTATE_WIDE_ENV_SENTINEL"] = nonce,
            ["ComSpec"] = null
        };
        Assert.False(string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ComSpec")), "The actual closed Windows fixture requires inherited ComSpec before its explicit clear.");
        var shell = PowerShellExecutable();
        var observe = """
            $ErrorActionPreference='Stop'
            function HashValue([string]$value) {
                $sha=[Security.Cryptography.SHA256]::Create()
                try { ([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($value)))).Replace('-','') }
                finally { $sha.Dispose() }
            }
            $a=[Environment]::GetEnvironmentVariable('MUTATE_WIDE_ENV_A')
            $b=[Environment]::GetEnvironmentVariable('MUTATE_WIDE_ENV_B')
            @{LengthA=$a.Length;LengthB=$b.Length;HashA=(HashValue $a);HashB=(HashValue $b);Sentinel=[Environment]::GetEnvironmentVariable('MUTATE_WIDE_ENV_SENTINEL');Cleared=($null -eq [Environment]::GetEnvironmentVariable('ComSpec'))}|ConvertTo-Json -Compress
            """;
        var actual = await WindowsProcessTree.RunAsync(shell, ["-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(observe))], owned.Root,
            TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken, environment, 1024 * 1024);
        Assert.Equal(0, actual.ExitCode); Assert.False(actual.TimedOut);
        using var row = JsonDocument.Parse(actual.StandardOutput);
        Assert.Equal(nonce, row.RootElement.GetProperty("Sentinel").GetString()); Assert.True(row.RootElement.GetProperty("Cleared").GetBoolean());
        foreach (var suffix in new[] { "A", "B" })
        {
            Assert.Equal(20_000, row.RootElement.GetProperty("Length" + suffix).GetInt32());
            Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(environment["MUTATE_WIDE_ENV_" + suffix]!))),
                row.RootElement.GetProperty("Hash" + suffix).GetString());
        }
        WindowsProcessTree.AssertDirectoryMayBeRemoved(owned.Root);
    }

    [Theory]
    [InlineData("row")]
    [InlineData("count")]
    [InlineData("total")]
    public async Task Finite_Windows_environment_refusal_precedes_native_birth_and_does_not_retain_an_absent_child(string mode)
    {
        if (!OperatingSystem.IsWindows()) return;
        await using var owned = OwnedDirectory.Create(OwnedDirectory.PrivateParent("windows-env-refusal"), "fixture");
        var environment = new Dictionary<string, string?>();
        if (mode == "row") environment["OVERSIZED"] = new('x', WindowsProcessTree.MaximumEnvironmentVariableCharacters + 1);
        else if (mode == "count")
            for (var index = 0; index <= WindowsProcessTree.MaximumEnvironmentVariables; index++) environment["ENV_" + index] = "x";
        else for (var index = 0; index < 512; index++) environment["ENV_" + index] = new('x', 9000);
        var marker = Path.Combine(owned.Root, "must-not-be-born.txt");
        var shell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
        var refusal = await Assert.ThrowsAsync<ExecutionEnvironmentUnavailableException>(() => WindowsProcessTree.RunAsync(shell,
            ["/d", "/c", "echo born>must-not-be-born.txt"], owned.Root, TimeSpan.FromSeconds(15),
            TestContext.Current.CancellationToken, environment, 1024 * 1024));
        Assert.Contains("before native birth", refusal.Message); Assert.False(File.Exists(marker));
        WindowsProcessTree.AssertDirectoryMayBeRemoved(owned.Root);
    }

    [Fact(Timeout = 30_000)]
    public async Task Actual_subordinate_scheduler_cancel_retires_then_admits_second_command_on_same_original()
    {
        Assert.SkipWhen(!OperatingSystem.IsWindows(), "Actual Windows subordinate cancellation and same-original subsequent birth.");
        await using var owned = OwnedDirectory.Create(OwnedDirectory.PrivateParent("windows-local-stop"), "fixture");
        var shell = PowerShellExecutable(); var ready = Path.Combine(owned.Root, "ready.data");
        var original = new ResolvedGraphBudget(TimeSpan.FromSeconds(25), TestContext.Current.CancellationToken);
        var before = original.Observe();
        using var local = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var script = "[IO.File]::WriteAllText('" + ready.Replace("'", "''", StringComparison.Ordinal) + "','ready'); while($true){Start-Sleep -Milliseconds 100}";
        var first = WindowsProcessTree.RunAsync(shell, ["-NoProfile", "-NonInteractive", "-EncodedCommand",
            Convert.ToBase64String(Encoding.Unicode.GetBytes(script))], owned.Root, TimeSpan.FromSeconds(15), local.Token, null, 65536, original);
        try
        {
            var watch = Stopwatch.StartNew();
            while (!File.Exists(ready))
            {
                if (first.IsCompleted) await first;
                Assert.True(watch.Elapsed < TimeSpan.FromSeconds(8), "Actual first child did not signal readiness.");
                await Task.Delay(10, TestContext.Current.CancellationToken);
            }
            local.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
            Assert.Equal(before, original.Observe()); original.CheckWork();
            WindowsProcessTree.AssertDirectoryMayBeRemoved(owned.Root);
            var second = await WindowsProcessTree.RunAsync(shell,
                ["-NoProfile", "-NonInteractive", "-Command", "[Console]::WriteLine('second-after-local-cancel'); exit 7"],
                owned.Root, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken, null, 65536, original);
            Assert.Equal(7, second.ExitCode); Assert.False(second.TimedOut);
            Assert.Contains("second-after-local-cancel", second.StandardOutput, StringComparison.Ordinal);
            Assert.Equal(before, original.Observe()); original.CheckWork();
            WindowsProcessTree.AssertDirectoryMayBeRemoved(owned.Root);
        }
        finally
        {
            if (!first.IsCompleted) local.Cancel();
            try { await first; } catch when (!first.IsCompletedSuccessfully) { }
        }
    }

    [Theory(Timeout = 60_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Actual_timeout_retires_the_original_descendant_pipes_and_file_under_legacy_or_same_graph_deadline(bool graph)
    {
        if (!OperatingSystem.IsWindows()) return;
        await using var owned = OwnedDirectory.Create(OwnedDirectory.PrivateParent("windows-timeout-profile"), "fixture");
        var shell = PowerShellExecutable();
        var ready = Path.Combine(owned.Root, "ready.json"); var locked = Path.Combine(owned.Root, "owned-lock.data");
        var nonce = Guid.NewGuid().ToString("N");
        static string Literal(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
        static string Encoded(string value) => Convert.ToBase64String(Encoding.Unicode.GetBytes(value));
        var child = $$"""
            $ErrorActionPreference='Stop'
            $held=[IO.File]::Open({{Literal(locked)}},[IO.FileMode]::CreateNew,[IO.FileAccess]::ReadWrite,[IO.FileShare]::Read)
            $actual=[Diagnostics.Process]::GetCurrentProcess()
            [Console]::WriteLine('actual timeout child holds pipe and file')
            $row=@{Pid=$PID;Started=$actual.StartTime.ToUniversalTime().Ticks;Nonce={{Literal(nonce)}}}|ConvertTo-Json -Compress
            [IO.File]::WriteAllText({{Literal(ready + ".tmp")}},$row)
            [IO.File]::Move({{Literal(ready + ".tmp")}},{{Literal(ready)}})
            try{Start-Sleep -Seconds 60}finally{$held.Dispose();$actual.Dispose()}
            """;
        var root = $$"""
            $ErrorActionPreference='Stop'
            $start=[Diagnostics.ProcessStartInfo]::new({{Literal(shell)}})
            $start.UseShellExecute=$false
            $start.Arguments='-NoProfile -NonInteractive -EncodedCommand {{Encoded(child)}}'
            $descendant=[Diagnostics.Process]::Start($start)
            try{Start-Sleep -Seconds 60}finally{$descendant.Dispose()}
            """;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var original = graph ? new ResolvedGraphBudget(TimeSpan.FromSeconds(30), cancellation.Token) : null;
        var origin = original?.Observe();
        var run = WindowsProcessTree.RunAsync(shell, ["-NoProfile", "-NonInteractive", "-EncodedCommand", Encoded(root)], owned.Root,
            TimeSpan.FromSeconds(8), cancellation.Token, null, 1024 * 1024, original);
        Process? actualChild = null;
        try
        {
            var observation = Stopwatch.StartNew();
            while (!File.Exists(ready))
            {
                Assert.True(observation.Elapsed < TimeSpan.FromSeconds(6), "Actual descendant did not become ready within this original fixture work allowance.");
                if (run.IsCompleted) await run;
                await Task.Delay(10, TestContext.Current.CancellationToken);
            }
            using var row = JsonDocument.Parse(await File.ReadAllBytesAsync(ready, TestContext.Current.CancellationToken));
            Assert.Equal(nonce, row.RootElement.GetProperty("Nonce").GetString());
            actualChild = Process.GetProcessById(row.RootElement.GetProperty("Pid").GetInt32());
            _ = actualChild.SafeHandle;
            Assert.Equal(row.RootElement.GetProperty("Started").GetInt64(), actualChild.StartTime.ToUniversalTime().Ticks);
            Assert.Throws<IOException>(() => { using var blocked = new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None); });
            var actual = await run;
            Assert.True(actual.TimedOut); Assert.True(actualChild.HasExited);
            Assert.Contains("actual timeout child holds pipe and file", actual.StandardOutput);
            using (new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
            WindowsProcessTree.AssertDirectoryMayBeRemoved(owned.Root);
            if (original is not null)
            {
                var retired = original.Observe();
                Assert.Equal(origin!.Original, retired.Original); Assert.Equal(origin!.WholeTicks, retired.WholeTicks);
                Assert.Equal(origin.WorkTicks, retired.WorkTicks); Assert.Null(retired.Cleanup);
                original.CheckWork();
                var second = await WindowsProcessTree.RunAsync(shell,
                    ["-NoProfile", "-NonInteractive", "-Command", "[Console]::WriteLine('second-local-command'); exit 7"],
                    owned.Root, TimeSpan.FromSeconds(5), cancellation.Token, null, 1024 * 1024, original);
                Assert.Equal(7, second.ExitCode); Assert.False(second.TimedOut);
                Assert.Contains("second-local-command", second.StandardOutput, StringComparison.Ordinal);
                Assert.Equal(origin, original.Observe()); original.CheckWork();
                WindowsProcessTree.AssertDirectoryMayBeRemoved(owned.Root);
            }
        }
        finally
        {
            // Always observe the SAME invocation; no PID adoption or replacement
            // budget can turn a failed fixture into native settlement evidence.
            if (!run.IsCompleted) cancellation.Cancel();
            try { await run; } catch when (!run.IsCompletedSuccessfully) { }
            actualChild?.Dispose();
        }
    }

    [Fact]
    public async Task Actual_maximum_value_length_is_admitted_independently_of_key_and_equals_characters()
    {
        if (!OperatingSystem.IsWindows()) return;
        await using var owned = OwnedDirectory.Create(OwnedDirectory.PrivateParent("windows-value-bound"), "fixture");
        var value = new string('v', WindowsProcessTree.MaximumEnvironmentVariableCharacters);
        var actual = await WindowsProcessTree.RunAsync(PowerShellExecutable(),
            ["-NoProfile", "-NonInteractive", "-Command", "[Console]::Write([Environment]::GetEnvironmentVariable('MUTATE_BOUND_VALUE').Length)"],
            owned.Root, TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken,
            new Dictionary<string, string?> { ["MUTATE_BOUND_VALUE"] = value }, 1024);
        Assert.False(actual.TimedOut); Assert.Equal(0, actual.ExitCode);
        Assert.Equal(value.Length.ToString(System.Globalization.CultureInfo.InvariantCulture), actual.StandardOutput);
        WindowsProcessTree.AssertDirectoryMayBeRemoved(owned.Root);
    }

    [Fact(Timeout = 20_000)]
    public async Task Actual_graph_work_timer_is_timeout_while_same_original_cleanup_closes_native_child()
    {
        if (!OperatingSystem.IsWindows()) return;
        await using var owned = OwnedDirectory.Create(OwnedDirectory.PrivateParent("windows-work-timer"), "fixture");
        var budget = new ResolvedGraphBudget(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var original = budget.Observe();
        await Assert.ThrowsAsync<ResolvedGraphWorkExpiredException>(() => ProcessTree.RunAsync(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"),
            ["/d", "/c", "echo born>actual-born.txt & ping -n 60 127.0.0.1 > nul"], owned.Root,
            TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken, originalBudget: budget));
        Assert.True(File.Exists(Path.Combine(owned.Root, "actual-born.txt")));
        Assert.True(budget.WorkEndpointExpired);
        Assert.Throws<ResolvedGraphWorkExpiredException>(budget.CheckWork);
        Assert.Equal(original.Original, budget.Observe().Original); Assert.Equal(original.WholeTicks, budget.Observe().WholeTicks);
        Assert.NotNull(budget.Observe().Cleanup); Assert.True(budget.RemainingDisposition > TimeSpan.Zero);
        WindowsProcessTree.AssertDirectoryMayBeRemoved(owned.Root);
    }

    [Fact(Timeout = 30_000)]
    public async Task Actual_v1_default_keeps_its_prior_successful_root_descendant_behavior()
    {
        if (!OperatingSystem.IsWindows()) return;
        await using var owned = OwnedDirectory.Create(OwnedDirectory.PrivateParent("windows-v1-compatibility"), "fixture");
        var shell = PowerShellExecutable();
        static string Literal(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
        static string Encoded(string value) => Convert.ToBase64String(Encoding.Unicode.GetBytes(value));
        var child = $$"""
            $ErrorActionPreference='Stop'
            [IO.File]::WriteAllText({{Literal(Path.Combine(owned.Root, "child.pid.tmp"))}},[string]$PID)
            [IO.File]::Move({{Literal(Path.Combine(owned.Root, "child.pid.tmp"))}},{{Literal(Path.Combine(owned.Root, "child.pid"))}})
            while(-not [IO.File]::Exists({{Literal(Path.Combine(owned.Root, "child-release"))}})){Start-Sleep -Milliseconds 10}
            [IO.File]::WriteAllText({{Literal(Path.Combine(owned.Root, "child-finished"))}},'actual retained v1 child')
            """;
        var root = $$"""
            $ErrorActionPreference='Stop'
            [IO.File]::WriteAllText({{Literal(Path.Combine(owned.Root, "root.pid.tmp"))}},[string]$PID)
            [IO.File]::Move({{Literal(Path.Combine(owned.Root, "root.pid.tmp"))}},{{Literal(Path.Combine(owned.Root, "root.pid"))}})
            $start=[Diagnostics.ProcessStartInfo]::new({{Literal(shell)}})
            $start.UseShellExecute=$false
            $start.Arguments='-NoProfile -NonInteractive -EncodedCommand {{Encoded(child)}}'
            $descendant=[Diagnostics.Process]::Start($start)
            try {
                while(-not [IO.File]::Exists({{Literal(Path.Combine(owned.Root, "root-release"))}})) { Start-Sleep -Milliseconds 10 }
            } finally { $descendant.Dispose() }
            """;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var run = ProcessTree.RunAsync(shell, ["-NoProfile", "-NonInteractive", "-EncodedCommand", Encoded(root)],
            owned.Root, TimeSpan.FromSeconds(15), cancellation.Token); // Deliberately no graph original.
        Process? actualRoot = null, actualChild = null;
        try
        {
            var watch = Stopwatch.StartNew();
            while (!File.Exists(Path.Combine(owned.Root, "root.pid")) || !File.Exists(Path.Combine(owned.Root, "child.pid")))
            {
                if (run.IsCompleted) await run;
                Assert.True(watch.Elapsed < TimeSpan.FromSeconds(6));
                await Task.Delay(10, TestContext.Current.CancellationToken);
            }
            actualRoot = Process.GetProcessById(int.Parse(await File.ReadAllTextAsync(Path.Combine(owned.Root, "root.pid"), TestContext.Current.CancellationToken)));
            actualChild = Process.GetProcessById(int.Parse(await File.ReadAllTextAsync(Path.Combine(owned.Root, "child.pid"), TestContext.Current.CancellationToken)));
            _ = actualRoot.SafeHandle; _ = actualChild.SafeHandle;
            Assert.False(actualRoot.HasExited); Assert.False(actualChild.HasExited);
            File.WriteAllText(Path.Combine(owned.Root, "root-release"), "release original root");
            await actualRoot.WaitForExitAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(5));
            // The v1 default must not acquire graph success-time descendant kill
            // semantics. This SAME child is released and independently observed.
            Assert.False(actualChild.HasExited);
            File.WriteAllText(Path.Combine(owned.Root, "child-release"), "release original child");
            await actualChild.WaitForExitAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(5));
            var actual = await run;
            Assert.Equal(0, actual.ExitCode); Assert.False(actual.TimedOut);
            Assert.Equal("actual retained v1 child", File.ReadAllText(Path.Combine(owned.Root, "child-finished")));
        }
        finally
        {
            File.WriteAllText(Path.Combine(owned.Root, "root-release"), "fixture retirement");
            File.WriteAllText(Path.Combine(owned.Root, "child-release"), "fixture retirement");
            if (!run.IsCompleted) cancellation.Cancel();
            try { await run; } catch when (!run.IsCompletedSuccessfully) { }
            if (actualChild is not null) await actualChild.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
            actualRoot?.Dispose(); actualChild?.Dispose();
        }
    }
    [Theory(Timeout = 30_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Actual_nullable_success_grace_retains_unknown_at_cap_or_retires_on_caller_cancel(bool cancel)
    {
        if (!OperatingSystem.IsWindows()) return;
        // Deliberate UNKNOWN must not enlist the shared ambient package cache.
        // Its unique owner/directory remain retained until test-host exit.
        var owned = OwnedDirectory.Create(OwnedDirectory.PrivateParent("windows-legacy-grace-terminal"), "fixture");
        var packages = Path.Combine(owned.Root, "packages");
        Directory.CreateDirectory(packages);
        var cscript = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cscript.exe");
        Assert.True(File.Exists(cscript), "The required Windows terminal-grace fixture needs the actual inbox WSH console host; missing/disabled WSH is setup failure, not a skipped native proof.");
        var shell = PowerShellExecutable();
        var nonce = Guid.NewGuid().ToString("N");
        var closed = Path.Combine(owned.Root, "child-closed-streams");
        var ready = Path.Combine(owned.Root, "ready.json");
        var releaseRoot = Path.Combine(owned.Root, "release-root");
        var releaseChild = Path.Combine(owned.Root, "release-child");
        var finished = Path.Combine(owned.Root, "child-finished");
        var childPath = Path.Combine(owned.Root, "close-streams.js");
        static string Literal(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
        static string Js(string value) => JsonSerializer.Serialize(value);
        var child = $$"""
            var files = new ActiveXObject("Scripting.FileSystemObject");
            WScript.StdOut.Close();
            WScript.StdErr.Close();
            var closed = files.CreateTextFile({{Js(closed + ".tmp")}}, false);
            closed.Write({{Js(nonce)}}); closed.Close();
            files.MoveFile({{Js(closed + ".tmp")}}, {{Js(closed)}});
            var endpoint = (new Date()).getTime() + 20000;
            while (!files.FileExists({{Js(releaseChild)}})) {
                if ((new Date()).getTime() >= endpoint) WScript.Quit(31);
                WScript.Sleep(10);
            }
            var finished = files.CreateTextFile({{Js(finished)}}, false);
            finished.Write({{Js(nonce)}}); finished.Close();
            WScript.Quit(0);
            """;
        await File.WriteAllTextAsync(childPath, child, TestContext.Current.CancellationToken);
        var root = $$"""
            $ErrorActionPreference='Stop'
            $start=[Diagnostics.ProcessStartInfo]::new({{Literal(cscript)}})
            $start.UseShellExecute=$false
            $start.Arguments='//nologo //E:JScript ' + '"' + {{Literal(childPath)}} + '"'
            # Inherit the ORIGINAL capture handles. The actual child closes both
            # WSH standard streams before publishing its atomic nonce witness.
            $child=[Diagnostics.Process]::Start($start)
            try {
                $watch=[Diagnostics.Stopwatch]::StartNew()
                while(-not [IO.File]::Exists({{Literal(closed)}})) {
                    if($child.HasExited){throw 'WSH child exited before the closed-stream witness'}
                    if($watch.Elapsed.TotalSeconds-ge6){throw 'Closed-stream witness unavailable'}
                    Start-Sleep -Milliseconds 10
                }
                if([IO.File]::ReadAllText({{Literal(closed)}})-cne{{Literal(nonce)}}){throw 'Closed-stream witness mismatch'}
                $actual=[Diagnostics.Process]::GetCurrentProcess()
                try {
                    $row=@{Nonce={{Literal(nonce)}};ChildPid=$child.Id;ChildStarted=$child.StartTime.ToUniversalTime().Ticks;RootPid=$PID;RootStarted=$actual.StartTime.ToUniversalTime().Ticks}|ConvertTo-Json -Compress
                    [IO.File]::WriteAllText({{Literal(ready + ".tmp")}},$row)
                    [IO.File]::Move({{Literal(ready + ".tmp")}},{{Literal(ready)}})
                } finally {$actual.Dispose()}
                while(-not [IO.File]::Exists({{Literal(releaseRoot)}})) {
                    if($watch.Elapsed.TotalSeconds-ge12){throw 'Original root was not released'}
                    Start-Sleep -Milliseconds 10
                }
                [Console]::Out.WriteLine('root stream before normal exit')
                [Console]::Error.WriteLine('root error before normal exit')
            } finally {$child.Dispose()}
            """;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var run = ProcessTree.RunAsync(shell, ["-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(root))],
            owned.Root, TimeSpan.FromSeconds(15), cancellation.Token,
            new Dictionary<string, string?> { ["NUGET_PACKAGES"] = packages }); // No graph original.
        Process? actualRoot = null, actualChild = null;
        Exception? primary = null;
        var knownCancellationClosure = false;
        try
        {
            var watch = Stopwatch.StartNew();
            while (!File.Exists(ready))
            {
                if (run.IsCompleted) await run;
                Assert.True(watch.Elapsed < TimeSpan.FromSeconds(6), "Actual closed-stream fixture did not become ready.");
                await Task.Delay(10, TestContext.Current.CancellationToken);
            }
            Assert.Equal(nonce, File.ReadAllText(closed));
            using var row = JsonDocument.Parse(await File.ReadAllBytesAsync(ready, TestContext.Current.CancellationToken));
            Assert.Equal(nonce, row.RootElement.GetProperty("Nonce").GetString());
            actualRoot = Process.GetProcessById(row.RootElement.GetProperty("RootPid").GetInt32());
            actualChild = Process.GetProcessById(row.RootElement.GetProperty("ChildPid").GetInt32());
            _ = actualRoot.SafeHandle; _ = actualChild.SafeHandle;
            Assert.Equal(row.RootElement.GetProperty("RootStarted").GetInt64(), actualRoot.StartTime.ToUniversalTime().Ticks);
            Assert.Equal(row.RootElement.GetProperty("ChildStarted").GetInt64(), actualChild.StartTime.ToUniversalTime().Ticks);
            Assert.False(actualRoot.HasExited); Assert.False(actualChild.HasExited);
            var releasedAt = Stopwatch.GetTimestamp();
            File.WriteAllText(releaseRoot, nonce);
            await actualRoot.WaitForExitAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(0, actualRoot.ExitCode);
            Assert.False(run.IsCompleted); Assert.False(actualChild.HasExited);
            if (cancel)
            {
                // Cancellation occurs inside an already-running grace. This real
                // known-closure positive does not manufacture a new graph owner.
                await Task.Delay(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
                Assert.False(run.IsCompleted); Assert.False(actualChild.HasExited);
                cancellation.Cancel();
                var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
                Assert.Equal(cancellation.Token, failure.CancellationToken);
                Assert.True(Stopwatch.GetElapsedTime(releasedAt) < TimeSpan.FromSeconds(8), "Cancellation did not finish within the original grace and observation slack.");
                Assert.True(actualChild.HasExited);
                WindowsProcessTree.AssertDirectoryMayBeRemoved(owned.Root);
                WindowsProcessTree.AssertDirectoryMayBeRemoved(packages);
                knownCancellationClosure = true;
            }
            else
            {
                var failure = await Assert.ThrowsAsync<SnapshotCleanupException>(() => run);
                var original = Assert.IsType<TimeoutException>(failure.OriginalFailure);
                var cleanup = Assert.IsType<TimeoutException>(failure.CleanupFailure);
                Assert.Contains("cleanup endpoint expired", original.Message, StringComparison.Ordinal);
                Assert.Contains("cleanup endpoint expired", cleanup.Message, StringComparison.Ordinal);
                Assert.InRange(Stopwatch.GetElapsedTime(releasedAt), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8));
                Assert.False(actualChild.HasExited, "Grace expiry must not perform a late success-time kill.");
                Assert.Throws<SnapshotCleanupException>(() => WindowsProcessTree.AssertDirectoryMayBeRemoved(owned.Root));
                Assert.Throws<SnapshotCleanupException>(() => WindowsProcessTree.AssertDirectoryMayBeRemoved(packages));
                File.WriteAllText(releaseChild, nonce); // Only the SAME identified fixture child.
                await actualChild.WaitForExitAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(0, actualChild.ExitCode); Assert.Equal(nonce, File.ReadAllText(finished));
                var repeated = await Assert.ThrowsAsync<SnapshotCleanupException>(() => run);
                Assert.Same(failure, repeated); // Late physical exit cannot turn UNKNOWN into PASS.
                Assert.Throws<SnapshotCleanupException>(() => WindowsProcessTree.AssertDirectoryMayBeRemoved(owned.Root));
            }
        }
        catch (Exception failure) { primary = failure; }
        finally
        {
            try
            {
                File.WriteAllText(releaseRoot, nonce);
                File.WriteAllText(releaseChild, nonce);
                if (!run.IsCompleted)
                {
                    cancellation.Cancel();
                    try { await run; }
                    catch (Exception stopped) { primary = primary is null ? stopped : new AggregateException(primary, stopped); }
                }
                if (actualChild is not null)
                    await actualChild.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
                actualRoot?.Dispose(); actualChild?.Dispose();
                // Deliberate UNKNOWN and every failing fixture retain all DATA.
                // Never Dispose/delete its owned directory or clear Unsettled.
                if (knownCancellationClosure && primary is null) await owned.DisposeAsync();
            }
            catch (Exception cleanup) { primary = primary is null ? cleanup : new AggregateException(primary, cleanup); }
            GC.KeepAlive(owned);
        }
        if (primary is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(primary).Throw();
    }
}
