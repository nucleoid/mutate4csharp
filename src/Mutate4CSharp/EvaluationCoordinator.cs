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

    public EvaluationCoordinator(SnapshotCaptureOptions? captureOptions = null) =>
        _captureOptions = captureOptions ?? SnapshotCaptureOptions.Default;

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
        Exception? fingerprintMaterialFailure = null;
        CheckConfiguration? checkConfiguration = null;
        EvaluationReason reason;
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
                    evidence.Add(new("CHECK_CONFIGURATION",
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
                    evidence.Add(new("CHECK_CONFIGURATION",
                        $"Validated configuration v{checkConfiguration.SchemaVersion}: " +
                        $"{checkConfiguration.Projects.Count} project(s), " +
                        $"{checkConfiguration.ExecutionSuites.Count} distinct suite execution(s)."));
                }
                evidence.Add(new("EXPLICIT_INPUT_CAPTURE",
                    $"Captured {scopePlan.Files.Count + scopePlan.Exclusions.Count} explicit non-Git input(s)."));
            }
            reason = options.Plan
                ? new("PLAN_ONLY", "Plan mode produces scope audit data and runs no tests.")
                : exactIdRequest
                    ? new("EXACT_ID_RERUN_UNAVAILABLE",
                        "Exact-ID reruns are diagnostic-only until mutation enumeration is available.")
                : new("ENUMERATION_NOT_IMPLEMENTED",
                    "Scope planning completed, but mutation enumeration/orchestration belongs to later issues.");
            if (exactIdRequest)
                evidence.Add(new("EXACT_ID_REQUEST",
                    $"Refused diagnostic exact-ID execution for {exactMutationIds.Count} requested mutation(s).",
                    exactMutationIds.Take(100).Concat(["planFingerprint=" + options.PlanFingerprint]).ToArray()));
            evidence.Add(new("SCOPE_PLAN",
                $"Scope plan v{scopePlan.SchemaVersion}: {scopePlan.Files.Count} file(s), " +
                $"{scopePlan.ProjectUnits.Count} project unit(s), {scopePlan.Exclusions.Count} exclusion(s).",
                scopePlan.Reasons.Select(item => BoundDiagnostic($"{item.Code}: {item.Message}"))
                    .Take(20).ToArray()));
        }
        catch (SnapshotCaptureException ex)
        {
            reason = SnapshotFailure(ex);
            evidence.Add(new(SnapshotEvidenceKind(ex), Bound(ex.Message)));
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

        try
        {
            if (snapshot is not null)
            {
                try { await snapshot.ValidateOriginalAsync(cancellationToken); }
                catch (SnapshotCaptureException ex)
                {
                    reason = SnapshotFailure(ex);
                    evidence.RemoveAll(item => item.Kind is "INPUT_SNAPSHOT" or "SCOPE_PLAN");
                    evidence.Add(new(SnapshotEvidenceKind(ex), Bound(ex.Message)));
                    snapshotId = null;
                    scopePlan = ScopePlan.Empty(selection.Kind, ".", null,
                        new EvaluationReason("SCOPE_UNAVAILABLE", "Scope plan was invalidated by snapshot divergence."));
                }
                catch (OperationCanceledException)
                {
                    reason = new("SNAPSHOT_CANCELLED", "Immutable capture validation was cancelled.");
                    evidence.RemoveAll(item => item.Kind is "INPUT_SNAPSHOT" or "SCOPE_PLAN");
                    evidence.Add(new("SNAPSHOT_CANCELLATION",
                        "Cancellation was observed before report publication."));
                    snapshotId = null;
                    scopePlan = ScopePlan.Empty(selection.Kind, ".", null,
                        new EvaluationReason("SCOPE_UNAVAILABLE", "Scope plan was invalidated by snapshot cancellation."));
                }
                catch (Exception ex)
                {
                    reason = new("SNAPSHOT_VALIDATION_FAILED",
                        "Immutable capture failed closed during native or runtime validation.");
                    evidence.RemoveAll(item => item.Kind is "INPUT_SNAPSHOT" or "SCOPE_PLAN");
                    evidence.Add(new("SNAPSHOT_VALIDATION_FAILURE",
                        Bound($"{ex.GetType().Name}: {ex.Message}")));
                    snapshotId = null;
                    scopePlan = ScopePlan.Empty(selection.Kind, ".", null,
                        new EvaluationReason("SCOPE_UNAVAILABLE", "Scope plan was invalidated by snapshot validation failure."));
                }
            }
            if (snapshot is not null)
            {
                if (!options.NoState && !options.Plan && !exactIdRequest && snapshotId is not null)
                {
                    try
                    {
                        fingerprintMaterial = await BuildEvaluationFingerprintMaterialAsync(
                            snapshot, snapshotId, scopePlan, checkConfiguration?.Policy ?? EvaluationReport.DefaultPolicy,
                            cancellationToken);
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
                    reason = new("SNAPSHOT_CLEANUP_FAILED",
                        "The immutable capture could not be cleaned up safely.");
                    evidence.RemoveAll(item => item.Kind is "INPUT_SNAPSHOT" or "SCOPE_PLAN");
                    evidence.Add(new("SNAPSHOT_CLEANUP", Bound(ex.Message)));
                    snapshotId = null;
                    scopePlan = ScopePlan.Empty(selection.Kind, ".", null,
                        new EvaluationReason("SCOPE_UNAVAILABLE", "Scope plan was invalidated by snapshot cleanup failure."));
                }
                snapshot = null;
            }
            var incompleteConditions = new List<EvaluationReason> { reason };
            incompleteConditions.AddRange(scopePlan.Reasons.Concat(scopePlan.Files.SelectMany(file => file.Reasons))
                .Where(item => ScopePlanner.IsBlockingCode(item.Code)));
            var facts = new EvaluationFacts(BaselineStatus.Unknown, null, [], false,
                incompleteConditions.Distinct().ToArray());
            var decision = EvaluationReducer.Reduce(facts);
            var reportEvidence = decision.Evidence.Concat(evidence).ToArray();
            var report = new EvaluationReport("1", runId, DateTimeOffset.UtcNow,
                options.Plan ? "plan" : "check", selection, scopePlan,
                checkConfiguration?.Policy ?? EvaluationReport.DefaultPolicy,
                facts.Baseline, [], facts.Units, decision.Counts, facts.IncompleteConditions,
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
                report = WithStatePublicationFailure(report, facts, evidence, fingerprintMaterialFailure);
            ReportWriter.Write(reportPath, report, options.Inputs);
            if (fingerprintMaterialFailure is OperationCanceledException cancellation)
                throw cancellation;
            if (fingerprintMaterialFailure is null && sidecarStore is not null && fingerprintMaterial is not null &&
                snapshotId is not null)
            {
                try
                {
                    var evaluationFingerprint = EvaluationFingerprint.Compute(fingerprintMaterial);
                    var reportBytes = ReportWriter.Serialize(report);
                    var discovery = new DiscoverySidecar("1", SidecarRecordKind.Discovery, runId,
                        report.GeneratedAtUtc, evaluationFingerprint, snapshotId, report.Outcome,
                        scopePlan.IsComplete,
                        scopePlan.Exclusions.Select(item => $"{item.Path}:{item.ReasonCode}").ToArray(),
                        Convert.ToHexString(SHA256.HashData(reportBytes)).ToLowerInvariant(),
                        reportBytes.LongLength);
                    sidecarStore.PublishDiscovery(discovery);
                }
                catch (OperationCanceledException ex)
                {
                    report = WithStatePublicationFailure(report, facts, evidence, ex);
                    ReportWriter.Write(reportPath, report, options.Inputs);
                    throw;
                }
                catch (Exception ex) when (!IsFatal(ex))
                {
                    report = WithStatePublicationFailure(report, facts, evidence, ex);
                    ReportWriter.Write(reportPath, report, options.Inputs);
                }
            }
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
        SnapshotDivergedException => new("SNAPSHOT_DIVERGED", "Inputs diverged during or after immutable capture."),
        SnapshotLimitException => new("SNAPSHOT_LIMIT", "Immutable capture exceeded a configured safety bound."),
        _ => new("SNAPSHOT_REFUSED", "Immutable capture refused an unsupported or unsafe input.")
    };

    private static string SnapshotEvidenceKind(SnapshotCaptureException exception) => exception switch
    {
        SnapshotCleanupException => "SNAPSHOT_CLEANUP",
        SnapshotEnvironmentException => "SNAPSHOT_ENVIRONMENT",
        _ => "SNAPSHOT_REFUSAL"
    };

    private static string Bound(string value) => value.Length <= 1024 ? value : value[..1024];
    private static string BoundDiagnostic(string value) => value.Length <= 512 ? value : value[..512];

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

    private static async Task<EvaluationFingerprintMaterial> BuildEvaluationFingerprintMaterialAsync(
        InputSnapshot snapshot, string snapshotId, ScopePlan scopePlan, EvaluationPolicy policy,
        CancellationToken cancellationToken)
    {
        var inputs = snapshot.Files.Select(file => new FingerprintInput(Classify(file.RelativePath),
            file.RelativePath, file.Length, file.Sha256, file.Exists, file.IsTracked)).ToArray();
        var scope = ReportWriter.SerializeCanonicalScope(scopePlan);
        SnapshotCapture.RejectInheritedGlobalJson(snapshot.CaptureRoot);
        var dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
        var sdk = await ProcessTree.RunAsync(dotnet, ["--version"], snapshot.CaptureRoot,
            TimeSpan.FromSeconds(30), cancellationToken);
        var sdkVersion = sdk.StandardOutput.Trim();
        if (sdk.TimedOut || sdk.ExitCode != 0 || sdkVersion.Length == 0 || sdkVersion.Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character is not ('.' or '-' or '+')))
            throw new EvaluationContractException("The resolved .NET SDK identity could not be established.");
        var runtimeIdentity = $"framework={RuntimeInformation.FrameworkDescription};" +
            $"rid={RuntimeInformation.RuntimeIdentifier};os={RuntimeInformation.OSDescription};" +
            $"osArch={RuntimeInformation.OSArchitecture};processArch={RuntimeInformation.ProcessArchitecture}";
        return new EvaluationFingerprintMaterial(inputs, snapshotId, scope, "strict-configuration-contract-v1",
            EvaluationFingerprint.ToolIdentity(typeof(EvaluationCoordinator).Assembly), "operator-registry-v1",
            $"dotnet-sdk={sdkVersion}", runtimeIdentity, "runner:not-executed",
            policy, ProvenanceComplete: false);

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
}
