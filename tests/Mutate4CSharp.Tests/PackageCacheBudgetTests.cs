namespace Mutate4CSharp.Tests;

// Actual bounded file operations only; no compiler, package restore, native child
// or accepted graph/coverage evidence is created by these controls.
public sealed class PackageCacheBudgetTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Closed_original_refuses_package_copy_or_fingerprint_before_enumeration(bool fingerprint)
    {
        var absent = Path.Combine(Path.GetTempPath(), "mutate-package-never-created-" + Guid.NewGuid().ToString("N"));
        var original = new ResolvedGraphBudget(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        original.BeginFailureCleanup(); var before = original.Observe();
        if (fingerprint)
            Assert.Throws<EvaluationContractException>(() => ExecutionEnvironment.FingerprintPackages(absent, originalBudget: original));
        else
            await Assert.ThrowsAsync<EvaluationContractException>(() => ExecutionEnvironment.CopyTreeAsync(absent, absent + "-copy", TestContext.Current.CancellationToken, original));
        Assert.Equal(before, original.Observe()); Assert.False(Directory.Exists(absent)); Assert.False(Directory.Exists(absent + "-copy"));
    }

    [Fact]
    public async Task Actual_package_copy_and_chunked_fingerprint_keep_same_original_and_v1_digest()
    {
        var root = Path.Combine(Path.GetTempPath(), "mutate-package-budget-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var source = Path.Combine(root, "source"); var destination = Path.Combine(root, "destination");
            Directory.CreateDirectory(source); Directory.CreateDirectory(destination);
            var bytes = Enumerable.Range(0, 200_000).Select(index => (byte)(index % 251)).ToArray();
            await File.WriteAllBytesAsync(Path.Combine(source, "content.bin"), bytes, TestContext.Current.CancellationToken);
            var original = new ResolvedGraphBudget(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken); var before = original.Observe();
            await ExecutionEnvironment.CopyTreeAsync(source, destination, TestContext.Current.CancellationToken, original);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(Path.Combine(destination, "content.bin"), TestContext.Current.CancellationToken));
            var expected = ExecutionEnvironment.FingerprintPackages(source, cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(expected, ExecutionEnvironment.FingerprintPackages(destination, cancellationToken: TestContext.Current.CancellationToken, originalBudget: original));
            Assert.Equal(before, original.Observe()); original.CheckWork();
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
