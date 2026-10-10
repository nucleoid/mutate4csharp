namespace Mutate4CSharp;

internal enum SuiteRunDisposition
{
    Passed = 0,
    Failed = 1,
    Empty = 2,
    Killed = 3,
    Survived = 4,
    Cancelled = 5,
    TimedOut = 6,
    Error = 7
}

internal sealed record SuiteRunResult(SuiteRunDisposition Disposition, TimeSpan Duration,
    IReadOnlyList<string> AccountedMembers, IReadOnlyList<string> FailedTestIds,
    IReadOnlyList<string> Diagnostics, IReadOnlyList<string> CoverageReports) : IAsyncDisposable
{
    internal OwnedCoverageReports? CoverageOwner { get; init; }
    internal CoverageMap? CoverageMap { get; init; }
    internal string? CoverageSha256 { get; init; }
    internal long CoverageLength { get; init; }
    internal bool HealthyControl { get; init; }
    public ValueTask DisposeAsync() => CoverageOwner?.DisposeAsync() ?? ValueTask.CompletedTask;
}

internal sealed record SuiteBaselineExecution(string Identity, IReadOnlyList<string> SuiteIds,
    SuiteRunResult Result);

internal sealed record SuiteBaselineResult(BaselineStatus Status,
    IReadOnlyList<SuiteBaselineExecution> Executions,
    IReadOnlyList<EvaluationReason> IncompleteConditions) : IAsyncDisposable
{
    public async ValueTask DisposeAsync()
    {
        await AsyncDisposal.DisposeAllAsync(Executions.Select(item => (IAsyncDisposable)item.Result));
    }
}

internal static class AsyncDisposal
{
    public static Exception CombineFailure(Exception? primaryFailure, Exception cleanupFailure)
    {
        ArgumentNullException.ThrowIfNull(cleanupFailure);
        if (cleanupFailure is SnapshotCleanupException { OriginalFailure: null } cleanupOnly)
            cleanupFailure = cleanupOnly.CleanupFailure;
        if (primaryFailure is not null)
            return new SnapshotCleanupException(primaryFailure, cleanupFailure);
        return cleanupFailure is SnapshotCleanupException
            ? cleanupFailure
            : new SnapshotCleanupException(cleanupFailure);
    }

    public static async ValueTask DisposeAllAsync(IEnumerable<IAsyncDisposable?> owners)
    {
        List<Exception>? failures = null;
        foreach (var owner in owners)
        {
            if (owner is null) continue;
            try { await owner.DisposeAsync(); }
            catch (Exception ex) { (failures ??= []).Add(ex); }
        }
        if (failures is not null)
            throw new SnapshotCleanupException(
                new AggregateException("One or more owned-resource cleanups failed.", failures));
    }

    public static async ValueTask DisposeAllPreservingFailureAsync(
        IEnumerable<IAsyncDisposable?> owners, Exception? primaryFailure)
    {
        Exception? failure = primaryFailure;
        try { await DisposeAllAsync(owners); }
        catch (Exception cleanupFailure)
        {
            failure = CombineFailure(primaryFailure, cleanupFailure);
        }
        if (failure is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}

internal sealed record SuiteMutationEvidence(string SuiteId, SuiteRunDisposition Disposition,
    IReadOnlyList<string> FailedTestIds, IReadOnlyList<string> Diagnostics,
    bool CompileInvalid = false);

internal sealed record AggregatedMutation(UnitDisposition Disposition,
    IReadOnlyList<EvaluationEvidence> Evidence);

internal interface ISuiteExecutor
{
    Task<SuiteRunResult> RunBaselineAsync(SuiteExecution suite, TimeSpan timeout,
        CancellationToken cancellationToken);
}

internal sealed class SuiteCoordinator
{
    private const int MaxFailedTests = 50;
    private const int MaxDiagnostics = 20;
    private const int MaxRetainedSuiteEvidence = 100;
    private readonly ISuiteExecutor _executor;
    private readonly TimeProvider _timeProvider;

    public SuiteCoordinator(ISuiteExecutor executor, TimeProvider timeProvider)
    {
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public async Task<SuiteBaselineResult> RunBaselinesAsync(string snapshotId,
        IReadOnlyList<SuiteExecution> suites, TimeSpan configuredTimeout, DateTimeOffset deadline,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(snapshotId))
            throw new ArgumentException("A snapshot identity is required.", nameof(snapshotId));
        if (suites is null || suites.Count == 0)
            throw new ArgumentException("At least one mapped suite is required.", nameof(suites));
        if (configuredTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(configuredTimeout));

        var executions = new List<SuiteBaselineExecution>();
        var conditions = new List<EvaluationReason>();
        var groups = suites.GroupBy(suite => ExecutionKey(snapshotId, suite), StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal).ToArray();
        foreach (var group in groups)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                AddCondition(conditions, "EXECUTION_CANCELLED", "Baseline execution was cancelled before all suites ran.");
                break;
            }
            var remaining = deadline - _timeProvider.GetUtcNow();
            if (remaining <= TimeSpan.Zero)
            {
                AddCondition(conditions, "OVERALL_DEADLINE_EXCEEDED", "The overall evaluation deadline expired during baselines.");
                break;
            }
            var timeout = remaining < configuredTimeout ? remaining : configuredTimeout;
            var members = group.SelectMany(item => item.ExpectedMembers).Distinct(StringComparer.Ordinal)
                .OrderBy(value => value, StringComparer.Ordinal).ToArray();
            var aliases = group.SelectMany(item => item.Aliases).Distinct(StringComparer.Ordinal)
                .OrderBy(value => value, StringComparer.Ordinal).ToArray();
            var first = group.First();
            var execution = first with { Identity = group.Key, Aliases = aliases, ExpectedMembers = members };
            SuiteRunResult result;
            SuiteRunResult? unboundedResult = null;
            try
            {
                unboundedResult = await _executor.RunBaselineAsync(execution, timeout, cancellationToken) ??
                    throw new EvaluationContractException("Suite executor returned no baseline result.");
                result = Bound(unboundedResult);
            }
            catch (OperationCanceledException ex)
            {
                if (unboundedResult is not null) await DisposeFailedResultAsync(unboundedResult, ex);
                AddCondition(conditions, "EXECUTION_CANCELLED", "Baseline execution was cancelled.");
                break;
            }
            catch (Exception ex) when (ex is SnapshotDivergedException or
                                       ExecutionBoundaryIntegrityException or
                                       SnapshotCleanupException or SnapshotLimitException)
            {
                if (unboundedResult is not null) await DisposeFailedResultAsync(unboundedResult, ex);
                throw;
            }
            catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException or AccessViolationException))
            {
                if (unboundedResult is not null)
                {
                    try { await unboundedResult.DisposeAsync(); }
                    catch (Exception cleanupFailure)
                    {
                        throw AsyncDisposal.CombineFailure(ex, cleanupFailure);
                    }
                }
                result = new(SuiteRunDisposition.Error, TimeSpan.Zero, [], [], [Bound(ex.Message, 512)], []);
            }
            executions.Add(new(group.Key, aliases, result));
            var missing = members.Except(result.AccountedMembers, StringComparer.OrdinalIgnoreCase).ToArray();
            if (missing.Length > 0)
                AddCondition(conditions, "SUITE_MEMBERS_MISSING",
                    $"Suite {aliases[0]} did not account for expected TRX member(s): {string.Join(", ", missing.Take(20))}.");
            if (result.Disposition == SuiteRunDisposition.Passed && result.CoverageReports.Count == 0)
                AddCondition(conditions, "COVERAGE_MISSING",
                    $"Suite {aliases[0]} did not produce fixture-verified Coverlet OpenCover output.");
            if (result.Disposition is SuiteRunDisposition.TimedOut)
                AddCondition(conditions, "BASELINE_TIMEOUT", $"Suite {aliases[0]} exceeded its baseline timeout.");
            else if (result.Disposition is SuiteRunDisposition.Cancelled)
                AddCondition(conditions, "EXECUTION_CANCELLED", $"Suite {aliases[0]} was cancelled.");
            else if (result.Disposition is SuiteRunDisposition.Error or SuiteRunDisposition.Killed or SuiteRunDisposition.Survived)
                AddCondition(conditions, "BASELINE_INCONCLUSIVE", $"Suite {aliases[0]} did not produce a valid baseline classification.");
        }

        if (cancellationToken.IsCancellationRequested)
            AddCondition(conditions, "EXECUTION_CANCELLED",
                "Baseline execution was cancelled before completion evidence was finalized.");

        var status = conditions.Count > 0 || executions.Count != groups.Length
            ? BaselineStatus.Unknown
            : executions.Any(item => item.Result.Disposition == SuiteRunDisposition.Failed)
                ? BaselineStatus.Red
                : executions.Any(item => item.Result.Disposition == SuiteRunDisposition.Empty)
                    ? BaselineStatus.Empty
                    : executions.All(item => item.Result.Disposition == SuiteRunDisposition.Passed)
                        ? BaselineStatus.Green
                        : BaselineStatus.Unknown;
        return new(status, executions, conditions);
    }

    public static AggregatedMutation AggregateMutant(string mutationId,
        IReadOnlyList<string> mappedSuiteIds, IReadOnlyList<SuiteMutationEvidence> suites)
    {
        if (!MutationIdentity.IsMutationId(mutationId))
            throw new EvaluationContractException("Suite aggregation requires a canonical mutation ID.");
        if (mappedSuiteIds is null || mappedSuiteIds.Count == 0 || mappedSuiteIds.Any(string.IsNullOrWhiteSpace) ||
            mappedSuiteIds.Distinct(StringComparer.Ordinal).Count() != mappedSuiteIds.Count)
            throw new EvaluationContractException("Every mutant requires distinct mapped suite IDs.");
        if (suites is null || suites.Count == 0)
            throw new EvaluationContractException("Every mutant requires evidence from all mapped suites.");
        var validEvidence = suites.Where(item => item is not null && !string.IsNullOrWhiteSpace(item.SuiteId) &&
                item.FailedTestIds is not null && item.Diagnostics is not null && Enum.IsDefined(item.Disposition))
            .ToArray();
        var invalidEntries = suites.Count - validEvidence.Length;
        var duplicate = validEvidence.GroupBy(item => item.SuiteId, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        var mapped = mappedSuiteIds.ToHashSet(StringComparer.Ordinal);
        var reported = validEvidence.Select(item => item.SuiteId).ToHashSet(StringComparer.Ordinal);
        var missing = mapped.Except(reported, StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal).ToArray();
        var unknown = reported.Except(mapped, StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal).ToArray();
        if (duplicate is not null || missing.Length > 0 || unknown.Length > 0 || invalidEntries > 0)
        {
            var kind = invalidEntries > 0 ? "SUITE_EVIDENCE_INVALID" : "SUITE_EVIDENCE_MISSING";
            var accountingRetained = validEvidence.OrderBy(item => item.SuiteId, StringComparer.Ordinal)
                .Take(MaxRetainedSuiteEvidence - 1).Select(ToEvidence).ToList();
            var duplicateEntries = validEvidence.Length - validEvidence.Select(item => item.SuiteId)
                .Distinct(StringComparer.Ordinal).Count();
            var details = new List<string>
            {
                $"received={suites.Count}", $"retained={accountingRetained.Count}",
                $"missing-count={missing.Length}", $"unknown-count={unknown.Length}",
                $"invalid-count={invalidEntries}", $"duplicate-entries={duplicateEntries}"
            };
            if (duplicate is not null) details.Add("duplicate=" + Bound(duplicate.Key, 128));
            details.AddRange(missing.Take(20).Select(value => "missing=" + Bound(value, 128)));
            details.AddRange(unknown.Take(20).Select(value => "unknown=" + Bound(value, 128)));
            accountingRetained.Insert(0, new(kind, "Mapped suite evidence failed exact accounting.", details));
            return new(UnitDisposition.Error, accountingRetained);
        }

        var orderedEvidence = validEvidence.OrderBy(item => item.SuiteId, StringComparer.Ordinal).ToArray();
        var retained = orderedEvidence.Take(MaxRetainedSuiteEvidence).Select(ToEvidence).ToList();
        if (orderedEvidence.Length > MaxRetainedSuiteEvidence)
        {
            retained = orderedEvidence.Take(MaxRetainedSuiteEvidence - 1).Select(ToEvidence).ToList();
            retained.Insert(0, new("SUITE_EVIDENCE_TRUNCATED",
                "Suite evidence was deterministically truncated to the bounded report limit.",
                [$"received={orderedEvidence.Length}", $"retained={retained.Count}",
                    $"omitted={orderedEvidence.Length - retained.Count}"]));
        }

        var compileInvalid = validEvidence.Any(item => item.CompileInvalid);
        var incomplete = validEvidence.Any(item => !item.CompileInvalid &&
            item.Disposition is SuiteRunDisposition.Cancelled or SuiteRunDisposition.TimedOut or
                SuiteRunDisposition.Error or SuiteRunDisposition.Empty or SuiteRunDisposition.Failed);
        var disposition = incomplete || compileInvalid && validEvidence.Any(item => !item.CompileInvalid)
            ? UnitDisposition.Error
            : compileInvalid ? UnitDisposition.CompileInvalid
            : validEvidence.Any(item => item.Disposition == SuiteRunDisposition.Killed) ? UnitDisposition.Killed
            : validEvidence.All(item => item.Disposition is SuiteRunDisposition.Survived or SuiteRunDisposition.Passed)
                ? UnitDisposition.Survived
                : UnitDisposition.Error;
        return new(disposition, retained);

        static EvaluationEvidence ToEvidence(SuiteMutationEvidence item) =>
            new("SUITE_MUTANT_RESULT",
                $"Suite {Bound(item.SuiteId, 128)} classified the mutant as {item.Disposition.ToString().ToUpperInvariant()}.",
                EvaluationEvidence.BoundDiagnostics(
                    new[] { $"failed-count={item.FailedTestIds.Count}", $"diagnostic-count={item.Diagnostics.Count}",
                        $"compile-invalid={item.CompileInvalid.ToString().ToLowerInvariant()}" }
                    .Concat(item.FailedTestIds.Take(MaxFailedTests).Select(value => "failed=" + Bound(value, 256)))
                    .Concat(item.Diagnostics.Take(MaxDiagnostics).Select(value => "diagnostic=" + Bound(value, 512)))));
    }

    public static EvaluationEvidence SurvivorEvidence(string mutationId, string path, int line,
        string original, string replacement, string planFingerprint) =>
        SurvivorEvidence(mutationId, path, line, original, replacement,
            new EvaluationSelection("inputs", null, [path]), planFingerprint);

    public static EvaluationEvidence SurvivorEvidence(string mutationId, string path, int line,
        string original, string replacement, EvaluationSelection selection, string planFingerprint)
    {
        if (!MutationIdentity.IsMutationId(mutationId) || !EvaluationFingerprint.IsFingerprint(planFingerprint))
            throw new EvaluationContractException("Survivor rerun evidence requires canonical mutation and plan IDs.");
        if (selection is null || selection.Kind == "base" && string.IsNullOrWhiteSpace(selection.BaseRevision) ||
            selection.Kind == "inputs" && (selection.Inputs is null || selection.Inputs.Count == 0 ||
                selection.Inputs.Any(string.IsNullOrWhiteSpace)) ||
            selection.Kind is not ("base" or "inputs"))
            throw new EvaluationContractException("Survivor rerun evidence requires the original exact selection.");
        var summaryPath = BoundWithoutSplittingSurrogate(path, 256);
        var summary = BoundWithoutSplittingSurrogate(
            $"{summaryPath}:{line} survived: {Bound(original, 96)} -> {Bound(replacement, 96)}", 512);
        var arguments = new List<string> { "dotnet", "tool", "run", "mutate4csharp", "--", "check" };
        if (selection.Kind == "base")
        {
            arguments.Add("--base");
            arguments.Add(selection.BaseRevision!);
        }
        else
        {
            foreach (var input in selection.Inputs)
            {
                arguments.Add("--input");
                arguments.Add(input);
            }
        }
        arguments.Add("--mutation-id");
        arguments.Add(mutationId);
        arguments.Add("--plan-fingerprint");
        arguments.Add(planFingerprint);
        var rerun = "rerun-argv-json=" + System.Text.Json.JsonSerializer.Serialize(arguments);
        var diagnostic = rerun.Length <= 512
            ? rerun
            : "rerun-argv-omitted=exact selection cannot fit the 512-character diagnostic limit; replay must be reconstructed from the immutable report selection.";
        return new("SURVIVOR", summary, [diagnostic]);
    }

    private static SuiteRunResult Bound(SuiteRunResult result)
    {
        RejectNullMembers(result.AccountedMembers, nameof(result.AccountedMembers));
        RejectNullMembers(result.FailedTestIds, nameof(result.FailedTestIds));
        RejectNullMembers(result.Diagnostics, nameof(result.Diagnostics));
        RejectNullMembers(result.CoverageReports, nameof(result.CoverageReports));
        return result with
        {
            AccountedMembers = result.AccountedMembers.Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase).Take(1000)
                .Select(value => Bound(value, 256)).ToArray(),
            FailedTestIds = result.FailedTestIds.Distinct(StringComparer.Ordinal).Take(MaxFailedTests)
                .Select(value => Bound(value, 256)).ToArray(),
            Diagnostics = result.Diagnostics.Take(MaxDiagnostics).Select(value => Bound(value, 512)).ToArray(),
            CoverageReports = result.CoverageReports.Distinct(StringComparer.Ordinal).Take(100)
                .Select(value => Bound(value, 1024)).ToArray()
        };
    }

    private static void RejectNullMembers(IReadOnlyList<string>? values, string member)
    {
        if (values is null || values.Any(value => value is null))
            throw new EvaluationContractException($"Suite executor returned an invalid {member} collection.");
    }

    private static async Task DisposeFailedResultAsync(SuiteRunResult result, Exception primaryFailure)
    {
        try { await result.DisposeAsync(); }
        catch (Exception cleanupFailure)
        {
            throw AsyncDisposal.CombineFailure(primaryFailure, cleanupFailure);
        }
    }

    private static string ExecutionKey(string snapshotId, SuiteExecution suite)
    {
        var members = suite.ExpectedMembers.OrderBy(value => value, StringComparer.Ordinal).ToArray();
        var components = new List<(string Name, string Value)>
        {
            ("snapshot", snapshotId), ("path", suite.Path), ("runner", suite.Runner),
            ("framework", suite.Framework), ("configuration", suite.Configuration),
            ("member-count", members.Length.ToString(System.Globalization.CultureInfo.InvariantCulture))
        };
        components.AddRange(members.Select((member, index) => ($"member.{index}", member)));
        return "suite-snapshot:v1:" + MutationIdentity.ComputeDigest("suite-snapshot-execution", components);
    }

    private static void AddCondition(List<EvaluationReason> conditions, string code, string message)
    {
        if (conditions.All(item => item.Code != code || item.Message != message))
            conditions.Add(new(code, message));
    }

    private static string Bound(string value, int maximum) => EvaluationTextBounds.Prefix(value, maximum);

    private static string BoundWithoutSplittingSurrogate(string value, int maximum)
    {
        return EvaluationTextBounds.Prefix(value, maximum);
    }
}
