namespace Mutate4CSharp;

internal static class ExecutionPolicy
{
    public const int MaxStabilityRepetitions = 100;
    public static EvaluationPolicy Default { get; } = new(1, 100, 600, 120, 1800, false, 1);

    public static EvaluationPolicy Create(int maxWorkers, int mutationCap, TimeSpan baselineTimeout,
        TimeSpan mutantTimeout, TimeSpan overallDeadline, bool allowNotApplicable, int stabilityRepetitions) =>
        Validate(new(maxWorkers, mutationCap, WholeSeconds(baselineTimeout, nameof(baselineTimeout)),
            WholeSeconds(mutantTimeout, nameof(mutantTimeout)), WholeSeconds(overallDeadline,
                nameof(overallDeadline)), allowNotApplicable, stabilityRepetitions));

    public static EvaluationPolicy Validate(EvaluationPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        Positive(policy.MaxWorkers, nameof(policy.MaxWorkers));
        Positive(policy.MutationCap, nameof(policy.MutationCap));
        Positive(policy.BaselineTimeoutSeconds, nameof(policy.BaselineTimeoutSeconds));
        Positive(policy.MutantTimeoutSeconds, nameof(policy.MutantTimeoutSeconds));
        Positive(policy.OverallDeadlineSeconds, nameof(policy.OverallDeadlineSeconds));
        Positive(policy.StabilityRepetitions, nameof(policy.StabilityRepetitions));
        if (policy.StabilityRepetitions > MaxStabilityRepetitions)
            throw new ArgumentOutOfRangeException(nameof(policy.StabilityRepetitions),
                $"Stability repetitions cannot exceed {MaxStabilityRepetitions}.");
        return policy;
    }

    public static EvaluationPolicy ValidateContract(EvaluationPolicy policy)
    {
        try { return Validate(policy); }
        catch (ArgumentException ex)
        {
            throw new EvaluationContractException($"Execution policy violates the evaluation contract: {ex.Message}", ex);
        }
    }

    private static int WholeSeconds(TimeSpan value, string name)
    {
        if (value <= TimeSpan.Zero || value > TimeSpan.FromSeconds(int.MaxValue) ||
            value.Ticks % TimeSpan.TicksPerSecond != 0)
            throw new ArgumentOutOfRangeException(name, "Execution-policy durations must be positive whole finite seconds.");
        return checked((int)value.TotalSeconds);
    }

    private static void Positive(int value, string name)
    {
        if (value < 1) throw new ArgumentOutOfRangeException(name, "Execution-policy values must be positive.");
    }
}
