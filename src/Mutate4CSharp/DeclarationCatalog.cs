using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Mutate4CSharp;

internal sealed record DeclarationComparison(
    IReadOnlyList<DeclarationScope> Declarations,
    IReadOnlyList<DeclarationScope> Removals,
    bool RequiresProjectExpansion,
    IReadOnlyList<EvaluationReason> Reasons);

internal static class DeclarationCatalog
{
    private sealed record Entry(string Id, string Kind, string DisplayName, int StartLine, int EndLine,
        string Hash, bool Partial, TextSpan Span);
    private sealed record Catalog(IReadOnlyDictionary<string, Entry> Entries, string Skeleton,
        bool HasErrors);

    public static DeclarationComparison Compare(string path, byte[]? beforeBytes, byte[]? currentBytes)
    {
        var before = beforeBytes is null ? null : Create(path, Decode(beforeBytes));
        var current = currentBytes is null ? null : Create(path, Decode(currentBytes));
        var selected = new List<DeclarationScope>();
        var removals = new List<DeclarationScope>();
        var reasons = new List<EvaluationReason>();
        foreach (var entry in (current?.Entries.Values ?? []).OrderBy(item => item.Id, StringComparer.Ordinal))
        {
            Entry? old = null;
            if (before?.Entries.TryGetValue(entry.Id, out old) == true && old.Hash == entry.Hash) continue;
            selected.Add(ToScope(entry, old is null ? "DECLARATION_ADDED" : "DECLARATION_CHANGED"));
        }
        foreach (var entry in (before?.Entries.Values ?? []).OrderBy(item => item.Id, StringComparer.Ordinal))
        {
            if (current?.Entries.ContainsKey(entry.Id) == true) continue;
            removals.Add(ToScope(entry, "DECLARATION_REMOVED"));
        }

        var expansion = false;
        if ((before is null) != (current is null))
        {
            expansion = true;
            reasons.Add(new(current is null ? "COMPILE_ITEM_REMOVED" : "COMPILE_ITEM_ADDED",
                $"{path} was {(current is null ? "removed from" : "added to")} the captured compile inventory; expand its configured project."));
        }
        if (before is not null && current is not null && before.Skeleton != current.Skeleton)
        {
            expansion = true;
            reasons.Add(new("SHARED_DECLARATION_CHANGED",
                $"{path} changed outside a supported executable declaration; expand to its configured project."));
        }
        if (selected.Any(scope => current?.Entries[scope.Id].Partial == true) ||
            removals.Any(scope => before?.Entries[scope.Id].Partial == true))
        {
            expansion = true;
            reasons.Add(new("PARTIAL_TYPE_CHANGED",
                $"{path} changes a partial type; sibling parts and callers remain risk-bearing."));
        }
        if (before?.HasErrors == true || current?.HasErrors == true)
        {
            expansion = true;
            reasons.Add(new("UNSUPPORTED_SYNTAX",
                $"{path} contains parse errors; declaration scope cannot be proven complete."));
        }
        if (selected.Count == 0 && removals.Count == 0 && beforeBytes is not null && currentBytes is not null &&
            SyntaxFingerprint(Decode(beforeBytes)) != SyntaxFingerprint(Decode(currentBytes)))
        {
            expansion = true;
            if (reasons.All(reason => reason.Code != "SHARED_DECLARATION_CHANGED"))
                reasons.Add(new("NO_SUPPORTED_DECLARATION",
                    $"{path} changed without an applicable supported declaration; expand conservatively."));
        }
        return new(selected, removals, expansion, reasons.OrderBy(reason => reason.Code,
            StringComparer.Ordinal).ToArray());
    }

    internal static IReadOnlyList<TextSpan> ResolveSelectionSpans(string path, string source,
        IReadOnlyList<string> declarationIds)
    {
        ArgumentNullException.ThrowIfNull(declarationIds);
        var catalog = Create(path, source);
        if (catalog.HasErrors)
            throw new EvaluationContractException($"Declaration selection for {path} contains parse errors.");
        var requested = declarationIds.Distinct(StringComparer.Ordinal).ToArray();
        if (requested.Length != declarationIds.Count)
            throw new EvaluationContractException($"Declaration selection for {path} contains duplicate IDs.");
        var missing = requested.FirstOrDefault(id => !catalog.Entries.ContainsKey(id));
        if (missing is not null)
            throw new EvaluationContractException(
                $"Declaration selection for {path} is stale or unsupported: {missing}.");
        return requested.Select(id => catalog.Entries[id].Span).OrderBy(span => span.Start).ToArray();
    }

    private static Catalog Create(string path, string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source, path: path);
        var root = tree.GetRoot();
        var nodes = root.DescendantNodes().Where(IsCatalogNode).ToArray();
        var entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
        var duplicates = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var node in nodes.OrderBy(node => node.SpanStart))
        {
            var rawId = Identity(node);
            duplicates.TryGetValue(rawId, out var duplicate);
            duplicates[rawId] = duplicate + 1;
            var id = BoundIdentity(duplicate == 0 ? rawId : $"{rawId}#{duplicate + 1}");
            var span = tree.GetLineSpan(node.Span);
            var display = DisplayName(node);
            var hashText = HashText(node);
            entries[id] = new(id, node.Kind().ToString(), display,
                span.StartLinePosition.Line + 1, span.EndLinePosition.Line + 1,
                SourceAnalyzer.Hash(hashText), IsInsidePartialType(node), node.Span);
        }
        var skeleton = source;
        foreach (var node in nodes.Where(node => !node.Ancestors().Any(IsCatalogNode))
                     .OrderByDescending(node => node.SpanStart))
            skeleton = skeleton[..node.SpanStart] + "<declaration>" + skeleton[node.Span.End..];
        return new(entries, SyntaxFingerprint(skeleton), tree.GetDiagnostics().Any(diagnostic =>
            diagnostic.Severity == DiagnosticSeverity.Error));
    }

    private static bool IsCatalogNode(SyntaxNode node) => node is BaseMethodDeclarationSyntax or
        AccessorDeclarationSyntax or FieldDeclarationSyntax or EventFieldDeclarationSyntax or
        GlobalStatementSyntax || node is PropertyDeclarationSyntax property &&
        (property.ExpressionBody is not null || property.Initializer is not null) ||
        node is IndexerDeclarationSyntax indexer && indexer.ExpressionBody is not null;

    private static string Identity(SyntaxNode node)
    {
        var namespaces = node.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().Reverse()
            .Select(item => item.Name.ToString());
        var types = node.Ancestors().OfType<TypeDeclarationSyntax>().Reverse()
            .Select(item => $"{item.Identifier.ValueText}`{item.TypeParameterList?.Parameters.Count ?? 0}");
        var owner = string.Join('.', namespaces.Concat(types));
        if (owner.Length == 0) owner = "<global>";
        return node switch
        {
            MethodDeclarationSyntax method => $"{owner}.method:{method.Identifier.ValueText}`{method.TypeParameterList?.Parameters.Count ?? 0}({Parameters(method.ParameterList)})",
            ConstructorDeclarationSyntax constructor => $"{owner}.ctor({Parameters(constructor.ParameterList)})",
            DestructorDeclarationSyntax => $"{owner}.dtor",
            OperatorDeclarationSyntax op => $"{owner}.operator:{op.OperatorToken.Text}({Parameters(op.ParameterList)})",
            ConversionOperatorDeclarationSyntax conversion => $"{owner}.conversion:{conversion.Type}({Parameters(conversion.ParameterList)})",
            AccessorDeclarationSyntax accessor => $"{owner}.{AccessorOwner(accessor)}.{accessor.Keyword.ValueText}",
            PropertyDeclarationSyntax property => $"{owner}.property:{property.Identifier.ValueText}",
            IndexerDeclarationSyntax indexer => $"{owner}.indexer({Parameters(indexer.ParameterList)})",
            FieldDeclarationSyntax field => $"{owner}.field:{string.Join(',', field.Declaration.Variables.Select(variable => variable.Identifier.ValueText))}",
            EventFieldDeclarationSyntax field => $"{owner}.event:{string.Join(',', field.Declaration.Variables.Select(variable => variable.Identifier.ValueText))}",
            GlobalStatementSyntax statement => $"<global>.statement:{GlobalOrdinal(statement)}:{ShortHash(TokenFingerprint(statement))}",
            _ => $"{owner}.{node.Kind()}"
        };
    }

    private static string HashText(SyntaxNode node) => node switch
    {
        PropertyDeclarationSyntax property when property.AccessorList is not null => SyntaxFingerprint(string.Concat(
            property.AttributeLists, property.Modifiers.ToString(), property.Type, property.ExplicitInterfaceSpecifier,
            property.Identifier, property.Initializer, property.ExpressionBody)),
        _ => TokenFingerprint(node)
    };

    private static string DisplayName(SyntaxNode node) => node switch
    {
        MethodDeclarationSyntax method => $"method {method.Identifier.ValueText}",
        ConstructorDeclarationSyntax constructor => $"constructor {constructor.Identifier.ValueText}",
        DestructorDeclarationSyntax destructor => $"destructor {destructor.Identifier.ValueText}",
        OperatorDeclarationSyntax op => $"operator {op.OperatorToken.Text}",
        ConversionOperatorDeclarationSyntax conversion => $"conversion {conversion.Type}",
        AccessorDeclarationSyntax accessor => $"{AccessorOwner(accessor)} {accessor.Keyword.ValueText}",
        PropertyDeclarationSyntax property => $"property {property.Identifier.ValueText}",
        IndexerDeclarationSyntax => "indexer",
        FieldDeclarationSyntax field => $"field {string.Join(", ", field.Declaration.Variables.Select(variable => variable.Identifier.ValueText))}",
        EventFieldDeclarationSyntax field => $"event {string.Join(", ", field.Declaration.Variables.Select(variable => variable.Identifier.ValueText))}",
        GlobalStatementSyntax => "top-level statement",
        _ => node.Kind().ToString()
    };

    private static string AccessorOwner(AccessorDeclarationSyntax accessor) => accessor.Parent?.Parent switch
    {
        PropertyDeclarationSyntax property => $"property {property.Identifier.ValueText}",
        IndexerDeclarationSyntax => "indexer",
        EventDeclarationSyntax @event => $"event {@event.Identifier.ValueText}",
        _ => "accessor"
    };

    private static string Parameters(BaseParameterListSyntax parameters) => string.Join(',',
        parameters.Parameters.Select(parameter => Normalize($"{parameter.Modifiers} {parameter.Type}")));

    private static int GlobalOrdinal(GlobalStatementSyntax statement) => statement.SyntaxTree.GetRoot()
        .DescendantNodes().OfType<GlobalStatementSyntax>().TakeWhile(item => item != statement).Count() + 1;

    private static string BoundIdentity(string value)
    {
        const int maxLength = 512;
        if (value.Length <= maxLength) return value;
        var suffix = $"~{ShortHash(value)}";
        return value[..(maxLength - suffix.Length)] + suffix;
    }

    private static string ShortHash(string value) => Convert.ToHexString(
        System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..24].ToLowerInvariant();

    private static string SyntaxFingerprint(string source) => TokenFingerprint(
        CSharpSyntaxTree.ParseText(source).GetRoot());

    private static string TokenFingerprint(SyntaxNode node)
    {
        var elements = new List<(int Start, int Order, string Text)>();
        foreach (var token in node.DescendantTokens(descendIntoTrivia: true))
            elements.Add((token.SpanStart, 1, $"T:{(int)token.Kind()}:{token.Text.Length}:{token.Text}"));
        foreach (var trivia in node.DescendantTrivia(descendIntoTrivia: true))
        {
            if (trivia.IsKind(SyntaxKind.WhitespaceTrivia) || trivia.IsKind(SyntaxKind.EndOfLineTrivia)) continue;
            var text = trivia.ToFullString();
            elements.Add((trivia.SpanStart, 0, $"V:{(int)trivia.Kind()}:{text.Length}:{text}"));
        }
        return string.Join('|', elements.OrderBy(item => item.Start).ThenBy(item => item.Order)
            .Select(item => item.Text));
    }

    private static bool IsInsidePartialType(SyntaxNode node) => node.Ancestors().OfType<TypeDeclarationSyntax>()
        .Any(type => type.Modifiers.Any(modifier => modifier.IsKind(SyntaxKind.PartialKeyword)));

    private static DeclarationScope ToScope(Entry entry, string reason) => new(entry.Id, entry.Kind,
        entry.DisplayName, entry.StartLine, entry.EndLine, reason);

    private static string Decode(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true), true);
        return reader.ReadToEnd();
    }

    private static string Normalize(string value) => string.Join(' ', value.Split((char[]?)null,
        StringSplitOptions.RemoveEmptyEntries));
}
