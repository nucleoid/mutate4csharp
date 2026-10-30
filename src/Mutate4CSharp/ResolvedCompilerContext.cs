using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Mutate4CSharp;

// Process-local semantic objects, never a report/JSON substitute for a compiler.
// Only the selected same-source worker constructs these from actual captured
// token, source, reference, generated-output and compiler-image inventories.
internal sealed record ResolvedCompilerContext(string Framework, string Configuration,
    IReadOnlyList<string> SourcePaths, IReadOnlySet<string> ExcludedPaths,
    IReadOnlyDictionary<string, string> Sources, IReadOnlyDictionary<string, SyntaxTree> Trees,
    CSharpCompilation Compilation, string Identity);
