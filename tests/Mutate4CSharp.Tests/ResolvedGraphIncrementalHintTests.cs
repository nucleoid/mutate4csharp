using System.Text;
using System.Text.Json;

namespace Mutate4CSharp.Tests;

// Complete owned-file DATA controls. These do not substitute for actual fresh
// compiler graph/cold-warm acceptance or prove the origin of an MSBuild value.
public sealed class ResolvedGraphIncrementalHintTests
{
    [Fact]
    public void Exact_owned_pair_hint_is_equivalent_with_both_complete_imports_and_raw_query_retained()
    {
        using var fixture = new Fixture();
        var props = fixture.Context(fixture.Props);
        var targets = fixture.Context(fixture.Targets);
        Assert.NotEqual(props.Properties["MSBuildAllProjects"], targets.Properties["MSBuildAllProjects"]);
        Assert.Equal(fixture.Identity(props), fixture.Identity(targets));
        using var record = JsonDocument.Parse(fixture.Bytes(props));
        Assert.Equal("$owned-generated-nuget-pair;;custom-first;custom-second",
            record.RootElement.GetProperty("Properties").GetProperty("MSBuildAllProjects").GetString());
        Assert.Equal(2, record.RootElement.GetProperty("IncrementalHintImports").GetArrayLength());
        Assert.StartsWith(fixture.Props, props.Properties["MSBuildAllProjects"], StringComparison.Ordinal);
    }

    [Fact]
    public void Both_complete_generated_imports_only_normalize_exact_owned_root_roles()
    {
        using var first = new Fixture(); using var second = new Fixture();
        Assert.Equal(first.Identity(first.Context(first.Props)), second.Identity(second.Context(second.Targets)));
        var before = second.Identity(second.Context(second.Targets));
        File.AppendAllText(second.Props, "custom-last-byte=A", new UTF8Encoding(false));
        var changedProps = second.Identity(second.Context(second.Targets));
        Assert.NotEqual(before, changedProps); // The non-winning file still binds.
        File.AppendAllText(second.Targets, "custom-last-byte=B", new UTF8Encoding(false));
        Assert.NotEqual(changedProps, second.Identity(second.Context(second.Targets)));
    }

    [Fact]
    public void Actual_reread_source_changes_still_change_the_complete_semantic_record()
    {
        using var fixture = new Fixture();
        var source = Path.Combine(fixture.Source, "App.cs");
        string Identity(string hint)
        {
            using var inputs = new ResolvedGraphInputs(TimeSpan.FromSeconds(30), CancellationToken.None);
            return new ResolvedGraphCanonicalContext(fixture.Source, fixture.Packages)
                .Identity(fixture.Context(hint), [inputs.Read(source, "source")], inputs);
        }
        File.WriteAllText(source, "class App { int Value => 1; }", new UTF8Encoding(false));
        var before = Identity(fixture.Props);
        Assert.Equal(before, Identity(fixture.Targets));
        File.WriteAllText(source, "class App { int Value => 2; }", new UTF8Encoding(false));
        Assert.NotEqual(before, Identity(fixture.Targets));
    }

    [Fact]
    public void Generated_import_xml_text_does_not_normalize_unowned_prefixes()
    {
        using var first = new Fixture(); using var second = new Fixture();
        File.AppendAllText(first.Props, "<Custom>" + first.Source + "-unowned</Custom>", new UTF8Encoding(false));
        File.AppendAllText(second.Props, "<Custom>" + second.Source + "-unowned</Custom>", new UTF8Encoding(false));
        Assert.NotEqual(first.Identity(first.Context(first.Props)), second.Identity(second.Context(second.Targets)));
    }

    [Theory]
    [InlineData("suffix-value")]
    [InlineData("suffix-order")]
    [InlineData("suffix-empty-slot")]
    [InlineData("source-option")]
    [InlineData("custom-property")]
    public void Custom_suffix_order_and_actual_compiler_semantics_remain_significant(string change)
    {
        using var fixture = new Fixture();
        var before = fixture.Context(fixture.Props);
        var after = fixture.Context(fixture.Targets);
        if (change.StartsWith("suffix-", StringComparison.Ordinal))
            after = Properties(after, "MSBuildAllProjects", fixture.Targets + (change switch {
                "suffix-value" => ";;custom-first;changed",
                "suffix-order" => ";;custom-second;custom-first",
                _ => ";custom-first;custom-second" }));
        else if (change == "source-option")
            after = after with { FirstCsc = after.FirstCsc! with { Tokens = ["/define:CHANGED"] } };
        else after = Properties(after, "CustomSemanticValue", "changed");
        Assert.NotEqual(fixture.Identity(before), fixture.Identity(after));
    }

    [Theory]
    [InlineData("MSBuildAllProjects")]
    [InlineData("msbuildallprojects")]
    public void Global_override_does_not_receive_even_the_exact_owned_pair_rule(string key)
    {
        using var fixture = new Fixture();
        var before = fixture.Context(fixture.Props);
        var after = fixture.Context(fixture.Targets);
        ResolvedProjectCapture Global(ResolvedProjectCapture value) => value with {
            FirstCsc = value.FirstCsc! with { GlobalProperties = new Dictionary<string, string> {
                [key] = "owner-selected-custom-value" } } };
        Assert.NotEqual(fixture.Identity(Global(before)), fixture.Identity(Global(after)));
        using var record = JsonDocument.Parse(fixture.Bytes(Global(before)));
        Assert.Empty(record.RootElement.GetProperty("IncrementalHintImports").EnumerateArray());
        var changed = Global(before) with { FirstCsc = Global(before).FirstCsc! with {
            GlobalProperties = new Dictionary<string, string> { [key] = "other-value" } } };
        Assert.NotEqual(fixture.Identity(Global(before)), fixture.Identity(changed));
    }

    [Theory]
    [InlineData("first-dot-alias")]
    [InlineData("first-case-alias")]
    [InlineData("extension-dot-alias")]
    [InlineData("extension-case-alias")]
    [InlineData("assets-other-parent")]
    [InlineData("assets-other-name")]
    [InlineData("unselected-project")]
    [InlineData("foreign-root")]
    [InlineData("foreign-prefix")]
    public void Only_exact_selected_owned_directory_project_and_assets_joins_receive_the_rule(string change)
    {
        using var fixture = new Fixture();
        var normal = fixture.Context(fixture.Props);
        var other = fixture.Context(fixture.Targets);
        other = change switch {
            "first-dot-alias" => Properties(other, "MSBuildAllProjects", Path.Combine(fixture.Extensions, ".", Path.GetFileName(fixture.Targets)) + ";;custom-first;custom-second"),
            "first-case-alias" => Properties(other, "MSBuildAllProjects", fixture.Targets[..^"targets".Length] + "TARGETS;;custom-first;custom-second"),
            "extension-dot-alias" => Properties(other, "MSBuildProjectExtensionsPath", Path.Combine(fixture.Extensions, ".") + Path.DirectorySeparatorChar),
            "extension-case-alias" => Properties(other, "MSBuildProjectExtensionsPath", fixture.Extensions[..^3] + "OBJ" + Path.DirectorySeparatorChar),
            "assets-other-parent" => Properties(other, "ProjectAssetsFile", Path.Combine(fixture.Source, "other", "project.assets.json")),
            "assets-other-name" => Properties(other, "ProjectAssetsFile", Path.Combine(fixture.Extensions, "custom.assets.json")),
            "unselected-project" => Properties(other, "MSBuildProjectFullPath", Path.Combine(fixture.Source, "Other.csproj")),
            _ => Foreign(other, change == "foreign-root" ? fixture.Packages : fixture.Source + "-unowned") };
        Assert.NotEqual(fixture.Identity(normal), fixture.Identity(other));
        using var record = JsonDocument.Parse(fixture.Bytes(other));
        Assert.Empty(record.RootElement.GetProperty("IncrementalHintImports").EnumerateArray());
    }

    [Theory]
    [InlineData("missing-targets")]
    [InlineData("malformed-utf8")]
    [InlineData("over-bound")]
    public void Incomplete_or_unreadable_pair_cannot_earn_semantic_equivalence(string change)
    {
        using var fixture = new Fixture();
        if (change == "missing-targets") File.Delete(fixture.Targets);
        else if (change == "malformed-utf8") File.WriteAllBytes(fixture.Targets, [0xC3, 0x28]);
        else File.WriteAllBytes(fixture.Targets, new byte[65_537]);
        // Dispose may repeat the retained primary failure; never retry the
        // failed input owner or interpret this DATA refusal as native proof.
        var failure = Record.Exception(() => fixture.Identity(fixture.Context(fixture.Props)));
        Assert.NotNull(failure);
        Assert.True(change == "malformed-utf8" ? failure is DecoderFallbackException : failure is AggregateException);
    }

    private static ResolvedProjectCapture Properties(ResolvedProjectCapture value, string key, string text)
    {
        var properties = value.Properties.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        properties[key] = text;
        return value with { Properties = properties };
    }

    private static ResolvedProjectCapture Foreign(ResolvedProjectCapture value, string directory)
    {
        var project = Path.Combine(directory, "App.csproj");
        value = Properties(value, "MSBuildProjectFullPath", project);
        value = Properties(value, "MSBuildProjectExtensionsPath", directory + Path.DirectorySeparatorChar);
        value = Properties(value, "ProjectAssetsFile", Path.Combine(directory, "project.assets.json"));
        value = Properties(value, "MSBuildAllProjects", project + ".nuget.g.targets;;custom-first;custom-second");
        return value with { FirstCsc = value.FirstCsc! with { ProjectFile = project } };
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "mutate-incremental-hint-" + Guid.NewGuid().ToString("N"));
        internal string Source => Path.Combine(_root, "source");
        internal string Packages => Path.Combine(_root, "packages");
        internal string Extensions => Path.Combine(Source, "obj");
        internal string Props => Path.Combine(Extensions, "App.csproj.nuget.g.props");
        internal string Targets => Path.Combine(Extensions, "App.csproj.nuget.g.targets");
        internal Fixture()
        {
            Directory.CreateDirectory(Extensions); Directory.CreateDirectory(Packages);
            // These are complete fixture DATA, not forged native/NuGet proof.
            File.WriteAllText(Props, "<Project><OwnedSource>" + Source + "</OwnedSource><Packages>" + Packages + "</Packages></Project>\n", new UTF8Encoding(false));
            File.WriteAllText(Targets, "<Project><Custom>significant</Custom></Project>\n", new UTF8Encoding(false));
        }
        internal ResolvedProjectCapture Context(string hint)
        {
            var project = Path.Combine(Source, "App.csproj");
            return new(new("App.csproj", "net10.0", "Release"), new Dictionary<string, string> {
                ["MSBuildProjectFullPath"] = project,
                ["MSBuildProjectExtensionsPath"] = Extensions + Path.DirectorySeparatorChar,
                ["ProjectAssetsFile"] = Path.Combine(Extensions, "project.assets.json"),
                ["MSBuildAllProjects"] = hint + ";;custom-first;custom-second" },
                new Dictionary<string, IReadOnlyList<ResolvedGraphItem>>(), [], "raw-" + hint,
                new("actual-data", project, new Dictionary<string, string>(), "external-compiler", "external-target", ["/define:SAME"], false));
        }
        internal string Identity(ResolvedProjectCapture value)
        {
            using var inputs = new ResolvedGraphInputs(TimeSpan.FromSeconds(30), CancellationToken.None);
            return new ResolvedGraphCanonicalContext(Source, Packages).Identity(value, [], inputs);
        }
        internal byte[] Bytes(ResolvedProjectCapture value)
        {
            using var inputs = new ResolvedGraphInputs(TimeSpan.FromSeconds(30), CancellationToken.None);
            return new ResolvedGraphCanonicalContext(Source, Packages).CanonicalBytes(value, [], inputs);
        }
        public void Dispose() => Directory.Delete(_root, recursive: true);
    }
}
