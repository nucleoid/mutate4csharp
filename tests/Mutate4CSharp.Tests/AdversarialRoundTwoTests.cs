using System.Reflection;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace Mutate4CSharp.Tests;

public sealed class AdversarialRoundTwoTests
{
    [Theory]
    [InlineData("""
interface I<T> where T : I<T> { static abstract T operator +(T left, T right); }
sealed class C : I<C>
{
    public static C operator +(C left, C right) { _ = true; return left; }
    static C I<C>.operator +(C left, C right) { _ = true; return right; }
}
""")]
    [InlineData("""
interface I<T> where T : I<T> { static abstract explicit operator int(T value); }
sealed class C : I<C>
{
    public static explicit operator int(C value) { _ = true; return 1; }
    static explicit I<C>.operator int(C value) { _ = true; return 2; }
}
""")]
    [InlineData("""
interface I { int this[int value] { get; } }
sealed class C : I
{
    public int this[int value] => true ? value : 0;
    int I.this[int value] => true ? value : 0;
}
""")]
    [InlineData("""
interface I { int this[int value] { get; } }
sealed class C : I
{
    public int this[int value] { get { return true ? value : 0; } }
    int I.this[int value] { get { return true ? value : 0; } }
}
""")]
    public void ExplicitInterfaceSitesHaveDistinctValidIdentities(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Preview),
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.DoesNotContain(tree.GetDiagnostics(TestContext.Current.CancellationToken),
            item => item.Severity == DiagnosticSeverity.Error);
        var offsets = Occurrences(source, "true");

        var first = Identity(tree, offsets[0], "true".Length);
        var second = Identity(tree, offsets[1], "true".Length);

        Assert.NotEqual(first.DeclarationIdentity, second.DeclarationIdentity);
        Assert.NotEqual(first.MutationId, second.MutationId);
        MutationIdentity.RefuseCollisions([first, second]);
    }

    [Theory]
    [InlineData(
        "class C { [Marker(true)] bool Flag { get; set; } }",
        "class C { bool Earlier { get; set; } [Marker(true)] bool Flag { get; set; } }")]
    [InlineData(
        "class C { [Marker(true)] int this[int value] { get => value; } }",
        "class C { int this[string value] { get => 0; } [Marker(true)] int this[int value] { get => value; } }")]
    [InlineData(
        "class C { [Marker(true)] event Action Changed { add { } remove { } } }",
        "class C { event Action Earlier { add { } remove { } } [Marker(true)] event Action Changed { add { } remove { } } }")]
    [InlineData(
        "delegate void Target(bool enabled = true);",
        "delegate void Earlier(bool enabled = false); delegate void Target(bool enabled = true);")]
    [InlineData(
        "namespace N { delegate void Target(bool enabled = true); }",
        "namespace Earlier { delegate void Other(bool enabled = false); } namespace N { delegate void Target(bool enabled = true); }")]
    public void EveryMemberDeclarationAnchorsIdentityAcrossUnrelatedSiblingInsertion(
        string original, string changed)
    {
        Assert.Equal(Identity(original, "true").MutationId, Identity(changed, "true").MutationId);
    }

    [Fact]
    public void TargetedPlanRequiresLiveFingerprintMaterialAndDistinguishesStaleCapture()
    {
        var bind = typeof(MutationSelection).GetMethods(BindingFlags.Static | BindingFlags.Public |
                BindingFlags.NonPublic)
            .SingleOrDefault(method => method.Name == "Bind" && method.GetParameters() is var parameters &&
                parameters.Length == 2 && parameters[1].ParameterType == typeof(EvaluationFingerprintMaterial));
        Assert.NotNull(bind);
        Assert.DoesNotContain(typeof(MutationSelection).GetMethods(BindingFlags.Static | BindingFlags.Public |
                BindingFlags.NonPublic), method => method.Name == "Bind" && method.GetParameters() is var parameters &&
                parameters.Length == 2 && parameters[1].ParameterType == typeof(string));

        var candidate = Candidate("src/A.cs", "Type.A", "site/1");
        var staleMaterial = FingerprintMaterial("old");
        var currentMaterial = FingerprintMaterial("current");
        var bound = bind.Invoke(null, [new[] { candidate }, staleMaterial]);
        Assert.NotNull(bound);
        var planFingerprint = (string)bound.GetType().GetProperty("PlanFingerprint")!.GetValue(bound)!;
        var targeted = typeof(MutationSelection).GetMethods(BindingFlags.Static | BindingFlags.Public |
                BindingFlags.NonPublic)
            .Single(method => method.Name == "PlanTargeted" && method.GetParameters().Any(parameter =>
                parameter.ParameterType == typeof(EvaluationFingerprintMaterial)));

        var error = Assert.Throws<TargetInvocationException>(() => targeted.Invoke(null,
            [bound, new[] { candidate.MutationId }, planFingerprint, currentMaterial]));
        Assert.IsType<EvaluationContractException>(error.InnerException);
        var currentBound = bind.Invoke(null, [new[] { candidate }, currentMaterial]);
        var currentPlanFingerprint = (string)currentBound!.GetType().GetProperty("PlanFingerprint")!
            .GetValue(currentBound)!;
        Assert.IsType<MutationSelectionPlan>(targeted.Invoke(null,
            [currentBound, new[] { candidate.MutationId }, currentPlanFingerprint, currentMaterial]));
    }

    [Fact]
    public void TargetedPlanFlagsAreReadOnlyAndAllIdsRemainIncompleteWithoutCopiedConditions()
    {
        var candidate = Candidate("src/A.cs", "Type.A", "site/1");
        var material = FingerprintMaterial("current");
        var bound = Bind([candidate], material);
        var plan = PlanTargeted(bound, [candidate.MutationId], bound.PlanFingerprint, material);

        Assert.False(typeof(MutationSelectionPlan).GetProperty(nameof(plan.IsDiagnosticPartial))!.CanWrite);
        Assert.False(typeof(MutationSelectionPlan).GetProperty(nameof(plan.CanAdvanceFullScopeSuccess))!.CanWrite);
        var final = plan.CreatePendingLedger()
            .Select(item => item with { Disposition = UnitDisposition.Killed }).ToArray();
        var decision = EvaluationReducer.Reduce(new(BaselineStatus.Green, final.Length, final, false, []));

        Assert.Equal(EvaluationOutcome.Incomplete, decision.Outcome);
        Assert.Contains(decision.Reasons, reason => reason.Code == "TARGETED_DIAGNOSTIC");
        var forgedPass = EvaluationReport.CreateSynthetic(EvaluationOutcome.Pass, "targeted", "TEST_FIXTURE") with
        {
            Units = final,
            Counts = decision.Counts with { Errors = 0 },
            IncompleteConditions = [],
            Reasons = [],
            Outcome = EvaluationOutcome.Pass,
            ExitCode = 0
        };
        Assert.Throws<EvaluationContractException>(() => ReportWriter.Serialize(forgedPass));
    }

    [Fact]
    public void CandidateCarriesAndVerifiesEvaluationUnitIdentityMaterial()
    {
        var candidate = Candidate("src/A.cs", "Type.A", "site/1");
        Assert.Equal("src/App.csproj", candidate.GetType().GetProperty("ProjectPath")!.GetValue(candidate));
        Assert.Equal("net10.0", candidate.GetType().GetProperty("TargetFramework")!.GetValue(candidate));
        Assert.Equal("lang=preview;symbols=A", candidate.GetType().GetProperty("ParseContext")!.GetValue(candidate));

        var material = FingerprintMaterial("candidate-validation");
        Assert.Throws<EvaluationContractException>(() => MutationSelection.Bind(
            [candidate with { EvaluationUnitId = "evaluation:v1:" + new string('f', 64) }], material));
        Assert.Throws<EvaluationContractException>(() => MutationSelection.Bind(
            [candidate with { EvaluationUnitId = "eval-a" }], material));
    }

    [Fact]
    public void StabilityEvidenceBoundsKindsAndTotalEvidenceAndRejectsNullsAsContractFailures()
    {
        var longKind = "A" + new string('B', 900);
        var attempts = Enumerable.Range(1, ExecutionPolicy.MaxStabilityRepetitions)
            .Select(attempt => new StabilityAttempt(attempt, UnitDisposition.Killed,
                Enumerable.Range(0, 20).Select(_ => new EvaluationEvidence(longKind, "observed")).ToArray()))
            .ToArray();
        var result = StabilityEvidence.Reduce("mutation:v1:" + new string('a', 64),
            "evaluation:v1:" + new string('b', 64), attempts,
            EvaluationReport.DefaultPolicy with { StabilityRepetitions = attempts.Length });

        Assert.InRange(result.Evidence.Count, 1, 200);
        Assert.All(result.Evidence.SelectMany(item => item.Diagnostics ?? []), diagnostic =>
            Assert.InRange(diagnostic.Length, 1, 512));
        Assert.Throws<EvaluationContractException>(() => StabilityEvidence.Reduce(
            "mutation:v1:" + new string('a', 64), "evaluation:v1:" + new string('b', 64), null!,
            EvaluationReport.DefaultPolicy));
        Assert.Throws<EvaluationContractException>(() => StabilityEvidence.Reduce(
            "mutation:v1:" + new string('a', 64), "evaluation:v1:" + new string('b', 64),
            [new StabilityAttempt(1, UnitDisposition.Killed, [null!])], EvaluationReport.DefaultPolicy));
    }

    [Fact]
    public void ReportAndSelectionMaterialFailuresAreEvaluationContractFailures()
    {
        var invalidPolicy = EvaluationReport.CreateSynthetic(EvaluationOutcome.Pass, "bad-policy", "TEST_FIXTURE")
            with { Policy = EvaluationReport.DefaultPolicy with { StabilityRepetitions = 101 } };
        Assert.Throws<EvaluationContractException>(() => ReportWriter.Serialize(invalidPolicy));

        var candidate = Candidate("src/A.cs", "Type.A", "site/1") with
        {
            Material = new MutationIdentityMaterial("../A.cs", "Type.A", "site/1", "test.operator", "1",
                "replacement")
        };
        Assert.Throws<EvaluationContractException>(() =>
            MutationSelection.Bind([candidate], FingerprintMaterial("invalid-selection")));
    }

    [Fact]
    public void VersionOneSchemaAndDocsPublishAdditiveUnstableAndMatchingBounds()
    {
        var root = RepositoryRoot();
        using var schema = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root,
            "docs/contracts/evaluation-report-v1.schema.json")));
        var definitions = schema.RootElement.GetProperty("$defs");
        Assert.Equal(ExecutionPolicy.MaxStabilityRepetitions,
            definitions.GetProperty("policy").GetProperty("properties").GetProperty("stabilityRepetitions")
                .GetProperty("maximum").GetInt32());
        Assert.Equal(200,
            definitions.GetProperty("unit").GetProperty("properties").GetProperty("evidence")
                .GetProperty("maxItems").GetInt32());

        var contract = File.ReadAllText(Path.Combine(root, "docs/evaluation-contract.md"));
        Assert.Contains("additive", contract, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("UNSTABLE", contract, StringComparison.Ordinal);
        Assert.Contains("errors", contract, StringComparison.Ordinal);
    }

    private static MutationCandidate Candidate(string path, string declaration, string site)
    {
        var material = new MutationIdentityMaterial(path, declaration, site, "test.operator", "1", "replacement");
        var mutationId = MutationIdentity.Compute(material);
        const string project = "src/App.csproj";
        const string target = "net10.0";
        const string parse = "lang=preview;symbols=A";
        var evaluationId = EvaluationUnitIdentity.Compute(new(mutationId, project, target, parse));
        var constructor = typeof(MutationCandidate).GetConstructors(BindingFlags.Instance | BindingFlags.Public |
                BindingFlags.NonPublic)
            .SingleOrDefault(item => item.GetParameters().Length == 8);
        Assert.NotNull(constructor);
        return Assert.IsType<MutationCandidate>(constructor.Invoke(
            [mutationId, evaluationId, material, project, target, parse, -1, -1]));
    }

    private static BoundMutationPlan Bind(IReadOnlyList<MutationCandidate> candidates,
        EvaluationFingerprintMaterial material)
    {
        var method = typeof(MutationSelection).GetMethods(BindingFlags.Static | BindingFlags.Public |
                BindingFlags.NonPublic)
            .SingleOrDefault(item => item.Name == "Bind" && item.GetParameters() is var parameters &&
                parameters.Length == 2 && parameters[1].ParameterType == typeof(EvaluationFingerprintMaterial));
        Assert.NotNull(method);
        return Assert.IsType<BoundMutationPlan>(method.Invoke(null, [candidates, material]));
    }

    private static MutationSelectionPlan PlanTargeted(BoundMutationPlan bound,
        IReadOnlyList<string> mutationIds, string planFingerprint, EvaluationFingerprintMaterial material)
    {
        var method = typeof(MutationSelection).GetMethods(BindingFlags.Static | BindingFlags.Public |
                BindingFlags.NonPublic)
            .SingleOrDefault(item => item.Name == "PlanTargeted" && item.GetParameters().Length == 4 &&
                item.GetParameters()[3].ParameterType == typeof(EvaluationFingerprintMaterial));
        Assert.NotNull(method);
        return Assert.IsType<MutationSelectionPlan>(method.Invoke(null,
            [bound, mutationIds, planFingerprint, material]));
    }

    private static EvaluationFingerprintMaterial FingerprintMaterial(string value) => new(
        [EvaluationFingerprint.FromBytes("source", "src/A.cs", System.Text.Encoding.UTF8.GetBytes(value))],
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(value))).ToLowerInvariant(),
        "scope", "configuration", "tool", "1", "sdk", "runtime", "runner",
        EvaluationReport.DefaultPolicy);

    private static IdentifiedMutation Identity(string source, string token)
    {
        var tree = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Preview),
            cancellationToken: TestContext.Current.CancellationToken);
        return Identity(tree, source.IndexOf(token, StringComparison.Ordinal), token.Length);
    }

    private static IdentifiedMutation Identity(SyntaxTree tree, int start, int length) =>
        MutationIdentity.Create("src/Target.cs", tree.GetRoot(TestContext.Current.CancellationToken),
            new TextSpan(start, length),
            "test.operator", "1", "replacement");

    private static int[] Occurrences(string source, string value)
    {
        var offsets = new List<int>();
        for (var start = 0; (start = source.IndexOf(value, start, StringComparison.Ordinal)) >= 0; start += value.Length)
            offsets.Add(start);
        return offsets.ToArray();
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "mutate4csharp.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repository root was not found.");
    }
}
