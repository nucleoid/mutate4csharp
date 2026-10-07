using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Json.Schema;

namespace Mutate4CSharp.Tests;

public sealed class IssueFiveReviewRoundFiveTests : IDisposable
{
    private static readonly string RepositoryRoot = Path.GetFullPath("../../../../../", AppContext.BaseDirectory);
    private static readonly string SchemaPath = Path.Combine(RepositoryRoot, "docs/contracts/check-config-v1.schema.json");
    private static readonly JsonSchema Schema = ConfigurationSchemaLoader.Load(File.ReadAllText(SchemaPath));
    private static readonly Lazy<NodeProbeResults> NodePathProbe = new(EvaluatePathCasesWithNode,
        LazyThreadSafetyMode.ExecutionAndPublication);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mutate4csharp-issue5-r5", Guid.NewGuid().ToString("N"));

    public IssueFiveReviewRoundFiveTests() => Directory.CreateDirectory(_root);

    [Theory]
    [MemberData(nameof(EcmaPathRejectionCases))]
    public void PathSchemaRejectsPostLineSeparatorHazardsUnderRealEcmaSemantics(string path)
    {
        var pattern = ReadPathPattern();
        Assert.False(EvaluateWithNode(pattern, path));
        Assert.False(SchemaAccepts(WithSourcePath(path)));
        Assert.False(RuntimeAccepts(WithSourcePath(path)));
    }

    [Theory]
    [InlineData("src/A\u2028safe.cs")]
    [InlineData("src/A\u2029safe.cs")]
    public void PathSchemaStillAcceptsSafeLineSeparatorsUnderRealEcmaSemantics(string path)
    {
        Assert.True(EvaluateWithNode(ReadPathPattern(), path));
        Assert.True(RuntimeAccepts(WithSourcePath(path)));
    }

    public static IEnumerable<object[]> EcmaPathRejectionCases()
    {
        foreach (var separator in new[] { "\u2028", "\u2029" })
        {
            yield return [$"src/A{separator}/../../outside.cs"];
            yield return [$"src/A{separator}\u0001.cs"];
            yield return [$"src/A{separator}/"];
            yield return [$"src/A{separator}\\x.cs"];
            yield return [$"src/A{separator}//x.cs"];
            yield return [$"src/A{separator}x "];
        }
    }

    [Theory]
    [InlineData("maxWorkers", "2", "1.00000000000000000000000000000001", false)]
    [InlineData("maxWorkers", "2", "0.99999999999999999999999999999999", false)]
    [InlineData("maxWorkers", "2", "2.00000000000000000000000000000000", true)]
    [InlineData("version", "1", "1.00000000000000000000000000000001", false)]
    [InlineData("version", "1", "0.99999999999999999999999999999999", false)]
    [InlineData("version", "1", "1.00000000000000000000000000000000", true)]
    public void SchemaAndRuntimeAgreeOnConversionSensitiveExactIntegers(
        string property, string original, string token, bool expected)
    {
        var json = Valid.Replace($"\"{property}\": {original}", $"\"{property}\": {token}", StringComparison.Ordinal);
        Assert.Equal(expected, SchemaAccepts(json));
        Assert.Equal(expected, RuntimeAccepts(json));
    }

    [Fact]
    public async Task SchedulerRetainsAccountingFailureWhenBoundingEvidence()
    {
        var suites = Enumerable.Range(0, 150)
            .Select(_ => new SuiteMutationEvidence("one", SuiteRunDisposition.Killed, [], []))
            .ToArray();
        var aggregate = SuiteCoordinator.AggregateMutant(MutationId, ["one"], suites);
        var result = await ScheduleAsync(aggregate);

        Assert.Equal(UnitDisposition.Error, result.Disposition);
        Assert.InRange(result.Evidence.Count, 1, 100);
        Assert.Contains(result.Evidence, item => item.Kind == "SUITE_EVIDENCE_MISSING" &&
            item.Diagnostics!.Contains("received=150"));
    }

    [Fact]
    public async Task SchedulerExplainsDeterministicEvidenceTruncation()
    {
        var ids = Enumerable.Range(0, 150).Select(index => $"suite-{index:D3}").ToArray();
        var suites = ids.Reverse().Select(id =>
            new SuiteMutationEvidence(id, SuiteRunDisposition.Killed, [], [])).ToArray();
        var aggregate = SuiteCoordinator.AggregateMutant(MutationId, ids, suites);
        var result = await ScheduleAsync(aggregate);

        Assert.Equal(UnitDisposition.Killed, result.Disposition);
        Assert.Equal(100, result.Evidence.Count);
        var truncation = Assert.Single(result.Evidence, item => item.Kind == "SUITE_EVIDENCE_TRUNCATED");
        Assert.Contains("received=150", truncation.Diagnostics!);
        Assert.Contains("retained=99", truncation.Diagnostics!);
        Assert.Equal("SUITE_EVIDENCE_TRUNCATED", result.Evidence[0].Kind);
        Assert.Contains("suite-000", result.Evidence[1].Summary, StringComparison.Ordinal);
        Assert.Contains("suite-098", result.Evidence[^1].Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void SurvivorRerunArgvPreservesExactBackslashesAndUnicode()
    {
        var path = "src/back\\slash/😀-\u2028-file.cs";
        var diagnostic = Assert.Single(SuiteCoordinator.SurvivorEvidence(
            MutationId, path, 7, "a", "b", Fingerprint).Diagnostics!);

        Assert.InRange(diagnostic.Length, 1, 512);
        Assert.StartsWith("rerun-argv-json=", diagnostic, StringComparison.Ordinal);
        var argv = JsonSerializer.Deserialize<string[]>(diagnostic["rerun-argv-json=".Length..]);
        Assert.NotNull(argv);
        Assert.Equal(path, argv![Array.IndexOf(argv, "--input") + 1]);
    }

    [Fact]
    public void SurvivorRerunArgvIsExplicitlyOmittedWhenExactReplayCannotFit()
    {
        var path = "src/" + string.Concat(Enumerable.Repeat("😀\\", 300)) + "file.cs";
        var evidence = SuiteCoordinator.SurvivorEvidence(MutationId, path, 7, "a", "b", Fingerprint);
        var diagnostic = Assert.Single(evidence.Diagnostics!);

        Assert.InRange(diagnostic.Length, 1, 512);
        Assert.StartsWith("rerun-argv-omitted=", diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain("rerun-argv-json=", diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain('\uFFFD', evidence.Summary);
        Assert.False(HasUnpairedSurrogate(evidence.Summary));
    }

    [Fact]
    public async Task SetSidDiscoveryUsesPathAndDoesNotCacheTransientProbeFailure()
    {
        if (!OperatingSystem.IsLinux()) return;
        var count = Path.Combine(_root, "transient-count");
        var setsid = WriteExecutable("setsid", $$"""
            #!/bin/sh
            count=0
            if [ -f '{{count}}' ]; then count=$(/bin/cat '{{count}}'); fi
            count=$((count + 1))
            printf '%s' "$count" > '{{count}}'
            if [ "$1" = '--help' ]; then
              if [ "$count" -eq 1 ]; then exit 70; fi
              printf '%s\n' 'Usage: setsid [options] program' ' -w, --wait wait program exit'
              exit 0
            fi
            if [ "$1" = '--wait' ]; then shift; "$@"; exit $?; fi
            exit 64
            """);
        _ = setsid;

        await WithPathAsync(_root, async () =>
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => ProcessTree.RunAsync(
                "/bin/sh", ["-c", "exit 0"], _root, TimeSpan.FromSeconds(5), CancellationToken.None,
                requireLinuxSessionIsolation: true));
            var result = await ProcessTree.RunAsync(
                "/bin/sh", ["-c", "exit 0"], _root, TimeSpan.FromSeconds(5), CancellationToken.None,
                requireLinuxSessionIsolation: true);
            Assert.Equal(0, result.ExitCode);
        });
        Assert.True(int.Parse(File.ReadAllText(count), System.Globalization.CultureInfo.InvariantCulture) >= 4);
    }

    [Fact]
    public async Task SetSidProbeIsBoundedAndRejectsHelpOnlyWaitClaims()
    {
        if (!OperatingSystem.IsLinux()) return;
        WriteExecutable("setsid", """
            #!/bin/sh
            if [ "$1" = '--help' ]; then
              printf '%s\n' 'Usage: setsid [options] program' ' -w, --wait wait program exit'
              exit 0
            fi
            if [ "$1" = '--wait' ]; then exit 0; fi
            exit 64
            """);

        await WithPathAsync(_root, async () =>
            await Assert.ThrowsAsync<InvalidOperationException>(() => ProcessTree.RunAsync(
                "/bin/sh", ["-c", "exit 0"], _root, TimeSpan.FromSeconds(5), CancellationToken.None,
                requireLinuxSessionIsolation: true)));

        WriteExecutable("setsid", """
            #!/bin/sh
            if [ "$1" = '--help' ]; then sleep 60; fi
            exit 64
            """);
        var watch = Stopwatch.StartNew();
        await WithPathAsync(_root, async () =>
            await Assert.ThrowsAsync<InvalidOperationException>(() => ProcessTree.RunAsync(
                "/bin/sh", ["-c", "exit 0"], _root, TimeSpan.FromSeconds(5), CancellationToken.None,
                requireLinuxSessionIsolation: true)));
        Assert.InRange(watch.Elapsed, TimeSpan.Zero, TimeSpan.FromSeconds(8));
    }

    [Fact]
    public async Task CancellationCleanupRunsDrainAndReapAndAggregatesBothFailures()
    {
        var method = typeof(ProcessTree).GetMethod("CompleteCancellationCleanupAsync",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        var primary = new TimeoutException("primary timeout");
        var drainCalled = false;
        var reapCalled = false;
        Task Drain()
        {
            drainCalled = true;
            return Task.FromException(new IOException("drain failed"));
        }
        Task Reap()
        {
            reapCalled = true;
            return Task.FromException(new IOException("reap failed"));
        }

        var invocation = Assert.IsAssignableFrom<Task>(method!.Invoke(null,
            [primary, (Func<Task>)Drain, (Func<Task>)Reap]));
        var error = await Assert.ThrowsAsync<SnapshotCleanupException>(() => invocation);

        Assert.True(drainCalled);
        Assert.True(reapCalled);
        Assert.Same(primary, error.OriginalFailure);
        var cleanup = Assert.IsType<AggregateException>(error.CleanupFailure);
        Assert.Equal(2, cleanup.InnerExceptions.Count);
    }

    private async Task<ScheduledMutationResult> ScheduleAsync(AggregatedMutation aggregate)
    {
        var scheduled = new ScheduledMutation(MutationId, EvaluationId, ["one"]);
        var executor = new DelegateMutationExecutor((mutation, _, _) => Task.FromResult(
            new ScheduledMutationResult(mutation.EvaluationUnitId, aggregate.Disposition, aggregate.Evidence)));
        var schedule = await new EvaluationScheduler(executor, TimeProvider.System).RunAsync(
            [scheduled], 1, TimeSpan.FromSeconds(5), DateTimeOffset.UtcNow.AddMinutes(1), CancellationToken.None);
        return Assert.Single(schedule.Results);
    }

    private static string ReadPathPattern()
    {
        using var schema = JsonDocument.Parse(File.ReadAllText(SchemaPath));
        return schema.RootElement.GetProperty("$defs").GetProperty("path").GetProperty("pattern").GetString()!;
    }

    private static bool EvaluateWithNode(string pattern, string input)
    {
        var probe = NodePathProbe.Value;
        Assert.Equal(pattern, probe.Pattern);
        return probe.Results[input];
    }

    private static NodeProbeResults EvaluatePathCasesWithNode()
    {
        var pattern = ReadPathPattern();
        var inputs = EcmaPathRejectionCases().Select(item => Assert.IsType<string>(Assert.Single(item)))
            .Concat(["src/A\u2028safe.cs", "src/A\u2029safe.cs"])
            .Distinct(StringComparer.Ordinal).ToArray();
        var info = new ProcessStartInfo("node")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        info.ArgumentList.Add("-e");
        info.ArgumentList.Add("let s='';process.stdin.setEncoding('utf8');process.stdin.on('data',c=>s+=c);" +
            "process.stdin.on('end',()=>{const x=JSON.parse(s),r=new RegExp(x.pattern);" +
            "process.stdout.write(JSON.stringify(x.inputs.map(v=>r.test(v))))})");
        using var process = Process.Start(info) ?? throw new InvalidOperationException("Could not start Node.js ECMA probe.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        process.StandardInput.Write(JsonSerializer.Serialize(new { pattern, inputs }));
        process.StandardInput.Close();
        if (!process.WaitForExit(30_000))
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            process.WaitForExit(5_000);
            throw new TimeoutException("Single batched Node.js ECMA probe timed out after 30 seconds.");
        }
        var standardError = error.GetAwaiter().GetResult();
        Assert.True(process.ExitCode == 0,
            $"Node.js ECMA probe exited {process.ExitCode}: {standardError}");
        var values = JsonSerializer.Deserialize<bool[]>(output.GetAwaiter().GetResult());
        Assert.NotNull(values);
        Assert.Equal(inputs.Length, values!.Length);
        return new(pattern, inputs.Select((input, index) => (input, values[index]))
            .ToDictionary(item => item.input, item => item.Item2, StringComparer.Ordinal));
    }

    private sealed record NodeProbeResults(string Pattern, IReadOnlyDictionary<string, bool> Results);

    private bool SchemaAccepts(string json)
    {
        using var document = JsonDocument.Parse(json);
        return Schema.Evaluate(document.RootElement).IsValid;
    }

    private bool RuntimeAccepts(string json)
    {
        try { _ = CheckConfiguration.Load(_root, Encoding.UTF8.GetBytes(json)); return true; }
        catch (ArgumentException) { return false; }
    }

    private static string WithSourcePath(string path) => Valid.Replace(
        "\"src/App/**/*.cs\"", JsonSerializer.Serialize(path), StringComparison.Ordinal);

    private string WriteExecutable(string name, string content)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllText(path, content.ReplaceLineEndings("\n"));
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    private static async Task WithPathAsync(string path, Func<Task> action)
    {
        var original = Environment.GetEnvironmentVariable("PATH");
        Environment.SetEnvironmentVariable("PATH", path);
        try { await action(); }
        finally { Environment.SetEnvironmentVariable("PATH", original); }
    }

    private static bool HasUnpairedSurrogate(string value)
    {
        for (var index = 0; index < value.Length; index++)
        {
            if (char.IsHighSurrogate(value[index]))
            {
                if (++index >= value.Length || !char.IsLowSurrogate(value[index])) return true;
            }
            else if (char.IsLowSurrogate(value[index])) return true;
        }
        return false;
    }

    private sealed class DelegateMutationExecutor(
        Func<ScheduledMutation, TimeSpan, CancellationToken, Task<ScheduledMutationResult>> execute) :
        IIsolatedMutationExecutor
    {
        public Task<ScheduledMutationResult> ExecuteAsync(ScheduledMutation mutation, TimeSpan timeout,
            CancellationToken cancellationToken) => execute(mutation, timeout, cancellationToken);
    }

    private const string MutationId = "mutation:v1:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string EvaluationId = "evaluation:v1:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string Fingerprint = "sha256:cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";
    private const string Valid = """
        { "version": 1,
          "projects": [{ "id": "app", "project": "src/App/App.csproj", "targetFramework": "net10.0",
            "parseContext": "default", "sources": ["src/App/**/*.cs"], "testSuites": ["unit"] }],
          "testSuites": [{ "id": "unit", "path": "tests/App.Tests/App.Tests.csproj", "runner": "vstest",
            "framework": "net10.0", "configuration": "Release", "expectedMembers": ["App.Tests.dll"] }],
          "exclusions": [{ "path": "generated/**/*.cs", "reason": "generated" }],
          "policy": { "maxWorkers": 2 } }
        """;

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }
}
