using System.Diagnostics;
using System.IO.Compression;
using System.Xml.Linq;

namespace Mutate4CSharp.Tests;

public sealed class IssueEightReviewRoundSixTests : IDisposable
{
    private static readonly string RepositoryRoot = Path.GetFullPath("../../../../../", AppContext.BaseDirectory);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mutate4csharp-round-six",
        Guid.NewGuid().ToString("N"));

    public IssueEightReviewRoundSixTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task PackRejectsCallerSuppliedRevisionThatIsNotRepositoryHead()
    {
        var source = Path.Combine(_root, "forged-source");
        await CopyCurrentRepositoryAsync(source);
        var forgedRevision = new string('1', 40);
        var result = await RunPackAsync(source,
            "src/Mutate4CSharp/Mutate4CSharp.csproj", "-c", "Release", "-m:1",
            $"-p:Version=0.1.0-local.{forgedRevision}", $"-p:SourceRevisionId={forgedRevision}",
            "-o", Path.Combine(_root, "forged-feed"));

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("repository HEAD", result.Diagnostic, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PackRejectsDirtyTrackedAndUntrackedSourceTrees()
    {
        var clone = Path.Combine(_root, "dirty-source");
        await CopyCurrentRepositoryAsync(clone);
        var revision = (await RunAsync(clone, "git", "rev-parse", "HEAD")).StandardOutput.Trim();
        File.AppendAllText(Path.Combine(clone, "README.md"), "\ntracked dirt\n");
        File.WriteAllText(Path.Combine(clone, "untracked-probe.txt"), "untracked dirt");

        var result = await RunPackAsync(clone,
            "src/Mutate4CSharp/Mutate4CSharp.csproj", "-c", "Release", "-m:1",
            $"-p:Version=0.1.0-local.{revision}", $"-p:SourceRevisionId={revision}",
            "-o", Path.Combine(_root, "dirty-feed"));

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("clean", result.Diagnostic, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PackRejectsAnOtherwiseCleanTreeWithOnlyAnUntrackedInput()
    {
        var source = Path.Combine(_root, "untracked-source");
        await CopyCurrentRepositoryAsync(source);
        var revision = (await RunAsync(source, "git", "rev-parse", "HEAD")).StandardOutput.Trim();
        File.WriteAllText(Path.Combine(source, "untracked-probe.txt"), "must not enter a package");

        var result = await RunPackAsync(source,
            "src/Mutate4CSharp/Mutate4CSharp.csproj", "-c", "Release", "-m:1",
            $"-p:Version=0.1.0-local.{revision}", $"-p:SourceRevisionId={revision}",
            "-o", Path.Combine(_root, "untracked-feed"));

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("clean", result.Diagnostic, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ShippedPrepareFailsClosedOnHostileAncestorBuildFiles()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The shipped workflow is a Bash integration.");
        var hostileParent = Path.Combine(_root, "hostile-parent");
        var source = Path.Combine(hostileParent, "source");
        var workspace = Path.Combine(hostileParent, "external-workspace");
        var receipt = Path.Combine(hostileParent, "receipt.tsv");
        await CopyCurrentRepositoryAsync(source);
        File.WriteAllText(Path.Combine(hostileParent, "Directory.Build.props"),
            "<Project><Target Name=\"HostileProps\" BeforeTargets=\"PrepareForBuild\"><Error Text=\"hostile parent props imported\" /></Target></Project>");
        File.WriteAllText(Path.Combine(hostileParent, "Directory.Build.targets"),
            "<Project><Target Name=\"HostileTargets\" BeforeTargets=\"Pack\"><Error Text=\"hostile parent targets imported\" /></Target></Project>");
        File.WriteAllText(Path.Combine(hostileParent, "Directory.Build.rsp"), "-p:Version=0.0.0-hostile");
        var ignoredBin = Path.Combine(source, "src", "Mutate4CSharp", "bin", "probe", "hostile-bin.txt");
        var ignoredObj = Path.Combine(source, "src", "Mutate4CSharp", "obj", "probe", "hostile-obj.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(ignoredBin)!);
        Directory.CreateDirectory(Path.GetDirectoryName(ignoredObj)!);
        File.WriteAllText(ignoredBin, "ignored bin debris");
        File.WriteAllText(ignoredObj, "ignored obj debris");
        Assert.Empty((await RunAsync(source, "git", "status", "--porcelain=v1",
            "--untracked-files=all")).StandardOutput);

        var prepare = await RunAsync(source, "bash", Path.Combine(source, "scripts", "agent-gate.sh"),
            "prepare", source, workspace, receipt);
        Assert.Equal(73, prepare.ExitCode);
        Assert.Contains("ancestor build-control input is not allowed", prepare.Diagnostic,
            StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(receipt));
    }

    [Fact]
    public void ShippedWorkflowIsLiteralAndReceiptIsNeverExecuted()
    {
        var scriptPath = Path.Combine(RepositoryRoot, "scripts", "agent-gate.sh");
        Assert.True(File.Exists(scriptPath), $"Missing shipped workflow: {scriptPath}");
        var script = File.ReadAllText(scriptPath);
        var docs = Read("docs/agent-workflow.md");

        Assert.Contains("EXPECTED_PACKAGE_SHA256", script, StringComparison.Ordinal);
        Assert.Contains("EXPECTED_PAYLOAD_SHA256", script, StringComparison.Ordinal);
        Assert.Contains("EXPECTED_TOOL_SOURCE_COMMIT", script, StringComparison.Ordinal);
        Assert.Contains("TASK_START", script, StringComparison.Ordinal);
        Assert.Contains("--locked-mode", script, StringComparison.Ordinal);
        Assert.Contains("--no-state", script, StringComparison.Ordinal);
        Assert.Contains("rev-parse HEAD", script, StringComparison.Ordinal);
        Assert.Contains("scripts/agent-gate.sh", docs, StringComparison.Ordinal);
        Assert.DoesNotContain("source \"$TOOL_RECEIPT\"", docs, StringComparison.Ordinal);
        Assert.DoesNotContain("local-tool.env", docs, StringComparison.Ordinal);
    }

    [Fact]
    public void ShippedWorkflowRejectsPayloadSymlinksAndHostileMsbuildDiscovery()
    {
        var script = Read("scripts/agent-gate.sh");
        Assert.Contains("-type l", script, StringComparison.Ordinal);
        Assert.Contains("DirectoryBuildPropsPath", script, StringComparison.Ordinal);
        Assert.Contains("DirectoryBuildTargetsPath", script, StringComparison.Ordinal);
        Assert.Contains("MSBuildProjectExtensionsPath", script, StringComparison.Ordinal);
        Assert.Contains("Directory.Build.rsp", script, StringComparison.Ordinal);
        Assert.DoesNotContain(" +    ", script, StringComparison.Ordinal);
    }

    [Fact]
    public void ExampleProjectsEnforceLockedRestoreWithoutCommandLineHelp()
    {
        foreach (var path in new[]
                 {
                     "examples/agent-gate/src/Example/Example.csproj",
                     "examples/agent-gate/tests/Example.Tests/Example.Tests.csproj"
                 })
        {
            var project = XDocument.Load(Path.Combine(RepositoryRoot, path));
            Assert.Contains(project.Descendants("RestoreLockedMode"),
                element => string.Equals(element.Value, "true", StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void ExtractedExamplePublicMappingCannotMatchTheToolCandidate()
    {
        var configuration = XDocument.Load(Path.Combine(RepositoryRoot, "examples", "agent-gate", "NuGet.Config"));
        var mappings = configuration.Descendants("packageSourceMapping").Elements("packageSource")
            .ToDictionary(element => (string)element.Attribute("key")!,
                element => element.Elements("package").Select(package => (string)package.Attribute("pattern")!).ToArray());

        Assert.Equal(["mutate4csharp"], mappings["mutate4csharp-local"]);
        Assert.DoesNotContain("*", mappings["nuget.org"]);
        Assert.DoesNotContain(mappings["nuget.org"],
            pattern => PatternMatches(pattern, "mutate4csharp"));
    }

    [Fact]
    public void EveryExplicitNonBuildPackageInputIsTrackedAndNotASymlink()
    {
        var project = XDocument.Load(Path.Combine(RepositoryRoot, "src", "Mutate4CSharp",
            "Mutate4CSharp.csproj"));
        var inputs = project.Descendants("None")
            .Select(element => (string?)element.Attribute("Include"))
            .Where(include => include?.StartsWith("../../", StringComparison.Ordinal) == true)
            .Select(include => include![6..].Replace('\\', '/'))
            .ToArray();

        Assert.NotEmpty(inputs);
        foreach (var input in inputs)
        {
            var tracked = GitOutput("ls-files", "--error-unmatch", "--", input);
            Assert.Equal(input, tracked.Trim());
            var mode = GitOutput("ls-files", "-s", "--", input).Split(' ', 2)[0];
            Assert.NotEqual("120000", mode);
        }
    }

    [Fact]
    public async Task ValidCleanHeadStillPacksAndHasDeterministicExplicitInventory()
    {
        var source = Path.Combine(_root, "valid-source");
        await CopyCurrentRepositoryAsync(source);
        var revision = (await RunAsync(source, "git", "rev-parse", "HEAD")).StandardOutput.Trim();
        var feed = Path.Combine(_root, "control-feed");
        var result = await RunPackAsync(source,
            "src/Mutate4CSharp/Mutate4CSharp.csproj", "-c", "Release", "-m:1",
            $"-p:Version=0.1.0-local.{revision}", $"-p:SourceRevisionId={revision}", "-o", feed);
        Assert.True(result.ExitCode == 0, result.Diagnostic);

        using var package = ZipFile.OpenRead(Path.Combine(feed, $"mutate4csharp.0.1.0-local.{revision}.nupkg"));
        Assert.DoesNotContain(package.Entries, entry =>
            entry.FullName.Contains("/bin/", StringComparison.OrdinalIgnoreCase) ||
            entry.FullName.Contains("/obj/", StringComparison.OrdinalIgnoreCase));
    }

    private static bool PatternMatches(string pattern, string packageId) =>
        pattern == "*" ||
        (pattern.EndsWith('*') && packageId.StartsWith(pattern[..^1], StringComparison.OrdinalIgnoreCase)) ||
        string.Equals(pattern, packageId, StringComparison.OrdinalIgnoreCase);

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

    private static string GitOutput(params string[] arguments)
    {
        var result = RunAsync(RepositoryRoot, "git", arguments).GetAwaiter().GetResult();
        Assert.True(result.ExitCode == 0, result.Diagnostic);
        return result.StandardOutput;
    }

    private static async Task CopyCurrentRepositoryAsync(string destination)
    {
        Directory.CreateDirectory(destination);
        var tracked = GitOutput("ls-files", "-z").Split('\0', StringSplitOptions.RemoveEmptyEntries);
        foreach (var relativePath in tracked)
        {
            var source = Path.Combine(RepositoryRoot, relativePath);
            var target = Path.Combine(destination, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(source, target);
        }
        Assert.Equal(0, (await RunAsync(destination, "git", "init", "--quiet")).ExitCode);
        Assert.Equal(0, (await RunAsync(destination, "git", "config", "user.email",
            "fixture@example.invalid")).ExitCode);
        Assert.Equal(0, (await RunAsync(destination, "git", "config", "user.name", "fixture")).ExitCode);
        Assert.Equal(0, (await RunAsync(destination, "git", "add", ".")).ExitCode);
        Assert.Equal(0, (await RunAsync(destination, "git", "commit", "--quiet", "-m", "fixture")).ExitCode);
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
        return new(process.ExitCode, await output, await error);
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
