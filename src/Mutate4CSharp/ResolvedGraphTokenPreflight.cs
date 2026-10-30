namespace Mutate4CSharp;

// Admission BEFORE the selected compiler parser. Actual primary e34a38d2:
// FlattenArgs opens @response; rulesets are read before the main option loop;
// wildcard/recurse expands filesystem entries; lib/load paths query directories.
// A refusal here preserves the missing closure; it is never a dropped option.
internal static class ResolvedGraphTokenPreflight
{
    private static readonly string[] EffectfulOptions = ["ruleset", "recurse", "lib", "loadpath"];

    internal static void RequireSupportedSourceEncoding(System.Text.Encoding? encoding)
    {
        // SourceText still detects a source BOM when no explicit code page was
        // selected. The snapshot writer preserves UTF8/UTF16 BOMs. An explicit
        // different decoder can produce matching text at the WRONG character
        // offset, so a later Original substring check alone is insufficient.
        if (encoding is not null && encoding.CodePage != System.Text.Encoding.UTF8.CodePage)
            throw new EvaluationContractException("Explicit non-UTF8 compiler source encoding is unsupported by the snapshot mutation writer.");
    }

    internal static void RequireSupportedSourceBytes(ReadOnlySpan<byte> bytes)
    {
        var (encoding, preamble) = SourceEncoding(bytes);
        try { _ = encoding.GetCharCount(bytes[preamble..]); }
        catch (System.Text.DecoderFallbackException) { throw InvalidSourceBytes(); }
    }

    internal static string DecodeSupportedSource(byte[] bytes)
    {
        var (encoding, preamble) = SourceEncoding(bytes);
        try { return encoding.GetString(bytes, preamble, bytes.Length - preamble); }
        catch (System.Text.DecoderFallbackException) { throw InvalidSourceBytes(); }
    }

    private static (System.Text.Encoding Encoding, int Preamble) SourceEncoding(ReadOnlySpan<byte> bytes)
    {
        // UTF32 LE begins with the UTF16 LE prefix. Both Roslyn and the default
        // reader detect UTF32, but the mutation writer only preserves UTF8/16.
        if (bytes.Length >= 4 &&
            (bytes[0] == 0xff && bytes[1] == 0xfe && bytes[2] == 0 && bytes[3] == 0 ||
             bytes[0] == 0 && bytes[1] == 0 && bytes[2] == 0xfe && bytes[3] == 0xff))
            throw new EvaluationContractException("UTF32 source BOM is unsupported by the snapshot mutation writer.");
        // Match the actual snapshot writer's throwing decoder, including the
        // no-BOM case. Do not assume a selected compiler's implicit fallback
        // decoder or replacement characters preserve the same source offsets.
        System.Text.Encoding encoding = new System.Text.UTF8Encoding(false, true);
        var preamble = 0;
        if (bytes.StartsWith(System.Text.Encoding.UTF8.GetPreamble())) preamble = 3;
        else if (bytes.StartsWith(System.Text.Encoding.Unicode.GetPreamble())) { encoding = new System.Text.UnicodeEncoding(false, false, true); preamble = 2; }
        else if (bytes.StartsWith(System.Text.Encoding.BigEndianUnicode.GetPreamble())) { encoding = new System.Text.UnicodeEncoding(true, false, true); preamble = 2; }
        return (encoding, preamble);
    }

    private static EvaluationContractException InvalidSourceBytes() => new(
        "Compiler source bytes are not valid in the snapshot writer's strict UTF8/UTF16 encoding profile.");

    internal static void RequireClosedParserInputs(IReadOnlyList<string> tokens)
    {
        if (tokens.Count is < 1 or > ResolvedGraphCapture.MaxItems)
            throw new SnapshotLimitException("Complete Csc token count limit.");
        long characters = 0;
        foreach (var token in tokens)
        {
            if (string.IsNullOrWhiteSpace(token) || token.Length > 65_536 ||
                (characters += token.Length) > ResolvedGraphCapture.MaxQueryBytes)
                throw new SnapshotLimitException("Complete Csc token character extent.");
            var arg = token.TrimEnd(); // Same response-prefix ordering as selected FlattenArgs.
            if (arg.StartsWith('@') || arg == "-" || arg.IndexOfAny(['*', '?']) >= 0)
                throw new EvaluationContractException("Unclosed response, stdin or wildcard compiler parser input.");
            if (arg[0] is not ('/' or '-')) continue;
            var colon = arg.IndexOf(':', 1);
            var name = colon < 0 ? arg[1..] : arg[1..colon];
            foreach (var effect in EffectfulOptions)
                if (name.Equals(effect, StringComparison.InvariantCultureIgnoreCase))
                    throw new EvaluationContractException("Compiler parser effect requires its complete admitted original input closure: " + effect);
        }
    }
}
