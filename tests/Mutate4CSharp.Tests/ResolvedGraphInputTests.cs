using System.Security.Cryptography;
using System.Text;

namespace Mutate4CSharp.Tests;

// Genuine temporary-file prerequisites. All cases remain UNEXECUTED until the
// orchestrator grants the actual selected-runtime validation slot.
public sealed class ResolvedGraphInputTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mutate-graph-input-" + Guid.NewGuid().ToString("N"));
    public ResolvedGraphInputTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void Complete_real_input_closes_before_use_and_changed_bytes_do_not_reuse_an_old_identity()
    {
        var path = Path.Combine(_root, "Source.cs");
        var bytes = Encoding.UTF8.GetBytes("public static class Subject { public const int Value = 1; }");
        File.WriteAllBytes(path, bytes);
        using var owner = new ResolvedGraphInputs(TimeSpan.FromSeconds(30), CancellationToken.None);
        var actual = owner.Read(path, "source");
        Assert.Equal(bytes, actual.Bytes);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), actual.Sha256);
        using (var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.Equal(bytes.Length, exclusive.Length);
            exclusive.Position = Array.IndexOf(bytes, (byte)'1'); exclusive.WriteByte((byte)'2'); exclusive.Flush(true);
        }
        Assert.Throws<SnapshotDivergedException>(() => owner.Recheck(actual));
    }

    [Fact]
    public void Complete_new_result_is_first_closed_and_collision_preserves_the_original_file()
    {
        var path = Path.Combine(_root, "resolved-graph-result.json");
        var bytes = Encoding.UTF8.GetBytes("{\"actual\":true,\"values\":[1,2,3]}");
        using (var owner = new ResolvedGraphInputs(TimeSpan.FromSeconds(30), CancellationToken.None))
            owner.WriteNewResult(path, bytes);
        Assert.Equal(bytes, File.ReadAllBytes(path));
        using (var exclusive = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
            Assert.Equal(bytes.Length, exclusive.Length);
        var second = new ResolvedGraphInputs(TimeSpan.FromSeconds(30), CancellationToken.None);
        Assert.Throws<AggregateException>(() => second.WriteNewResult(path, [0]));
        var firstDispositionFailure = Assert.Throws<AggregateException>(() => second.Dispose());
        Assert.Same(firstDispositionFailure, Assert.Throws<AggregateException>(() => second.Dispose()));
        Assert.Equal(bytes, File.ReadAllBytes(path));
    }

    [Fact]
    public void Actual_generated_directory_enumerator_is_first_closed_before_entries_are_delivered()
    {
        var directory = Path.Combine(_root, "generated"); Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "Generated.cs"); File.WriteAllText(path, "internal class Generated {}");
        using var owner = new ResolvedGraphInputs(TimeSpan.FromSeconds(30), CancellationToken.None);
        Assert.True(owner.GeneratedDirectoryPresent(directory));
        var row = Assert.Single(owner.EnumerateGeneratedDirectory(directory));
        Assert.Equal(path, row.Path); Assert.Equal(0, (int)(row.Attributes & FileAttributes.ReparsePoint));
        // A fresh actual observation is allowed only after the first one ended;
        // it is not mistaken for continuation of that original enumerator.
        Assert.Equal(path, Assert.Single(owner.EnumerateGeneratedDirectory(directory)).Path);
    }
    [Fact]
    public void Restrictive_request_limit_uses_actual_file_length_and_closes_refused_original_without_retry()
    {
        var path = Path.Combine(_root, "request.json");
        var bytes = Encoding.UTF8.GetBytes("{\"complete\":\"original request\"}");
        File.WriteAllBytes(path, bytes);
        using (var positive = new ResolvedGraphInputs(TimeSpan.FromSeconds(30), CancellationToken.None))
            Assert.Equal(bytes, positive.Read(path, "config", bytes.Length).Bytes);
        var refused = new ResolvedGraphInputs(TimeSpan.FromSeconds(30), CancellationToken.None);
        Assert.Throws<AggregateException>(() => refused.Read(path, "config", bytes.Length - 1));
        var retained = Assert.Throws<AggregateException>(refused.Dispose);
        Assert.Same(retained, Assert.Throws<AggregateException>(refused.Dispose));
        using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.Equal(bytes.Length, exclusive.Length);
        var unchanged = new byte[bytes.Length]; exclusive.ReadExactly(unchanged);
        Assert.Equal(bytes, unchanged);
    }
    [Fact]
    public void Complete_large_streamed_image_positive_rechecks_every_byte_and_retains_no_image_array()
    {
        var path = Path.Combine(_root, "compiler-image.dll");
        const int length = 19 * 1024 * 1024;
        using (var created = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
        {
            created.SetLength(length); created.Position = length - 1; created.WriteByte(17); created.Flush(true);
        }
        var original = new ResolvedGraphBudget(TimeSpan.FromSeconds(30), CancellationToken.None);
        using var inputs = new ResolvedGraphInputs(original);
        var pin = inputs.PinImage(path);
        Assert.Equal(length, pin.Bytes);
        using (var complete = File.OpenRead(path))
            Assert.Equal(Convert.ToHexString(SHA256.HashData(complete)).ToLowerInvariant(), pin.Sha256);
        inputs.Recheck(pin);
        var charged = original.Resources.ObservePreDelivery();
        Assert.Equal(2L * (length + 1), charged.IoCharged);
        Assert.True(charged.SemanticCharged < 128 * 1024);
        // First stream/hash dispositions have returned: obtain a real exclusive
        // writer, then change the final byte, not only a cached prefix.
        using (var changed = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            changed.Position = length - 1; changed.WriteByte(18); changed.Flush(true);
        }
        Assert.Throws<SnapshotDivergedException>(() => inputs.Recheck(pin));
        Assert.NotNull(original.Observe().Cleanup);
        Assert.Throws<EvaluationContractException>(() => inputs.PinImage(path));
    }

    public void Dispose()
    {
        var target = Path.GetFullPath(_root);
        var temporary = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) + Path.DirectorySeparatorChar;
        if (!target.StartsWith(temporary, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) ||
            !Path.GetFileName(target).StartsWith("mutate-graph-input-", StringComparison.Ordinal))
            throw new InvalidOperationException("Only this test's owned temporary fixture can be removed.");
        Directory.Delete(target, recursive: true);
    }
}
