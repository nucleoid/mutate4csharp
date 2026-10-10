using System.Text.Json.Serialization;

namespace Mutate4CSharp;

internal sealed record EvaluationEvidence(string Kind, string Summary,
    IReadOnlyList<string>? Diagnostics = null)
{
    public const int MaxDiagnostics = 20;
    public const int MaxDiagnosticLength = 512;

    public static IReadOnlyList<string> BoundDiagnostics(IEnumerable<string> diagnostics,
        IReadOnlyList<string>? requiredTail = null, string truncationLabel = "diagnostics-truncated")
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        requiredTail ??= [];
        if (requiredTail.Count > MaxDiagnostics)
            throw new EvaluationContractException("Required evidence diagnostics exceed schema bounds.");
        var values = diagnostics.Select(Bound).ToArray();
        var tail = requiredTail.Select(Bound).ToArray();
        var available = MaxDiagnostics - tail.Length;
        if (values.Length <= available) return values.Concat(tail).ToArray();
        if (available == 0)
            throw new EvaluationContractException("Evidence diagnostics leave no room for truncation accounting.");
        var retained = available - 1;
        return values.Take(retained)
            .Append(Bound($"{truncationLabel}={values.Length - retained}"))
            .Concat(tail).ToArray();
    }

    public static IReadOnlyList<string> BoundDiagnostics(IEnumerable<string> retainedDiagnostics,
        int originalCount, string truncationLabel = "diagnostics-truncated")
    {
        ArgumentNullException.ThrowIfNull(retainedDiagnostics);
        var values = retainedDiagnostics.Select(Bound).ToArray();
        if (originalCount < values.Length)
            throw new EvaluationContractException("Original diagnostic count cannot be smaller than retained evidence.");
        if (originalCount <= MaxDiagnostics) return values;
        var retained = Math.Min(values.Length, MaxDiagnostics - 1);
        return values.Take(retained)
            .Append(Bound($"{truncationLabel}={originalCount - retained}"))
            .ToArray();
    }

    private static string Bound(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return EvaluationTextBounds.Prefix(value, MaxDiagnosticLength);
    }
}
internal sealed record EvaluationReason
{
    public const int MaxMessageLength = 1024;

    public EvaluationReason(string code, string message)
    {
        Code = code;
        Message = BoundMessage(message);
    }

    public string Code { get; init; }
    public string Message { get; init; }

    public static string BoundMessage(string message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return EvaluationTextBounds.Prefix(message, MaxMessageLength);
    }
}

internal static class EvaluationTextBounds
{
    public static string Prefix(string value, int maximum)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentOutOfRangeException.ThrowIfNegative(maximum);
        if (value.Length <= maximum) return value;
        var length = maximum;
        if (length > 0 && char.IsHighSurrogate(value[length - 1]) &&
            char.IsLowSurrogate(value[length])) length--;
        return value[..length];
    }

    public static string Suffix(string value, int maximum)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentOutOfRangeException.ThrowIfNegative(maximum);
        if (maximum == 0) return string.Empty;
        if (value.Length <= maximum) return value;
        var start = value.Length - maximum;
        if (start > 0 && char.IsLowSurrogate(value[start]) &&
            char.IsHighSurrogate(value[start - 1])) start++;
        return value[start..];
    }
}
internal sealed record EvaluationUnitResult(string UnitId, string EvaluationUnitId,
    UnitDisposition Disposition, IReadOnlyList<EvaluationEvidence> Evidence)
{
    private object? OriginatingPlanToken { get; init; }
    private object? ExpectedCompletionToken { get; init; }
    private object? CompletionToken { get; init; }
    private EvaluationPolicy? ExpectedPolicy { get; init; }
    private string? CompletionDigest { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool DiagnosticPartial { get; private init; }

    [JsonIgnore]
    public string? OriginatingPlanFingerprint { get; private init; }

    internal static EvaluationUnitResult Issue(string unitId, string evaluationUnitId,
        UnitDisposition disposition, IReadOnlyList<EvaluationEvidence> evidence,
        bool diagnosticPartial, string planFingerprint, object planToken,
        object completionToken, EvaluationPolicy policy) =>
        new(unitId, evaluationUnitId, disposition, evidence)
        {
            DiagnosticPartial = diagnosticPartial,
            OriginatingPlanFingerprint = planFingerprint,
            OriginatingPlanToken = planToken,
            ExpectedCompletionToken = completionToken,
            ExpectedPolicy = policy
        };

    internal EvaluationUnitResult Complete(UnitDisposition disposition,
        IReadOnlyList<EvaluationEvidence> evidence) => this with
    {
        Disposition = disposition,
        Evidence = evidence,
        CompletionToken = null,
        CompletionDigest = null
    };

    internal EvaluationPolicy RequireIssuedPolicy(EvaluationPolicy? assertedPolicy = null)
    {
        if (Disposition != UnitDisposition.Pending || OriginatingPlanToken is null ||
            ExpectedCompletionToken is null || ExpectedPolicy is null)
            throw new EvaluationContractException(
                "Stability reduction requires a pending unit issued by a mutation selection plan.");
        if (assertedPolicy is not null && assertedPolicy != ExpectedPolicy)
            throw new EvaluationContractException(
                "Stability reduction policy does not match the issuing mutation selection plan.");
        return ExpectedPolicy;
    }

    internal EvaluationUnitResult CompleteFromStabilityReducer(UnitDisposition disposition,
        IReadOnlyList<EvaluationEvidence> evidence, EvaluationPolicy policy, object completionToken)
    {
        _ = RequireIssuedPolicy(policy);
        if (ExpectedCompletionToken is null || !ReferenceEquals(ExpectedCompletionToken, completionToken))
            throw new EvaluationContractException(
                "Stability completion authority does not match the issuing mutation selection plan.");
        var completed = this with
        {
            Disposition = disposition,
            Evidence = evidence,
            CompletionToken = completionToken
        };
        return completed with { CompletionDigest = completed.ComputeCompletionDigest() };
    }

    internal string RequireTrustedCompletionDigest(object completionToken, EvaluationPolicy policy)
    {
        if (!HasTrustedCompletion(completionToken, policy, CompletionDigest))
            throw new EvaluationContractException(
                "Stability completion no longer matches the reducer-issued result.");
        return CompletionDigest!;
    }

    internal bool HasTrustedCompletion(object completionToken, EvaluationPolicy policy,
        string? issuedCompletionDigest)
    {
        if (CompletionToken is null || !ReferenceEquals(CompletionToken, completionToken) ||
            ExpectedPolicy != policy || CompletionDigest is null || issuedCompletionDigest is null ||
            !string.Equals(CompletionDigest, issuedCompletionDigest, StringComparison.Ordinal) || Evidence is null ||
            string.IsNullOrWhiteSpace(UnitId) || string.IsNullOrWhiteSpace(EvaluationUnitId) ||
            Evidence.Any(item => item is null || string.IsNullOrWhiteSpace(item.Kind) ||
                string.IsNullOrWhiteSpace(item.Summary) || item.Diagnostics?.Any(value => value is null) == true))
            return false;
        return string.Equals(CompletionDigest, ComputeCompletionDigest(), StringComparison.Ordinal);
    }

    internal bool WasIssuedBy(object planToken) =>
        OriginatingPlanToken is not null && ReferenceEquals(OriginatingPlanToken, planToken);

    private string ComputeCompletionDigest()
    {
        var components = new List<(string Label, string Value)>
        {
            ("unit", UnitId),
            ("evaluation-unit", EvaluationUnitId),
            ("disposition", Disposition.ToString()),
            ("evidence-count", Evidence.Count.ToString(System.Globalization.CultureInfo.InvariantCulture))
        };
        for (var index = 0; index < Evidence.Count; index++)
        {
            var item = Evidence[index];
            var prefix = $"evidence-{index:D8}-";
            components.Add((prefix + "kind", item.Kind));
            components.Add((prefix + "summary", item.Summary));
            components.Add((prefix + "diagnostics-presence", item.Diagnostics is null ? "null" : "present"));
            var diagnostics = item.Diagnostics ?? [];
            components.Add((prefix + "diagnostic-count",
                diagnostics.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            for (var diagnosticIndex = 0; diagnosticIndex < diagnostics.Count; diagnosticIndex++)
                components.Add(($"{prefix}diagnostic-{diagnosticIndex:D8}", diagnostics[diagnosticIndex]));
        }
        return MutationIdentity.ComputeDigest("evaluation-unit-completion", components);
    }
}
internal sealed record EvaluationFacts(BaselineStatus Baseline, int? EnumerationCount,
    IReadOnlyList<EvaluationUnitResult> Units, bool AllowNotApplicable,
    IReadOnlyList<EvaluationReason> IncompleteConditions)
{
    public bool DiagnosticPartial => Units.Any(unit => unit.DiagnosticPartial);
}

internal sealed record EvaluationCounts(int? Enumerated, int Selected, int Executed,
    int FreshUncovered, int Omitted, int CompileInvalid, int Killed, int Survived, int Errors)
{
    public static EvaluationCounts From(int? enumerated, IReadOnlyList<EvaluationUnitResult> units) => new(
        enumerated,
        units.Count,
        units.Count(unit => unit.Disposition is UnitDisposition.Killed or UnitDisposition.Survived or
            UnitDisposition.CompileInvalid or UnitDisposition.Error or UnitDisposition.Unstable),
        units.Count(unit => unit.Disposition == UnitDisposition.Uncovered),
        units.Count(unit => unit.Disposition == UnitDisposition.Omitted),
        units.Count(unit => unit.Disposition == UnitDisposition.CompileInvalid),
        units.Count(unit => unit.Disposition == UnitDisposition.Killed),
        units.Count(unit => unit.Disposition == UnitDisposition.Survived),
        units.Count(unit => unit.Disposition is UnitDisposition.Error or UnitDisposition.Unstable));
}

internal sealed record EvaluationDecision(EvaluationOutcome Outcome, int ExitCode,
    EvaluationCounts Counts, IReadOnlyList<EvaluationReason> Reasons,
    IReadOnlyList<EvaluationEvidence> Evidence);

internal sealed record EvaluationSelection(string Kind, string? BaseRevision,
    IReadOnlyList<string> Inputs);
internal sealed record EvaluationPolicy(int MaxWorkers, int MutationCap,
    int BaselineTimeoutSeconds, int MutantTimeoutSeconds, int OverallDeadlineSeconds,
    bool AllowNotApplicable, int StabilityRepetitions);
internal sealed record SuiteEvidence(string SuiteId, BaselineStatus Baseline,
    IReadOnlyList<EvaluationEvidence> Evidence);

internal sealed record EvaluationReport(
    string SchemaVersion,
    string RunId,
    DateTimeOffset GeneratedAtUtc,
    string Mode,
    EvaluationSelection Selection,
    ScopePlan ScopePlan,
    EvaluationPolicy Policy,
    BaselineStatus Baseline,
    IReadOnlyList<SuiteEvidence> Suites,
    IReadOnlyList<EvaluationUnitResult> Units,
    EvaluationCounts Counts,
    IReadOnlyList<EvaluationReason> IncompleteConditions,
    IReadOnlyList<EvaluationReason> Reasons,
    IReadOnlyList<EvaluationEvidence> Evidence,
    EvaluationOutcome Outcome,
    int ExitCode)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool DiagnosticPartial => Units.Any(unit => unit.DiagnosticPartial);

    public static EvaluationReport CreateSynthetic(EvaluationOutcome outcome, string runId, string reasonCode)
    {
        var unitDisposition = outcome switch
        {
            EvaluationOutcome.Pass or EvaluationOutcome.Incomplete => UnitDisposition.Killed,
            EvaluationOutcome.Fail => UnitDisposition.Survived,
            _ => (UnitDisposition?)null
        };
        IReadOnlyList<EvaluationUnitResult> units = unitDisposition is null ? [] :
            [new("mutation:v1:" + new string('a', 64), "evaluation:v1:" + new string('b', 64), unitDisposition.Value,
                [new("SYNTHETIC_UNIT", "Synthetic unit evidence.")])];
        IReadOnlyList<EvaluationReason> incompleteConditions = outcome == EvaluationOutcome.Incomplete
            ? [new(reasonCode, "Synthetic incomplete condition.")]
            : [];
        var facts = new EvaluationFacts(BaselineStatus.Green, units.Count, units, false, incompleteConditions);
        var decision = EvaluationReducer.Reduce(facts);
        IReadOnlyList<SuiteEvidence> suites = outcome == EvaluationOutcome.Incomplete ? [] :
            [new("synthetic-suite", BaselineStatus.Green,
                [new("SUITE_BASELINE", "Synthetic green baseline evidence.",
                    ["disposition=Passed", "tests=1", "accountedMembers=Synthetic.Tests.dll",
                     "expectedMembers=Synthetic.Tests.dll", "coverageSha256=" + new string('a', 64),
                     "coverageLength=1", "pathMap=baseline-clone-to-snapshot-v1"])])];
        var evidence = decision.Evidence;
        if (suites.Count > 0)
            evidence = evidence.Concat([ReportWriter.ConfigurationSuiteEvidence(
                suites.Select(suite => suite.SuiteId))]).ToArray();
        return new("1", runId, DateTimeOffset.UtcNow, "check",
            new("inputs", null, ["fixture.cs"]), ScopePlan.Empty("inputs", ".", null),
            DefaultPolicy, facts.Baseline, suites, facts.Units,
            decision.Counts, facts.IncompleteConditions, decision.Reasons, evidence,
            decision.Outcome, decision.ExitCode);
    }

    public static EvaluationPolicy DefaultPolicy => ExecutionPolicy.Default;
}
