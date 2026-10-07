using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis.CSharp;

namespace Mutate4CSharp.Tests;

public sealed class AdversarialRoundSixTests
{
    [Fact]
    public void SelectionPlanHasNoOrdinaryConstructionPath()
    {
        var constructors = typeof(MutationSelectionPlan).GetConstructors(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        Assert.NotEmpty(constructors);
        Assert.All(constructors, constructor => Assert.True(constructor.IsPrivate,
            $"Selection-plan constructor must be private: {constructor}."));
    }

    [Fact]
    public void EverySelectionPlanFactoryRequiresABoundPlanAndCurrentEvaluationMaterial()
    {
        var factories = typeof(MutationSelectionPlan).Assembly.GetTypes()
            .SelectMany(type => type.GetMethods(BindingFlags.Static | BindingFlags.Public |
                BindingFlags.NonPublic))
            .Where(method => method.ReturnType == typeof(MutationSelectionPlan))
            .ToArray();

        Assert.NotEmpty(factories);
        Assert.All(factories, factory =>
        {
            var parameterTypes = factory.GetParameters().Select(parameter => parameter.ParameterType).ToArray();
            Assert.Contains(typeof(BoundMutationPlan), parameterTypes);
            Assert.Contains(typeof(EvaluationFingerprintMaterial), parameterTypes);
        });
    }

    [Fact]
    public void VerifiedFullAndTargetedFactoriesRemainAvailable()
    {
        var candidate = Candidate();
        var material = FingerprintMaterial("round-six");
        var bound = MutationSelection.Bind([candidate], material);

        var full = MutationSelection.Plan(bound, material);
        var targeted = MutationSelection.PlanTargeted(bound, [candidate.MutationId],
            bound.PlanFingerprint, material);

        Assert.False(full.IsDiagnosticPartial);
        Assert.True(full.CanAdvanceFullScopeSuccess);
        Assert.True(targeted.IsDiagnosticPartial);
        Assert.False(targeted.CanAdvanceFullScopeSuccess);
    }

    private static MutationCandidate Candidate()
    {
        const string source = "class C { bool M(int value) => value == 1; }";
        var tree = CSharpSyntaxTree.ParseText(source);
        var token = tree.GetRoot().DescendantTokens().Single(item => item.Text == "==");
        var mutation = MutationIdentity.Create("src/A.cs", tree.GetRoot(), token.Span,
            "binary.equal", "1", "!=");
        const string project = "src/App.csproj";
        const string target = "net10.0";
        const string parse = "symbols=A";
        return new(mutation.MutationId,
            EvaluationUnitIdentity.Compute(new(mutation.MutationId, project, target, parse)),
            mutation.Material, project, target, parse, mutation.SourceStart, mutation.SourceLength);
    }

    private static EvaluationFingerprintMaterial FingerprintMaterial(string value)
    {
        var scope = ScopePlan.Empty("inputs", ".", null);
        return new([
                EvaluationFingerprint.FromBytes("source", "src/A.cs", Encoding.UTF8.GetBytes(value)),
                EvaluationFingerprint.FromBytes("dependency", "packages.lock.json", "dependency"u8)
            ], Digest(value), ReportWriter.SerializeCanonicalScope(scope), "configuration-v1", "tool-v1",
            "operator-v1", "sdk-v1", "runtime-v1", "vstest-v1", EvaluationReport.DefaultPolicy,
            ProvenanceComplete: true);
    }

    private static string Digest(string value) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
