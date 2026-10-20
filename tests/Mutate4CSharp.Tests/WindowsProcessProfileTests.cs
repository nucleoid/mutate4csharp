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
        var selected = Environment.GetEnvironmentVariable("MUTATE4CSHARP_TEST_PWSH") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PowerShell", "7", "pwsh.exe");
        Assert.True(Path.IsPathFullyQualified(selected) && File.Exists(selected), "An actual existing absolute PowerShell7 executable is required; set MUTATE4CSHARP_TEST_PWSH or install the CI default.");
        return selected;
    }

    [Fact]
    public async Task Genuine_Windows_child_accepts_environment_above_32Ki_without_losing_values_or_clears()
    {
        if (!OperatingSystem.IsWindows()) return;
        await using var owned = OwnedDirectory.Create(OwnedDirectory.PrivateParent("windows-wide-env"), "fixture");
        var nonce = Guid.NewGuid().ToString("N");
        var environment = new Dictionary<string, string?> {
            ["MUTATE_WIDE_ENV_A"] = new('a', 20_000), ["MUTATE_WIDE_ENV_B"] = new('b', 20_000),
            ["MUTATE_WIDE_ENV_SENTINEL"] = nonce, ["ComSpec"] = null };
        Assert.False(string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ComSpec")), "The actual closed Windows fixture requires inherited ComSpec before its explicit clear.");
        var shell = PowerShellExecutable();
        var observe = """
            $a=[Environment]::GetEnvironmentVariable('MUTATE_WIDE_ENV_A')
            $b=[Environment]::GetEnvironmentVariable('MUTATE_WIDE_ENV_B')
            @{LengthA=$a.Length;LengthB=$b.Length;HashA=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($a)));HashB=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($b)));Sentinel=[Environment]::GetEnvironmentVariable('MUTATE_WIDE_ENV_SENTINEL');Cleared=($null -eq [Environment]::GetEnvironmentVariable('ComSpec'))}|ConvertTo-Json -Compress
            """;
        var actual = await ProcessTree.RunAsync(shell, ["-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(observe))], owned.Root,
            TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken, environment);
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
        if (mode == "row") environment["OVERSIZED"] = new('x', WindowsProcessTree.MaximumEnvironmentVariableCharacters);
        else if (mode == "count")
            for (var index = 0; index <= WindowsProcessTree.MaximumEnvironmentVariables; index++) environment["ENV_" + index] = "x";
        else for (var index = 0; index < 512; index++) environment["ENV_" + index] = new('x', 9000);
        var marker = Path.Combine(owned.Root, "must-not-be-born.txt");
        var shell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
        var refusal = await Assert.ThrowsAsync<ArgumentException>(() => ProcessTree.RunAsync(shell,
            ["/d", "/c", "echo born>must-not-be-born.txt"], owned.Root, TimeSpan.FromSeconds(15),
            TestContext.Current.CancellationToken, environment));
        Assert.Contains("before native birth", refusal.Message); Assert.False(File.Exists(marker));
        WindowsProcessTree.AssertDirectoryMayBeRemoved(owned.Root);
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
        var run = ProcessTree.RunAsync(shell, ["-NoProfile", "-NonInteractive", "-EncodedCommand", Encoded(root)], owned.Root,
            TimeSpan.FromSeconds(8), cancellation.Token, originalBudget: original);
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
                Assert.NotNull(retired.Cleanup); Assert.True(original.RemainingDisposition > TimeSpan.Zero);
                Assert.Throws<EvaluationContractException>(original.CheckWork);
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
}
