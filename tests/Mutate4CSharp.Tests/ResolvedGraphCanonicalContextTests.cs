using System.Text;

namespace Mutate4CSharp.Tests;

// Complete context DATA regressions, not native/compiler provenance. Actual
// fresh-clone/compiler cold-warm runs remain a separate mandatory acceptance.
public sealed class ResolvedGraphCanonicalContextTests
{
    [Fact]
    public void Exact_original_MSBuild_path_map_global_keeps_both_owned_roles_and_escaped_separator()
    {
        var parent = Path.Combine(Path.GetTempPath(), "graph-path-map-global");
        var first = Path.Combine(parent, "first"); var second = Path.Combine(parent, "second");
        Assert.Equal(Identity(first, PathMapContext(first)), Identity(second, PathMapContext(second)));
        using var inputs = new ResolvedGraphInputs(TimeSpan.FromSeconds(30), CancellationToken.None);
        var bytes = new ResolvedGraphCanonicalContext(Path.Combine(first, "source"), Path.Combine(first, "packages"))
            .CanonicalBytes(PathMapContext(first), [], inputs);
        using var record = System.Text.Json.JsonDocument.Parse(bytes);
        Assert.Equal("$owned-source=/_/mutate-source%2C$owned-packages=/_/mutate-packages",
            record.RootElement.GetProperty("Task").GetProperty("GlobalProperties").GetProperty("PathMap").GetString());
        var custom = PathMapContext(first);
        var before = custom.FirstCsc!.GlobalProperties.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        before["CustomEscapedValue"] = "custom%2Cvalue";
        custom = custom with { FirstCsc = custom.FirstCsc! with { GlobalProperties = before } };
        var after = before.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        after["CustomEscapedValue"] = "custom,value";
        Assert.NotEqual(Identity(first, custom), Identity(first, custom with {
            FirstCsc = custom.FirstCsc! with { GlobalProperties = after } }));
        // The same byte pattern in a custom global gets no escaped-path rule.
        ResolvedProjectCapture CustomMap(string root)
        {
            var value = PathMapContext(root);
            return value with { FirstCsc = value.FirstCsc! with {
                GlobalProperties = new Dictionary<string, string> { ["CustomMap"] = OriginalPathMap(root) } } };
        }
        Assert.NotEqual(Identity(first, CustomMap(first)), Identity(second, CustomMap(second)));
    }

    [Theory]
    [InlineData("literal-comma")]
    [InlineData("lowercase-escape")]
    [InlineData("source-trailing-separator")]
    [InlineData("package-trailing-separator")]
    [InlineData("source-dot-alias")]
    [InlineData("different-destination")]
    public void Path_map_global_rejects_aliases_instead_of_decoding_or_omitting_them(string corruption)
    {
        var root = Path.Combine(Path.GetTempPath(), "graph-path-map-refusal");
        var source = Path.Combine(root, "source"); var packages = Path.Combine(root, "packages");
        var raw = OriginalPathMap(root);
        raw = corruption switch {
            "literal-comma" => raw.Replace("%2C", ",", StringComparison.Ordinal),
            "lowercase-escape" => raw.Replace("%2C", "%2c", StringComparison.Ordinal),
            "source-trailing-separator" => raw.Replace(source + "=", source + Path.DirectorySeparatorChar + "=", StringComparison.Ordinal),
            "package-trailing-separator" => raw.Replace(packages + "=", packages + Path.DirectorySeparatorChar + "=", StringComparison.Ordinal),
            "source-dot-alias" => raw.Replace(source + "=", source + Path.DirectorySeparatorChar + ".=", StringComparison.Ordinal),
            _ => raw.Replace("/_/mutate-packages", "/_/other-packages", StringComparison.Ordinal)
        };
        var actual = PathMapContext(root);
        actual = actual with { FirstCsc = actual.FirstCsc! with {
            GlobalProperties = new Dictionary<string, string> { ["PathMap"] = raw } } };
        Assert.Throws<EvaluationContractException>(() => Identity(root, actual));
    }

    private static ResolvedProjectCapture PathMapContext(string root)
    {
        var actual = Context(root, "physical", "external-compiler");
        return actual with { FirstCsc = actual.FirstCsc! with { GlobalProperties =
            new Dictionary<string, string> { ["Configuration"] = "Release", ["PathMap"] = OriginalPathMap(root) } } };
    }

    private static string OriginalPathMap(string root) =>
        ResolvedGraphPathProfile.Argument(Path.Combine(root, "source"), Path.Combine(root, "packages"))["-p:PathMap=".Length..];

    [Fact]
    public void Derived_owned_directory_joins_full_path_before_fresh_root_normalization()
    {
        var parent = Path.Combine(Path.GetTempPath(), "graph-derived-directory");
        var firstRoot = Path.Combine(parent, "first"); var secondRoot = Path.Combine(parent, "second");
        ResolvedProjectCapture Actual(string root)
        {
            var actual = Context(root, "physical", "external-compiler");
            var path = Path.Combine(root, "source", "A.cs");
            return actual with { Items = new Dictionary<string, IReadOnlyList<ResolvedGraphItem>> {
                ["Compile"] = [new("A.cs", ItemMetadata(path))] } };
        }
        Assert.Equal(Identity(firstRoot, Actual(firstRoot)), Identity(secondRoot, Actual(secondRoot)));
        var changed = Actual(secondRoot);
        var metadata = ItemMetadata(Path.Combine(secondRoot, "source", "A.cs"));
        metadata["CustomSemanticDirectory"] = "different";
        changed = changed with { Items = new Dictionary<string, IReadOnlyList<ResolvedGraphItem>> {
            ["Compile"] = [new("A.cs", metadata)] } };
        Assert.NotEqual(Identity(firstRoot, Actual(firstRoot)), Identity(secondRoot, changed));
    }

    [Theory]
    [InlineData("mismatched-directory")]
    [InlineData("directory-alias")]
    [InlineData("full-path-alias")]
    public void Owned_derived_directory_does_not_accept_mismatched_or_aliased_joins(string corruption)
    {
        var root = Path.Combine(Path.GetTempPath(), "graph-derived-invalid");
        var path = Path.Combine(root, "source", "A.cs");
        var metadata = ItemMetadata(path);
        if (corruption == "mismatched-directory") metadata["Directory"] += "unowned" + Path.DirectorySeparatorChar;
        else if (corruption == "directory-alias") metadata["Directory"] += "." + Path.DirectorySeparatorChar;
        else metadata["FullPath"] = Path.Combine(root, "source", ".", "A.cs");
        var actual = Context(root, "physical", "compiler") with {
            Items = new Dictionary<string, IReadOnlyList<ResolvedGraphItem>> { ["Compile"] = [new("A.cs", metadata)] } };
        Assert.Throws<EvaluationContractException>(() => Identity(root, actual));
    }

    [Fact]
    public void Only_exact_reread_boundary_file_and_derived_directory_have_one_fresh_root_role()
    {
        var parent = Path.Combine(Path.GetTempPath(), "graph-boundary-data-" + Guid.NewGuid().ToString("N"));
        var firstRoot = Path.Combine(parent, "first"); var secondRoot = Path.Combine(parent, "second");
        Directory.CreateDirectory(firstRoot); Directory.CreateDirectory(secondRoot);
        var firstPath = Path.Combine(firstRoot, ".editorconfig"); var secondPath = Path.Combine(secondRoot, ".editorconfig");
        File.WriteAllBytes(firstPath, "root = true\n"u8.ToArray());
        File.WriteAllBytes(secondPath, "root = true\n"u8.ToArray());
        try
        {
            var first = BoundaryContext(firstRoot, firstPath);
            var second = BoundaryContext(secondRoot, secondPath);
            Assert.Equal(Identity(firstRoot, first), Identity(secondRoot, second));
            // A sibling prefix and a dot-segment alias are not this exact owned
            // file. Neither earns the parent-directory role or byte admission.
            Assert.NotEqual(Identity(secondRoot, second),
                Identity(secondRoot, BoundaryContext(secondRoot, secondPath + "-unowned")));
            Assert.NotEqual(Identity(secondRoot, second),
                Identity(secondRoot, BoundaryContext(secondRoot, Path.Combine(secondRoot, ".", ".editorconfig"))));
            var invalidMetadata = ItemMetadata(secondPath);
            invalidMetadata["RelativeDir"] += "unowned" + Path.DirectorySeparatorChar;
            var invalidBoundary = second with { Items = new Dictionary<string, IReadOnlyList<ResolvedGraphItem>> {
                ["EditorConfigFiles"] = [new(secondPath, invalidMetadata)] } };
            Assert.Throws<EvaluationContractException>(() => Identity(secondRoot, invalidBoundary));
            var unrelated = Context(firstRoot, "physical", "compiler");
            var properties = unrelated.Properties.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
            properties["CustomParentPath"] = firstRoot;
            unrelated = unrelated with { Properties = properties };
            Assert.NotEqual(Identity(firstRoot, unrelated), Identity(firstRoot, Context(firstRoot, "physical", "compiler")));
            var retained = new ResolvedGraphFile(secondPath, "config", "root = true\n"u8.ToArray(), new string('1', 64));
            string WithRetained(ResolvedGraphFile file)
            {
                using var inputs = new ResolvedGraphInputs(TimeSpan.FromSeconds(30), CancellationToken.None);
                return new ResolvedGraphCanonicalContext(Path.Combine(secondRoot, "source"), Path.Combine(secondRoot, "packages"))
                    .Identity(second, [file], inputs);
            }
            Assert.NotEqual(WithRetained(retained), WithRetained(retained with { Sha256 = new string('2', 64) }));
            File.WriteAllBytes(secondPath, "root = trueX"u8.ToArray());
            Assert.Throws<EvaluationContractException>(() => Identity(secondRoot, second));
        }
        finally { Directory.Delete(parent, recursive: true); }
    }

    private static ResolvedProjectCapture BoundaryContext(string root, string boundary)
    {
        var actual = Context(root, "physical", "external-compiler");
        var items = actual.Items.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        items["EditorConfigFiles"] = [new(boundary, ItemMetadata(boundary))];
        return actual with { Items = items,
            FirstCsc = actual.FirstCsc! with { Tokens = ["/define:ONE", "/analyzerconfig:" + boundary] } };
    }

    private static Dictionary<string, string> ItemMetadata(string path)
    {
        var directory = Path.GetDirectoryName(path)!;
        var complete = Path.EndsInDirectorySeparator(directory) ? directory : directory + Path.DirectorySeparatorChar;
        var root = Path.GetPathRoot(path)!;
        return new(StringComparer.Ordinal) { ["Identity"] = path, ["FullPath"] = path, ["RootDir"] = root,
            ["Directory"] = complete[root.Length..], ["RelativeDir"] = complete,
            ["CustomSemanticDirectory"] = "unchanged" };
    }

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
