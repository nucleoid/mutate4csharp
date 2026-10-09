using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace Mutate4CSharp.Tests;

public sealed class IssueEightReviewRoundTenTests : IDisposable
{
    private const int OrchestrationRefusal = 73;
    private static readonly string RepositoryRoot = Path.GetFullPath("../../../../../", AppContext.BaseDirectory);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mutate4csharp-round-ten",
        Guid.NewGuid().ToString("N"));

    public IssueEightReviewRoundTenTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task GateReportValidationCannotImportTargetJsonModule()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The shipped workflow is a Bash integration.");
        var target = Path.Combine(_root, "json-target");
        await CreateRepositoryAsync(target);
        var marker = Path.Combine(_root, "json-imported");
        await File.WriteAllTextAsync(Path.Combine(target, "json.py"),
            $"from pathlib import Path\nPath({PythonLiteral(marker)}).write_text('executed')\nraise RuntimeError('target json imported')\n",
            TestContext.Current.CancellationToken);
        var head = (await RunAsync(target, "git", "rev-parse", "HEAD")).StandardOutput.Trim();
        var fixture = await CreateToolFixtureAsync();

        var environment = new Dictionary<string, string?> { ["PYTHONPATH"] = target };
        var result = await RunAsync(target, "bash", environment, GateScript(), "gate", target, fixture.Receipt,
            fixture.PackageSha, fixture.PayloadSha, fixture.ToolCommit, head, head,
            Path.Combine(_root, "json-report.json"), "no-state");

        Assert.True(result.ExitCode == 4, result.Diagnostic);
        Assert.False(File.Exists(marker), result.Diagnostic);
    }

    [Fact]
    public async Task VerifyExampleXmlRewriteCannotImportTargetXmlPackage()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The shipped workflow is a Bash integration.");
        var target = Path.Combine(_root, "xml-target");
        await CreateExampleShellRepositoryAsync(target);
        var marker = Path.Combine(_root, "xml-imported");
        var xmlPackage = Path.Combine(target, "xml");
        Directory.CreateDirectory(xmlPackage);
        await File.WriteAllTextAsync(Path.Combine(xmlPackage, "__init__.py"),
            $"from pathlib import Path\nPath({PythonLiteral(marker)}).write_text('executed')\nraise RuntimeError('target xml imported')\n",
            TestContext.Current.CancellationToken);
        var head = (await RunAsync(target, "git", "rev-parse", "HEAD")).StandardOutput.Trim();
        var fixture = await CreateToolFixtureAsync();

        var environment = new Dictionary<string, string?> { ["PYTHONPATH"] = target };
        var result = await RunAsync(target, "bash", environment, GateScript(), "verify-example", target,
            fixture.Receipt, fixture.PackageSha, fixture.PayloadSha, fixture.ToolCommit, head, head,
            Path.Combine(_root, "xml-reports"));

        Assert.NotEqual(0, result.ExitCode);
        Assert.False(File.Exists(marker), result.Diagnostic);
        Assert.True(File.Exists(Path.Combine(_root, "xml-reports", "negative-restore.log")), result.Diagnostic);
        var rewrittenConfig = await File.ReadAllTextAsync(
            Path.Combine(_root, "xml-reports", "mapping-control", "NuGet.Config"),
            TestContext.Current.CancellationToken);
        Assert.Contains("surrogate-public", rewrittenConfig, StringComparison.Ordinal);
        Assert.DoesNotContain("https://api.nuget.org/v3/index.json", rewrittenConfig, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("8.0.418")]
    [InlineData("99.0.100")]
    public async Task GateUsesReceiptWorkspaceSdkRegardlessOfTargetGlobalJson(string? targetSdk)
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The shipped workflow is a Bash integration.");
        var target = Path.Combine(_root, "sdk-target-" + (targetSdk ?? "absent").Replace('.', '-'));
        await CreateRepositoryAsync(target, targetSdk);
        var head = (await RunAsync(target, "git", "rev-parse", "HEAD")).StandardOutput.Trim();
        var fixture = await CreateToolFixtureAsync();

        var result = await RunAsync(target, "bash", GateScript(), "gate", target, fixture.Receipt,
            fixture.PackageSha, fixture.PayloadSha, fixture.ToolCommit, head, head,
            Path.Combine(_root, $"sdk-{targetSdk ?? "absent"}.json"), "no-state");

        Assert.True(result.ExitCode == 4, result.Diagnostic);
    }

    [Fact]
    public async Task PrepareUsesAnEnvironmentAllowlistAndIgnoresHostileMsbuildHooks()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The shipped workflow is a Bash integration.");
        var source = Path.Combine(_root, "clean-current-source");
        await CopyCurrentRepositoryAsync(source);
        var workspace = Path.Combine(_root, "hostile-workspace");
        var receipt = Path.Combine(_root, "hostile-receipt.tsv");
        var hostileHome = Path.Combine(_root, "hostile-home");
        var userImport = Path.Combine(hostileHome, ".local", "share", "Microsoft", "MSBuild", "Current",
            "Microsoft.Common.targets", "ImportAfter", "hostile.targets");
        var customTargets = Path.Combine(_root, "hostile-csharp.targets");
        var resolver = Path.Combine(_root, "hostile-resolver");
        var marker = Path.Combine(_root, "hostile-msbuild-executed");
        var resolverMarker = Path.Combine(_root, "hostile-resolver-executed");
        Directory.CreateDirectory(Path.GetDirectoryName(userImport)!);
        Directory.CreateDirectory(resolver);
        var project = $"<Project><Target Name=\"Hostile\" BeforeTargets=\"Pack\"><WriteLinesToFile File=\"{marker}\" Lines=\"executed\" Overwrite=\"true\" /></Target></Project>";
        await File.WriteAllTextAsync(userImport, project, TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(customTargets, project, TestContext.Current.CancellationToken);
        await WriteExecutableAsync(Path.Combine(resolver, "hostile-resolver"),
            $"#!/usr/bin/env bash\nprintf executed > '{resolverMarker}'\n");

        var environment = new Dictionary<string, string?>
        {
            ["HOME"] = hostileHome,
            ["CustomAfterMicrosoftCSharpTargets"] = customTargets,
            ["ImportUserLocationsByWildcardAfterMicrosoftCommonTargets"] = "true",
            ["MSBUILDADDITIONALSDKRESOLVERSFOLDER"] = resolver,
            ["DOTNET_MSBUILD_SDK_RESOLVER_SDKS_DIR"] = resolver,
            ["DOTNET_MSBUILD_SDK_RESOLVER_CLI_DIR"] = resolver
        };
        var result = await RunAsync(source, "bash", environment,
            Path.Combine(source, "scripts", "agent-gate.sh"), "prepare", source, workspace, receipt);

        Assert.True(result.ExitCode == 0, result.Diagnostic);
        Assert.True(File.Exists(receipt));
        Assert.False(File.Exists(marker), result.Diagnostic);
        Assert.False(File.Exists(resolverMarker), result.Diagnostic);
    }

    [Fact]
    public void IsolationContractClosesEveryDocumentedEnvironmentAndMsbuildHook()
    {
        var script = Read("scripts/agent-gate.sh");
        var project = Read("src/Mutate4CSharp/Mutate4CSharp.csproj");
        Assert.Contains("env -i", script, StringComparison.Ordinal);
        foreach (var token in new[]
                 {
                     "ImportUserLocationsByWildcardBeforeMicrosoftCommonTargets=false",
                     "ImportUserLocationsByWildcardAfterMicrosoftCommonTargets=false",
                     "CustomBeforeMicrosoftCSharpTargets=", "CustomAfterMicrosoftCSharpTargets=",
                     "MSBUILDADDITIONALSDKRESOLVERSFOLDER", "DOTNET_MSBUILD_SDK_RESOLVER_SDKS_DIR",
                     "DOTNET_MSBUILD_SDK_RESOLVER_CLI_DIR", "MSBuildUserExtensionsPath"
                 })
            Assert.Contains(token, script + project, StringComparison.Ordinal);
    }

    [Fact]
    public void PythonImportsAreIsolatedAndSdkChecksUseOneNeutralDirectory()
    {
        var script = Read("scripts/agent-gate.sh");
        Assert.Equal(4, Count(script, "python3 -I -"));
        Assert.DoesNotContain("python3 - ", script, StringComparison.Ordinal);
        Assert.Contains("neutral SDK directory", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("trusted_sdk_version", script, StringComparison.Ordinal);
    }

    [Fact]
    public void TrustedEnvironmentPinsTheReceiptBoundHostForToolInternalDotnetProcesses()
    {
        var script = Read("scripts/agent-gate.sh");
        Assert.Contains("DOTNET_HOST_PATH=\"$TRUSTED_DOTNET\"", script, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("SIDECAR_WRITE_FAILED", "SYNTHETIC_OUTCOME")]
    [InlineData("EXECUTION_NOT_IMPLEMENTED", "SIDECAR_PUBLICATION_FAILURE")]
    public async Task GateRejectsReportsWithSidecarPublicationFailure(string conditionCode, string evidenceKind)
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The shipped workflow is a Bash integration.");
        var target = Path.Combine(_root, "sidecar-failure-" + conditionCode);
        await CreateRepositoryAsync(target);
        var head = (await RunAsync(target, "git", "rev-parse", "HEAD")).StandardOutput.Trim();
        var reportJson = $$"""
            {"schemaVersion":"1","outcome":"INCOMPLETE","exitCode":4,"incompleteConditions":[{"code":"EXECUTION_NOT_IMPLEMENTED"},{"code":"{{conditionCode}}"}],"counts":{"enumerated":0},"evidence":[{"kind":"{{evidenceKind}}"}]}
            """;
        var fixture = await CreateToolFixtureAsync(
            $"printf '%s\\n' '{reportJson}' > \"$5\"\nexit 4");

        var result = await RunAsync(target, "bash", GateScript(), "gate", target, fixture.Receipt,
            fixture.PackageSha, fixture.PayloadSha, fixture.ToolCommit, head, head,
            Path.Combine(_root, conditionCode + ".json"), "default-state");

        Assert.Equal(OrchestrationRefusal, result.ExitCode);
        Assert.Contains("sidecar", result.Diagnostic, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DefaultStateGateRequiresAReportBoundDiscoveryRecord()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The shipped workflow is a Bash integration.");
        var target = Path.Combine(_root, "missing-discovery-target");
        await CreateRepositoryAsync(target);
        var head = (await RunAsync(target, "git", "rev-parse", "HEAD")).StandardOutput.Trim();
        var fixture = await CreateToolFixtureAsync();

        var result = await RunAsync(target, "bash", GateScript(), "gate", target, fixture.Receipt,
            fixture.PackageSha, fixture.PayloadSha, fixture.ToolCommit, head, head,
            Path.Combine(_root, "missing-discovery.json"), "default-state");

        Assert.Equal(OrchestrationRefusal, result.ExitCode);
        Assert.Contains("discovery", result.Diagnostic, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void VerifyExampleRecordsActualProjectSdkResolution()
    {
        var script = Read("scripts/agent-gate.sh");
        Assert.Contains("sdk-resolution.tsv", script, StringComparison.Ordinal);
        Assert.Contains("NETCoreSdkVersion", script, StringComparison.Ordinal);
        Assert.Contains("MSBuildExtensionsPath", script, StringComparison.Ordinal);
    }

    [Fact]
    public void DocumentationAndCiStateOnlyTheImplementedSupportContract()
    {
        var matrix = Read("docs/supported-matrix.md");
        var workflow = Read("docs/agent-workflow.md");
        var ci = Read(".github/workflows/ci.yml");
        Assert.DoesNotContain("Windows build/pack/test", matrix, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Windows support currently covers direct build/pack/test", matrix,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("setsid --wait", matrix, StringComparison.Ordinal);
        Assert.Contains("report validation", matrix, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("consumer global.json", workflow, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Direct pack", workflow, StringComparison.Ordinal);
        Assert.Contains("if: runner.os == 'Linux'", ci, StringComparison.Ordinal);
        Assert.DoesNotContain("dotnet pack", ci, StringComparison.Ordinal);
    }

    private async Task<ToolFixture> CreateToolFixtureAsync(string? checkBody = null)
    {
        var id = Guid.NewGuid().ToString("N");
        var workspace = Path.Combine(_root, "fixture-" + id);
        var package = Path.Combine(workspace, "candidate.nupkg");
        var payload = Path.Combine(workspace, "tool");
        var sdk = Path.Combine(workspace, "sdk");
        var command = Path.Combine(payload, "mutate4csharp");
        var receipt = Path.Combine(_root, $"receipt-{id}.tsv");
        var toolCommit = new string('a', 40);
        var version = $"0.1.0-local.{toolCommit}";
        Directory.CreateDirectory(payload);
        Directory.CreateDirectory(sdk);
        await File.WriteAllTextAsync(package, "package", TestContext.Current.CancellationToken);
        File.Copy(Path.Combine(RepositoryRoot, "global.json"), Path.Combine(sdk, "global.json"));
        var body = checkBody ?? "printf '%s\\n' '{\"schemaVersion\":\"1\",\"outcome\":\"INCOMPLETE\",\"exitCode\":4,\"incompleteConditions\":[{\"code\":\"EXECUTION_NOT_IMPLEMENTED\"}],\"counts\":{\"enumerated\":0}}' > \"$5\"\nexit 4";
        await WriteExecutableAsync(command,
            $"#!/usr/bin/env bash\nif [[ ${{1:-}} = --version ]]; then echo '{version}+{toolCommit}'; exit 0; fi\n{body}\n");
        var runtime = (await RunAsync(sdk, DotnetHost(), "--version")).StandardOutput.Trim();
        await File.WriteAllTextAsync(receipt,
            $"format\tmutate4csharp-agent-gate-v3\nlocal_tool_version\t{version}\ntool_package\t{package}\ntool_payload\t{payload}\nruntime_version\t{runtime}\ndotnet_host\t{CanonicalDotnetHost()}\n",
            TestContext.Current.CancellationToken);
        return new(receipt, Sha256File(package), PayloadSha256(payload), toolCommit);
    }

    private static async Task CreateRepositoryAsync(string path, string? sdkVersion = null)
    {
        Directory.CreateDirectory(path);
        await RunRequiredAsync(path, "git", "init", "--quiet");
        await RunRequiredAsync(path, "git", "config", "user.email", "fixture@example.invalid");
        await RunRequiredAsync(path, "git", "config", "user.name", "fixture");
        await File.WriteAllTextAsync(Path.Combine(path, "README.md"), "fixture",
            TestContext.Current.CancellationToken);
        if (sdkVersion is not null)
            await File.WriteAllTextAsync(Path.Combine(path, "global.json"),
                $"{{\"sdk\":{{\"version\":\"{sdkVersion}\",\"rollForward\":\"disable\"}}}}\n",
                TestContext.Current.CancellationToken);
        await RunRequiredAsync(path, "git", "add", ".");
        await RunRequiredAsync(path, "git", "commit", "--quiet", "-m", "fixture");
    }

    private static async Task CreateExampleShellRepositoryAsync(string path)
    {
        await CreateRepositoryAsync(path);
        await File.WriteAllTextAsync(Path.Combine(path, "NuGet.Config"), """
            <?xml version="1.0" encoding="utf-8"?>
            <configuration>
              <packageSources>
                <clear />
                <add key="mutate4csharp-local" value="../mutate4csharp-local-feed" />
                <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
              </packageSources>
            </configuration>
            """, TestContext.Current.CancellationToken);
    }

    private async Task CopyCurrentRepositoryAsync(string destination)
    {
        Directory.CreateDirectory(destination);
        var listed = await RunAsync(RepositoryRoot, "git", "ls-files", "-co", "--exclude-standard", "-z");
        Assert.True(listed.ExitCode == 0, listed.Diagnostic);
        foreach (var relativePath in listed.StandardOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            var source = Path.Combine(RepositoryRoot, relativePath);
            var target = Path.Combine(destination, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(source, target);
        }
        await RunRequiredAsync(destination, "git", "init", "--quiet");
        await RunRequiredAsync(destination, "git", "config", "user.email", "fixture@example.invalid");
        await RunRequiredAsync(destination, "git", "config", "user.name", "fixture");
        await RunRequiredAsync(destination, "git", "add", ".");
        await RunRequiredAsync(destination, "git", "commit", "--quiet", "-m", "fixture");
    }

    private static string PythonLiteral(string path) => "r'" + path.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
    private static string GateScript() => Path.Combine(RepositoryRoot, "scripts", "agent-gate.sh");
    private static string Read(string path) => File.ReadAllText(Path.Combine(RepositoryRoot, path));
    private static int Count(string value, string token) => value.Split(token, StringSplitOptions.None).Length - 1;
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

    private static async Task WriteExecutableAsync(string path, string content)
    {
        if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        await File.WriteAllTextAsync(path, content, TestContext.Current.CancellationToken);
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private static async Task RunRequiredAsync(string directory, string executable, params string[] arguments)
    {
        var result = await RunAsync(directory, executable, arguments);
        Assert.True(result.ExitCode == 0, result.Diagnostic);
    }

    private static Task<ProcessResult> RunAsync(string directory, string executable, params string[] arguments) =>
        RunAsync(directory, executable, null, arguments);

    private static async Task<ProcessResult> RunAsync(string directory, string executable,
        IReadOnlyDictionary<string, string?>? environment, params string[] arguments)
    {
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = directory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        if (environment is not null)
            foreach (var (key, value) in environment)
                start.Environment[key] = value;
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

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }
}
