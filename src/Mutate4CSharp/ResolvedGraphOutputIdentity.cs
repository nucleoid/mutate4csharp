using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;

namespace Mutate4CSharp;

// Complete byte-level output correspondence only. This is not a Csc task or
// process provenance issuer. The capture owner must retain and revalidate the
// original files, producer, options and graph independently of this DATA result.
internal static class ResolvedGraphOutputIdentity
{
    internal const int MaximumDocuments = 32_768;
    internal const int MaximumDocumentPath = 4096;
    private static readonly Guid Sha256 = new("8829d00f-11b8-4213-878b-770e8597ac16");
    private static readonly Guid Sha1 = new("ff1816ec-aa5e-4d10-87f7-6f4963833460");

    internal static ResolvedGraphOutputData Inspect(byte[] completePe, byte[] completePdb,
        IReadOnlyDictionary<string, byte[]> completeDocumentBytes, ResolvedGraphBudget original)
    {
        original.CheckWork();
        if (completePe.Length is 0 or > ResolvedGraphInputs.MaximumFileBytes ||
            completePdb.Length is 0 or > ResolvedGraphInputs.MaximumFileBytes ||
            completeDocumentBytes.Count is 0 or > MaximumDocuments)
            throw new SnapshotLimitException("Complete graph PE/PDB/document input bound exceeded.");
        foreach (var row in completeDocumentBytes)
        {
            original.CheckWork();
            if (row.Key.Length is 0 or > MaximumDocumentPath || row.Key.Contains('\0') ||
                row.Value.Length > ResolvedGraphInputs.MaximumFileBytes)
                throw new SnapshotLimitException("Complete compiler document name/byte bound exceeded.");
        }
        // These are in-memory views over retained complete inputs, not additional
        // file acquisitions. Their BCL parsing/disposal is synchronous/cooperative;
        // caller integration must account parser allocations in its joint ledger.
        using var peBytes = new MemoryStream(completePe, writable: false);
        using var pdbBytes = new MemoryStream(completePdb, writable: false);
        using var pe = new PEReader(peBytes, PEStreamOptions.LeaveOpen);
        original.CheckWork();
        if (!pe.HasMetadata) throw Refuse("Managed PE metadata required.");
        var metadata = pe.GetMetadataReader();
        original.CheckWork();
        var debugEntries = pe.ReadDebugDirectory();
        original.CheckWork();
        var codeViewEntries = debugEntries.Where(e => e.Type == DebugDirectoryEntryType.CodeView).ToArray();
        if (codeViewEntries.Length != 1) throw Refuse("Exactly one original CodeView identity required.");
        var entry = codeViewEntries[0];
        var codeView = pe.ReadCodeViewDebugDirectoryData(entry);
        original.CheckWork();
        using var provider = MetadataReaderProvider.FromPortablePdbStream(pdbBytes, MetadataStreamOptions.LeaveOpen);
        original.CheckWork();
        var pdb = provider.GetMetadataReader();
        original.CheckWork();
        var header = pdb.DebugMetadataHeader ?? throw Refuse("Portable PDB identity required.");
        var identity = new BlobContentId(header.Id);
        if (!entry.IsPortableCodeView || codeView.Age != 1 || codeView.Guid != identity.Guid || entry.Stamp != identity.Stamp)
            throw Refuse("PE and complete portable PDB identities differ.");
        if (pdb.Documents.Count > MaximumDocuments) throw new SnapshotLimitException("Portable PDB document count exceeded.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var handle in pdb.Documents)
        {
            original.CheckWork();
            var document = pdb.GetDocument(handle);
            var name = pdb.GetString(document.Name);
            original.CheckWork();
            if (name.Length is 0 or > MaximumDocumentPath || !seen.Add(name) ||
                !completeDocumentBytes.TryGetValue(name, out var source) || document.HashAlgorithm.IsNil || document.Hash.IsNil)
                throw Refuse("PDB document is duplicate, unbound, or lacks its complete checksum.");
            var algorithm = pdb.GetGuid(document.HashAlgorithm);
            original.CheckWork();
            var expected = pdb.GetBlobBytes(document.Hash);
            original.CheckWork();
            byte[] actual;
            if (algorithm == Sha256 && expected.Length == 32) actual = SHA256.HashData(source);
            else if (algorithm == Sha1 && expected.Length == 20) actual = SHA1.HashData(source);
            else throw Refuse("Unsupported or malformed PDB document checksum.");
            original.CheckWork();
            if (!actual.AsSpan().SequenceEqual(expected)) throw Refuse("PDB document differs from complete captured source bytes.");
        }
        // No requirement that a source without sequence points appear in the PDB:
        // complete Csc source membership is checked by the independent input gate.
        var module = metadata.GetModuleDefinition();
        var mvid = metadata.GetGuid(module.Mvid);
        original.CheckWork();
        var peHash = Convert.ToHexString(SHA256.HashData(completePe)).ToLowerInvariant();
        original.CheckWork();
        var pdbHash = Convert.ToHexString(SHA256.HashData(completePdb)).ToLowerInvariant();
        original.CheckWork();
        return new(mvid, identity.Guid, identity.Stamp, seen.Count, peHash, pdbHash);
    }

    private static EvaluationContractException Refuse(string message) => new(message);
}

internal sealed record ResolvedGraphOutputData(Guid ModuleVersionId, Guid PortablePdbGuid,
    uint PortablePdbStamp, int MatchedDocuments, string PeSha256, string PdbSha256);
