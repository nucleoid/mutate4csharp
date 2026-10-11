using System.Reflection;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace Mutate4CSharp.Tests;

public sealed class AdversarialRoundOneTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "mutate4csharp-round-one",
        Guid.NewGuid().ToString("N"));

    public AdversarialRoundOneTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public void ConstructorAndCheckedOperatorFormsHaveDistinctMutationIdentities()
    {
        var instance = Identity("class C { C() { _ = true; } }", "true");
        var @static = Identity("class C { static C() { _ = true; } }", "true");
        var ordinaryOperator = Identity(
            "class C { public static C operator +(C a, C b) { _ = true; return a; } }", "true");
        var checkedOperator = Identity(
            "class C { public static C operator checked +(C a, C b) { _ = true; return a; } }", "true");
        var ordinaryConversion = Identity(
            "class C { public static explicit operator int(C value) { _ = true; return 1; } }", "true");
        var checkedConversion = Identity(
            "class C { public static explicit operator checked int(C value) { _ = true; return 1; } }", "true");

        Assert.NotEqual(instance.MutationId, @static.MutationId);
        Assert.NotEqual(ordinaryOperator.MutationId, checkedOperator.MutationId);
        Assert.NotEqual(ordinaryConversion.MutationId, checkedConversion.MutationId);
    }

    [Fact]
    public void ExtensionReceiverParticipatesInMemberIdentityWhenSyntaxIsSupported()
    {
        const string source = """
public static class Extensions
{
    extension(string value) { public bool Flag() => true; }
    extension(int value) { public bool Flag() => true; }
}
""";
        var first = Identity(source, "true");
        var second = Identity(source, "true", source.IndexOf("true", StringComparison.Ordinal) + 1);

        Assert.NotEqual(first.DeclarationIdentity, second.DeclarationIdentity);
        Assert.NotEqual(first.MutationId, second.MutationId);
    }

    [Fact]
    public void EqualMaterialAtDifferentSourceSpansIsRefused()
    {
        const string source = "class C { bool M() => true; } class C { bool M() => true; }";
        var first = Identity(source, "true");
        var second = Identity(source, "true", source.IndexOf("true", StringComparison.Ordinal) + 1);

        Assert.Equal(first.Material, second.Material);
        Assert.Equal(first.MutationId, second.MutationId);
        Assert.Throws<EvaluationContractException>(() => MutationIdentity.RefuseCollisions([first, second]));
    }

    [Theory]
    [InlineData("[Marker(1)] class Target { }", "class Earlier { bool X = true; } [Marker(1)] class Target { }")]
    [InlineData("class Target(int value = 1) { }", "class Earlier { bool X = true; } class Target(int value = 1) { }")]
    [InlineData("class Target : Base(1) { }", "class Earlier { bool X = true; } class Target : Base(1) { }")]
    [InlineData("enum Target { One = 1 }", "class Earlier { bool X = true; } enum Target { One = 1 }")]
    public void NonMemberSitesRemainStableAfterEarlierUnrelatedTypeInsertion(string original, string changed)
    {
        Assert.Equal(Identity(original, "1").MutationId, Identity(changed, "1").MutationId);
    }

    [Fact]
    public void LedgerAllowsOneMutationAcrossContextsButRefusesDuplicateEvaluationUnit()
    {
        var mutationId = "mutation:v1:" + new string('a', 64);
        var first = Unit(mutationId, "evaluation:v1:" + new string('b', 64));
        var second = Unit(mutationId, "evaluation:v1:" + new string('c', 64));
        var decision = EvaluationReducer.Reduce(new(BaselineStatus.Green, 2, [first, second], false, []));

        Assert.Equal(EvaluationOutcome.Pass, decision.Outcome);
        Assert.Throws<EvaluationContractException>(() => EvaluationReducer.Reduce(new(BaselineStatus.Green, 2,
            [first, second with { UnitId = "mutation:v1:" + new string('d', 64), EvaluationUnitId = first.EvaluationUnitId }],
            false, [])));
    }

    [Fact]
    public void ReportAllowsRepeatedMutationIdOnlyForDistinctEvaluationUnits()
    {
        var mutationId = "mutation:v1:" + new string('a', 64);
        var units = new[]
        {
            Unit(mutationId, "evaluation:v1:" + new string('b', 64)),
            Unit(mutationId, "evaluation:v1:" + new string('c', 64))
        };
        var facts = new EvaluationFacts(BaselineStatus.Green, units.Length, units, false, []);
        var decision = EvaluationReducer.Reduce(facts);
        var fixture = EvaluationReport.CreateSynthetic(EvaluationOutcome.Pass, "multi-context", "TEST_FIXTURE");
        var report = fixture with
        {
            Baseline = facts.Baseline,
            Units = units,
            Counts = decision.Counts,
            Reasons = decision.Reasons,
            Evidence = decision.Evidence.Concat(fixture.Evidence.Where(item => item.Kind is "CHECK_CONFIGURATION" or "INPUT_SNAPSHOT")).ToArray(),
            Outcome = decision.Outcome,
            ExitCode = decision.ExitCode
        };

        Assert.NotEmpty(ReportWriter.Serialize(report));
    }

    [Fact]
    public void TargetedPlanIsFingerprintBoundIncompleteAndPublicationRefusedEvenWhenAllIdsRequested()
    {
        var candidates = new[] { Candidate("src/A.cs", "Type.A", "site/1", "eval-a") };
        var material = FingerprintMaterial("current");
        var boundPlan = MutationSelection.Bind(candidates, material);
        var plan = MutationSelection.PlanTargeted(boundPlan,
            candidates.Select(item => item.MutationId), boundPlan.PlanFingerprint, material);
        var conditions = Assert.IsAssignableFrom<IReadOnlyList<EvaluationReason>>(plan.GetType()
            .GetProperty("IncompleteConditions")!.GetValue(plan));
        var final = plan.CreatePendingLedger().Select(item => item with { Disposition = UnitDisposition.Killed }).ToArray();
        var decision = EvaluationReducer.Reduce(new(BaselineStatus.Green, final.Length, final, false, conditions));

        Assert.Equal(EvaluationOutcome.Incomplete, decision.Outcome);
        Assert.Throws<EvaluationContractException>(() => plan.RequireProvenPublicationEligibility(
            EvaluationReport.CreateSynthetic(EvaluationOutcome.Incomplete, "targeted", "TARGETED"), material));
    }

    [Fact]
    public void CallerSuppliedMatchingFingerprintStringsCannotAuthorizeTargetedRerun()
    {
        Assert.DoesNotContain(typeof(MutationSelection).GetMethods(BindingFlags.Static | BindingFlags.Public |
            BindingFlags.NonPublic), method => method.Name == "Bind" && method.GetParameters() is var parameters &&
            parameters.Length == 2 && parameters[1].ParameterType == typeof(string));
    }

    [Fact]
    public void StabilityReductionEnforcesPolicyAndBoundsStructuredOriginalEvidence()
    {
        var method = typeof(StabilityEvidence).GetMethods(BindingFlags.Static | BindingFlags.Public |
                BindingFlags.NonPublic)
            .SingleOrDefault(item => item.Name == "Reduce" && item.GetParameters().Length == 4);
        Assert.NotNull(method);
        var policy = EvaluationReport.DefaultPolicy with { StabilityRepetitions = 2 };
        var attempts = new[]
        {
            new StabilityAttempt(1, UnitDisposition.Killed,
                [new EvaluationEvidence("ORIGINAL_KIND", new string('x', 5000), [new string('y', 900)])]),
            new StabilityAttempt(2, UnitDisposition.Killed,
                [new EvaluationEvidence("SECOND_KIND", "short")])
        };
        var result = Assert.IsType<EvaluationUnitResult>(method.Invoke(null,
            ["mutation:v1:" + new string('a', 64), "evaluation:v1:" + new string('b', 64), attempts, policy]));

        Assert.All(result.Evidence, item => Assert.InRange(item.Summary.Length, 1, 1024));
        Assert.Contains(result.Evidence, item => item.Diagnostics?.Contains("attempt=1") == true &&
            item.Diagnostics.Contains("sourceKind=ORIGINAL_KIND"));
        Assert.Throws<TargetInvocationException>(() => method.Invoke(null,
            ["mutation:v1:" + new string('a', 64), "evaluation:v1:" + new string('b', 64), attempts[..1], policy]));
    }

    [Fact]
    public void UnaryAnalyzerUsesOperatorTokenForIdentityAndKeepsApplicationValid()
    {
        var file = Write("Unary.cs", "class C { bool M(bool value) => !   value; }");
        var analysis = SourceAnalyzer.Analyze(file, _directory);
        var unary = Assert.Single(analysis.Sites, item => item.Description == "remove unary !");
        var expected = analysis.SourceWithoutManifest.IndexOf('!');

        Assert.Equal(expected, unary.Start);
        Assert.Equal(1, unary.Length);
        var identity = MutationIdentity.Create("Unary.cs", analysis.Tree.GetRoot(TestContext.Current.CancellationToken),
            new TextSpan(unary.Start, unary.Length), unary.OperatorId, unary.OperatorVersion, unary.Replacement);
        Assert.True(MutationIdentity.IsMutationId(identity.MutationId));
        Assert.DoesNotContain('!', unary.Apply(analysis.SourceWithoutManifest));
    }

    [Fact]
    public void AnalyzerIdentitySelectionAndLedgerPreserveContextUnitsAndRejectForgedIds()
    {
        var file = Write("Pipeline.cs", "class C { bool M(int value) => value == 1; }");
        var analysis = SourceAnalyzer.Analyze(file, _directory);
        var site = Assert.Single(analysis.Sites, item => item.Description == "replace == with !=");
        var identity = MutationIdentity.Create("Pipeline.cs", analysis.Tree.GetRoot(TestContext.Current.CancellationToken),
            new TextSpan(site.Start, site.Length), site.OperatorId, site.OperatorVersion, site.Replacement);
        var first = new MutationCandidate(identity.MutationId,
            EvaluationUnitIdentity.Compute(new(identity.MutationId, "src/App.csproj", "net10.0", "symbols=A")),
            identity.Material, "src/App.csproj", "net10.0", "symbols=A", identity.SourceStart,
            identity.SourceLength);
        var second = first with
        {
            EvaluationUnitId = EvaluationUnitIdentity.Compute(new(identity.MutationId, "src/App.csproj", "net9.0",
                "symbols=A")),
            TargetFramework = "net9.0"
        };
        var material = FingerprintMaterial("ledger-contexts");
        var plan = MutationSelection.Plan(MutationSelection.Bind([second, first], material), material);

        Assert.Equal(2, plan.CreatePendingLedger().Count);
        Assert.Equal(plan.Selected.Select(item => item.EvaluationUnitId),
            plan.OrderResults(plan.CreatePendingLedger().Reverse()).Select(item => item.EvaluationUnitId));
        Assert.Throws<EvaluationContractException>(() => MutationSelection.Bind(
            [first with { MutationId = "mutation:v1:" + new string('f', 64) }], material));
    }

    [Fact]
    public void EvaluationUnitHashUsesAnIndependentIdentityDomain()
    {
        var mutation = MutationIdentity.Compute(Material("Type.M", "site/1"));
        var evaluation = EvaluationUnitIdentity.Compute(new(mutation, "src/App.csproj", "net10.0", "symbols=A"));
        var mutationShaped = MutationIdentity.Compute(new("src/App.csproj", mutation, "symbols=A",
            "evaluation-unit", "1", "net10.0"));

        Assert.NotEqual("evaluation:v1:" + mutationShaped[12..], evaluation);
    }

    private IdentifiedMutation Identity(string source, string token, int startAt = 0)
    {
        var tree = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Preview));
        var start = source.IndexOf(token, startAt, StringComparison.Ordinal);
        Assert.True(start >= 0);
        return MutationIdentity.Create("src/Target.cs", tree.GetRoot(), new TextSpan(start, token.Length),
            "test.operator", "1", "replacement");
    }

    private static MutationIdentityMaterial Material(string declaration, string site) =>
        new("src/Target.cs", declaration, site, "test.operator", "1", "replacement");

    private static MutationCandidate Candidate(string path, string declaration, string site, string evaluationId)
    {
        var material = new MutationIdentityMaterial(path, declaration, site, "test.operator", "1", "replacement");
        var mutationId = MutationIdentity.Compute(material);
        const string project = "src/App.csproj";
        const string target = "net10.0";
        var parse = $"case={evaluationId}";
        return new(mutationId, EvaluationUnitIdentity.Compute(new(mutationId, project, target, parse)), material,
            project, target, parse);
    }

    private static EvaluationFingerprintMaterial FingerprintMaterial(string value) => new(
        [EvaluationFingerprint.FromBytes("source", "src/A.cs", System.Text.Encoding.UTF8.GetBytes(value))],
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(value))).ToLowerInvariant(),
        "scope", "configuration", "tool", "1", "sdk", "runtime", "runner",
        EvaluationReport.DefaultPolicy);

    private static EvaluationUnitResult Unit(string mutationId, string evaluationId) =>
        new(mutationId, evaluationId, UnitDisposition.Killed, [new("TEST_RUN", "Killed.")]);

    private string Write(string name, string content)
    {
        var path = Path.Combine(_directory, name);
        File.WriteAllText(path, content);
        return path;
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); }
        catch { }
    }
}
