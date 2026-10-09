using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Mutate4CSharp;

internal static class ScopePlanner
{
    internal sealed record ExplicitScopePlan(ScopePlan Plan, byte[]? ConfigurationBytes);

    internal const int MaxScopeItems = 100_000;
    public static async Task<ScopePlan> PlanGitAsync(InputSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        var changes = await GitChangePlanner.DiscoverAsync(snapshot, cancellationToken);
        var configuration = LoadCapturedConfiguration(snapshot);
        return Build("base", snapshot.CaptureRoot, snapshot.OriginalRoot, changes.BaseCommit,
            changes.Files, configuration);
    }

    public static async Task<ExplicitScopePlan> PlanExplicitAsync(IReadOnlyList<string> inputs, string root,
        CancellationToken cancellationToken)
    {
        var fullRoot = Path.GetFullPath(root);
        var configurationPath = Path.Combine(fullRoot, "mutate4csharp.json");
        var configurationBytes = File.Exists(configurationPath) ?
            await SnapshotCapture.ReadRegularInputAsync(configurationPath, "mutate4csharp.json",
                SnapshotCaptureOptions.Default.MaxFileBytes, cancellationToken) : null;
        var configuration = ScopeConfiguration.Load(fullRoot, configurationBytes);
        var changes = new List<GitFileChange>();
        var seen = new HashSet<string>(PathComparer);
        foreach (var input in inputs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fullPath = Path.GetFullPath(input, fullRoot);
            var relative = Path.GetRelativePath(fullRoot, fullPath).Replace('\\', '/');
            if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith("../", StringComparison.Ordinal))
                throw new ArgumentException($"Explicit input must be inside the planning root: {input}");
            relative = SnapshotInputPolicy.NormalizeRelative(relative);
            if (!seen.Add(relative)) continue;
            if (!relative.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || !File.Exists(fullPath))
                throw new ArgumentException($"Explicit input must be one existing .cs file: {input}");
            var bytes = await SnapshotCapture.ReadRegularInputAsync(fullPath, relative,
                SnapshotCaptureOptions.Default.MaxFileBytes, cancellationToken);
            changes.Add(new(ChangeKind.Explicit, null, relative, null, bytes));
        }
        var plan = Build("inputs", fullRoot, fullRoot, null, changes, configuration);
        foreach (var change in changes)
        {
            var currentPath = Path.Combine(fullRoot, change.CurrentPath!.Replace('/', Path.DirectorySeparatorChar));
            var current = await SnapshotCapture.ReadRegularInputAsync(currentPath, change.CurrentPath,
                SnapshotCaptureOptions.Default.MaxFileBytes, cancellationToken);
            if (!current.AsSpan().SequenceEqual(change.CurrentBytes))
                throw new SnapshotDivergedException($"Explicit input changed during planning: {change.CurrentPath}");
        }
        var currentConfiguration = File.Exists(configurationPath) ?
            await SnapshotCapture.ReadRegularInputAsync(configurationPath, "mutate4csharp.json",
                SnapshotCaptureOptions.Default.MaxFileBytes, cancellationToken) : null;
        if ((configurationBytes is null) != (currentConfiguration is null) ||
            configurationBytes is not null && !configurationBytes.AsSpan().SequenceEqual(currentConfiguration))
            throw new SnapshotDivergedException("mutate4csharp.json changed during explicit-input planning.");
        return new(plan, configurationBytes?.ToArray());
    }

    public static ScopePlan PlanCapturedExplicit(InputSnapshot snapshot, IReadOnlyList<string> inputs)
    {
        var captured = snapshot.Files.Where(file => file.Exists).ToDictionary(file => file.RelativePath,
            PathComparer);
        var changes = new List<GitFileChange>();
        var seen = new HashSet<string>(PathComparer);
        foreach (var input in inputs)
        {
            var fullPath = Path.GetFullPath(input);
            var relative = Path.GetRelativePath(snapshot.OriginalRoot, fullPath).Replace('\\', '/');
            relative = SnapshotInputPolicy.NormalizeRelative(relative);
            if (!relative.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException($"Explicit input must be one existing .cs file: {input}");
            if (!seen.Add(relative)) continue;
            if (!captured.TryGetValue(relative, out _))
                throw new SnapshotCaptureException($"Explicit input was not present in the immutable capture: {relative}");
            changes.Add(new(ChangeKind.Explicit, null, relative, null,
                File.ReadAllBytes(Path.Combine(snapshot.CaptureRoot,
                    relative.Replace('/', Path.DirectorySeparatorChar)))));
        }
        var configuration = LoadCapturedConfiguration(snapshot);
        return Build("inputs", snapshot.CaptureRoot, snapshot.OriginalRoot, null, changes, configuration);
    }

    private static ScopeConfiguration LoadCapturedConfiguration(InputSnapshot snapshot)
    {
        var config = snapshot.Files.SingleOrDefault(file => file.Exists &&
            file.RelativePath.Equals("mutate4csharp.json", StringComparison.OrdinalIgnoreCase));
        var bytes = config is null ? null : File.ReadAllBytes(Path.Combine(snapshot.CaptureRoot,
            config.RelativePath.Replace('/', Path.DirectorySeparatorChar)));
        return ScopeConfiguration.Load(snapshot.CaptureRoot, bytes);
    }

    private static ScopePlan Build(string selectionKind, string contentRoot, string displayRoot,
        string? baseCommit, IReadOnlyList<GitFileChange> changes, ScopeConfiguration configuration)
    {
        if (changes.Count > MaxScopeItems)
            throw new SnapshotLimitException($"Scope planning exceeded {MaxScopeItems} changed inputs.");
        _ = contentRoot;
        _ = displayRoot;
        var files = new List<ScopeFilePlan>();
        var exclusions = new List<ScopeExclusion>();
        var reasons = new List<EvaluationReason>();
        var productionPaths = new List<string>();
        foreach (var change in changes.OrderBy(item => item.CurrentPath ?? item.BasePath, StringComparer.Ordinal))
        {
            var path = change.CurrentPath ?? change.BasePath!;
            if (!path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            {
                exclusions.Add(new(path, IsExpansionInput(path) ? "SCOPE_EXPANSION_INPUT" :
                    "UNSUPPORTED_CHANGED_INPUT", IsExpansionInput(path)
                        ? "Changed non-C# input is represented through configured project expansion."
                        : "Changed input type is unsupported and blocks complete mutation scope.", false));
                continue;
            }
            var basePath = change.BasePath;
            var baseExcluded = basePath is null ? null : Exclusion(basePath, change.BaseBytes, configuration);
            var dispositionBytes = change.CurrentPath is null ? change.BaseBytes : change.CurrentBytes;
            var disposition = SourceDisposition(path, dispositionBytes, configuration);
            var retainsBaseRemoval = basePath is not null && baseExcluded is null &&
                (change.Kind == ChangeKind.Renamed || disposition is { Overridden: false });
            if (retainsBaseRemoval)
            {
                productionPaths.Add(basePath!);
                reasons.Add(new("COMPILE_ITEM_REMOVED",
                    change.Kind == ChangeKind.Renamed
                        ? $"{basePath} was removed by rename to {path}; expand the base-side configured project."
                        : $"{basePath} left production scope after becoming {disposition!.ReasonCode}; expand the base-side configured project."));
            }
            if (disposition is not null)
            {
                exclusions.Add(disposition);
                if (disposition.Overridden)
                    reasons.Add(new($"{disposition.ReasonCode}_INCLUDED",
                        $"{path} is included because its configured source exclusion was explicitly overridden."));
                else if (retainsBaseRemoval)
                    files.Add(new(path, basePath, change.Kind, [], [], true,
                        [new("COMPILE_ITEM_REMOVED", Bound(
                            $"{basePath} left production scope even though {path} is excluded as {disposition.ReasonCode}."))]));
                if (!disposition.Overridden) continue;
            }
            var comparison = DeclarationCatalog.Compare(path, change.BaseBytes, change.CurrentBytes);
            var fileReasons = comparison.Reasons.ToList();
            var expansion = comparison.RequiresProjectExpansion;
            if (ContainsConditionalDirectives(change.BaseBytes) ||
                ContainsConditionalDirectives(change.CurrentBytes))
            {
                expansion = true;
                fileReasons.Add(new("PREPROCESSOR_SCOPE_EXPANSION",
                    $"{path} contains conditional compilation directives; use the complete configured project context."));
            }
            if (change.Kind == ChangeKind.Renamed && comparison.Declarations.Count == 0 &&
                comparison.Removals.Count == 0)
            {
                expansion = true;
                fileReasons.Add(new("RENAME_PATH_RISK",
                    $"{change.BasePath} moved to {path}; namespace, build-path, and caller effects require project scope."));
            }
            if (comparison.Removals.Count > 0)
            {
                expansion = true;
                fileReasons.Add(new("REMOVAL_DEPENDENCY_RISK",
                    $"{path} removes declarations; unchanged callers were not proven correct."));
            }
            if (comparison.Declarations.Count > 0 || comparison.Removals.Count > 0 || expansion)
                productionPaths.Add(path);
            else
                fileReasons.Add(new("NO_SEMANTIC_DECLARATION_CHANGE",
                    $"{path} has no semantic change in a supported declaration."));
            files.Add(new(path, change.BasePath, change.Kind, comparison.Declarations,
                comparison.Removals, expansion, fileReasons.OrderBy(item => item.Code,
                    StringComparer.Ordinal).Select(reason => reason with { Message = Bound(reason.Message) }).ToArray()));
        }

        var ownership = ProjectOwnershipResolver.Resolve(configuration, productionPaths);
        reasons.AddRange(ownership.Reasons);
        var units = ownership.Units.ToDictionary(unit => unit.Project, StringComparer.Ordinal);
        AddNonProductionExpansions(changes, configuration, units, reasons);
        foreach (var unit in units.Values.ToArray())
        {
            var missing = new[] { unit.Project }.Concat(unit.Tests).Where(path =>
                !File.Exists(Path.Combine(contentRoot, path.Replace('/', Path.DirectorySeparatorChar)))).ToArray();
            if (missing.Length == 0) continue;
            units.Remove(unit.Project);
            reasons.Add(new("CONFIGURED_PATH_MISSING", Bound(
                $"Configured ownership for {unit.Project} references missing captured path(s): {string.Join(", ", missing)}.")));
        }
        foreach (var file in files.Where(file => file.RequiresProjectExpansion))
        {
            foreach (var unit in units.Values.Where(unit => unit.Inputs.Contains(file.Path, StringComparer.Ordinal) ||
                         file.BasePath is not null && unit.Inputs.Contains(file.BasePath, StringComparer.Ordinal)).ToArray())
                units[unit.Project] = unit with { Expansion = "FULL_PROJECT" };
        }
        foreach (var unsupported in exclusions.Where(item => item.ReasonCode == "UNSUPPORTED_CHANGED_INPUT"))
            reasons.Add(new("UNSUPPORTED_CHANGED_INPUT",
                $"{unsupported.Path} changed but has no supported mutation-scope mapping."));
        if (units.Count == 0 && files.All(file => file.Declarations.Count == 0 &&
                                          file.Removals.Count == 0 && !file.RequiresProjectExpansion) &&
            reasons.All(reason => IsBlockingCode(reason.Code) == false))
            reasons.Add(new("NO_APPLICABLE_PRODUCTION_CHANGES",
                "No eligible changed production C# source or configured project expansion was found."));
        foreach (var fileReason in files.SelectMany(file => file.Reasons))
            if (fileReason.Code is "UNSUPPORTED_SYNTAX" or "NO_SUPPORTED_DECLARATION") reasons.Add(fileReason);

        var orderedReasons = reasons.Select(reason => reason with { Message = Bound(reason.Message) }).Distinct()
            .OrderBy(reason => reason.Code, StringComparer.Ordinal)
            .ThenBy(reason => reason.Message, StringComparer.Ordinal).ToArray();
        if (files.Count > MaxScopeItems || exclusions.Count > MaxScopeItems ||
            units.Count > MaxScopeItems || orderedReasons.Length > MaxScopeItems ||
            files.Any(file => file.Declarations.Count > MaxScopeItems || file.Removals.Count > MaxScopeItems ||
                              file.Reasons.Count > MaxScopeItems) ||
            units.Values.Any(unit => unit.Tests.Count > MaxScopeItems || unit.Inputs.Count > MaxScopeItems))
            throw new SnapshotLimitException($"Scope plan arrays exceeded {MaxScopeItems} published items.");
        var blocking = orderedReasons.Any(reason => IsBlockingCode(reason.Code));
        return new("1", selectionKind, ".", baseCommit,
            files.OrderBy(file => file.Path, StringComparer.Ordinal).ToArray(),
            exclusions.OrderBy(item => item.Path, StringComparer.Ordinal).ToArray(),
            units.Values.OrderBy(unit => unit.Project, StringComparer.Ordinal).ToArray(),
            orderedReasons, !blocking);
    }

    private static void AddNonProductionExpansions(IReadOnlyList<GitFileChange> changes,
        ScopeConfiguration configuration, Dictionary<string, ProjectScopeUnit> units,
        List<EvaluationReason> reasons)
    {
        foreach (var change in changes)
        {
            var paths = new[] { change.BasePath, change.CurrentPath }.OfType<string>()
                .Distinct(StringComparer.Ordinal).OrderBy(path => path, StringComparer.Ordinal);
            foreach (var path in paths)
            {
                var isTestSource = path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) &&
                    IsTest(path, configuration);
                if (path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) && !isTestSource) continue;
                var owners = configuration.Projects.Where(project =>
                    !path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) &&
                    ProjectOwnershipResolver.IsUnderProject(path, project.Project) || project.Tests.Any(test =>
                        ProjectOwnershipResolver.IsUnderTestProject(path, test))).ToArray();
                if (path.Equals("mutate4csharp.json", StringComparison.OrdinalIgnoreCase) ||
                    path.EndsWith(".props", StringComparison.OrdinalIgnoreCase) ||
                    path.EndsWith(".targets", StringComparison.OrdinalIgnoreCase))
                    owners = configuration.Projects.ToArray();
                else if (path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) ||
                         path.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) ||
                         path.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase))
                    owners = configuration.Projects.Where(project => project.Project == path ||
                        project.Tests.Contains(path, StringComparer.Ordinal)).ToArray();
                if (owners.Length == 0 && (IsExpansionInput(path) || isTestSource))
                {
                    reasons.Add(new("UNMAPPED_PROJECT", isTestSource
                        ? $"No configured production project maps changed test source {path}; add its test project to mutate4csharp.json."
                        : $"No configured project maps changed build input {path}; add it to mutate4csharp.json."));
                    continue;
                }
                foreach (var owner in owners)
                {
                    if (!units.TryGetValue(owner.Project, out var unit))
                        unit = new(owner.Project, owner.Tests, [], "FULL_PROJECT");
                    else unit = unit with { Expansion = "FULL_PROJECT" };
                    units[owner.Project] = unit;
                    reasons.Add(new("NON_PRODUCTION_CHANGE_EXPANSION",
                        $"{path} affects configured project {owner.Project}; the production project scope is expanded."));
                }
            }
        }
    }

    private static ScopeExclusion? Exclusion(string path, byte[]? currentBytes,
        ScopeConfiguration configuration) => SourceDisposition(path, currentBytes, configuration) is
        { Overridden: false } exclusion ? exclusion : null;

    private static ScopeExclusion? SourceDisposition(string path, byte[]? currentBytes,
        ScopeConfiguration configuration)
    {
        var configured = configuration.Exclusions.FirstOrDefault(exclusion =>
            ProjectOwnershipResolver.GlobMatches(path, exclusion.Path));
        if (configured is not null)
            return new(path, "CONFIGURED_EXCLUSION",
                Bound($"Configured exclusion '{configured.Path}': {configured.Reason}"), false);
        var generated = IsGenerated(path, currentBytes);
        var test = IsTest(path, configuration);
        if (generated && !configuration.IncludeGenerated)
            return new(path, "GENERATED_SOURCE",
                "Generated C# source is excluded; set includeGenerated=true to override.", false);
        if (test && !configuration.IncludeTests)
            return new(path, "TEST_SOURCE",
                "Test C# source is excluded; set includeTests=true to override.", false);
        if (test)
            return new(path, "TEST_SOURCE", generated
                ? "Generated test C# source was included by includeGenerated=true and includeTests=true."
                : "Test C# source was included by includeTests=true.", true);
        if (generated)
            return new(path, "GENERATED_SOURCE",
                "Generated C# source was included by includeGenerated=true.", true);
        return null;
    }

    private static bool IsGenerated(string path, byte[]? bytes)
    {
        var name = Path.GetFileName(path);
        if (name.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(".generated.cs", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(".designer.cs", StringComparison.OrdinalIgnoreCase)) return true;
        if (bytes is null) return false;
        var source = new UTF8Encoding(false, true).GetString(bytes);
        return IsGeneratedSource(path, source);
    }

    internal static bool IsGeneratedSource(string path, string source)
    {
        var name = Path.GetFileName(path);
        if (name.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(".generated.cs", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(".designer.cs", StringComparison.OrdinalIgnoreCase)) return true;
        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        return root.GetLeadingTrivia().Where(trivia => trivia.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.SingleLineCommentTrivia) ||
                trivia.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.MultiLineCommentTrivia) ||
                trivia.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.SingleLineDocumentationCommentTrivia) ||
                trivia.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.MultiLineDocumentationCommentTrivia))
            .Any(trivia => trivia.ToFullString().Contains("<auto-generated", StringComparison.OrdinalIgnoreCase));
    }

    private static bool ContainsConditionalDirectives(byte[]? bytes)
    {
        if (bytes is null) return false;
        var source = new UTF8Encoding(false, true).GetString(bytes);
        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        return root.DescendantTrivia(descendIntoTrivia: true).Any(trivia => trivia.Kind() is
            SyntaxKind.IfDirectiveTrivia or SyntaxKind.ElifDirectiveTrivia or
            SyntaxKind.ElseDirectiveTrivia or SyntaxKind.EndIfDirectiveTrivia);
    }

    private static bool IsTest(string path, ScopeConfiguration configuration) =>
        IsTestSource(path, configuration.Projects.SelectMany(project => project.Tests));

    internal static bool IsTestSource(string path, IEnumerable<string> mappedTestProjects)
    {
        var parts = path.Split('/');
        return parts.Any(part => part.Equals("test", StringComparison.OrdinalIgnoreCase) ||
                                 part.Equals("tests", StringComparison.OrdinalIgnoreCase)) ||
               Path.GetFileNameWithoutExtension(path).EndsWith(".Tests", StringComparison.OrdinalIgnoreCase) ||
               mappedTestProjects.Any(test => ProjectOwnershipResolver.IsUnderTestProject(path, test));
    }

    internal static bool IsBlockingCode(string code) => code is "AMBIGUOUS_PROJECT_OWNERSHIP" or
        "UNMAPPED_PROJECT" or "CONFIGURED_PATH_MISSING" or "UNSUPPORTED_SYNTAX" or
        "NO_SUPPORTED_DECLARATION" or "UNSUPPORTED_CHANGED_INPUT";

    private static bool IsExpansionInput(string path) =>
        path.Equals("mutate4csharp.json", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".props", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".targets", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase);

    private static string Bound(string value)
    {
        const int maxLength = 1024;
        if (value.Length <= maxLength) return value;
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            Encoding.UTF8.GetBytes(value)))[..16].ToLowerInvariant();
        var suffix = $" … [sha256:{hash}]";
        return EvaluationTextBounds.Prefix(value, maxLength - suffix.Length) + suffix;
    }

    private static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}
