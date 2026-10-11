using System.IO.Compression;
using System.Text.Json;

namespace Mutate4CSharp.Tests;

public sealed partial class ToolPackagingIntegrationTests
{
    [Fact(Timeout = 1_800_000)]
    public async Task InstalledStrictCliCertifiesRealOutcomesFromIndependentCleanConsumers()
    {
        var feed = Environment.GetEnvironmentVariable("MUTATE4CSHARP_PACKED_FEED") ?? Path.Combine(_root, "contract-feed");
        Directory.CreateDirectory(feed);
        if (Environment.GetEnvironmentVariable("MUTATE4CSHARP_PACKED_FEED") is null)
        {
            var pack = await PackCurrentHeadAsync(feed, "contract-source");
            Assert.True(pack.ExitCode == 0, pack.Diagnostic);
        }
        var packagePath = Path.Combine(feed, $"mutate4csharp.{PackageVersion}.nupkg");
        var config = Path.Combine(_root, "contract-tool.config");
        File.WriteAllText(config, $"<configuration><packageSources><clear /><add key=\"local\" value=\"{System.Security.SecurityElement.Escape(feed)}\" /></packageSources></configuration>");
        var toolPath = Path.Combine(_root, "contract-tool");
        var install = await RunAsync(_root, DotnetHost(), "tool", "install", "mutate4csharp", "--tool-path", toolPath,
            "--version", PackageVersion, "--configfile", config, "--no-cache");
        Assert.True(install.ExitCode == 0, install.Diagnostic);
        var command = Path.Combine(toolPath, OperatingSystem.IsWindows() ? "mutate4csharp.exe" : "mutate4csharp");
        Assert.Equal($"{PackageVersion}+{SourceCommit}", (await RunAsync(_root, command, "--version")).StandardOutput.Trim());
        var payload = HashDirectory(toolPath);

        foreach (var scenario in new[] { "strong", "weak", "uncovered", "red", "empty", "zero" })
        {
            var consumer = Path.Combine(_root, "contract-" + scenario);
            Directory.CreateDirectory(consumer);
            using (var package = ZipFile.OpenRead(packagePath))
                foreach (var entry in package.Entries.Where(entry => entry.FullName.StartsWith("examples/agent-gate/", StringComparison.Ordinal) && entry.Name.Length > 0))
                {
                    var target = Path.GetFullPath(Path.Combine(consumer, entry.FullName["examples/agent-gate/".Length..]));
                    Assert.StartsWith(Path.GetFullPath(consumer) + Path.DirectorySeparatorChar, target,
                        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    entry.ExtractToFile(target);
                }
            var source = Path.Combine(consumer, "src", "Example", "Flag.cs");
            var tests = Path.Combine(consumer, "tests", "Example.Tests", "FlagTests.cs");
            File.WriteAllText(tests, scenario switch
            {
                "weak" => "using Example; using Xunit; public class FlagTests { [Fact] public void Smoke() { _ = Flag.Value(); Assert.True(true); } }",
                "uncovered" => "using Example; using Xunit; public class FlagTests { [Fact] public void OtherMethod() => Assert.True(Flag.Other(true)); }",
                "red" => "using Example; using Xunit; public class FlagTests { [Fact] public void Red() => Assert.False(Flag.Value()); }",
                "empty" => "using Example; using Xunit; public class FlagTests { [Fact(Skip=\"Intentional empty executed baseline\")] public void Skipped() => Assert.True(Flag.Value()); }",
                "zero" => "using Example; using Xunit; public class FlagTests { [Fact] public void Identity() => Assert.True(Flag.Value(true)); }",
                _ => "using Example; using Xunit; public class FlagTests { [Fact] public void Behavior() => Assert.True(Flag.Value()); }"
            });
            File.WriteAllText(source, scenario == "zero"
                ? "namespace Example; public static class Flag { public static bool Value(bool input) { return input; } }"
                : "namespace Example;\npublic static class Flag\n{\n public static bool Other(bool input) => input;\n public static bool Value() => false;\n}\n");
            foreach (var arguments in new[] { new[] { "init", "--quiet" }, new[] { "config", "user.email", "fixture@example.invalid" },
                         new[] { "config", "user.name", "fixture" }, new[] { "add", "." }, new[] { "commit", "--quiet", "-m", "consumer baseline" } })
                Assert.Equal(0, (await RunAsync(consumer, "git", arguments)).ExitCode);
            var baseline = (await RunAsync(consumer, "git", "rev-parse", "HEAD")).StandardOutput.Trim();
            File.WriteAllText(source, scenario == "zero"
                ? "namespace Example; public static class Flag { public static bool Value(bool input) => input; }"
                : "namespace Example;\npublic static class Flag\n{\n public static bool Other(bool input) => input;\n public static bool Value() => true;\n}\n");
            Assert.Equal(0, (await RunAsync(consumer, "git", "add", ".")).ExitCode);
            Assert.Equal(0, (await RunAsync(consumer, "git", "commit", "--quiet", "-m", "changed implementation")).ExitCode);
            var bytes = File.ReadAllBytes(source);
            var index = (await RunAsync(consumer, "git", "diff", "--cached", "--binary")).StandardOutput;
            var status = (await RunAsync(consumer, "git", "status", "--porcelain=v1", "--untracked-files=all")).StandardOutput;
            Assert.Empty(status);
            JsonDocument? first = null;
            try
            {
                foreach (var noState in new[] { true, false })
                {
                    var reportPath = Path.Combine(_root, $"contract-{scenario}-{noState}.json");
                    var arguments = new List<string> { "check", "--base", baseline, "--report", reportPath };
                    if (noState) arguments.Add("--no-state");
                    var result = await RunAsync(consumer, command, arguments.ToArray());
                    using var report = JsonDocument.Parse(File.ReadAllBytes(reportPath));
                    var outcome = scenario switch { "strong" => "PASS", "weak" or "uncovered" => "FAIL", "zero" => "NOT_APPLICABLE", _ => "INCOMPLETE" };
                    var exit = scenario switch { "strong" => 0, "weak" or "uncovered" => 3, "zero" => 5, _ => 2 };
                    Assert.True(result.ExitCode == exit, scenario + ": " + result.Diagnostic);
                    Assert.Equal(outcome, report.RootElement.GetProperty("outcome").GetString());
                    var counts = report.RootElement.GetProperty("counts");
                    Assert.Equal(scenario == "zero" ? 0 : 1, counts.GetProperty("enumerated").GetInt32());
                    if (scenario == "strong") Assert.True(counts.GetProperty("executed").GetInt32() > 0 && counts.GetProperty("killed").GetInt32() > 0);
                    if (scenario == "weak") Assert.True(counts.GetProperty("survived").GetInt32() > 0);
                    if (scenario == "uncovered") Assert.Equal(1, counts.GetProperty("freshUncovered").GetInt32());
                    if (first is null) first = JsonDocument.Parse(File.ReadAllBytes(reportPath));
                    else Assert.Equal(first.RootElement.GetProperty("counts").GetRawText(), counts.GetRawText());
                    Assert.Equal(bytes, File.ReadAllBytes(source));
                    Assert.Equal(index, (await RunAsync(consumer, "git", "diff", "--cached", "--binary")).StandardOutput);
                    Assert.Equal(status, (await RunAsync(consumer, "git", "status", "--porcelain=v1", "--untracked-files=all")).StandardOutput);
                    Assert.Equal(payload, HashDirectory(toolPath));
                    if (noState) Assert.False(Directory.Exists(Path.Combine(consumer, ".mutate4csharp")));
                }
                var proof = Path.Combine(consumer, ".mutate4csharp", "proven");
                Assert.Equal(scenario == "strong", Directory.Exists(proof) && Directory.EnumerateFiles(proof, "*.json").Any());
            }
            finally { first?.Dispose(); }
        }
    }
}
