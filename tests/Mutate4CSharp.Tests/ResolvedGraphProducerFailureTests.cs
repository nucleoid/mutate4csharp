namespace Mutate4CSharp.Tests;

public sealed class ResolvedGraphProducerFailureTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Public_failure_redacts_all_owned_roots_before_bounding_and_preserves_private_actual_return(bool slashSpelling)
    {
        var privateRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "private-graph-capture-" + new string('x', 180)));
        var source = Path.Combine(privateRoot, "source"); var packages = Path.Combine(privateRoot, "packages");
        string Spelling(string path) => slashSpelling ? path.Replace('\\', '/') : path;
        var returned = new ProcessTreeResult(1, false,
            "MSBUILD : Logger error MSB4017: " + Spelling(source) + "/Subject.cs; " + Spelling(packages) + "/Dependency.cs; " +
            Spelling(privateRoot) + "/csc/events.json; https://user:secret@example.invalid/path?token=hidden\n", "", TimeSpan.FromSeconds(1));
        var failure = ResolvedGraphCapture.CreateCompilerFailure(returned, source, packages, privateRoot);
        Assert.Contains("/_/mutate-source/Subject.cs", failure.Message);
        Assert.Contains("/_/mutate-packages/Dependency.cs", failure.Message);
        Assert.Contains("/_/mutate-private/csc/events.json", failure.Message);
        Assert.Contains("[source-uri]", failure.Message);
        Assert.DoesNotContain(privateRoot, failure.Message); Assert.DoesNotContain(Spelling(privateRoot), failure.Message);
        Assert.DoesNotContain("secret", failure.Message); Assert.DoesNotContain(new string('x', 20), failure.Message);
        Assert.True(failure.Message.Length <= EvaluationEvidence.MaxDiagnosticLength);
        Assert.Same(returned, failure.Data["ResolvedGraphCompiler.ActualReturn"]);
    }

    [Fact]
    public void Original_callback_cause_survives_long_command_preamble_and_shutdown_refusal()
    {
        var returned = new ProcessTreeResult(1, false,
            new string('p', 4096) + "\nMSBUILD : error MSB4017: Logger failed.\n" +
            " ---> Mutate4CSharp.EvaluationContractException: Original project event identity differs.\n" +
            " at logger callback\n",
            "MSBUILD : error MSB4017: Actual graph owner has entered terminal cleanup; no further work.\n",
            TimeSpan.FromSeconds(1));
        var failure = ResolvedGraphCapture.CreateCompilerFailure(returned);
        Assert.Contains("Original project event identity differs", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("terminal cleanup", failure.Message, StringComparison.Ordinal);
        Assert.True(failure.Message.Length <= EvaluationEvidence.MaxDiagnosticLength);
        Assert.Same(returned, failure.Data["ResolvedGraphCompiler.ActualReturn"]);
    }

    [Fact]
    public void Msbuild_plain_exception_header_preserves_callback_cause_without_an_inner_arrow()
    {
        var returned = new ProcessTreeResult(1, false,
            new string('p', 4096) + "\nMSBUILD : Logger error MSB1029: Callback failed before shutdown.\n" +
            "Mutate4CSharp.EvaluationContractException: Actual compiler path mapping differs.\n at callback\n",
            "", TimeSpan.FromSeconds(1));
        var failure = ResolvedGraphCapture.CreateCompilerFailure(returned);
        Assert.Contains("Actual compiler path mapping differs", failure.Message, StringComparison.Ordinal);
        Assert.Same(returned, failure.Data["ResolvedGraphCompiler.ActualReturn"]);
    }

    [Fact]
    public void Ordinary_compiler_error_is_bounded_and_withholds_source_credentials()
    {
        var returned = new ProcessTreeResult(1, false,
            new string('p', 4096) + "\nApp.cs(1): error CS0001: source https://user:secret@example.invalid/path?token=hidden\n" +
            "Unrelated trailing status\n", "", TimeSpan.FromSeconds(1));
        var failure = ResolvedGraphCapture.CreateCompilerFailure(returned);
        Assert.Contains("error CS0001", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("token=", failure.Message, StringComparison.Ordinal);
        Assert.Same(returned, failure.Data["ResolvedGraphCompiler.ActualReturn"]);
    }
}
