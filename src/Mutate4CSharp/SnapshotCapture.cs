using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Microsoft.Win32.SafeHandles;

namespace Mutate4CSharp;

internal static class SnapshotCapture
{
    private static readonly string[] InheritedBuildInputNames =
    [
        "Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props",
        "Directory.Build.rsp", "Directory.Solution.props", "Directory.Solution.targets",
        "NuGet.Config", "global.json", ".editorconfig", ".globalconfig"
    ];

    internal sealed record Inventory(IReadOnlyList<InventoryEntry> Entries, string BaseCommit,
        string RequestedBaseRevision, string IndexFingerprint, IReadOnlySet<string> ProjectDirectories,
        IReadOnlyList<IgnoredEntry> IgnoredEntries, IReadOnlyList<string> ExcludedEntries);
    internal sealed record InventoryEntry(string RelativePath, bool IsTracked, bool Exists, string? GitMode);
    internal sealed record IgnoredEntry(string RelativePath, bool IsDirectory);

    public static async Task<InputSnapshot> CaptureAsync(string root, string? baseRevision,
        IReadOnlyList<string> requiredInputs, SnapshotCaptureOptions options, CancellationToken cancellationToken)
    {
        ValidateOptions(options);
        var repositoryRoot = await ResolveRootAsync(root, options, cancellationToken);
        var inventory = await EnumerateAsync(repositoryRoot, baseRevision, options, cancellationToken);
        ValidateRequiredInputs(repositoryRoot, requiredInputs, inventory.ProjectDirectories);
        options.Hook?.Invoke(SnapshotCaptureStage.AfterInventory, null);
        OwnedDirectory? owner = null;
        try
        {
            try
            {
                owner = OwnedDirectory.Create(OwnedDirectory.PrivateParent("snapshots"), "capture");
                Directory.CreateDirectory(Path.Combine(owner.Root, "root"));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && ex is not SnapshotCaptureException)
            {
                throw new SnapshotEnvironmentException($"Snapshot staging could not be created: {ex.Message}", ex);
            }
            var captureRoot = Path.Combine(owner.Root, "root");
            var files = new List<SnapshotFile>(inventory.Entries.Count);
            long totalBytes = 0;
            foreach (var entry in inventory.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!entry.Exists)
                {
                    files.Add(new(entry.RelativePath, 0, MissingHash, entry.IsTracked, false));
                    continue;
                }
                var source = ToPath(repositoryRoot, entry.RelativePath);
                await using var input = OpenCapturedInput(source, entry.RelativePath, entry.GitMode);
                if (Path.GetFileName(entry.RelativePath).Equals("NuGet.Config", StringComparison.OrdinalIgnoreCase))
                    RejectNuGetCredentials(input, entry.RelativePath);
                var length = input.Length;
                if (length > options.MaxFileBytes)
                    throw new SnapshotLimitException($"Snapshot file exceeds the {options.MaxFileBytes}-byte limit: {entry.RelativePath}");
                checked { totalBytes += length; }
                if (totalBytes > options.MaxTotalBytes)
                    throw new SnapshotLimitException($"Snapshot exceeds the {options.MaxTotalBytes}-byte total limit.");
                var destination = ToPath(captureRoot, entry.RelativePath);
                try { Directory.CreateDirectory(Path.GetDirectoryName(destination)!); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    throw new SnapshotEnvironmentException($"Snapshot staging directory could not be created: {ex.Message}", ex);
                }
                var (actualLength, hash) = await CopyAndHashAsync(input, destination, options.MaxFileBytes, cancellationToken);
                if (actualLength != length)
                    throw new SnapshotDivergedException($"Input changed length while being captured: {entry.RelativePath}");
                files.Add(new(entry.RelativePath, actualLength, hash, entry.IsTracked, true));
                options.Hook?.Invoke(SnapshotCaptureStage.AfterFileCopied, entry.RelativePath);
            }
            ValidateRequiredPresence(repositoryRoot, requiredInputs, files);
            ValidateStaticDependencies(repositoryRoot, captureRoot, files, inventory.ProjectDirectories,
                inventory.IgnoredEntries, inventory.ExcludedEntries);
            options.Hook?.Invoke(SnapshotCaptureStage.BeforeCaptureValidation, null);
            var temporary = new InputSnapshot(repositoryRoot, captureRoot,
                CreateIdentity(inventory, files, totalBytes), files, owner, inventory, options);
            await temporary.ValidateOriginalAsync(cancellationToken);
            return temporary;
        }
        catch (Exception captureFailure)
        {
            if (owner is not null)
            {
                try { await owner.DisposeAsync(); }
                catch (Exception cleanupFailure)
                {
                    throw new SnapshotCleanupException(captureFailure, cleanupFailure);
                }
            }
            throw;
        }
    }

    internal static async Task<string> FindRepositoryRootAsync(string path, CancellationToken cancellationToken,
        SnapshotCaptureOptions? options = null)
    {
        var full = Path.GetFullPath(path);
        var start = Directory.Exists(full) ? full : Path.GetDirectoryName(full);
        if (start is null || !Directory.Exists(start))
            throw new SnapshotCaptureException($"Cannot locate a repository for input: {path}");
        var resolved = (await RunGitAsync(start, ["rev-parse", "--show-toplevel"],
            options ?? SnapshotCaptureOptions.Default, cancellationToken)).Trim();
        return Path.GetFullPath(resolved);
    }

    internal static async Task ValidateOriginalAsync(InputSnapshot snapshot, Inventory expected,
        SnapshotCaptureOptions options, CancellationToken cancellationToken)
    {
        var current = await EnumerateAsync(snapshot.OriginalRoot, expected.RequestedBaseRevision, options,
            cancellationToken);
        if (!string.Equals(current.BaseCommit, expected.BaseCommit, StringComparison.Ordinal))
            throw new SnapshotDivergedException("The requested base revision moved after snapshot enumeration.");
        if (!string.Equals(current.IndexFingerprint, expected.IndexFingerprint, StringComparison.Ordinal) ||
            !current.Entries.SequenceEqual(expected.Entries) ||
            !current.IgnoredEntries.SequenceEqual(expected.IgnoredEntries) ||
            !current.ExcludedEntries.SequenceEqual(expected.ExcludedEntries))
            throw new SnapshotDivergedException("Git inventory or index changed after snapshot enumeration.");

        var entriesByPath = expected.Entries.ToDictionary(entry => entry.RelativePath, StringComparer.Ordinal);
        foreach (var file in snapshot.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = ToPath(snapshot.OriginalRoot, file.RelativePath);
            if (!file.Exists)
            {
                if (File.Exists(path) || Directory.Exists(path))
                    throw new SnapshotDivergedException($"Previously deleted input reappeared: {file.RelativePath}");
                continue;
            }
            if (!File.Exists(path)) throw new SnapshotDivergedException($"Captured input disappeared: {file.RelativePath}");
            options.Hook?.Invoke(SnapshotCaptureStage.BeforeOriginalFileHashed, file.RelativePath);
            var (length, hash) = await HashFileAsync(path, file.RelativePath,
                entriesByPath[file.RelativePath].GitMode, options.MaxFileBytes, cancellationToken);
            if (length != file.Length || !string.Equals(hash, file.Sha256, StringComparison.Ordinal))
                throw new SnapshotDivergedException($"Captured input content changed: {file.RelativePath}");
        }
    }

    private static async Task<Inventory> EnumerateAsync(string root, string? baseRevision,
        SnapshotCaptureOptions options, CancellationToken cancellationToken)
    {
        var revision = string.IsNullOrWhiteSpace(baseRevision) ? "HEAD" : baseRevision;
        var baseCommit = (await RunGitAsync(root, ["rev-parse", "--verify", revision + "^{commit}"], options,
            cancellationToken)).Trim();
        if (baseCommit.Length != 40 && baseCommit.Length != 64)
            throw new SnapshotCaptureException("Git did not resolve the base to a full commit ID.");
        var listed = SplitNull(await RunGitAsync(root,
            ["ls-files", "-z", "--cached", "--others", "--exclude-standard"], options, cancellationToken));
        var ignored = SplitNull(await RunGitAsync(root,
                ["ls-files", "-z", "--others", "--ignored", "--exclude-standard", "--directory",
                    "--no-empty-directory"], options, cancellationToken))
            .Select(raw => new IgnoredEntry(SnapshotInputPolicy.NormalizeRelative(raw.TrimEnd('/')),
                raw.EndsWith("/", StringComparison.Ordinal)))
            .OrderBy(entry => entry.RelativePath, StringComparer.Ordinal)
            .ToArray();
        var tracked = SplitNull(await RunGitAsync(root, ["ls-files", "-z", "--cached"], options, cancellationToken))
            .ToHashSet(StringComparer.Ordinal);
        var flags = ParseFlags(await RunGitAsync(root, ["ls-files", "-z", "-v", "--cached"], options,
            cancellationToken));
        var stageOutput = await RunGitAsync(root, ["ls-files", "-z", "--stage"], options, cancellationToken);
        var modes = ParseModes(stageOutput);
        var projectDirectories = listed.Select(SnapshotInputPolicy.NormalizeRelative)
            .Where(path => Path.GetExtension(path).Equals(".csproj", StringComparison.OrdinalIgnoreCase))
            .Select(path => Path.GetDirectoryName(path)?.Replace('\\', '/') ?? string.Empty)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (ignored.Length > options.MaxFiles)
            throw new SnapshotLimitException(
                $"Ignored inventory contains {ignored.Length} entries; limit is {options.MaxFiles}.");
        ValidateIgnoredProjectInputs(root, ignored, projectDirectories, options);
        var entries = new List<InventoryEntry>();
        var excludedEntries = new List<string>();
        var portableNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in listed.OrderBy(value => value, StringComparer.Ordinal))
        {
            var relative = SnapshotInputPolicy.NormalizeRelative(raw);
            var isTracked = tracked.Contains(raw);
            if (SnapshotInputPolicy.IsExcluded(relative, projectDirectories) || SnapshotInputPolicy.IsSecret(relative) ||
                SnapshotInputPolicy.IsAdditionalExcluded(relative, options))
            {
                excludedEntries.Add(relative);
                continue;
            }
            if (!portableNames.Add(relative))
                throw new SnapshotCaptureException($"Case-colliding snapshot paths are unsupported: {relative}");
            if (SnapshotInputPolicy.Depth(relative) > options.MaxDepth)
                throw new SnapshotLimitException($"Snapshot path exceeds depth {options.MaxDepth}: {relative}");
            if (flags.TryGetValue(raw, out var flag) && char.ToUpperInvariant(flag) == 'S')
                throw new SnapshotCaptureException($"Sparse/skip-worktree input is unsupported: {relative}");
            modes.TryGetValue(raw, out var mode);
            if (mode == "160000") throw new SnapshotCaptureException($"Git submodule input is unsupported: {relative}");
            var path = ToPath(root, relative);
            var exists = File.Exists(path);
            if (!exists && !isTracked)
                throw new SnapshotDivergedException($"Untracked input disappeared during enumeration: {relative}");
            if (Directory.Exists(path)) throw new SnapshotCaptureException($"Non-file Git input is unsupported: {relative}");
            entries.Add(new(relative, isTracked, exists, mode));
        }
        if (entries.Count > options.MaxFiles)
            throw new SnapshotLimitException($"Snapshot contains {entries.Count} files; limit is {options.MaxFiles}.");
        var indexFingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(stageOutput))).ToLowerInvariant();
        return new(entries, baseCommit, revision, indexFingerprint, projectDirectories, ignored,
            excludedEntries.OrderBy(path => path, StringComparer.Ordinal).ToArray());
    }

    private static void ValidateIgnoredProjectInputs(string root, IReadOnlyList<IgnoredEntry> ignored,
        IReadOnlySet<string> projectDirectories, SnapshotCaptureOptions options)
    {
        foreach (var entry in ignored)
        {
            var relative = entry.RelativePath;
            if (!projectDirectories.Any(projectDirectory => IsWithinProject(relative, projectDirectory))) continue;
            if (SnapshotInputPolicy.IsExcluded(relative, projectDirectories) || SnapshotInputPolicy.IsSecret(relative))
                continue;
            if (!entry.IsDirectory && SnapshotInputPolicy.IsAdditionalExcluded(relative, options)) continue;
            if (entry.IsDirectory && IgnoredDirectoryContainsOnlyAdditionalExclusions(root, relative,
                    projectDirectories, options)) continue;
            throw new SnapshotCaptureException(
                $"Gitignored input under a captured project is not captured and was not read: {relative}");
        }
    }

    private static bool IgnoredDirectoryContainsOnlyAdditionalExclusions(string root, string relativeDirectory,
        IReadOnlySet<string> projectDirectories, SnapshotCaptureOptions options)
    {
        var prefix = relativeDirectory.TrimEnd('/') + "/";
        if (options.ExcludedRelativePaths?.Any(path => path.StartsWith(prefix, StringComparison.Ordinal)) != true)
            return false;
        try
        {
            var files = Directory.EnumerateFiles(ToPath(root, relativeDirectory), "*", SearchOption.AllDirectories)
                .Take(options.MaxFiles == int.MaxValue ? int.MaxValue : options.MaxFiles + 1).ToArray();
            if (files.LongLength > options.MaxFiles)
                throw new SnapshotLimitException(
                    $"Ignored directory validation exceeds the {options.MaxFiles}-file limit: {relativeDirectory}");
            return files.All(path =>
                {
                    var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
                    return SnapshotInputPolicy.IsAdditionalExcluded(relative, options) ||
                           SnapshotInputPolicy.IsExcluded(relative, projectDirectories) ||
                           SnapshotInputPolicy.IsSecret(relative);
                });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException &&
                                   ex is not SnapshotCaptureException)
        {
            throw new SnapshotCaptureException(
                $"Ignored directory could not be validated without reading contents: {relativeDirectory}", ex);
        }
    }

    private static bool IsWithinProject(string relativePath, string projectDirectory) =>
        string.IsNullOrEmpty(projectDirectory) ||
        string.Equals(relativePath, projectDirectory.TrimEnd('/'), StringComparison.OrdinalIgnoreCase) ||
        relativePath.StartsWith(projectDirectory.TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase);

    private static async Task<string> ResolveRootAsync(string root, SnapshotCaptureOptions options,
        CancellationToken cancellationToken)
    {
        var requested = Path.GetFullPath(root);
        if (!Directory.Exists(requested)) throw new SnapshotCaptureException($"Snapshot root does not exist: {requested}");
        RejectPathLinks(requested);
        var repositoryRoot = await FindRepositoryRootAsync(requested, cancellationToken, options);
        if (!PathEquals(requested, repositoryRoot))
            throw new SnapshotCaptureException("Snapshot root must be the Git worktree root.");
        return repositoryRoot;
    }

    private static void ValidateRequiredInputs(string root, IReadOnlyList<string> requiredInputs,
        IReadOnlySet<string> projectDirectories)
    {
        foreach (var required in requiredInputs)
        {
            var path = Path.GetFullPath(required, root);
            EnsureWithin(root, path, "Required input");
            var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
            if (SnapshotInputPolicy.IsExcluded(relative, projectDirectories) || SnapshotInputPolicy.IsSecret(relative))
                throw new SnapshotCaptureException($"Required input is excluded or secret and was not read: {relative}");
            if (!File.Exists(path))
                throw new SnapshotCaptureException($"Required input does not exist or is not a regular file: {relative}");
            try
            {
                RejectPathLinks(path);
                using var readable = OpenRegularInput(path, relative, null);
            }
            catch (SnapshotDivergedException ex)
            {
                throw new SnapshotCaptureException($"Required input is inaccessible: {relative}", ex);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && ex is not SnapshotCaptureException)
            {
                throw new SnapshotCaptureException($"Required input is inaccessible: {relative}", ex);
            }
        }
    }

    private static void ValidateRequiredPresence(string root, IReadOnlyList<string> requiredInputs,
        IReadOnlyList<SnapshotFile> files)
    {
        var captured = files.Where(file => file.Exists).Select(file => file.RelativePath).ToHashSet(StringComparer.Ordinal);
        foreach (var required in requiredInputs)
        {
            var relative = Path.GetRelativePath(root, Path.GetFullPath(required, root)).Replace('\\', '/');
            if (!captured.Contains(relative))
                throw new SnapshotCaptureException($"Required input is not an eligible captured file: {relative}");
        }
    }

    private static void ValidateStaticDependencies(string originalRoot, string captureRoot,
        IReadOnlyList<SnapshotFile> files, IReadOnlySet<string> projectDirectories,
        IReadOnlyList<IgnoredEntry> ignoredEntries, IReadOnlyList<string> excludedEntries)
    {
        var captured = files.Where(file => file.Exists).Select(file => file.RelativePath).ToHashSet(StringComparer.Ordinal);
        ValidateImplicitProjectInputs(originalRoot, files, captured);
        foreach (var responseFile in files.Where(file => file.Exists && Path.GetFileName(file.RelativePath)
                     .Equals("Directory.Build.rsp", StringComparison.OrdinalIgnoreCase)))
            if (!string.IsNullOrWhiteSpace(File.ReadAllText(ToPath(captureRoot, responseFile.RelativePath))))
                throw new SnapshotCaptureException(
                    $"Non-empty Directory.Build.rsp cannot be frozen safely: {responseFile.RelativePath}");
        foreach (var file in files.Where(file => file.Exists && IsBuildXml(file.RelativePath)))
        {
            var path = ToPath(captureRoot, file.RelativePath);
            XDocument document;
            try { document = XDocument.Load(path, LoadOptions.PreserveWhitespace); }
            catch (Exception ex) when (ex is IOException or System.Xml.XmlException)
            {
                throw new SnapshotCaptureException($"Build input is not valid XML: {file.RelativePath}", ex);
            }
            if (Path.GetFileName(file.RelativePath).Equals("NuGet.Config", StringComparison.OrdinalIgnoreCase) &&
                document.Descendants().Any(element => element.Name.LocalName.Equals("packageSourceCredentials", StringComparison.OrdinalIgnoreCase)))
                throw new SnapshotCaptureException($"NuGet credentials cannot be copied into a snapshot: {file.RelativePath}");
            if (Path.GetFileName(file.RelativePath).Equals("NuGet.Config", StringComparison.OrdinalIgnoreCase) &&
                document.Descendants().Where(element => element.Name.LocalName.Equals("fallbackPackageFolders",
                        StringComparison.OrdinalIgnoreCase)).SelectMany(element => element.Elements())
                    .Any(element => !element.Name.LocalName.Equals("clear", StringComparison.OrdinalIgnoreCase)))
                throw new SnapshotCaptureException(
                    $"NuGet fallback package folders cannot be frozen safely: {file.RelativePath}");
            foreach (var element in document.Descendants())
            {
                var name = element.Name.LocalName;
                var isProperty = element.Parent?.Name.LocalName.Equals("PropertyGroup",
                    StringComparison.OrdinalIgnoreCase) == true;
                var isInsideTarget = element.Ancestors().Any(ancestor =>
                    ancestor.Name.LocalName.Equals("Target", StringComparison.OrdinalIgnoreCase));
                var isItem = element.Parent?.Name.LocalName.Equals("ItemGroup",
                    StringComparison.OrdinalIgnoreCase) == true;
                var isItemDefinition = element.Parent?.Name.LocalName.Equals("ItemDefinitionGroup",
                    StringComparison.OrdinalIgnoreCase) == true;
                var isProjectEvaluationPipelineItem = IsProjectEvaluationPipelineItem(name);
                if (isProperty && IsNamed(name, "RestoreFallbackFolders", "RestoreAdditionalProjectFallbackFolders",
                        "RestoreAdditionalProjectFallbackFoldersExcludes") &&
                    !string.IsNullOrWhiteSpace(element.Value))
                    throw new SnapshotCaptureException(
                        $"MSBuild fallback package folders cannot be frozen safely: {file.RelativePath}");
                if (isProperty && IsRestoreOrImportRedirectionProperty(name) &&
                    !string.IsNullOrWhiteSpace(element.Value))
                    throw new SnapshotCaptureException(
                        $"MSBuild restore, package, or import redirection cannot be frozen safely: {name}");
                if (name.Equals("Project", StringComparison.OrdinalIgnoreCase) &&
                    Path.GetExtension(file.RelativePath).Equals(".slnx", StringComparison.OrdinalIgnoreCase))
                {
                    var solutionProject = AttributeValue(element, "Path");
                    ValidateBuildPath(solutionProject, file.RelativePath, originalRoot, captured,
                        projectDirectories, ignoredEntries, excludedEntries, true);
                    ValidateCSharpProjectExtension(solutionProject, "Solution entry");
                    continue;
                }
                if ((isProperty || IsItemMetadataElement(element)) &&
                    IsNamed(name, "HintPath", "AssemblyOriginatorKeyFile", "CodeAnalysisRuleSet",
                        "ApplicationIcon", "ApplicationManifest", "Win32Resource", "RunSettingsFilePath"))
                {
                    RefuseProjectRelativePathInImportedBuildFile(element.Value, file.RelativePath, name);
                    ValidateBuildPath(element.Value, file.RelativePath, originalRoot, captured, projectDirectories,
                        ignoredEntries, excludedEntries, true);
                    continue;
                }
                if (IsImportElement(element))
                {
                    var import = AttributeValue(element, "Project");
                    if (string.IsNullOrWhiteSpace(import)) continue;
                    if (import.Split(';').Any(string.IsNullOrWhiteSpace))
                        throw new SnapshotCaptureException(
                            "Empty Import Project path segments cannot be frozen safely.");
                    foreach (var importPath in SplitBuildPathValues(import))
                    {
                        var normalizedImport = NormalizeImportedBuildPathForValidation(
                            importPath, file.RelativePath);
                        ValidateImportedBuildFileExtension(normalizedImport);
                        ValidateBuildPath(normalizedImport, file.RelativePath, originalRoot, captured,
                            projectDirectories, ignoredEntries, excludedEntries, true);
                    }
                    continue;
                }
                if (IsUsingTaskElement(element))
                {
                    var assemblyFile = AttributeValue(element, "AssemblyFile");
                    if (!string.IsNullOrWhiteSpace(assemblyFile))
                    {
                        RefuseProjectRelativePathInImportedBuildFile(assemblyFile, file.RelativePath,
                            "UsingTask AssemblyFile");
                        ValidateBuildPath(assemblyFile, file.RelativePath, originalRoot, captured,
                            projectDirectories, ignoredEntries, excludedEntries, true);
                    }
                    foreach (var code in element.Descendants().Where(descendant =>
                                 descendant.Name.LocalName.Equals("Code", StringComparison.OrdinalIgnoreCase)))
                    {
                        var source = AttributeValue(code, "Source");
                        if (string.IsNullOrWhiteSpace(source)) continue;
                        RefuseProjectRelativePathInImportedBuildFile(source, file.RelativePath,
                            "UsingTask Code Source");
                        ValidateBuildPath(source, file.RelativePath, originalRoot, captured,
                            projectDirectories, ignoredEntries, excludedEntries, true);
                    }
                    foreach (var reference in element.Descendants().Where(descendant =>
                                 descendant.Name.LocalName.Equals("Reference", StringComparison.OrdinalIgnoreCase) &&
                                 descendant.Ancestors().Any(ancestor =>
                                     ancestor.Name.LocalName.Equals("Task", StringComparison.OrdinalIgnoreCase))))
                    {
                        var taskReferenceInclude = AttributeValue(reference, "Include");
                        if (string.IsNullOrWhiteSpace(taskReferenceInclude) ||
                            IsNameOnlyAssemblyReference(taskReferenceInclude)) continue;
                        RefuseProjectRelativePathInImportedBuildFile(taskReferenceInclude, file.RelativePath,
                            "UsingTask Reference Include");
                        ValidateBuildPath(taskReferenceInclude, file.RelativePath, originalRoot, captured,
                            projectDirectories, ignoredEntries, excludedEntries, true);
                    }
                    continue;
                }
                if (IsMsBuildTaskElement(element))
                {
                    var projects = AttributeValue(element, "Projects");
                    if (!string.IsNullOrWhiteSpace(projects))
                        ValidateStaticProjectInputs(projects, "MSBuild Projects", file.RelativePath, originalRoot,
                            captured, projectDirectories, ignoredEntries, excludedEntries, true);
                    foreach (var parameter in new[] { "Properties", "AdditionalProperties" })
                    {
                        var value = AttributeValue(element, parameter);
                        if (string.IsNullOrWhiteSpace(value)) continue;
                        try
                        {
                            ValidateForwardedProperties($"MSBuild {parameter}", value, file.RelativePath,
                                originalRoot);
                        }
                        catch (SnapshotCaptureException exception)
                        {
                            throw new SnapshotCaptureException(
                                $"MSBuild {parameter} cannot be frozen safely: {exception.Message}", exception);
                        }
                    }
                    var removeProperties = AttributeValue(element, "RemoveProperties");
                    if (!string.IsNullOrWhiteSpace(removeProperties))
                        ValidateRemovedProperties(removeProperties);
                    continue;
                }
                if (IsTaskOutputElement(element))
                {
                    var itemName = AttributeValue(element, "ItemName");
                    RefuseDynamicTaskOutputName(itemName, "ItemName");
                    if (!string.IsNullOrWhiteSpace(itemName) && IsProjectEvaluationPipelineItem(itemName))
                        throw new SnapshotCaptureException(
                            $"Task Output ItemName cannot populate project-evaluation input {itemName} safely.");
                    var propertyName = AttributeValue(element, "PropertyName");
                    RefuseDynamicTaskOutputName(propertyName, "PropertyName");
                    if (!string.IsNullOrWhiteSpace(propertyName) &&
                        (propertyName.Equals("AdditionalProjects", StringComparison.OrdinalIgnoreCase) ||
                         IsRestoreOrImportRedirectionProperty(propertyName)))
                        throw new SnapshotCaptureException(
                            $"Task Output PropertyName cannot populate project-evaluation input {propertyName} safely.");
                    continue;
                }
                if (isProperty && name.Equals("AdditionalProjects", StringComparison.OrdinalIgnoreCase))
                {
                    ValidateStaticProjectInputs(element.Value, name, file.RelativePath, originalRoot, captured,
                        projectDirectories, ignoredEntries, excludedEntries, false);
                    continue;
                }
                if (isProperty && IsLogicalNonFilesystemProperty(name))
                {
                    RefuseEncodedMsBuildOctet(element.Value, name);
                    ValidateStaticPropertyAssignments(element.Value, file.RelativePath, originalRoot);
                }
                else if (isProperty)
                    ValidateStaticPropertyValue(element.Value, file.RelativePath, originalRoot);
                if ((isItem || isItemDefinition) && !isInsideTarget)
                    ValidateItemMetadata(element, file.RelativePath, originalRoot, captured, projectDirectories,
                        ignoredEntries, excludedEntries);
                else if (isItem && isProjectEvaluationPipelineItem)
                    ValidateForwardingMetadata(element, file.RelativePath, originalRoot, captured,
                        projectDirectories, ignoredEntries, excludedEntries);
                if (!isItem || (isInsideTarget && !isProjectEvaluationPipelineItem) ||
                    element.Attributes().All(attribute => !IsNamed(attribute.Name.LocalName, "Include", "Update", "Remove")))
                    continue;
                var include = AttributeValue(element, "Include");
                RefuseEncodedMsBuildOctet(include, $"{name} Include");
                if (!(name.Equals("Reference", StringComparison.OrdinalIgnoreCase) && include is not null &&
                      !include.Contains('/') && !include.Contains('\\')))
                    foreach (var value in SplitBuildPathValues(include))
                    {
                        var validatedValue = NormalizeImportedBuildPathForValidation(value, file.RelativePath);
                        if (!ContainsDynamicExpression(validatedValue) && IsNonCSharpProjectPath(validatedValue))
                            ValidateCSharpProjectExtension(validatedValue, name);
                        if (!IsPathItem(name) && !isProjectEvaluationPipelineItem &&
                            ContainsDynamicExpression(validatedValue)) continue;
                        if (isProjectEvaluationPipelineItem && ContainsDynamicExpression(validatedValue) &&
                            !IsReservedSelfProjectExpression(value))
                            throw new SnapshotCaptureException(
                                $"Dynamic {name} Include cannot be frozen safely.");
                        if (isProjectEvaluationPipelineItem && IsReservedSelfProjectExpression(value)) continue;
                        if (IsProjectRelativePathItem(name) || isProjectEvaluationPipelineItem)
                            RefuseProjectRelativePathInImportedBuildFile(value, file.RelativePath, name);
                        try
                        {
                            ValidateBuildPath(value, file.RelativePath, originalRoot, captured, projectDirectories,
                                ignoredEntries, excludedEntries, true, AttributeValue(element, "Exclude"));
                        }
                        catch (SnapshotCaptureException exception) when (isProjectEvaluationPipelineItem)
                        {
                            throw new SnapshotCaptureException(
                                $"{name} Include cannot be frozen safely: {exception.Message}", exception);
                        }
                        if (isProjectEvaluationPipelineItem)
                            ValidateCSharpProjectExtension(validatedValue, name);
                    }
                foreach (var value in SplitBuildPathValues(AttributeValue(element, "Update")))
                {
                    var validatedValue = NormalizeImportedBuildPathForValidation(value, file.RelativePath);
                    if (!ContainsDynamicExpression(validatedValue) && IsNonCSharpProjectPath(validatedValue))
                        ValidateCSharpProjectExtension(validatedValue, name);
                    if (!IsPathItem(name) && !isProjectEvaluationPipelineItem) continue;
                    if (isProjectEvaluationPipelineItem && ContainsDynamicExpression(validatedValue) &&
                        !IsReservedSelfProjectExpression(value))
                        throw new SnapshotCaptureException(
                            $"Dynamic {name} Update cannot be frozen safely.");
                    if (isProjectEvaluationPipelineItem && IsReservedSelfProjectExpression(value)) continue;
                    try
                    {
                        ValidateBuildPath(value, file.RelativePath, originalRoot, captured, projectDirectories,
                            ignoredEntries, excludedEntries, false, ignoreProjectOutputEntries: true);
                    }
                    catch (SnapshotCaptureException exception) when (isProjectEvaluationPipelineItem)
                    {
                        throw new SnapshotCaptureException(
                            $"{name} Update cannot be frozen safely: {exception.Message}", exception);
                    }
                    if (isProjectEvaluationPipelineItem)
                        ValidateCSharpProjectExtension(validatedValue, name);
                }
                // Remove only subtracts already-created items. Treating it as an input creates false refusals.
            }
        }
        foreach (var solution in files.Where(file => file.Exists && Path.GetExtension(file.RelativePath)
                     .Equals(".sln", StringComparison.OrdinalIgnoreCase)))
        {
            foreach (var line in File.ReadLines(ToPath(captureRoot, solution.RelativePath)))
            {
                if (!line.StartsWith("Project(", StringComparison.OrdinalIgnoreCase)) continue;
                var quoted = line.Split('"');
                if (quoted.Length >= 6)
                {
                    if (quoted[1].Equals("{2150E333-8FDC-42A3-9474-1A3956D46DE8}",
                            StringComparison.OrdinalIgnoreCase)) continue;
                    ValidateBuildPath(quoted[5], solution.RelativePath, originalRoot, captured,
                        projectDirectories, ignoredEntries, excludedEntries, true);
                    ValidateCSharpProjectExtension(quoted[5], "Solution entry");
                }
            }
        }
        RejectInheritedBuildInputs(originalRoot, InheritedBuildInputNames);
    }

    internal static void RejectInheritedGlobalJson(string root) =>
        RejectInheritedBuildInputs(root, ["global.json"]);

    private static void RejectInheritedBuildInputs(string root, IReadOnlyList<string> names)
    {
        for (var parent = Directory.GetParent(root); parent is not null; parent = parent.Parent)
        foreach (var name in names)
            if (File.Exists(Path.Combine(parent.FullName, name)))
                throw new SnapshotCaptureException(
                    $"Inherited build input outside the snapshot root is unsupported: {name}");
    }

    private static void ValidateItemMetadata(XElement item, string declaringFile, string originalRoot,
        IReadOnlySet<string> captured, IReadOnlySet<string> projectDirectories,
        IReadOnlyList<IgnoredEntry> ignoredEntries, IReadOnlyList<string> excludedEntries)
    {
        var itemName = item.Name.LocalName;
        foreach (var attribute in item.Attributes().Where(attribute => !attribute.IsNamespaceDeclaration &&
                     !IsNamed(attribute.Name.LocalName, "Include", "Exclude", "Remove", "Update", "Condition")))
            ValidateItemMetadataValue(itemName, attribute.Name.LocalName, attribute.Value, declaringFile,
                originalRoot, captured, projectDirectories, ignoredEntries, excludedEntries);
        foreach (var metadata in item.Elements())
            ValidateItemMetadataValue(itemName, metadata.Name.LocalName, metadata.Value, declaringFile,
                originalRoot, captured, projectDirectories, ignoredEntries, excludedEntries);
    }

    private static void ValidateForwardingMetadata(XElement item, string declaringFile, string originalRoot,
        IReadOnlySet<string> captured, IReadOnlySet<string> projectDirectories,
        IReadOnlyList<IgnoredEntry> ignoredEntries, IReadOnlyList<string> excludedEntries)
    {
        var itemName = item.Name.LocalName;
        foreach (var attribute in item.Attributes().Where(attribute =>
                     IsProjectReferenceForwardingMetadata(attribute.Name.LocalName)))
            ValidateItemMetadataValue(itemName, attribute.Name.LocalName, attribute.Value, declaringFile,
                originalRoot, captured, projectDirectories, ignoredEntries, excludedEntries);
        foreach (var metadata in item.Elements().Where(metadata =>
                     IsProjectReferenceForwardingMetadata(metadata.Name.LocalName)))
            ValidateItemMetadataValue(itemName, metadata.Name.LocalName, metadata.Value, declaringFile,
                originalRoot, captured, projectDirectories, ignoredEntries, excludedEntries);
    }

    private static bool IsImportElement(XElement element) =>
        element.Name.LocalName.Equals("Import", StringComparison.OrdinalIgnoreCase) &&
        element.Parent is not null && IsNamed(element.Parent.Name.LocalName, "Project", "ImportGroup");

    private static bool IsUsingTaskElement(XElement element) =>
        element.Name.LocalName.Equals("UsingTask", StringComparison.OrdinalIgnoreCase) &&
        element.Parent?.Name.LocalName.Equals("Project", StringComparison.OrdinalIgnoreCase) == true;

    private static bool IsMsBuildTaskElement(XElement element) =>
        element.Name.LocalName.Equals("MSBuild", StringComparison.OrdinalIgnoreCase) &&
        element.Parent?.Name.LocalName.Equals("Target", StringComparison.OrdinalIgnoreCase) == true;

    private static bool IsTaskOutputElement(XElement element) =>
        element.Name.LocalName.Equals("Output", StringComparison.OrdinalIgnoreCase) &&
        element.Parent is not null &&
        !IsNamed(element.Parent.Name.LocalName, "PropertyGroup", "ItemGroup") &&
        element.Parent.Parent?.Name.LocalName.Equals("Target", StringComparison.OrdinalIgnoreCase) == true;

    private static bool IsItemMetadataElement(XElement element) =>
        element.Parent?.Parent is not null &&
        IsNamed(element.Parent.Parent.Name.LocalName, "ItemGroup", "ItemDefinitionGroup");

    private static void RefuseDynamicTaskOutputName(string? value, string attribute)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        RefuseEncodedMsBuildOctet(value, $"Task Output {attribute}");
        if (ContainsDynamicExpression(value))
            throw new SnapshotCaptureException(
                $"Dynamic task Output {attribute} cannot be proven unrelated to project-evaluation inputs.");
    }

    private static void ValidateItemMetadataValue(string itemName, string metadataName, string value,
        string declaringFile, string originalRoot, IReadOnlySet<string> captured,
        IReadOnlySet<string> projectDirectories, IReadOnlyList<IgnoredEntry> ignoredEntries,
        IReadOnlyList<string> excludedEntries)
    {
        RefuseEncodedMsBuildOctet(value, $"{itemName} {metadataName} metadata");
        if (IsProjectReferenceForwardingMetadata(metadataName))
        {
            try
            {
                ValidateProjectReferenceProperties(metadataName, value, declaringFile, originalRoot);
            }
            catch (SnapshotCaptureException exception)
            {
                throw new SnapshotCaptureException(
                    $"{itemName} {metadataName} forwarding metadata cannot be frozen safely: {exception.Message}",
                    exception);
            }
            return;
        }
        ValidateStaticPropertyAssignments(value, declaringFile, originalRoot);
        // NuGet's PackagePath is a logical path inside a package, not a filesystem input path.
        if (metadataName.Equals("PackagePath", StringComparison.OrdinalIgnoreCase)) return;
        // Container environment values are values inside the image, not host filesystem inputs.
        if (itemName.Equals("ContainerEnvironmentVariable", StringComparison.OrdinalIgnoreCase) &&
            metadataName.Equals("Value", StringComparison.OrdinalIgnoreCase)) return;
        if (IsNamed(metadataName, "HintPath", "AssemblyOriginatorKeyFile", "CodeAnalysisRuleSet",
                "ApplicationIcon", "ApplicationManifest", "Win32Resource", "RunSettingsFilePath"))
        {
            RefuseProjectRelativePathInImportedBuildFile(value, declaringFile, metadataName);
            ValidateBuildPath(value, declaringFile, originalRoot, captured, projectDirectories, ignoredEntries,
                excludedEntries, true);
            return;
        }
        ValidateStaticPropertyValue(value, declaringFile, originalRoot);
    }

    private static bool IsProjectReferenceForwardingMetadata(string metadataName) =>
        IsNamed(metadataName, "AdditionalProperties", "Properties", "SetConfiguration", "SetPlatform",
            "SetTargetFramework");

    private static bool IsProjectEvaluationPipelineItem(string itemName) =>
        IsNamed(itemName, "ProjectReference", "ProjectReferenceWithConfiguration",
            "_ProjectReferenceWithConfiguration", "_MSBuildProjectReference",
            "_MSBuildProjectReferenceExistent", "AnnotatedProjects", "UpdatedAnnotatedProjects",
            "ProjectsWithNearestPlatform", "_ProjectsWithPlatformAssignment",
            "_ProjectReferencePlatformPossibilities", "_ProjectReferenceTargetFrameworkPossibilities",
            // SDK 10.0.103 restore source plus every item identity used directly by an MSBuild
            // task's Projects attribute across restore/build/test/publish/pack/watch/store/crossgen.
            "RestoreGraphProjectInputItems", "FilteredRestoreGraphProjectInputItems", "_AllProjects",
            "FilteredRestoreGraphProjectInputItemsWithoutDuplicates", "_GenerateRestoreGraphProjectEntryInput",
            "_RestoreProjectPathItems", "_CurrentRestoreProjectPathItems", "_RestoreProjectPathItemsWithoutDupes",
            "AssembliestoCrossgen", "PackageReferencesToStore", "StaticWebAssetProjectConfiguration",
            "_InnerBuild", "_InnerBuildProjects", "_ProjectConfigurationsWithEmbeddedPublishTargets",
            "_ProjectConfigurationsWithPublishTargets", "_ProjectReferencesFromAssetsFile", "_ProjectsWithTFM",
            "_ProjectsWithTFMNoBuild", "_ProjectToTestWithTFM", "_RidSpecificToolPackageProject",
            "_StaticWebAssetProjectReference", "_StaticWebAssetsEmbeddedProjectAssetConfigurations",
            "_StaticWebAssetsProjectReference", "_WatchProjects");

    private static void ValidateStaticProjectInputs(string projects, string context, string declaringFile,
        string originalRoot, IReadOnlySet<string> captured, IReadOnlySet<string> projectDirectories,
        IReadOnlyList<IgnoredEntry> ignoredEntries, IReadOnlyList<string> excludedEntries,
        bool allowReservedSelfReferences)
    {
        RefuseEncodedMsBuildOctet(projects, context);
        foreach (var project in SplitBuildPathValues(projects))
        {
            if (allowReservedSelfReferences && IsReservedSelfProjectExpression(project)) continue;
            var validatedProject = NormalizeImportedBuildPathForValidation(project, declaringFile);
            if (ContainsDynamicExpression(validatedProject))
                throw new SnapshotCaptureException($"Dynamic {context} values cannot be frozen safely.");
            if (validatedProject.IndexOfAny(['*', '?']) >= 0)
                throw new SnapshotCaptureException($"Wildcard {context} values cannot be frozen safely.");
            try
            {
                RefuseProjectRelativePathInImportedBuildFile(project, declaringFile, context);
                ValidateCSharpProjectExtension(validatedProject, context);
                ValidateBuildPath(project, declaringFile, originalRoot, captured, projectDirectories,
                    ignoredEntries, excludedEntries, true);
                var declaringDirectory = Path.GetDirectoryName(ToPath(originalRoot, declaringFile))!;
                var resolved = Path.GetFullPath(validatedProject.Replace('\\', Path.DirectorySeparatorChar),
                    declaringDirectory);
                var relative = NormalizeRootRelative(Path.GetRelativePath(originalRoot, resolved));
                if (!captured.Contains(relative))
                    throw new SnapshotCaptureException(
                        $"{context} input is not present in the snapshot: {relative}");
            }
            catch (SnapshotCaptureException exception)
            {
                throw new SnapshotCaptureException(
                    $"{context} cannot be frozen safely: {exception.Message}", exception);
            }
        }
    }

    private static bool IsReservedSelfProjectExpression(string value) =>
        IsNamed(value.Trim(), "$(MSBuildProjectFile)", "$(MSBuildProjectFullPath)",
            "$(MSBuildThisFileFullPath)");

    private static bool IsLogicalNonFilesystemProperty(string propertyName) =>
        propertyName.Equals("ContainerWorkingDirectory", StringComparison.OrdinalIgnoreCase);

    private static bool IsNonCSharpProjectPath(string value)
    {
        var extension = Path.GetExtension(value.Split(['*', '?'], 2)[0]);
        return extension.EndsWith("proj", StringComparison.OrdinalIgnoreCase) &&
               !extension.Equals(".csproj", StringComparison.OrdinalIgnoreCase);
    }

    private static void ValidateProjectReferenceProperties(string metadataName, string value,
        string declaringFile, string originalRoot)
        => ValidateForwardedProperties($"ProjectReference {metadataName}", value, declaringFile, originalRoot);

    private static void ValidateForwardedProperties(string context, string value,
        string declaringFile, string originalRoot)
    {
        if (ContainsDynamicExpression(value))
            throw new SnapshotCaptureException(
                $"Dynamic {context} values cannot be frozen safely before MSBuild expands and splits them.");
        foreach (var pair in SplitBuildPathValues(value))
        {
            var equals = pair.IndexOf('=');
            if (equals <= 0)
                throw new SnapshotCaptureException(
                    $"{context} must contain static Name=Value pairs to be frozen safely.");
            var propertyName = pair[..equals].Trim();
            var propertyValue = pair[(equals + 1)..].Trim();
            if (ContainsDynamicExpression(propertyName))
                throw new SnapshotCaptureException(
                    "Dynamic ProjectReference property names cannot be frozen safely.");
            if (IsRestoreOrImportRedirectionProperty(propertyName))
                throw new SnapshotCaptureException(
                    $"MSBuild restore, package, or import redirection cannot be frozen safely: {propertyName}");
            ValidateStaticPropertyValue(propertyValue, declaringFile, originalRoot);
        }
    }

    private static void ValidateRemovedProperties(string value)
    {
        RefuseEncodedMsBuildOctet(value, "MSBuild RemoveProperties");
        if (ContainsDynamicExpression(value))
            throw new SnapshotCaptureException(
                "Dynamic MSBuild RemoveProperties values cannot be frozen safely.");
        foreach (var propertyName in SplitBuildPathValues(value))
        {
            if (IsRestoreOrImportRedirectionProperty(propertyName))
                throw new SnapshotCaptureException(
                    $"MSBuild RemoveProperties cannot name restore or import redirector {propertyName}.");
        }
    }

    private static void ValidateImplicitProjectInputs(string originalRoot, IReadOnlyList<SnapshotFile> files,
        IReadOnlySet<string> captured)
    {
        var implicitNames = new[] { "Directory.Build.props", "Directory.Build.targets",
            "Directory.Packages.props", "Directory.Build.rsp", "global.json" };
        foreach (var project in files.Where(file => file.Exists && Path.GetExtension(file.RelativePath)
                     .Equals(".csproj", StringComparison.OrdinalIgnoreCase)))
        {
            var projectPath = ToPath(originalRoot, project.RelativePath);
            RefuseUncaptured(projectPath + ".user");
            for (var directory = new DirectoryInfo(Path.GetDirectoryName(projectPath)!);;
                 directory = directory.Parent!)
            {
                foreach (var name in implicitNames) RefuseUncaptured(Path.Combine(directory.FullName, name));
                RefuseUncapturedNamed(directory.FullName, "NuGet.Config");
                if (PathEquals(directory.FullName, originalRoot)) break;
                if (directory.Parent is null)
                    throw new SnapshotCaptureException("Project directory escaped the snapshot root during implicit-input validation.");
            }
        }
        foreach (var source in files.Where(file => file.Exists && Path.GetExtension(file.RelativePath)
                     .Equals(".cs", StringComparison.OrdinalIgnoreCase)))
            RefuseAlongAncestors(Path.GetDirectoryName(ToPath(originalRoot, source.RelativePath))!,
                [".editorconfig", ".globalconfig"], "compiler");
        foreach (var solution in files.Where(file => file.Exists && Path.GetExtension(file.RelativePath) is var extension &&
                     (extension.Equals(".sln", StringComparison.OrdinalIgnoreCase) ||
                      extension.Equals(".slnx", StringComparison.OrdinalIgnoreCase))))
            RefuseAlongAncestors(Path.GetDirectoryName(ToPath(originalRoot, solution.RelativePath))!,
                ["Directory.Solution.props", "Directory.Solution.targets"], "solution");

        void RefuseAlongAncestors(string start, IReadOnlyList<string> names, string kind)
        {
            for (var directory = new DirectoryInfo(start);; directory = directory.Parent!)
            {
                foreach (var name in names) RefuseUncaptured(Path.Combine(directory.FullName, name), kind);
                if (PathEquals(directory.FullName, originalRoot)) break;
                if (directory.Parent is null)
                    throw new SnapshotCaptureException(
                        $"{kind} input directory escaped the snapshot root during implicit-input validation.");
            }
        }

        void RefuseUncaptured(string path, string kind = "MSBuild")
        {
            if (!File.Exists(path)) return;
            var relative = Path.GetRelativePath(originalRoot, path).Replace('\\', '/');
            if (!captured.Contains(relative))
                throw new SnapshotCaptureException(
                    $"Existing implicit {kind} input is not captured and was not read: {relative}");
        }

        void RefuseUncapturedNamed(string directory, string name)
        {
            foreach (var path in Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
                         .Where(path => Path.GetFileName(path).Equals(name, StringComparison.OrdinalIgnoreCase)))
                RefuseUncaptured(path);
        }
    }

    private static void ValidateBuildPath(string? value, string declaringFile, string originalRoot,
        IReadOnlySet<string> captured, IReadOnlySet<string> projectDirectories,
        IReadOnlyList<IgnoredEntry> ignoredEntries, IReadOnlyList<string> excludedEntries, bool requireCaptured,
        string? exclude = null, bool validateExcludedPath = true, bool validateGlobMatches = true,
        bool ignoreProjectOutputEntries = false)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        RefuseEncodedMsBuildOctet(value, "build path");
        RefuseEncodedMsBuildOctet(exclude, "build exclusion");
        value = NormalizeImportedBuildPathForValidation(value, declaringFile);
        if (value.Contains("$(", StringComparison.Ordinal) || value.Contains("@(", StringComparison.Ordinal) ||
            value.Contains("%(", StringComparison.Ordinal))
            throw new SnapshotCaptureException("Dynamic external build input cannot be frozen safely.");
        var platformPath = value.Replace('\\', Path.DirectorySeparatorChar);
        if (Path.IsPathRooted(platformPath) ||
            (Uri.TryCreate(value, UriKind.Absolute, out var rootedUri) && rootedUri.IsFile) ||
            (value.Length >= 3 && char.IsLetter(value[0]) && value[1] == ':' &&
             (value[2] == '/' || value[2] == '\\')))
            throw new SnapshotCaptureException(
                "Rooted build inputs resolve to the live original tree and cannot be frozen safely.");
        var projectDirectory = Path.GetDirectoryName(ToPath(originalRoot, declaringFile))!;
        var hasGlob = value.IndexOfAny(['*', '?']) >= 0;
        var staticPart = value.Split(['*', '?'], 2)[0];
        var resolved = string.IsNullOrWhiteSpace(staticPart)
            ? projectDirectory
            : Path.GetFullPath(staticPart.Replace('\\', Path.DirectorySeparatorChar), projectDirectory);
        EnsureWithin(originalRoot, resolved, "Build input");
        var relative = NormalizeRootRelative(Path.GetRelativePath(originalRoot, resolved));
        if (validateExcludedPath && relative.Length > 0 &&
            (SnapshotInputPolicy.IsExcluded(relative, projectDirectories) || SnapshotInputPolicy.IsSecret(relative)))
            throw new SnapshotCaptureException($"Required build input is excluded or secret: {relative}");
        if (hasGlob && validateGlobMatches)
        {
            var endsAtDirectory = staticPart.EndsWith('/') || staticPart.EndsWith('\\');
            var globRoot = string.IsNullOrEmpty(staticPart)
                ? projectDirectory
                : endsAtDirectory ? resolved : Path.GetDirectoryName(resolved)!;
            var relativeGlobRoot = NormalizeRootRelative(Path.GetRelativePath(originalRoot, globRoot));
            var excludedRoots = StaticExclusionRoots(exclude, projectDirectory, originalRoot);
            bool IsCoveredByExclude(string path) => excludedRoots.Any(root => IsWithinBuildPath(path, root));
            if (ignoredEntries.Any(entry =>
                    (IsWithinProject(entry.RelativePath, relativeGlobRoot) ||
                     IsWithinProject(relativeGlobRoot, entry.RelativePath)) &&
                    !(ignoreProjectOutputEntries && SnapshotInputPolicy.IsProjectOutput(
                        entry.RelativePath, projectDirectories)) && !IsCoveredByExclude(entry.RelativePath)))
                throw new SnapshotCaptureException(
                    $"Static build glob reaches ignored input that was not captured: {value}");
            if (excludedEntries.Any(path => IsWithinProject(path, relativeGlobRoot) &&
                    !(ignoreProjectOutputEntries && SnapshotInputPolicy.IsProjectOutput(path, projectDirectories)) &&
                    !IsCoveredByExclude(path)))
                throw new SnapshotCaptureException(
                    $"Static build glob reaches excluded or secret input that was not captured: {value}");
        }
        if (requireCaptured && !hasGlob && File.Exists(resolved) && !captured.Contains(relative))
            throw new SnapshotCaptureException($"Required build input is not present in the snapshot: {relative}");
    }

    private static IReadOnlyList<string> SplitBuildPathValues(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static bool IsNamed(string actual, params string[] expected) =>
        expected.Any(name => actual.Equals(name, StringComparison.OrdinalIgnoreCase));

    private static bool ContainsDynamicExpression(string value) =>
        value.Contains("$(", StringComparison.Ordinal) || value.Contains("@(", StringComparison.Ordinal) ||
        value.Contains("%(", StringComparison.Ordinal);

    private static bool HasEncodedMsBuildOctet(string value)
    {
        for (var index = 0; index + 2 < value.Length; index++)
            if (value[index] == '%' && IsHex(value[index + 1]) && IsHex(value[index + 2])) return true;
        return false;

        static bool IsHex(char value) =>
            value is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F';
    }

    private static void RefuseEncodedMsBuildOctet(string? value, string context)
    {
        if (!string.IsNullOrEmpty(value) && HasEncodedMsBuildOctet(value))
            throw new SnapshotCaptureException(
                $"MSBuild %XX encoded octets are unsupported in statically inspected {context} values; " +
                "validation fails closed before interpreting their semantics.");
    }

    private static string? AttributeValue(XElement element, string name) =>
        element.Attributes().FirstOrDefault(attribute =>
            attribute.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase))?.Value;

    internal static IReadOnlyList<string> RestoreOrImportRedirectionPropertyNames { get; } =
    [
        "RestoreSources", "RestoreAdditionalProjectSources", "RestorePackagesPath", "RestoreConfigFile",
        // NuGet.targets uses this property as the fallback project-entry list for restore graph evaluation.
        "RestoreGraphProjectInput",
        // NuGet forwards this semicolon-delimited property bag into nested restore evaluations.
        "_GenerateRestoreGraphProjectEntryInputProperties",
        "MSBuildProjectExtensionsPath", "DirectoryBuildPropsPath", "DirectoryBuildTargetsPath",
        "DirectoryPackagesPropsPath", "DirectorySolutionPropsPath", "DirectorySolutionTargetsPath",
        "BaseIntermediateOutputPath", "MSBuildUserExtensionsPath", "MSBuildExtensionsPath",
        "NuGetPackageRoot", "NuGetPackageFolders", "ProjectAssetsFile", "RestoreOutputPath",
        "NuGetRestoreTargets", "CSharpCoreTargetsPath", "CSharpDesignTimeTargetsPath",
        // Used as the prefix of compound SDK imports and by the SDK resolver.
        "MSBuildSDKsPath",
        "_DirectoryBuildTargetsBasePath", "_DirectoryBuildTargetsFile",
        "_DirectoryPackagesPropsBasePath", "_DirectoryPackagesPropsFile",
        // Mechanically derived from pure-property Import Project="$(X)" sites in SDK 10.0.103.
        "AdditionalImport", "AfterMicrosoftNetSdkProps", "AfterMicrosoftNETSdkTargets",
        "AfterTargetFrameworkInferenceTargets", "AlternateCommonProps", "BeforeMicrosoftNETSdkTargets",
        "BeforeTargetFrameworkInferenceTargets", "CodeAnalysisTargets", "CommonTargetsPath",
        "CSharpTargetsPath", "FSharpDesignTimeTargetsPath", "FSharpOverridesTargetsShim", "FSharpPropsShim",
        "FSharpTargetsShim", "ILCompilerTargetsPath", "ILLinkTargetsPath", "LanguageTargets",
        "MsAppxPackageTargets", "MsTestToolsTargets", "NETCoreSdkBundledCliToolsProps",
        "NETCoreSdkBundledMSBuildInformationProps", "NETCoreSdkBundledVersionsProps",
        "NetFrameworkPropsPath", "NetFrameworkTargetsPath", "NuGetBuildTasksPackTargets",
        "RazorDesignTimeTargets", "RazorSdkCurrentVersionProps", "RazorSdkCurrentVersionTargets",
        "ReportingServicesTargets", "StaticWebAssetsSdkCurrentVersionProps",
        "StaticWebAssetsSdkCurrentVersionTargets", "VisualBasicCoreTargetsPath",
        "VisualBasicDesignTimeTargetsPath", "VisualBasicTargetsPath", "WebPublishProfileFile",
        "_BlazorWebAssemblyPropsFile", "_BlazorWebAssemblyTargetsFile",
        "_BlazorWebAssemblyVersionedTargetsFile", "_WebAssemblyPropsFile", "_WebAssemblyTargetsFile"
    ];

    internal static bool IsRestoreOrImportRedirectionProperty(string name) =>
        RestoreOrImportRedirectionPropertyNames.Any(candidate =>
            name.Equals(candidate, StringComparison.OrdinalIgnoreCase)) ||
        IsDirectoryImportRedirectionProperty(name) ||
        name.StartsWith("CustomBefore", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("CustomAfter", StringComparison.OrdinalIgnoreCase);

    private static void ValidateStaticPropertyValue(string value, string declaringFile, string root)
    {
        RefuseEncodedMsBuildOctet(value, "property value");
        foreach (var candidate in SplitBuildPathValues(value))
        {
            var pathCandidate = candidate;
            if (TrySplitStaticPropertyAssignment(candidate, out var propertyName, out var propertyValue))
            {
                if (IsRestoreOrImportRedirectionProperty(propertyName))
                    throw new SnapshotCaptureException(
                        $"MSBuild restore, package, or import redirection cannot be frozen safely: {propertyName}");
                pathCandidate = propertyValue;
            }
            if (ContainsDynamicExpression(pathCandidate)) continue;
            if (IsRootedHostPath(pathCandidate))
                throw new SnapshotCaptureException(
                    "Rooted static property values resolve to the live original tree and cannot be frozen safely.");
            string resolved;
            try
            {
                if (Uri.TryCreate(pathCandidate, UriKind.Absolute, out var uri))
                {
                    if (!uri.IsFile) continue;
                    resolved = Path.GetFullPath(uri.LocalPath);
                }
                else
                    resolved = Path.GetFullPath(pathCandidate.Replace('\\', Path.DirectorySeparatorChar),
                        Path.GetDirectoryName(ToPath(root, declaringFile))!);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                continue;
            }
            var relative = Path.GetRelativePath(root, resolved);
            if (relative is ".." ||
                relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
                Path.IsPathRooted(relative))
                throw new SnapshotCaptureException(
                    "Static property value resolves outside the snapshot and cannot be frozen safely.");
        }
    }

    private static void ValidateStaticPropertyAssignments(string value, string declaringFile, string root)
    {
        foreach (var candidate in SplitBuildPathValues(value))
        {
            if (!TrySplitStaticPropertyAssignment(candidate, out var propertyName, out var propertyValue)) continue;
            if (IsRestoreOrImportRedirectionProperty(propertyName))
                throw new SnapshotCaptureException(
                    $"MSBuild restore, package, or import redirection cannot be frozen safely: {propertyName}");
            ValidateStaticPropertyValue(propertyValue, declaringFile, root);
        }
    }

    private static bool TrySplitStaticPropertyAssignment(string value, out string propertyName,
        out string propertyValue)
    {
        var equals = value.IndexOf('=');
        propertyName = equals > 0 ? value[..equals].Trim() : string.Empty;
        propertyValue = equals > 0 ? value[(equals + 1)..].Trim() : string.Empty;
        return propertyName.Length > 0 &&
               (char.IsLetter(propertyName[0]) || propertyName[0] == '_') &&
               propertyName.All(character => char.IsLetterOrDigit(character) || character == '_');
    }

    private static bool IsNameOnlyAssemblyReference(string value) =>
        !HasEncodedMsBuildOctet(value) &&
        !ContainsDynamicExpression(value) &&
        !value.Contains('/') &&
        !value.Contains('\\') &&
        !value.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) &&
        !value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);

    private static bool IsDirectoryImportRedirectionProperty(string name)
    {
        var candidate = name.TrimStart('_');
        return (candidate.StartsWith("DirectoryBuild", StringComparison.OrdinalIgnoreCase) ||
                candidate.StartsWith("DirectoryPackages", StringComparison.OrdinalIgnoreCase) ||
                candidate.StartsWith("DirectorySolution", StringComparison.OrdinalIgnoreCase)) &&
               (candidate.EndsWith("Path", StringComparison.OrdinalIgnoreCase) ||
                candidate.EndsWith("File", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsPathItem(string name) =>
        IsNamed(name, "Reference", "ProjectReference", "Compile", "Content", "None", "EmbeddedResource",
            "AdditionalFiles", "Analyzer", "GlobalAnalyzerConfigFiles", "EditorConfigFiles", "Protobuf",
            "Resource", "Page");

    private static bool IsProjectRelativePathItem(string name) =>
        IsNamed(name, "ProjectReference", "Compile", "Content", "None", "EmbeddedResource", "AdditionalFiles",
            "Analyzer", "GlobalAnalyzerConfigFiles", "EditorConfigFiles", "Protobuf", "Resource", "Page");

    private static void ValidateImportedBuildFileExtension(string value)
    {
        RefuseEncodedMsBuildOctet(value, "Import Project");
        if (value.Contains("$(", StringComparison.Ordinal) || value.Contains("@(", StringComparison.Ordinal) ||
            value.Contains("%(", StringComparison.Ordinal))
            throw new SnapshotCaptureException("Dynamic Import Project values cannot be frozen safely.");
        var extension = Path.GetExtension(value);
        if (!extension.Equals(".props", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".targets", StringComparison.OrdinalIgnoreCase))
            throw new SnapshotCaptureException(
                $"Imported build file extension is not inspected and cannot be frozen safely: {value}");
    }

    private static void RefuseProjectRelativePathInImportedBuildFile(string value, string declaringFile, string name)
    {
        RefuseEncodedMsBuildOctet(value, name);
        var extension = Path.GetExtension(declaringFile);
        if ((!extension.Equals(".props", StringComparison.OrdinalIgnoreCase) &&
             !extension.Equals(".targets", StringComparison.OrdinalIgnoreCase)) ||
            Path.IsPathRooted(value)) return;
        if (TryNormalizeImportedBuildPath(value, declaringFile, out _)) return;
        throw new SnapshotCaptureException(
            $"A project-relative {name} path declared in imported {extension} cannot be frozen safely: {value}");
    }

    private static string NormalizeImportedBuildPathForValidation(string value, string declaringFile) =>
        TryNormalizeImportedBuildPath(value, declaringFile, out var normalized) ? normalized : value;

    private static bool TryNormalizeImportedBuildPath(string value, string declaringFile, out string normalized)
    {
        const string prefix = "$(MSBuildThisFileDirectory)";
        normalized = value;
        var extension = Path.GetExtension(declaringFile);
        if ((!extension.Equals(".props", StringComparison.OrdinalIgnoreCase) &&
             !extension.Equals(".targets", StringComparison.OrdinalIgnoreCase)) ||
            !value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
        var suffix = value[prefix.Length..];
        if (string.IsNullOrWhiteSpace(suffix) || ContainsDynamicExpression(suffix)) return false;
        normalized = suffix;
        return true;
    }

    private static bool IsRootedHostPath(string value)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.IsFile) return true;
        var platformPath = value.Replace('\\', Path.DirectorySeparatorChar);
        return Path.IsPathRooted(platformPath) ||
               value.Length >= 3 && char.IsLetter(value[0]) && value[1] == ':' &&
               (value[2] == '/' || value[2] == '\\');
    }

    private static void ValidateCSharpProjectExtension(string? value, string source)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        RefuseEncodedMsBuildOctet(value, source);
        if (value.Contains("$(", StringComparison.Ordinal) || value.Contains("@(", StringComparison.Ordinal) ||
            value.Contains("%(", StringComparison.Ordinal)) return;
        var extension = Path.GetExtension(value);
        if (!extension.Equals(".csproj", StringComparison.OrdinalIgnoreCase))
            throw new SnapshotCaptureException(
                $"{source} is not a C# project and cannot be frozen safely: {value}");
    }

    private static IReadOnlyList<string> StaticExclusionRoots(string? value, string baseDirectory, string root)
    {
        if (string.IsNullOrWhiteSpace(value)) return [];
        var roots = new List<string>();
        foreach (var candidate in value.Split(';',
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (candidate.Contains("$(", StringComparison.Ordinal) ||
                candidate.Contains("@(", StringComparison.Ordinal) ||
                candidate.Contains("%(", StringComparison.Ordinal)) continue;
            var staticPart = candidate.Split(['*', '?'], 2)[0];
            var suffix = candidate[staticPart.Length..].Replace('\\', '/');
            if (string.IsNullOrWhiteSpace(staticPart) ||
                !staticPart.EndsWith('/') && !staticPart.EndsWith('\\') ||
                suffix is not ("**" or "**/*")) continue;
            var resolved = Path.GetFullPath(staticPart.Replace('\\', Path.DirectorySeparatorChar), baseDirectory);
            EnsureWithin(root, resolved, "Build exclusion");
            roots.Add(NormalizeRootRelative(Path.GetRelativePath(root, resolved)));
        }
        return roots;
    }

    private static string NormalizeRootRelative(string path)
    {
        var normalized = path.Replace('\\', '/').TrimEnd('/');
        return normalized == "." ? string.Empty : normalized;
    }

    private static bool IsWithinBuildPath(string relativePath, string directory)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return string.IsNullOrEmpty(directory) ||
               string.Equals(relativePath, directory.TrimEnd('/'), comparison) ||
               relativePath.StartsWith(directory.TrimEnd('/') + "/", comparison);
    }

    private static bool IsBuildXml(string relativePath)
    {
        var extension = Path.GetExtension(relativePath);
        var name = Path.GetFileName(relativePath);
        return extension.Equals(".csproj", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".props", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".targets", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".slnx", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("NuGet.Config", StringComparison.OrdinalIgnoreCase);
    }

    private static SnapshotIdentity CreateIdentity(Inventory inventory, IReadOnlyList<SnapshotFile> files, long totalBytes)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendFramed(hash, "snapshot-v1");
        AppendFramed(hash, inventory.BaseCommit);
        AppendFramed(hash, inventory.IndexFingerprint);
        foreach (var file in files)
        {
            AppendFramed(hash, file.RelativePath);
            AppendFramed(hash, file.IsTracked ? "tracked" : "untracked");
            AppendFramed(hash, file.Exists ? "file" : "deleted");
            AppendFramed(hash, file.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
            AppendFramed(hash, file.Sha256);
        }
        return new(Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(), inventory.BaseCommit,
            inventory.IndexFingerprint, files.Count, totalBytes);
    }

    private static void AppendFramed(IncrementalHash hash, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        hash.AppendData(BitConverter.GetBytes(bytes.Length));
        hash.AppendData(bytes);
    }

    private static async Task<(long Length, string Hash)> CopyAndHashAsync(FileStream input, string destination,
        long maxBytes, CancellationToken cancellationToken)
    {
        input.Position = 0;
        FileStream output;
        try
        {
            output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new SnapshotEnvironmentException($"Snapshot staging file could not be created: {ex.Message}", ex);
        }
        await using (output)
        {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        long length = 0;
        int read;
        while (true)
        {
            try { read = await input.ReadAsync(buffer, cancellationToken); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new SnapshotDivergedException($"Input became unreadable during capture: {ex.Message}");
            }
            if (read == 0) break;
            checked { length += read; }
            if (length > maxBytes) throw new SnapshotLimitException($"Input exceeded the {maxBytes}-byte file limit while reading.");
            hash.AppendData(buffer.AsSpan(0, read));
            try { await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new SnapshotEnvironmentException($"Snapshot staging write failed: {ex.Message}", ex);
            }
        }
        try { await output.FlushAsync(cancellationToken); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new SnapshotEnvironmentException($"Snapshot staging flush failed: {ex.Message}", ex);
        }
        return (length, Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());
        }
    }

    private static async Task<(long Length, string Hash)> HashFileAsync(string path, string relativePath,
        string? gitMode, long maxBytes, CancellationToken cancellationToken)
    {
        await using var stream = OpenCapturedInput(path, relativePath, gitMode);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        long length = 0;
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken)) > 0)
        {
            checked { length += read; }
            if (length > maxBytes) throw new SnapshotLimitException($"Input exceeded the {maxBytes}-byte file limit while validating.");
            hash.AppendData(buffer.AsSpan(0, read));
        }
        return (length, Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());
    }

    private static async Task<string> RunGitAsync(string root, IReadOnlyList<string> arguments,
        SnapshotCaptureOptions options, CancellationToken cancellationToken)
    {
        var bytes = await RunGitBytesAsync(root, arguments, options, cancellationToken);
        try { return new UTF8Encoding(false, true).GetString(bytes); }
        catch (DecoderFallbackException ex)
        {
            throw new SnapshotCaptureException("Git returned a filename that is not valid UTF-8.", ex);
        }
    }

    internal static async Task<byte[]> RunGitBytesAsync(string root, IReadOnlyList<string> arguments,
        SnapshotCaptureOptions options, CancellationToken cancellationToken, byte[]? standardInput = null)
    {
        var info = new ProcessStartInfo(options.GitExecutable) { WorkingDirectory = root,
            RedirectStandardOutput = true, RedirectStandardError = true,
            RedirectStandardInput = standardInput is not null, UseShellExecute = false };
        foreach (var key in info.Environment.Keys.Where(key => key.StartsWith("GIT_", StringComparison.Ordinal)).ToArray())
            info.Environment.Remove(key);
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = info };
        var stdout = new MemoryStream();
        var stderr = new MemoryStream();
        Task output = Task.CompletedTask;
        Task error = Task.CompletedTask;
        Task input = Task.CompletedTask;
        try
        {
            if (!process.Start()) throw new SnapshotCaptureException("Could not start Git.");
            var gitTimeout = options.GitTimeout ?? TimeSpan.FromSeconds(30);
            using var timeout = new CancellationTokenSource(gitTimeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
            output = CopyBoundedAsync(process.StandardOutput.BaseStream, stdout, options.MaxGitOutputBytes,
                linked.Token);
            error = CopyBoundedAsync(process.StandardError.BaseStream, stderr, options.MaxGitOutputBytes,
                linked.Token);
            if (standardInput is not null)
                input = WriteInputAsync(process.StandardInput.BaseStream, standardInput, linked.Token);
            try
            {
                await Task.WhenAll(process.WaitForExitAsync(linked.Token), output, error, input);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested &&
                                                     !cancellationToken.IsCancellationRequested)
            {
                await TerminateAsync(process, output, error, input);
                throw new SnapshotCaptureException(
                    $"Git command exceeded the bounded {gitTimeout.TotalSeconds:g}-second capture timeout.");
            }
            catch (SnapshotLimitException)
            {
                await TerminateAsync(process, output, error, input);
                throw;
            }
            var errorText = Encoding.UTF8.GetString(stderr.ToArray());
            if (process.ExitCode != 0) throw new SnapshotCaptureException($"Git failed: {errorText.Trim()}");
            return stdout.ToArray();
        }
        catch (Win32Exception ex)
        {
            throw new SnapshotCaptureException("Could not start Git.", ex);
        }
        catch (OperationCanceledException)
        {
            await TerminateAsync(process, output, error, input);
            throw;
        }
    }

    private static async Task WriteInputAsync(Stream destination, byte[] input,
        CancellationToken cancellationToken)
    {
        await destination.WriteAsync(input, cancellationToken);
        await destination.FlushAsync(cancellationToken);
        destination.Close();
    }

    internal static async Task<byte[]> ReadRegularInputAsync(string path, string relativePath,
        long maxBytes, CancellationToken cancellationToken)
    {
        await using var stream = OpenRegularInput(path, relativePath, null);
        using var output = new MemoryStream();
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken)) > 0)
        {
            checked { total += read; }
            if (total > maxBytes)
                throw new SnapshotLimitException($"Input exceeded the {maxBytes}-byte file limit while reading.");
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
        return output.ToArray();
    }

    private static async Task CopyBoundedAsync(Stream source, Stream destination, long maxBytes,
        CancellationToken cancellationToken)
    {
        if (maxBytes < 1) throw new ArgumentOutOfRangeException(nameof(maxBytes));
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
        {
            checked { total += read; }
            if (total > maxBytes)
                throw new SnapshotLimitException($"Git output exceeded the {maxBytes}-byte capture limit.");
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
    }

    private static async Task TerminateAsync(Process process, params Task[] drains)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
        try { await process.WaitForExitAsync(CancellationToken.None); } catch { }
        foreach (var drain in drains)
            try { await drain; } catch { }
    }

    private static IReadOnlyList<string> SplitNull(string value) =>
        value.Split('\0', StringSplitOptions.RemoveEmptyEntries);

    private static Dictionary<string, string> ParseModes(string stageOutput)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var record in SplitNull(stageOutput))
        {
            var tab = record.IndexOf('\t');
            if (tab < 0) throw new SnapshotCaptureException("Unexpected Git index record.");
            var header = record[..tab].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (header.Length != 3) throw new SnapshotCaptureException("Unexpected Git index metadata.");
            if (header[0] == "0" || header[1].All(character => character == '0'))
                throw new SnapshotCaptureException($"Intent-to-add or unresolved index entry is unsupported: {record[(tab + 1)..]}");
            result[record[(tab + 1)..]] = header[0];
        }
        return result;
    }

    private static Dictionary<string, char> ParseFlags(string output)
    {
        var result = new Dictionary<string, char>(StringComparer.Ordinal);
        foreach (var record in SplitNull(output))
        {
            if (record.Length < 3 || record[1] != ' ')
                throw new SnapshotCaptureException("Unexpected Git path-flag record.");
            result[record[2..]] = record[0];
        }
        return result;
    }

    private static void RejectNuGetCredentials(Stream stream, string relativePath)
    {
        XDocument document;
        try
        {
            stream.Position = 0;
            document = XDocument.Load(stream, LoadOptions.None);
            stream.Position = 0;
        }
        catch (Exception ex) when (ex is IOException or System.Xml.XmlException)
        {
            throw new SnapshotCaptureException($"NuGet configuration is not valid XML: {relativePath}", ex);
        }
        if (document.Descendants().Any(element => element.Name.LocalName.Equals(
                "packageSourceCredentials", StringComparison.OrdinalIgnoreCase)))
            throw new SnapshotCaptureException($"NuGet credentials cannot be copied into a snapshot: {relativePath}");
    }

    private static FileStream OpenRegularInput(string path, string relativePath, string? gitMode)
    {
        RejectPathLinks(path);
        if (gitMode == "120000")
            throw new SnapshotCaptureException($"Link, reparse point, or special input is unsupported: {relativePath}");
        if (OperatingSystem.IsLinux())
        {
            if (!IsSupportedLinuxArchitecture(RuntimeInformation.ProcessArchitecture))
                throw new SnapshotCaptureException(
                    $"Safe regular-file validation is not implemented for Linux {RuntimeInformation.ProcessArchitecture}: {relativePath}");
            const int readOnly = 0;
            const int nonBlocking = 0x800;
            const int closeOnExec = 0x80000;
            const int noFollow = 0x20000;
            var descriptor = Open(path, readOnly | nonBlocking | closeOnExec | noFollow);
            if (descriptor < 0)
            {
                var error = Marshal.GetLastPInvokeError();
                if (error is 6 or 40)
                    throw new SnapshotCaptureException($"Link, reparse point, or special input is unsupported: {relativePath}");
                throw new SnapshotDivergedException($"Input became unavailable: {relativePath} ({new Win32Exception(error).Message})");
            }
            var handle = new SafeFileHandle((IntPtr)descriptor, ownsHandle: true);
            try
            {
                LinuxStat status;
                try
                {
                    if (FStat(descriptor, out status) != 0)
                    {
                        var error = Marshal.GetLastPInvokeError();
                        throw new SnapshotDivergedException(
                            $"Input type could not be validated: {relativePath} ({new Win32Exception(error).Message})");
                    }
                }
                catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or
                                           BadImageFormatException)
                {
                    throw new SnapshotCaptureException(
                        $"Native regular-file validation is unavailable on this Linux runtime: {relativePath}", ex);
                }
                const uint fileTypeMask = 0xF000;
                const uint regularFile = 0x8000;
                if ((status.Mode & fileTypeMask) != regularFile)
                    throw new SnapshotCaptureException($"Link, reparse point, or special input is unsupported: {relativePath}");
                return new FileStream(handle, FileAccess.Read, 81920, isAsync: false);
            }
            catch
            {
                handle.Dispose();
                throw;
            }
        }
        var attributes = File.GetAttributes(path);
        if ((attributes & (FileAttributes.Directory | FileAttributes.Device | FileAttributes.ReparsePoint)) != 0 ||
            gitMode == "120000")
            throw new SnapshotCaptureException($"Link, reparse point, or special input is unsupported: {relativePath}");
        return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
    }

    private static FileStream OpenCapturedInput(string path, string relativePath, string? gitMode)
    {
        try { return OpenRegularInput(path, relativePath, gitMode); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && ex is not SnapshotCaptureException)
        {
            throw new SnapshotDivergedException($"Input became unavailable during capture: {relativePath} ({ex.Message})");
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LinuxTimespec { public long Seconds; public long Nanoseconds; }

    [StructLayout(LayoutKind.Sequential)]
    private struct LinuxStat
    {
        public ulong Device;
        public ulong Inode;
        public ulong HardLinks;
        public uint Mode;
        public uint UserId;
        public uint GroupId;
        public int Padding;
        public ulong DeviceType;
        public long Size;
        public long BlockSize;
        public long Blocks;
        public LinuxTimespec AccessTime;
        public LinuxTimespec ModificationTime;
        public LinuxTimespec ChangeTime;
        public long Reserved1;
        public long Reserved2;
        public long Reserved3;
    }

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int Open(string path, int flags);

    [DllImport("libc", EntryPoint = "fstat", SetLastError = true)]
    private static extern int FStat(int descriptor, out LinuxStat status);

    internal static bool IsSupportedLinuxArchitecture(Architecture architecture) => architecture == Architecture.X64;

    private static void RejectPathLinks(string path)
    {
        for (var current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new SnapshotCaptureException($"Symlink/reparse-point path is unsupported: {path}");
            var parent = Path.GetDirectoryName(current);
            if (string.Equals(parent, current, StringComparison.Ordinal)) break;
        }
    }

    private static void EnsureWithin(string root, string path, string label)
    {
        var relative = Path.GetRelativePath(root, path);
        if (relative is ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            Path.IsPathRooted(relative))
            throw new SnapshotCaptureException($"{label} escapes the snapshot root: {path}");
    }

    private static string ToPath(string root, string relative) =>
        Path.GetFullPath(relative.Replace('/', Path.DirectorySeparatorChar), root);

    private static bool PathEquals(string left, string right) => string.Equals(left.TrimEnd(Path.DirectorySeparatorChar),
        right.TrimEnd(Path.DirectorySeparatorChar), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static void ValidateOptions(SnapshotCaptureOptions options)
    {
        if (options.MaxFiles < 1 || options.MaxTotalBytes < 1 || options.MaxFileBytes < 1 || options.MaxDepth < 1 ||
            options.MaxGitOutputBytes < 1)
            throw new ArgumentOutOfRangeException(nameof(options), "Snapshot bounds must be positive.");
        if (string.IsNullOrWhiteSpace(options.GitExecutable) || options.GitTimeout is { } timeout && timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options), "Git executable and timeout must be valid.");
    }

    private const string MissingHash = "missing";
}
