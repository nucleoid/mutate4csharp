using System.Security.Cryptography;
using System.Runtime.InteropServices;

namespace Mutate4CSharp;

internal interface IEvaluationCoordinator
{
    Task<EvaluationRunResult> RunAsync(StrictCheckOptions options, CancellationToken cancellationToken);
}

internal sealed record EvaluationRunResult(EvaluationReport Report, string ReportPath, string? SnapshotId);

internal sealed class EvaluationCoordinator : IEvaluationCoordinator
{
    private readonly SnapshotCaptureOptions _captureOptions;
    private readonly Action<EvaluationPublicationPhase>? _beforePublication;

    public EvaluationCoordinator(SnapshotCaptureOptions? captureOptions = null,
        Action<EvaluationPublicationPhase>? beforePublication = null)
    {
        _captureOptions = captureOptions ?? SnapshotCaptureOptions.Default;
        _beforePublication = beforePublication;
    }

    public async Task<EvaluationRunResult> RunAsync(StrictCheckOptions options,
        CancellationToken cancellationToken)
    {
        var runId = options.RunId;
        var exactMutationIds = options.MutationIds ?? [];
        var exactIdRequest = exactMutationIds.Count > 0;
        var selection = options.BaseRevision is not null
            ? new EvaluationSelection("base", options.BaseRevision, [])
            : new EvaluationSelection("inputs", null, options.Inputs);
        var usesDefaultReportPath = options.ReportPath is null;
        var reportPath = usesDefaultReportPath
            ? Path.Combine(Environment.CurrentDirectory, ".mutate4csharp", "reports",
                $"{DateTimeOffset.UtcNow:yyyyMMddTHHmmssfffZ}-{runId}.json")
            : ReportWriter.ResolveSafeDestination(options.ReportPath!, options.Inputs);
        if (usesDefaultReportPath)
            new SidecarStore(Environment.CurrentDirectory).PrepareDefaultReportRoot();
        var scopePlan = ScopePlan.Empty(selection.Kind, ".", null,
            new EvaluationReason("SCOPE_UNAVAILABLE", "Scope planning did not complete."));
        InputSnapshot? snapshot = null;
        string? snapshotId = null;
        string? stateRoot = null;
        EvaluationFingerprintMaterial? fingerprintMaterial = null;
        MutationSelectionPlan? finalizingPlan = null;
        Exception? fingerprintMaterialFailure = null;
        string? semanticContextIdentity = null;
        string? sdkVersion = null;
        CheckConfiguration? checkConfiguration = null;
        int? enumerationCount = null;
        IReadOnlyList<EvaluationUnitResult> reportUnits = [];
        IReadOnlyList<EvaluationReason> enumerationReasons = [];
        var baseline = BaselineStatus.Unknown;
        IReadOnlyList<SuiteEvidence> reportSuites = [];
        IReadOnlyList<CoverageProvenance> coverageProvenance = [];
        EvaluationReason? reason = null;
        var evidence = new List<EvaluationEvidence>();
        try
        {
            var inputPaths = options.Inputs.Select(Path.GetFullPath).ToArray();
            var gitRoot = options.BaseRevision is not null
                ? await SnapshotCapture.FindRepositoryRootAsync(Environment.CurrentDirectory,
                    cancellationToken, _captureOptions)
                : FindGitMarkerRoot(inputPaths[0]);
            if (gitRoot is not null)
            {
                stateRoot = gitRoot;
                var excluded = ReportExclusions(gitRoot, reportPath);
                snapshot = await SnapshotCapture.CaptureAsync(gitRoot, options.BaseRevision ?? "HEAD",
                    inputPaths, _captureOptions with { ExcludedRelativePaths = excluded }, cancellationToken);
                snapshotId = snapshot.Identity.CaptureId;
                scopePlan = options.BaseRevision is not null
                    ? await ScopePlanner.PlanGitAsync(snapshot, cancellationToken)
                    : ScopePlanner.PlanCapturedExplicit(snapshot, inputPaths);
                checkConfiguration = LoadCapturedCheckConfiguration(snapshot);
                if (checkConfiguration is not null)
                {
                    ValidateConfiguredScope(checkConfiguration, scopePlan);
                    evidence.Add(ReportWriter.ConfigurationSuiteEvidence(
                        checkConfiguration.ExecutionSuites.Select(item => item.Identity),
                        $"Validated configuration v{checkConfiguration.SchemaVersion}: " +
                        $"{checkConfiguration.Projects.Count} project(s), " +
                        $"{checkConfiguration.ExecutionSuites.Count} distinct suite execution(s)."));
                }
                evidence.Add(new("INPUT_SNAPSHOT", $"Captured {snapshot.Identity.FileCount} files as {snapshotId}.",
                    [$"captureId={snapshotId}", $"baseCommit={snapshot.Identity.BaseCommit}",
                     $"indexFingerprint={snapshot.Identity.IndexFingerprint}",
                     $"totalBytes={snapshot.Identity.TotalBytes}", "dependencyFingerprint=not-prepared"]));
            }
            else
            {
                var explicitRoot = ResolveExplicitRoot(inputPaths);
                var explicitPlan = await ScopePlanner.PlanExplicitAsync(inputPaths, explicitRoot, cancellationToken);
                scopePlan = explicitPlan.Plan;
                checkConfiguration = LoadExplicitCheckConfiguration(explicitRoot, explicitPlan.ConfigurationBytes);
                if (checkConfiguration is not null)
                {
                    ValidateConfiguredScope(checkConfiguration, scopePlan);
                    evidence.Add(ReportWriter.ConfigurationSuiteEvidence(
                        checkConfiguration.ExecutionSuites.Select(item => item.Identity),
                        $"Validated configuration v{checkConfiguration.SchemaVersion}: " +
                        $"{checkConfiguration.Projects.Count} project(s), " +
                        $"{checkConfiguration.ExecutionSuites.Count} distinct suite execution(s)."));
                }
                evidence.Add(new("EXPLICIT_INPUT_CAPTURE",
                    $"Captured {scopePlan.Files.Count + scopePlan.Exclusions.Count} explicit non-Git input(s)."));
            }
            if (options.Plan)
            {
                reason = new("PLAN_ONLY", "Plan mode produces scope audit data and runs no tests.");
            }
            else if (snapshot is null)
            {
                reason = new("ENUMERATION_REQUIRES_GIT_SNAPSHOT",
                    "Strict semantic enumeration v1 requires one immutable Git-backed project snapshot.");
            }
            else if (checkConfiguration is null)
            {
                reason = new("ENUMERATION_CONFIGURATION_REQUIRED",
                    "Strict semantic enumeration requires validated mutate4csharp.json test-suite configuration.");
            }
            else if (!scopePlan.IsComplete)
            {
                reason = new("ENUMERATION_SCOPE_INCOMPLETE",
                    "Strict semantic enumeration requires one complete scope plan.");
            }
            else
            {
                var execution = await StrictExecutionPipeline.RunAsync(snapshot, snapshotId!, runId, scopePlan,
                    checkConfiguration, exactMutationIds, options.PlanFingerprint, cancellationToken);
                fingerprintMaterial = execution.FingerprintMaterial;
                finalizingPlan = execution.FinalizingPlan;
                semanticContextIdentity = execution.SemanticContextIdentity;
                sdkVersion = execution.SdkVersion;
                enumerationCount = execution.EnumerationCount;
                reportUnits = execution.Units;
                baseline = execution.Baseline;
                reportSuites = execution.Suites;
                coverageProvenance = execution.Coverage;
                enumerationReasons = execution.IncompleteConditions;
                reason = execution.Reason;
                var dependency = execution.FingerprintMaterial.Inputs.Single(input =>
                    input.Kind == "dependency" &&
                    input.Path == ".mutate4csharp/frozen-dependencies");
                var snapshotEvidenceIndex = evidence.FindIndex(item => item.Kind == "INPUT_SNAPSHOT");
                if (snapshotEvidenceIndex >= 0)
                {
                    var captured = evidence[snapshotEvidenceIndex];
                    evidence[snapshotEvidenceIndex] = captured with
                    {
                        Diagnostics = (captured.Diagnostics ?? [])
                            .Where(value => !value.StartsWith("dependencyFingerprint=",
                                StringComparison.Ordinal))
                            .Append("dependencyFingerprint=" +
                                (dependency.Sha256.StartsWith("sha256:", StringComparison.Ordinal)
                                    ? dependency.Sha256
                                    : "sha256:" + dependency.Sha256)).ToArray()
                    };
                }
                evidence.AddRange(execution.Evidence);
                if (exactIdRequest)
                {
                    var counters = new[]
                    {
                        $"executed={reportUnits.Count(item => item.Disposition is not (UnitDisposition.Omitted or UnitDisposition.Uncovered or UnitDisposition.Pending))}",
                        $"omitted={reportUnits.Count(item => item.Disposition == UnitDisposition.Omitted)}"
                    };
                    evidence.Add(new("EXACT_ID_REQUEST",
                        $"Freshly bound {exactMutationIds.Count} diagnostic mutation ID(s); " +
                        "terminal dispositions are recorded in the unit ledger.",
                        EvaluationEvidence.BoundDiagnostics(exactMutationIds, counters,
                            "ids-truncated")));
                }
            }
        }
        catch (StrictExecutionRefusalException ex)
        {
            reason = ex.Reason;
            enumerationReasons = ex.Reasons;
            evidence.Add(new("STRICT_EXECUTION_REFUSAL", reason.Message,
                ex.Reasons.Select(item => BoundDiagnostic($"{item.Code}: {item.Message}"))
                    .Take(20).ToArray()));
        }
        catch (SnapshotCaptureException ex)
        {
            reason = SnapshotFailure(ex);
            enumerationReasons = PreservedFailureReasons(reason, [], ex);
            AddSnapshotFailureEvidence(evidence, ex);
        }
        catch (OperationCanceledException)
        {
            reason = new("SNAPSHOT_CANCELLED", "Immutable capture or scope planning was cancelled.");
            evidence.Add(new("SNAPSHOT_CANCELLATION",
                "Cancellation was observed and owned capture state was cleaned up."));
        }
        catch (System.Text.DecoderFallbackException ex)
        {
            reason = new("SCOPE_UNSUPPORTED_ENCODING",
                "Scope planning requires supported UTF-8 or BOM-detected C# source encoding.");
            evidence.Add(new("SCOPE_PLAN_FAILURE", Bound(ex.Message)));
        }
        catch (CheckConfigurationException ex)
        {
            reason = new("CONFIGURATION_INVALID", "mutate4csharp.json is invalid for strict execution.");
            evidence.Add(new("CONFIGURATION_FAILURE", Bound(ex.Message)));
        }
        catch (ArgumentException ex)
        {
            reason = new("SCOPE_PLAN_INVALID", "Scope configuration or explicit input selection is invalid.");
            evidence.Add(new("SCOPE_PLAN_FAILURE", Bound(ex.Message)));
        }
        catch (Exception ex)
        {
            reason = new("SNAPSHOT_VALIDATION_FAILED",
                "Immutable capture or scope planning failed closed during validation.");
            evidence.Add(new("SNAPSHOT_VALIDATION_FAILURE", Bound($"{ex.GetType().Name}: {ex.Message}")));
        }

        if (exactIdRequest && evidence.All(item => item.Kind != "EXACT_ID_REQUEST"))
        {
            enumerationReasons = enumerationReasons.Concat([
                new EvaluationReason("EXACT_ID_RERUN_UNAVAILABLE",
                    "Exact-ID reruns require a freshly bound complete semantic enumeration plan.")
            ]).Distinct().ToArray();
            evidence.Add(new("EXACT_ID_REQUEST",
                $"Refused {exactMutationIds.Count} exact mutation ID request(s) without a complete bound plan.",
                EvaluationEvidence.BoundDiagnostics(exactMutationIds,
                    truncationLabel: "ids-truncated")));
        }
        evidence.Add(new("SCOPE_PLAN",
            $"Scope plan v{scopePlan.SchemaVersion}: {scopePlan.Files.Count} file(s), " +
            $"{scopePlan.ProjectUnits.Count} project unit(s), {scopePlan.Exclusions.Count} exclusion(s).",
            scopePlan.Reasons.Select(item => BoundDiagnostic($"{item.Code}: {item.Message}"))
                .Take(20).ToArray()));

        try
        {
            if (snapshot is not null)
            {
                try { await snapshot.ValidateOriginalAsync(cancellationToken); }
                catch (SnapshotCaptureException ex)
                {
                    var previousReason = reason;
                    var previousReasons = enumerationReasons;
                    reason = SnapshotFailure(ex);
                    InvalidateSnapshotExecutionEvidence(evidence, ref baseline, ref reportSuites);
                    AddSnapshotFailureEvidence(evidence, ex);
                    snapshotId = null;
                    enumerationCount = null;
                    reportUnits = [];
                    finalizingPlan = null;
                    enumerationReasons = PreservedFailureReasons(previousReason, previousReasons, ex);
                    scopePlan = ScopePlan.Empty(selection.Kind, ".", null,
                        new EvaluationReason("SCOPE_UNAVAILABLE", "Scope plan was invalidated by snapshot divergence."));
                }
                catch (OperationCanceledException ex)
                {
                    var previousReason = reason;
                    var previousReasons = enumerationReasons;
                    reason = new("SNAPSHOT_CANCELLED", "Immutable capture validation was cancelled.");
                    evidence.Add(new("SNAPSHOT_CANCELLATION",
                        "Original-input revalidation was cancelled; captured execution facts cannot authorize success."));
                    if (finalizingPlan is null)
                    {
                        InvalidateSnapshotExecutionEvidence(evidence, ref baseline, ref reportSuites);
                        snapshotId = null;
                        enumerationCount = null;
                        reportUnits = [];
                        scopePlan = ScopePlan.Empty(selection.Kind, ".", null,
                            new EvaluationReason("SCOPE_UNAVAILABLE", "Scope plan was invalidated by snapshot cancellation."));
                    }
                    enumerationReasons = PreservedFailureReasons(previousReason, previousReasons, ex);
                }
                catch (Exception ex)
                {
                    var previousReason = reason;
                    var previousReasons = enumerationReasons;
                    reason = new("SNAPSHOT_VALIDATION_FAILED",
                        "Immutable capture failed closed during native or runtime validation.");
                    InvalidateSnapshotExecutionEvidence(evidence, ref baseline, ref reportSuites);
                    evidence.Add(new("SNAPSHOT_VALIDATION_FAILURE",
                        Bound($"{ex.GetType().Name}: {ex.Message}")));
                    snapshotId = null;
                    enumerationCount = null;
                    reportUnits = [];
                    finalizingPlan = null;
                    enumerationReasons = PreservedFailureReasons(previousReason, previousReasons, ex);
                    scopePlan = ScopePlan.Empty(selection.Kind, ".", null,
                        new EvaluationReason("SCOPE_UNAVAILABLE", "Scope plan was invalidated by snapshot validation failure."));
                }
            }
            if (snapshot is not null)
            {
                if (!options.Plan && !exactIdRequest && snapshotId is not null &&
                    (!options.NoState || checkConfiguration is not null))
                {
                    try
                    {
                        sdkVersion ??= await ResolveSdkVersionAsync(snapshot, cancellationToken);
                        fingerprintMaterial ??= BuildEvaluationFingerprintMaterial(
                            snapshot, snapshotId, scopePlan, checkConfiguration?.Policy ?? EvaluationReport.DefaultPolicy,
                            semanticContextIdentity ?? "semantic-context-unavailable", sdkVersion);
                        evidence.Add(new("TOOL_INTERNAL_SDK",
                            "Recorded the SDK resolved inside the captured consumer snapshot.",
                            [fingerprintMaterial.SdkIdentity]));
                    }
                    catch (Exception ex) when (!IsFatal(ex))
                    {
                        fingerprintMaterialFailure = ex;
                    }
                }
                try { await snapshot.DisposeAsync(); }
                catch (Exception ex)
                {
                    var previousReason = reason;
                    var previousReasons = enumerationReasons;
                    reason = new("SNAPSHOT_CLEANUP_FAILED",
                        "The immutable capture could not be cleaned up safely.");
                    InvalidateSnapshotExecutionEvidence(evidence, ref baseline, ref reportSuites);
                    if (ex is SnapshotCaptureException snapshotFailure)
                    {
                        AddSnapshotFailureEvidence(evidence, snapshotFailure);
                    }
                    else
                    {
                        evidence.Add(new("SNAPSHOT_CLEANUP", Bound(ex.Message)));
                    }
                    enumerationReasons = PreservedFailureReasons(previousReason, previousReasons, ex);
                    snapshotId = null;
                    enumerationCount = null;
                    reportUnits = [];
                    finalizingPlan = null;
                    scopePlan = ScopePlan.Empty(selection.Kind, ".", null,
                        new EvaluationReason("SCOPE_UNAVAILABLE", "Scope plan was invalidated by snapshot cleanup failure."));
                }
                snapshot = null;
            }
            if (exactIdRequest && evidence.All(item => item.Kind != "EXACT_ID_REQUEST"))
            {
                enumerationReasons = enumerationReasons.Concat([
                    new EvaluationReason("EXACT_ID_RERUN_UNAVAILABLE",
                        "Exact-ID reruns require a freshly bound complete semantic enumeration plan.")
                ]).Distinct().ToArray();
                evidence.Add(new("EXACT_ID_REQUEST",
                    $"Refused {exactMutationIds.Count} exact mutation ID request(s) without a complete bound plan.",
                    EvaluationEvidence.BoundDiagnostics(exactMutationIds,
                        truncationLabel: "ids-truncated")));
            }
            var incompleteConditions = new List<EvaluationReason>();
            if (reason is not null) incompleteConditions.Add(reason);
            incompleteConditions.AddRange(enumerationReasons);
            incompleteConditions.AddRange(scopePlan.Reasons.Concat(scopePlan.Files.SelectMany(file => file.Reasons))
                .Where(item => ScopePlanner.IsBlockingCode(item.Code)));
            var distinctConditions = incompleteConditions.Distinct().ToArray();
            var facts = finalizingPlan is not null
                ? finalizingPlan.FinalizeFacts(baseline, reportUnits,
                    checkConfiguration?.Policy.AllowNotApplicable ?? false, distinctConditions)
                : new EvaluationFacts(baseline, enumerationCount, reportUnits,
                    checkConfiguration?.Policy.AllowNotApplicable ?? false, distinctConditions);
            var decision = EvaluationReducer.Reduce(facts);
            var reportEvidence = decision.Evidence.Concat(evidence).ToArray();
            var report = new EvaluationReport("1", runId, DateTimeOffset.UtcNow,
                options.Plan ? "plan" : "check", selection, scopePlan,
                checkConfiguration?.Policy ?? EvaluationReport.DefaultPolicy,
                facts.Baseline, reportSuites, facts.Units, decision.Counts, facts.IncompleteConditions,
                decision.Reasons, reportEvidence, decision.Outcome, decision.ExitCode);
            SidecarStore? sidecarStore = null;
            if (!options.NoState && !options.Plan && !exactIdRequest && stateRoot is not null && snapshotId is not null &&
                fingerprintMaterial is not null)
            {
                try
                {
                    sidecarStore = new SidecarStore(stateRoot);
                    sidecarStore.PrepareForPublication();
                }
                catch (Exception ex) when (!IsFatal(ex))
                {
                    sidecarStore = null;
                    report = WithStatePublicationFailure(report, facts, evidence, ex);
                }
            }
            if (fingerprintMaterialFailure is not null)
                report = WithFingerprintFailure(report, facts, evidence, fingerprintMaterialFailure);
            report = EvaluationPublication.Publish(reportPath, options.Inputs, report, facts,
                fingerprintMaterialFailure is null ? sidecarStore : null, snapshotId, fingerprintMaterial,
                finalizingPlan, coverageProvenance, _beforePublication);
            if (fingerprintMaterialFailure is OperationCanceledException cancellation)
                throw cancellation;
            return new(report, reportPath, snapshotId);
        }
        finally
        {
            if (snapshot is not null) await snapshot.DisposeAsync();
        }
    }

    private static CheckConfiguration? LoadCapturedCheckConfiguration(InputSnapshot snapshot)
    {
        var file = snapshot.Files.SingleOrDefault(item => item.Exists &&
            item.RelativePath.Equals("mutate4csharp.json", StringComparison.OrdinalIgnoreCase));
        if (file is null) return null;
        var path = Path.Combine(snapshot.CaptureRoot,
            file.RelativePath.Replace('/', Path.DirectorySeparatorChar));
        try
        {
            var bytes = File.ReadAllBytes(path);
            using var document = System.Text.Json.JsonDocument.Parse(bytes);
            if (!document.RootElement.EnumerateObject().Any(property =>
                    property.Name.Equals("testSuites", StringComparison.OrdinalIgnoreCase))) return null;
            return CheckConfiguration.Load(snapshot.CaptureRoot, bytes);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or
                                   System.Text.Json.JsonException)
        {
            throw new CheckConfigurationException($"Strict configuration validation failed: {ex.Message}", ex);
        }
    }

    private static void ValidateConfiguredScope(CheckConfiguration configuration, ScopePlan scopePlan)
    {
        var suites = configuration.ExecutionPathsByAlias();
        foreach (var unit in scopePlan.ProjectUnits)
        {
            var matches = configuration.Projects.Where(item =>
                item.Project.Equals(unit.Project, StringComparison.Ordinal)).ToArray();
            if (matches.Length != 1)
                throw new CheckConfigurationException(
                    $"Scope project {unit.Project} has {(matches.Length == 0 ? "no" : "ambiguous")} strict configuration membership.");
            var expected = matches[0].TestSuites.Select(id => suites[id]).Distinct(StringComparer.Ordinal)
                .OrderBy(value => value, StringComparer.Ordinal).ToArray();
            var actual = unit.Tests.OrderBy(value => value, StringComparer.Ordinal).ToArray();
            if (!expected.SequenceEqual(actual, StringComparer.Ordinal))
                throw new CheckConfigurationException(
                    $"Scope project {unit.Project} does not account for every configured test suite.");
        }
    }

    private static CheckConfiguration? LoadExplicitCheckConfiguration(string root, byte[]? bytes)
    {
        if (bytes is null) return null;
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(bytes);
            if (!document.RootElement.EnumerateObject().Any(property =>
                    property.Name.Equals("testSuites", StringComparison.OrdinalIgnoreCase))) return null;
            return CheckConfiguration.Load(root, bytes);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or
                                   System.Text.Json.JsonException)
        {
            throw new CheckConfigurationException($"Strict configuration validation failed: {ex.Message}", ex);
        }
    }

    private static IReadOnlySet<string> ReportExclusions(string root, string reportPath)
    {
        var excluded = new HashSet<string>(OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var output in new[] { reportPath, ReportWriter.LockPath(reportPath) })
        {
            var relative = Path.GetRelativePath(root, Path.GetFullPath(output));
            if (relative is not ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar,
                    StringComparison.Ordinal) && !Path.IsPathRooted(relative))
                excluded.Add(relative.Replace('\\', '/'));
        }
        return excluded;
    }

    private static string? FindGitMarkerRoot(string input)
    {
        var directory = File.Exists(input) ? Path.GetDirectoryName(Path.GetFullPath(input)) :
            Path.GetDirectoryName(Path.GetFullPath(input));
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory, ".git")) || Directory.Exists(Path.Combine(directory, ".git")))
                return directory;
            directory = Directory.GetParent(directory)?.FullName;
        }
        return null;
    }

    private static string ResolveExplicitRoot(IReadOnlyList<string> inputs)
    {
        var first = Path.GetDirectoryName(inputs[0])!;
        var current = Path.GetFullPath(Environment.CurrentDirectory);
        if (inputs.All(input => IsUnder(current, input))) return current;
        var common = new DirectoryInfo(first);
        while (common.Parent is not null && inputs.Any(input => !IsUnder(common.FullName, input)))
            common = common.Parent;
        return common.FullName;

        static bool IsUnder(string root, string path)
        {
            var relative = Path.GetRelativePath(root, Path.GetFullPath(path));
            return relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar,
                StringComparison.Ordinal) && !Path.IsPathRooted(relative);
        }
    }

    private static EvaluationReason SnapshotFailure(SnapshotCaptureException exception) => exception switch
    {
        SnapshotCleanupException => new("SNAPSHOT_CLEANUP_FAILED",
            "The immutable capture could not be cleaned up safely."),
        SnapshotEnvironmentException => new("SNAPSHOT_ENVIRONMENT",
            "Immutable capture could not create or write its private staging environment."),
        ExecutionBoundaryIntegrityException => new("EXECUTION_BOUNDARY_INTEGRITY",
            "Execution escaped or changed a frozen private workspace or dependency boundary."),
        SnapshotDivergedException => new("SNAPSHOT_DIVERGED", "Inputs diverged during or after immutable capture."),
        SnapshotLimitException => new("SNAPSHOT_LIMIT", "Immutable capture exceeded a configured safety bound."),
        _ => new("SNAPSHOT_REFUSED", "Immutable capture refused an unsupported or unsafe input.")
    };

    private static string SnapshotEvidenceKind(SnapshotCaptureException exception) => exception switch
    {
        SnapshotCleanupException => "SNAPSHOT_CLEANUP",
        SnapshotEnvironmentException => "SNAPSHOT_ENVIRONMENT",
        ExecutionBoundaryIntegrityException => "EXECUTION_BOUNDARY_INTEGRITY",
        SnapshotDivergedException => "SNAPSHOT_DIVERGENCE",
        SnapshotLimitException => "SNAPSHOT_LIMIT",
        _ => "SNAPSHOT_REFUSAL"
    };

    internal static IReadOnlyList<EvaluationReason> PreservedFailureReasons(
        EvaluationReason? currentReason, IReadOnlyList<EvaluationReason> existingReasons,
        Exception latestFailure)
    {
        ArgumentNullException.ThrowIfNull(existingReasons);
        ArgumentNullException.ThrowIfNull(latestFailure);
        return existingReasons.Concat(currentReason is null ? [] : [currentReason]).Where(IsIntegrityFailure)
            .Concat(FailureChain(latestFailure).Skip(1).SelectMany(FailureReasons))
            .Distinct().ToArray();
    }

    internal static IReadOnlyList<EvaluationEvidence> PreservedFailureEvidence(Exception failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        return FailureChain(failure).SelectMany(FailureEvidence).Distinct().ToArray();
    }

    private static void AddSnapshotFailureEvidence(List<EvaluationEvidence> evidence,
        SnapshotCaptureException exception)
    {
        foreach (var item in PreservedFailureEvidence(exception))
            if (!evidence.Contains(item)) evidence.Add(item);
    }

    private static IEnumerable<Exception> FailureChain(Exception exception)
    {
        Exception? current = exception;
        for (var depth = 0; current is not null && depth < 16; depth++)
        {
            yield return current;
            current = current is SnapshotCleanupException { OriginalFailure: { } original } ? original : null;
        }
    }

    private static IEnumerable<EvaluationReason> FailureReasons(Exception failure) => failure switch
    {
        SnapshotCaptureException snapshot => [SnapshotFailure(snapshot)],
        StrictExecutionRefusalException refusal => refusal.Reasons,
        _ => []
    };

    private static IEnumerable<EvaluationEvidence> FailureEvidence(Exception failure) => failure switch
    {
        SnapshotCaptureException snapshot =>
            [new EvaluationEvidence(SnapshotEvidenceKind(snapshot), Bound(snapshot.Message))],
        StrictExecutionRefusalException refusal =>
            [new EvaluationEvidence("STRICT_EXECUTION_REFUSAL", refusal.Reason.Message,
                EvaluationEvidence.BoundDiagnostics(refusal.Reasons.Select(item =>
                    $"{item.Code}: {item.Message}")))],
        _ => []
    };

    private static bool IsIntegrityFailure(EvaluationReason reason) => reason.Code is
        "EXECUTION_BOUNDARY_INTEGRITY" or "SNAPSHOT_DIVERGED" or "SNAPSHOT_LIMIT";

    private static void InvalidateSnapshotExecutionEvidence(List<EvaluationEvidence> evidence,
        ref BaselineStatus baseline, ref IReadOnlyList<SuiteEvidence> reportSuites)
    {
        baseline = BaselineStatus.Unknown;
        reportSuites = [];
        evidence.RemoveAll(item => item.Kind is "INPUT_SNAPSHOT" or "SCOPE_PLAN" or
            "MUTATION_PLAN" or "DEPENDENCY_INPUT" or "EXACT_ID_REQUEST");
    }

    private static string Bound(string value) =>
        EvaluationTextBounds.Prefix(value, EvaluationReason.MaxMessageLength);
    private static string BoundDiagnostic(string value) =>
        EvaluationTextBounds.Prefix(value, EvaluationEvidence.MaxDiagnosticLength);

    private static bool IsFatal(Exception exception) =>
        exception is OutOfMemoryException or StackOverflowException or AccessViolationException;

    private static EvaluationReport WithStatePublicationFailure(EvaluationReport report, EvaluationFacts facts,
        IReadOnlyList<EvaluationEvidence> evidence, Exception exception)
    {
        var stateFailure = new EvaluationReason("SIDECAR_WRITE_FAILED",
            "Discovery state could not be published safely.");
        var failedConditions = report.IncompleteConditions.Concat([stateFailure]).Distinct().ToArray();
        var failedFacts = facts with { IncompleteConditions = failedConditions };
        var failedDecision = EvaluationReducer.Reduce(failedFacts);
        var failedEvidence = failedDecision.Evidence.Concat(evidence)
            .Concat([new EvaluationEvidence("SIDECAR_PUBLICATION_FAILURE", Bound(exception.Message))]).ToArray();
        return report with
        {
            IncompleteConditions = failedConditions,
            Reasons = failedDecision.Reasons,
            Evidence = failedEvidence,
            Counts = failedDecision.Counts,
            Outcome = failedDecision.Outcome,
            ExitCode = failedDecision.ExitCode
        };
    }

    private static EvaluationReport WithFingerprintFailure(EvaluationReport report, EvaluationFacts facts,
        IReadOnlyList<EvaluationEvidence> evidence, Exception exception)
    {
        var condition = new EvaluationReason("FINGERPRINT_UNAVAILABLE",
            "Complete evaluation provenance could not be established.");
        var conditions = report.IncompleteConditions.Append(condition).Distinct().ToArray();
        var decision = EvaluationReducer.Reduce(facts with { IncompleteConditions = conditions });
        return report with
        {
            IncompleteConditions = conditions,
            Reasons = decision.Reasons,
            Evidence = decision.Evidence.Concat(evidence)
                .Append(new EvaluationEvidence("FINGERPRINT_FAILURE", Bound(exception.Message))).ToArray(),
            Counts = decision.Counts,
            Outcome = decision.Outcome,
            ExitCode = decision.ExitCode
        };
    }

    internal static EvaluationFingerprintMaterial BuildEvaluationFingerprintMaterial(
        InputSnapshot snapshot, string snapshotId, ScopePlan scopePlan, EvaluationPolicy policy,
        string semanticContextIdentity, string sdkVersion,
        FrozenExecutionEnvironment? environment = null,
        IReadOnlyList<SuiteExecution>? suites = null,
        IReadOnlyDictionary<string, SuiteBaselineExecution>? baselines = null,
        IReadOnlyList<MutationCandidate>? candidates = null)
    {
        var inputs = snapshot.Files.Select(file => new FingerprintInput(Classify(file.RelativePath),
            file.RelativePath, file.Length, file.Sha256, file.Exists, file.IsTracked)).ToList();
        if (environment is not null)
            inputs.Add(new("dependency", ".mutate4csharp/frozen-dependencies", 0,
                environment.Fingerprint, true, false));
        var scope = ReportWriter.SerializeCanonicalScope(scopePlan);
        var runtimeIdentity = $"framework={RuntimeInformation.FrameworkDescription};" +
            $"rid={RuntimeInformation.RuntimeIdentifier};os={RuntimeInformation.OSDescription};" +
            $"osArch={RuntimeInformation.OSArchitecture};processArch={RuntimeInformation.ProcessArchitecture}";
        var runnerIdentity = BuildRunnerIdentity(suites, baselines, candidates);
        var provenanceComplete = environment is not null && suites is { Count: > 0 } &&
            baselines is not null && candidates is not null &&
            suites.All(suite => baselines.TryGetValue(suite.Identity, out var execution) &&
                execution.Result.Disposition == SuiteRunDisposition.Passed &&
                execution.Result.HealthyControl && execution.Result.CoverageOwner is not null &&
                execution.Result.CoverageMap is { HasMalformedEvidence: false } &&
                EvaluationFingerprint.IsSha256(execution.Result.CoverageSha256) && execution.Result.CoverageLength > 0 &&
                execution.Result.AccountedMembers.Count == suite.ExpectedMembers.Count &&
                execution.Result.AccountedMembers.Distinct(StringComparer.OrdinalIgnoreCase).Count() ==
                    execution.Result.AccountedMembers.Count &&
                execution.Result.AccountedMembers.Order(StringComparer.OrdinalIgnoreCase)
                    .SequenceEqual(suite.ExpectedMembers.Order(StringComparer.OrdinalIgnoreCase),
                        StringComparer.OrdinalIgnoreCase));
        return new EvaluationFingerprintMaterial(inputs, snapshotId, scope,
            $"strict-configuration-contract-v2;semantic={semanticContextIdentity}",
            EvaluationFingerprint.ToolIdentity(typeof(EvaluationCoordinator).Assembly), "operator-registry-v1",
            $"dotnet-sdk={sdkVersion};dependencies={environment?.Fingerprint ?? "not-prepared"}",
            runtimeIdentity, runnerIdentity,
            policy, ProvenanceComplete: provenanceComplete);

        static string BuildRunnerIdentity(IReadOnlyList<SuiteExecution>? suites,
            IReadOnlyDictionary<string, SuiteBaselineExecution>? baselines,
            IReadOnlyList<MutationCandidate>? candidates)
        {
            if (suites is null || baselines is null || candidates is null) return "runner:not-executed";
            var suiteDigest = MutationIdentity.ComputeDigest("runner-suites",
                suites.OrderBy(item => item.Identity, StringComparer.Ordinal)
                    .Select((item, index) => ($"suite-{index:D8}", item.Identity)).ToArray());
            var coverageComponents = suites.OrderBy(item => item.Identity, StringComparer.Ordinal)
                .SelectMany((suite, index) =>
                {
                    baselines.TryGetValue(suite.Identity, out var execution);
                    return new (string Label, string Value)[]
                    {
                        ($"suite-{index:D8}-identity", suite.Identity),
                        ($"suite-{index:D8}-coverage", execution?.Result.CoverageMap?.CanonicalIdentity() ?? "missing"),
                        ($"suite-{index:D8}-members", execution is null ? "missing" :
                            string.Join(',', execution.Result.AccountedMembers.Order(StringComparer.OrdinalIgnoreCase)))
                    };
                }).ToArray();
            var coverageDigest = MutationIdentity.ComputeDigest("coverage-provenance", coverageComponents);
            var planDigest = MutationIdentity.ComputeDigest("mutation-plan-inputs",
                candidates.OrderBy(item => item.EvaluationUnitId, StringComparer.Ordinal)
                    .Select((item, index) => ($"candidate-{index:D8}", item.EvaluationUnitId)).ToArray());
            return $"runner=strict-vstest-v1;suites=sha256:{suiteDigest};" +
                   $"collector=coverlet-opencover-v1;coverage=sha256:{coverageDigest};plan=sha256:{planDigest}";
        }

        static string Classify(string path)
        {
            var parts = path.Split('/');
            if (parts.Any(part => part.Equals("test", StringComparison.OrdinalIgnoreCase) ||
                                  part.Equals("tests", StringComparison.OrdinalIgnoreCase))) return "test";
            var name = Path.GetFileName(path);
            if (name.Equals("mutate4csharp.json", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("global.json", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("NuGet.Config", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("Directory.Build.props", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("Directory.Build.targets", StringComparison.OrdinalIgnoreCase)) return "configuration";
            if (name.Equals("packages.lock.json", StringComparison.OrdinalIgnoreCase) ||
                name.EndsWith(".deps.json", StringComparison.OrdinalIgnoreCase)) return "dependency";
            if (path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase)) return "project";
            if (path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) return "source";
            return "asset";
        }
    }

    private static async Task<string> ResolveSdkVersionAsync(InputSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        SnapshotCapture.RejectInheritedGlobalJson(snapshot.CaptureRoot);
        var dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
        var sdk = await ProcessTree.RunAsync(dotnet, ["--version"], snapshot.CaptureRoot,
            TimeSpan.FromSeconds(30), cancellationToken);
        var sdkVersion = sdk.StandardOutput.Trim();
        if (sdk.TimedOut || sdk.ExitCode != 0 || sdkVersion.Length == 0 || sdkVersion.Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character is not ('.' or '-' or '+')))
            throw new EvaluationContractException("The resolved .NET SDK identity could not be established.");
        return sdkVersion;
    }
}
