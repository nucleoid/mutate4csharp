using System.Diagnostics;

namespace Mutate4CSharp.Tests;

public sealed class IssueFiveReviewRoundSevenTests : IDisposable
{
    private static readonly string RepositoryRoot = Path.GetFullPath("../../../../../", AppContext.BaseDirectory);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mutate4csharp-issue5-r7",
        Guid.NewGuid().ToString("N"));

    public IssueFiveReviewRoundSevenTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task DefaultStatefulFingerprintProbeDoesNotRequireStrictLinuxSetSid()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var repository = new SnapshotTestRepository();
        repository.WriteText("src/App/App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />\n");
        repository.WriteText("tests/App.Tests/App.Tests.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />\n");
        repository.WriteText("src/App/A.cs", "class A { int Value() => 1; }\n");
        repository.WriteText("mutate4csharp.json", StrictConfiguration);
        repository.Git("add", ".");
        repository.Git("commit", "-m", "base");
        repository.WriteText("src/App/A.cs", "class A { int Value() => 2; }\n");
        var tools = Path.Combine(_root, "tools");
        Directory.CreateDirectory(tools);
        WriteExecutable(Path.Combine(tools, "setsid"), "#!/bin/sh\nexit 64\n");
        var report = Path.Combine(_root, "stateful-report.json");
        var originalDirectory = Environment.CurrentDirectory;
        var originalPath = Environment.GetEnvironmentVariable("PATH");
        try
        {
            Environment.CurrentDirectory = repository.Root;
            Environment.SetEnvironmentVariable("PATH", tools + Path.PathSeparator + originalPath);
            var result = await new EvaluationCoordinator().RunAsync(
                new(false, "HEAD", [], report, "stateful-without-setsid"),
                TestContext.Current.CancellationToken);

            Assert.DoesNotContain(result.Report.IncompleteConditions,
                item => item.Code == "SIDECAR_WRITE_FAILED");
            Assert.DoesNotContain(result.Report.Evidence, item => item.Kind == "SIDECAR_WRITE_FAILURE");
            Assert.True(Directory.Exists(Path.Combine(repository.Root, ".mutate4csharp", "discovery")));
        }
        finally
        {
            Environment.CurrentDirectory = originalDirectory;
            Environment.SetEnvironmentVariable("PATH", originalPath);
        }
    }

    [Fact]
    public async Task LinuxCleanupRecapturesDescendantThatEscapesIntoANewSession()
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/usr/bin/setsid")) return;
        var marker = Path.Combine(_root, "escaped.pid");
        var command = $"/usr/bin/setsid /bin/sh -c 'printf %s $$ > \"$1\"; exec /bin/sleep 300' child {Shell(marker)} " +
            "</dev/null >/dev/null 2>&1 & " +
            $"while [ ! -s {Shell(marker)} ]; do /bin/sleep 0.01; done; /bin/sleep 0.2";
        var run = ProcessTree.RunAsync("/bin/sh", ["-c", command], _root, TimeSpan.FromSeconds(15),
            TestContext.Current.CancellationToken, requireLinuxSessionIsolation: true);
        await WaitForFileAsync(marker, TimeSpan.FromSeconds(10));
        using var escaped = Process.GetProcessById(int.Parse(File.ReadAllText(marker),
            System.Globalization.CultureInfo.InvariantCulture));
        var escapedStart = escaped.StartTime;
        try
        {
            var result = await run;
            Assert.Equal(0, result.ExitCode);
            Assert.True(escaped.HasExited, $"Escaped descendant {escaped.Id} survived successful cleanup.");
        }
        finally
        {
            try
            {
                if (!escaped.HasExited && escaped.StartTime == escapedStart)
                {
                    escaped.Kill(entireProcessTree: true);
                    escaped.WaitForExit(5_000);
                }
            }
            catch (InvalidOperationException) { }
        }
    }

    [Fact]
    public void StrictSuiteAliasesCollapseByNormalizedPathBeforeScopeConstruction()
    {
        var configuration = ScopeConfiguration.Load(_root,
            System.Text.Encoding.UTF8.GetBytes(StrictConfiguration.ReplaceLineEndings("\n").Replace(
                "\"testSuites\": [\"unit\"]", "\"testSuites\": [\"unit\", \"unit-alias\"]",
                StringComparison.Ordinal).Replace(
                "}],\n  \"policy\"", "}, {\n    \"id\": \"unit-alias\", \"path\": \"tests/App.Tests/App.Tests.csproj\", \"runner\": \"vstest\",\n    \"framework\": \"net10.0\", \"configuration\": \"Release\", \"expectedMembers\": [\"App.Tests.dll\"]\n  }],\n  \"policy\"", StringComparison.Ordinal)));

        var project = Assert.Single(configuration.Projects);
        Assert.Equal(["tests/App.Tests/App.Tests.csproj"], project.Tests);
        var unit = Assert.Single(ProjectOwnershipResolver.Resolve(configuration, ["src/App/A.cs"]).Units);
        Assert.Equal(["tests/App.Tests/App.Tests.csproj"], unit.Tests);
    }

    [Fact]
    public async Task NonGitExplicitRunReportsValidatedConfiguredPolicy()
    {
        var source = Path.Combine(_root, "src", "App", "A.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        File.WriteAllText(source, "class A { int Value() => 1; }\n");
        File.WriteAllText(Path.Combine(_root, "mutate4csharp.json"), StrictConfiguration);
        var report = Path.Combine(_root, "explicit-report.json");
        var originalDirectory = Environment.CurrentDirectory;
        try
        {
            Environment.CurrentDirectory = _root;
            var result = await new EvaluationCoordinator().RunAsync(
                new(false, null, [source], report, "explicit-policy", NoState: true),
                TestContext.Current.CancellationToken);

            Assert.Equal(3, result.Report.Policy.MaxWorkers);
            Assert.Contains(result.Report.Evidence, item => item.Kind == "CHECK_CONFIGURATION");
        }
        finally
        {
            Environment.CurrentDirectory = originalDirectory;
        }
    }

    [Fact]
    public void CleanupPathsNeverUseAnUnboundRawProcessGroupPid()
    {
        var source = File.ReadAllText(Path.Combine(RepositoryRoot, "src/Mutate4CSharp/ExecutionEnvironment.cs"));
        Assert.DoesNotContain("TryKillLinuxProcessGroup(process.Id);", source, StringComparison.Ordinal);
    }

    private static void WriteExecutable(string path, string content)
    {
        File.WriteAllText(path, content.ReplaceLineEndings("\n"));
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private static string Shell(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    private static async Task WaitForFileAsync(string path, TimeSpan timeout)
    {
        var watch = Stopwatch.StartNew();
        while (!File.Exists(path) || new FileInfo(path).Length == 0)
        {
            if (watch.Elapsed >= timeout) throw new TimeoutException("Fixture child process did not start.");
            await Task.Delay(25, TestContext.Current.CancellationToken);
        }
    }

    private const string StrictConfiguration = """
        {
          "version": 1,
          "projects": [{
            "id": "app", "project": "src/App/App.csproj", "targetFramework": "net10.0",
            "parseContext": "default", "sources": ["src/App/**/*.cs"], "testSuites": ["unit"]
          }],
          "testSuites": [{
            "id": "unit", "path": "tests/App.Tests/App.Tests.csproj", "runner": "vstest",
            "framework": "net10.0", "configuration": "Release", "expectedMembers": ["App.Tests.dll"]
          }],
          "policy": { "maxWorkers": 3 }
        }
        """;

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }
}
