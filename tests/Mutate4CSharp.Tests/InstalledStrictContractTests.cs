using System.IO.Compression;
using System.Text.Json;

namespace Mutate4CSharp.Tests;

public sealed partial class ToolPackagingIntegrationTests
{
    [Fact(Timeout = 3_600_000)]
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

        foreach (var scenario in new[] { "strong", "weak", "uncovered", "red", "empty", "zero", "partial", "unsupported", "timeout" })
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
                "timeout" => "using Example; using Xunit; public class FlagTests { [Fact] public void Slow() { System.Threading.Thread.Sleep(20_000); Assert.True(Flag.Value()); } }",
                _ => "using Example; using Xunit; public class FlagTests { [Fact] public void Behavior() => Assert.True(Flag.Value()); }"
            });
            if (scenario == "timeout")
            {
                var policyPath = Path.Combine(consumer, "mutate4csharp.json");
                File.WriteAllText(policyPath, File.ReadAllText(policyPath).Replace("\"baselineTimeoutSeconds\": 600", "\"baselineTimeoutSeconds\": 1"));
            }
            if (scenario == "unsupported")
            {
                var project = Path.Combine(consumer, "src", "Example", "Example.csproj");
                File.WriteAllText(project, File.ReadAllText(project).Replace("</Project>", "<PropertyGroup><TargetFramework>net9.0</TargetFramework></PropertyGroup></Project>"));
            }
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
                    if (scenario == "partial") arguments.Add("--plan");
                    var result = await RunAsync(consumer, command, arguments.ToArray());
                    using var report = JsonDocument.Parse(File.ReadAllBytes(reportPath));
                    var outcome = scenario switch { "strong" => "PASS", "weak" or "uncovered" => "FAIL", "zero" => "NOT_APPLICABLE", _ => "INCOMPLETE" };
                    var exit = scenario switch { "strong" => 0, "weak" or "uncovered" => 3, "zero" => 5, "red" or "empty" => 2, _ => 4 };
                    Assert.True(result.ExitCode == exit, scenario + ": " + result.Diagnostic);
                    Assert.Equal(outcome, report.RootElement.GetProperty("outcome").GetString());
                    var counts = report.RootElement.GetProperty("counts");
                    Assert.Equal("2", report.RootElement.GetProperty("schemaVersion").GetString());
                    Assert.False(string.IsNullOrWhiteSpace(report.RootElement.GetProperty("runId").GetString()));
                    var units = report.RootElement.GetProperty("units").EnumerateArray().ToArray();
                    Assert.Equal(counts.GetProperty("selected").GetInt32(), units.Length);
                    Assert.Equal(units.Length, units.Select(unit => unit.GetProperty("evaluationUnitId").GetString()).Distinct().Count());
                    Assert.Equal(units.Length, counts.GetProperty("executed").GetInt32() + counts.GetProperty("freshUncovered").GetInt32() + counts.GetProperty("omitted").GetInt32());
                    if (scenario is not ("partial" or "unsupported"))
                        Assert.Equal(scenario == "zero" ? 0 : 1, counts.GetProperty("enumerated").GetInt32());
                    if (scenario is "strong" or "weak" or "uncovered" or "zero")
                        foreach (var suite in report.RootElement.GetProperty("suites").EnumerateArray())
                        {
                            Assert.Equal("GREEN", suite.GetProperty("baseline").GetString());
                            var accounting = suite.GetProperty("accounting");
                            Assert.Equal(report.RootElement.GetProperty("runId").GetString(), accounting.GetProperty("runId").GetString());
                            Assert.True(accounting.GetProperty("collectedFresh").GetBoolean());
                            Assert.Equal(64, accounting.GetProperty("coverageReportSha256").GetString()!.Length);
                            Assert.True(accounting.GetProperty("coverageReportLength").GetInt64() > 0);
                            Assert.NotEmpty(accounting.GetProperty("accountedMembers").EnumerateArray());
                        }
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
