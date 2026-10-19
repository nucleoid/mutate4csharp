namespace Mutate4CSharp;

// Admission BEFORE the selected compiler parser. Actual primary e34a38d2:
// FlattenArgs opens @response; rulesets are read before the main option loop;
// wildcard/recurse expands filesystem entries; lib/load paths query directories.
// A refusal here preserves the missing closure; it is never a dropped option.
internal static class ResolvedGraphTokenPreflight
{
    private static readonly string[] EffectfulOptions = ["ruleset", "recurse", "lib", "loadpath"];

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