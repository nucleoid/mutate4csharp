using System.IO.Compression;
using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Diagnostics;

namespace Mutate4CSharp.Tests;

public sealed partial class ToolPackagingIntegrationTests
{
    [Fact(Timeout = 3_600_000)]
    public async Task InstalledStrictCliCertifiesRealOutcomesFromIndependentCleanConsumers()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Installed packaging certification uses the Linux Bash profile.");
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
        var installedPackages = Directory.EnumerateFiles(toolPath, "*.nupkg", SearchOption.AllDirectories).ToArray();
        Assert.NotEmpty(installedPackages);
        foreach (var installedPackage in installedPackages)
            Assert.Equal(SHA256.HashData(File.ReadAllBytes(packagePath)), SHA256.HashData(File.ReadAllBytes(installedPackage)));
        var payload = GateCompatiblePayloadHash(toolPath);

        foreach (var scenario in new[] { "strong", "weak", "uncovered", "red", "empty", "zero", "zero-allowed", "plan", "partial", "unsupported", "timeout", "cancel", "drift" })
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
            var cancelMarker = Path.Combine(_root, "cancel-ready");
            File.WriteAllText(tests, scenario switch
            {
                "weak" => "using Example; using Xunit; public class FlagTests { [Fact] public void Smoke() { _ = Flag.Value(); Assert.True(true); } }",
                "uncovered" => "using Example; using Xunit; public class FlagTests { [Fact] public void OtherMethod() => Assert.True(Flag.Other(true)); }",
                "red" => "using Example; using Xunit; public class FlagTests { [Fact] public void Red() => Assert.False(Flag.Value()); }",
                "empty" => "using Example; using Xunit; public class FlagTests { [Fact(Skip=\"Intentional empty executed baseline\")] public void Skipped() => Assert.True(Flag.Value()); }",
                "zero" or "zero-allowed" => "using Example; using Xunit; public class FlagTests { [Fact] public void Identity() => Assert.True(Flag.Value(true)); }",
                "timeout" => "using Example; using Xunit; public class FlagTests { [Fact] public void Slow() { System.Threading.Thread.Sleep(20_000); Assert.True(Flag.Value()); } }",
                "cancel" => "using Example; using Xunit; public class FlagTests { [Fact] public void WaitForSignal() { System.IO.File.WriteAllText(" + JsonSerializer.Serialize(cancelMarker) + ", \"ready\"); System.Threading.Thread.Sleep(300_000); Assert.True(Flag.Value()); } }",
                "drift" => "using Example; using Xunit; public class FlagTests { [Fact] public void ChangeOriginal() { System.IO.File.WriteAllText(" + JsonSerializer.Serialize(source) + ", \"changed original during frozen execution\"); Assert.True(Flag.Value()); } }",
                _ => "using Example; using Xunit; public class FlagTests { [Fact] public void Behavior() => Assert.True(Flag.Value()); }"
            });
            if (scenario is "timeout" or "partial" or "zero-allowed")
            {
                var policyPath = Path.Combine(consumer, "mutate4csharp.json");
                var configuration = JsonNode.Parse(File.ReadAllText(policyPath))!;
                if (scenario == "timeout") configuration["policy"]!["baselineTimeoutSeconds"] = 1;
                if (scenario == "partial") configuration["policy"]!["mutationCap"] = 1;
                if (scenario == "zero-allowed") configuration["policy"]!["allowNotApplicable"] = true;
                File.WriteAllText(policyPath, configuration.ToJsonString());
            }
            var zero = scenario is "zero" or "zero-allowed";
            File.WriteAllText(source, zero
                ? "namespace Example; public static class Flag { public static bool Value(bool input) { return input; } }"
                : "namespace Example;\npublic static class Flag\n{\n public static bool Other(bool input) => input;\n public static bool Value() => false;\n}\n");
            foreach (var arguments in new[] { new[] { "init", "--quiet" }, new[] { "config", "user.email", "fixture@example.invalid" },
                         new[] { "config", "user.name", "fixture" }, new[] { "add", "." }, new[] { "commit", "--quiet", "-m", "consumer baseline" } })
                Assert.Equal(0, (await RunAsync(consumer, "git", arguments)).ExitCode);
            var baseline = (await RunAsync(consumer, "git", "rev-parse", "HEAD")).StandardOutput.Trim();
            File.WriteAllText(source, zero
                ? "namespace Example; public static class Flag { public static bool Value(bool input) => input; }"
                : "namespace Example;\npublic static class Flag\n{\n public static bool Other(bool input) => input;\n public static bool Value() => true;\n}\n");
            if (scenario == "partial") File.WriteAllText(source, File.ReadAllText(source).Replace("Value() => true;", "Value() => true && true;"));
            if (scenario == "unsupported") File.WriteAllText(source, File.ReadAllText(source), Encoding.Unicode);
            Assert.Equal(0, (await RunAsync(consumer, "git", "add", ".")).ExitCode);
            Assert.Equal(0, (await RunAsync(consumer, "git", "commit", "--quiet", "-m", "changed implementation")).ExitCode);
            var bytes = File.ReadAllBytes(source);
            var index = (await RunAsync(consumer, "git", "diff", "--cached", "--binary")).StandardOutput;
            var status = (await RunAsync(consumer, "git", "status", "--porcelain=v1", "--untracked-files=all")).StandardOutput;
            Assert.Empty(status);
            var sourceState = ConsumerFiles(consumer);
            var indexFlags = (await RunAsync(consumer, "git", "ls-files", "--stage", "-v")).StandardOutput;
            var head = (await RunAsync(consumer, "git", "rev-parse", "HEAD")).StandardOutput.Trim();
            var tree = (await RunAsync(consumer, "git", "rev-parse", "HEAD^{tree}")).StandardOutput.Trim();
            JsonDocument? first = null;
            try
            {
                foreach (var noState in new[] { true, false })
                {
                    var reportPath = Path.Combine(_root, $"contract-{scenario}-{noState}.json");
                    var arguments = new List<string> { "check", "--base", baseline, "--report", reportPath };
                    if (noState) arguments.Add("--no-state");
                    if (scenario == "plan") arguments.Add("--plan");
                    File.Delete(cancelMarker);
                    var result = scenario == "cancel" ? await RunAndCancelAsync(consumer, command, cancelMarker, arguments.ToArray())
                        : await RunAsync(consumer, command, arguments.ToArray());
                    using var report = JsonDocument.Parse(File.ReadAllBytes(reportPath));
                    var outcome = scenario switch { "strong" => "PASS", "weak" or "uncovered" => "FAIL", "zero" or "zero-allowed" => "NOT_APPLICABLE", _ => "INCOMPLETE" };
                    var exit = scenario switch { "strong" or "zero-allowed" => 0, "weak" or "uncovered" => 3, "zero" => 5, "red" or "empty" => 2, _ => 4 };
                    Assert.True(result.ExitCode == exit, scenario + ": " + result.Diagnostic);
                    Assert.Equal(outcome, report.RootElement.GetProperty("outcome").GetString());
                    Assert.Contains("Run ID: " + report.RootElement.GetProperty("runId").GetString(), result.StandardOutput);
                    if (scenario != "plan") Assert.Contains("Strict evaluation " + outcome + ";", result.StandardOutput);
                    var counts = report.RootElement.GetProperty("counts");
                    Assert.Equal("2", report.RootElement.GetProperty("schemaVersion").GetString());
                    Assert.False(string.IsNullOrWhiteSpace(report.RootElement.GetProperty("runId").GetString()));
                    var units = report.RootElement.GetProperty("units").EnumerateArray().ToArray();
                    Assert.Equal(counts.GetProperty("selected").GetInt32(), units.Length);
                    Assert.Equal(units.Length, units.Select(unit => unit.GetProperty("evaluationUnitId").GetString()).Distinct().Count());
                    Assert.Equal(units.Length, counts.GetProperty("executed").GetInt32() + counts.GetProperty("freshUncovered").GetInt32() + counts.GetProperty("omitted").GetInt32());
                    if (scenario is not ("plan" or "partial" or "unsupported" or "cancel" or "drift"))
                        Assert.Equal(zero ? 0 : 1, counts.GetProperty("enumerated").GetInt32());
                    if (scenario is "strong" or "weak" or "uncovered" or "zero" or "zero-allowed")
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
                    var reasonCodes = report.RootElement.GetProperty("reasons").EnumerateArray()
                        .Concat(report.RootElement.GetProperty("incompleteConditions").EnumerateArray())
                        .Select(reason => reason.GetProperty("code").GetString()).ToArray();
                    var expectedReason = scenario switch { "red" => "BASELINE_RED", "empty" => "BASELINE_EMPTY", "plan" => "PLAN_ONLY",
                        "partial" => "BUDGET_OMITTED", "unsupported" => "ENUMERATION_ENCODING_UNSUPPORTED", "timeout" => "BASELINE_TIMEOUT",
                        "cancel" => "SNAPSHOT_CANCELLED", "drift" => "SNAPSHOT_DIVERGED", _ => null };
                    if (expectedReason is not null) Assert.Contains(expectedReason, reasonCodes);
                    if (scenario == "partial") Assert.True(counts.GetProperty("omitted").GetInt32() > 0);
                    if (scenario == "drift")
                    {
                        Assert.NotEqual(bytes, File.ReadAllBytes(source));
                        // Restore the deliberately changed fixture only after verifying drift refusal.
                        File.WriteAllBytes(source, bytes);
                    }
                    if (first is null) first = JsonDocument.Parse(File.ReadAllBytes(reportPath));
                    else Assert.Equal(first.RootElement.GetProperty("counts").GetRawText(), counts.GetRawText());
                    Assert.Equal(bytes, File.ReadAllBytes(source));
                    Assert.Equal(index, (await RunAsync(consumer, "git", "diff", "--cached", "--binary")).StandardOutput);
                    Assert.Equal(status, (await RunAsync(consumer, "git", "status", "--porcelain=v1", "--untracked-files=all")).StandardOutput);
                    Assert.Equal(payload, GateCompatiblePayloadHash(toolPath));
                    Assert.Equal(sourceState, ConsumerFiles(consumer));
                    Assert.Equal(indexFlags, (await RunAsync(consumer, "git", "ls-files", "--stage", "-v")).StandardOutput);
                    Assert.Equal(head, (await RunAsync(consumer, "git", "rev-parse", "HEAD")).StandardOutput.Trim());
                    if (noState) Assert.False(Directory.Exists(Path.Combine(consumer, ".mutate4csharp")));
                    else AssertDocumentedSidecars(consumer);
                    SaveCertification(scenario, noState, baseline, head, tree, sourceState, packagePath,
                        payload, indexFlags, status, arguments, result.ExitCode, File.ReadAllBytes(reportPath), report.RootElement);
                }
                var proof = Path.Combine(consumer, ".mutate4csharp", "proven");
                Assert.Equal(scenario == "strong", Directory.Exists(proof) && Directory.EnumerateFiles(proof, "*.json").Any());
            }
            finally { first?.Dispose(); }
        }
    }

    private async Task<ProcessResult> RunAndCancelAsync(string consumer, string command, string marker, string[] arguments)
    {
        var info = new ProcessStartInfo(command) { WorkingDirectory = consumer, RedirectStandardOutput = true,
            RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(8));
            while (!File.Exists(marker) && !process.HasExited) await Task.Delay(100, deadline.Token);
            Assert.True(File.Exists(marker), "Installed cancellation fixture never reached real baseline execution.");
            Assert.Equal(0, (await RunAsync(_root, "/usr/bin/kill", "-TERM", process.Id.ToString())).ExitCode);
            await process.WaitForExitAsync(deadline.Token);
            return new(process.ExitCode, await output, await error);
        }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
        }
    }

    private static void AssertDocumentedSidecars(string consumer)
    {
        var state = Path.Combine(consumer, ".mutate4csharp");
        if (!Directory.Exists(state)) return;
        Assert.Equal("*\n", File.ReadAllText(Path.Combine(state, ".gitignore")));
        foreach (var path in Directory.EnumerateFileSystemEntries(state, "*", SearchOption.AllDirectories))
        {
            var parts = Path.GetRelativePath(state, path).Replace('\\', '/').Split('/');
            if (parts[0] == ".gitignore") continue;
            Assert.Contains(parts[0], new[] { "discovery", "proven", "locks", "reports" });
            Assert.True(parts.Length <= 2);
            if (File.Exists(path)) Assert.EndsWith(parts[0] == "locks" ? ".lock" : ".json", path);
        }
    }

    private static string GateCompatiblePayloadHash(string root)
    {
        if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            hash.AppendData(Encoding.UTF8.GetBytes("./" + Path.GetRelativePath(root, path).Replace('\\', '/')));
            hash.AppendData([0]);
            hash.AppendData(Encoding.ASCII.GetBytes(Convert.ToString((int)File.GetUnixFileMode(path), 8) + "\n"));
            hash.AppendData(Encoding.ASCII.GetBytes(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))) + "\n"));
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static string[] ConsumerFiles(string root) => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
        .Select(path => (Path: path, Relative: Path.GetRelativePath(root, path).Replace('\\', '/')))
        .Where(item => !item.Relative.StartsWith(".git/", StringComparison.Ordinal) &&
                       !item.Relative.StartsWith(".mutate4csharp/", StringComparison.Ordinal))
        .Select(item => item.Relative + ":" + Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(item.Path))))
        .Order(StringComparer.Ordinal).ToArray();

    private static void SaveCertification(string scenario, bool noState, string baseline, string head,
        string tree, string[] sourceState, string package, string payload, string indexFlags, string status, List<string> arguments,
        int exit, byte[] reportBytes, JsonElement report)
    {
        var directory = Environment.GetEnvironmentVariable("MUTATE4CSHARP_CERTIFICATION_DIRECTORY");
        if (string.IsNullOrEmpty(directory)) return;
        Directory.CreateDirectory(directory);
        // This is a sanitized test observation, never a replacement tool report or approved quality baseline.
        var observation = new
        {
            schema = "mutate4csharp-installed-certification-v1", fixture = true, scenario,
            sourceCommit = SourceCommit, toolVersion = PackageVersion + "+" + SourceCommit,
            packageSha256 = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(package))),
            payloadSha256 = payload, baseline, head, tree, noState, exit,
            argv = arguments.Select((value, index) => index > 0 && arguments[index - 1] == "--report" ? "<external-report>" : value).ToArray(),
            sourceFiles = sourceState, sourceStateUnchanged = scenario != "drift", indexStatusHeadUnchanged = scenario != "drift",
            fixtureRestoredOriginalAfterDriftRefusal = scenario == "drift",
            sourceStateDigest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", sourceState)))),
            indexDigest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(indexFlags))),
            statusDigest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(status))),
            originalReportSha256 = Convert.ToHexStringLower(SHA256.HashData(reportBytes)), originalReportLength = reportBytes.Length,
            runId = report.GetProperty("runId").GetString(), outcome = report.GetProperty("outcome").GetString(),
            counts = report.GetProperty("counts"), policy = report.GetProperty("policy"),
            suites = report.GetProperty("suites").EnumerateArray().Select(suite => new
            { suiteId = suite.GetProperty("suiteId").GetString(), baseline = suite.GetProperty("baseline").GetString(),
              accounting = suite.TryGetProperty("accounting", out var accounting) ? (JsonElement?)accounting : null }).ToArray(),
            units = report.GetProperty("units").EnumerateArray().Select(unit => new
            { unitId = unit.GetProperty("unitId").GetString(), evaluationUnitId = unit.GetProperty("evaluationUnitId").GetString(), disposition = unit.GetProperty("disposition").GetString() }).ToArray(),
            reasonCodes = report.GetProperty("reasons").EnumerateArray().Select(reason => reason.GetProperty("code").GetString()).ToArray(),
            incompleteCodes = report.GetProperty("incompleteConditions").EnumerateArray().Select(reason => reason.GetProperty("code").GetString()).ToArray(),
            limitations = new[] { "Repository-owned fixture; no approved production baseline", "Linux installed workflow; Windows Bash gate uncertified", "Pinned VSTest/xUnit/Coverlet profile" }
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(observation, new JsonSerializerOptions { WriteIndented = true });
        var path = Path.Combine(directory, scenario + (noState ? "-no-state" : "-default-state") + ".json");
        File.WriteAllBytes(path, bytes);
        File.WriteAllText(path + ".sha256", Convert.ToHexStringLower(SHA256.HashData(bytes)) + "\n");
    }
}
