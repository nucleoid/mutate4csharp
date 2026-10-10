using Microsoft.CodeAnalysis;

namespace Mutate4CSharp;

internal enum ExitCode { Success = 0, Usage = 1, BaselineFailed = 2, Survived = 3, Infrastructure = 4 }
internal enum MutantStatus { Killed, Survived, Timeout, Uncovered, CompileError, Error }

internal sealed record Options(string? Target, bool Help, bool Scan, bool UpdateManifest, bool ReuseCoverage,
    HashSet<int>? Lines, bool SinceLastRun, bool MutateAll, int MutationWarning, int MaxWorkers,
    double TimeoutFactor, bool Verbose, string? Project, string? TestProject, string? Root, string? CoverageReport,
    StrictCheckOptions? StrictCheck = null);
internal sealed record StrictCheckOptions(bool Plan, string? BaseRevision,
    IReadOnlyList<string> Inputs, string? ReportPath, string RunId, bool NoState = false,
    IReadOnlyList<string>? MutationIds = null, string? PlanFingerprint = null);
internal sealed record ScopeInfo(string Id, string Kind, int StartLine, int EndLine, string Hash, int Start, int End);
internal sealed record MutationSite(int Index, int Start, int Length, int Line, string Replacement,
    string Description, string ScopeId, string ScopeHash, string OperatorId = "legacy.unknown",
    string OperatorVersion = MutationIdentity.OperatorContractVersion)
{ public string Apply(string source) => source[..Start] + Replacement + source[(Start + Length)..]; }
internal sealed record AnalysisResult(string SourceWithoutManifest, string OriginalSource, SyntaxTree Tree,
    IReadOnlyList<ScopeInfo> Scopes, IReadOnlyList<MutationSite> Sites);
internal sealed record ManifestScope(string Id, string Kind, int StartLine, int EndLine, string Hash);
internal sealed record EmbeddedManifest(int Version, string ContextFingerprint, string SourceHash, List<ManifestScope> Scopes);
internal sealed record TestRunResult(int ExitCode, TimeSpan Duration, bool TimedOut, bool TestsDiscovered,
    bool HasFailedTests, bool TrxValid, string StandardOutput, string StandardError,
    IReadOnlyList<string> TrxPaths, bool HasRunErrors = false,
    IReadOnlyList<string>? FailedTestIds = null, IReadOnlyList<string>? Diagnostics = null,
    int? FailedTestCount = null, int? DiagnosticCount = null);
internal sealed record MutantResult(MutationSite Site, MutantStatus Status, TimeSpan Duration, string? Detail = null);
internal sealed record ProjectContext(string Root, string Project, string TestProject, string TargetRelativePath, string DisplayPath);
