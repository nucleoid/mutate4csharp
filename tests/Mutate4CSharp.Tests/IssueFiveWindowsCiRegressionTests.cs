namespace Mutate4CSharp.Tests;

public sealed class IssueFiveWindowsCiRegressionTests
{
    [Theory(Timeout = 90_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OriginalWindowsBoundaryClosesChildPipesAndFileAfterRootExitOrCancellation(bool cancel)
    {
        if (!OperatingSystem.IsWindows()) return;
        var parent = OwnedDirectory.PrivateParent("windows-job-test");
        await using var directory = OwnedDirectory.Create(parent, "fixture");
        var ready = Path.Combine(directory.Root, "ready.json");
        var release = Path.Combine(directory.Root, "release-root");
        var locked = Path.Combine(directory.Root, "owned.dll");
        var token = Guid.NewGuid().ToString("N");
        var shell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe");
        Assert.True(File.Exists(shell), "The genuine Windows process fixture requires Windows PowerShell.");
        static string Literal(string text) => "'" + text.Replace("'", "''", StringComparison.Ordinal) + "'";
        static string Encoded(string text) => Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(text));
        var child = $$"""
            $ErrorActionPreference='Stop'
            $lock=[IO.File]::Open({{Literal(locked)}},[IO.FileMode]::CreateNew,[IO.FileAccess]::ReadWrite,[IO.FileShare]::Read)
            $actual=[Diagnostics.Process]::GetCurrentProcess()
            $row=@{Pid=$PID;Started=$actual.StartTime.ToUniversalTime().Ticks;Token={{Literal(token)}}}|ConvertTo-Json -Compress
            [Console]::Error.WriteLine('owned child has inherited stderr and a delete-denying file')
            [IO.File]::WriteAllText({{Literal(ready + ".tmp")}},$row)
            [IO.File]::Move({{Literal(ready + ".tmp")}},{{Literal(ready)}})
            try{Start-Sleep -Seconds 60}finally{$lock.Dispose();$actual.Dispose()}
            """;
        var root = $$"""
            $ErrorActionPreference='Stop'
            $start=[Diagnostics.ProcessStartInfo]::new({{Literal(shell)}})
            $start.UseShellExecute=$false
            $start.Arguments='-NoProfile -NonInteractive -EncodedCommand {{Encoded(child)}}'
            $child=[Diagnostics.Process]::Start($start)
            $watch=[Diagnostics.Stopwatch]::StartNew()
            while(![IO.File]::Exists({{Literal(ready)}})){if($watch.Elapsed.TotalSeconds-ge15){throw 'Child never became ready'};Start-Sleep -Milliseconds 10}
            while(![IO.File]::Exists({{Literal(release)}})){if($watch.Elapsed.TotalSeconds-ge25){throw 'Parent never released root'};Start-Sleep -Milliseconds 10}
            {{(cancel ? "Start-Sleep -Seconds 60" : "exit 0")}}
            """;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var original = new ResolvedGraphBudget(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        var run = ProcessTree.RunAsync(shell, ["-NoProfile", "-NonInteractive", "-EncodedCommand", Encoded(root)],
            directory.Root, TimeSpan.FromSeconds(30), cancellation.Token, originalBudget: original);
        System.Diagnostics.Process? actualChild = null;
        DateTime? actualStart = null;
        try
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            while (!File.Exists(ready))
            {
                Assert.True(watch.Elapsed < TimeSpan.FromSeconds(20), "Real fixture child never published READY.");
                if (run.IsCompleted) await run;
                await Task.Delay(10, TestContext.Current.CancellationToken);
            }
            using var row = System.Text.Json.JsonDocument.Parse(await File.ReadAllBytesAsync(ready,
                TestContext.Current.CancellationToken));
            Assert.Equal(token, row.RootElement.GetProperty("Token").GetString());
            // The root cannot exit (and we have not requested cancellation) until
            // this actual child handle and identity are bound. No exit-race catch
            // can turn a missing/mismatched process into an accepted observation.
            actualChild = System.Diagnostics.Process.GetProcessById(row.RootElement.GetProperty("Pid").GetInt32());
            _ = actualChild.SafeHandle;
            actualStart = new DateTime(row.RootElement.GetProperty("Started").GetInt64(), DateTimeKind.Utc);
            Assert.Equal(actualStart.Value, actualChild.StartTime.ToUniversalTime());
            await File.WriteAllTextAsync(release, token, TestContext.Current.CancellationToken);
            if (cancel)
            {
                cancellation.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
            }
            else
            {
                var result = await run;
                Assert.Equal(0, result.ExitCode);
                Assert.False(result.TimedOut);
                Assert.Contains("owned child", result.StandardError, StringComparison.Ordinal);
            }
            if (actualChild is not null) Assert.True(actualChild.HasExited, "Original fixture descendant survived returned cleanup.");
            // This positive uses the real file-sharing contract, not a reflected
            // Job count or an implementation-produced boolean.
            using (File.Open(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
            File.Delete(locked);
        }
        finally
        {
            // Negative RED cleanup cannot promote the assertion to PASS. Touch only
            // the genuine fixture process whose start identity was observed above.
            if (actualChild is not null)
            {
                if (!actualChild.HasExited && actualChild.StartTime.ToUniversalTime() == actualStart)
                {
                    actualChild.Kill(entireProcessTree: true);
                    await actualChild.WaitForExitAsync(TestContext.Current.CancellationToken)
                        .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
                }
                actualChild.Dispose();
            }
        }
    }

    [Fact]
    public async Task LinuxIsolationRequestOnNonLinuxUsesNormalProcessResultAndCleanup()
    {
        var executable = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh";
        string[] arguments = OperatingSystem.IsWindows()
            ? ["/d", "/s", "/c", "(echo normal-output)&(echo normal-error 1>&2)&exit /b 23"]
            : ["-c", "printf normal-output; printf normal-error >&2; exit 23"];

        var result = await ProcessTree.RunAsync(new ProcessPlatformCapabilities(IsLinux: false),
            executable, arguments, AppContext.BaseDirectory, TimeSpan.FromSeconds(30),
            TestContext.Current.CancellationToken, requireLinuxSessionIsolation: true);

        Assert.Equal(23, result.ExitCode);
        Assert.Equal("normal-output", result.StandardOutput.Trim());
        Assert.Equal("normal-error", result.StandardError.Trim());
        Assert.False(result.TimedOut);
    }

    [Theory]
    [InlineData("")]
    [InlineData("/d")]
    [InlineData("/s")]
    [InlineData("/c")]
    [InlineData("plain")]
    [InlineData("two words")]
    [InlineData("two\twords")]
    [InlineData("two\u00a0words")]
    [InlineData("one\"two")]
    [InlineData("plain\\")]
    [InlineData("two words\\")]
    [InlineData("one\\\"two")]
    public void WindowsArgumentSerializationRoundTripsTheIndependentNativeParser(string argument)
    {
        Assert.SkipWhen(!OperatingSystem.IsWindows(), "Independent Windows native parser compatibility requires Windows.");
        Assert.InRange(argument.Length, 0, 256);
        var quote = typeof(WindowsProcessTree).GetMethod("Quote",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(quote);
        var serialized = Assert.IsType<string>(quote.Invoke(null, [argument]));
        // This independent parser checks argument serialization, not process birth
        // or cmd.exe switch grammar; the unchanged genuine command Fact covers that.
        var argv = ParseWindowsCommandLine("\"argument-probe.exe\" " + serialized, out var count);
        Assert.NotEqual(IntPtr.Zero, argv);
        Exception? primary = null;
        try
        {
            Assert.Equal(2, count);
            Assert.Equal(argument, System.Runtime.InteropServices.Marshal.PtrToStringUni(
                System.Runtime.InteropServices.Marshal.ReadIntPtr(argv, IntPtr.Size)));
        }
        catch (Exception error)
        {
            primary = error;
            throw;
        }
        finally
        {
            // One original allocation, one first free, including assertion failure.
            if (FreeWindowsArguments(argv) != IntPtr.Zero)
            {
                var close = new System.ComponentModel.Win32Exception(
                    System.Runtime.InteropServices.Marshal.GetLastWin32Error(), "LocalFree failed.");
                if (primary is not null) throw new AggregateException(primary, close);
                throw close;
            }
        }
    }

    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [System.Runtime.InteropServices.DllImport("shell32.dll", EntryPoint = "CommandLineToArgvW",
        CharSet = System.Runtime.InteropServices.CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern IntPtr ParseWindowsCommandLine(string commandLine, out int count);

    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [System.Runtime.InteropServices.DllImport("kernel32.dll", EntryPoint = "LocalFree",
        ExactSpelling = true, SetLastError = true)]
    private static extern IntPtr FreeWindowsArguments(IntPtr allocation);
}
