using System.Text;
using System.Text.Json;

namespace Mutate4CSharp;

// Raw event/query/image evidence remains retained separately. Only these exact
// creation-owned roots may vary between equivalent cold invocations. External
// SDK/compiler/reference/import names and all values/bytes remain significant.
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
    {
        if (value.Length > 65_536 || value.Contains('\0')) throw new SnapshotLimitException("Canonical graph scalar extent.");
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
        var first = actual.FirstCsc ?? throw new EvaluationContractException("Original Csc context required.");
        // Do not include process/event numeric IDs or complete raw-query SHA in
        // semantic identity: they are physical joins, retained on the original.
        var semantic = new {
            actual.Selection, actual.Properties,
            Items = actual.Items.ToDictionary(p => p.Key, p => p.Value.Select(i => new { i.Identity,
                Metadata = SemanticMetadata(i.Metadata) }).ToArray(), StringComparer.Ordinal),
            Edges = actual.Edges.Select(e => new { e.Selection, ActualMetadata = SemanticMetadata(e.ActualMetadata) }).ToArray(),
            Task = new { first.ProjectFile, first.GlobalProperties, first.EvaluatedProperties,
                first.TaskAssemblyLocation, first.TaskFile, first.Tokens, first.SkipCompilerExecution },
            Inputs = retained.Select(f => new { Path = Value(f.Path), f.Role, SemanticBytes = InputIdentity(f) })
                .OrderBy(f => f.Path, StringComparer.Ordinal).ThenBy(f => f.Role, StringComparer.Ordinal).ToArray()
        };
        var encoded = ResolvedGraphProtocol.SerializeBounded(semantic, inputs, ResolvedGraphCapture.MaxQueryBytes, 1_048_576);
        using var document = JsonDocument.Parse(encoded, new JsonDocumentOptions { MaxDepth = 32 });
        var bytes = ResolvedGraphProtocol.WriteBounded(writer => Write(document.RootElement, writer, 0),
            inputs, ResolvedGraphCapture.MaxQueryBytes, 1_048_576);
        inputs.CheckWork();
        inputs.ReserveAllocation(4L * bytes.Length);
        return MutationIdentity.ComputeDigest("resolved-canonical-context-v2",
            [("complete-semantic-record", Encoding.UTF8.GetString(bytes))]);
    }

    private string InputIdentity(ResolvedGraphFile file)
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
            var text = new UTF8Encoding(false, true).GetString(file.Bytes);
            return MutationIdentity.ComputeDigest("generated-editorconfig-owned-role-v1", [("complete-text", Value(text))]);
        }
        return file.Sha256;
    }

    private static IReadOnlyDictionary<string, string> SemanticMetadata(IReadOnlyDictionary<string, string> metadata) =>
        // These three MSBuild well-known physical stat fields are kept on the
        // raw original row, not interpreted as compiler semantics. All custom
        // metadata, defining imports, values, ordering and byte hashes remain.
        metadata.Where(p => p.Key is not ("ModifiedTime" or "CreatedTime" or "AccessedTime"))
            .ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);

    private void Write(JsonElement node, Utf8JsonWriter writer, int depth)
    {
        if (depth > 32) throw new SnapshotLimitException("Canonical graph depth.");
        switch (node.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in node.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                { writer.WritePropertyName(property.Name); Write(property.Value, writer, depth + 1); }
                writer.WriteEndObject(); break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in node.EnumerateArray()) Write(item, writer, depth + 1);
                writer.WriteEndArray(); break;
            case JsonValueKind.String: writer.WriteStringValue(Value(node.GetString()!)); break;
            default: node.WriteTo(writer); break;
        }
    }
    private static string Root(string value) => Path.GetFullPath(value).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}
