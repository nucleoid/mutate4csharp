namespace Mutate4CSharp;

internal enum SidecarRecordKind { Discovery, Proven }

internal sealed record DiscoverySidecar(
    string SchemaVersion,
    SidecarRecordKind RecordKind,
    string RunId,
    DateTimeOffset GeneratedAtUtc,
    string EvaluationFingerprint,
    string SnapshotId,
    EvaluationOutcome EvaluationOutcome,
    bool ScopeComplete,
    IReadOnlyList<string> ScopeExclusions,
    string ReportSha256,
    long ReportLength);

internal sealed record ProvenEvaluationSidecar(
    string SchemaVersion,
    SidecarRecordKind RecordKind,
    string RunId,
    DateTimeOffset GeneratedAtUtc,
    string EvaluationFingerprint,
    string SnapshotId,
    string ReportSha256,
    long ReportLength,
    bool PolicyComplete,
    IReadOnlyList<CoverageProvenance> Coverage,
    EvaluationCounts Counts);

internal sealed record SidecarReadResult<T>(T? Record, string? Error)
{
    public bool IsValid => Record is not null && Error is null;
}
