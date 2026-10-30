#if MUTATE_RESOLVED_GRAPH_WORKER
using Microsoft.CodeAnalysis;

namespace Mutate4CSharp;

// Uses the selected compiler's public analyzer-config implementation. No analyzer
// is loaded or executed here: the original producer owns generator/analyzer work.
internal sealed class ResolvedGraphTreeOptions : SyntaxTreeOptionsProvider
{
    private readonly ResolvedGraphInputs _inputs;
    private readonly Dictionary<SyntaxTree, AnalyzerConfigOptionsResult> _rows =
        new(ReferenceEqualityComparer.Instance);
    private readonly AnalyzerConfigOptionsResult _global;

    internal ResolvedGraphTreeOptions(ResolvedGraphInputs inputs,
        IReadOnlyList<(string Path, string Text)> configurations, IEnumerable<SyntaxTree> trees)
    {
        _inputs = inputs;
        if (configurations.Count > 4096)
            throw new SnapshotLimitException("Complete analyzer-config count exceeds its finite profile.");
        var parsed = new List<AnalyzerConfig>(configurations.Count);
        var names = new HashSet<string>(OperatingSystem.IsWindows() ?
            StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var file in configurations)
        {
            inputs.CheckWork();
            if (!Path.IsPathFullyQualified(file.Path) || !names.Add(file.Path))
                throw new EvaluationContractException("Distinct actual analyzer-config paths required.");
            parsed.Add(AnalyzerConfig.Parse(file.Text, file.Path));
            inputs.CheckWork();
        }
        inputs.CheckWork();
        var set = AnalyzerConfigSet.Create(parsed, out var diagnostics);
        inputs.CheckWork();
        if (diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error))
            throw new EvaluationContractException("Actual analyzer-config set has compiler errors.");
        _global = set.GlobalConfigOptions;
        foreach (var tree in trees)
        {
            inputs.CheckWork();
            var options = set.GetOptionsForSourcePath(tree.FilePath);
            inputs.CheckWork();
            if (options.Diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error) || !_rows.TryAdd(tree, options))
                throw new EvaluationContractException("Invalid or duplicate actual analyzer-config tree binding.");
        }
    }

    private AnalyzerConfigOptionsResult Row(SyntaxTree tree, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        _inputs.CheckWork();
        return _rows.TryGetValue(tree, out var row) ? row :
            throw new EvaluationContractException("Foreign tree cannot obtain captured analyzer options.");
    }

    public override GeneratedKind IsGenerated(SyntaxTree tree, CancellationToken cancellationToken)
    {
        var options = Row(tree, cancellationToken);
        return options.AnalyzerOptions.TryGetValue("generated_code", out var text) && bool.TryParse(text, out var value)
            ? value ? GeneratedKind.MarkedGenerated : GeneratedKind.NotGenerated : GeneratedKind.Unknown;
    }

    public override bool TryGetDiagnosticValue(SyntaxTree tree, string diagnosticId,
        CancellationToken cancellationToken, out ReportDiagnostic severity) =>
        Row(tree, cancellationToken).TreeOptions.TryGetValue(diagnosticId, out severity);

    public override bool TryGetGlobalDiagnosticValue(string diagnosticId,
        CancellationToken cancellationToken, out ReportDiagnostic severity)
    {
        cancellationToken.ThrowIfCancellationRequested(); _inputs.CheckWork();
        return _global.TreeOptions.TryGetValue(diagnosticId, out severity);
    }
}
#endif