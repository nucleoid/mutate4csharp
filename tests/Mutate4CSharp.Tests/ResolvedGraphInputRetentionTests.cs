using System.Collections;
using System.Reflection;

namespace Mutate4CSharp.Tests;

// Inert resource-lifetime control. Private-frame setup below is a test seam,
// never compiler provenance or an accepted production input receipt.
public sealed class ResolvedGraphInputRetentionTests
{
    [Fact]
    public async Task Expired_first_close_roots_same_actual_stream_and_refuses_namespace_release_without_retry()
    {
        var owned = OwnedDirectory.Create(OwnedDirectory.PrivateParent("retained-input"), "fixture");
        var root = owned.Root;
        var path = Path.Combine(root, "actual-held-input.bin");
        var original = new ResolvedGraphBudget(TimeSpan.FromSeconds(1), CancellationToken.None);
        var inputs = new ResolvedGraphInputs(original);
        var held = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read);
        held.WriteByte(42); held.Flush(true);
        var actualHandle = held.SafeFileHandle;
        const BindingFlags instance = BindingFlags.Instance | BindingFlags.NonPublic;
        var frameType = typeof(ResolvedGraphInputs).GetNestedType("Frame", BindingFlags.NonPublic)!;
        var frame = Activator.CreateInstance(frameType, nonPublic: true)!;
        frameType.GetField("Path", instance)!.SetValue(frame, path);
        frameType.GetField("Stream", instance)!.SetValue(frame, held);
        frameType.GetField("AcquireEntered", instance)!.SetValue(frame, true);
        frameType.GetField("AcquireReturned", instance)!.SetValue(frame, true);
        ((IList)typeof(ResolvedGraphInputs).GetField("_frames", instance)!.GetValue(inputs)!).Add(frame);
        await Task.Delay(TimeSpan.FromSeconds(1.05), TestContext.Current.CancellationToken);
        var failure = Assert.Throws<AggregateException>(inputs.Dispose);
        Assert.False(actualHandle.IsClosed);
        var retained = (IEnumerable)typeof(ResolvedGraphInputs).GetField("Unsettled", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        Assert.Contains(retained.Cast<object>(), value => ReferenceEquals(value, inputs));
        Assert.Throws<SnapshotCleanupException>(() => ResolvedGraphInputs.AssertDirectoryMayBeRemoved(root));
        await Assert.ThrowsAsync<SnapshotCleanupException>(() => owned.DisposeAsync().AsTask());
        Assert.True(Directory.Exists(root)); Assert.True(File.Exists(path));
        Assert.Same(failure, Assert.Throws<AggregateException>(inputs.Dispose));
        Assert.False(actualHandle.IsClosed);
        Assert.Throws<IOException>(() => { using var writer = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None); });
        // Deliberately retain the SAME frame/stream/namespace after expiry. The
        // fixture does not dispose late or pretend a fresh budget owns this handle.
    }
}
