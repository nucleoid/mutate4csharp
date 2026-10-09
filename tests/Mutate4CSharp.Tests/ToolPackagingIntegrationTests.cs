using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace Mutate4CSharp.Tests;

public sealed class ToolPackagingIntegrationTests : IDisposable
{
    private static readonly string RepositoryRoot = Path.GetFullPath("../../../../../", AppContext.BaseDirectory);
    private static readonly string SourceCommit = GitOutput("rev-parse", "HEAD").Trim();
    private static readonly string PackageVersion = $"0.1.0-local.{SourceCommit}";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mutate4csharp-tool-package", Guid.NewGuid().ToString("N"));

    public ToolPackagingIntegrationTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void LocalToolUsesAnExternalGeneratedManifestWithoutAnApplicationPackageReference()
    {
        Assert.False(File.Exists(Path.Combine(RepositoryRoot, ".config", "dotnet-tools.json")));
        Assert.Contains("tool install mutate4csharp --tool-path \"$payload\"",
            File.ReadAllText(Path.Combine(RepositoryRoot, "scripts", "agent-gate.sh")), StringComparison.Ordinal);

        var project = File.ReadAllText(Path.Combine(RepositoryRoot, "examples", "agent-gate", "src", "Example",
            "Example.csproj"));
        Assert.DoesNotContain("PackageReference", project, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PackedToolRestoresAndRunsFromAnIsolatedLocalFeedInACleanConsumer()
    {
        var configuredFeed = Environment.GetEnvironmentVariable("MUTATE4CSHARP_PACKED_FEED");
        var feed = configuredFeed is null ? Path.Combine(_root, "feed") : Path.GetFullPath(configuredFeed);
        var consumer = Path.Combine(_root, "clean consumer λ");
        Directory.CreateDirectory(feed);
        Directory.CreateDirectory(consumer);

        ProcessResult? pack = null;
        if (configuredFeed is null)
        {
            pack = await PackCurrentHeadAsync(feed, "restored-tool-source");
            Assert.Equal(0, pack.ExitCode);
        }
        var packagePath = Path.Combine(feed, $"mutate4csharp.{PackageVersion}.nupkg");
        Assert.True(File.Exists(packagePath), pack?.Diagnostic ?? $"Missing CI candidate {packagePath}");
        using (var package = ZipFile.OpenRead(packagePath))
        {
            var entries = package.Entries.Select(entry => entry.FullName).ToHashSet(StringComparer.Ordinal);
            Assert.Contains("README.md", entries);
            Assert.Contains("ATTRIBUTION.md", entries);
            Assert.Contains("NuGet.Config", entries);
            Assert.Contains("docs/agent-workflow.md", entries);
            Assert.Contains("docs/supported-matrix.md", entries);
            Assert.Contains("docs/change-scope.md", entries);
            Assert.Contains("docs/check-configuration.md", entries);
            Assert.Contains("docs/evaluation-contract.md", entries);
            Assert.Contains("docs/reproducibility.md", entries);
            Assert.Contains("docs/sidecar-state.md", entries);
            Assert.Contains("docs/snapshot-inputs.md", entries);
            Assert.Contains("scripts/agent-gate.sh", entries);
            Assert.Contains("docs/contracts/check-config-v1.schema.json", entries);
            Assert.Contains("docs/contracts/discovery-state-v1.schema.json", entries);
            Assert.Contains("docs/contracts/evaluation-report-v1.schema.json", entries);
            Assert.Contains("docs/contracts/proven-state-v1.schema.json", entries);
            Assert.Contains("NuGet.local.config", entries);
            Assert.Contains("examples/agent-gate/NuGet.Config", entries);
            Assert.Contains("examples/agent-gate/.gitignore", entries);
            Assert.Contains("examples/agent-gate/mutate4csharp.json", entries);
            Assert.Contains("examples/agent-gate/src/Example/Example.csproj", entries);
            Assert.Contains("examples/agent-gate/src/Example/Flag.cs", entries);
            Assert.Contains("examples/agent-gate/src/Example/packages.lock.json", entries);
            Assert.Contains("examples/agent-gate/tests/Example.Tests/Example.Tests.csproj", entries);
            Assert.Contains("examples/agent-gate/tests/Example.Tests/FlagTests.cs", entries);
            Assert.Contains("examples/agent-gate/tests/Example.Tests/packages.lock.json", entries);
            Assert.Contains("tools/net10.0/any/DotnetToolSettings.xml", entries);
            Assert.DoesNotContain(entries, entry =>
                entry.Contains("/bin/", StringComparison.OrdinalIgnoreCase) ||
                entry.Contains("/obj/", StringComparison.OrdinalIgnoreCase));
        }

        using (var package = ZipFile.OpenRead(packagePath))
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

        Assert.Equal(0, (await RunAsync(consumer, "git", "init", "--quiet")).ExitCode);
        Assert.Equal(0, (await RunAsync(consumer, "git", "config", "user.email", "fixture@example.invalid")).ExitCode);
        Assert.Equal(0, (await RunAsync(consumer, "git", "config", "user.name", "fixture")).ExitCode);
        Assert.Equal(0, (await RunAsync(consumer, "git", "add", ".")).ExitCode);
        Assert.Equal(0, (await RunAsync(consumer, "git", "commit", "--quiet", "-m", "fixture")).ExitCode);

        var toolConfig = Path.Combine(_root, "NuGet.local.config");
        File.WriteAllText(toolConfig, $$"""
            <?xml version="1.0" encoding="utf-8"?>
            <configuration>
              <packageSources>
                <clear />
                <add key="mutate4csharp-local" value="{{feed}}" />
              </packageSources>
            </configuration>
            """);
        var toolPath = Path.Combine(_root, "tool");
        var restore = await RunAsync(consumer, DotnetHost(), "tool", "install", "mutate4csharp",
            "--tool-path", toolPath, "--version", PackageVersion, "--configfile", toolConfig, "--no-cache");
        Assert.True(restore.ExitCode == 0, restore.Diagnostic);
        var toolCommand = Path.Combine(toolPath,
            OperatingSystem.IsWindows() ? "mutate4csharp.exe" : "mutate4csharp");
        Assert.True(File.Exists(toolCommand), $"Missing external tool command: {toolCommand}");

        var help = await RunAsync(consumer, toolCommand, "--help");
        Assert.Equal(0, help.ExitCode);
        Assert.Contains("5 not applicable", help.StandardOutput, StringComparison.OrdinalIgnoreCase);

        var identity = await RunAsync(consumer, toolCommand, "--version");
        Assert.True(identity.ExitCode == 0, identity.Diagnostic);
        Assert.Equal($"{PackageVersion}+{SourceCommit}", identity.StandardOutput.Trim());
        var payloadIdentity = HashDirectory(toolPath);

        var exampleRestore = await RunAsync(consumer, DotnetHost(), "restore", "src/Example/Example.csproj",
            "--configfile", "NuGet.Config", "--locked-mode", "--no-cache");
        Assert.Equal(0, exampleRestore.ExitCode);
        var exampleBuild = await RunAsync(consumer, DotnetHost(), "build", "src/Example/Example.csproj",
            "-c", "Release", "-m:1", "--no-restore");
        Assert.Equal(0, exampleBuild.ExitCode);
        var exampleTestsRestore = await RunAsync(consumer, DotnetHost(), "restore",
            "tests/Example.Tests/Example.Tests.csproj", "--configfile", "NuGet.Config", "--locked-mode", "--no-cache");
        Assert.Equal(0, exampleTestsRestore.ExitCode);
        var exampleTestsBuild = await RunAsync(consumer, DotnetHost(), "build",
            "tests/Example.Tests/Example.Tests.csproj", "-c", "Release", "-m:1", "--no-restore");
        Assert.Equal(0, exampleTestsBuild.ExitCode);

        var source = Path.Combine(consumer, "src", "Example", "Flag.cs");
        File.WriteAllText(source, File.ReadAllText(source).Replace(
            "configured && true", "configured || false", StringComparison.Ordinal));
        var sourceBefore = File.ReadAllBytes(source);
        var indexBefore = (await RunAsync(consumer, "git", "diff", "--cached", "--binary")).StandardOutput;
        var statusBefore = (await RunAsync(consumer, "git", "status", "--short", "--untracked-files=all")).StandardOutput;
        var stateBefore = SnapshotState(consumer);
        var report = Path.Combine(_root, "no-state-report.json");
        Assert.Equal(payloadIdentity, HashDirectory(toolPath));
        var check = await RunAsync(consumer, toolCommand, "check", "--base", "HEAD", "--no-state",
            "--report", report);
        Assert.Equal(4, check.ExitCode);
        Assert.DoesNotContain("SNAPSHOT_REFUSED", check.Diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain("UNSUPPORTED_CHANGED_INPUT", check.Diagnostic, StringComparison.Ordinal);
        using var parsed = JsonDocument.Parse(File.ReadAllBytes(report));
        Assert.Equal("INCOMPLETE", parsed.RootElement.GetProperty("outcome").GetString());
        Assert.Contains(parsed.RootElement.GetProperty("reasons").EnumerateArray(),
            reason => reason.GetProperty("code").GetString() == "EXECUTION_NOT_IMPLEMENTED");
        Assert.Equal(JsonValueKind.Number,
            parsed.RootElement.GetProperty("counts").GetProperty("enumerated").ValueKind);
        Assert.Equal(sourceBefore, File.ReadAllBytes(source));
        Assert.Equal(indexBefore, (await RunAsync(consumer, "git", "diff", "--cached", "--binary")).StandardOutput);
        Assert.Equal(statusBefore, (await RunAsync(consumer, "git", "status", "--short", "--untracked-files=all")).StandardOutput);
        Assert.Equal(stateBefore, SnapshotState(consumer));

        var defaultReport = Path.Combine(_root, "default-state-report.json");
        Assert.Equal(payloadIdentity, HashDirectory(toolPath));
        var defaultCheck = await RunAsync(consumer, toolCommand, "check", "--base", "HEAD",
            "--report", defaultReport);
        Assert.Equal(4, defaultCheck.ExitCode);
        Assert.DoesNotContain("SNAPSHOT_REFUSED", defaultCheck.Diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain("UNSUPPORTED_CHANGED_INPUT", defaultCheck.Diagnostic, StringComparison.Ordinal);
        Assert.Equal(sourceBefore, File.ReadAllBytes(source));
        Assert.Equal(indexBefore, (await RunAsync(consumer, "git", "diff", "--cached", "--binary")).StandardOutput);
        Assert.Equal(statusBefore, (await RunAsync(consumer, "git", "status", "--short", "--untracked-files=all")).StandardOutput);
        var defaultState = SnapshotState(consumer);
        Assert.Contains(Path.Combine(".mutate4csharp", ".gitignore"), defaultState);
        Assert.Contains(defaultState, path => path.StartsWith(
            Path.Combine(".mutate4csharp", "discovery") + Path.DirectorySeparatorChar,
            StringComparison.Ordinal));
        Assert.DoesNotContain(defaultState, path => path.StartsWith(
            Path.Combine(".mutate4csharp", "proven") + Path.DirectorySeparatorChar,
            StringComparison.Ordinal));
        Assert.All(defaultState, path => Assert.True(
            path == Path.Combine(".mutate4csharp", ".gitignore") ||
            path == Path.Combine(".mutate4csharp", "discovery") ||
            path.StartsWith(Path.Combine(".mutate4csharp", "discovery") + Path.DirectorySeparatorChar,
                StringComparison.Ordinal) ||
            path == Path.Combine(".mutate4csharp", "locks") ||
            path.StartsWith(Path.Combine(".mutate4csharp", "locks") + Path.DirectorySeparatorChar,
                StringComparison.Ordinal),
            $"Unexpected default strict state path: {path}"));
    }

    [Fact]
    public async Task PlainToolRestoreUsesRepositorySourceMapping()
    {
        var feed = Path.Combine(_root, "plain-consumer", "artifacts", "local-feed");
        var consumer = Path.Combine(_root, "plain-consumer");
        Directory.CreateDirectory(feed);

        var pack = await PackCurrentHeadAsync(feed, "plain-restore-source");
        Assert.True(pack.ExitCode == 0, pack.Diagnostic);
        WriteToolManifest(consumer);
        Copy(Path.Combine(RepositoryRoot, "NuGet.Config"), Path.Combine(consumer, "NuGet.Config"));

        var restore = await RunAsync(consumer, DotnetHost(), "tool", "restore", "--no-cache");
        Assert.True(restore.ExitCode == 0, restore.Diagnostic);
        var help = await RunAsync(consumer, DotnetHost(), "tool", "run", "mutate4csharp", "--", "--help");
        Assert.True(help.ExitCode == 0, help.Diagnostic);
        Assert.Contains("5 not applicable", help.StandardOutput, StringComparison.OrdinalIgnoreCase);

        var restoredCandidate = Path.Combine(_root, "nuget-packages", "mutate4csharp", PackageVersion,
            "tools", "net10.0", "any", "Mutate4CSharp.dll");
        Assert.True(File.Exists(restoredCandidate), $"Missing mapped candidate: {restoredCandidate}");
    }

    [Fact]
    public async Task ExtractedExamplePlainRestoreRejectsCandidateFromSurrogatePublicFeed()
    {
        var packageFeed = Path.Combine(_root, "candidate-feed");
        var consumer = Path.Combine(_root, "extracted-example-consumer");
        var localFeed = Path.Combine(_root, "mutate4csharp-local-feed");
        var surrogatePublicFeed = Path.Combine(_root, "surrogate-public-feed");
        Directory.CreateDirectory(packageFeed);
        Directory.CreateDirectory(consumer);
        Directory.CreateDirectory(localFeed);
        Directory.CreateDirectory(surrogatePublicFeed);

        var pack = await PackCurrentHeadAsync(packageFeed, "surrogate-source");
        Assert.True(pack.ExitCode == 0, pack.Diagnostic);
        var packagePath = Path.Combine(packageFeed, $"mutate4csharp.{PackageVersion}.nupkg");
        using (var package = ZipFile.OpenRead(packagePath))
        {
            foreach (var entry in package.Entries.Where(entry =>
                         entry.FullName.StartsWith("examples/agent-gate/", StringComparison.Ordinal) &&
                         !string.IsNullOrEmpty(entry.Name)))
            {
                var destination = Path.Combine(consumer, entry.FullName["examples/agent-gate/".Length..]);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                entry.ExtractToFile(destination);
            }
        }
        WriteToolManifest(consumer);
        File.Copy(packagePath, Path.Combine(surrogatePublicFeed, Path.GetFileName(packagePath)));

        var configPath = Path.Combine(consumer, "NuGet.Config");
        var config = File.ReadAllText(configPath)
            .Replace("../mutate4csharp-local-feed", localFeed, StringComparison.Ordinal)
            .Replace("https://api.nuget.org/v3/index.json", surrogatePublicFeed, StringComparison.Ordinal);
        File.WriteAllText(configPath, config);

        var rejected = await RunAsync(consumer, DotnetHost(), "tool", "restore", "--no-cache");
        Assert.NotEqual(0, rejected.ExitCode);
        Assert.Contains("mutate4csharp", rejected.Diagnostic, StringComparison.OrdinalIgnoreCase);

        File.Copy(packagePath, Path.Combine(localFeed, Path.GetFileName(packagePath)));
        var accepted = await RunAsync(consumer, DotnetHost(), "tool", "restore", "--no-cache");
        Assert.True(accepted.ExitCode == 0, accepted.Diagnostic);
    }

    [Fact]
    public async Task PackedExampleInventoryIsExactDespiteIgnoredBuildFilesInATemporaryRepositoryCopy()
    {
        var repositoryCopy = Path.Combine(_root, "tracked-repository-copy");
        await CopyTrackedRepository(repositoryCopy);
        var example = Path.Combine(repositoryCopy, "examples", "agent-gate");
        var hostileIgnored = Path.Combine(example, ".idea", "hostile-ignored.txt");
        var hostileBuildOutput = Path.Combine(example, "src", "Example", "bin", "probe", "hostile-build.txt");
        var hostileIntermediate = Path.Combine(example, "src", "Example", "obj", "probe",
            "hostile-intermediate.txt");
        var packageOutput = Path.Combine(_root, "seeded-example-feed");

        Directory.CreateDirectory(Path.GetDirectoryName(hostileIgnored)!);
        Directory.CreateDirectory(Path.GetDirectoryName(hostileBuildOutput)!);
        Directory.CreateDirectory(Path.GetDirectoryName(hostileIntermediate)!);
        File.WriteAllText(hostileIgnored, "ignored IDE state must not be packaged");
        File.WriteAllText(hostileBuildOutput, "build output must not be packaged");
        File.WriteAllText(hostileIntermediate, "intermediate output must not be packaged");
        Assert.Empty((await RunAsync(repositoryCopy, "git", "status", "--porcelain=v1",
            "--untracked-files=all")).StandardOutput);

        var copyCommit = (await RunAsync(repositoryCopy, "git", "rev-parse", "HEAD")).StandardOutput.Trim();
        var copyVersion = $"0.1.0-local.{copyCommit}";
        var restore = await RunAsync(repositoryCopy, DotnetHost(), "restore",
            "src/Mutate4CSharp/Mutate4CSharp.csproj", "--configfile", "NuGet.Config", "--no-cache");
        Assert.True(restore.ExitCode == 0, restore.Diagnostic);
        var pack = await PackIsolatedAsync(repositoryCopy,
            "src/Mutate4CSharp/Mutate4CSharp.csproj", "-c", "Release", "-m:1",
            $"-p:Version={copyVersion}", $"-p:SourceRevisionId={copyCommit}", "-o", packageOutput);
        Assert.True(pack.ExitCode == 0, pack.Diagnostic);

        var packagePath = Path.Combine(packageOutput, $"mutate4csharp.{copyVersion}.nupkg");
        Assert.True(File.Exists(packagePath), pack.Diagnostic);
        using var package = ZipFile.OpenRead(packagePath);
        var actualExampleEntries = package.Entries
            .Where(entry => entry.FullName.StartsWith("examples/agent-gate/", StringComparison.Ordinal))
            .Select(entry => entry.FullName)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var expectedExampleEntries = new[]
        {
            "examples/agent-gate/.gitignore",
            "examples/agent-gate/NuGet.Config",
            "examples/agent-gate/mutate4csharp.json",
            "examples/agent-gate/src/Example/Example.csproj",
            "examples/agent-gate/src/Example/Flag.cs",
            "examples/agent-gate/src/Example/packages.lock.json",
            "examples/agent-gate/tests/Example.Tests/Example.Tests.csproj",
            "examples/agent-gate/tests/Example.Tests/FlagTests.cs",
            "examples/agent-gate/tests/Example.Tests/packages.lock.json"
        };
        Assert.Equal(expectedExampleEntries, actualExampleEntries);
        Assert.DoesNotContain(package.Entries, entry => entry.FullName.Contains("hostile-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PackagedAgentWorkflowKeepsManifestGuidanceWithoutABrokenPackageLink()
    {
        var packageOutput = Path.Combine(_root, "packaged-doc-feed");
        var pack = await PackCurrentHeadAsync(packageOutput, "packaged-doc-source");
        Assert.Equal(0, pack.ExitCode);

        var packagePath = Path.Combine(packageOutput, $"mutate4csharp.{PackageVersion}.nupkg");
        Assert.True(File.Exists(packagePath), pack.Diagnostic);
        using var package = ZipFile.OpenRead(packagePath);
        var workflow = package.GetEntry("docs/agent-workflow.md");
        var script = package.GetEntry("scripts/agent-gate.sh");
        Assert.NotNull(workflow);
        Assert.NotNull(script);
        using var reader = new StreamReader(workflow.Open());
        var text = await reader.ReadToEndAsync(TestContext.Current.CancellationToken);
        Assert.Contains("scripts/agent-gate.sh", text, StringComparison.Ordinal);
        Assert.DoesNotContain("checked-in repository tool manifest", text, StringComparison.Ordinal);
    }

    private static string DotnetHost() => Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";

    private async Task<ProcessResult> PackCurrentHeadAsync(string output, string sourceName)
    {
        var source = Path.Combine(_root, sourceName);
        var clone = await RunAsync(_root, "git", "clone", "--quiet", "--no-local", RepositoryRoot, source);
        Assert.True(clone.ExitCode == 0, clone.Diagnostic);
        var restore = await RunAsync(source, DotnetHost(), "restore",
            "src/Mutate4CSharp/Mutate4CSharp.csproj", "-m:1", "--locked-mode", "--configfile", "NuGet.Config");
        Assert.True(restore.ExitCode == 0, restore.Diagnostic);
        return await PackIsolatedAsync(source, "src/Mutate4CSharp/Mutate4CSharp.csproj",
            "-c", "Release", "-m:1", $"-p:Version={PackageVersion}",
            $"-p:SourceRevisionId={SourceCommit}", "-o", output);
    }

    private async Task<ProcessResult> PackIsolatedAsync(string source, params string[] arguments)
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

    private static void Copy(string source, string destination)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Copy(source, destination);
    }

    private static void WriteToolManifest(string consumer)
    {
        var manifestPath = Path.Combine(consumer, ".config", "dotnet-tools.json");
        Directory.CreateDirectory(Path.GetDirectoryName(manifestPath)!);
        File.WriteAllText(manifestPath, $$"""
            {
              "version": 1,
              "isRoot": true,
              "tools": {
                "mutate4csharp": {
                  "version": "{{PackageVersion}}",
                  "commands": [ "mutate4csharp" ]
                }
              }
            }
            """);
    }

    private static string GitOutput(params string[] arguments)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = RepositoryRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0) throw new InvalidOperationException(error);
        return output;
    }

    private static string HashDirectory(string root)
    {
        using var aggregate = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                     .Order(StringComparer.Ordinal))
        {
            var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
            aggregate.AppendData(System.Text.Encoding.UTF8.GetBytes(relative));
            aggregate.AppendData([0]);
            aggregate.AppendData(SHA256.HashData(File.ReadAllBytes(path)));
        }
        return Convert.ToHexString(aggregate.GetHashAndReset());
    }

    private async Task CopyTrackedRepository(string destination)
    {
        var tracked = await RunAsync(RepositoryRoot, "git", "ls-files", "-z");
        Assert.Equal(0, tracked.ExitCode);
        var relativePaths = tracked.StandardOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Concat([
                "NuGet.Config",
                "examples/agent-gate/NuGet.Config",
                "examples/agent-gate/src/Example/packages.lock.json",
                "examples/agent-gate/tests/Example.Tests/packages.lock.json"
            ])
            .Distinct(StringComparer.Ordinal);
        foreach (var relativePath in relativePaths)
        {
            var source = Path.Combine(RepositoryRoot, relativePath);
            if (File.Exists(source)) Copy(source, Path.Combine(destination, relativePath));
        }
        Assert.Equal(0, (await RunAsync(destination, "git", "init", "--quiet")).ExitCode);
        Assert.Equal(0, (await RunAsync(destination, "git", "config", "user.email",
            "fixture@example.invalid")).ExitCode);
        Assert.Equal(0, (await RunAsync(destination, "git", "config", "user.name", "fixture")).ExitCode);
        Assert.Equal(0, (await RunAsync(destination, "git", "add", ".")).ExitCode);
        Assert.Equal(0, (await RunAsync(destination, "git", "commit", "--quiet", "-m", "fixture")).ExitCode);
    }

    private static string[] SnapshotState(string root)
    {
        var stateRoot = Path.Combine(root, ".mutate4csharp");
        return Directory.Exists(stateRoot)
            ? Directory.GetFileSystemEntries(stateRoot, "*", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(root, path))
                .Order(StringComparer.Ordinal)
                .ToArray()
            : [];
    }

    private async Task<ProcessResult> RunAsync(string workingDirectory, string executable, params string[] arguments)
    {
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        if (!Path.GetFullPath(workingDirectory).Equals(RepositoryRoot, StringComparison.Ordinal) &&
            Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            start.Environment["DOTNET_CLI_HOME"] = Path.Combine(_root, "dotnet-home");
            start.Environment["NUGET_PACKAGES"] = Path.Combine(_root, "nuget-packages");
            start.Environment["DOTNET_NOLOGO"] = "1";
        }
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
