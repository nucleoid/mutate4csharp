namespace Mutate4CSharp.Tests;

public sealed class ExecutionPolicyTests
{
    [Fact]
    public void DefaultsMatchThePublishedStrictPolicy()
    {
        var policy = ExecutionPolicy.Default;

        Assert.Equal(1, policy.MaxWorkers);
        Assert.Equal(100, policy.MutationCap);
        Assert.Equal(600, policy.BaselineTimeoutSeconds);
        Assert.Equal(120, policy.MutantTimeoutSeconds);
        Assert.Equal(1800, policy.OverallDeadlineSeconds);
        Assert.Equal(1, policy.StabilityRepetitions);
        Assert.Equal(policy, EvaluationReport.DefaultPolicy);
    }

    [Fact]
    public void PositiveOverridesArePreservedAndDoNotDependOnMeasuredBaselineTime()
    {
        var policy = ExecutionPolicy.Create(4, 25, TimeSpan.FromSeconds(30),
            TimeSpan.FromSeconds(7), TimeSpan.FromSeconds(90), false, 3);

        Assert.Equal(new EvaluationPolicy(4, 25, 30, 7, 90, false, 3), policy);
        Assert.Equal(policy, ExecutionPolicy.Validate(policy));
    }

    [Theory]
    [InlineData(0, 1, 1, 1, 1, 1)]
    [InlineData(1, 0, 1, 1, 1, 1)]
    [InlineData(1, 1, 0, 1, 1, 1)]
    [InlineData(1, 1, 1, 0, 1, 1)]
    [InlineData(1, 1, 1, 1, 0, 1)]
    [InlineData(1, 1, 1, 1, 1, 0)]
    public void RejectsNonPositiveOverrides(int workers, int cap, int baseline, int mutant,
        int overall, int repetitions)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ExecutionPolicy.Validate(
            new(workers, cap, baseline, mutant, overall, false, repetitions)));
    }

    [Fact]
    public void RejectsNonFiniteOrSubsecondDurations()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ExecutionPolicy.Create(1, 1,
            TimeSpan.MaxValue, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), false, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => ExecutionPolicy.Create(1, 1,
            TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), false, 1));
    }
}
