using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis.Text;

namespace Mutate4CSharp.Tests;

public sealed class PortablePdbCoverageTests
{
    [Theory]
    [InlineData("valid")]
    [InlineData("changed-source")]
    [InlineData("changed-pdb")]
    [InlineData("wrong-offset")]
    [InlineData("coverlet-line")]
    [InlineData("coverlet-visited-line")]
    public async Task PlaceholderCoverageNeedsVerifiedAssemblyPdbSourceAndSequenceOffset(string alteration)
    {
        using var repository = new SnapshotTestRepository();
        repository.WriteText("src/A.cs", "public static class A { public static bool Value() => true; }\n");
        repository.Git("add", ".");
        repository.Git("commit", "-m", "PDB coverage fixture");
        await using var snapshot = await SnapshotCapture.CaptureAsync(repository.Root, "HEAD", [],
            SnapshotCaptureOptions.Default, TestContext.Current.CancellationToken);
        await using var clone = await SnapshotWorkspace.CreateCloneAsync(snapshot, "pdb-proof", TestContext.Current.CancellationToken);
        var file = Path.Combine(clone.Root, "src/A.cs");
        SourceText text;
        using (var sourceStream = File.OpenRead(file))
            text = SourceText.From(sourceStream, Encoding.UTF8, SourceHashAlgorithm.Sha256);
        var tree = CSharpSyntaxTree.ParseText(text, path: file, cancellationToken: TestContext.Current.CancellationToken);
        var compilation = CSharpCompilation.Create("PdbFixture", [tree],
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, deterministic: true));
        var assembly = Path.Combine(clone.Root, "PdbFixture.dll");
        var pdb = Path.ChangeExtension(assembly, ".pdb");
        using (var pe = File.Create(assembly))
        using (var symbols = File.Create(pdb))
            Assert.True(compilation.Emit(pe, symbols, options: new EmitOptions(
                debugInformationFormat: DebugInformationFormat.PortablePdb),
                cancellationToken: TestContext.Current.CancellationToken).Success);

        int token = 0, offset = 0, line = 0, column = 0, endLine = 0, endColumn = 0;
        using (var stream = File.OpenRead(pdb))
        using (var provider = MetadataReaderProvider.FromPortablePdbStream(stream))
        {
            var reader = provider.GetMetadataReader();
            foreach (var handle in reader.MethodDebugInformation)
            {
                var points = reader.GetMethodDebugInformation(handle).GetSequencePoints().Where(item => !item.IsHidden).ToArray();
                if (points.Length == 0) continue;
                var point = points[0];
                token = 0x06000000 | MetadataTokens.GetRowNumber(handle);
                offset = point.Offset;
                line = point.StartLine; column = point.StartColumn;
                endLine = point.EndLine; endColumn = point.EndColumn;
                break;
            }
        }
        Assert.NotEqual(0, token);
        if (alteration == "changed-source") File.WriteAllText(file, "public class Different {}\n");
        if (alteration == "changed-pdb")
        {
            var bytes = File.ReadAllBytes(pdb);
            bytes[^1] ^= 1;
            File.WriteAllBytes(pdb, bytes);
        }
        if (alteration == "wrong-offset") offset += 10000;
        var report = Path.Combine(clone.Root, "coverage.xml");
        var tokenXml = alteration.StartsWith("coverlet-", StringComparison.Ordinal)
            ? "<MetadataToken/><Name>System.Boolean A::Value()</Name>" : $"<MetadataToken>{token}</MetadataToken>";
        var visits = alteration == "coverlet-visited-line" ? 1 : 0;
        var offsetXml = alteration.StartsWith("coverlet-", StringComparison.Ordinal) ? "" : $"offset=\"{offset}\"";
        File.WriteAllText(report, $"""
            <CoverageSession><Modules><Module><ModulePath>{System.Security.SecurityElement.Escape(assembly)}</ModulePath>
            <Files><File uid="1" fullPath="{System.Security.SecurityElement.Escape(file)}"/></Files>
            <Classes><Class><Methods><Method>{tokenXml}<FileRef uid="1"/><SequencePoints>
            <SequencePoint vc="{visits}" {offsetXml} sl="{line}" sc="1" el="{line}" ec="2"/>
            </SequencePoints></Method></Methods></Class></Classes></Module></Modules></CoverageSession>
            """);
        var map = CoverageMap.Load([report], clone, snapshot)!;

        Assert.Equal(alteration is "valid" or "coverlet-line" ? CoverageState.Uncovered : CoverageState.Unknown,
            map.GetState("src/A.cs", line, column, endLine, endColumn));
    }
}
