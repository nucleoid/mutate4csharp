namespace Mutate4CSharp;

internal sealed record CoverageProvenance(
    string SchemaVersion,
    string RunId,
    string SuiteId,
    string EvaluationFingerprint,
    string SnapshotId,
    BaselineStatus Baseline,
    string CoverageReportSha256,
    long CoverageReportLength,
    string PathMapVersion,
    string RunnerIdentity,
    bool CollectedFresh)
{
    public void Validate(string expectedFingerprint, string expectedSnapshotId, string expectedRunId,
        string expectedSuiteId)
    {
        if (SchemaVersion != "1" || RunId != expectedRunId || SuiteId != expectedSuiteId ||
            string.IsNullOrWhiteSpace(SuiteId) || SuiteId.Length > 512 ||
            EvaluationFingerprint != expectedFingerprint ||
            !Mutate4CSharp.EvaluationFingerprint.IsFingerprint(EvaluationFingerprint) ||
            SnapshotId != expectedSnapshotId ||
            !Mutate4CSharp.EvaluationFingerprint.IsSha256(SnapshotId) || Baseline != BaselineStatus.Green ||
            !Mutate4CSharp.EvaluationFingerprint.IsSha256(CoverageReportSha256) || CoverageReportLength < 1 ||
            string.IsNullOrWhiteSpace(PathMapVersion) || PathMapVersion.Length > 128 ||
            string.IsNullOrWhiteSpace(RunnerIdentity) || RunnerIdentity.Length > 1024 || !CollectedFresh)
            throw new EvaluationContractException("Coverage provenance is incomplete or does not match evaluation inputs.");
    }
}
