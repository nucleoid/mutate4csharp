using System.Security.Cryptography;
using System.Text;

namespace Mutate4CSharp.Tests;

public sealed class EvaluationFingerprintTests
{
    [Fact]
    public void EveryRequiredInputAndExecutionDimensionInvalidatesTheFingerprint()
    {
        var baseline = Material();
        var original = EvaluationFingerprint.Compute(baseline);
        var mutations = new EvaluationFingerprintMaterial[]
        {
            baseline with { Inputs = ReplaceInput(baseline, "source") },
            baseline with { Inputs = ReplaceInput(baseline, "test") },
            baseline with { Inputs = ReplaceInput(baseline, "asset") },
            baseline with { Inputs = ReplaceInput(baseline, "project") },
            baseline with { Inputs = ReplaceInput(baseline, "configuration") },
            baseline with { Inputs = ReplaceInput(baseline, "dependency") },
            baseline with { SnapshotId = Digest("snapshot-v2") },
            baseline with { Scope = "scope-v2" },
            baseline with { Configuration = "configuration-v2" },
            baseline with { ToolVersion = "tool-v2" },
            baseline with { OperatorVersion = "operator-v2" },
            baseline with { SdkIdentity = "sdk-v2" },
            baseline with { RuntimeIdentity = "runtime-v2" },
            baseline with { RunnerIdentity = "runner-v2" },
            baseline with { Policy = baseline.Policy with { MutationCap = baseline.Policy.MutationCap + 1 } }
        };

        Assert.All(mutations, changed => Assert.NotEqual(original, EvaluationFingerprint.Compute(changed)));
        Assert.Equal(mutations.Length, mutations.Select(EvaluationFingerprint.Compute).Distinct().Count());
    }

    [Fact]
    public void OrderingTimingAndTemporaryLocationsDoNotChangeIdentity()
    {
        var baseline = Material();
        var reordered = baseline with { Inputs = baseline.Inputs.Reverse().ToArray() };

        Assert.Equal(EvaluationFingerprint.Compute(baseline), EvaluationFingerprint.Compute(reordered));
    }

    [Fact]
    public void LengthFramingSeparatesOtherwiseAmbiguousComponents()
    {
        var first = Material() with { Scope = "ab", Configuration = "c" };
        var second = Material() with { Scope = "a", Configuration = "bc" };

        Assert.NotEqual(EvaluationFingerprint.Compute(first), EvaluationFingerprint.Compute(second));
    }

    [Fact]
    public void CompleteScopeHasNoOneMegabyteSemanticLimit()
    {
        var material = Material() with { Scope = new string('s', 1024 * 1024 + 1) };

        var fingerprint = EvaluationFingerprint.Compute(material);

        Assert.True(EvaluationFingerprint.IsFingerprint(fingerprint));
    }

    [Fact]
    public void DeletedAndUntrackedInputStateIsBoundedAndIdentityRelevant()
    {
        var baseline = Material();
        var deleted = baseline with
        {
            Inputs = baseline.Inputs.Append(new FingerprintInput("source", "src/Deleted.cs", 0, "missing",
                Exists: false, IsTracked: true)).ToArray()
        };
        var untracked = baseline with
        {
            Inputs = baseline.Inputs.Select(input => input.Kind == "source" ? input with { IsTracked = false } : input)
                .ToArray()
        };

        Assert.NotEqual(EvaluationFingerprint.Compute(baseline), EvaluationFingerprint.Compute(deleted));
        Assert.NotEqual(EvaluationFingerprint.Compute(baseline), EvaluationFingerprint.Compute(untracked));
    }

    [Fact]
    public void ProvenanceCompletenessDefaultsFailClosed()
    {
        Assert.False(Material().ProvenanceComplete);
        Assert.Throws<EvaluationContractException>(() => EvaluationFingerprint.ComputeForProven(Material()));
    }

    [Fact]
    public void ProvenMaterialRequiresCoverageCollectorAndPlanIdentity()
    {
        var incompleteRunner = Material() with { ProvenanceComplete = true };
        var completeRunner = incompleteRunner with
        {
            RunnerIdentity = "runner=vstest;collector=coverlet-opencover-v1;" +
                "coverage=sha256:" + new string('a', 64) + ";plan=sha256:" + new string('b', 64)
        };

        Assert.Throws<EvaluationContractException>(() =>
            EvaluationFingerprint.ComputeForProven(incompleteRunner));
        Assert.True(EvaluationFingerprint.IsFingerprint(
            EvaluationFingerprint.ComputeForProven(completeRunner)));
    }

    private static EvaluationFingerprintMaterial Material() => new(
        [
            Input("source", "src/A.cs", "source"),
            Input("test", "tests/A.Tests.cs", "test"),
            Input("asset", "assets/data.txt", "asset"),
            Input("project", "src/App.csproj", "project"),
            Input("configuration", "mutate4csharp.json", "configuration"),
            Input("dependency", "packages.lock.json", "dependency")
        ],
        Digest("snapshot-v1"), "scope-v1", "configuration-v1", "tool-v1", "operator-v1", "sdk-v1", "runtime-v1",
        "runner-v1", EvaluationReport.DefaultPolicy);

    private static IReadOnlyList<FingerprintInput> ReplaceInput(EvaluationFingerprintMaterial material, string kind) =>
        material.Inputs.Select(input => input.Kind == kind ? Input(kind, input.Path, kind + "-changed") : input)
            .ToArray();

    private static FingerprintInput Input(string kind, string path, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        return new(kind, path, bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
    }

    private static string Digest(string value) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
