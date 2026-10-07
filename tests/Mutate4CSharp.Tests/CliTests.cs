namespace Mutate4CSharp.Tests;

public sealed class CliTests
{
    [Fact]
    public void ParsesAllExecutionFlags()
    {
        var (options, error) = Cli.Parse(["a.cs", "--lines", "2,4", "--max-workers", "3", "--timeout-factor", "2.5", "--mutation-warning", "9", "--verbose", "--project", "p.csproj", "--test-project", "t.csproj", "--root", "root"]);
        Assert.Null(error); Assert.NotNull(options);
        Assert.Equal([2, 4], options.Lines!.Order());
        Assert.Equal(3, options.MaxWorkers); Assert.Equal(2.5, options.TimeoutFactor);
        Assert.Equal(9, options.MutationWarning); Assert.True(options.Verbose);
    }

    [Theory]
    [InlineData("--scan", "--update-manifest")]
    [InlineData("--scan", "--reuse-coverage")]
    [InlineData("--lines", "--since-last-run")]
    [InlineData("--since-last-run", "--mutate-all")]
    public void RejectsConflicts(string first, string second)
    {
        var args = first == "--lines" ? new[] { "a.cs", first, "1", second } : new[] { "a.cs", first, second };
        Assert.NotNull(Cli.Parse(args).Error);
    }

    [Fact] public void HelpNeedsNoTarget() => Assert.True(Cli.Parse(["--help"]).Options!.Help);
    [Fact] public void DefaultWorkerCountIsBoundedToOne() => Assert.Equal(1, Cli.Parse(["a.cs"]).Options!.MaxWorkers);

    [Fact]
    public void ParsesBoundExactIdDiagnosticRerun()
    {
        var mutationId = "mutation:v1:" + new string('a', 64);
        var fingerprint = "sha256:" + new string('b', 64);
        var parsed = Cli.Parse(["check", "--input", "src/A.cs", "--mutation-id", mutationId,
            "--plan-fingerprint", fingerprint]);

        Assert.Null(parsed.Error);
        Assert.Equal([mutationId], parsed.Options!.StrictCheck!.MutationIds);
        Assert.Equal(fingerprint, parsed.Options.StrictCheck.PlanFingerprint);
    }

    [Theory]
    [InlineData("--mutation-id")]
    [InlineData("--plan-fingerprint")]
    public void RejectsUnboundExactIdDiagnosticRerun(string option)
    {
        var value = option == "--mutation-id" ? "mutation:v1:" + new string('a', 64) :
            "sha256:" + new string('b', 64);
        Assert.NotNull(Cli.Parse(["check", "--input", "src/A.cs", option, value]).Error);
    }
}
