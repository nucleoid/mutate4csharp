using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace Mutate4CSharp.Tests;

public sealed class ResolvedGraphEncodingProfileTests
{
    private const string Source = "// éééééé\npublic static class Subject { public static bool Value() => true||true; }";

    [Fact]
    public void Actual_ascii_decoder_can_pass_original_substring_at_the_wrong_token_and_is_refused()
    {
        var parsed = Parse("20127"); // Built-in ASCII; no optional code-page provider.
        Assert.DoesNotContain(parsed.Errors, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.Equal(Encoding.ASCII.CodePage, parsed.Encoding!.CodePage);
        using var bytes = new MemoryStream(Encoding.UTF8.GetBytes(Source), writable: false);
        var actualText = SourceText.From(bytes, parsed.Encoding, parsed.ChecksumAlgorithm);
        var actualTree = CSharpSyntaxTree.ParseText(actualText, parsed.ParseOptions);
        var actualFirst = actualTree.GetRoot().DescendantTokens().First(token => token.IsKind(SyntaxKind.TrueKeyword));
        var writerFirst = Source.IndexOf("true", StringComparison.Ordinal);
        Assert.Equal(writerFirst + 6, actualFirst.SpanStart);
        Assert.Equal(Source.IndexOf("true", writerFirst + 4, StringComparison.Ordinal), actualFirst.SpanStart);
        Assert.Equal("true", Source.Substring(actualFirst.SpanStart, actualFirst.Span.Length));
        Assert.NotEqual(writerFirst, actualFirst.SpanStart);
        Assert.Throws<EvaluationContractException>(() =>
            ResolvedGraphTokenPreflight.RequireSupportedSourceEncoding(parsed.Encoding));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Default_and_explicit_utf8_preserve_actual_source_offsets(bool explicitUtf8)
    {
        var parsed = Parse(explicitUtf8 ? "65001" : null);
        Assert.DoesNotContain(parsed.Errors, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        ResolvedGraphTokenPreflight.RequireSupportedSourceEncoding(parsed.Encoding);
        ResolvedGraphTokenPreflight.RequireSupportedSourceBytes(Encoding.UTF8.GetBytes(Source));
        Assert.Equal(Source, ResolvedGraphTokenPreflight.DecodeSupportedSource(Encoding.UTF8.GetBytes(Source)));
        using var bytes = new MemoryStream(Encoding.UTF8.GetBytes(Source), writable: false);
        var text = SourceText.From(bytes, parsed.Encoding, parsed.ChecksumAlgorithm);
        Assert.Equal(Source, text.ToString());
        Assert.Equal(Source.IndexOf("true", StringComparison.Ordinal),
            CSharpSyntaxTree.ParseText(text, parsed.ParseOptions).GetRoot().DescendantTokens()
                .First(token => token.IsKind(SyntaxKind.TrueKeyword)).SpanStart);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Implicit_utf16_bom_still_overrides_default_decoder_without_changing_character_offsets(bool bigEndian)
    {
        var parsed = Parse(null);
        ResolvedGraphTokenPreflight.RequireSupportedSourceEncoding(parsed.Encoding);
        var encoding = new UnicodeEncoding(bigEndian, byteOrderMark: true, throwOnInvalidBytes: true);
        var complete = encoding.GetPreamble().Concat(encoding.GetBytes(Source)).ToArray();
        ResolvedGraphTokenPreflight.RequireSupportedSourceBytes(complete);
        Assert.Equal(Source, ResolvedGraphTokenPreflight.DecodeSupportedSource(complete));
        using var bytes = new MemoryStream(complete, writable: false);
        var actual = SourceText.From(bytes, parsed.Encoding, parsed.ChecksumAlgorithm);
        Assert.Equal(Source, actual.ToString());
        Assert.Equal(encoding.CodePage, actual.Encoding!.CodePage);
        Assert.Equal(Source.IndexOf("true", StringComparison.Ordinal),
            CSharpSyntaxTree.ParseText(actual, parsed.ParseOptions).GetRoot().DescendantTokens()
                .First(token => token.IsKind(SyntaxKind.TrueKeyword)).SpanStart);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Implicit_utf32_bom_is_refused_before_a_different_writer_decoder_can_apply_offsets(bool bigEndian)
    {
        var parsed = Parse(null);
        ResolvedGraphTokenPreflight.RequireSupportedSourceEncoding(parsed.Encoding);
        var encoding = new UTF32Encoding(bigEndian, byteOrderMark: true, throwOnInvalidCharacters: true);
        var complete = encoding.GetPreamble().Concat(encoding.GetBytes(Source)).ToArray();
        using var bytes = new MemoryStream(complete, writable: false);
        // Same default reader used by parent span validation detects UTF32.
        // Refuse before relying on a writer that only preserves UTF8/UTF16.
        using var reader = new StreamReader(bytes, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        Assert.Equal(Source, reader.ReadToEnd());
        Assert.Throws<EvaluationContractException>(() =>
            ResolvedGraphTokenPreflight.RequireSupportedSourceBytes(complete));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Invalid_utf8_is_refused_by_both_byte_admission_and_parent_decoder(bool bom)
    {
        var valid = Encoding.UTF8.GetBytes(Source);
        if (bom) valid = Encoding.UTF8.GetPreamble().Concat(valid).ToArray();
        ResolvedGraphTokenPreflight.RequireSupportedSourceBytes(valid);
        Assert.Equal(Source, ResolvedGraphTokenPreflight.DecodeSupportedSource(valid));
        // Valid multibyte UTF8 followed by an isolated legacy-codepage byte:
        // neither replacement text nor an implicit fallback is writer-compatible.
        var malformed = valid.Concat(new byte[] { 0xe9 }).ToArray();
        Assert.Throws<EvaluationContractException>(() => ResolvedGraphTokenPreflight.RequireSupportedSourceBytes(malformed));
        Assert.Throws<EvaluationContractException>(() => ResolvedGraphTokenPreflight.DecodeSupportedSource(malformed));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Malformed_utf16_is_refused_by_both_byte_admission_and_parent_decoder(bool bigEndian)
    {
        var encoding = new UnicodeEncoding(bigEndian, true, true);
        var valid = encoding.GetPreamble().Concat(encoding.GetBytes(Source)).ToArray();
        Assert.Equal(Source, ResolvedGraphTokenPreflight.DecodeSupportedSource(valid));
        var malformed = valid.Concat(new byte[] { 0x61 }).ToArray();
        Assert.Throws<EvaluationContractException>(() => ResolvedGraphTokenPreflight.RequireSupportedSourceBytes(malformed));
        Assert.Throws<EvaluationContractException>(() => ResolvedGraphTokenPreflight.DecodeSupportedSource(malformed));
    }

    private static CSharpCommandLineArguments Parse(string? codePage)
    {
        var tokens = new List<string> { "/target:library", "/langversion:14.0", "Subject.cs" };
        if (codePage is not null) tokens.Insert(0, "/codepage:" + codePage);
        ResolvedGraphTokenPreflight.RequireClosedParserInputs(tokens);
        return CSharpCommandLineParser.Default.Parse(tokens, AppContext.BaseDirectory, sdkDirectory: null);
    }
}
