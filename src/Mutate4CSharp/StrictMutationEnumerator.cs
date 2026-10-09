using System.Collections.Concurrent;
using System.Text;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace Mutate4CSharp;

internal sealed record StrictMutationEnumerationResult(
    IReadOnlyList<MutationCandidate> Candidates,
    IReadOnlyList<EvaluationReason> Reasons,
    bool IsComplete,
    string? SemanticContextIdentity);

internal static class StrictMutationEnumerator
{
    private const int MaxDiagnostics = 100;
    private static readonly ConcurrentDictionary<string, Lazy<ReferenceSet>> ReferenceSetCache =
        new(StringComparer.Ordinal);
    private static readonly HashSet<string> SupportedProjectProperties = new(
    [
        "TargetFramework", "Nullable", "LangVersion", "DefineConstants",
        "EnableDefaultItems", "EnableDefaultCompileItems", "DefaultItemExcludes",
        "DisableImplicitFrameworkDefines", "DisableImplicitConfigurationDefines",
        "DisableDiagnosticTracing", "ImplicitUsings", "OutputType",
        "RootNamespace", "AssemblyName", "Configurations", "Platforms", "PlatformTarget",
        "IsPackable", "IsPublishable", "GenerateDocumentationFile", "NoWarn",
        "TreatWarningsAsErrors", "WarningsAsErrors", "WarningsNotAsErrors", "WarningLevel",
        "AnalysisLevel", "AnalysisMode", "EnforceCodeStyleInBuild", "Deterministic", "DebugType",
        "DebugSymbols", "Optimize",
        "RestorePackagesWithLockFile", "RestoreLockedMode", "ContinuousIntegrationBuild",
        "GenerateAssemblyInfo", "AppendTargetFrameworkToOutputPath", "Version", "VersionPrefix",
        "VersionSuffix", "PackageId",
        "Authors", "Company", "Description", "Copyright", "RepositoryUrl", "RepositoryType",
        "PackageTags", "PackageLicenseExpression", "PackageReadmeFile", "PublishRepositoryUrl",
        "IncludeSymbols", "SymbolPackageFormat", "GeneratePackageOnBuild", "IsTestProject",
        "InvariantGlobalization"
    ], StringComparer.OrdinalIgnoreCase);
    private const string ImplicitUsingsSource = """
        global using global::System;
        global using global::System.Collections.Generic;
        global using global::System.IO;
        global using global::System.Linq;
        global using global::System.Net.Http;
        global using global::System.Threading;
        global using global::System.Threading.Tasks;
        """;

    public static StrictMutationEnumerationResult Enumerate(InputSnapshot snapshot,
        CheckConfiguration configuration, ScopePlan scopePlan, CancellationToken cancellationToken,
        string? sdkVersion = null)
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
            var contextIdentities = new List<(string Project, string Identity)>();
            foreach (var unit in scopePlan.ProjectUnits.OrderBy(unit => unit.Project,
                         StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!projects.TryGetValue(unit.Project, out var project))
                    return Refused("ENUMERATION_PROJECT_UNMAPPED",
                        $"Scope project {unit.Project} has no exact strict configuration context.");
                var context = BuildContext(snapshot, captured, project, configuration,
                    sdkVersion, cancellationToken);
                contextIdentities.Add((project.Project, context.Identity));
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
                        var files = scopePlan.Files.Where(item =>
                            item.Path.Equals(path, StringComparison.Ordinal)).Take(2).ToArray();
                        if (files.Length > 1)
                            return Refused("ENUMERATION_SCOPE_STALE",
                                $"Changed-declaration source {path} has duplicate captured declaration plans.");
                        var file = files.SingleOrDefault();
                        if (file is null)
                            return Refused("ENUMERATION_SCOPE_STALE",
                                $"Changed-declaration source {path} has no captured declaration plan.");
                        IReadOnlyList<TextSpan> spans;
                        try
                        {
                            spans = DeclarationCatalog.ResolveSelectionSpans(path,
                                context.Sources[path], file.Declarations.Select(item => item.Id).ToArray());
                        }
                        catch (EvaluationContractException ex)
                        {
                            return Refused("ENUMERATION_SCOPE_STALE", ex.Message);
                        }
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

            try
            {
                foreach (var group in candidates.GroupBy(candidate =>
                             (candidate.ProjectPath, candidate.TargetFramework, candidate.ParseContext)))
                    MutationIdentity.RefuseCollisions(group.Select(candidate => new IdentifiedMutation(
                        candidate.MutationId, candidate.Material, candidate.SourceStart, candidate.SourceLength)));
            }
            catch (EvaluationContractException ex)
            {
                return Refused("ENUMERATION_IDENTITY_COLLISION", ex.Message);
            }
            var duplicate = candidates.GroupBy(candidate => candidate.EvaluationUnitId,
                    StringComparer.Ordinal).FirstOrDefault(group => group.Count() > 1);
            if (duplicate is not null)
                return Refused("ENUMERATION_DUPLICATE_UNIT",
                    $"Semantic enumeration produced duplicate evaluation unit {duplicate.Key}.");
            var ordered = candidates.OrderBy(candidate => candidate.Material.RepositoryPath,
                    StringComparer.Ordinal).ThenBy(candidate => candidate.Material.DeclarationIdentity,
                    StringComparer.Ordinal).ThenBy(candidate => candidate.Material.StructuralSiteIdentity,
                    StringComparer.Ordinal).ThenBy(candidate => candidate.Material.OperatorId,
                    StringComparer.Ordinal).ThenBy(candidate => candidate.Material.Replacement,
                    StringComparer.Ordinal).ThenBy(candidate => candidate.ProjectPath,
                    StringComparer.Ordinal).ThenBy(candidate => candidate.TargetFramework,
                    StringComparer.Ordinal).ThenBy(candidate => candidate.ParseContext,
                    StringComparer.Ordinal).ToArray();
            var contextIdentity = MutationIdentity.ComputeDigest("semantic-enumeration-context-v2",
                contextIdentities.Distinct().OrderBy(item => item.Project, StringComparer.Ordinal)
                    .ThenBy(item => item.Identity, StringComparer.Ordinal)
                    .SelectMany((item, index) => new[]
                    {
                        ($"project-{index:D8}", item.Project),
                        ($"identity-{index:D8}", item.Identity)
                    }).ToArray());
            return new(ordered, [], true, contextIdentity);
        }
        catch (OperationCanceledException) { throw; }
        catch (EnumerationContextException ex)
        {
            return Refused(ex.Code, ex.Message);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or
                                   EvaluationContractException or System.Xml.XmlException or DecoderFallbackException or
                                   System.Text.RegularExpressions.RegexMatchTimeoutException)
        {
            return Refused("ENUMERATION_CONTEXT_UNSUPPORTED",
                $"Strict semantic context could not be proven complete: {Bound(ex.Message)}");
        }
    }

    private static ProjectContext BuildContext(InputSnapshot snapshot,
        IReadOnlyDictionary<string, SnapshotFile> captured, CheckProject project,
        CheckConfiguration configuration, string? sdkVersion, CancellationToken cancellationToken)
    {
        if (!captured.ContainsKey(project.Project))
            throw new EvaluationContractException($"Configured project is not captured: {project.Project}.");
        var sourcePaths = captured.Keys.Where(path => path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) &&
                project.Sources.Any(pattern => ProjectOwnershipResolver.GlobMatches(path, pattern)))
            .OrderBy(path => path, StringComparer.Ordinal).ToArray();
        if (sourcePaths.Length == 0)
            throw new EvaluationContractException(
                $"Configured project {project.Project} has no captured compile inputs.");
        var buildConfiguration = ResolveBuildConfiguration(configuration, project);
        var projectSemantics = ValidateProjectFile(snapshot, captured, project, sourcePaths,
            buildConfiguration);
        var excluded = sourcePaths.Where(path => configuration.Exclusions.Any(exclusion =>
            ProjectOwnershipResolver.GlobMatches(path, exclusion.Path))).ToHashSet(StringComparer.Ordinal);
        var parseOptions = new CSharpParseOptions(LanguageVersion.CSharp14,
            preprocessorSymbols: projectSemantics.PreprocessorSymbols);
        var trees = new Dictionary<string, SyntaxTree>(StringComparer.Ordinal);
        var sources = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var path in sourcePaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var source = ReadCapturedText(snapshot, path);
            sources.Add(path, source);
            if (ScopePlanner.IsGeneratedSource(path, source)) excluded.Add(path);
            var tree = CSharpSyntaxTree.ParseText(SourceText.From(source, Encoding.UTF8), parseOptions,
                path, cancellationToken);
            var parseErrors = tree.GetDiagnostics(cancellationToken).Where(diagnostic =>
                diagnostic.Severity == DiagnosticSeverity.Error).Take(MaxDiagnostics).ToArray();
            if (parseErrors.Length > 0)
                throw new EnumerationContextException("ENUMERATION_PARSE_INVALID",
                    $"Captured source {path} has parse errors: {FormatDiagnostic(parseErrors[0])}");
            trees.Add(path, tree);
        }
        if (projectSemantics.ImplicitUsings)
        {
            const string implicitPath = ".mutate4csharp/generated/ImplicitUsings.g.cs";
            trees.Add(implicitPath, CSharpSyntaxTree.ParseText(ImplicitUsingsSource, parseOptions,
                implicitPath, Encoding.UTF8, cancellationToken));
        }
        var referenceSet = PlatformReferences(sdkVersion);
        var compilation = CSharpCompilation.Create(
            "StrictEnumeration_" + project.Id,
            trees.Values,
            referenceSet.References,
            new CSharpCompilationOptions(projectSemantics.OutputKind,
                deterministic: true, nullableContextOptions: projectSemantics.Nullable));
        var errors = compilation.GetDiagnostics(cancellationToken).Where(diagnostic =>
                diagnostic.Severity == DiagnosticSeverity.Error)
            .Take(MaxDiagnostics).ToArray();
        if (errors.Length > 0)
            throw new EnumerationContextException("ENUMERATION_SEMANTIC_INVALID",
                $"Captured project {project.Project} has unresolved semantic diagnostics: {FormatDiagnostic(errors[0])}");
        var identity = MutationIdentity.ComputeDigest("project-semantic-context-v2",
            [
                ("project", project.Project), ("configuration", buildConfiguration),
                ("target-framework", project.TargetFramework), ("parse-context", project.ParseContext),
                ("language-version", project.LanguageVersion), ("nullable", project.Nullable),
                ("implicit-usings", projectSemantics.ImplicitUsings ? "enabled" : "disabled"),
                ("output-kind", projectSemantics.OutputKind.ToString()),
                ("references", referenceSet.Identity),
                ("symbols", string.Join(";", projectSemantics.PreprocessorSymbols)),
                ("ancestor-build-controls", "none-observed"),
                ("semantic-environment", "cleared-v1")
            ]);
        return new(sourcePaths, excluded, sources, trees, compilation, identity);
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

    private static ProjectSemantics ValidateProjectFile(InputSnapshot snapshot,
        IReadOnlyDictionary<string, SnapshotFile> captured, CheckProject project,
        IReadOnlyList<string> configuredSourcePaths, string buildConfiguration)
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
            throw new EnumerationContextException("ENUMERATION_PROJECT_UNSUPPORTED",
                $"Configured project XML is invalid: {project.Project}.");
        var sdk = root.Attribute("Sdk")?.Value.Trim();
        if (!string.Equals(sdk, "Microsoft.NET.Sdk", StringComparison.Ordinal))
            throw new EnumerationContextException("ENUMERATION_SDK_UNSUPPORTED",
                $"Enumeration v1 supports only Microsoft.NET.Sdk projects: {project.Project}.");
        if (!project.TargetFramework.Equals("net10.0", StringComparison.Ordinal))
            throw new EnumerationContextException("ENUMERATION_FRAMEWORK_UNSUPPORTED",
                $"Enumeration v1 supports only exact net10.0 project contexts: {project.Project}.");
        if (root.Descendants().Any(element => element.Name.LocalName is "Import" or "Sdk"))
            throw new EnumerationContextException("ENUMERATION_IMPORT_UNSUPPORTED",
                $"Explicit MSBuild imports and nested SDK declarations are unsupported: {project.Project}.");
        var unsupportedProjectChild = root.Elements().FirstOrDefault(element =>
            element.Name.LocalName is not ("PropertyGroup" or "ItemGroup"));
        if (unsupportedProjectChild is not null)
            throw new EnumerationContextException("ENUMERATION_PROJECT_ELEMENT_UNSUPPORTED",
                $"Project element {unsupportedProjectChild.Name.LocalName} is unsupported in enumeration v1: {project.Project}.");
        RefuseApplicableDirectoryBuildFiles(captured, project.Project);
        RefuseHostAncestorBuildFiles(snapshot.OriginalRoot);
        if (root.Descendants().Any(element => element.Attribute("Condition") is not null))
            throw new EnumerationContextException("ENUMERATION_CONDITION_UNSUPPORTED",
                $"Conditional project evaluation is unsupported for strict enumeration: {project.Project}.");
        if (root.Descendants().Any(element => element.Name.LocalName is "ProjectReference" or
                "PackageReference" or "FrameworkReference" or "Reference" or "Using"))
            throw new EnumerationContextException("ENUMERATION_REFERENCE_UNSUPPORTED",
                $"Project/package reference semantic resolution is not supported in enumeration v1: {project.Project}.");
        var unsupportedItem = root.Descendants().Where(element => element.Parent?.Name.LocalName == "ItemGroup")
            .FirstOrDefault(element => element.Name.LocalName != "Compile");
        if (unsupportedItem is not null)
            throw new EnumerationContextException("ENUMERATION_ITEM_UNSUPPORTED",
                $"Project item {unsupportedItem.Name.LocalName} is unsupported in enumeration v1: {project.Project}.");
        var unsupportedProperty = root.Descendants().Where(element =>
                element.Parent?.Name.LocalName == "PropertyGroup")
            .FirstOrDefault(element => !SupportedProjectProperties.Contains(element.Name.LocalName));
        if (unsupportedProperty is not null)
            throw new EnumerationContextException("ENUMERATION_PROPERTY_UNSUPPORTED",
                $"Project property {unsupportedProperty.Name.LocalName} is unsupported in enumeration v1: {project.Project}.");
        if (SingleProperty(root, "DefaultItemExcludes") is not null)
            throw new EnumerationContextException("ENUMERATION_PROPERTY_UNSUPPORTED",
                $"DefaultItemExcludes is unsupported in enumeration v1: {project.Project}.");
        var disableFrameworkDefines = SingleProperty(root, "DisableImplicitFrameworkDefines");
        if (disableFrameworkDefines is not null &&
            (!bool.TryParse(disableFrameworkDefines, out var disabled) || disabled))
            throw new EnumerationContextException("ENUMERATION_PROPERTY_UNSUPPORTED",
                $"DisableImplicitFrameworkDefines is unsupported in enumeration v1: {project.Project}.");
        var disableConfigurationDefines = OptionalBooleanProperty(root,
            "DisableImplicitConfigurationDefines", project.Project);
        var disableDiagnosticTracing = OptionalBooleanProperty(root,
            "DisableDiagnosticTracing", project.Project);
        if (root.Descendants().Any(element => element.Name.LocalName == "Compile" &&
                (element.Attribute("Remove") is not null || element.Attribute("Update") is not null)))
            throw new EnumerationContextException("ENUMERATION_COMPILE_TRANSFORM_UNSUPPORTED",
                $"Compile Remove/Update transforms are unsupported in enumeration v1: {project.Project}.");
        var compileElements = root.Descendants().Where(element =>
            element.Name.LocalName == "Compile").ToArray();
        if (compileElements.Any(element => element.Attribute("Exclude") is not null))
            throw new EnumerationContextException("ENUMERATION_COMPILE_TRANSFORM_UNSUPPORTED",
                $"Compile Exclude transforms are unsupported in enumeration v1: {project.Project}.");
        var frameworks = root.Descendants().Where(element =>
            element.Name.LocalName is "TargetFramework" or "TargetFrameworks").ToArray();
        if (frameworks.Any(element => element.Name.LocalName == "TargetFrameworks") ||
            frameworks.Length != 1 || frameworks[0].Value.Trim() != project.TargetFramework)
            throw new EnumerationContextException("ENUMERATION_FRAMEWORK_UNSUPPORTED",
                $"Captured project target framework does not match strict configuration: {project.Project}.");
        var langVersion = SingleProperty(root, "LangVersion");
        if (langVersion is not null && langVersion is not ("14" or "14.0"))
            throw new EnumerationContextException("ENUMERATION_LANGUAGE_UNSUPPORTED",
                $"Captured project language version is unsupported: {langVersion}.");
        var nullable = (SingleProperty(root, "Nullable") ?? "disable").ToLowerInvariant();
        if (!nullable.Equals(project.Nullable, StringComparison.Ordinal))
            throw new EnumerationContextException("ENUMERATION_NULLABLE_MISMATCH",
                $"Captured project nullable context does not match strict configuration: {project.Project}.");
        var symbols = ResolvePreprocessorSymbols(root, project, buildConfiguration,
            disableConfigurationDefines, disableDiagnosticTracing);

        var compileInventory = new HashSet<string>(StringComparer.Ordinal);
        var defaultItems = SingleProperty(root, "EnableDefaultItems");
        if (defaultItems is not null && !bool.TryParse(defaultItems, out _))
            throw new EnumerationContextException("ENUMERATION_COMPILE_INVENTORY_UNSUPPORTED",
                $"EnableDefaultItems must be true or false: {project.Project}.");
        var defaultCompile = SingleProperty(root, "EnableDefaultCompileItems");
        if (defaultCompile is not null && !bool.TryParse(defaultCompile, out _))
            throw new EnumerationContextException("ENUMERATION_COMPILE_INVENTORY_UNSUPPORTED",
                $"EnableDefaultCompileItems must be true or false: {project.Project}.");
        if (!string.Equals(defaultItems, "false", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(defaultCompile, "false", StringComparison.OrdinalIgnoreCase))
        {
            var projectDirectory = RepositoryDirectory(project.Project);
            foreach (var source in captured.Keys.Where(path =>
                         path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) &&
                         IsUnderDirectory(path, projectDirectory) && !HasHiddenSegment(path, projectDirectory)))
                compileInventory.Add(source);
        }
        foreach (var element in compileElements)
        {
            var include = element.Attribute("Include")?.Value;
            if (string.IsNullOrWhiteSpace(include))
                throw new EnumerationContextException("ENUMERATION_COMPILE_INVENTORY_UNSUPPORTED",
                    $"Compile items require one static Include in enumeration v1: {project.Project}.");
            var resolved = ResolveStaticCompilePath(snapshot, project.Project, include);
            if (!captured.ContainsKey(resolved))
                throw new EnumerationContextException("ENUMERATION_COMPILE_INVENTORY_UNSUPPORTED",
                    $"Compile input is absent from the frozen snapshot: {resolved}.");
            compileInventory.Add(resolved);
        }
        var configured = configuredSourcePaths.ToHashSet(StringComparer.Ordinal);
        if (!compileInventory.SetEquals(configured))
        {
            var missing = compileInventory.Except(configured, StringComparer.Ordinal).Order().FirstOrDefault();
            var extra = configured.Except(compileInventory, StringComparer.Ordinal).Order().FirstOrDefault();
            throw new EnumerationContextException("ENUMERATION_COMPILE_INVENTORY_MISMATCH",
                $"Configured source inventory does not equal the captured static Compile inventory for " +
                $"{project.Project}; missing={missing ?? "<none>"}, extra={extra ?? "<none>"}.");
        }
        var implicitUsings = SingleProperty(root, "ImplicitUsings")?.ToLowerInvariant();
        var implicitEnabled = implicitUsings is not null && implicitUsings is not ("disable" or "false");
        if (implicitUsings is not null && implicitUsings is not ("enable" or "true" or "disable" or "false"))
            throw new EnumerationContextException("ENUMERATION_IMPLICIT_USINGS_UNSUPPORTED",
                $"ImplicitUsings must be enabled or disabled explicitly: {project.Project}.");
        var outputType = (SingleProperty(root, "OutputType") ?? "Library").ToLowerInvariant();
        var outputKind = outputType switch
        {
            "library" => OutputKind.DynamicallyLinkedLibrary,
            "exe" => OutputKind.ConsoleApplication,
            "winexe" => OutputKind.WindowsApplication,
            _ => throw new EnumerationContextException("ENUMERATION_OUTPUT_TYPE_UNSUPPORTED",
                $"Unsupported OutputType {outputType}: {project.Project}.")
        };
        var nullableContext = nullable switch
        {
            "enable" => NullableContextOptions.Enable,
            "disable" => NullableContextOptions.Disable,
            "annotations" => NullableContextOptions.Annotations,
            "warnings" => NullableContextOptions.Warnings,
            _ => throw new EnumerationContextException("ENUMERATION_NULLABLE_MISMATCH",
                $"Unsupported nullable context {nullable}: {project.Project}.")
        };
        return new(outputKind, implicitEnabled, nullableContext, symbols);
    }

    private static void RefuseApplicableDirectoryBuildFiles(
        IReadOnlyDictionary<string, SnapshotFile> captured, string projectPath)
    {
        var directory = RepositoryDirectory(projectPath);
        while (true)
        {
            foreach (var name in new[] { "Directory.Build.props", "Directory.Build.targets" })
            {
                var path = directory.Length == 0 ? name : directory + "/" + name;
                if (captured.ContainsKey(path))
                    throw new EnumerationContextException("ENUMERATION_INHERITED_BUILD_UNSUPPORTED",
                        $"Captured inherited build configuration is unsupported in enumeration v1: {path}.");
            }
            if (directory.Length == 0) break;
            directory = RepositoryDirectory(directory);
        }
    }

    private static void RefuseHostAncestorBuildFiles(string repositoryRoot)
    {
        var directory = Directory.GetParent(Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(repositoryRoot)));
        string[] names =
        [
            "Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props",
            "Directory.Build.rsp", "MSBuild.rsp", ".globalconfig", "global.json"
        ];
        while (directory is not null)
        {
            foreach (var name in names)
            {
                var path = Path.Combine(directory.FullName, name);
                if (File.Exists(path) || Directory.Exists(path))
                    throw new EnumerationContextException("ENUMERATION_ANCESTOR_BUILD_UNSUPPORTED",
                        $"Build control above the repository root is unsupported in enumeration v1: {path}.");
            }
            directory = directory.Parent;
        }
    }

    private static IReadOnlyList<string> ResolvePreprocessorSymbols(XElement root,
        CheckProject project, string buildConfiguration, bool disableConfigurationDefines,
        bool disableDiagnosticTracing)
    {
        const string inheritedPrefix = "$(DefineConstants)";
        var property = SingleProperty(root, "DefineConstants");
        var retainsConfigurationSymbols = property is null;
        string[] declared;
        if (property is null)
        {
            declared = [];
        }
        else if (property.StartsWith(inheritedPrefix, StringComparison.Ordinal))
        {
            retainsConfigurationSymbols = true;
            var suffix = property[inheritedPrefix.Length..];
            if (suffix.Length > 0 && suffix[0] != ';')
                throw new EnumerationContextException("ENUMERATION_SYMBOL_UNSUPPORTED",
                    $"DefineConstants must use an exact $(DefineConstants) prefix: {project.Project}.");
            declared = SplitSymbols(suffix.TrimStart(';'));
        }
        else
        {
            if (property.Contains("$(", StringComparison.Ordinal) ||
                property.Contains("@(", StringComparison.Ordinal) ||
                property.Contains("%(", StringComparison.Ordinal))
                throw new EnumerationContextException("ENUMERATION_SYMBOL_UNSUPPORTED",
                    $"Dynamic DefineConstants are unsupported in enumeration v1: {project.Project}.");
            declared = SplitSymbols(property);
        }
        var expected = project.DefineConstants.OrderBy(value => value, StringComparer.Ordinal).ToArray();
        if (!declared.OrderBy(value => value, StringComparer.Ordinal).SequenceEqual(expected,
                StringComparer.Ordinal))
            throw new EnumerationContextException("ENUMERATION_SYMBOL_MISMATCH",
                $"Captured project DefineConstants do not match strict configuration: {project.Project}.");
        return FrameworkSymbols()
            .Concat(retainsConfigurationSymbols && !disableDiagnosticTracing ? ["TRACE"] : [])
            .Concat(disableConfigurationDefines ? [] : [buildConfiguration.ToUpperInvariant()])
            .Concat(declared).Distinct(StringComparer.Ordinal).OrderBy(value => value,
                StringComparer.Ordinal).ToArray();

        static string[] SplitSymbols(string value) => value.Split(';',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static string ResolveStaticCompilePath(InputSnapshot snapshot, string projectPath, string include)
    {
        if (include.Contains(';') || include.IndexOfAny(['*', '?']) >= 0 ||
            include.Contains("$(", StringComparison.Ordinal) || include.Contains("@(", StringComparison.Ordinal) ||
            Path.IsPathRooted(include))
            throw new EnumerationContextException("ENUMERATION_COMPILE_INVENTORY_UNSUPPORTED",
                $"Dynamic or multi-value Compile Include is unsupported in enumeration v1: {include}.");
        var projectDirectory = Path.GetDirectoryName(CapturedPath(snapshot, projectPath))!;
        var full = Path.GetFullPath(include.Replace('/', Path.DirectorySeparatorChar), projectDirectory);
        var relative = Path.GetRelativePath(snapshot.CaptureRoot, full).Replace('\\', '/');
        try { return SnapshotInputPolicy.NormalizeRelative(relative); }
        catch (SnapshotCaptureException ex)
        {
            throw new EnumerationContextException("ENUMERATION_COMPILE_INVENTORY_UNSUPPORTED",
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

    private static bool HasHiddenSegment(string path, string projectDirectory)
    {
        var relative = projectDirectory.Length == 0 ? path : path[(projectDirectory.Length + 1)..];
        return relative.Split('/').SkipLast(1)
            .Any(segment => segment.Length > 0 && segment[0] == '.');
    }

    private static string? SingleProperty(XElement root, string name)
    {
        var values = root.Descendants().Where(element => element.Name.LocalName == name).ToArray();
        if (values.Length > 1)
            throw new EnumerationContextException("ENUMERATION_PROJECT_UNSUPPORTED",
                $"Multiple {name} properties are unsupported.");
        return values.SingleOrDefault()?.Value.Trim();
    }

    private static bool OptionalBooleanProperty(XElement root, string name, string projectPath)
    {
        var value = SingleProperty(root, name);
        if (value is null) return false;
        if (bool.TryParse(value, out var parsed)) return parsed;
        throw new EnumerationContextException("ENUMERATION_PROPERTY_UNSUPPORTED",
            $"{name} must be true or false: {projectPath}.");
    }

    private static ReferenceSet PlatformReferences(string? sdkVersion)
    {
        var key = sdkVersion ?? "runtime:" + Path.GetFileName(Path.TrimEndingDirectorySeparator(
            System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory()));
        return ReferenceSetCache.GetOrAdd(key, _ => new(() => CreatePlatformReferences(sdkVersion),
            LazyThreadSafetyMode.ExecutionAndPublication)).Value;
    }

    private static ReferenceSet CreatePlatformReferences(string? sdkVersion)
    {
        var runtimeDirectory = Path.TrimEndingDirectorySeparator(
            System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory());
        var runtimeVersion = Path.GetFileName(runtimeDirectory);
        var frameworkDirectory = Directory.GetParent(runtimeDirectory);
        var sharedDirectory = frameworkDirectory?.Parent;
        var dotnetDirectory = sharedDirectory?.Parent;
        var dotnetRoot = dotnetDirectory?.FullName;
        var packRoot = dotnetRoot is null ? null : Path.Combine(dotnetRoot, "packs", "Microsoft.NETCore.App.Ref");
        if (packRoot is null || !Directory.Exists(packRoot))
            throw new EnumerationContextException("ENUMERATION_REFERENCE_PACK_UNAVAILABLE",
                "The pinned Microsoft.NETCore.App.Ref pack is unavailable.");
        var targetingPackVersion = sdkVersion is null
            ? runtimeVersion
            : ResolveTargetingPackVersion(dotnetRoot!, sdkVersion);
        var referenceDirectory = Path.Combine(packRoot, targetingPackVersion, "ref", "net10.0");
        if (!Directory.Exists(referenceDirectory))
            throw new EnumerationContextException("ENUMERATION_REFERENCE_PACK_UNAVAILABLE",
                $"The resolved SDK {sdkVersion ?? "<runtime-fallback>"} requires Microsoft.NETCore.App.Ref " +
                $"{targetingPackVersion}, but its exact net10.0 reference pack is unavailable.");
        var paths = Directory.EnumerateFiles(referenceDirectory, "*.dll", SearchOption.TopDirectoryOnly)
            .OrderBy(Path.GetFileName, StringComparer.Ordinal).ToArray();
        if (paths.Length == 0)
            throw new EnumerationContextException("ENUMERATION_REFERENCE_PACK_UNAVAILABLE",
                "The net10.0 reference assembly set is empty.");
        var identity = MutationIdentity.ComputeDigest("net10-reference-set-v1",
            new[] { ("sdk-version", sdkVersion ?? "runtime-fallback"),
                ("targeting-pack-version", targetingPackVersion) }.Concat(paths.SelectMany((path, index) =>
                new[]
                {
                    ($"name-{index:D8}", Path.GetFileName(path)),
                    ($"sha256-{index:D8}", Convert.ToHexString(
                        System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant())
                })).ToArray());
        return new(paths.Select(path => MetadataReference.CreateFromFile(path)).ToArray(), identity);
    }

    private static string ResolveTargetingPackVersion(string dotnetRoot, string sdkVersion)
    {
        var sdkDirectory = Path.Combine(dotnetRoot, "sdk", sdkVersion);
        var bundledVersions = Path.Combine(sdkDirectory, "Microsoft.NETCoreSdk.BundledVersions.props");
        if (!File.Exists(bundledVersions))
            throw new EnumerationContextException("ENUMERATION_REFERENCE_PACK_UNAVAILABLE",
                $"The resolved SDK directory is unavailable: {sdkDirectory}.");
        XDocument document;
        try { document = XDocument.Load(bundledVersions, LoadOptions.None); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            throw new EnumerationContextException("ENUMERATION_REFERENCE_PACK_UNAVAILABLE",
                $"The resolved SDK targeting-pack manifest could not be read: {bundledVersions}.", ex);
        }
        var declaredSdk = document.Descendants().FirstOrDefault(element =>
            element.Name.LocalName == "NETCoreSdkVersion")?.Value.Trim();
        var targetingPack = document.Descendants().FirstOrDefault(element =>
            element.Name.LocalName == "BundledNETCoreAppPackageVersion")?.Value.Trim();
        if (!string.Equals(declaredSdk, sdkVersion, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(targetingPack) || targetingPack.Any(character =>
                !char.IsAsciiDigit(character) && character is not ('.' or '-' or '+')))
            throw new EnumerationContextException("ENUMERATION_REFERENCE_PACK_UNAVAILABLE",
                $"The resolved SDK targeting-pack manifest does not bind SDK {sdkVersion} to one pack.");
        return targetingPack;
    }

    private static IEnumerable<string> FrameworkSymbols()
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

    private static string ResolveBuildConfiguration(CheckConfiguration configuration, CheckProject project)
    {
        var suiteIds = project.TestSuites.ToHashSet(StringComparer.Ordinal);
        var configurations = configuration.TestSuites.Where(suite => suiteIds.Contains(suite.Id))
            .Select(suite => suite.Configuration).Distinct(StringComparer.Ordinal).ToArray();
        if (configurations.Length != 1 || configurations[0] is not ("Debug" or "Release"))
            throw new EnumerationContextException("ENUMERATION_CONFIGURATION_UNSUPPORTED",
                $"Project {project.Project} requires one shared Debug or Release suite configuration.");
        return configurations[0];
    }

    private static string ReadCapturedText(InputSnapshot snapshot, string path)
    {
        using var stream = new FileStream(CapturedPath(snapshot, path), FileMode.Open, FileAccess.Read,
            FileShare.Read, 64 * 1024, FileOptions.SequentialScan);
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true), true);
        return reader.ReadToEnd();
    }

    private static string CapturedPath(InputSnapshot snapshot, string path) => Path.Combine(
        snapshot.CaptureRoot, path.Replace('/', Path.DirectorySeparatorChar));

    private static string FormatDiagnostic(Diagnostic diagnostic)
    {
        var line = diagnostic.Location.GetLineSpan().StartLinePosition;
        return $"{diagnostic.Id} at {line.Line + 1}:{line.Character + 1}: {Bound(diagnostic.GetMessage())}";
    }

    private static StrictMutationEnumerationResult Refused(string code, string message) =>
        new([], [new(code, Bound(message))], false, null);

    private static string Bound(string value) => value.Length <= 1024 ? value : value[..1024];

    private static StringComparison HostPathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    private sealed record ProjectContext(IReadOnlyList<string> SourcePaths,
        IReadOnlySet<string> ExcludedPaths, IReadOnlyDictionary<string, string> Sources,
        IReadOnlyDictionary<string, SyntaxTree> Trees, CSharpCompilation Compilation, string Identity);
    private sealed record ProjectSemantics(OutputKind OutputKind, bool ImplicitUsings,
        NullableContextOptions Nullable, IReadOnlyList<string> PreprocessorSymbols);
    private sealed record ReferenceSet(IReadOnlyList<MetadataReference> References, string Identity);
    private sealed class EnumerationContextException : Exception
    {
        public EnumerationContextException(string code, string message, Exception? inner = null) :
            base(message, inner) => Code = code;
        public string Code { get; }
    }
}
