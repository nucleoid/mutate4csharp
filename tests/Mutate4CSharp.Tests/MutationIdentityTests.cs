using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace Mutate4CSharp.Tests;

public sealed class MutationIdentityTests
{
    [Fact]
    public void UnrelatedEarlierDeclarationDoesNotRenumberStableIdentity()
    {
        const string original = "namespace Demo; class Target { bool Check(int value) => value == 1; }";
        const string changed = "namespace Demo; class Earlier { int Other() => 0; } class Target { bool Check(int value) => value == 1; }";

        var first = Identity(original, original.IndexOf("==", StringComparison.Ordinal));
        var second = Identity(changed, changed.IndexOf("==", StringComparison.Ordinal));

        Assert.Equal(first.MutationId, second.MutationId);
        Assert.Equal("src/Target.cs", first.RepositoryPath);
        Assert.Contains("Demo.Target`0.method:Check`0", first.DeclarationIdentity);
    }

    [Fact]
    public void RepeatedIdenticalExpressionsHaveDistinctStructuralSites()
    {
        const string source = "namespace Demo; class Target { bool Check(int value) => value == 1 || value == 1; }";
        var firstOffset = source.IndexOf("==", StringComparison.Ordinal);
        var secondOffset = source.IndexOf("==", firstOffset + 2, StringComparison.Ordinal);

        var first = Identity(source, firstOffset);
        var second = Identity(source, secondOffset);

        Assert.NotEqual(first.StructuralSiteIdentity, second.StructuralSiteIdentity);
        Assert.NotEqual(first.MutationId, second.MutationId);
    }

    [Fact]
    public void LengthFramingAndEveryIdentityDimensionAreSignificant()
    {
        var first = Material("ab", "c");
        var second = Material("a", "bc");
        var original = MutationIdentity.Compute(first);

        Assert.NotEqual(MutationIdentity.Compute(first), MutationIdentity.Compute(second));
        Assert.NotEqual(original, MutationIdentity.Compute(first with { RepositoryPath = "src/B.cs" }));
        Assert.NotEqual(original, MutationIdentity.Compute(first with { DeclarationIdentity = "Other" }));
        Assert.NotEqual(original, MutationIdentity.Compute(first with { StructuralSiteIdentity = "site/2" }));
        Assert.NotEqual(original, MutationIdentity.Compute(first with { OperatorId = "binary.not-equals" }));
        Assert.NotEqual(original, MutationIdentity.Compute(first with { OperatorVersion = "2" }));
        Assert.NotEqual(original, MutationIdentity.Compute(first with { Replacement = "!=" }));
        Assert.DoesNotContain("Target.cs", original, StringComparison.Ordinal);
    }

    [Fact]
    public void CollisionRefusalNeverMergesTwoEnumeratedSites()
    {
        var id = "mutation:v1:" + new string('a', 64);
        var first = new IdentifiedMutation(id, Material("declaration", "site/1"));
        var second = new IdentifiedMutation(id, Material("declaration", "site/2"));

        Assert.Throws<EvaluationContractException>(() => MutationIdentity.RefuseCollisions([first, second]));
    }

    [Fact]
    public void EvaluationUnitIdentitySeparatelyBindsProjectTargetAndParseContext()
    {
        var mutation = MutationIdentity.Compute(Material("declaration", "site"));
        var baseline = new EvaluationUnitIdentityMaterial(mutation, "src/App.csproj", "net10.0",
            "lang=preview;symbols=A");
        var original = EvaluationUnitIdentity.Compute(baseline);

        Assert.True(EvaluationUnitIdentity.IsEvaluationUnitId(original));
        Assert.NotEqual(original, EvaluationUnitIdentity.Compute(baseline with { ProjectPath = "src/Other.csproj" }));
        Assert.NotEqual(original, EvaluationUnitIdentity.Compute(baseline with { TargetFramework = "net9.0" }));
        Assert.NotEqual(original, EvaluationUnitIdentity.Compute(baseline with { ParseContext = "lang=preview;symbols=B" }));
    }

    [Theory]
    [InlineData("/absolute.cs")]
    [InlineData("../escape.cs")]
    [InlineData("src//Target.cs")]
    public void RefusesNonCanonicalRepositoryPaths(string path)
    {
        Assert.Throws<ArgumentException>(() => MutationIdentity.Compute(Material("declaration", "site") with
        {
            RepositoryPath = path
        }));
    }

    private static IdentifiedMutation Identity(string source, int tokenStart)
    {
        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        return MutationIdentity.Create("src\\Target.cs", root, new TextSpan(tokenStart, 2),
            "binary.equals", "1", "!=");
    }

    private static MutationIdentityMaterial Material(string declaration, string site) =>
        new("src/Target.cs", declaration, site, "binary.equals", "1", "false");
}
