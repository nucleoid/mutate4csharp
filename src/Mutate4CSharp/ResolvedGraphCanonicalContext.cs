using System.Text;
using System.Text.Json;
using System.Security.Cryptography;

namespace Mutate4CSharp;

// Raw event/query/image evidence remains retained separately. Only these exact
// creation-owned roots and independently reread fixed editorconfig boundary may
// vary between equivalent cold invocations. External SDK/compiler/reference/
// import names and all values/bytes remain significant.
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

    private string Value(string value, ResolvedGraphFile? boundary)
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
                var before = index == 0 || value[index - 1] is '=' or ':' or ';' or ',' or '"' or ' ';
                var after = end == value.Length || value[end] is '/' or '\\' or ';' or ',' or '"' or ' ' or '=';
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
        return MutationIdentity.ComputeDigest("resolved-canonical-context-v4",
            [("complete-semantic-record-sha256", Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant())]);
    }

    internal byte[] CanonicalBytes(ResolvedProjectCapture actual, IReadOnlyList<ResolvedGraphFile> retained, ResolvedGraphInputs inputs)
    {
        var first = actual.FirstCsc ?? throw new EvaluationContractException("Original Csc context required.");
        var boundary = ReadObservedBoundary(actual, retained, inputs);
        // Do not include process/event numeric IDs or complete raw-query SHA in
        // semantic identity: they are physical joins, retained on the original.
        var semantic = new {
            actual.Selection, actual.Properties,
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
        inputs.CheckWork();
        inputs.ReserveAllocation(4L * bytes.Length);
        return bytes;
    }

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
