namespace Mutate4CSharp.Tests;

public sealed class ResolvedGraphTokenPreflightTests
{
    private static readonly string[] SupportedShape = ["/noconfig", "/target:library", "/langversion:14.0",
        "/nullable:enable", "/reference:dependencyAlias=/sdk/reference.dll", "Source With Spaces.cs"];

    [Fact]
    public void Expanded_source_reference_alias_and_compiler_options_are_retained_without_invoking_parser() =>
        ResolvedGraphTokenPreflight.RequireClosedParserInputs(SupportedShape);

    [Theory]
    [InlineData("@actual.rsp")]
    [InlineData("/ruleset:actual.ruleset")]
    [InlineData("-RULESET:actual.ruleset")]
    [InlineData("/recurse:Sources.cs")]
    [InlineData("/lib:/unbound-directory")]
    [InlineData("/loadpath:/unbound-directory")]
    [InlineData("Sources/*.cs")]
    [InlineData("Sources/?.cs")]
    [InlineData("-")]
    public void Real_parser_effects_refuse_before_the_parser_can_acquire_unenrolled_inputs(string effect)
    {
        ResolvedGraphTokenPreflight.RequireClosedParserInputs(SupportedShape);
        Assert.Throws<EvaluationContractException>(() =>
            ResolvedGraphTokenPreflight.RequireClosedParserInputs(SupportedShape.Append(effect).ToArray()));
    }
}