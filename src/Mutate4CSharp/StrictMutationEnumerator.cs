using System.Text;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace Mutate4CSharp;

internal sealed record StrictMutationEnumerationResult(
    IReadOnlyList<MutationCandidate> Candidates,
    IReadOnlyList<EvaluationReason> Reasons,
    bool IsComplete);

internal static class StrictMutationEnumerator
{
    private const int MaxDiagnostics = 100;

    public static StrictMutationEnumerationResult Enumerate(InputSnapshot snapshot,
        CheckConfiguration configuration, ScopePlan scopePlan, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(scopePlan);
        if (!scopePlan.IsComplete)
            return Refused("ENUMERATION_SCOPE_INCOMPLETE",
                "Semantic enumeration requires one complete captured scope plan.");
        if (!Path.GetFullPath(configuration.Root).Equals(Path.GetFullPath(snapshot.CaptureRoot),
                HostPathComparison))
            return Refused("ENUMERATION_CONFIGURATION_MISMATCH",
                "Strict configuration was not loaded from the immutable snapshot root.");

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var captured = snapshot.Files.Where(file => file.Exists)
                .ToDictionary(file => file.RelativePath, StringComparer.Ordinal);
            var projects = configuration.Projects.ToDictionary(project => project.Project,
                StringComparer.Ordinal);
            var candidates = new List<MutationCandidate>();
            foreach (var unit in scopePlan.ProjectUnits.OrderBy(unit => unit.Project,
                         StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!projects.TryGetValue(unit.Project, out var project))
                    return Refused("ENUMERATION_PROJECT_UNMAPPED",
                        $"Scope project {unit.Project} has no exact strict configuration context.");
                var context = BuildContext(snapshot, captured, project, configuration.Exclusions,
                    cancellationToken);
                var targetPaths = SelectTargetPaths(unit, scopePlan, context.SourcePaths)
                    .Where(path => !context.ExcludedPaths.Contains(path)).ToArray();
                foreach (var path in targetPaths)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!context.Trees.TryGetValue(path, out var tree))
                        return Refused("ENUMERATION_STALE_SOURCE",
                            $"Selected source {path} is absent from the captured compile inventory for {project.Project}.");
                    var model = context.Compilation.GetSemanticModel(tree, ignoreAccessibility: true);
                    var sites = SourceAnalyzer.DiscoverStrictSites(tree, model);
                    if (unit.Expansion.Equals("CHANGED_DECLARATIONS", StringComparison.Ordinal))
                    {
                        var file = scopePlan.Files.SingleOrDefault(item =>
                            item.Path.Equals(path, StringComparison.Ordinal));
                        if (file is null)
                            return Refused("ENUMERATION_SCOPE_STALE",
                                $"Changed-declaration source {path} has no captured declaration plan.");
                        var spans = DeclarationCatalog.ResolveSelectionSpans(path,
                            context.Sources[path], file.Declarations.Select(item => item.Id).ToArray());
                        sites = sites.Where(site => spans.Any(span => span.Contains(
                            new Microsoft.CodeAnalysis.Text.TextSpan(site.Start, site.Length)))).ToArray();
                    }
                    var root = tree.GetRoot(cancellationToken);
                    foreach (var site in sites)
                    {
                        if (site.Start < 0 || site.Length <= 0 || site.Start + site.Length > root.FullSpan.End)
                            return Refused("ENUMERATION_STALE_SPAN",
                                $"Mutation site for {path} is outside the captured syntax tree.");
                        var mutation = MutationIdentity.Create(path, root,
                            new Microsoft.CodeAnalysis.Text.TextSpan(site.Start, site.Length),
                            site.OperatorId, site.OperatorVersion, site.Replacement);
                        var evaluationId = EvaluationUnitIdentity.Compute(new(mutation.MutationId,
                            project.Project, project.TargetFramework, project.ParseContext));
                        candidates.Add(new(mutation.MutationId, evaluationId, mutation.Material,
                            project.Project, project.TargetFramework, project.ParseContext,
                            site.Start, site.Length));
                    }
                }
            }

            foreach (var group in candidates.GroupBy(candidate =>
                         (candidate.ProjectPath, candidate.TargetFramework, candidate.ParseContext)))
                MutationIdentity.RefuseCollisions(group.Select(candidate => new IdentifiedMutation(
                    candidate.MutationId, candidate.Material, candidate.SourceStart, candidate.SourceLength)));
            var duplicate = candidates.GroupBy(candidate => candidate.EvaluationUnitId,
                    StringComparer.Ordinal).FirstOrDefault(group => group.Count() > 1);
            if (duplicate is not null)
                return Refused("ENUMERATION_DUPLICATE_UNIT",
                    $"Semantic enumeration produced duplicate evaluation unit {duplicate.Key}.");
            return new(candidates.OrderBy(candidate => candidate.Material.RepositoryPath,
                    StringComparer.Ordinal).ThenBy(candidate => candidate.Material.DeclarationIdentity,
                    StringComparer.Ordinal).ThenBy(candidate => candidate.Material.StructuralSiteIdentity,
                    StringComparer.Ordinal).ThenBy(candidate => candidate.Material.OperatorId,
                    StringComparer.Ordinal).ThenBy(candidate => candidate.Material.Replacement,
                    StringComparer.Ordinal).ThenBy(candidate => candidate.ProjectPath,
                    StringComparer.Ordinal).ThenBy(candidate => candidate.TargetFramework,
                    StringComparer.Ordinal).ThenBy(candidate => candidate.ParseContext,
                    StringComparer.Ordinal).ToArray(), [], true);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or
                                   EvaluationContractException or System.Xml.XmlException or DecoderFallbackException)
        {
            return Refused("ENUMERATION_CONTEXT_UNSUPPORTED",
                $"Strict semantic context could not be proven complete: {Bound(ex.Message)}");
        }
    }

    private static ProjectContext BuildContext(InputSnapshot snapshot,
        IReadOnlyDictionary<string, SnapshotFile> captured, CheckProject project,
        IReadOnlyList<CheckExclusion> exclusions, CancellationToken cancellationToken)
    {
        if (!captured.ContainsKey(project.Project))
            throw new EvaluationContractException($"Configured project is not captured: {project.Project}.");
        var sourcePaths = captured.Keys.Where(path => path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) &&
                project.Sources.Any(pattern => ProjectOwnershipResolver.GlobMatches(path, pattern)))
            .OrderBy(path => path, StringComparer.Ordinal).ToArray();
        if (sourcePaths.Length == 0)
            throw new EvaluationContractException(
                $"Configured project {project.Project} has no captured compile inputs.");
        ValidateProjectFile(snapshot, captured, project, sourcePaths);
        var excluded = sourcePaths.Where(path => exclusions.Any(exclusion =>
            ProjectOwnershipResolver.GlobMatches(path, exclusion.Path))).ToHashSet(StringComparer.Ordinal);
        var parseOptions = new CSharpParseOptions(LanguageVersion.CSharp14,
            preprocessorSymbols: StandardSymbols(project).Concat(project.DefineConstants)
                .Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal));
        var trees = new Dictionary<string, SyntaxTree>(StringComparer.Ordinal);
        var sources = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var path in sourcePaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var source = ReadCapturedText(snapshot, path);
            sources.Add(path, source);
            if (IsGeneratedSource(path, source)) excluded.Add(path);
            var tree = CSharpSyntaxTree.ParseText(SourceText.From(source, Encoding.UTF8), parseOptions,
                path, cancellationToken);
            var parseErrors = tree.GetDiagnostics(cancellationToken).Where(diagnostic =>
                diagnostic.Severity == DiagnosticSeverity.Error).Take(MaxDiagnostics).ToArray();
            if (parseErrors.Length > 0)
                throw new EvaluationContractException(
                    $"Captured source {path} has parse errors: {FormatDiagnostic(parseErrors[0])}");
            trees.Add(path, tree);
        }
        var references = PlatformReferences();
        var nullable = project.Nullable switch
        {
            "enable" => NullableContextOptions.Enable,
            "disable" => NullableContextOptions.Disable,
            "annotations" => NullableContextOptions.Annotations,
            "warnings" => NullableContextOptions.Warnings,
            _ => throw new EvaluationContractException($"Unsupported nullable context: {project.Nullable}.")
        };
        var compilation = CSharpCompilation.Create(
            "StrictEnumeration_" + project.Id,
            trees.Values,
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
                deterministic: true, nullableContextOptions: nullable));
        var errors = compilation.GetDiagnostics(cancellationToken).Where(diagnostic =>
                diagnostic.Severity == DiagnosticSeverity.Error)
            .Take(MaxDiagnostics).ToArray();
        if (errors.Length > 0)
            throw new EvaluationContractException(
                $"Captured project {project.Project} has unresolved semantic diagnostics: {FormatDiagnostic(errors[0])}");
        return new(sourcePaths, excluded, sources, trees, compilation);
    }

    private static IReadOnlyList<string> SelectTargetPaths(ProjectScopeUnit unit, ScopePlan scopePlan,
        IReadOnlyList<string> sourcePaths)
    {
        if (unit.Expansion.Equals("FULL_PROJECT", StringComparison.Ordinal)) return sourcePaths;
        if (!unit.Expansion.Equals("CHANGED_DECLARATIONS", StringComparison.Ordinal))
            throw new EvaluationContractException(
                $"Unsupported scope expansion mode for {unit.Project}: {unit.Expansion}.");
        return unit.Inputs.Where(path => sourcePaths.Contains(path, StringComparer.Ordinal) &&
                    scopePlan.Files.Any(file => file.Path.Equals(path, StringComparison.Ordinal)))
            .Distinct(StringComparer.Ordinal).OrderBy(path => path, StringComparer.Ordinal).ToArray();
    }

    private static void ValidateProjectFile(InputSnapshot snapshot,
        IReadOnlyDictionary<string, SnapshotFile> captured, CheckProject project,
        IReadOnlyList<string> configuredSourcePaths)
    {
        var path = CapturedPath(snapshot, project.Project);
        XDocument document;
        try { document = XDocument.Load(path, LoadOptions.SetLineInfo); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            throw new EvaluationContractException(
                $"Configured project XML could not be read: {project.Project}.", ex);
        }
        var root = document.Root;
        if (root?.Name.LocalName != "Project")
            throw new EvaluationContractException($"Configured project XML is invalid: {project.Project}.");
        if (root.Descendants().Any(element => element.Attribute("Condition") is not null))
            throw new EvaluationContractException(
                $"Conditional project evaluation is unsupported for strict enumeration: {project.Project}.");
        if (root.Descendants().Any(element => element.Name.LocalName is "ProjectReference" or "PackageReference"))
            throw new EvaluationContractException(
                $"Project/package reference semantic resolution is not supported in enumeration v1: {project.Project}.");
        if (root.Descendants().Any(element => element.Name.LocalName == "Compile" &&
                (element.Attribute("Remove") is not null || element.Attribute("Update") is not null)))
            throw new EvaluationContractException(
                $"Compile Remove/Update transforms are unsupported in enumeration v1: {project.Project}.");
        var compileElements = root.Descendants().Where(element =>
            element.Name.LocalName == "Compile").ToArray();
        if (compileElements.Any(element => element.Attribute("Exclude") is not null))
            throw new EvaluationContractException(
                $"Compile Exclude transforms are unsupported in enumeration v1: {project.Project}.");
        var frameworks = root.Descendants().Where(element =>
            element.Name.LocalName is "TargetFramework" or "TargetFrameworks").ToArray();
        if (frameworks.Any(element => element.Name.LocalName == "TargetFrameworks") ||
            frameworks.Length != 1 || frameworks[0].Value.Trim() != project.TargetFramework)
            throw new EvaluationContractException(
                $"Captured project target framework does not match strict configuration: {project.Project}.");
        var langVersion = SingleProperty(root, "LangVersion");
        if (langVersion is not null && langVersion is not ("14" or "14.0"))
            throw new EvaluationContractException(
                $"Captured project language version is unsupported: {langVersion}.");
        var nullable = SingleProperty(root, "Nullable");
        if (nullable is not null && !nullable.Equals(project.Nullable, StringComparison.OrdinalIgnoreCase))
            throw new EvaluationContractException(
                $"Captured project nullable context does not match strict configuration: {project.Project}.");
        var constants = SingleProperty(root, "DefineConstants");
        if (constants is not null)
        {
            var actual = constants.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .OrderBy(value => value, StringComparer.Ordinal).ToArray();
            var expected = project.DefineConstants.OrderBy(value => value, StringComparer.Ordinal).ToArray();
            if (!actual.SequenceEqual(expected, StringComparer.Ordinal))
                throw new EvaluationContractException(
                    $"Captured project DefineConstants do not match strict configuration: {project.Project}.");
        }

        var compileInventory = new HashSet<string>(StringComparer.Ordinal);
        var defaultCompile = SingleProperty(root, "EnableDefaultCompileItems");
        if (defaultCompile is not null && !bool.TryParse(defaultCompile, out _))
            throw new EvaluationContractException(
                $"EnableDefaultCompileItems must be true or false: {project.Project}.");
        if (!string.Equals(defaultCompile, "false", StringComparison.OrdinalIgnoreCase))
        {
            var projectDirectory = RepositoryDirectory(project.Project);
            foreach (var source in captured.Keys.Where(path =>
                         path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) &&
                         IsUnderDirectory(path, projectDirectory)))
                compileInventory.Add(source);
        }
        foreach (var element in compileElements)
        {
            var include = element.Attribute("Include")?.Value;
            if (string.IsNullOrWhiteSpace(include))
                throw new EvaluationContractException(
                    $"Compile items require one static Include in enumeration v1: {project.Project}.");
            var resolved = ResolveStaticCompilePath(snapshot, project.Project, include);
            if (!captured.ContainsKey(resolved))
                throw new EvaluationContractException(
                    $"Compile input is absent from the frozen snapshot: {resolved}.");
            compileInventory.Add(resolved);
        }
        var configured = configuredSourcePaths.ToHashSet(StringComparer.Ordinal);
        if (!compileInventory.SetEquals(configured))
        {
            var missing = compileInventory.Except(configured, StringComparer.Ordinal).Order().FirstOrDefault();
            var extra = configured.Except(compileInventory, StringComparer.Ordinal).Order().FirstOrDefault();
            throw new EvaluationContractException(
                $"Configured source inventory does not equal the captured static Compile inventory for " +
                $"{project.Project}; missing={missing ?? "<none>"}, extra={extra ?? "<none>"}.");
        }
    }

    private static string ResolveStaticCompilePath(InputSnapshot snapshot, string projectPath, string include)
    {
        if (include.Contains(';') || include.IndexOfAny(['*', '?']) >= 0 ||
            include.Contains("$(", StringComparison.Ordinal) || include.Contains("@(", StringComparison.Ordinal) ||
            Path.IsPathRooted(include))
            throw new EvaluationContractException(
                $"Dynamic or multi-value Compile Include is unsupported in enumeration v1: {include}.");
        var projectDirectory = Path.GetDirectoryName(CapturedPath(snapshot, projectPath))!;
        var full = Path.GetFullPath(include.Replace('/', Path.DirectorySeparatorChar), projectDirectory);
        var relative = Path.GetRelativePath(snapshot.CaptureRoot, full).Replace('\\', '/');
        try { return SnapshotInputPolicy.NormalizeRelative(relative); }
        catch (SnapshotCaptureException ex)
        {
            throw new EvaluationContractException(
                $"Compile Include escapes the immutable snapshot: {include}.", ex);
        }
    }

    private static string RepositoryDirectory(string path)
    {
        var index = path.LastIndexOf('/');
        return index < 0 ? string.Empty : path[..index];
    }

    private static bool IsUnderDirectory(string path, string directory) =>
        directory.Length == 0 || path.StartsWith(directory + "/", StringComparison.Ordinal);

    private static string? SingleProperty(XElement root, string name)
    {
        var values = root.Descendants().Where(element => element.Name.LocalName == name).ToArray();
        if (values.Length > 1)
            throw new EvaluationContractException($"Multiple {name} properties are unsupported.");
        return values.SingleOrDefault()?.Value.Trim();
    }

    private static IReadOnlyList<MetadataReference> PlatformReferences()
    {
        var trusted = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES");
        var paths = (trusted ?? string.Empty).Split(Path.PathSeparator,
            StringSplitOptions.RemoveEmptyEntries).Distinct(HostPathComparer).OrderBy(path => path,
            StringComparer.Ordinal).ToArray();
        if (paths.Length == 0)
            throw new EvaluationContractException("Trusted net10.0 platform references are unavailable.");
        return paths.Select(path => MetadataReference.CreateFromFile(path)).ToArray();
    }

    private static IEnumerable<string> StandardSymbols(CheckProject project)
    {
        yield return "NET";
        yield return "NET10_0";
        yield return "NET10_0_OR_GREATER";
        yield return "NETCOREAPP";
        yield return "NETCOREAPP1_0_OR_GREATER";
        yield return "NETCOREAPP2_0_OR_GREATER";
        yield return "NETCOREAPP2_1_OR_GREATER";
        yield return "NETCOREAPP2_2_OR_GREATER";
        yield return "NETCOREAPP3_0_OR_GREATER";
        yield return "NETCOREAPP3_1_OR_GREATER";
        for (var version = 5; version <= 10; version++) yield return $"NET{version}_0_OR_GREATER";
    }

    private static string ReadCapturedText(InputSnapshot snapshot, string path)
    {
        using var stream = new FileStream(CapturedPath(snapshot, path), FileMode.Open, FileAccess.Read,
            FileShare.Read, 64 * 1024, FileOptions.SequentialScan);
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true), true);
        return reader.ReadToEnd();
    }

    private static bool IsGeneratedSource(string path, string source)
    {
        var file = Path.GetFileName(path);
        if (file.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase) ||
            file.EndsWith(".generated.cs", StringComparison.OrdinalIgnoreCase) ||
            file.EndsWith(".designer.cs", StringComparison.OrdinalIgnoreCase))
            return true;
        var prefix = source.AsSpan(0, Math.Min(source.Length, 2048));
        return prefix.Contains("<auto-generated", StringComparison.OrdinalIgnoreCase);
    }

    private static string CapturedPath(InputSnapshot snapshot, string path) => Path.Combine(
        snapshot.CaptureRoot, path.Replace('/', Path.DirectorySeparatorChar));

    private static string FormatDiagnostic(Diagnostic diagnostic)
    {
        var line = diagnostic.Location.GetLineSpan().StartLinePosition;
        return $"{diagnostic.Id} at {line.Line + 1}:{line.Character + 1}: {Bound(diagnostic.GetMessage())}";
    }

    private static StrictMutationEnumerationResult Refused(string code, string message) =>
        new([], [new(code, Bound(message))], false);

    private static string Bound(string value) => value.Length <= 1024 ? value : value[..1024];

    private static StringComparison HostPathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    private static StringComparer HostPathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private sealed record ProjectContext(IReadOnlyList<string> SourcePaths,
        IReadOnlySet<string> ExcludedPaths, IReadOnlyDictionary<string, string> Sources,
        IReadOnlyDictionary<string, SyntaxTree> Trees, CSharpCompilation Compilation);
}
