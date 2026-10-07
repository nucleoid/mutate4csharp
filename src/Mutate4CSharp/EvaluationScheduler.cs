namespace Mutate4CSharp;

internal sealed record ScheduledMutation(string MutationId, string EvaluationUnitId,
    IReadOnlyList<string> SuiteIds);

internal sealed record ScheduledMutationResult(string EvaluationUnitId, UnitDisposition Disposition,
    IReadOnlyList<EvaluationEvidence> Evidence);

internal sealed record EvaluationScheduleResult(IReadOnlyList<ScheduledMutationResult> Results,
    IReadOnlyList<EvaluationReason> IncompleteConditions);

internal interface IIsolatedMutationExecutor
{
    Task<ScheduledMutationResult> ExecuteAsync(ScheduledMutation mutation, TimeSpan timeout,
        CancellationToken cancellationToken);
}

internal sealed class EvaluationScheduler
{
    private readonly IIsolatedMutationExecutor _executor;
    private readonly TimeProvider _timeProvider;

    public EvaluationScheduler(IIsolatedMutationExecutor executor, TimeProvider timeProvider)
    {
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public async Task<EvaluationScheduleResult> RunAsync(IReadOnlyList<ScheduledMutation> mutations,
        int maxWorkers, TimeSpan configuredTimeout, DateTimeOffset deadline,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(mutations);
        if (maxWorkers is < 1 or > 32)
            throw new ArgumentOutOfRangeException(nameof(maxWorkers), "Worker count must be between 1 and 32.");
        if (configuredTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(configuredTimeout));
        Validate(mutations);

        var ordered = mutations.OrderBy(item => item.EvaluationUnitId, StringComparer.Ordinal).ToArray();
        var results = new ScheduledMutationResult?[ordered.Length];
        var next = -1;
        var cancelled = 0;
        var deadlineExceeded = 0;
        async Task Worker()
        {
            while (true)
            {
                var index = Interlocked.Increment(ref next);
                if (index >= ordered.Length) return;
                if (cancellationToken.IsCancellationRequested)
                {
                    Interlocked.Exchange(ref cancelled, 1);
                    return;
                }
                var remaining = deadline - _timeProvider.GetUtcNow();
                if (remaining <= TimeSpan.Zero)
                {
                    Interlocked.Exchange(ref deadlineExceeded, 1);
                    return;
                }
                var mutation = ordered[index];
                var timeout = remaining < configuredTimeout ? remaining : configuredTimeout;
                var started = _timeProvider.GetUtcNow();
                using var timeoutSource = new CancellationTokenSource(timeout, _timeProvider);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken,
                    timeoutSource.Token);
                try
                {
                    var result = await _executor.ExecuteAsync(mutation, timeout, linked.Token);
                    if (_timeProvider.GetUtcNow() - started >= timeout)
                    {
                        results[index] = Timeout(mutation, timeout);
                        continue;
                    }
                    if (!string.Equals(result.EvaluationUnitId, mutation.EvaluationUnitId, StringComparison.Ordinal) ||
                        result.Evidence is null || result.Evidence.Count == 0 ||
                        result.Disposition is UnitDisposition.Pending or UnitDisposition.Omitted)
                        throw new EvaluationContractException(
                            "Isolated mutation execution returned invalid or mismatched evidence.");
                    results[index] = Bound(result);
                }
                catch (OperationCanceledException) when (timeoutSource.IsCancellationRequested &&
                                                         !cancellationToken.IsCancellationRequested)
                {
                    results[index] = Timeout(mutation, timeout);
                }
                catch (OperationCanceledException)
                {
                    Interlocked.Exchange(ref cancelled, 1);
                    results[index] = Error(mutation, "Execution was cancelled after assignment.");
                    return;
                }
                catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException or AccessViolationException))
                {
                    results[index] = Error(mutation, ex.Message);
                }
            }
        }

        await Task.WhenAll(Enumerable.Range(0, Math.Min(maxWorkers, Math.Max(1, ordered.Length)))
            .Select(_ => Worker()));

        if (cancellationToken.IsCancellationRequested)
            Interlocked.Exchange(ref cancelled, 1);

        for (var index = 0; index < ordered.Length; index++)
        {
            if (results[index] is not null) continue;
            var reason = Volatile.Read(ref cancelled) == 1 ? "CANCELLED_BEFORE_ASSIGNMENT" :
                Volatile.Read(ref deadlineExceeded) == 1 ? "DEADLINE_BEFORE_ASSIGNMENT" : "NOT_ASSIGNED";
            results[index] = new(ordered[index].EvaluationUnitId, UnitDisposition.Omitted,
                [new(reason, "The bounded scheduler did not assign this evaluation unit.")]);
        }
        var conditions = new List<EvaluationReason>();
        if (Volatile.Read(ref cancelled) == 1)
            conditions.Add(new("EXECUTION_CANCELLED", "Cancellation stopped new mutant assignment; all units were reconciled."));
        if (Volatile.Read(ref deadlineExceeded) == 1)
            conditions.Add(new("OVERALL_DEADLINE_EXCEEDED", "The overall deadline stopped new mutant assignment; all units were reconciled."));
        return new(results.Select(item => item!).ToArray(), conditions);
    }

    private static void Validate(IReadOnlyList<ScheduledMutation> mutations)
    {
        var duplicate = mutations.GroupBy(item => item.EvaluationUnitId, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
            throw new EvaluationContractException($"Duplicate scheduled evaluation unit: {duplicate.Key}.");
        foreach (var mutation in mutations)
        {
            if (!MutationIdentity.IsMutationId(mutation.MutationId) ||
                !EvaluationUnitIdentity.IsEvaluationUnitId(mutation.EvaluationUnitId))
                throw new EvaluationContractException("The scheduler requires canonical mutation and evaluation-unit IDs.");
            if (mutation.SuiteIds is null || mutation.SuiteIds.Count == 0 ||
                mutation.SuiteIds.Any(string.IsNullOrWhiteSpace) ||
                mutation.SuiteIds.Distinct(StringComparer.Ordinal).Count() != mutation.SuiteIds.Count)
                throw new EvaluationContractException("Every scheduled mutation requires distinct mapped suite IDs.");
        }
    }

    private static ScheduledMutationResult Error(ScheduledMutation mutation, string detail) =>
        new(mutation.EvaluationUnitId, UnitDisposition.Error,
            [new("MUTANT_EXECUTION_INCOMPLETE", Bound(detail, 512))]);

    private static ScheduledMutationResult Timeout(ScheduledMutation mutation, TimeSpan timeout) =>
        new(mutation.EvaluationUnitId, UnitDisposition.Error,
            [new("MUTANT_TIMEOUT", $"The isolated mutant exceeded its {timeout.TotalSeconds:g}-second timeout.")]);

    private static ScheduledMutationResult Bound(ScheduledMutationResult result) => result with
    {
        Evidence = result.Evidence.Take(100).Select(item => new EvaluationEvidence(Bound(item.Kind, 128),
            Bound(item.Summary, 512), BoundDiagnostics(item.Diagnostics))).ToArray()
    };

    private static IReadOnlyList<string>? BoundDiagnostics(IReadOnlyList<string>? diagnostics)
    {
        if (diagnostics is null) return null;
        const int maximum = 20;
        if (diagnostics.Count <= maximum)
            return diagnostics.Select(value => Bound(value, 512)).ToArray();
        return diagnostics.Take(maximum - 1).Select(value => Bound(value, 512))
            .Append($"diagnostics-truncated={diagnostics.Count - (maximum - 1)}").ToArray();
    }

    private static string Bound(string value, int maximum) => value.Length <= maximum ? value : value[..maximum];
}
