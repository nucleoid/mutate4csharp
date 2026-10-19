using System.Diagnostics;

namespace Mutate4CSharp;

// Restrictive view of one actual graph operation. An exported/imported observation
// is DATA only; the original process owner must separately prove its own same
// invocation and request. An imported deadline never creates process authority.
internal sealed class ResolvedGraphBudget
{
    private readonly object _gate = new();
    private readonly CancellationToken _token;
    private readonly long _original;
    private readonly TimeSpan _whole;
    private readonly TimeSpan _work;
    private long? _cleanup;

    internal ResolvedGraphBudget(TimeSpan enclosingRemaining, CancellationToken token)
    {
        if (enclosingRemaining <= TimeSpan.Zero || enclosingRemaining > TimeSpan.FromDays(7))
            throw new ArgumentOutOfRangeException(nameof(enclosingRemaining), "Graph work must remain within the actual enclosing policy and its existing seven-day schema maximum.");
        _original = Stopwatch.GetTimestamp();
        _whole = _work = enclosingRemaining;
        _token = token;
    }

    // The creating parent supplies its already-running original, never a fresh
    // worker allowance. Exact frequency/limits join before the first file read.
    internal ResolvedGraphBudget(ResolvedGraphBudgetObservation enclosing, CancellationToken token)
    {
        var now = Stopwatch.GetTimestamp();
        if (enclosing.Frequency != Stopwatch.Frequency || enclosing.Original <= 0 || enclosing.Original > now ||
            enclosing.WholeTicks <= 0 || enclosing.WholeTicks > TimeSpan.FromDays(7).Ticks ||
            enclosing.WorkTicks <= 0 || enclosing.WorkTicks > enclosing.WholeTicks ||
            enclosing.Cleanup is { } cleanup && (cleanup < enclosing.Original || cleanup > now))
            throw new EvaluationContractException("Invalid restrictive original graph budget observation.");
        _original = enclosing.Original;
        _whole = TimeSpan.FromTicks(enclosing.WholeTicks);
        _work = TimeSpan.FromTicks(enclosing.WorkTicks);
        _cleanup = enclosing.Cleanup;
        _token = token;
        CheckWork();
    }

    internal ResolvedGraphBudgetObservation Observe()
    {
        lock (_gate) return new(_original, Stopwatch.Frequency, _whole.Ticks, _work.Ticks, _cleanup);
    }

    internal void ImportEarlierCleanup(ResolvedGraphBudgetObservation enclosing)
    {
        lock (_gate)
        {
            var own = Observe();
            if (enclosing.Original != own.Original || enclosing.Frequency != own.Frequency ||
                enclosing.WholeTicks != own.WholeTicks || enclosing.WorkTicks != own.WorkTicks ||
                enclosing.Cleanup is { } invalid && (invalid < _original || invalid > Stopwatch.GetTimestamp()))
                throw new EvaluationContractException("Cleanup observation belongs to a different original graph budget.");
            if (enclosing.Cleanup is { } earlier && (_cleanup is null || earlier < _cleanup.Value)) _cleanup = earlier;
        }
    }

    internal void BeginFailureCleanup()
    {
        lock (_gate) _cleanup ??= Stopwatch.GetTimestamp();
    }

    internal void CheckWork()
    {
        lock (_gate)
        {
            if (_cleanup is not null) throw new EvaluationContractException("Actual graph owner has entered terminal cleanup; no further work.");
            _token.ThrowIfCancellationRequested();
            CheckWhole();
            if (Stopwatch.GetElapsedTime(_original) >= _work)
                throw new TimeoutException("Actual graph original work endpoint expired.");
        }
    }

    internal void CheckDisposition()
    {
        lock (_gate)
        {
            // Cancellation cannot veto an otherwise admitted first disposition.
            if (_token.IsCancellationRequested) _cleanup ??= Stopwatch.GetTimestamp();
            CheckWhole();
            if (_cleanup is { } cleanup && Stopwatch.GetElapsedTime(cleanup) >= TimeSpan.FromSeconds(60))
                throw new TimeoutException("Actual graph earliest once-cleanup endpoint expired.");
        }
    }

    internal TimeSpan RemainingWork
    {
        get { CheckWork(); return _work - Stopwatch.GetElapsedTime(_original); }
    }

    private void CheckWhole()
    {
        if (Stopwatch.GetElapsedTime(_original) >= _whole)
            throw new TimeoutException("Actual graph enclosing original deadline expired; no cleanup extension.");
    }
}

internal sealed record ResolvedGraphBudgetObservation(long Original, long Frequency,
    long WholeTicks, long WorkTicks, long? Cleanup);
