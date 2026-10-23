using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Mutate4CSharp.Tests;

public sealed class ResolvedGraphPathProfileTests
{
    [Fact]
    public void Actual_parser_terminated_destinations_join_the_closed_profile_and_map_one_separator()
    {
        var source = Path.Combine(Path.GetTempPath(), "graph parser source");
        var packages = Path.Combine(Path.GetTempPath(), "graph parser packages");
        var parsed = CSharpCommandLineParser.Default.Parse(
            ["/target:library", "/out:fixture.dll", $"/pathmap:{source}={ResolvedGraphPathProfile.Source},{packages}={ResolvedGraphPathProfile.Packages}"],
            source, sdkDirectory: null);
        Assert.DoesNotContain(parsed.Errors, error => error.Severity == DiagnosticSeverity.Error);
        var map = ResolvedGraphPathProfile.Require(parsed.PathMap, source, packages);
        Assert.Equal("/_/mutate-source/Subject.cs", ResolvedGraphPathProfile.Map(Path.Combine(source, "Subject.cs"), map));
        Assert.Equal("/_/mutate-packages/Dependency.cs", ResolvedGraphPathProfile.Map(Path.Combine(packages, "Dependency.cs"), map));
    }

    [Theory]
    [InlineData("//")]
    [InlineData("\\")]
    [InlineData("/../other")]
    public void Parser_representation_does_not_allow_other_destination_aliases(string suffix)
    {
        var source = Path.Combine(Path.GetTempPath(), "graph-map-source");
        var packages = Path.Combine(Path.GetTempPath(), "graph-map-packages");
        Assert.Throws<EvaluationContractException>(() => ResolvedGraphPathProfile.Require(ImmutableArray.Create(
            KeyValuePair.Create(source, ResolvedGraphPathProfile.Source + suffix),
            KeyValuePair.Create(packages, ResolvedGraphPathProfile.Packages + "/")), source, packages));
    }

    [Fact]
    public void Explicit_observed_owned_map_is_portable_and_unmapped_external_paths_are_preserved()
    {
        var root = Path.Combine(Path.GetTempPath(), "graph-path-profile");
        var source = Path.Combine(root, "source"); var packages = Path.Combine(root, "packages");
        var map = ResolvedGraphPathProfile.Require(ImmutableArray.Create(
            KeyValuePair.Create(source, ResolvedGraphPathProfile.Source),
            KeyValuePair.Create(packages, ResolvedGraphPathProfile.Packages)), source, packages);
        Assert.Equal("/_/mutate-source/src/App.cs", ResolvedGraphPathProfile.Map(Path.Combine(source, "src", "App.cs"), map));
        Assert.Equal("/_/mutate-packages/package/Source.cs", ResolvedGraphPathProfile.Map(Path.Combine(packages, "package", "Source.cs"), map));
        var external = Path.Combine(root, "outside", "Compiler.cs");
        Assert.Equal(external, ResolvedGraphPathProfile.Map(external, map));
        Assert.Equal(Path.Combine(source, "src", "App.cs"), ResolvedGraphPathProfile.UnmapSource("/_/mutate-source/src/App.cs", source));
        Assert.Throws<SnapshotCaptureException>(() => ResolvedGraphPathProfile.UnmapSource("/_/mutate-source/../private.cs", source));
        Assert.Throws<EvaluationContractException>(() => ResolvedGraphPathProfile.Require(
            ImmutableArray.Create(KeyValuePair.Create(source, "/unreviewed")), source, packages));
    }
}
