using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Json.Schema;

namespace Mutate4CSharp.Tests;

public sealed class IssueFiveReviewRoundSixTests : IDisposable
{
    private static readonly string RepositoryRoot = Path.GetFullPath("../../../../../", AppContext.BaseDirectory);
    private static readonly string SchemaPath = Path.Combine(RepositoryRoot, "docs/contracts/check-config-v1.schema.json");
    private static readonly JsonSchema Schema = ConfigurationSchemaLoader.Load(File.ReadAllText(SchemaPath));
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mutate4csharp-issue5-r6", Guid.NewGuid().ToString("N"));

    public IssueFiveReviewRoundSixTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task LegacyProcessLaunchDoesNotRequireSetSidAndDiscoverySkipsNonExecutableCandidates()
    {
        if (!OperatingSystem.IsLinux()) return;
        var candidate = Path.Combine(_root, "setsid");
        File.WriteAllText(candidate, "not executable\n");
        File.SetUnixFileMode(candidate, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        await WithPathAsync(_root, async () =>
        {
            var result = await ProcessTree.RunAsync("/bin/sh", ["-c", "printf legacy"], _root,
                TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.Equal(0, result.ExitCode);
            Assert.Equal("legacy", result.StandardOutput);

            var find = typeof(ProcessTree).GetMethod("FindLinuxSetSid", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(find);
            Assert.NotEqual(candidate, Assert.IsType<string>(find!.Invoke(null, null)));
        });
    }

    [Fact]
    public async Task SetSidProbeStartFailureHasBoundedCompatibilityDiagnostic()
    {
        if (!OperatingSystem.IsLinux()) return;
        var candidate = Path.Combine(_root, "setsid");
        File.WriteAllText(candidate, "#!/missing/interpreter\n");
        File.SetUnixFileMode(candidate, UnixFileMode.UserRead | UnixFileMode.UserExecute);

        await WithPathAsync(_root, () =>
        {
            var resolve = typeof(ProcessTree).GetMethod("ResolveLinuxSetSid", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(resolve);
            var invocation = Assert.Throws<TargetInvocationException>(() => resolve!.Invoke(null, null));
            var error = Assert.IsType<InvalidOperationException>(invocation.InnerException);
            Assert.Contains("setsid", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.InRange(error.Message.Length, 1, 512);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public void CiUsesTheAuthoritativeSerializedXunitMode()
    {
        var workflow = File.ReadAllText(Path.Combine(RepositoryRoot, ".github/workflows/ci.yml"));
        Assert.Contains("xUnit.MaxParallelThreads=1", workflow, StringComparison.Ordinal);
        var assembly = File.ReadAllText(Path.Combine(RepositoryRoot,
            "tests/Mutate4CSharp.Tests/AssemblyInfo.cs"));
        Assert.Contains("DisableTestParallelization = true", assembly, StringComparison.Ordinal);
    }

    [Fact]
    public void OversizedExponentLexemesAreRejectedWithSchemaRuntimeParity()
    {
        var positive = Valid.Replace("\"maxWorkers\": 2", "\"maxWorkers\": 1e1000000", StringComparison.Ordinal);
        var negative = Valid.Replace("\"maxWorkers\": 2", "\"maxWorkers\": 1e-1000000", StringComparison.Ordinal);
        var saturating = Valid.Replace("\"version\": 1",
            "\"version\": 1" + new string('0', 100_000) + "e-1000000", StringComparison.Ordinal);

        foreach (var json in new[] { positive, negative, saturating })
        {
            Assert.False(SchemaAccepts(json));
            Assert.False(RuntimeAccepts(json));
        }
    }

    [Fact]
    public async Task SchedulerRetainsCountersAndTruncationForLargeAccountingFailure()
    {
        var mapped = Enumerable.Range(0, 30).Select(index => $"suite-{index:D2}").ToArray();
        var supplied = Enumerable.Range(30, 30).Select(index =>
            new SuiteMutationEvidence($"suite-{index:D2}", SuiteRunDisposition.Killed, [], [])).ToArray();
        var aggregate = SuiteCoordinator.AggregateMutant(MutationId, mapped, supplied);
        var result = await ScheduleAsync(mapped, aggregate);
        var accounting = Assert.Single(result.Evidence, item => item.Kind == "SUITE_EVIDENCE_MISSING");

        Assert.Contains("received=30", accounting.Diagnostics!);
        Assert.Contains("missing-count=30", accounting.Diagnostics!);
        Assert.Contains("unknown-count=30", accounting.Diagnostics!);
        Assert.Contains(accounting.Diagnostics!, value => value.StartsWith("diagnostics-truncated=", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SchedulerRetainsFailedAndDiagnosticCountersAndTruncation()
    {
        var suite = new SuiteMutationEvidence("one", SuiteRunDisposition.Failed,
            Enumerable.Range(0, 80).Select(index => $"failed-{index:D2}").ToArray(),
            Enumerable.Range(0, 40).Select(index => $"diagnostic-{index:D2}").ToArray());
        var result = await ScheduleAsync(["one"], SuiteCoordinator.AggregateMutant(MutationId, ["one"], [suite]));
        var evidence = Assert.Single(result.Evidence);

        Assert.Contains("failed-count=80", evidence.Diagnostics!);
        Assert.Contains("diagnostic-count=40", evidence.Diagnostics!);
        Assert.Contains(evidence.Diagnostics!, value => value.StartsWith("diagnostics-truncated=", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("parseContext")]
    [InlineData("expectedMembers")]
    [InlineData("reason")]
    public void EveryFreeFormStringPatternRejectsTrailingWhitespaceAcrossEcmaLineSeparators(string property)
    {
        var pattern = ReadPattern(property);
        var value = "x\u2028y\u2029 ";
        Assert.False(EvaluateWithNode(pattern, value));

        var json = property switch
        {
            "parseContext" => Valid.Replace("\"default\"", JsonSerializer.Serialize(value), StringComparison.Ordinal),
            "expectedMembers" => Valid.Replace("\"App.Tests.dll\"", JsonSerializer.Serialize(value), StringComparison.Ordinal),
            _ => Valid.Replace("\"generated\"", JsonSerializer.Serialize(value), StringComparison.Ordinal)
        };
        Assert.False(SchemaAccepts(json));
        Assert.False(RuntimeAccepts(json));
    }

    [Fact]
    public void ExpectedMemberCaseInsensitiveUniquenessIsDocumented()
    {
        using var schema = JsonDocument.Parse(File.ReadAllText(SchemaPath));
        var members = schema.RootElement.GetProperty("properties").GetProperty("testSuites")
            .GetProperty("items").GetProperty("properties").GetProperty("expectedMembers");
        Assert.Contains("case-insensitive", members.GetProperty("description").GetString(),
            StringComparison.OrdinalIgnoreCase);
        Assert.False(RuntimeAccepts(Valid.Replace("[\"App.Tests.dll\"]",
            "[\"App.Tests.dll\",\"app.tests.dll\"]", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData("base")]
    [InlineData("inputs")]
    public void SurvivorRerunRoundTripsOriginalSelectionMode(string kind)
    {
        var selection = kind == "base"
            ? new EvaluationSelection("base", "origin/main", [])
            : new EvaluationSelection("inputs", null, ["src/A.cs", "src/B.cs"]);
        var method = typeof(SuiteCoordinator).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .SingleOrDefault(candidate => candidate.Name == nameof(SuiteCoordinator.SurvivorEvidence) &&
                candidate.GetParameters().Any(parameter => parameter.ParameterType == typeof(EvaluationSelection)));
        Assert.NotNull(method);
        var evidence = Assert.IsType<EvaluationEvidence>(method!.Invoke(null,
            [MutationId, "src/A.cs", 7, "a", "b", selection, Fingerprint]));
        var diagnostic = Assert.Single(evidence.Diagnostics!);
        var argv = JsonSerializer.Deserialize<string[]>(diagnostic["rerun-argv-json=".Length..])!;

        if (kind == "base")
        {
            Assert.Equal("origin/main", argv[Array.IndexOf(argv, "--base") + 1]);
            Assert.DoesNotContain("--input", argv);
        }
        else
        {
            Assert.DoesNotContain("--base", argv);
            Assert.Equal(selection.Inputs, argv.Where((_, index) => index > 0 && argv[index - 1] == "--input"));
        }
    }

    [Fact]
    public void LinuxIdentityContinuityUsesPidAndStartTimeDespiteSessionOrGroupChange()
    {
        if (!OperatingSystem.IsLinux()) return;
        var identityType = typeof(ProcessTree).GetNestedType("LinuxProcessIdentity", BindingFlags.NonPublic)!;
        var read = typeof(ProcessTree).GetMethod("TryReadLinuxIdentity", BindingFlags.NonPublic | BindingFlags.Static)!;
        var current = new object?[] { Environment.ProcessId, null };
        Assert.True((bool)read.Invoke(null, current)!);
        var identity = current[1]!;
        var start = (ulong)identityType.GetProperty("StartTime")!.GetValue(identity)!;
        var parent = (int)identityType.GetProperty("ParentPid")!.GetValue(identity)!;
        var group = (int)identityType.GetProperty("ProcessGroupId")!.GetValue(identity)!;
        var session = (int)identityType.GetProperty("SessionId")!.GetValue(identity)!;
        var forged = Activator.CreateInstance(identityType,
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public, null,
            [Environment.ProcessId, parent, group + 1, session + 1, start, false], null)!;
        var same = typeof(ProcessTree).GetMethod("IsSameLinuxProcess", BindingFlags.NonPublic | BindingFlags.Static)!;

        Assert.True((bool)same.Invoke(null, [forged])!);
    }

    private async Task<ScheduledMutationResult> ScheduleAsync(IReadOnlyList<string> suites, AggregatedMutation aggregate)
    {
        var scheduled = new ScheduledMutation(MutationId, EvaluationId, suites);
        var executor = new DelegateMutationExecutor((mutation, _, _) => Task.FromResult(
            new ScheduledMutationResult(mutation.EvaluationUnitId, aggregate.Disposition, aggregate.Evidence)));
        var schedule = await new EvaluationScheduler(executor, TimeProvider.System).RunAsync(
            [scheduled], 1, TimeSpan.FromSeconds(5), DateTimeOffset.UtcNow.AddMinutes(1), CancellationToken.None);
        return Assert.Single(schedule.Results);
    }

    private static string ReadPattern(string property)
    {
        using var schema = JsonDocument.Parse(File.ReadAllText(SchemaPath));
        var root = schema.RootElement;
        return property switch
        {
            "parseContext" => root.GetProperty("properties").GetProperty("projects").GetProperty("items")
                .GetProperty("properties").GetProperty(property).GetProperty("pattern").GetString()!,
            "expectedMembers" => root.GetProperty("properties").GetProperty("testSuites").GetProperty("items")
                .GetProperty("properties").GetProperty(property).GetProperty("items").GetProperty("pattern").GetString()!,
            _ => root.GetProperty("properties").GetProperty("exclusions").GetProperty("items")
                .GetProperty("properties").GetProperty(property).GetProperty("pattern").GetString()!
        };
    }

    private static bool EvaluateWithNode(string pattern, string input)
    {
        var info = new ProcessStartInfo("node") { RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false };
        info.ArgumentList.Add("-e");
        info.ArgumentList.Add("const [p,s]=process.argv.slice(1); process.exit(new RegExp(p).test(s)?0:1)");
        info.ArgumentList.Add(pattern);
        info.ArgumentList.Add(input);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("Could not start Node.js.");
        Assert.True(process.WaitForExit(10_000));
        return process.ExitCode == 0;
    }

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

    private static async Task WithPathAsync(string path, Func<Task> action)
    {
        var original = Environment.GetEnvironmentVariable("PATH");
        Environment.SetEnvironmentVariable("PATH", path);
        try { await action(); }
        finally { Environment.SetEnvironmentVariable("PATH", original); }
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
