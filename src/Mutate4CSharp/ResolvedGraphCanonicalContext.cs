using System.Text;
using System.Text.Json;
using System.Security.Cryptography;

namespace Mutate4CSharp;

// Raw event/query/image evidence remains retained separately. Only these exact
// creation-owned roots, fixed editorconfig boundary and exact generated NuGet
// pair's first incremental hint have explicit equivalence rules. Complete pair
// contents, custom suffixes and external compiler/reference/imports still bind.
internal sealed class ResolvedGraphCanonicalContext
{
    private readonly (string Root, string Role)[] _roots;
    internal ResolvedGraphCanonicalContext(string sourceRoot, string packageRoot)
    {
        _roots = [(Root(sourceRoot), "source"), (Root(packageRoot), "packages")];
        if (_roots[0].Root == _roots[1].Root || _roots.Any(r => r.Root.Length < 4))
            throw new EvaluationContractException("Distinct full owned graph roots required.");
    }

    internal string Value(string value)
        => Value(value, null);

    private string Value(string value, ResolvedGraphFile? boundary, bool generatedImport = false)
    {
        if (value.Length > 65_536 || value.Contains('\0')) throw new SnapshotLimitException("Canonical graph scalar extent.");
        if (boundary is not null)
        {
            if (SamePath(value, boundary.Path)) return "$owned-execution-boundary/.editorconfig";
            const string analyzerConfig = "/analyzerconfig:";
            if (value.StartsWith(analyzerConfig, StringComparison.Ordinal) &&
                SamePath(value[analyzerConfig.Length..], boundary.Path))
                return analyzerConfig + "$owned-execution-boundary/.editorconfig";
        }
        var roots = OperatingSystem.IsWindows() ? _roots.SelectMany(r => new[] { r, (Root: r.Root.Replace('\\', '/'), Role: r.Role) }) : _roots;
        foreach (var (root, role) in roots.OrderByDescending(r => r.Root.Length))
        {
            var start = 0;
            while (start < value.Length)
            {
                var index = value.IndexOf(root, start, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
                if (index < 0) break;
                var end = index + root.Length;
                var before = index == 0 || value[index - 1] is '=' or ':' or ';' or ',' or '"' or ' ' ||
                    generatedImport && (value[index - 1] is '>' or '\'');
                var after = end == value.Length || value[end] is '/' or '\\' or ';' or ',' or '"' or ' ' or '=' ||
                    generatedImport && (value[end] is '<' or '\'');
                if (before && after)
                {
                    var replacement = "$owned-" + role;
                    value = value[..index] + replacement + value[end..];
                    start = index + replacement.Length;
                }
                else start = end;
            }
        }
        return value;
    }

    internal string Identity(ResolvedProjectCapture actual, IReadOnlyList<ResolvedGraphFile> retained, ResolvedGraphInputs inputs)
    {
        var bytes = CanonicalBytes(actual, retained, inputs);
        // The complete bounded record can exceed the identity framing API's
        // 16KiB scalar limit. Hash EVERY canonical byte, then frame its fixed
        // digest under a new domain; never truncate or expand that global limit.
        return MutationIdentity.ComputeDigest("resolved-canonical-context-v5",
            [("complete-semantic-record-sha256", Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant())]);
    }

    internal byte[] CanonicalBytes(ResolvedProjectCapture actual, IReadOnlyList<ResolvedGraphFile> retained, ResolvedGraphInputs inputs)
    {
        var first = actual.FirstCsc ?? throw new EvaluationContractException("Original Csc context required.");
        var boundary = ReadObservedBoundary(actual, retained, inputs);
        var incremental = ReadIncrementalHint(actual, inputs);
        // Do not include process/event numeric IDs or complete raw-query SHA in
        // semantic identity: they are physical joins, retained on the original.
        var semantic = new {
            actual.Selection, Properties = incremental.Properties,
            IncrementalHintImports = incremental.Files.Select(file => new {
                Path = Value(file.Path), SemanticBytes = GeneratedImportIdentity(file, inputs) }).ToArray(),
            Items = actual.Items.ToDictionary(p => p.Key, p => p.Value.Select(i => new { i.Identity,
                Metadata = SemanticMetadata(i.Metadata, boundary) }).ToArray(), StringComparer.Ordinal),
            Edges = actual.Edges.Select(e => new { e.Selection, ActualMetadata = SemanticMetadata(e.ActualMetadata, boundary) }).ToArray(),
            Task = new { first.ProjectFile, GlobalProperties = SemanticGlobalProperties(first.GlobalProperties), first.EvaluatedProperties,
                first.TaskAssemblyLocation, first.TaskFile, first.Tokens, first.SkipCompilerExecution },
            Inputs = retained.Select(f => new { Path = Value(f.Path, boundary), f.Role, SemanticBytes = InputIdentity(f, inputs) })
                .OrderBy(f => f.Path, StringComparer.Ordinal).ThenBy(f => f.Role, StringComparer.Ordinal).ToArray(),
            ExecutionBoundary = boundary is null ? null : new { Path = "$owned-execution-boundary/.editorconfig", boundary.Sha256 }
        };
        var encoded = ResolvedGraphProtocol.SerializeBounded(semantic, inputs, ResolvedGraphCapture.MaxQueryBytes, 1_048_576);
        using var document = JsonDocument.Parse(encoded, new JsonDocumentOptions { MaxDepth = 32 });
        var bytes = ResolvedGraphProtocol.WriteBounded(writer => Write(document.RootElement, writer, 0, boundary),
            inputs, ResolvedGraphCapture.MaxQueryBytes, 1_048_576);
        if (boundary is not null) inputs.Recheck(boundary, 12);
        foreach (var file in incremental.Files) inputs.Recheck(file, 65_536);
        inputs.CheckWork();
        inputs.ReserveAllocation(4L * bytes.Length);
        return bytes;
    }

    private (IReadOnlyDictionary<string, string> Properties, ResolvedGraphFile[] Files) ReadIncrementalHint(
        ResolvedProjectCapture actual, ResolvedGraphInputs inputs)
    {
        // MSBuild prepends a timestamp-selected import to MSBuildAllProjects.
        // This is a deliberately narrow semantic profile, NOT proof of which
        // target assigned the property: a custom target choosing this SAME
        // exact pair also receives this equivalence. Its full suffix, globals,
        // Csc inputs/options and BOTH complete import contents remain significant.
        // Unknown paths, aliases and global overrides get no first-slot rule.
        if (!actual.Properties.TryGetValue("MSBuildAllProjects", out var raw) || raw.Length == 0 ||
            actual.FirstCsc!.GlobalProperties.Keys.Any(key => key.Equals("MSBuildAllProjects", StringComparison.OrdinalIgnoreCase)) ||
            !actual.Properties.TryGetValue("MSBuildProjectFullPath", out var project) ||
            !actual.Properties.TryGetValue("MSBuildProjectExtensionsPath", out var extensionPath) ||
            !actual.Properties.TryGetValue("ProjectAssetsFile", out var assets))
            return (actual.Properties, []);
        if (raw.Length > 65_536) throw new SnapshotLimitException("Canonical incremental hint extent.");
        var separator = raw.IndexOf(';');
        var hint = separator < 0 ? raw : raw[..separator];
        if (!ExactPath(project) || !ExactPath(extensionPath) || !ExactPath(assets) || !ExactPath(hint))
            return (actual.Properties, []);
        var extensions = Path.TrimEndingDirectorySeparator(extensionPath);
        var selected = Path.GetFullPath(actual.Selection.Project, _roots[0].Root);
        if (!project.Equals(selected, StringComparison.Ordinal) ||
            !project.Equals(actual.FirstCsc.ProjectFile, StringComparison.Ordinal) ||
            !Within(_roots[0].Root, project) || !Within(_roots[0].Root, extensions) ||
            !string.Equals(Path.GetDirectoryName(assets), extensions, StringComparison.Ordinal) ||
            !string.Equals(Path.GetFileName(assets), "project.assets.json", StringComparison.Ordinal))
            return (actual.Properties, []);
        // No guessed obj/bin location or filename wildcard. These exact names
        // join the selected project filename and its actually queried directory.
        var props = Path.Combine(extensions, Path.GetFileName(project) + ".nuget.g.props");
        var targets = Path.Combine(extensions, Path.GetFileName(project) + ".nuget.g.targets");
        if (!hint.Equals(props, StringComparison.Ordinal) && !hint.Equals(targets, StringComparison.Ordinal))
            return (actual.Properties, []);
        var files = new[] { inputs.Read(props, "import", 65_536), inputs.Read(targets, "import", 65_536) };
        var properties = actual.Properties.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        properties["MSBuildAllProjects"] = "$owned-generated-nuget-pair" + (separator < 0 ? "" : raw[separator..]);
        return (properties, files);
    }

    private string GeneratedImportIdentity(ResolvedGraphFile file, ResolvedGraphInputs inputs)
    {
        // Preserve raw bytes/SHA for the SAME bounded input owner's analysis
        // lifetime; this does not add an external artifact/export census. Only exact
        // owned source/package roots in complete throwing-UTF8 text become
        // roles; no XML parse, list sorting, omission or generic path redaction.
        inputs.ReserveAllocation(checked(8L * file.Bytes.LongLength + 256));
        var text = new UTF8Encoding(false, true).GetString(file.Bytes);
        // The exact generated pair also has XML element-text/single-quoted
        // attribute boundaries. This only extends root-token boundaries for
        // these two complete files; all other canonical strings keep Value's
        // prior rules and every XML byte outside owned-root tokens still binds.
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Value(text, null, generatedImport: true)))).ToLowerInvariant();
    }

    private static bool ExactPath(string path) => path.Length is > 0 and <= 4096 &&
        !path.Contains('\0') && Path.IsPathFullyQualified(path) &&
        path.Equals(Path.GetFullPath(path), StringComparison.Ordinal);

    private string InputIdentity(ResolvedGraphFile file, ResolvedGraphInputs inputs)
    {
        var relative = Path.GetRelativePath(_roots[0].Root, file.Path);
        var segments = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (file.Role == "config" && !Path.IsPathRooted(relative) && !segments.Contains("..", StringComparer.Ordinal) &&
            segments.Contains("obj", StringComparer.Ordinal) &&
            Path.GetFileName(file.Path).EndsWith(".GeneratedMSBuildEditorConfig.editorconfig", StringComparison.Ordinal))
        {
            // Keep the complete raw file/SHA in retained evidence. SDK-generated
            // analyzer config contains build_property.ProjectDir; only admitted
            // owned-root substrings become role values for semantic identity.
            // All custom fields and actual generated output bytes still bind.
            inputs.ReserveAllocation(checked(4L * file.Bytes.LongLength + 256));
            var text = new UTF8Encoding(false, true).GetString(file.Bytes);
            return MutationIdentity.ComputeDigest("generated-editorconfig-owned-role-v2",
                [("complete-text-sha256", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Value(text)))).ToLowerInvariant())]);
        }
        return file.Sha256;
    }

    private IReadOnlyDictionary<string, string> SemanticGlobalProperties(IReadOnlyDictionary<string, string> properties)
    {
        var result = properties.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        foreach (var property in properties.Where(p => p.Key.Equals("PathMap", StringComparison.OrdinalIgnoreCase)))
        {
            // This exact producer property retains MSBuild's literal %2C.
            // Decode no arbitrary string: admit only the complete closed pair
            // which generated this actual compiler invocation, then name roles.
            var expected = ResolvedGraphPathProfile.Argument(_roots[0].Root, _roots[1].Root)["-p:PathMap=".Length..];
            if (property.Value != expected)
                throw new EvaluationContractException("Original PathMap global must match the exact owned producer profile.");
            result[property.Key] = "$owned-source=" + ResolvedGraphPathProfile.Source +
                "%2C$owned-packages=" + ResolvedGraphPathProfile.Packages;
        }
        return result;
    }

    private ResolvedGraphFile? ReadObservedBoundary(ResolvedProjectCapture actual,
        IReadOnlyList<ResolvedGraphFile> retained, ResolvedGraphInputs inputs)
    {
        // SnapshotWorkspace creates exactly this parent-level boundary before
        // populating its owned /root clone. Never normalize the parent itself.
        var path = Path.Combine(Path.GetDirectoryName(_roots[0].Root)!, ".editorconfig");
        var observed = actual.Items.TryGetValue("EditorConfigFiles", out var configurations) &&
                configurations.Any(item => SamePath(item.Identity, path)) ||
            retained.Any(file => file.Role == "config" && SamePath(file.Path, path)) ||
            actual.FirstCsc!.Tokens.Any(token => token.StartsWith("/analyzerconfig:", StringComparison.Ordinal) &&
                SamePath(token["/analyzerconfig:".Length..], path));
        if (!observed) return null;
        var boundary = inputs.Read(path, "config", 12);
        if (!boundary.Bytes.AsSpan().SequenceEqual("root = true\n"u8))
            throw new EvaluationContractException("Exact owned execution editorconfig boundary bytes required.");
        inputs.Recheck(boundary, 12);
        return boundary;
    }

    private IReadOnlyDictionary<string, string> SemanticMetadata(IReadOnlyDictionary<string, string> metadata,
        ResolvedGraphFile? boundary)
    {
        // These three MSBuild well-known physical stat fields are kept on the
        // raw original row, not interpreted as compiler semantics. All custom
        // metadata, defining imports, values, ordering and byte hashes remain.
        var result = metadata.Where(p => p.Key is not ("ModifiedTime" or "CreatedTime" or "AccessedTime"))
            .ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        if (!metadata.TryGetValue("FullPath", out var fullPath) || !Path.IsPathFullyQualified(fullPath) ||
            !metadata.TryGetValue("RootDir", out var root) || !metadata.TryGetValue("Directory", out var directory))
            return result;
        var owned = _roots.Any(item => Within(item.Root, fullPath));
        var boundaryFile = boundary is not null && SamePath(fullPath, boundary.Path);
        if (!owned && !boundaryFile) return result;
        var fullDirectory = Path.GetDirectoryName(fullPath)!;
        var expected = Path.EndsInDirectorySeparator(fullDirectory) ? fullDirectory : fullDirectory + Path.DirectorySeparatorChar;
        if (!SamePath(Path.GetFullPath(fullPath), fullPath) || !SamePath(root, Path.GetPathRoot(fullPath)!) ||
            Path.IsPathRooted(directory) || !SamePath(root + directory, expected))
            throw new EvaluationContractException("Owned item Directory must join its exact RootDir and FullPath.");
        result["Directory"] = boundaryFile ? "$owned-execution-boundary/" : Value(expected);
        if (boundaryFile && metadata.TryGetValue("RelativeDir", out var relativeDirectory))
        {
            if (!metadata.TryGetValue("Identity", out var identity) || !SamePath(identity, fullPath) ||
                !SamePath(relativeDirectory, expected))
                throw new EvaluationContractException("Owned boundary RelativeDir must join its exact absolute item identity.");
            result["RelativeDir"] = "$owned-execution-boundary/";
        }
        return result;
    }

    private void Write(JsonElement node, Utf8JsonWriter writer, int depth, ResolvedGraphFile? boundary)
    {
        if (depth > 32) throw new SnapshotLimitException("Canonical graph depth.");
        switch (node.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in node.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                { writer.WritePropertyName(property.Name); Write(property.Value, writer, depth + 1, boundary); }
                writer.WriteEndObject(); break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in node.EnumerateArray()) Write(item, writer, depth + 1, boundary);
                writer.WriteEndArray(); break;
            case JsonValueKind.String: writer.WriteStringValue(Value(node.GetString()!, boundary)); break;
            default: node.WriteTo(writer); break;
        }
    }
    private static bool SamePath(string left, string right) => left.Equals(right,
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    private static bool Within(string root, string path) => SamePath(path, root) ||
        path.StartsWith(root + Path.DirectorySeparatorChar,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    private static string Root(string value) => Path.GetFullPath(value).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}
