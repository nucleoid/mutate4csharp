using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Mutate4CSharp.Tests;

// Complete actual file bytes and the production discovery/parser/preservation
// chain. The XML is deliberately test DATA, not compiler/collector authority.
public sealed class PinnedGraphCoverageTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Captured_coverage_bytes_drive_parse_preservation_and_digest_despite_same_extent_replacement(bool replace)
    {
        using var repository = new SnapshotTestRepository();
        repository.WriteText("src/Target.cs", "public static class Target { public static int Value() => 0; }\n");
        repository.Git("add", "."); repository.Git("commit", "-m", "pinned coverage DATA control");
        var token = TestContext.Current.CancellationToken;
        await using var snapshot = await SnapshotCapture.CaptureAsync(repository.Root, "HEAD", [], SnapshotCaptureOptions.Default, token);
        await using var clone = await SnapshotWorkspace.CreateCloneAsync(snapshot, "pinned-coverage", token);
        var source = Path.Combine(clone.Root, "src", "Target.cs");
        var report = Path.Combine(clone.Root, "coverage.opencover.xml");
        var xml = $"""
            <CoverageSession><Modules><Module><Files><File uid="1" fullPath="{System.Security.SecurityElement.Escape(source)}"/></Files>
            <Classes><Class><Methods><Method><FileRef uid="1"/><SequencePoints>
            <SequencePoint vc="0" sl="1" sc="2" el="1" ec="3"/>
            </SequencePoints></Method></Methods></Class></Classes></Module></Modules></CoverageSession>
            """;
        var bytes = Encoding.UTF8.GetBytes(xml);
        await File.WriteAllBytesAsync(report, bytes, token);
        var original = new ResolvedGraphBudget(TimeSpan.FromSeconds(30), token);
        var inputs = new ResolvedGraphInputs(original);
        var captured = new Dictionary<string, ResolvedGraphFile>(StringComparer.Ordinal);
        var discovered = TestRunner.FindCoverage(clone.Root, inputs, captured);
        Assert.Equal(report, Assert.Single(discovered));
        var pin = Assert.Single(captured.Values);
        Assert.Equal(bytes, pin.Bytes);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), pin.Sha256);
        if (replace)
        {
            var changed = Encoding.UTF8.GetBytes(xml.Replace("vc=\"0\"", "vc=\"1\"", StringComparison.Ordinal));
            Assert.Equal(bytes.Length, changed.Length); Assert.NotEqual(bytes, changed);
            await File.WriteAllBytesAsync(report, changed, token);
            // The unpinned route really observes different semantics. The graph
            // parser must use its captured bytes, never a second pathname read.
            Assert.Equal(CoverageState.Covered, CoverageMap.Load([report], clone, snapshot)!.GetState("src/Target.cs", 1));
        }
        var map = CoverageMap.Load(discovered, clone, snapshot, pinnedCoverage: captured)!;
        Assert.Equal(CoverageState.Uncovered, map.GetState("src/Target.cs", 1));
        await using var preserved = await VstestSuiteExecutor.PreservePinnedOpenCoverReportsAsync(captured.Values, inputs, "pinned-control");
        Assert.NotNull(preserved);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(Assert.Single(preserved.Reports), token));
        var preservedPin = Assert.Single(preserved.Pins!);
        Assert.Equal(pin.Sha256, preservedPin.Sha256); Assert.Equal(pin.Bytes, preservedPin.Bytes);
        var length = new byte[8]; BinaryPrimitives.WriteInt64BigEndian(length, bytes.LongLength);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(length.Concat(bytes).ToArray())),
            VstestSuiteExecutor.HashPinnedReports(preserved.Pins!));
        if (replace)
        {
            // The actual baseline makes this recheck BEFORE parsing/preserving.
            // This control separately demonstrates that exact captured bytes
            // remain immutable while a same-length changed input is refused.
            var drift = Assert.Throws<SnapshotDivergedException>(() => inputs.Recheck(pin));
            Assert.Contains("changed across its compiler/semantic boundary", drift.Message, StringComparison.Ordinal);
            Assert.NotNull(original.Observe().Cleanup);
            // Recheck's mismatch is outside the completed read frame. It latches
            // retained failure/terminal WORK; it does not invent a close failure.
            var refusal = Assert.Throws<EvaluationContractException>(inputs.CheckWork);
            Assert.Contains("retained failure", refusal.Message, StringComparison.Ordinal);
            var neverWritten = Path.Combine(clone.Root, "must-remain-absent.data");
            Assert.Throws<EvaluationContractException>(() => inputs.WriteNewResult(neverWritten, [1]));
            Assert.False(File.Exists(neverWritten));
        }
        else { inputs.Recheck(pin); original.CheckWork(); Assert.Null(original.Observe().Cleanup); }
        // All actual read/write/enumerator frames already first-closed. Dispose
        // returns on this direct-Recheck path and its returned state is sticky;
        // unlike an in-frame append mismatch, no Aggregate is manufactured.
        inputs.Dispose();
        inputs.Dispose();
        Assert.Throws<EvaluationContractException>(inputs.CheckWork);
    }
}
