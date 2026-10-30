namespace Mutate4CSharp.Tests;

public sealed class ResolvedGraphSharedOriginalBudgetTests
{
    [Fact]
    public void Coexisting_readers_cannot_renew_same_original_semantic_admission()
    {
        var original = new ResolvedGraphBudget(TimeSpan.FromMinutes(1), CancellationToken.None);
        using var first = new ResolvedGraphInputs(original);
        using var second = new ResolvedGraphInputs(original);
        using var third = new ResolvedGraphInputs(original);
        const long mib = 1024 * 1024;
        first.ReserveAllocation(200 * mib);
        second.ReserveAllocation(200 * mib);
        third.ReserveAllocation(112 * mib);
        // No large arrays are allocated by this admission test. All three real
        // readers retain the SAME original; a fourth byte cannot be renewed by
        // choosing a different reader. Source32 incorrectly permits this byte.
        Assert.Throws<SnapshotLimitException>(() => third.ReserveAllocation(1));
        Assert.NotNull(original.Observe().Cleanup);
        Assert.Throws<EvaluationContractException>(original.CheckWork);
    }

    [Fact]
    public void Child_reservations_are_prebirth_finite_nonrefundable_and_foreign_returns_cannot_reissue_them()
    {
        var original = new ResolvedGraphBudget(TimeSpan.FromMinutes(1), CancellationToken.None);
        var grant = original.Resources.ReserveChild("logger");
        var before = original.Resources.ObservePreDelivery();
        Assert.Equal(ResolvedGraphResources.LoggerIoLimit, before.IoCharged);
        // Resource contract DATA test, not a fake native-owner positive.
        var usage = new GraphResourceUsage(grant, 100, 200, 2);
        original.Resources.ObserveChildReturn(grant, usage, 20);
        Assert.Equal(before.IoCharged, original.Resources.ObservePreDelivery().IoCharged);
        var foreign = grant with { };
        Assert.Throws<SnapshotLimitException>(() => original.Resources.ObserveChildReturn(foreign, usage, 20));
        Assert.NotNull(original.Observe().Cleanup);
    }

    [Fact]
    public void Imported_child_subgrant_is_exact_and_cannot_renew_original_clock_or_issue_another_child()
    {
        var original = new ResolvedGraphBudget(TimeSpan.FromMinutes(1), CancellationToken.None);
        var grant = original.Resources.ReserveChild("logger");
        var child = new ResolvedGraphBudget(original.Observe(), CancellationToken.None, grant);
        Assert.Equal(original.Observe().Original, child.Observe().Original);
        child.Resources.AdmitIo(ResolvedGraphResources.LoggerIoLimit);
        Assert.Throws<SnapshotLimitException>(() => child.Resources.AdmitIo(1));
        Assert.NotNull(child.Observe().Cleanup);
        Assert.Throws<EvaluationContractException>(() => ResolvedGraphResources.RequireGrant(grant with { IoBytes = grant.IoBytes + 1 }, "logger"));
        Assert.Throws<EvaluationContractException>(() => ResolvedGraphResources.RequireGrant(grant with { Role = "worker" }, "logger"));
    }
}
