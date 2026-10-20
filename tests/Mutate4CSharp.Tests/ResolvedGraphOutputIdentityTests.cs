using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis.Text;

namespace Mutate4CSharp.Tests;

// Real compiler-emitted in-memory artifacts; all cases are UNEXECUTED pending
// Root runtime admission. These prove correspondence, not original task ownership.
public sealed class ResolvedGraphOutputIdentityTests
{
    [Fact]
    public async Task Logger_generated_directory_is_owned_before_any_source_or_output_read_or_traversal()
    {
        await using var owned = OwnedDirectory.Create(OwnedDirectory.PrivateParent("logger-generated-control"), "data");
        var sourceRoot = Path.Combine(owned.Root, "source"); var packageRoot = Path.Combine(owned.Root, "packages");
        var generatedRoot = Path.Combine(sourceRoot, "generated");
        var externalRoot = Path.Combine(owned.Root, "source-lookalike");
        foreach (var directory in new[] { sourceRoot, packageRoot, generatedRoot, externalRoot }) Directory.CreateDirectory(directory);
        var externalFile = Path.Combine(externalRoot, "must-not-be-traversed.txt"); File.WriteAllText(externalFile, "preserve private external bytes");
        var source = Path.Combine(sourceRoot, "Subject.cs"); var pe = Path.Combine(sourceRoot, "GraphOutput.dll"); var pdb = Path.ChangeExtension(pe, ".pdb");
        var actual = Emit("public sealed class Subject { public int Value() => 1; }", ResolvedGraphPathProfile.Source + "/Subject.cs");
        File.WriteAllBytes(source, actual.Source); File.WriteAllBytes(pe, actual.Pe); File.WriteAllBytes(pdb, actual.Pdb);
        string[] Tokens(string generated) => ["/target:library", "/out:" + pe, "/pdb:" + pdb, "/debug:portable", "/deterministic+",
            "/pathmap:" + sourceRoot + "=" + ResolvedGraphPathProfile.Source + "," + packageRoot + "=" + ResolvedGraphPathProfile.Packages,
            "/generatedfilesout:" + generated, source];
        var positiveBudget = new ResolvedGraphBudget(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        using (var inputs = new ResolvedGraphInputs(positiveBudget))
        {
            var retained = new List<ResolvedGraphFile>();
            var snapshot = ResolvedGraphCompiledSources.Capture(Tokens(generatedRoot), Path.Combine(sourceRoot, "Graph.csproj"),
                sourceRoot, packageRoot, inputs, positiveBudget, retained);
            Assert.Equal(1, snapshot.Proof.MatchedDocuments); Assert.Equal(3, retained.Count);
        }
        var refusedBudget = new ResolvedGraphBudget(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        using (var inputs = new ResolvedGraphInputs(refusedBudget))
        {
            var retained = new List<ResolvedGraphFile>();
            var refusal = Assert.Throws<SnapshotCaptureException>(() => ResolvedGraphCompiledSources.Capture(Tokens(externalRoot),
                Path.Combine(sourceRoot, "Graph.csproj"), sourceRoot, packageRoot, inputs, refusedBudget, retained));
            Assert.Contains("generated source escapes", refusal.Message);
            var usage = refusedBudget.Resources.ObservePreDelivery();
            Assert.Equal(0, usage.Frames); Assert.Equal(0, usage.IoCharged); Assert.Equal(0, usage.SemanticCharged);
            Assert.Empty(retained);
        }
        Assert.Equal("preserve private external bytes", File.ReadAllText(externalFile));
    }

    [Fact]
    public void Actual_emit_binds_complete_pe_pdb_and_every_emitted_source_checksum()
    {
        var actual = Emit("public sealed class Subject { public int Choose(int x) => x > 2 ? 1 : 0; }");
        var original = new ResolvedGraphBudget(TimeSpan.FromSeconds(30), CancellationToken.None);
        var proof = ResolvedGraphOutputIdentity.Inspect(actual.Pe, actual.Pdb,
            new Dictionary<string, byte[]> { [actual.Document] = actual.Source }, original);
        Assert.NotEqual(Guid.Empty, proof.ModuleVersionId);
        Assert.NotEqual(Guid.Empty, proof.PortablePdbGuid);
        Assert.Equal(1, proof.MatchedDocuments);
        Assert.Equal(64, proof.PeSha256.Length);
        Assert.Equal(64, proof.PdbSha256.Length);
    }

    [Fact]
    public void Genuine_positive_precedes_foreign_pdb_and_changed_source_refusals()
    {
        var actual = Emit("public sealed class Subject { public int Value() => 1; }");
        var foreign = Emit("public sealed class Subject { public int Value() => 2; }");
        ResolvedGraphBudget Budget() => new(TimeSpan.FromSeconds(30), CancellationToken.None);
        var source = new Dictionary<string, byte[]> { [actual.Document] = actual.Source };
        Assert.Equal(1, ResolvedGraphOutputIdentity.Inspect(actual.Pe, actual.Pdb, source, Budget()).MatchedDocuments);
        Assert.Throws<EvaluationContractException>(() =>
            ResolvedGraphOutputIdentity.Inspect(actual.Pe, foreign.Pdb, source, Budget()));
        Assert.Throws<EvaluationContractException>(() =>
            ResolvedGraphOutputIdentity.Inspect(actual.Pe, actual.Pdb,
                new Dictionary<string, byte[]> { [actual.Document] = foreign.Source }, Budget()));
        Assert.Throws<EvaluationContractException>(() =>
            ResolvedGraphOutputIdentity.Inspect(actual.Pe, actual.Pdb,
                new Dictionary<string, byte[]> { ["foreign.cs"] = actual.Source }, Budget()));
    }

    [Fact]
    public void Actual_pdb_name_is_required_without_guessing_path_mapped_or_line_document_aliases()
    {
        var actual = Emit("public sealed class Subject { public int Value() => 1; }", "/logical/Subject.cs");
        ResolvedGraphBudget Budget() => new(TimeSpan.FromSeconds(30), CancellationToken.None);
        Assert.Equal(1, ResolvedGraphOutputIdentity.Inspect(actual.Pe, actual.Pdb,
            new Dictionary<string, byte[]> { [actual.Document] = actual.Source }, Budget()).MatchedDocuments);
        Assert.Throws<EvaluationContractException>(() => ResolvedGraphOutputIdentity.Inspect(actual.Pe, actual.Pdb,
            new Dictionary<string, byte[]> { ["Subject.cs"] = actual.Source }, Budget()));
    }

    private static (byte[] Pe, byte[] Pdb, byte[] Source, string Document) Emit(string code,
        string document = "Subject.cs")
    {
        var source = Encoding.UTF8.GetBytes(code);
        using var input = new MemoryStream(source, writable: false);
        var text = SourceText.From(input, Encoding.UTF8, SourceHashAlgorithm.Sha256);
        var tree = CSharpSyntaxTree.ParseText(text, path: document);
        var compilation = CSharpCompilation.Create("GraphOutput", [tree],
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, deterministic: true));
        using var pe = new MemoryStream();
        using var pdb = new MemoryStream();
        var result = compilation.Emit(pe, pdb, options: new EmitOptions(
            debugInformationFormat: DebugInformationFormat.PortablePdb));
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        return (pe.ToArray(), pdb.ToArray(), source, document);
    }
}
