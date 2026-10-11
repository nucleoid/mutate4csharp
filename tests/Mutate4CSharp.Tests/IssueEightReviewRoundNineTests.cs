using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mutate4CSharp.Tests;

public sealed class IssueEightReviewRoundNineTests : IDisposable
{
    private const int OrchestrationRefusal = 73;
    private static readonly string RepositoryRoot = Path.GetFullPath("../../../../../", AppContext.BaseDirectory);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mutate4csharp-round-nine", Guid.NewGuid().ToString("N"));

    public IssueEightReviewRoundNineTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void CiUsesOnlyTheSupportedIsolatedPackPath()
    {
        var workflow = Read(".github/workflows/ci.yml");
        Assert.DoesNotContain("dotnet pack src/Mutate4CSharp/Mutate4CSharp.csproj", workflow, StringComparison.Ordinal);
        Assert.Contains("scripts/agent-gate.sh prepare", workflow, StringComparison.Ordinal);
        Assert.Contains("IssueEightReviewRoundTenTests", workflow, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DirectPackWithoutIsolationContractFailsWithStableDiagnostic()
    {
        var revision = (await RunAsync(RepositoryRoot, "git", "rev-parse", "HEAD")).StandardOutput.Trim();
        var result = await RunAsync(RepositoryRoot, DotnetHost(), "pack",
            "src/Mutate4CSharp/Mutate4CSharp.csproj", "-c", "Release", "-m:1", "--no-restore",
            $"-p:Version=0.1.0-local.{revision}", $"-p:SourceRevisionId={revision}",
            "-o", Path.Combine(_root, "unsupported-direct-feed"));
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Direct pack is fail closed", result.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public void PackContractCoversExternalPathsHooksWildcardsResponseFilesAndGlobalConfig()
    {
        var project = Read("src/Mutate4CSharp/Mutate4CSharp.csproj");
        var script = Read("scripts/agent-gate.sh");
        foreach (var token in new[]
                 {
                     "CustomBeforeMicrosoftCommonTargets", "CustomAfterMicrosoftCommonTargets",
                     "ImportUserLocationsByWildcardBeforeMicrosoftCSharpTargets",
                     "ImportUserLocationsByWildcardAfterMicrosoftCSharpTargets", "MSBuildSDKsPath",
                     "MSBUILD_EXE_PATH", ".globalconfig", "-noAutoResponse"
                 })
            Assert.Contains(token, project + script, StringComparison.Ordinal);
        Assert.Contains("must remain outside the source repository", project + script, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WorkflowRecordsCanonicalHostAndSdkAndScrubsRuntimeInjection()
    {
        var script = Read("scripts/agent-gate.sh");
        foreach (var token in new[]
                 {
                     "dotnet_host", "runtime_version", "DOTNET_GENERATE_ASPNET_CERTIFICATE",
                     "DOTNET_STARTUP_HOOKS", "DOTNET_ADDITIONAL_DEPS", "DOTNET_ROOT_*", "CORECLR_*/COR_*",
                     "MSBuildSDKsPath", "MSBUILD_EXE_PATH", "MSBUILDADDITIONALSDKRESOLVERSFOLDER",
                     "DOTNET_MSBUILD_SDK_RESOLVER_SDKS_DIR", "DOTNET_MSBUILD_SDK_RESOLVER_CLI_DIR"
                 })
            Assert.Contains(token, script, StringComparison.Ordinal);
        Assert.Contains("env -i", script, StringComparison.Ordinal);
        Assert.Contains("trusted_dotnet", script, StringComparison.Ordinal);
        Assert.DoesNotContain("&& dotnet ", script, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("duplicate-key", "{\"schemaVersion\":\"2\",\"mode\":\"check\",\"exitCode\":4,\"exitCode\":0}", 4, "duplicate JSON key")]
    [InlineData("nested-duplicate-key", "{\"schemaVersion\":\"2\",\"mode\":\"check\",\"counts\":{\"killed\":0,\"killed\":1}}", 4, "duplicate JSON key")]
    [InlineData("nonfinite-json", "{\"schemaVersion\":\"2\",\"mode\":\"check\",\"counts\":{\"killed\":NaN}}", 4, "nonfinite JSON number")]
    [InlineData("preview-schema", "{\"schemaVersion\":\"1\",\"mode\":\"check\"}", 4, "schema v2 check report")]
    [InlineData("na-policy-exit", "{\"schemaVersion\":\"2\",\"mode\":\"check\",\"outcome\":\"NOT_APPLICABLE\",\"exitCode\":5,\"policy\":{\"allowNotApplicable\":true}}", 5, "N/A exit does not match its policy")]
    [InlineData("preview-finalization", "{\"outcome\":\"INCOMPLETE\",\"exitCode\":4,\"incompleteConditions\":[{\"code\":\"FINALIZATION_PENDING\"}],\"counts\":{\"enumerated\":0}}", 4, "preview finalization")]
    [InlineData("mismatched-exit", "{\"outcome\":\"INCOMPLETE\",\"exitCode\":3,\"incompleteConditions\":[{\"code\":\"BASELINE_INCONCLUSIVE\"}],\"counts\":{\"enumerated\":0}}", 4, "exitCode does not match")]
    [InlineData("wrong-outcome", "{\"outcome\":\"COMPLETE\",\"exitCode\":4,\"baseline\":\"UNKNOWN\",\"incompleteConditions\":[{\"code\":\"BASELINE_INCONCLUSIVE\"}],\"counts\":{\"enumerated\":0}}", 4, "outcome does not match")]
    [InlineData("missing-condition", "{\"outcome\":\"INCOMPLETE\",\"exitCode\":4,\"incompleteConditions\":[],\"counts\":{\"enumerated\":0}}", 4, "lacks an accepted execution or enumeration incomplete condition")]
    [InlineData("malformed", "{", 4, "report validation failed")]
    [InlineData("non-object", "[]", 4, "report root must be an object")]
    [InlineData("unknown-condition", "{\"outcome\":\"INCOMPLETE\",\"exitCode\":4,\"incompleteConditions\":[{\"code\":\"NOT_A_GATE_CONDITION\"}],\"counts\":{\"enumerated\":null}}", 4, "lacks an accepted execution or enumeration incomplete condition")]
    [InlineData("retired-enumeration", "{\"outcome\":\"INCOMPLETE\",\"exitCode\":4,\"incompleteConditions\":[{\"code\":\"ENUMERATION_NOT_IMPLEMENTED\"}],\"counts\":{\"enumerated\":null}}", 4, "lacks an accepted execution or enumeration incomplete condition")]
    [InlineData("sdk-unavailable", "{\"outcome\":\"INCOMPLETE\",\"exitCode\":4,\"incompleteConditions\":[{\"code\":\"ENUMERATION_SDK_UNAVAILABLE\"}],\"counts\":{\"enumerated\":null}}", 4, "lacks an accepted execution or enumeration incomplete condition")]
    [InlineData("execution-with-foreign-condition", "{\"outcome\":\"INCOMPLETE\",\"exitCode\":4,\"incompleteConditions\":[{\"code\":\"BASELINE_INCONCLUSIVE\"},{\"code\":\"SNAPSHOT_DIVERGED\"}],\"counts\":{\"enumerated\":1}}", 4, "lacks an accepted execution or enumeration incomplete condition")]
    [InlineData("zero-with-report", "{\"outcome\":\"INCOMPLETE\",\"exitCode\":0,\"incompleteConditions\":[{\"code\":\"BASELINE_INCONCLUSIVE\"}],\"counts\":{\"enumerated\":0}}", 0, "outcome does not match")]
    [InlineData("exit-two-green", "{\"outcome\":\"INCOMPLETE\",\"exitCode\":2,\"baseline\":\"GREEN\",\"incompleteConditions\":[{\"code\":\"BASELINE_INCONCLUSIVE\"}],\"counts\":{\"enumerated\":1}}", 2, "exit 2 requires a RED or EMPTY baseline")]
    [InlineData("exit-four-red", "{\"outcome\":\"INCOMPLETE\",\"exitCode\":4,\"baseline\":\"RED\",\"incompleteConditions\":[{\"code\":\"BASELINE_INCONCLUSIVE\"}],\"counts\":{\"enumerated\":1}}", 4, "RED or EMPTY baseline requires exit 2")]
    public async Task GateReportBindingNegativesRefuseExactly(string name, string json, int processExit, string diagnostic)
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The shipped workflow is a Bash integration.");
        var target = Path.Combine(_root, "target-" + name);
        await CreateRepositoryAsync(target);
        var head = (await RunAsync(target, "git", "rev-parse", "HEAD")).StandardOutput.Trim();
        var fixture = await CreateToolFixtureAsync(json, processExit);
        var report = Path.Combine(_root, name + ".json");
        var result = await RunAsync(target, "bash", Path.Combine(RepositoryRoot, "scripts", "agent-gate.sh"),
            "gate", target, fixture.Receipt, fixture.PackageSha, fixture.PayloadSha, fixture.ToolCommit,
            head, head, report, "no-state");
        Assert.Equal(OrchestrationRefusal, result.ExitCode);
        Assert.Contains(diagnostic, result.Diagnostic, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("ENUMERATION_REFERENCE_UNSUPPORTED")]
    [InlineData("ENUMERATION_LIMIT_EXCEEDED")]
    [InlineData("ENUMERATION_ENCODING_UNSUPPORTED")]
    [InlineData("ENUMERATION_COMPILE_INVENTORY_UNSUPPORTED")]
    [InlineData("UNSUPPORTED_CHANGED_INPUT")]
    [InlineData("UNSUPPORTED_SYNTAX")]
    [InlineData("NO_SUPPORTED_DECLARATION")]
    [InlineData("UNMAPPED_PROJECT")]
    [InlineData("AMBIGUOUS_PROJECT_OWNERSHIP")]
    [InlineData("CONFIGURED_PATH_MISSING")]
    [InlineData("EXACT_ID_RERUN_UNAVAILABLE")]
    [InlineData("TARGET_SELECTION_INVALID")]
    [InlineData("DEPENDENCY_INPUT_UNAVAILABLE")]
    [InlineData("EXECUTION_ENVIRONMENT_UNAVAILABLE")]
    public async Task GateAcceptsValidatedSemanticAndScopeRefusalsAsIncompleteNotIntegrityFailure(string code)
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The shipped workflow is a Bash integration.");
        var target = Path.Combine(_root, "semantic-refusal-target-" + code);
        await CreateRepositoryAsync(target);
        var head = (await RunAsync(target, "git", "rev-parse", "HEAD")).StandardOutput.Trim();
        var fixture = await CreateToolFixtureAsync(
            $"{{\"outcome\":\"INCOMPLETE\",\"exitCode\":4,\"incompleteConditions\":[{{\"code\":\"{code}\"}}],\"counts\":{{\"enumerated\":null}},\"evidence\":[]}}", 4);

        var result = await RunAsync(target, "bash", Path.Combine(RepositoryRoot, "scripts", "agent-gate.sh"),
            "gate", target, fixture.Receipt, fixture.PackageSha, fixture.PayloadSha, fixture.ToolCommit,
            head, head, Path.Combine(_root, "semantic-refusal-" + code + ".json"), "no-state");

        Assert.Equal(4, result.ExitCode);
        Assert.DoesNotContain("agent-gate:", result.Diagnostic, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("OVERALL_DEADLINE_EXCEEDED")]
    [InlineData("EXECUTION_CANCELLED")]
    [InlineData("BASELINE_TIMEOUT")]
    [InlineData("BASELINE_INCONCLUSIVE")]
    [InlineData("COVERAGE_MISSING")]
    [InlineData("SUITE_MEMBERS_MISSING")]
    [InlineData("MUTATION_ATTEMPT_OMITTED")]
    [InlineData("BASELINE_ACCOUNTING_INCOMPLETE")]
    [InlineData("PROOF_ELIGIBILITY_FAILED")]
    public async Task GateAcceptsValidatedExecutionIncompletenessWithoutAcceptingIntegrityFailures(string code)
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The shipped workflow is a Bash integration.");
        var target = Path.Combine(_root, "execution-incomplete-target-" + code);
        await CreateRepositoryAsync(target);
        var head = (await RunAsync(target, "git", "rev-parse", "HEAD")).StandardOutput.Trim();
        var fixture = await CreateToolFixtureAsync(
            $"{{\"outcome\":\"INCOMPLETE\",\"exitCode\":4,\"incompleteConditions\":[{{\"code\":\"{code}\"}}],\"counts\":{{\"enumerated\":1}},\"evidence\":[]}}", 4);

        var result = await RunAsync(target, "bash", Path.Combine(RepositoryRoot, "scripts", "agent-gate.sh"),
            "gate", target, fixture.Receipt, fixture.PackageSha, fixture.PayloadSha, fixture.ToolCommit,
            head, head, Path.Combine(_root, "execution-incomplete-" + code + ".json"), "no-state");

        Assert.Equal(4, result.ExitCode);
        Assert.DoesNotContain("agent-gate:", result.Diagnostic, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("RED")]
    [InlineData("EMPTY")]
    public async Task GateAcceptsValidatedNonGreenBaselineExitTwo(string baseline)
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The shipped workflow is a Bash integration.");
        var target = Path.Combine(_root, "non-green-baseline-" + baseline.ToLowerInvariant());
        await CreateRepositoryAsync(target);
        var head = (await RunAsync(target, "git", "rev-parse", "HEAD")).StandardOutput.Trim();
        var fixture = await CreateToolFixtureAsync(
            $"{{\"outcome\":\"INCOMPLETE\",\"exitCode\":2,\"baseline\":\"{baseline}\",\"incompleteConditions\":[],\"counts\":{{\"enumerated\":1}},\"evidence\":[]}}", 2);

        var result = await RunAsync(target, "bash", Path.Combine(RepositoryRoot, "scripts", "agent-gate.sh"),
            "gate", target, fixture.Receipt, fixture.PackageSha, fixture.PayloadSha, fixture.ToolCommit,
            head, head, Path.Combine(_root, "non-green-baseline-" + baseline + ".json"), "no-state");

        Assert.Equal(2, result.ExitCode);
        Assert.DoesNotContain("agent-gate:", result.Diagnostic, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GateRejectsSymlinkReportWithStableRefusal()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The shipped workflow is a Bash integration.");
        var target = Path.Combine(_root, "symlink-report-target");
        await CreateRepositoryAsync(target);
        var head = (await RunAsync(target, "git", "rev-parse", "HEAD")).StandardOutput.Trim();
        var fixture = await CreateToolFixtureAsync("{}", 4);
        var report = Path.Combine(_root, "report-link.json");
        File.CreateSymbolicLink(report, Path.Combine(_root, "missing-report.json"));
        var result = await RunAsync(target, "bash", Path.Combine(RepositoryRoot, "scripts", "agent-gate.sh"),
            "gate", target, fixture.Receipt, fixture.PackageSha, fixture.PayloadSha, fixture.ToolCommit,
            head, head, report, "no-state");
        Assert.Equal(OrchestrationRefusal, result.ExitCode);
        Assert.Contains("report must not already exist", result.Diagnostic, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GateRejectsSymlinkRepositoryBoundaryAndRuntimeMismatchExactly()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The shipped workflow is a Bash integration.");
        var target = Path.Combine(_root, "real-target");
        await CreateRepositoryAsync(target);
        var linked = Path.Combine(_root, "linked-target");
        Directory.CreateSymbolicLink(linked, target);
        var head = (await RunAsync(target, "git", "rev-parse", "HEAD")).StandardOutput.Trim();
        var fixture = await CreateToolFixtureAsync("{}", 4);
        var linkedResult = await RunAsync(target, "bash", Path.Combine(RepositoryRoot, "scripts", "agent-gate.sh"),
            "gate", linked, fixture.Receipt, fixture.PackageSha, fixture.PayloadSha, fixture.ToolCommit,
            head, head, Path.Combine(_root, "linked-boundary.json"), "no-state");
        Assert.Equal(OrchestrationRefusal, linkedResult.ExitCode);
        Assert.Contains("target repository must not be symbolic", linkedResult.Diagnostic, StringComparison.OrdinalIgnoreCase);

        var receipt = await File.ReadAllTextAsync(fixture.Receipt, TestContext.Current.CancellationToken);
        var runtime = (await File.ReadAllLinesAsync(fixture.Receipt, TestContext.Current.CancellationToken))
            .Single(line => line.StartsWith("runtime_version\t", StringComparison.Ordinal))["runtime_version\t".Length..];
        await File.WriteAllTextAsync(fixture.Receipt,
            receipt.Replace($"runtime_version\t{runtime}", "runtime_version\t0.0.0-forged", StringComparison.Ordinal),
            TestContext.Current.CancellationToken);
        var runtimeResult = await RunAsync(target, "bash", Path.Combine(RepositoryRoot, "scripts", "agent-gate.sh"),
            "gate", target, fixture.Receipt, fixture.PackageSha, fixture.PayloadSha, fixture.ToolCommit,
            head, head, Path.Combine(_root, "runtime.json"), "no-state");
        Assert.Equal(OrchestrationRefusal, runtimeResult.ExitCode);
        Assert.Contains("trusted runtime version does not match receipt", runtimeResult.Diagnostic, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ToolFixtureDerivesRuntimeFromNeutralSdkWhenCallerSdkIsUnavailable()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The shipped workflow is a Bash integration.");
        await File.WriteAllTextAsync(Path.Combine(_root, "global.json"),
            "{\"sdk\":{\"version\":\"99.99.999\",\"rollForward\":\"disable\"}}\n",
            TestContext.Current.CancellationToken);
        var target = Path.Combine(_root, "neutral-sdk-target");
        await CreateRepositoryAsync(target);
        var head = (await RunAsync(target, "git", "rev-parse", "HEAD")).StandardOutput.Trim();
        var fixture = await CreateToolFixtureAsync(
            "{\"outcome\":\"INCOMPLETE\",\"exitCode\":4,\"baseline\":\"UNKNOWN\",\"incompleteConditions\":[{\"code\":\"BASELINE_INCONCLUSIVE\"}],\"counts\":{\"enumerated\":0}}", 4);

        var result = await RunAsync(target, "bash", Path.Combine(RepositoryRoot, "scripts", "agent-gate.sh"),
            "gate", target, fixture.Receipt, fixture.PackageSha, fixture.PayloadSha, fixture.ToolCommit,
            head, head, Path.Combine(_root, "neutral-sdk-report.json"), "no-state");

        Assert.Equal(4, result.ExitCode);
        Assert.DoesNotContain("trusted runtime version does not match receipt", result.Diagnostic,
            StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("PASS", false, "tests/Straße.Tests.csproj", "Straße.Tests.dll")]
    [InlineData("PASS", false, "tests/ı.Tests.csproj", "ı.Tests.dll")]
    [InlineData("NOT_APPLICABLE", false, "tests/App.Tests.csproj", "Tests.dll")]
    [InlineData("NOT_APPLICABLE", true, "tests/App.Tests.csproj", "Tests.dll")]
    public async Task GateAcceptsConclusiveReportAccountingAndPolicy(string outcome, bool allowNa,
        string suitePath, string member)
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The shipped workflow is a Bash integration.");
        var report = EvaluationReport.CreateSynthetic(outcome == "PASS" ? EvaluationOutcome.Pass :
            EvaluationOutcome.NotApplicable, "gate-conclusive", "STRUCTURAL_FIXTURE");
        var suite = Assert.Single(report.Suites);
        var configuration = suite.Accounting!.Configuration with { Path = suitePath, ExpectedMembers = [member] };
        suite = suite with
        {
            SuiteId = CheckConfiguration.SuiteIdentity(configuration),
            Accounting = suite.Accounting with { Configuration = configuration, AccountedMembers = [member] }
        };
        report = report with
        {
            Suites = [suite], Policy = report.Policy with { AllowNotApplicable = allowNa },
            ExitCode = outcome == "PASS" || allowNa ? 0 : 5,
            Evidence = report.Evidence.Where(item => item.Kind != "CHECK_CONFIGURATION")
                .Append(ReportWriter.ConfigurationSuiteEvidence([suite.SuiteId])).ToArray()
        };
        var target = Path.Combine(_root, "conclusive-target");
        await CreateRepositoryAsync(target);
        var head = (await RunAsync(target, "git", "rev-parse", "HEAD")).StandardOutput.Trim();
        var fixture = await CreateToolFixtureAsync(Encoding.UTF8.GetString(ReportWriter.Serialize(report)), report.ExitCode);
        var result = await RunAsync(target, "bash", Path.Combine(RepositoryRoot, "scripts", "agent-gate.sh"),
            "gate", target, fixture.Receipt, fixture.PackageSha, fixture.PayloadSha, fixture.ToolCommit,
            head, head, Path.Combine(_root, "conclusive-report.json"), "no-state");
        Assert.True(result.ExitCode == report.ExitCode, result.Diagnostic);
    }

    private async Task<ToolFixture> CreateToolFixtureAsync(string json, int processExit)
    {
        // Complete the unchanged structural fields around each deliberate fault.
        // Explicit v2 JSON is kept byte-for-byte for duplicate-key and malformed tests.
        if (!json.Contains("schemaVersion", StringComparison.Ordinal))
        {
            try
            {
                if (JsonNode.Parse(json) is JsonObject envelope && envelope["counts"] is JsonObject count)
                {
                    envelope["schemaVersion"] = "2";
                    envelope["mode"] = "check";
                    envelope["baseline"] ??= "GREEN";
                    envelope["reasons"] ??= new JsonArray();
                    var selected = count["enumerated"]?.GetValue<int>() ?? 0;
                    foreach (var key in new[] { "selected", "executed", "freshUncovered", "omitted", "compileInvalid", "killed", "survived", "errors" })
                        count[key] = key is "selected" or "omitted" ? selected : 0;
                    var units = new JsonArray();
                    for (var index = 0; index < selected; index++)
                        units.Add(new JsonObject
                        {
                            ["unitId"] = "mutation:v1:" + index.ToString("x64"),
                            ["evaluationUnitId"] = "evaluation:v1:" + index.ToString("x64"),
                            ["disposition"] = "OMITTED"
                        });
                    envelope["units"] = units;
                    json = envelope.ToJsonString();
                }
            }
            catch (JsonException) { /* Exercise the original malformed bytes. */ }
        }
        var id = Guid.NewGuid().ToString("N");
        var package = Path.Combine(_root, $"candidate-{id}.nupkg");
        var payload = Path.Combine(_root, $"payload-{id}");
        var command = Path.Combine(payload, "mutate4csharp");
        var receipt = Path.Combine(_root, $"receipt-{id}.tsv");
        var toolCommit = new string('a', 40);
        var version = $"0.1.0-local.{toolCommit}";
        Directory.CreateDirectory(payload);
        var sdk = Path.Combine(Path.GetDirectoryName(payload)!, "sdk");
        Directory.CreateDirectory(sdk);
        File.Copy(Path.Combine(RepositoryRoot, "global.json"), Path.Combine(sdk, "global.json"), overwrite: true);
        await File.WriteAllTextAsync(package, "package", TestContext.Current.CancellationToken);
        await WriteExecutableAsync(command, $"#!/usr/bin/env bash\nif [[ ${{1:-}} = --version ]]; then echo '{version}+{toolCommit}'; exit 0; fi\nprintf '%s\\n' '{json}' > \"$5\"\nexit {processExit}\n");
        var runtime = (await RunAsync(sdk, DotnetHost(), "--version")).StandardOutput.Trim();
        await File.WriteAllTextAsync(receipt, $"format\tmutate4csharp-agent-gate-v4\nlocal_tool_version\t{version}\ntool_package\t{package}\ntool_payload\t{payload}\nruntime_version\t{runtime}\ndotnet_host\t{CanonicalDotnetHost()}\n", TestContext.Current.CancellationToken);
        return new(receipt, Sha256File(package), PayloadSha256(payload), toolCommit);
    }

    private static async Task CreateRepositoryAsync(string path)
    {
        Directory.CreateDirectory(path);
        await RunRequiredAsync(path, "git", "init", "--quiet");
        await RunRequiredAsync(path, "git", "config", "user.email", "fixture@example.invalid");
        await RunRequiredAsync(path, "git", "config", "user.name", "fixture");
        await File.WriteAllTextAsync(Path.Combine(path, "README.md"), "fixture", TestContext.Current.CancellationToken);
        await RunRequiredAsync(path, "git", "add", ".");
        await RunRequiredAsync(path, "git", "commit", "--quiet", "-m", "fixture");
    }

    private static async Task WriteExecutableAsync(string path, string content)
    {
        if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        await File.WriteAllTextAsync(path, content, TestContext.Current.CancellationToken);
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private static string Read(string path) => File.ReadAllText(Path.Combine(RepositoryRoot, path));
    private static string DotnetHost() => Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
    private static string CanonicalDotnetHost() => new FileInfo(DotnetHost()).ResolveLinkTarget(true)?.FullName ?? Path.GetFullPath(DotnetHost());
    private static string Sha256File(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    private static string PayloadSha256(string root)
    {
        if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        using var aggregate = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            aggregate.AppendData(Encoding.UTF8.GetBytes("./" + Path.GetRelativePath(root, path).Replace('\\', '/')));
            aggregate.AppendData([0]);
            aggregate.AppendData(Encoding.ASCII.GetBytes(Convert.ToString((int)File.GetUnixFileMode(path), 8) + "\n"));
            aggregate.AppendData(Encoding.ASCII.GetBytes(Sha256File(path) + "\n"));
        }
        return Convert.ToHexString(aggregate.GetHashAndReset()).ToLowerInvariant();
    }

    private static async Task RunRequiredAsync(string directory, string executable, params string[] arguments)
    {
        var result = await RunAsync(directory, executable, arguments);
        Assert.Equal(0, result.ExitCode);
    }

    private static async Task<ProcessResult> RunAsync(string directory, string executable, params string[] arguments)
    {
        var start = new ProcessStartInfo(executable) { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        return new(process.ExitCode, await stdout, await stderr);
    }

    private sealed record ToolFixture(string Receipt, string PackageSha, string PayloadSha, string ToolCommit);
    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError)
    { public string Diagnostic => $"exit={ExitCode}\nstdout:\n{StandardOutput}\nstderr:\n{StandardError}"; }
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }
}
