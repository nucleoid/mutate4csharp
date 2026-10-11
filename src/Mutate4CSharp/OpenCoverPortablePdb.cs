using System.Globalization;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Xml.Linq;

namespace Mutate4CSharp;

internal sealed record ProvenSequenceSpan(int StartLine, int StartColumn, int EndLine, int EndColumn);

// Coverlet's OpenCover columns can be placeholders. Only a matching portable PDB
// and captured source checksum can supply precise source spans for those points.
internal sealed class OpenCoverPortablePdb
{
    private readonly Dictionary<(int Token, int Offset, string File), ProvenSequenceSpan> _spans = new();
    private readonly Dictionary<(string Method, string File), List<ProvenSequenceSpan>> _methods = new();
    private readonly string _root;

    private OpenCoverPortablePdb(string root) => _root = root;

    internal static OpenCoverPortablePdb? Load(XElement module, SnapshotClone clone, InputSnapshot snapshot,
        Action<string>? diagnostic = null)
    {
        try
        {
            var modulePaths = module.Elements().Where(item => item.Name.LocalName == "ModulePath").ToArray();
            if (modulePaths.Length != 1) return Reject("module-path-missing");
            var modulePath = modulePaths[0].Value;
            var assembly = Path.GetFullPath(modulePath, clone.Root);
            if (!File.Exists(assembly) && modulePath == Path.GetFileName(modulePath) &&
                !modulePath.Contains('*') && !modulePath.Contains('?'))
            {
                var candidates = Directory.EnumerateFiles(clone.Root, modulePath, new EnumerationOptions
                {
                    RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint
                }).Where(path => path.Split(Path.DirectorySeparatorChar).Contains("bin", StringComparer.OrdinalIgnoreCase))
                    .Order(StringComparer.Ordinal).Take(129).ToArray();
                if (candidates.Length is 0 or > 128) return Reject("module-output-missing-or-unbounded");
                var identities = new HashSet<string>(StringComparer.Ordinal);
                foreach (var candidate in candidates)
                {
                    EnsureWithin(clone.Root, candidate);
                    var symbols = Path.ChangeExtension(candidate, ".pdb");
                    EnsureWithin(clone.Root, symbols);
                    identities.Add(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(candidate))) + ":" +
                        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(symbols))));
                }
                if (identities.Count != 1) return Reject("module-output-ambiguous");
                assembly = candidates[0];
            }
            EnsureWithin(clone.Root, assembly);
            var pdbPath = Path.ChangeExtension(assembly, ".pdb");
            EnsureWithin(clone.Root, pdbPath);
            var pdbBytes = File.ReadAllBytes(pdbPath);
            using var peStream = File.OpenRead(assembly);
            using var pe = new PEReader(peStream);
            using var provider = MetadataReaderProvider.FromPortablePdbStream(new MemoryStream(pdbBytes));
            var reader = provider.GetMetadataReader();
            var id = reader.DebugMetadataHeader?.Id;
            if (id is null || id.Value.Length < 20) return Reject("pdb-id-missing");
            var guid = new Guid(id.Value.AsSpan()[..16]);
            var codeViews = pe.ReadDebugDirectory().Where(item => item.Type == DebugDirectoryEntryType.CodeView).ToArray();
            if (codeViews.Length != 1 || pe.ReadCodeViewDebugDirectoryData(codeViews[0]).Guid != guid ||
                pe.ReadCodeViewDebugDirectoryData(codeViews[0]).Age != 1 ||
                codeViews[0].Stamp != System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(id.Value.AsSpan()[16..20]))
                return Reject("assembly-pdb-id-mismatch");
            var checksums = pe.ReadDebugDirectory().Where(item => item.Type == DebugDirectoryEntryType.PdbChecksum).ToArray();
            if (checksums.Length != 1) return Reject("pdb-checksum-missing");
            var checksum = pe.ReadPdbChecksumDebugDirectoryData(checksums[0]);
            var checksumBytes = pdbBytes.ToArray();
            var idOffset = reader.DebugMetadataHeader!.IdStartOffset;
            if (idOffset < 0 || idOffset > checksumBytes.Length - 20) return Reject("pdb-id-offset-invalid");
            Array.Clear(checksumBytes, idOffset, 20);
            if (checksum.AlgorithmName != "SHA256" ||
                !CryptographicOperations.FixedTimeEquals(checksum.Checksum.AsSpan(), SHA256.HashData(checksumBytes)))
                return Reject("pdb-checksum-mismatch");

            var captured = snapshot.Files.Where(item => item.Exists)
                .ToDictionary(item => item.RelativePath, OperatingSystem.IsWindows()
                    ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
            var documents = new Dictionary<DocumentHandle, string>();
            foreach (var handle in reader.Documents)
            {
                var document = reader.GetDocument(handle);
                var file = Path.GetFullPath(reader.GetString(document.Name), clone.Root);
                EnsureWithin(clone.Root, file);
                var relative = Path.GetRelativePath(clone.Root, file).Replace('\\', '/');
                if (!captured.TryGetValue(relative, out var input)) continue;
                var bytes = File.ReadAllBytes(file);
                if (bytes.LongLength != input.Length ||
                    Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() != input.Sha256) continue;
                var algorithm = reader.GetGuid(document.HashAlgorithm);
                var hash = algorithm == new Guid("8829d00f-11b8-4213-878b-770e8597ac16") ? SHA256.HashData(bytes) :
                    algorithm == new Guid("ff1816ec-aa5e-4d10-87f7-6f4963833460") ? SHA1.HashData(bytes) : null;
                if (hash is null || !CryptographicOperations.FixedTimeEquals(hash, reader.GetBlobBytes(document.Hash))) continue;
                documents.Add(handle, file);
            }
            var result = new OpenCoverPortablePdb(clone.Root);
            var metadata = pe.GetMetadataReader();
            foreach (var handle in reader.MethodDebugInformation)
            {
                var row = MetadataTokens.GetRowNumber(handle);
                if (row > metadata.MethodDefinitions.Count) return null;
                var method = metadata.GetMethodDefinition(MetadataTokens.MethodDefinitionHandle(row));
                if (method.RelativeVirtualAddress == 0) continue;
                var length = pe.GetMethodBody(method.RelativeVirtualAddress).GetILBytes()?.Length ?? 0;
                var debug = reader.GetMethodDebugInformation(handle);
                string? methodName = null;
                try
                {
                    var signature = method.DecodeSignature(new CecilSignatureNames(), (object?)null);
                    methodName = signature.ReturnType + " " + CecilSignatureNames.TypeName(metadata, method.GetDeclaringType()) +
                        "::" + metadata.GetString(method.Name) + "(" + string.Join(',', signature.ParameterTypes) + ")";
                }
                catch (NotSupportedException) { }
                foreach (var point in debug.GetSequencePoints())
                {
                    var document = point.Document.IsNil ? debug.Document : point.Document;
                    if (point.IsHidden || point.Offset < 0 || point.Offset >= length ||
                        !documents.TryGetValue(document, out var file) || point.StartLine <= 0 ||
                        point.StartColumn <= 0 || point.EndLine < point.StartLine || point.EndColumn <= 0 ||
                        point.StartLine == point.EndLine && point.EndColumn <= point.StartColumn) continue;
                    if (!result._spans.TryAdd((0x06000000 | row, point.Offset, IdentityPath(file)),
                            new(point.StartLine, point.StartColumn, point.EndLine, point.EndColumn))) return null;
                    if (methodName is not null)
                    {
                        var key = (methodName, IdentityPath(file));
                        if (!result._methods.TryGetValue(key, out var methodSpans)) result._methods[key] = methodSpans = [];
                        methodSpans.Add(new(point.StartLine, point.StartColumn, point.EndLine, point.EndColumn));
                    }
                }
            }
            diagnostic?.Invoke($"pdb-documents={documents.Count};pdb-spans={result._spans.Count}");
            return result;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or BadImageFormatException or
            ArgumentException or InvalidOperationException)
        {
            return Reject("pdb-refused-" + error.GetType().Name);
        }

        OpenCoverPortablePdb? Reject(string reason)
        {
            diagnostic?.Invoke(reason);
            return null;
        }
    }

    internal ProvenSequenceSpan? Resolve(XElement method, XElement point, string file, int line)
    {
        var tokens = method.Elements().Where(item => item.Name.LocalName == "MetadataToken").ToArray();
        if (tokens.Length != 1 || !int.TryParse(tokens[0].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var token) ||
            !int.TryParse(point.Attribute("offset")?.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var offset)) return null;
        var path = Path.GetFullPath(file, _root);
        return _spans.TryGetValue((token, offset, IdentityPath(path)), out var span) && span.StartLine == line ? span : null;
    }

    internal IReadOnlyList<ProvenSequenceSpan>? ResolveCoverletLine(XElement method, string file, int line)
    {
        var names = method.Elements().Where(item => item.Name.LocalName == "Name").ToArray();
        if (names.Length != 1 || !_methods.TryGetValue((names[0].Value, IdentityPath(Path.GetFullPath(file, _root))), out var spans))
            return null;
        var reported = method.Descendants().Where(item => item.Name.LocalName == "SequencePoint")
            .Select(item => int.TryParse(item.Attribute("sl")?.Value, NumberStyles.None, CultureInfo.InvariantCulture,
                out var value) ? value : -1).ToArray();
        if (reported.Any(item => item <= 0) || reported.Distinct().Count() != reported.Length ||
            spans.Select(item => item.StartLine).Except(reported).Any()) return null;
        var matching = spans.Where(item => item.StartLine == line).Distinct().ToArray();
        return matching.Length == 0 ? null : matching;
    }

    private static string IdentityPath(string path) => OperatingSystem.IsWindows() ? path.ToUpperInvariant() : path;

    private static void EnsureWithin(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        if (Path.IsPathRooted(relative) || relative is ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar,
                StringComparison.Ordinal)) throw new IOException("Portable PDB input is outside the baseline clone.");
        for (var current = new FileInfo(path).Directory; current is not null; current = current.Parent)
        {
            if ((current.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Portable PDB input traverses a link.");
            if (string.Equals(current.FullName, Path.GetFullPath(root), OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) break;
        }
        AtomicOwnedFile.RejectSpecialPath(path, "Portable PDB input");
    }
}
