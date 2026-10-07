using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

namespace Mutate4CSharp.Tests;

public sealed class IssueEightReviewRoundSevenTests : IDisposable
{
    private static readonly string RepositoryRoot = Path.GetFullPath("../../../../../", AppContext.BaseDirectory);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mutate4csharp-round-seven",
        Guid.NewGuid().ToString("N"));

    public IssueEightReviewRoundSevenTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task GateRejectsAnActuallyExecutedForgedReceiptCommand()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The shipped workflow is a Bash integration.");
        var target = Path.Combine(_root, "target");
        await CreateRepositoryAsync(target);
        var head = (await RunAsync(target, "git", "rev-parse", "HEAD")).StandardOutput.Trim();
        var package = Path.Combine(_root, "candidate.nupkg");
        var payload = Path.Combine(_root, "payload");
        var canonicalCommand = Path.Combine(payload, "mutate4csharp");
        var forgedCommand = Path.Combine(_root, "forged-command");
        var forgedMarker = Path.Combine(_root, "forged-command-ran");
        var receipt = Path.Combine(_root, "forged-receipt.tsv");
        var report = Path.Combine(_root, "report.json");
        var toolCommit = new string('a', 40);
        var version = $"0.1.0-local.{toolCommit}";
        Directory.CreateDirectory(payload);
        await File.WriteAllTextAsync(package, "package", TestContext.Current.CancellationToken);
        await WriteExecutableAsync(canonicalCommand, "#!/usr/bin/env bash\nexit 97\n");
        await WriteExecutableAsync(forgedCommand,
            $"#!/usr/bin/env bash\ntouch '{forgedMarker}'\nif [[ ${{1:-}} = --version ]]; then printf '%s\\n' '{version}+{toolCommit}'; fi\nexit 0\n");
        await File.WriteAllTextAsync(receipt, $"""
            format	mutate4csharp-agent-gate-v1
            local_tool_version	{version}
            tool_package	{package}
            tool_payload	{payload}
            tool_command	{forgedCommand}
            """ + "\n", TestContext.Current.CancellationToken);

        var result = await RunAsync(target, "bash", Path.Combine(RepositoryRoot, "scripts", "agent-gate.sh"),
            "gate", target, receipt, Sha256File(package), PayloadSha256(payload), toolCommit, head, head, report,
            "no-state");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("receipt contains unknown key: tool_command", result.Diagnostic,
            StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(forgedMarker), "The forged receipt command was executed.");
    }

    [Fact]
    public async Task PrepareRefusesAncestorDirectoryPackagesPropsAndWritesNoExistingReceipt()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The shipped workflow is a Bash integration.");
        var hostileParent = Path.Combine(_root, "hostile-parent");
        var source = Path.Combine(hostileParent, "source");
        var workspace = Path.Combine(hostileParent, "workspace");
        var receipt = Path.Combine(hostileParent, "receipt.tsv");
        await CopyCurrentRepositoryAsync(source);
        File.Copy(Path.Combine(RepositoryRoot, "scripts", "agent-gate.sh"),
            Path.Combine(source, "scripts", "agent-gate.sh"), overwrite: true);
        await RunRequiredAsync(source, "git", "config", "user.email", "fixture@example.invalid");
        await RunRequiredAsync(source, "git", "config", "user.name", "fixture");
        await RunRequiredAsync(source, "git", "add", "scripts/agent-gate.sh");
        if (!string.IsNullOrWhiteSpace((await RunAsync(source, "git", "status", "--porcelain=v1")).StandardOutput))
            await RunRequiredAsync(source, "git", "commit", "--quiet", "-m", "fixture implementation");
        await File.WriteAllTextAsync(Path.Combine(hostileParent, "Directory.Packages.props"),
            "<Project><Target Name=\"HostilePackages\" BeforeTargets=\"Restore\"><Error Text=\"hostile Directory.Packages.props imported\" /></Target></Project>",
            TestContext.Current.CancellationToken);

        var prepare = await RunAsync(source, "bash", Path.Combine(source, "scripts", "agent-gate.sh"),
            "prepare", source, workspace, receipt);
        Assert.Equal(73, prepare.ExitCode);
        Assert.Contains("ancestor build-control input is not allowed", prepare.Diagnostic,
            StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(receipt));
        File.Delete(Path.Combine(hostileParent, "Directory.Packages.props"));

        var secondWorkspace = Path.Combine(hostileParent, "second-workspace");
        var existingReceipt = Path.Combine(hostileParent, "existing-receipt.tsv");
        await File.WriteAllTextAsync(existingReceipt, "preserve me", TestContext.Current.CancellationToken);
        var refused = await RunAsync(source, "bash", Path.Combine(source, "scripts", "agent-gate.sh"),
            "prepare", source, secondWorkspace, existingReceipt);
        Assert.NotEqual(0, refused.ExitCode);
        Assert.Contains("must not already exist", refused.Diagnostic, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("preserve me", await File.ReadAllTextAsync(existingReceipt,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public void ProductionAndTestProjectsCommitLockedDependencyGraphs()
    {
        foreach (var projectPath in new[]
                 {
                     "src/Mutate4CSharp/Mutate4CSharp.csproj",
                     "tests/Mutate4CSharp.Tests/Mutate4CSharp.Tests.csproj"
                 })
        {
            var project = XDocument.Load(Path.Combine(RepositoryRoot, projectPath));
            Assert.Contains(project.Descendants("RestorePackagesWithLockFile"),
                element => string.Equals(element.Value, "true", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(project.Descendants("RestoreLockedMode"),
                element => string.Equals(element.Value, "true", StringComparison.OrdinalIgnoreCase));
            var lockPath = Path.Combine(Path.GetDirectoryName(Path.Combine(RepositoryRoot, projectPath))!,
                "packages.lock.json");
            Assert.True(File.Exists(lockPath), $"Missing dependency lock: {lockPath}");
            using var document = JsonDocument.Parse(File.ReadAllBytes(lockPath));
            Assert.Equal(1, document.RootElement.GetProperty("version").GetInt32());
        }
    }

    [Fact]
    public void ShippedWorkflowCanonicalizesExternalPathsAndDoesNotTrustACommandReceiptField()
    {
        var script = Read("scripts/agent-gate.sh");
        Assert.DoesNotContain("tool_command)", script, StringComparison.Ordinal);
        Assert.Contains("canonical payload executable", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ImportDirectoryPackagesProps=false", script, StringComparison.Ordinal);
        Assert.Contains("CustomBeforeMicrosoftCommonProps=", script, StringComparison.Ordinal);
        Assert.Contains("report_directory=$(canonical_new_target", script, StringComparison.Ordinal);
        Assert.DoesNotContain("sed -i", script, StringComparison.Ordinal);
    }

    [Fact]
    public void PackagingTestsUseCleanTrackedCopiesAndCiScopesBashIntegrationsToLinux()
    {
        var packagingTests = Read("tests/Mutate4CSharp.Tests/ToolPackagingIntegrationTests.cs");
        Assert.DoesNotContain("RunAsync(RepositoryRoot, DotnetHost(), \"pack\"", packagingTests,
            StringComparison.Ordinal);
        var workflow = Read(".github/workflows/ci.yml");
        Assert.Contains("round-seven-shipped-workflow", workflow, StringComparison.Ordinal);
        Assert.Contains("if: runner.os == 'Linux'", workflow, StringComparison.Ordinal);
        Assert.Contains("scripts/agent-gate.sh prepare", workflow, StringComparison.Ordinal);
        Assert.Contains("scripts/agent-gate.sh verify-example", workflow, StringComparison.Ordinal);
        Assert.Contains("scripts/agent-gate.sh gate", workflow, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DirectPackRejectsAnIgnoredSourceInputOutsideBuildOutputs()
    {
        var source = Path.Combine(_root, "ignored-input-source");
        await CopyCurrentRepositoryAsync(source);
        var revision = (await RunAsync(source, "git", "rev-parse", "HEAD")).StandardOutput.Trim();
        var ignoredInput = Path.Combine(source, "src", "Mutate4CSharp", "TestResults", "Injected.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(ignoredInput)!);
        await File.WriteAllTextAsync(ignoredInput, "internal static class Injected { }",
            TestContext.Current.CancellationToken);
        var ignored = await RunAsync(source, "git", "check-ignore", "-v", ignoredInput);
        Assert.Equal(0, ignored.ExitCode);
        Assert.Contains("TestResults/", ignored.StandardOutput, StringComparison.Ordinal);

        var pack = await RunPackAsync(source, "src/Mutate4CSharp/Mutate4CSharp.csproj",
            "-c", "Release", "-m:1", $"-p:Version=0.1.0-local.{revision}",
            $"-p:SourceRevisionId={revision}", "-o", Path.Combine(_root, "ignored-feed"));

        Assert.NotEqual(0, pack.ExitCode);
        Assert.Contains("ignored project inputs", pack.Diagnostic, StringComparison.OrdinalIgnoreCase);
    }

    private static string Read(string relativePath) => File.ReadAllText(Path.Combine(RepositoryRoot, relativePath));

    private static string DotnetHost() => Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";

    private async Task<ProcessResult> RunPackAsync(string source, params string[] arguments)
    {
        var isolation = Path.Combine(_root, "pack-isolation", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(isolation, "obj"));
        Directory.CreateDirectory(Path.Combine(isolation, "bin"));
        var emptyProps = Path.Combine(isolation, "empty.props");
        var emptyTargets = Path.Combine(isolation, "empty.targets");
        await File.WriteAllTextAsync(emptyProps, "<Project />", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(emptyTargets, "<Project />", TestContext.Current.CancellationToken);
        return await RunAsync(source, DotnetHost(), ["pack", .. arguments,
            "-p:Mutate4CSharpPackIsolation=true", "-p:ImportDirectoryBuildProps=false",
            "-p:ImportDirectoryBuildTargets=false", "-p:ImportDirectoryPackagesProps=false",
            $"-p:DirectoryBuildPropsPath={emptyProps}", $"-p:DirectoryBuildTargetsPath={emptyTargets}",
            "-p:CustomBeforeMicrosoftCommonProps=", "-p:CustomAfterMicrosoftCommonProps=",
            "-p:CustomBeforeMicrosoftCommonTargets=", "-p:CustomAfterMicrosoftCommonTargets=",
            "-p:CustomBeforeMicrosoftCSharpTargets=", "-p:CustomAfterMicrosoftCSharpTargets=",
            "-p:ImportUserLocationsByWildcardBeforeMicrosoftCommonProps=false",
            "-p:ImportUserLocationsByWildcardAfterMicrosoftCommonProps=false",
            "-p:ImportUserLocationsByWildcardBeforeMicrosoftCommonTargets=false",
            "-p:ImportUserLocationsByWildcardAfterMicrosoftCommonTargets=false",
            "-p:ImportUserLocationsByWildcardBeforeMicrosoftCSharpTargets=false",
            "-p:ImportUserLocationsByWildcardAfterMicrosoftCSharpTargets=false",
            $"-p:MSBuildUserExtensionsPath={Path.Combine(isolation, "user-extensions")}",
            $"-p:MSBuildProjectExtensionsPath={Path.Combine(isolation, "obj")}{Path.DirectorySeparatorChar}",
            $"-p:BaseIntermediateOutputPath={Path.Combine(isolation, "obj")}{Path.DirectorySeparatorChar}",
            $"-p:BaseOutputPath={Path.Combine(isolation, "bin")}{Path.DirectorySeparatorChar}",
            $"-p:OutputPath={Path.Combine(isolation, "bin")}{Path.DirectorySeparatorChar}",
            "-p:Mutate4CSharpNoAutoResponse=true"]);
    }

    private static async Task WriteExecutableAsync(string path, string content)
    {
        await File.WriteAllTextAsync(path, content, TestContext.Current.CancellationToken);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private static string Sha256File(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))
        .ToLowerInvariant();

    private static string PayloadSha256(string root)
    {
        if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        using var aggregate = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                     .Order(StringComparer.Ordinal))
        {
            var relative = "./" + Path.GetRelativePath(root, path).Replace('\\', '/');
            aggregate.AppendData(Encoding.UTF8.GetBytes(relative));
            aggregate.AppendData([0]);
            var mode = Convert.ToString((int)File.GetUnixFileMode(path), 8);
            aggregate.AppendData(Encoding.ASCII.GetBytes(mode + "\n"));
            aggregate.AppendData(Encoding.ASCII.GetBytes(Sha256File(path) + "\n"));
        }
        return Convert.ToHexString(aggregate.GetHashAndReset()).ToLowerInvariant();
    }

    private async Task CopyCurrentRepositoryAsync(string destination)
    {
        var clone = await RunAsync(_root, "git", "clone", "--quiet", "--no-local", RepositoryRoot, destination);
        Assert.Equal(0, clone.ExitCode);
    }

    private static async Task RunRequiredAsync(string directory, string executable, params string[] arguments)
    {
        var result = await RunAsync(directory, executable, arguments);
        Assert.True(result.ExitCode == 0, result.Diagnostic);
    }

    private async Task CreateRepositoryAsync(string path)
    {
        Directory.CreateDirectory(path);
        Assert.Equal(0, (await RunAsync(path, "git", "init", "--quiet")).ExitCode);
        Assert.Equal(0, (await RunAsync(path, "git", "config", "user.email", "fixture@example.invalid")).ExitCode);
        Assert.Equal(0, (await RunAsync(path, "git", "config", "user.name", "fixture")).ExitCode);
        await File.WriteAllTextAsync(Path.Combine(path, "README.md"), "fixture",
            TestContext.Current.CancellationToken);
        Assert.Equal(0, (await RunAsync(path, "git", "add", ".")).ExitCode);
        Assert.Equal(0, (await RunAsync(path, "git", "commit", "--quiet", "-m", "fixture")).ExitCode);
    }

    private static async Task<ProcessResult> RunAsync(string workingDirectory, string executable,
        params string[] arguments)
    {
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var error = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        return new ProcessResult(process.ExitCode, await output, await error);
    }

    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError)
    {
        public string Diagnostic => $"exit={ExitCode}\nstdout:\n{StandardOutput}\nstderr:\n{StandardError}";
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }
}
