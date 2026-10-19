using System.Collections.Immutable;

namespace Mutate4CSharp.Tests;

public sealed class ResolvedGraphPathProfileTests
{
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
