namespace Mutate4CSharp;

internal enum ChangeKind { Added, Modified, Deleted, Renamed, Explicit }

internal sealed record ScopePlan(
    string SchemaVersion,
    string SelectionKind,
    string Root,
    string? BaseCommit,
    IReadOnlyList<ScopeFilePlan> Files,
    IReadOnlyList<ScopeExclusion> Exclusions,
    IReadOnlyList<ProjectScopeUnit> ProjectUnits,
    IReadOnlyList<EvaluationReason> Reasons,
    bool IsComplete)
{
    public static ScopePlan Empty(string selectionKind, string root, string? baseCommit,
        params EvaluationReason[] reasons) => new("1", selectionKind, root, baseCommit, [], [], [],
            reasons, reasons.Length == 0);
}

internal sealed record ScopeFilePlan(
    string Path,
    string? BasePath,
    ChangeKind Change,
    IReadOnlyList<DeclarationScope> Declarations,
    IReadOnlyList<DeclarationScope> Removals,
    bool RequiresProjectExpansion,
    IReadOnlyList<EvaluationReason> Reasons);

internal sealed record DeclarationScope(
    string Id,
    string Kind,
    string DisplayName,
    int StartLine,
    int EndLine,
    string Reason);

internal sealed record ScopeExclusion(string Path, string ReasonCode, string Message, bool Overridden);

internal sealed record ProjectScopeUnit(
    string Project,
    IReadOnlyList<string> Tests,
    IReadOnlyList<string> Inputs,
    string Expansion);

internal sealed record OwnershipResolution(
    IReadOnlyList<ProjectScopeUnit> Units,
    IReadOnlyList<EvaluationReason> Reasons);
