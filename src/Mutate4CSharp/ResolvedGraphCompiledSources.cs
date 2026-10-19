using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Mutate4CSharp;

// Called by the actual selected Csc TaskFinished callback, before later targets
// can replace generated inputs. The original logger retains and rechecks every
// file through Shutdown. Its serialized result is DATA, not an ownership issuer.
internal static class ResolvedGraphCompiledSources
{
    internal static GraphCompiledSnapshot Capture(IReadOnlyList<string> tokens, string project, string sourceRoot, string packageRoot,
        ResolvedGraphInputs inputs, ResolvedGraphBudget budget, List<ResolvedGraphFile> retained)
    {
        inputs.CheckWork();
        ResolvedGraphTokenPreflight.RequireClosedParserInputs(tokens);
        var directory = Path.GetDirectoryName(Path.GetFullPath(project))!;
        var parsed = CSharpCommandLineParser.Default.Parse(tokens, directory, sdkDirectory: null);
        inputs.CheckWork();
        if (parsed.Errors.Any(d => d.Severity == DiagnosticSeverity.Error) || parsed.OutputFileName is null || parsed.OutputDirectory is null || !parsed.EmitPdb ||
            !parsed.CompilationOptions.Deterministic)
            throw new EvaluationContractException("Complete deterministic compiler output profile required.");
        var pathMap = ResolvedGraphPathProfile.Require(parsed.PathMap, sourceRoot, packageRoot);
        var pePath = Path.GetFullPath(Path.Combine(parsed.OutputDirectory, parsed.OutputFileName));
        var pdbPath = parsed.PdbPath is null ? Path.ChangeExtension(pePath, ".pdb") : Path.GetFullPath(parsed.PdbPath, directory);
        var pe = inputs.Read(pePath, "reference"); retained.Add(pe);
        var pdb = inputs.Read(pdbPath, "reference"); retained.Add(pdb);
        var files = new Dictionary<string, ResolvedGraphFile>(StringComparer.Ordinal);
        foreach (var source in parsed.SourceFiles) Add(Path.GetFullPath(source.Path, directory), "source");
        // Generated compiler trees are emitted by the SAME Csc invocation. No
        // guessing virtual filenames, arbitrary PDB-directed disk reads or prefix
        // acceptance: all generated directory entries pass the existing owner.
        if (parsed.GeneratedFilesOutputDirectory is { } generated)
        {
            var root = Path.GetFullPath(generated, directory);
            if (inputs.GeneratedDirectoryPresent(root))
            {
                var pending = new Queue<(string Path, int Depth)>(); pending.Enqueue((root, 0));
                var count = 0;
                while (pending.Count != 0)
                {
                    var row = pending.Dequeue(); inputs.CheckWork();
                    if (row.Depth > 24) throw new SnapshotLimitException("Compiler generated source depth.");
                    foreach (var item in inputs.EnumerateGeneratedDirectory(row.Path))
                    {
                        if (++count > 8192) throw new SnapshotLimitException("Compiler generated entry count.");
                        if ((item.Attributes & FileAttributes.Directory) != 0) pending.Enqueue((item.Path, row.Depth + 1));
                        else if (item.Path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) Add(item.Path, "generated-source");
                        else throw new EvaluationContractException("Unaccounted compiler generated output.");
                    }
                }
            }
        }
        var proof = ResolvedGraphOutputIdentity.Inspect(pe.Bytes, pdb.Bytes,
            files.ToDictionary(p => ResolvedGraphPathProfile.Map(p.Key, pathMap), p => p.Value.Bytes, StringComparer.Ordinal), budget);
        inputs.CheckWork();
        return new(pe.Path, pe.Sha256, pdb.Path, pdb.Sha256,
            files.Select(p => new GraphCompiledDocument(p.Key, p.Value.Sha256)).OrderBy(p => p.Path, StringComparer.Ordinal).ToArray(), pathMap, proof);

        void Add(string path, string role)
        {
            inputs.CheckWork();
            path = Path.GetFullPath(path);
            if (files.ContainsKey(path)) throw new EvaluationContractException("Duplicate original compiled source.");
            var file = inputs.Read(path, role); retained.Add(file); files.Add(path, file);
        }
    }

    internal static ResolvedGraphOutputData Verify(GraphCompiledSnapshot expected,
        IReadOnlyDictionary<string, byte[]> sources, ResolvedGraphInputs inputs, ResolvedGraphBudget budget)
    {
        try { return VerifyCore(expected, sources, inputs, budget); }
        catch { budget.BeginFailureCleanup(); throw; }
    }
    private static ResolvedGraphOutputData VerifyCore(GraphCompiledSnapshot expected,
        IReadOnlyDictionary<string, byte[]> sources, ResolvedGraphInputs inputs, ResolvedGraphBudget budget)
    {
        inputs.CheckWork();
        if (expected.Documents.Length is < 1 or > ResolvedGraphInputs.MaximumFiles ||
            expected.Documents.Select(d => d.Path).Distinct(StringComparer.Ordinal).Count() != expected.Documents.Length ||
            sources.Count != expected.Documents.Length)
            throw new EvaluationContractException("Complete original compiled document membership differs.");
        foreach (var document in expected.Documents)
        {
            inputs.CheckWork();
            if (!sources.TryGetValue(document.Path, out var bytes) ||
                Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant() != document.Sha256)
                throw new SnapshotDivergedException("Compiler-consumed source differs from semantic input: " + document.Path);
        }
        var pe = inputs.Read(expected.PePath, "reference");
        var pdb = inputs.Read(expected.PdbPath, "reference");
        if (pe.Sha256 != expected.PeSha256 || pdb.Sha256 != expected.PdbSha256)
            throw new SnapshotDivergedException("Original compiler output bytes changed.");
        var proof = ResolvedGraphOutputIdentity.Inspect(pe.Bytes, pdb.Bytes,
            sources.ToDictionary(p => ResolvedGraphPathProfile.Map(p.Key, expected.PathMap), p => p.Value, StringComparer.Ordinal), budget);
        if (proof != expected.Proof) throw new SnapshotDivergedException("Original compiler output correspondence changed.");
        inputs.Recheck(pe); inputs.Recheck(pdb); inputs.CheckWork();
        return proof;
    }
}

internal sealed record GraphCompiledDocument(string Path, string Sha256);
internal sealed record GraphCompiledSnapshot(string PePath, string PeSha256, string PdbPath, string PdbSha256,
    GraphCompiledDocument[] Documents, IReadOnlyDictionary<string, string> PathMap, ResolvedGraphOutputData Proof);
