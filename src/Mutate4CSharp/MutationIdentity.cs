using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Mutate4CSharp;

internal sealed record MutationIdentityMaterial(
    string RepositoryPath,
    string DeclarationIdentity,
    string StructuralSiteIdentity,
    string OperatorId,
    string OperatorVersion,
    string Replacement);

internal sealed record IdentifiedMutation(string MutationId, MutationIdentityMaterial Material,
    int SourceStart = -1, int SourceLength = -1)
{
    public string RepositoryPath => Material.RepositoryPath;
    public string DeclarationIdentity => Material.DeclarationIdentity;
    public string StructuralSiteIdentity => Material.StructuralSiteIdentity;
}

internal sealed record EvaluationUnitIdentityMaterial(
    string MutationId,
    string ProjectPath,
    string TargetFramework,
    string ParseContext);

internal static class MutationIdentity
{
    public const string Algorithm = "sha256-length-framed-v1";
    public const string OperatorContractVersion = "1";

    public static IdentifiedMutation Create(string repositoryPath, SyntaxNode root, TextSpan siteSpan,
        string operatorId, string operatorVersion, string replacement)
    {
        ArgumentNullException.ThrowIfNull(root);
        if (siteSpan.Start < root.FullSpan.Start || siteSpan.End > root.FullSpan.End || siteSpan.IsEmpty)
            throw new ArgumentOutOfRangeException(nameof(siteSpan));

        var site = FindSite(root, siteSpan);
        var declaration = FindDeclaration(site.Parent ?? root);
        var material = new MutationIdentityMaterial(NormalizeRelative(repositoryPath),
            DeclarationIdentity(declaration), StructuralIdentity(declaration, site), operatorId,
            operatorVersion, replacement);
        return new(Compute(material), material, siteSpan.Start, siteSpan.Length);
    }

    public static string Compute(MutationIdentityMaterial material)
    {
        ArgumentNullException.ThrowIfNull(material);
        var path = NormalizeRelative(material.RepositoryPath);
        ValidateComponent(material.DeclarationIdentity, nameof(material.DeclarationIdentity));
        ValidateComponent(material.StructuralSiteIdentity, nameof(material.StructuralSiteIdentity));
        ValidateToken(material.OperatorId, nameof(material.OperatorId));
        ValidateToken(material.OperatorVersion, nameof(material.OperatorVersion));
        ValidateComponent(material.Replacement, nameof(material.Replacement), allowEmpty: true);

        return "mutation:v1:" + ComputeDigest("mutation", [
            ("algorithm", Algorithm),
            ("identity-version", "1"),
            ("repository-path", path),
            ("declaration", material.DeclarationIdentity),
            ("structural-site", material.StructuralSiteIdentity),
            ("operator", material.OperatorId),
            ("operator-version", material.OperatorVersion),
            ("replacement", material.Replacement)
        ]);
    }

    public static void RefuseCollisions(IEnumerable<IdentifiedMutation> mutations)
    {
        ArgumentNullException.ThrowIfNull(mutations);
        foreach (var group in mutations.GroupBy(item => item.MutationId, StringComparer.Ordinal))
        {
            bool materialMatches;
            try { materialMatches = group.All(item => Compute(item.Material) == item.MutationId); }
            catch (ArgumentException ex)
            {
                throw new EvaluationContractException(
                    $"Mutation identity material is invalid: {ex.Message}", ex);
            }
            if (!IsMutationId(group.Key) || !materialMatches)
                throw new EvaluationContractException("Mutation IDs must match their canonical versioned material.");
            var occurrences = group.Select(item => (item.Material, item.SourceStart, item.SourceLength)).Distinct();
            if (occurrences.Skip(1).Any())
                throw new EvaluationContractException($"Mutation identity collision refused for {group.Key}.");
        }
    }

    public static bool IsMutationId(string? value) => value?.Length == 76 &&
        value.StartsWith("mutation:v1:", StringComparison.Ordinal) &&
        value[12..].All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    internal static string NormalizeRelative(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (path.StartsWith('/') || path.StartsWith('\\') ||
            path.Length >= 2 && char.IsAsciiLetter(path[0]) && path[1] == ':')
            throw new ArgumentException("Mutation paths must be repository-relative.", nameof(path));
        var normalized = path.Replace('\\', '/');
        if (string.IsNullOrWhiteSpace(normalized) || normalized.Length > 4096 ||
            normalized.Split('/').Any(part => part is "" or "." or ".."))
            throw new ArgumentException("Mutation paths must be canonical slash-relative paths.", nameof(path));
        return normalized;
    }

    private static SyntaxNodeOrToken FindSite(SyntaxNode root, TextSpan span)
    {
        var token = root.FindToken(span.Start, findInsideTrivia: false);
        if (token.Span == span) return token;
        var node = root.FindNode(span, getInnermostNodeForTie: true);
        if (node.Span == span) return node;
        throw new ArgumentException("Mutation span must identify one exact syntax node or token.", nameof(span));
    }

    private static SyntaxNode FindDeclaration(SyntaxNode node) =>
        node.AncestorsAndSelf().FirstOrDefault(IsDeclaration) ?? node.SyntaxTree.GetRoot();

    private static bool IsDeclaration(SyntaxNode node) => node is BaseMethodDeclarationSyntax or
        AccessorDeclarationSyntax or FieldDeclarationSyntax or EventFieldDeclarationSyntax or
        PropertyDeclarationSyntax or IndexerDeclarationSyntax or EventDeclarationSyntax or
        DelegateDeclarationSyntax or GlobalStatementSyntax or BaseTypeDeclarationSyntax or
        EnumMemberDeclarationSyntax or ExtensionBlockDeclarationSyntax;

    private static string DeclarationIdentity(SyntaxNode node)
    {
        if (node is CompilationUnitSyntax) return "<file>";
        var namespaces = node.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().Reverse()
            .Select(item => NormalizeSyntax(item.Name));
        var containers = node.Ancestors().Where(item => item is BaseTypeDeclarationSyntax or
                ExtensionBlockDeclarationSyntax).Reverse().Select(ContainerIdentity);
        var owner = string.Join('.', namespaces.Concat(containers));
        if (owner.Length == 0) owner = "<global>";
        return node switch
        {
            MethodDeclarationSyntax method => $"{owner}.method:{ExplicitInterface(method.ExplicitInterfaceSpecifier)}{method.Identifier.ValueText}`{method.TypeParameterList?.Parameters.Count ?? 0}({Parameters(method.ParameterList)}):{NormalizeSyntax(method.ReturnType)}",
            ConstructorDeclarationSyntax constructor =>
                $"{owner}.{(constructor.Modifiers.Any(SyntaxKind.StaticKeyword) ? "cctor" : "ctor")}({Parameters(constructor.ParameterList)})",
            DestructorDeclarationSyntax => $"{owner}.dtor",
            OperatorDeclarationSyntax op => $"{owner}.operator:{ExplicitInterface(op.ExplicitInterfaceSpecifier)}{Checked(op.CheckedKeyword)}{op.OperatorToken.Text}({Parameters(op.ParameterList)}):{NormalizeSyntax(op.ReturnType)}",
            ConversionOperatorDeclarationSyntax conversion => $"{owner}.conversion:{ExplicitInterface(conversion.ExplicitInterfaceSpecifier)}{conversion.ImplicitOrExplicitKeyword.ValueText}:{Checked(conversion.CheckedKeyword)}{NormalizeSyntax(conversion.Type)}({Parameters(conversion.ParameterList)})",
            AccessorDeclarationSyntax accessor => $"{owner}.{AccessorOwner(accessor)}.{accessor.Keyword.ValueText}",
            PropertyDeclarationSyntax property => $"{owner}.property:{ExplicitInterface(property.ExplicitInterfaceSpecifier)}{property.Identifier.ValueText}:{NormalizeSyntax(property.Type)}",
            IndexerDeclarationSyntax indexer => $"{owner}.indexer:{ExplicitInterface(indexer.ExplicitInterfaceSpecifier)}({Parameters(indexer.ParameterList)}):{NormalizeSyntax(indexer.Type)}",
            EventDeclarationSyntax @event => $"{owner}.event:{ExplicitInterface(@event.ExplicitInterfaceSpecifier)}{@event.Identifier.ValueText}:{NormalizeSyntax(@event.Type)}",
            DelegateDeclarationSyntax @delegate => $"{owner}.delegate:{@delegate.Identifier.ValueText}`{@delegate.TypeParameterList?.Parameters.Count ?? 0}({Parameters(@delegate.ParameterList)}):{NormalizeSyntax(@delegate.ReturnType)}",
            FieldDeclarationSyntax field => $"{owner}.field:{string.Join(',', field.Declaration.Variables.Select(variable => variable.Identifier.ValueText))}:{NormalizeSyntax(field.Declaration.Type)}",
            EventFieldDeclarationSyntax field => $"{owner}.event:{string.Join(',', field.Declaration.Variables.Select(variable => variable.Identifier.ValueText))}:{NormalizeSyntax(field.Declaration.Type)}",
            ExtensionBlockDeclarationSyntax extension => $"{owner}.extension({ExtensionParameters(extension)})",
            TypeDeclarationSyntax type => $"{owner}.type:{type.Keyword.ValueText}:{type.Identifier.ValueText}`{type.TypeParameterList?.Parameters.Count ?? 0}",
            EnumDeclarationSyntax @enum => $"{owner}.type:enum:{@enum.Identifier.ValueText}",
            EnumMemberDeclarationSyntax member => $"{owner}.enum-member:{member.Identifier.ValueText}",
            GlobalStatementSyntax statement => $"<global>.statement:{SiteFingerprint(statement)}:{IdenticalGlobalOrdinal(statement)}",
            _ => $"{owner}.{node.Kind()}"
        };
    }

    private static string StructuralIdentity(SyntaxNode declaration, SyntaxNodeOrToken site)
    {
        var components = new List<string>();
        var current = site;
        while (current != declaration)
        {
            var parent = current.Parent ?? throw new EvaluationContractException(
                "Mutation site is not contained by its declaration.");
            var siblings = parent.ChildNodesAndTokens()
                .Where(item => item.IsToken == current.IsToken && item.RawKind == current.RawKind).ToArray();
            var ordinal = Array.FindIndex(siblings, item => item == current);
            if (ordinal < 0) throw new EvaluationContractException("Mutation structural path could not be established.");
            components.Add($"{(current.IsToken ? 't' : 'n')}:{current.RawKind}:{ordinal}");
            current = parent;
        }
        components.Reverse();
        return components.Count == 0 ? "self" : string.Join('/', components);
    }

    private static int IdenticalGlobalOrdinal(GlobalStatementSyntax statement)
    {
        var fingerprint = SiteFingerprint(statement);
        return statement.SyntaxTree.GetRoot().DescendantNodes().OfType<GlobalStatementSyntax>()
            .TakeWhile(item => item != statement).Count(item => SiteFingerprint(item) == fingerprint) + 1;
    }

    private static string SiteFingerprint(SyntaxNode node) => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(string.Join('|', node.DescendantTokens(descendIntoTrivia: false)
            .Select(token => $"{token.RawKind}:{token.Text.Length}:{token.Text}")))))[..24].ToLowerInvariant();

    private static string Parameters(BaseParameterListSyntax parameters) => string.Join(',',
        parameters.Parameters.Select(parameter => NormalizeSyntax($"{parameter.Modifiers} {parameter.Type}")));

    private static string ContainerIdentity(SyntaxNode node) => node switch
    {
        ExtensionBlockDeclarationSyntax extension => $"extension({ExtensionParameters(extension)})",
        TypeDeclarationSyntax type => $"{type.Identifier.ValueText}`{type.TypeParameterList?.Parameters.Count ?? 0}",
        EnumDeclarationSyntax @enum => @enum.Identifier.ValueText,
        BaseTypeDeclarationSyntax type => type.Identifier.ValueText,
        _ => throw new EvaluationContractException("Unsupported declaration container.")
    };

    private static string Checked(SyntaxToken token) =>
        token.IsKind(SyntaxKind.CheckedKeyword) ? "checked:" : "unchecked:";

    private static string ExtensionParameters(ExtensionBlockDeclarationSyntax extension) =>
        extension.ParameterList is null ? "<missing>" : Parameters(extension.ParameterList);

    private static string ExplicitInterface(ExplicitInterfaceSpecifierSyntax? value) =>
        value is null ? string.Empty : NormalizeSyntax(value.Name) + ".";

    private static string AccessorOwner(AccessorDeclarationSyntax accessor) => accessor.Parent?.Parent switch
    {
        PropertyDeclarationSyntax property => $"property:{ExplicitInterface(property.ExplicitInterfaceSpecifier)}{property.Identifier.ValueText}:{NormalizeSyntax(property.Type)}",
        IndexerDeclarationSyntax indexer => $"indexer:{ExplicitInterface(indexer.ExplicitInterfaceSpecifier)}({Parameters(indexer.ParameterList)}):{NormalizeSyntax(indexer.Type)}",
        EventDeclarationSyntax @event => $"event:{ExplicitInterface(@event.ExplicitInterfaceSpecifier)}{@event.Identifier.ValueText}:{NormalizeSyntax(@event.Type)}",
        _ => "accessor"
    };

    private static string NormalizeSyntax(object? value) => string.Join(' ', (value?.ToString() ?? string.Empty)
        .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static void Add(IncrementalHash hash, string label, string value)
    {
        var labelBytes = Encoding.UTF8.GetBytes(label);
        var valueBytes = Encoding.UTF8.GetBytes(value);
        Span<byte> length = stackalloc byte[16];
        BinaryPrimitives.WriteInt64BigEndian(length[..8], labelBytes.LongLength);
        BinaryPrimitives.WriteInt64BigEndian(length[8..], valueBytes.LongLength);
        hash.AppendData(length[..8]);
        hash.AppendData(labelBytes);
        hash.AppendData(length[8..]);
        hash.AppendData(valueBytes);
    }

    internal static string ComputeDigest(string domain, IReadOnlyList<(string Label, string Value)> components)
    {
        ValidateToken(domain, nameof(domain));
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Add(hash, "identity-domain", domain);
        foreach (var (label, value) in components)
        {
            ValidateToken(label, nameof(components));
            ValidateComponent(value, nameof(components), allowEmpty: true);
            Add(hash, label, value);
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static void ValidateToken(string value, string name)
    {
        ValidateComponent(value, name);
        if (value.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('.' or '-' or '_' or ':')))
            throw new ArgumentException($"{name} must be a bounded canonical token.", name);
    }

    private static void ValidateComponent(string value, string name, bool allowEmpty = false)
    {
        if ((!allowEmpty && string.IsNullOrWhiteSpace(value)) || value.Length > 16 * 1024 || value.IndexOf('\0') >= 0)
            throw new ArgumentException($"{name} is outside identity bounds.", name);
    }
}

internal static class EvaluationUnitIdentity
{
    public static string Compute(EvaluationUnitIdentityMaterial material)
    {
        ArgumentNullException.ThrowIfNull(material);
        if (!MutationIdentity.IsMutationId(material.MutationId))
            throw new ArgumentException("Evaluation units require a valid mutation ID.");
        var project = MutationIdentity.NormalizeRelative(material.ProjectPath);
        if (string.IsNullOrWhiteSpace(material.TargetFramework) || string.IsNullOrWhiteSpace(material.ParseContext))
            throw new ArgumentException("Evaluation units require target-framework and parse context identities.");
        return "evaluation:v1:" + MutationIdentity.ComputeDigest("evaluation-unit", [
            ("algorithm", MutationIdentity.Algorithm),
            ("identity-version", "1"),
            ("mutation-id", material.MutationId),
            ("project-path", project),
            ("target-framework", material.TargetFramework),
            ("parse-context", material.ParseContext)
        ]);
    }

    public static bool IsEvaluationUnitId(string? value) => value?.Length == 78 &&
        value.StartsWith("evaluation:v1:", StringComparison.Ordinal) &&
        value[14..].All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
}
