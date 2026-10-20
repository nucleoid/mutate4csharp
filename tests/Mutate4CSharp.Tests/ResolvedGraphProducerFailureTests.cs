namespace Mutate4CSharp.Tests;

public sealed class ResolvedGraphProducerFailureTests
{
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
