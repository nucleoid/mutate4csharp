using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Mutate4CSharp;

internal static class SourceAnalyzer
{
    public static AnalysisResult Analyze(string file, string? projectDirectory = null)
    {
        var original = File.ReadAllText(file);
        var source = ManifestStore.Strip(original);
        var tree = CSharpSyntaxTree.ParseText(source, path: Path.GetFullPath(file));
        var root = tree.GetRoot();
        var compilation = CreateCompilation(tree, projectDirectory);
        var model = compilation.GetSemanticModel(tree, ignoreAccessibility: true);
        var scopes = DiscoverScopes(root);
        var sites = DiscoverSitesCore(root, tree, model, scopes);
        return new(source, original, tree, scopes, sites);
    }

    internal static IReadOnlyList<MutationSite> DiscoverStrictSites(SyntaxTree tree,
        SemanticModel model)
    {
        ArgumentNullException.ThrowIfNull(tree);
        ArgumentNullException.ThrowIfNull(model);
        if (!ReferenceEquals(model.SyntaxTree, tree))
            throw new EvaluationContractException("Strict semantic model does not belong to the source tree.");
        var root = tree.GetRoot();
        return DiscoverSitesCore(root, tree, model, DiscoverScopes(root));
    }

    private static CSharpCompilation CreateCompilation(SyntaxTree target, string? projectDirectory)
    {
        var trees = new List<SyntaxTree> { target };
        if (projectDirectory is not null && Directory.Exists(projectDirectory))
        {
            foreach (var file in ProjectLocator.EnumerateFilesSafe(projectDirectory, "*.cs")
                         .Where(x => !Path.GetFullPath(x).Equals(target.FilePath, StringComparison.Ordinal)))
            {
                try { trees.Add(CSharpSyntaxTree.ParseText(ManifestStore.Strip(File.ReadAllText(file)), path: file)); }
                catch (IOException) { }
            }
        }
        var tpa = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES");
        var refs = (tpa ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(x => MetadataReference.CreateFromFile(x));
        return CSharpCompilation.Create("MutationAnalysis", trees, refs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
    }

    private static IReadOnlyList<ScopeInfo> DiscoverScopes(SyntaxNode root)
    {
        var nodes = root.DescendantNodes().Where(IsScopeNode).ToList();
        if (nodes.Count == 0) nodes.Add(root);
        var duplicate = new Dictionary<string, int>(StringComparer.Ordinal);
        var result = new List<ScopeInfo>();
        foreach (var node in nodes.OrderBy(n => n.SpanStart))
        {
            var rawId = ScopeId(node);
            duplicate.TryGetValue(rawId, out var ordinal); duplicate[rawId] = ordinal + 1;
            var id = ordinal == 0 ? rawId : $"{rawId}#{ordinal + 1}";
            var span = node.SyntaxTree.GetLineSpan(node.Span);
            result.Add(new(id, node.Kind().ToString(), span.StartLinePosition.Line + 1,
                span.EndLinePosition.Line + 1, Hash(node.NormalizeWhitespace().ToFullString()), node.SpanStart, node.Span.End));
        }
        return result;
    }

    private static bool IsScopeNode(SyntaxNode n) => n is BaseMethodDeclarationSyntax or
        AccessorDeclarationSyntax or PropertyDeclarationSyntax or IndexerDeclarationSyntax or
        FieldDeclarationSyntax or EventFieldDeclarationSyntax or GlobalStatementSyntax;

    private static string ScopeId(SyntaxNode node)
    {
        var type = string.Join('.', node.Ancestors().OfType<BaseTypeDeclarationSyntax>().Reverse().Select(x => x.Identifier.ValueText));
        var prefix = string.IsNullOrEmpty(type) ? "<global>" : type;
        return node switch
        {
            MethodDeclarationSyntax m => $"{prefix}.method:{m.Identifier.ValueText}({string.Join(',', m.ParameterList.Parameters.Select(p => p.Type?.ToString()))})",
            ConstructorDeclarationSyntax c => $"{prefix}.ctor({string.Join(',', c.ParameterList.Parameters.Select(p => p.Type?.ToString()))})",
            DestructorDeclarationSyntax => $"{prefix}.dtor",
            OperatorDeclarationSyntax o => $"{prefix}.operator:{o.OperatorToken.Text}({o.ParameterList.Parameters.Count})",
            ConversionOperatorDeclarationSyntax c => $"{prefix}.conversion:{c.Type}({c.ParameterList.Parameters.Count})",
            AccessorDeclarationSyntax a => $"{prefix}.{AccessorOwner(a)}.{a.Keyword.Text}",
            PropertyDeclarationSyntax p => $"{prefix}.property:{p.Identifier.ValueText}",
            IndexerDeclarationSyntax => $"{prefix}.indexer",
            FieldDeclarationSyntax f => $"{prefix}.field:{string.Join(',', f.Declaration.Variables.Select(v => v.Identifier.ValueText))}",
            EventFieldDeclarationSyntax f => $"{prefix}.event:{string.Join(',', f.Declaration.Variables.Select(v => v.Identifier.ValueText))}",
            GlobalStatementSyntax g => $"<global>.statement:{g.SpanStart}",
            _ => $"{prefix}.{node.Kind()}"
        };
    }

    private static string AccessorOwner(AccessorDeclarationSyntax accessor) => accessor.Parent?.Parent switch
    {
        PropertyDeclarationSyntax p => $"property:{p.Identifier.ValueText}",
        IndexerDeclarationSyntax => "indexer",
        EventDeclarationSyntax e => $"event:{e.Identifier.ValueText}",
        _ => "accessor"
    };

    private static IReadOnlyList<MutationSite> DiscoverSitesCore(SyntaxNode root, SyntaxTree tree,
        SemanticModel model, IReadOnlyList<ScopeInfo> scopes)
    {
        var pending = new List<(TextSpan Span, string Replacement, string Description, string OperatorId)>();
        foreach (var literal in root.DescendantNodes().OfType<LiteralExpressionSyntax>())
        {
            if (literal.IsKind(SyntaxKind.TrueLiteralExpression)) pending.Add((literal.Span, "false", "replace true with false", "literal.boolean"));
            else if (literal.IsKind(SyntaxKind.FalseLiteralExpression)) pending.Add((literal.Span, "true", "replace false with true", "literal.boolean"));
            else if (TryIntegerReplacement(literal.Token, out var numericReplacement, out var numericDescription))
                pending.Add((literal.Span, numericReplacement, numericDescription, "literal.integer-boundary"));
        }
        foreach (var binary in root.DescendantNodes().OfType<BinaryExpressionSyntax>())
        {
            var replacement = binary.Kind() switch
            {
                SyntaxKind.EqualsExpression => "!=",
                SyntaxKind.NotEqualsExpression => "==",
                SyntaxKind.LessThanExpression => "<=",
                SyntaxKind.LessThanOrEqualExpression => "<",
                SyntaxKind.GreaterThanExpression => ">=",
                SyntaxKind.GreaterThanOrEqualExpression => ">",
                SyntaxKind.LogicalAndExpression => "||",
                SyntaxKind.LogicalOrExpression => "&&",
                SyntaxKind.SubtractExpression => "+",
                SyntaxKind.MultiplyExpression => "/",
                SyntaxKind.DivideExpression => "*",
                SyntaxKind.AddExpression when !IsStringConcat(binary, model) => "-",
                _ => null
            };
            if (replacement is not null) pending.Add((binary.OperatorToken.Span, replacement,
                $"replace {binary.OperatorToken.Text} with {replacement}", $"binary.{binary.Kind()}"));
        }
        foreach (var unary in root.DescendantNodes().OfType<PrefixUnaryExpressionSyntax>())
        {
            if (unary.IsKind(SyntaxKind.LogicalNotExpression) || unary.IsKind(SyntaxKind.UnaryMinusExpression))
                pending.Add((unary.OperatorToken.Span,
                    string.Empty, $"remove unary {unary.OperatorToken.Text}", $"unary.{unary.Kind()}"));
        }
        foreach (var expr in root.DescendantNodes().OfType<ExpressionSyntax>().Where(IsDirectRValue))
        {
            if (expr.IsKind(SyntaxKind.NullLiteralExpression) || expr is AnonymousFunctionExpressionSyntax) continue;
            var type = model.GetTypeInfo(expr).ConvertedType ?? model.GetTypeInfo(expr).Type;
            if (type is { IsReferenceType: true, TypeKind: not TypeKind.Error } && type.NullableAnnotation != NullableAnnotation.None)
                pending.Add((expr.Span, "null", $"replace rvalue '{Compact(expr)}' with null", "rvalue.null"));
        }
        var ordered = pending.Distinct().OrderBy(x => x.Span.Start).ThenBy(x => x.Span.Length).ToList();
        var sites = new List<MutationSite>();
        for (var i = 0; i < ordered.Count; i++)
        {
            var item = ordered[i];
            var line = tree.GetLineSpan(item.Span).StartLinePosition.Line + 1;
            var scope = scopes.Where(s => s.Start <= item.Span.Start && s.End >= item.Span.End).OrderBy(s => s.End - s.Start).FirstOrDefault()
                        ?? new ScopeInfo("<file>", "CompilationUnit", 1, int.MaxValue, Hash(root.NormalizeWhitespace().ToFullString()), 0, root.FullSpan.End);
            sites.Add(new(i + 1, item.Span.Start, item.Span.Length, line, item.Replacement,
                item.Description, scope.Id, scope.Hash, item.OperatorId,
                MutationIdentity.OperatorContractVersion));
        }
        return sites;
    }

    private static bool IsStringConcat(BinaryExpressionSyntax b, SemanticModel model)
    {
        var type = model.GetTypeInfo(b).ConvertedType ?? model.GetTypeInfo(b).Type;
        return type?.SpecialType == SpecialType.System_String;
    }

    private static bool TryIntegerReplacement(SyntaxToken token, out string replacement, out string description)
    {
        var value = token.Value switch
        {
            byte x => (ulong)x,
            sbyte x when x >= 0 => (ulong)x,
            short x when x >= 0 => (ulong)x,
            ushort x => (ulong)x,
            int x when x >= 0 => (ulong)x,
            uint x => x,
            long x when x >= 0 => (ulong)x,
            ulong x => x,
            _ => ulong.MaxValue
        };
        if (value is not (0 or 1)) { replacement = description = string.Empty; return false; }
        var text = token.Text;
        var suffixStart = text.Length;
        while (suffixStart > 0 && text[suffixStart - 1] is 'u' or 'U' or 'l' or 'L') suffixStart--;
        var suffix = text[suffixStart..];
        replacement = (value == 0 ? "1" : "0") + suffix;
        description = $"replace {value} with {1 - value}";
        return true;
    }

    private static bool IsDirectRValue(ExpressionSyntax e) => e.Parent switch
    {
        EqualsValueClauseSyntax x when x.Value == e => true,
        AssignmentExpressionSyntax x when x.Right == e => true,
        ReturnStatementSyntax x when x.Expression == e => true,
        ArrowExpressionClauseSyntax x when x.Expression == e => true,
        YieldStatementSyntax x when x.Expression == e => true,
        _ => false
    };

    private static string Compact(SyntaxNode node)
    {
        var value = node.ToString().ReplaceLineEndings(" ");
        return value.Length <= 30 ? value : value[..27] + "...";
    }

    internal static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
