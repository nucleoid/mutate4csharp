using System.Diagnostics;
using System.IO.Compression;

namespace Mutate4CSharp.Tests;

public sealed class IssueEightReviewRoundTwelveTests : IDisposable
{
    private static readonly string RepositoryRoot = Path.GetFullPath("../../../../../", AppContext.BaseDirectory);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mutate4csharp-round-twelve",
        Guid.NewGuid().ToString("N"));

    public IssueEightReviewRoundTwelveTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task VerifyExampleIsolatesHostileBuildResolverPythonAndPathWhileRecordingBothSdkIdentities()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The shipped workflow is a Bash integration.");
        var source = Path.Combine(_root, "source");
        await CopyTrackedRepositoryAsync(source);
        var workspace = Path.Combine(_root, "workspace");
        var receipt = Path.Combine(_root, "receipt.tsv");
        var prepare = await RunAsync(source, "bash", Path.Combine(source, "scripts", "agent-gate.sh"),
            "prepare", source, workspace, receipt);
        Assert.True(prepare.ExitCode == 0, prepare.Diagnostic);
        var values = prepare.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split('=', 2)).Where(parts => parts.Length == 2)
            .ToDictionary(parts => parts[0], parts => parts[1], StringComparer.Ordinal);

        var consumer = Path.Combine(_root, "consumer");
        Directory.CreateDirectory(consumer);
        using (var package = ZipFile.OpenRead(Directory.EnumerateFiles(Path.Combine(workspace, "feed"), "*.nupkg").Single()))
        {
            foreach (var entry in package.Entries.Where(entry =>
                         entry.FullName.StartsWith("examples/agent-gate/", StringComparison.Ordinal) &&
                         !string.IsNullOrEmpty(entry.Name)))
            {
                var relative = entry.FullName["examples/agent-gate/".Length..];
                var destination = Path.GetFullPath(Path.Combine(consumer, relative));
                Assert.StartsWith(Path.GetFullPath(consumer) + Path.DirectorySeparatorChar, destination,
                    StringComparison.Ordinal);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                entry.ExtractToFile(destination);
            }
        }
        await File.WriteAllTextAsync(Path.Combine(consumer, "global.json"),
            $"{{\"sdk\":{{\"version\":\"{values["SDK_VERSION"]}\",\"rollForward\":\"latestPatch\"}}}}\n",
            TestContext.Current.CancellationToken);
        await RunRequiredAsync(consumer, "git", "init", "--quiet");
        await RunRequiredAsync(consumer, "git", "config", "user.email", "fixture@example.invalid");
        await RunRequiredAsync(consumer, "git", "config", "user.name", "fixture");
        await RunRequiredAsync(consumer, "git", "add", ".");
        await RunRequiredAsync(consumer, "git", "commit", "--quiet", "-m", "fixture");
        var head = (await RunAsync(consumer, "git", "rev-parse", "HEAD")).StandardOutput.Trim();
        var exampleSource = Path.Combine(consumer, "src", "Example", "Flag.cs");
        var exampleText = await File.ReadAllTextAsync(exampleSource, TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(exampleSource, exampleText.Replace(
            "configured && true", "configured || false", StringComparison.Ordinal),
            TestContext.Current.CancellationToken);

        var ancestorBuildMarker = Path.Combine(_root, "ancestor-build-executed");
        var ancestorPropsMarker = Path.Combine(_root, "ancestor-props-executed");
        await File.WriteAllTextAsync(Path.Combine(_root, "Directory.Build.props"),
            $"<Project><Target Name=\"HostileProps\" BeforeTargets=\"Restore\"><WriteLinesToFile File=\"{ancestorPropsMarker}\" Lines=\"executed\" Overwrite=\"true\" /></Target></Project>",
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(_root, "Directory.Build.targets"),
            $"<Project><Target Name=\"HostileTargets\" BeforeTargets=\"Build\"><WriteLinesToFile File=\"{ancestorBuildMarker}\" Lines=\"executed\" Overwrite=\"true\" /></Target></Project>",
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(_root, "Directory.Build.rsp"),
            "-p:RoundThirteenAncestorResponse=true\n", TestContext.Current.CancellationToken);
        var ancestorReports = Path.Combine(_root, "ancestor-reports");
        var ancestorAttempt = await RunAsync(consumer, "bash", new Dictionary<string, string?>
        {
            ["DOTNET_HOST_PATH"] = CanonicalDotnetHost()
        }, Path.Combine(source, "scripts", "agent-gate.sh"), "verify-example", consumer, receipt,
            values["PACKAGE_SHA256"], values["PAYLOAD_SHA256"], values["TOOL_SOURCE_COMMIT"], head, head,
            ancestorReports);
        var ancestorWasRejectedBeforeExecution = ancestorAttempt.ExitCode == OrchestrationRefusal &&
            !File.Exists(ancestorBuildMarker) && !File.Exists(ancestorPropsMarker);
        File.Delete(Path.Combine(_root, "Directory.Build.props"));
        File.Delete(Path.Combine(_root, "Directory.Build.targets"));
        File.Delete(Path.Combine(_root, "Directory.Build.rsp"));

        var hostileHome = Path.Combine(_root, "hostile-home");
        var userImport = Path.Combine(hostileHome, ".local", "share", "Microsoft", "MSBuild", "Current",
            "Microsoft.Common.targets", "ImportAfter", "hostile.targets");
        var buildMarker = Path.Combine(_root, "hostile-build-executed");
        Directory.CreateDirectory(Path.GetDirectoryName(userImport)!);
        await File.WriteAllTextAsync(userImport,
            $"<Project><Target Name=\"HostileBuild\" BeforeTargets=\"Build\"><WriteLinesToFile File=\"{buildMarker}\" Lines=\"executed\" Overwrite=\"true\" /></Target></Project>",
            TestContext.Current.CancellationToken);

        var hostileNuGetConfig = Path.Combine(hostileHome, ".nuget", "NuGet", "NuGet.Config");
        Directory.CreateDirectory(Path.GetDirectoryName(hostileNuGetConfig)!);
        await File.WriteAllTextAsync(hostileNuGetConfig, """
            <?xml version="1.0" encoding="utf-8"?>
            <configuration>
              <config><add key="globalPackagesFolder" value="hostile-global-packages" /></config>
              <packageSources><clear /><add key="hostile" value="https://example.invalid/nuget" /></packageSources>
            </configuration>
            """, TestContext.Current.CancellationToken);
        var hostileHomeBefore = SnapshotTree(hostileHome);

        var resolver = Path.Combine(_root, "hostile-resolver");
        var resolverMarker = Path.Combine(_root, "hostile-resolver-executed");
        await CreateResolverAsync(resolver, resolverMarker);
        var resolverControl = Path.Combine(_root, "resolver-control");
        Directory.CreateDirectory(resolverControl);
        await File.WriteAllTextAsync(Path.Combine(resolverControl, "ResolverControl.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>",
            TestContext.Current.CancellationToken);
        var resolverControlResult = await RunAsync(resolverControl, CanonicalDotnetHost(),
            new Dictionary<string, string?> { ["MSBUILDADDITIONALSDKRESOLVERSFOLDER"] = resolver },
            "msbuild", "ResolverControl.csproj", "-getProperty:MSBuildProjectName");
        Assert.True(resolverControlResult.ExitCode == 0, resolverControlResult.Diagnostic);
        Assert.True(File.Exists(resolverMarker), "The real resolver adversary control did not execute.");
        File.Delete(resolverMarker);

        var hostilePythonMarker = Path.Combine(_root, "hostile-python-executed");
        await File.WriteAllTextAsync(Path.Combine(consumer, "json.py"),
            $"from pathlib import Path\nPath({PythonLiteral(hostilePythonMarker)}).write_text('json')\nraise RuntimeError('hostile json imported')\n",
            TestContext.Current.CancellationToken);
        var hostileXml = Path.Combine(consumer, "xml");
        Directory.CreateDirectory(hostileXml);
        await File.WriteAllTextAsync(Path.Combine(hostileXml, "__init__.py"),
            $"from pathlib import Path\nPath({PythonLiteral(hostilePythonMarker)}).write_text('xml')\nraise RuntimeError('hostile xml imported')\n",
            TestContext.Current.CancellationToken);
        await File.AppendAllTextAsync(Path.Combine(consumer, ".git", "info", "exclude"),
            "\n/json.py\n/xml/\n", TestContext.Current.CancellationToken);
        var hostileBin = Path.Combine(_root, "hostile-bin");
        var pathDotnetMarker = Path.Combine(_root, "hostile-path-dotnet-executed");
        Directory.CreateDirectory(hostileBin);
        await WriteExecutableAsync(Path.Combine(hostileBin, "dotnet"),
            $"#!/usr/bin/env bash\nprintf executed > '{pathDotnetMarker}'\nexit 97\n");

        var reports = Path.Combine(_root, "reports");
        var environment = new Dictionary<string, string?>
        {
            ["HOME"] = hostileHome,
            ["DOTNET_CLI_HOME"] = Path.Combine(hostileHome, "caller-dotnet-home"),
            ["NUGET_HTTP_CACHE_PATH"] = Path.Combine(hostileHome, "caller-http-cache"),
            ["NUGET_PLUGINS_CACHE_PATH"] = Path.Combine(hostileHome, "caller-plugins-cache"),
            ["PATH"] = hostileBin + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH"),
            ["PYTHONPATH"] = consumer,
            ["DOTNET_HOST_PATH"] = CanonicalDotnetHost(),
            ["MSBUILDADDITIONALSDKRESOLVERSFOLDER"] = resolver,
            ["DOTNET_MSBUILD_SDK_RESOLVER_SDKS_DIR"] = resolver,
            ["DOTNET_MSBUILD_SDK_RESOLVER_CLI_DIR"] = resolver
        };
        var verify = await RunAsync(consumer, "bash", environment,
            Path.Combine(source, "scripts", "agent-gate.sh"), "verify-example", consumer, receipt,
            values["PACKAGE_SHA256"], values["PAYLOAD_SHA256"], values["TOOL_SOURCE_COMMIT"], head, head, reports);

        Assert.True(verify.ExitCode == 0, verify.Diagnostic);
        Assert.False(File.Exists(buildMarker), verify.Diagnostic);
        Assert.False(File.Exists(resolverMarker), verify.Diagnostic);
        Assert.False(File.Exists(pathDotnetMarker), verify.Diagnostic);
        Assert.False(File.Exists(hostilePythonMarker), verify.Diagnostic);
        var hostileHomeAfter = SnapshotTree(hostileHome);
        var orchestration = await File.ReadAllTextAsync(Path.Combine(reports, "sdk-resolution.tsv"),
            TestContext.Current.CancellationToken);
        Assert.Contains($"\t{values["SDK_VERSION"]}\t", orchestration, StringComparison.Ordinal);
        var toolInternal = await File.ReadAllTextAsync(Path.Combine(reports, "tool-sdk-resolution.tsv"),
            TestContext.Current.CancellationToken);
        Assert.Contains($"tool-internal\tdotnet-sdk={values["SDK_VERSION"]}", toolInternal,
            StringComparison.Ordinal);

        var fallbackEnvironment = new Dictionary<string, string?>
        {
            ["DOTNET_HOST_PATH"] = null,
            ["PATH"] = hostileBin + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH")
        };
        var fallback = await RunAsync(consumer, "bash", fallbackEnvironment,
            Path.Combine(source, "scripts", "agent-gate.sh"), "gate", consumer, receipt,
            values["PACKAGE_SHA256"], values["PAYLOAD_SHA256"], values["TOOL_SOURCE_COMMIT"], head, head,
            Path.Combine(reports, "receipt-host-fallback.json"), "no-state");
        Assert.Equal(3, fallback.ExitCode);
        Assert.False(File.Exists(pathDotnetMarker), fallback.Diagnostic);
        var defects = new List<string>();
        if (!ancestorWasRejectedBeforeExecution) defects.Add("ancestor controls were not rejected before execution");
        if (!hostileHomeBefore.SequenceEqual(hostileHomeAfter, StringComparer.Ordinal))
            defects.Add("verify-example changed caller HOME");
        if (!Directory.Exists(Path.Combine(reports, "user-home"))) defects.Add("task-owned HOME is missing");
        if (!Directory.Exists(Path.Combine(reports, "dotnet-home"))) defects.Add("task-owned DOTNET_CLI_HOME is missing");
        if (!Directory.Exists(Path.Combine(reports, "nuget-http-cache")))
            defects.Add("task-owned NUGET_HTTP_CACHE_PATH is missing");
        Assert.True(defects.Count == 0, string.Join("; ", defects) + "\n" + ancestorAttempt.Diagnostic);
    }

    [Fact]
    public void ScriptAndDocumentationDescribeTheBoundedRoundThirteenContract()
    {
        var script = File.ReadAllText(Path.Combine(RepositoryRoot, "scripts", "agent-gate.sh"));
        var docs = File.ReadAllText(Path.Combine(RepositoryRoot, "docs", "agent-workflow.md"));
        var verify = script[script.IndexOf("verify_example()", StringComparison.Ordinal)..];
        Assert.Contains("reject_ancestor_build_controls \"$target_repository\"", verify,
            StringComparison.Ordinal);
        Assert.Contains("NUGET_HTTP_CACHE_PATH=\"$report_directory/nuget-http-cache\"", verify,
            StringComparison.Ordinal);
        Assert.Contains("hostile `dotnet` earlier on `PATH`", docs, StringComparison.Ordinal);
        Assert.DoesNotContain("hostile earlier-`PATH`", docs, StringComparison.Ordinal);
    }

    [Fact]
    public void SdkRegressionDerivesTheExpectedVersionInsteadOfRequiringAnExtraPatchBand()
    {
        var tests = File.ReadAllText(Path.Combine(RepositoryRoot, "tests", "Mutate4CSharp.Tests",
            "IssueEightReviewRoundTwelveTests.cs"));
        var ci = File.ReadAllText(Path.Combine(RepositoryRoot, ".github", "workflows", "ci.yml"));
        Assert.DoesNotContain("8.0.418", ci, StringComparison.Ordinal);
        Assert.Contains("values[\"SDK_VERSION\"]", tests, StringComparison.Ordinal);
        Assert.Contains("rollForward\\\":\\\"latestPatch", tests, StringComparison.Ordinal);
    }

    private const int OrchestrationRefusal = 73;

    private static async Task CreateResolverAsync(string root, string marker)
    {
        var source = root + "-source";
        var output = Path.Combine(root, "HostileResolver");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(output);
        var sdkVersion = (await RunAsync(source, CanonicalDotnetHost(), "--version")).StandardOutput.Trim();
        var framework = Path.Combine(Path.GetDirectoryName(CanonicalDotnetHost())!, "sdk", sdkVersion,
            "Microsoft.Build.Framework.dll");
        await File.WriteAllTextAsync(Path.Combine(source, "HostileResolver.csproj"), $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework><AssemblyName>HostileResolver</AssemblyName></PropertyGroup>
              <ItemGroup><Reference Include="Microsoft.Build.Framework" HintPath="{framework}" Private="true" /></ItemGroup>
            </Project>
            """, TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(source, "Resolver.cs"), $$"""
            using Microsoft.Build.Framework;
            public sealed class HostileResolver : SdkResolver
            {
                public override string Name => "RoundThirteenHostileResolver";
                public override int Priority => 1;
                public override SdkResult Resolve(SdkReference reference, SdkResolverContext context, SdkResultFactory factory)
                {
                    System.IO.File.WriteAllText(@"{{marker}}", reference.Name);
                    return factory.IndicateFailure(new[] { "control resolver deliberately declines" });
                }
            }
            """, TestContext.Current.CancellationToken);
        var build = await RunAsync(source, CanonicalDotnetHost(), "build", "HostileResolver.csproj", "-c",
            "Release", "-o", output, "-m:1");
        Assert.True(build.ExitCode == 0, build.Diagnostic);
    }

    private static string[] SnapshotTree(string root) => Directory.EnumerateFileSystemEntries(root, "*",
            SearchOption.AllDirectories)
        .Select(path => Path.GetRelativePath(root, path) + (Directory.Exists(path) ? "/" :
            ":" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)))))
        .Order(StringComparer.Ordinal).ToArray();

    private static string PythonLiteral(string path) =>
        "r'" + path.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    private static async Task CopyTrackedRepositoryAsync(string destination)
    {
        Directory.CreateDirectory(destination);
        var listed = await RunAsync(RepositoryRoot, "git", "ls-files", "-z");
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

    private static string DotnetHost() => Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
    private static string CanonicalDotnetHost() =>
        new FileInfo(DotnetHost()).ResolveLinkTarget(true)?.FullName ?? Path.GetFullPath(DotnetHost());

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

    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError)
    { public string Diagnostic => $"exit={ExitCode}\nstdout:\n{StandardOutput}\nstderr:\n{StandardError}"; }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }
}
