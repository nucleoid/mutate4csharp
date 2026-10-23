using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis.Text;

namespace Mutate4CSharp.Tests;

public sealed class ResolvedGraphCompiledSourceTests
{
    [Fact]
    public void Genuine_compiled_output_precedes_changed_generated_source_and_foreign_output_refusals()
    {
        using var repository = new SnapshotTestRepository();
        var path = Path.Combine(repository.Root, "Generated.cs");
        var source = Encoding.UTF8.GetBytes("public static class Value { public static int Get() => 7; }");
        using var sourceStream = new MemoryStream(source);
        var text = SourceText.From(sourceStream, Encoding.UTF8, SourceHashAlgorithm.Sha256);
        var tree = CSharpSyntaxTree.ParseText(text, path: path);
        var compilation = CSharpCompilation.Create("CompiledSource", [tree],
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, deterministic: true));
        using var pe = new MemoryStream(); using var pdb = new MemoryStream();
        Assert.True(compilation.Emit(pe, pdb, options: new EmitOptions(debugInformationFormat: DebugInformationFormat.PortablePdb)).Success);
        var original = new ResolvedGraphBudget(TimeSpan.FromSeconds(30), CancellationToken.None);
        var documents = new Dictionary<string, byte[]> { [path] = source };
        var proof = ResolvedGraphOutputIdentity.Inspect(pe.ToArray(), pdb.ToArray(), documents, original);
        var pePath = Path.Combine(repository.Root, "CompiledSource.dll");
        var pdbPath = Path.Combine(repository.Root, "CompiledSource.pdb");
        File.WriteAllBytes(pePath, pe.ToArray()); File.WriteAllBytes(pdbPath, pdb.ToArray());
        var snapshot = new GraphCompiledSnapshot(pePath, proof.PeSha256, pdbPath, proof.PdbSha256,
            [new(path, Convert.ToHexString(SHA256.HashData(source)).ToLowerInvariant())],
            new Dictionary<string, string>(), proof);
        using (var input = new ResolvedGraphInputs(original))
            Assert.Equal(proof, ResolvedGraphCompiledSources.Verify(snapshot, documents, input, original));
        var changedBudget = new ResolvedGraphBudget(TimeSpan.FromSeconds(30), CancellationToken.None);
        using (var input = new ResolvedGraphInputs(changedBudget))
            Assert.Throws<SnapshotDivergedException>(() => ResolvedGraphCompiledSources.Verify(snapshot,
                new Dictionary<string, byte[]> { [path] = Encoding.UTF8.GetBytes("public static class Value { public static int Get() => 8; }") }, input, changedBudget));
        Assert.NotNull(changedBudget.Observe().Cleanup);
        Assert.Throws<EvaluationContractException>(changedBudget.CheckWork);
        // No mutation of the retained expected proof can promote a different
        // actual compiler output. Separate input frames keep refusals explicit.
        File.WriteAllBytes(pePath, new byte[] { 0 });
        var foreignBudget = new ResolvedGraphBudget(TimeSpan.FromSeconds(30), CancellationToken.None);
        using (var input = new ResolvedGraphInputs(foreignBudget))
            Assert.Throws<SnapshotDivergedException>(() => ResolvedGraphCompiledSources.Verify(snapshot, documents, input, foreignBudget));
    }
}
