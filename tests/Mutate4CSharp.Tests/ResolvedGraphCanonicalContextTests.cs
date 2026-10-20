using System.Text;

namespace Mutate4CSharp.Tests;

// Complete context DATA regressions, not native/compiler provenance. Actual
// fresh-clone/compiler cold-warm runs remain a separate mandatory acceptance.
public sealed class ResolvedGraphCanonicalContextTests
{
    [Fact]
    public void Complete_large_context_hashes_the_suffix_beyond_identity_scalar_bounds()
    {
        var root = Path.Combine(Path.GetTempPath(), "graph-large-context");
        var actual = Context(root, "physical", "compiler");
        var properties = actual.Properties.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        properties["LargeActualProperty"] = new string('x', 40 * 1024) + "A";
        var before = Identity(root, actual with { Properties = properties });
        properties = properties.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        properties["LargeActualProperty"] = new string('x', 40 * 1024) + "B";
        Assert.Equal(64, before.Length);
        Assert.NotEqual(before, Identity(root, actual with { Properties = properties }));
    }

    [Fact]
    public void Complete_large_generated_config_keeps_last_byte_semantic_after_owned_root_mapping()
    {
        var root = Path.Combine(Path.GetTempPath(), "graph-large-config");
        var actual = Context(root, "physical", "compiler");
        var source = Path.Combine(root, "source");
        string Hash(string suffix)
        {
            using var inputs = new ResolvedGraphInputs(TimeSpan.FromSeconds(30), CancellationToken.None);
            var bytes = Encoding.UTF8.GetBytes("build_property.ProjectDir=" + source + "\ncustom=" + new string('x', 32 * 1024) + suffix);
            var file = new ResolvedGraphFile(Path.Combine(source, "obj", "App.GeneratedMSBuildEditorConfig.editorconfig"),
                "config", bytes, new string('1', 64));
            return new ResolvedGraphCanonicalContext(source, Path.Combine(root, "packages")).Identity(actual, [file], inputs);
        }
        Assert.NotEqual(Hash("A"), Hash("B"));
    }

    [Fact]
    public void Fresh_owned_roots_do_not_change_semantics_but_raw_evidence_and_external_inputs_remain_distinct()
    {
        var parent = Path.Combine(Path.GetTempPath(), "graph-canonical-" + Guid.NewGuid().ToString("N"));
        var first = Context(Path.Combine(parent, "first"), "a", "external-compiler-1");
        var second = Context(Path.Combine(parent, "second"), "b", "external-compiler-1");
        Assert.NotEqual(first.RawSha256, second.RawSha256);
        Assert.Equal(Identity(Path.Combine(parent, "first"), first), Identity(Path.Combine(parent, "second"), second));
        Assert.NotEqual(Identity(Path.Combine(parent, "first"), first),
            Identity(Path.Combine(parent, "second"), Context(Path.Combine(parent, "second"), "b", "external-compiler-2")));
        var changedProperties = second.Properties.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        changedProperties["DefineConstants"] = "DIFFERENT";
        var changed = second with { Properties = changedProperties };
        Assert.NotEqual(Identity(Path.Combine(parent, "first"), first), Identity(Path.Combine(parent, "second"), changed));
        var canonical = new ResolvedGraphCanonicalContext(Path.Combine(parent, "first", "source"), Path.Combine(parent, "first", "packages"));
        var foreignPrefix = Path.Combine(parent, "first", "source-unowned", "Private.cs");
        Assert.Equal(foreignPrefix, canonical.Value(foreignPrefix));
    }

    [Fact]
    public void Complete_input_bytes_array_order_and_custom_metadata_are_semantic()
    {
        var root = Path.Combine(Path.GetTempPath(), "graph-identity");
        var actual = Context(root, "raw", "compiler");
        var file = new ResolvedGraphFile(Path.Combine(root, "source", "A.cs"), "source", Encoding.UTF8.GetBytes("one"), new string('1', 64));
        string Hash(ResolvedProjectCapture value, ResolvedGraphFile input)
        {
            using var inputs = new ResolvedGraphInputs(TimeSpan.FromSeconds(30), CancellationToken.None);
            return new ResolvedGraphCanonicalContext(Path.Combine(root, "source"), Path.Combine(root, "packages")).Identity(value, [input], inputs);
        }
        var before = Hash(actual, file);
        Assert.NotEqual(before, Hash(actual, file with { Sha256 = new string('2', 64) }));
        var items = actual.Items.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        items["Compile"] = [new("A.cs", new Dictionary<string, string> { ["CustomSemanticMetadata"] = "different" })];

        Assert.NotEqual(before, Hash(actual with { Items = items }, file));
        var reordered = actual with { FirstCsc = actual.FirstCsc! with { Tokens = ["/define:TWO", "/define:ONE"] } };
        Assert.NotEqual(before, Hash(reordered, file));
    }

    private static string Identity(string root, ResolvedProjectCapture actual)
    {
        using var inputs = new ResolvedGraphInputs(TimeSpan.FromSeconds(30), CancellationToken.None);
        return new ResolvedGraphCanonicalContext(Path.Combine(root, "source"), Path.Combine(root, "packages")).Identity(actual, [], inputs);
    }
    private static ResolvedProjectCapture Context(string root, string physical, string compiler)
    {
        var source = Path.Combine(root, "source");
        var project = Path.Combine(source, "App.csproj");
        var task = new ResolvedGraphObservedTask("physical-" + physical, project,
            new Dictionary<string, string> { ["Configuration"] = "Release" }, compiler, "selected-stock-target",
            ["/define:ONE", "/define:TWO"], false) { ProjectInstanceId = physical == "a" ? 1 : 2 };
        return new(new("App.csproj", "net10.0", "Release"),
            new Dictionary<string, string> { ["MSBuildProjectFullPath"] = project, ["NuGetPackageRoot"] = Path.Combine(root, "packages"), ["DefineConstants"] = "SAME" },
            new Dictionary<string, IReadOnlyList<ResolvedGraphItem>> { ["Compile"] = [new("A.cs", new Dictionary<string, string> {
                ["FullPath"] = Path.Combine(source, "A.cs"), ["ModifiedTime"] = physical })] }, [], physical, task);
    }
}
