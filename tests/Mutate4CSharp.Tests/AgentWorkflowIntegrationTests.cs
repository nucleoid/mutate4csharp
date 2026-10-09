namespace Mutate4CSharp.Tests;

public sealed class AgentWorkflowIntegrationTests
{
    private static readonly string RepositoryRoot = Path.GetFullPath("../../../../../", AppContext.BaseDirectory);

    [Fact]
    public void DocumentationUsesOnlySupportedToolGrammarAndExplainsFailClosedOutcomes()
    {
        var readme = Read("README.md");
        var workflow = Read("docs/agent-workflow.md");
        var matrix = Read("docs/supported-matrix.md");
        var localNuget = Read("NuGet.local.config");
        var shippedScript = Read("scripts/agent-gate.sh");
        var combined = string.Join('\n', readme, workflow, matrix);

        Assert.Contains("/external/pinned-tool/mutate4csharp check", combined, StringComparison.Ordinal);
        Assert.DoesNotContain("dotnet mutate4csharp", combined, StringComparison.Ordinal);
        Assert.Contains("INCOMPLETE", workflow, StringComparison.Ordinal);
        Assert.Contains("NOT_APPLICABLE", workflow, StringComparison.Ordinal);
        Assert.Contains("--input", workflow, StringComparison.Ordinal);
        Assert.Contains("legacy", workflow, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("never", workflow, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("NuGet.local.config", shippedScript, StringComparison.Ordinal);
        Assert.Contains("--configfile \"$config\" --no-cache", shippedScript, StringComparison.Ordinal);
        Assert.Contains("NUGET_PACKAGES", shippedScript, StringComparison.Ordinal);
        Assert.Contains("DOTNET_CLI_HOME", shippedScript, StringComparison.Ordinal);
        Assert.DoesNotContain("--add-source", combined, StringComparison.Ordinal);
        Assert.DoesNotContain("--ignore-failed-sources", combined, StringComparison.Ordinal);
        Assert.Contains("<clear />", localNuget, StringComparison.Ordinal);

        Assert.DoesNotContain("always exits `4`", combined, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("supported capture", readme, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("supported Git-backed", workflow, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("supported Git/configuration contexts", Cli.HelpText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("usage", Cli.HelpText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("snapshot refusal", Cli.HelpText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("`INCOMPLETE`", workflow, StringComparison.Ordinal);
        Assert.Contains("`EXECUTION_NOT_IMPLEMENTED`", workflow, StringComparison.Ordinal);
        Assert.Contains("until mutation execution is connected", workflow, StringComparison.Ordinal);
        Assert.Contains("\"schemaVersion\": \"1\"", workflow, StringComparison.Ordinal);
        Assert.Contains("\"incompleteConditions\"", workflow, StringComparison.Ordinal);
        Assert.Contains("\"code\": \"BASELINE_UNKNOWN\"", workflow, StringComparison.Ordinal);
        Assert.Contains("\"code\": \"UNIT_OMITTED\"", workflow, StringComparison.Ordinal);
        Assert.Contains("reducer-derived", workflow, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("exit `1` is a usage error", workflow, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("may not write a report", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("`INCOMPLETE` / exit `1`", workflow, StringComparison.Ordinal);

        Assert.Contains("net10.0", matrix, StringComparison.Ordinal);
        Assert.Contains("10.0.103", matrix, StringComparison.Ordinal);
        Assert.Contains("latestPatch", matrix, StringComparison.Ordinal);
        Assert.Contains("Windows", matrix, StringComparison.Ordinal);
        Assert.Contains("Linux", matrix, StringComparison.Ordinal);
        Assert.Contains("CI-configured", matrix, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("neither hosted OS is claimed successful until its CI run completes", matrix,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Windows CI currently", matrix, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not package creation or execution of the Bash orchestration script", matrix,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("invocation on Windows and Linux CI", matrix, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("VSTest", matrix, StringComparison.Ordinal);
        Assert.Contains("TRX", matrix, StringComparison.Ordinal);
        Assert.Contains("xUnit", matrix, StringComparison.Ordinal);
        Assert.Contains("Coverlet", matrix, StringComparison.Ordinal);
        Assert.Contains("OpenCover", matrix, StringComparison.Ordinal);
        Assert.Contains("packaged strict", matrix, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("legacy", matrix, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("does not run VSTest", matrix, StringComparison.Ordinal);
        Assert.DoesNotContain("SLA", matrix, StringComparison.Ordinal);

        Assert.Contains("5 not applicable", Cli.HelpText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CiRunsCoreTestsCrossPlatformAndUsesTheIsolatedPackWorkflowOnLinux()
    {
        var workflow = Read(".github/workflows/ci.yml");
        Assert.Contains("ubuntu-latest", workflow, StringComparison.Ordinal);
        Assert.Contains("windows-latest", workflow, StringComparison.Ordinal);
        Assert.Contains("local-tool-workflow", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("dotnet pack", workflow, StringComparison.Ordinal);
        Assert.Contains("scripts/agent-gate.sh prepare", workflow, StringComparison.Ordinal);
        Assert.Contains("if: runner.os == 'Linux'", workflow, StringComparison.Ordinal);
        Assert.Contains("ToolPackagingIntegrationTests", workflow, StringComparison.Ordinal);
        Assert.Contains("xUnit.MaxParallelThreads=1", workflow, StringComparison.Ordinal);
    }

    private static string Read(string relativePath) => File.ReadAllText(Path.Combine(RepositoryRoot, relativePath));
}
