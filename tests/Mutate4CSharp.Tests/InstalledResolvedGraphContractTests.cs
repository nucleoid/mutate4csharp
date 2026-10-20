using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mutate4CSharp.Tests;

public sealed partial class ToolPackagingIntegrationTests
{
    [Fact(Timeout = 1_100_000)]
    [Trait("InstalledProfile", "graph-sdk401")]
    public async Task InstalledResolvedGraphUsesPackagedHelpersAndExplicitRegeneration()
    {
        Assert.False(OperatingSystem.IsWindows(), "This installed profile requires the existing Linux prepare route.");
        var feed = Environment.GetEnvironmentVariable("MUTATE4CSHARP_INSTALLED_GRAPH_FEED");
        Assert.False(string.IsNullOrEmpty(feed), "Prepare a fresh same-source graph-sdk401 feed before this installed-profile case.");
        var host = Environment.GetEnvironmentVariable("MUTATE4CSHARP_INSTALLED_GRAPH_HOST");
        Assert.True(host is not null && Path.IsPathFullyQualified(host), "Exact prepared DOTNET_HOST_PATH required.");
        var evidence = Path.Combine(Environment.GetEnvironmentVariable("MUTATE4CSHARP_CERTIFICATION_DIRECTORY") ??
            Path.Combine(Path.GetTempPath(), "mutate4csharp-installed-graph-evidence"), "resolved-graph-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(evidence);
        var packagePath = Path.Combine(feed!, $"mutate4csharp.{PackageVersion}.nupkg");
        var consumer = Path.Combine(_root, "installed graph consumer");
        Directory.CreateDirectory(consumer);
        using (var package = ZipFile.OpenRead(packagePath))
        {
            Assert.Contains(package.Entries, entry => entry.FullName == "tools/net10.0/any/graph/sdk401/deployment.json");
            foreach (var entry in package.Entries.Where(entry => entry.Name.Length > 0 &&
                         entry.FullName.StartsWith("examples/agent-gate/", StringComparison.Ordinal)))
            {
                var destination = Path.GetFullPath(entry.FullName["examples/agent-gate/".Length..], consumer);
                Assert.StartsWith(consumer + Path.DirectorySeparatorChar, destination, StringComparison.Ordinal);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                entry.ExtractToFile(destination);
            }
        }
        // Real initial locks come from the packaged example. Only the selected
        // SDK/configuration are explicit fixture inputs; no synthetic lockfiles.
        File.WriteAllText(Path.Combine(consumer, "global.json"),
            "{\"sdk\":{\"version\":\"10.0.401\",\"rollForward\":\"disable\",\"allowPrerelease\":false}}\n");
        var configPath = Path.Combine(consumer, "mutate4csharp.json");
        var configuration = JsonNode.Parse(File.ReadAllText(configPath))!.AsObject();
        configuration["version"] = 2;
        configuration["semanticProfile"] = "resolved-project-graph-v1";
        configuration["projects"]![0]!["configuration"] = "Release";
        configuration["policy"]!["overallDeadlineSeconds"] = 480;
        configuration["policy"]!["baselineTimeoutSeconds"] = 120;
        configuration["policy"]!["mutantTimeoutSeconds"] = 30;
        File.WriteAllText(configPath, configuration.ToJsonString());
        foreach (var arguments in new[] { new[] { "init", "--quiet" }, new[] { "config", "user.email", "fixture@example.invalid" },
                     new[] { "config", "user.name", "fixture" }, new[] { "add", "." }, new[] { "commit", "--quiet", "-m", "graph fixture" } })
        {
            var git = await RunAsync(consumer, "git", arguments);
            Assert.True(git.ExitCode == 0, git.Diagnostic);
        }
        var baseline = (await RunAsync(consumer, "git", "rev-parse", "HEAD")).StandardOutput.Trim();
        var source = Path.Combine(consumer, "src", "Example", "Flag.cs");
        File.WriteAllText(source, File.ReadAllText(source).Replace("configured && true", "configured || false", StringComparison.Ordinal));
        var sourceBefore = File.ReadAllBytes(source);
        var statusBefore = (await RunAsync(consumer, "git", "status", "--porcelain=v1", "--untracked-files=all")).StandardOutput;
        var toolConfig = Path.Combine(_root, "graph-tool.config");
        File.WriteAllText(toolConfig, $"<configuration><packageSources><clear/><add key=\"local\" value=\"{System.Security.SecurityElement.Escape(feed)}\"/></packageSources></configuration>");
        var toolPath = Path.Combine(_root, "graph-tool");
        var install = await RunAsync(consumer, host!, "tool", "install", "mutate4csharp", "--tool-path", toolPath,
            "--version", PackageVersion, "--configfile", toolConfig, "--no-cache");
        File.WriteAllText(Path.Combine(evidence, "install.txt"), install.Diagnostic);
        Assert.True(install.ExitCode == 0, install.Diagnostic);
        var command = Path.Combine(toolPath, "mutate4csharp");
        var installedManifest = Directory.EnumerateFiles(toolPath, "deployment.json", SearchOption.AllDirectories).Single();
        var installedGenerator = Directory.EnumerateFiles(toolPath, "create-graph-deployment.py", SearchOption.AllDirectories).Single();
        Assert.Equal(File.ReadAllBytes(Path.Combine(RepositoryRoot, "scripts", "create-graph-deployment.py")), File.ReadAllBytes(installedGenerator));
        var graphRoot = Path.GetDirectoryName(installedManifest)!;
        var payloadBefore = HashDirectory(toolPath);
        var keys = new[] { "DOTNET_HOST_PATH", "MUTATE4CSHARP_GRAPH_CAPTURE_MANIFEST", "MUTATE4CSHARP_GRAPH_CAPTURE_MANIFEST_SHA256" };
        var previous = keys.ToDictionary(key => key, Environment.GetEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable("DOTNET_HOST_PATH", host);
            Environment.SetEnvironmentVariable(keys[1], null);
            Environment.SetEnvironmentVariable(keys[2], null);
            await Check("packaged", positive: true);

            // Installation regeneration uses the already installed helpers and
            // exact selected host/SDK images. It never changes the package or
            // invents a fallback SDK selection.
            var regenerated = Path.Combine(evidence, "deployment.json");
            var generation = await RunAsync(consumer, "python3", "-I",
                installedGenerator, "--external-manifest",
                "--host", host!, "--logger-dir", Path.Combine(graphRoot, "logger"), "--worker-dir", Path.Combine(graphRoot, "worker"),
                "--task", Path.Combine(Path.GetDirectoryName(host)!, "sdk", "10.0.401", "Roslyn", "Microsoft.Build.Tasks.CodeAnalysis.dll"),
                "--source-revision", SourceCommit, "--output", regenerated);
            File.WriteAllText(Path.Combine(evidence, "regeneration.txt"), generation.Diagnostic);
            Assert.True(generation.ExitCode == 0, generation.Diagnostic);
            var digest = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(regenerated)));
            Assert.Equal(digest, generation.StandardOutput.Trim());
            Environment.SetEnvironmentVariable(keys[1], regenerated);
            Environment.SetEnvironmentVariable(keys[2], digest);
            Environment.SetEnvironmentVariable("DOTNET_HOST_PATH", Path.Combine(_root, "different-host", "dotnet"));
            await Check("wrong-host", positive: false);
            Environment.SetEnvironmentVariable("DOTNET_HOST_PATH", host);
            await Check("regenerated", positive: true);
            Assert.Equal(payloadBefore, HashDirectory(toolPath));
            Assert.Equal(sourceBefore, File.ReadAllBytes(source));
            Assert.Equal(statusBefore, (await RunAsync(consumer, "git", "status", "--porcelain=v1", "--untracked-files=all")).StandardOutput);
            File.WriteAllText(Path.Combine(evidence, "identity.json"), JsonSerializer.Serialize(new {
                schema = "mutate4csharp-installed-resolved-graph-fixture-v1", fixture = true,
                sourceCommit = SourceCommit, packageVersion = PackageVersion, baseline, host,
                packageSha256 = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(packagePath))),
                payloadSha256 = payloadBefore, regeneratedManifestSha256 = digest
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        finally { foreach (var (key, value) in previous) Environment.SetEnvironmentVariable(key, value); }

        async Task Check(string name, bool positive)
        {
            var report = Path.Combine(evidence, name + ".json");
            var run = await RunAsync(consumer, command, "check", "--base", baseline, "--no-state", "--report", report);
            File.WriteAllText(Path.Combine(evidence, name + ".txt"), run.Diagnostic);
            Assert.True(File.Exists(report), run.Diagnostic);
            using var parsed = JsonDocument.Parse(File.ReadAllBytes(report));
            var document = parsed.RootElement;
            Assert.Equal("2", document.GetProperty("schemaVersion").GetString());
            if (positive)
            {
                // The deliberately weak test suite must expose a real survivor;
                // neither INCOMPLETE nor a baseline-only run satisfies this.
                Assert.True(run.ExitCode == 3, run.Diagnostic);
                Assert.Equal("FAIL", document.GetProperty("outcome").GetString());
                Assert.True(document.GetProperty("counts").GetProperty("enumerated").GetInt32() > 0);
                Assert.True(document.GetProperty("counts").GetProperty("executed").GetInt32() > 0);
                Assert.Contains(document.GetProperty("evidence").EnumerateArray(), entry =>
                    entry.GetProperty("kind").GetString() == "DEPENDENCY_INPUT" &&
                    entry.GetProperty("diagnostics").EnumerateArray().Any(value => value.GetString() == "sdk=10.0.401"));
                Assert.Empty(document.GetProperty("incompleteConditions").EnumerateArray());
                Assert.Contains(document.GetProperty("reasons").EnumerateArray(), reason => reason.GetProperty("code").GetString() == "SURVIVED_MUTATION");
            }
            else
            {
                Assert.Equal("INCOMPLETE", document.GetProperty("outcome").GetString());
                Assert.Contains(document.GetProperty("reasons").EnumerateArray(), reason => reason.GetProperty("code").GetString() == "GRAPH_DEPLOYMENT_STALE");
                Assert.Equal(0, document.GetProperty("counts").GetProperty("executed").GetInt32());
            }
        }
    }
}
