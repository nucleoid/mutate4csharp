using System.Text;
using System.Text.Json;
using System.Security.Cryptography;

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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Exact_evaluated_profile_package_alias_matches_physical_owned_root_across_fresh_pairs(bool trailingSeparator)
    {
        using var first = new Fixture(); using var second = new Fixture();
        Assert.Contains("UserProfile", ResolvedProjectCapture.PropertyNames);
        Assert.Contains("NuGetPackageRoot", ResolvedProjectCapture.PropertyNames);
        first.WritePackage(first.PackageAlias);
        second.WritePackage(second.Packages);
        var left = first.BoundContext(first.Props, trailingSeparator);
        var right = second.BoundContext(second.Targets, trailingSeparator);
        Assert.Equal(first.Identity(left), second.Identity(right));
        using var record = JsonDocument.Parse(first.Bytes(left));
        Assert.Equal(first.Profile, record.RootElement.GetProperty("Properties").GetProperty("UserProfile").GetString());
        var before = first.ImportHash(left);
        File.AppendAllText(first.Props, "<Custom>still-significant</Custom>", new UTF8Encoding(false));
        Assert.NotEqual(before, first.ImportHash(left));
    }

    [Theory]
    [InlineData("missing-profile")]
    [InlineData("missing-packages")]
    [InlineData("relative-profile")]
    [InlineData("profile-dot-alias")]
    [InlineData("foreign-profile")]
    [InlineData("foreign-packages")]
    [InlineData("package-dot-alias")]
    [InlineData("UserProfile")]
    [InlineData("userprofile")]
    [InlineData("NuGetPackageRoot")]
    [InlineData("nugetpackageroot")]
    public void Unsupported_query_bindings_and_global_overrides_keep_complete_alias_bytes(string change)
    {
        using var fixture = new Fixture();
        var text = fixture.WritePackage(fixture.PackageAlias);
        var value = fixture.BoundContext(fixture.Props);
        if (change.StartsWith("missing-", StringComparison.Ordinal))
        {
            var properties = value.Properties.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
            properties.Remove(change == "missing-profile" ? "UserProfile" : "NuGetPackageRoot");
            value = value with { Properties = properties };
        }
        else if (change is "relative-profile" or "profile-dot-alias" or "foreign-profile")
            value = Properties(value, "UserProfile", change switch {
                "relative-profile" => "relative-profile",
                "profile-dot-alias" => Path.Combine(fixture.Profile, "."),
                _ => fixture.Source });
        else if (change is "foreign-packages" or "package-dot-alias")
            value = Properties(value, "NuGetPackageRoot", change == "foreign-packages" ? fixture.Source : Path.Combine(fixture.Packages, "."));
        else value = value with { FirstCsc = value.FirstCsc! with {
            GlobalProperties = new Dictionary<string, string> { [change] = "explicit-global" } } };
        // Inspect the import digest itself: changed query/global fields cannot
        // make this pass merely by changing the surrounding semantic record.
        Assert.Equal(Sha(text), fixture.ImportHash(value));
        Assert.NotEqual(fixture.ImportHash(fixture.BoundContext(fixture.Props)), fixture.ImportHash(value));
    }

    [Theory]
    [InlineData("macro-case")]
    [InlineData("unknown-macro")]
    [InlineData("suffix-case")]
    [InlineData("unowned-suffix")]
    [InlineData("unowned-prefix")]
    public void Other_macro_spellings_and_partial_path_tokens_are_not_expanded(string change)
    {
        using var fixture = new Fixture();
        var value = fixture.BoundContext(fixture.Props);
        fixture.WritePackage(fixture.PackageAlias);
        var accepted = fixture.ImportHash(value);
        var alias = change switch {
            "macro-case" => fixture.PackageAlias.Replace("$(UserProfile)", "$(userprofile)", StringComparison.Ordinal),
            "unknown-macro" => fixture.PackageAlias.Replace("$(UserProfile)", "$(Unknown)", StringComparison.Ordinal),
            "suffix-case" => fixture.PackageAlias[..^"packages".Length] + "PACKAGES",
            "unowned-suffix" => fixture.PackageAlias + "-unowned",
            _ => "prefix" + fixture.PackageAlias };
        var text = fixture.WritePackage(alias);
        Assert.Equal(Sha(text), fixture.ImportHash(value));
        Assert.NotEqual(accepted, fixture.ImportHash(value));
    }

    [Fact]
    public void Evaluated_profile_and_global_values_remain_significant_without_alias_replacement()
    {
        using var fixture = new Fixture();
        var value = fixture.BoundContext(fixture.Props);
        Assert.NotEqual(fixture.Identity(value), fixture.Identity(Properties(value, "UserProfile", fixture.Source)));
        var first = value with { FirstCsc = value.FirstCsc! with {
            GlobalProperties = new Dictionary<string, string> { ["UserProfile"] = "first-global" } } };
        var second = first with { FirstCsc = first.FirstCsc! with {
            GlobalProperties = new Dictionary<string, string> { ["UserProfile"] = "second-global" } } };
        Assert.NotEqual(fixture.Identity(first), fixture.Identity(second));
    }

    [Fact]
    public void Repeated_package_aliases_charge_every_actual_replacement_on_the_same_original()
    {
        using var fixture = new Fixture();
        var text = string.Concat(Enumerable.Repeat("<Root>" + fixture.PackageAlias + "</Root>\n", 300));
        Assert.InRange(Encoding.UTF8.GetByteCount(text), 16_384, 65_536);
        File.WriteAllText(fixture.Props, text, new UTF8Encoding(false));
        using var inputs = new ResolvedGraphInputs(TimeSpan.FromSeconds(30), CancellationToken.None);
        inputs.ReserveAllocation(ResolvedGraphResources.SemanticLimit - 4 * 1024 * 1024);
        var failure = Assert.Throws<SnapshotLimitException>(() => new ResolvedGraphCanonicalContext(fixture.Source, fixture.Packages)
            .Identity(fixture.BoundContext(fixture.Props), [], inputs));
        Assert.Contains("semantic/buffer admission", failure.Message, StringComparison.Ordinal);
    }

    private static string Sha(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

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

    [Fact]
    public void Repeated_generated_import_roots_charge_each_actual_replacement_before_allocation()
    {
        using var fixture = new Fixture();
        // Consume the SAME original ledger without allocating a giant buffer.
        // A flat8x-file reservation fits the4MiB remainder; all actual repeated
        // replacement segment/concat buffers cannot. No resource cap is widened.
        var text = string.Concat(Enumerable.Repeat("<Root>" + fixture.Source + "</Root>\n", 300));
        Assert.InRange(Encoding.UTF8.GetByteCount(text), 16_384, 65_536);
        File.WriteAllText(fixture.Props, text, new UTF8Encoding(false));
        using var inputs = new ResolvedGraphInputs(TimeSpan.FromSeconds(30), CancellationToken.None);
        inputs.ReserveAllocation(ResolvedGraphResources.SemanticLimit - 4 * 1024 * 1024);
        var failure = Assert.Throws<SnapshotLimitException>(() => new ResolvedGraphCanonicalContext(fixture.Source, fixture.Packages)
            .Identity(fixture.Context(fixture.Props), [], inputs));
        Assert.Contains("semantic/buffer admission", failure.Message, StringComparison.Ordinal);
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
        if (change == "malformed-utf8")
        {
            var typed = Assert.IsType<SnapshotCaptureException>(failure);
            Assert.IsType<DecoderFallbackException>(typed.InnerException);
        }
        else
        {
            // The SAME failed owner disposal retains its original typed causes,
            // so do not replace its independent first-close aggregate.
            var retained = Assert.IsType<AggregateException>(failure).Flatten().InnerExceptions;
            Assert.All(retained, cause => Assert.True(change == "over-bound"
                ? cause is SnapshotLimitException : cause is FileNotFoundException or DirectoryNotFoundException));
        }
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
        internal string Profile => Path.TrimEndingDirectorySeparator(Path.GetTempPath());
        internal string PackageAlias => "$(UserProfile)" + Packages[Profile.Length..];
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
        internal string WritePackage(string value)
        {
            var text = "<Project><Packages>" + value + "</Packages></Project>\n";
            File.WriteAllText(Props, text, new UTF8Encoding(false));
            return text;
        }
        internal ResolvedProjectCapture BoundContext(string hint, bool trailingSeparator = false)
            => Properties(Properties(Context(hint), "UserProfile", Profile), "NuGetPackageRoot",
                Packages + (trailingSeparator ? Path.DirectorySeparatorChar.ToString() : ""));
        internal string ImportHash(ResolvedProjectCapture value)
        {
            using var document = JsonDocument.Parse(Bytes(value));
            return document.RootElement.GetProperty("IncrementalHintImports")[0].GetProperty("SemanticBytes").GetString()!;
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
