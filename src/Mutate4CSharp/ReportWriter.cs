using System.Text.Encodings.Web;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Mutate4CSharp;

internal static class ReportWriter
{
    internal const string SchemaVersion = "2";
    private static readonly Regex CodePattern = new("^[A-Z][A-Z0-9_]*$",
        RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly Regex ConfigurationIdentifier = new("^[A-Za-z0-9][A-Za-z0-9._-]{0,127}\\z",
        RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.Default,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseUpper) }
    };

    public static byte[] Serialize(EvaluationReport report)
    {
        Validate(report);
        return JsonSerializer.SerializeToUtf8Bytes(report, JsonOptions);
    }

    public static string SerializeCanonicalScope(ScopePlan scopePlan) =>
        JsonSerializer.Serialize(scopePlan, JsonOptions);

    internal static EvaluationEvidence ConfigurationSuiteEvidence(IEnumerable<string> suiteIds,
        string summary = "Validated strict configuration suite identities.")
    {
        var ids = suiteIds.OrderBy(value => value, StringComparer.Ordinal).ToArray();
        if (ids.Length == 0 || ids.Any(string.IsNullOrWhiteSpace) ||
            ids.Distinct(StringComparer.Ordinal).Count() != ids.Length)
            throw new EvaluationContractException("Configured execution identities must be non-empty and unique.");
        return new("CHECK_CONFIGURATION", summary,
            [$"suiteCount={ids.Length}", $"suiteSet={SuiteSetIdentity(ids)}"]);
    }

    public static void Write(string path, EvaluationReport report, IReadOnlyList<string>? inputs = null)
    {
        var fullPath = ResolveSafeDestination(path, inputs ?? []);
        _ = Path.GetDirectoryName(fullPath) ?? throw new IOException("Report path has no parent directory.");
        var lockPath = LockPath(fullPath);
        // The destination directory is the permission boundary; no shared temp root. The same
        // primitive also publishes sidecars, so report and state writes cannot drift semantically.
        AtomicOwnedFile.Write(fullPath, lockPath, Serialize(report), ValidateExistingDestination,
            replaceExisting: true);
    }

    public static string ResolveSafeDestination(string path, IReadOnlyList<string> inputs)
    {
        var fullPath = Path.GetFullPath(path);
        if (!string.Equals(Path.GetExtension(fullPath), ".json", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("--report must use a .json destination.");
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        foreach (var input in inputs)
        {
            if (comparer.Equals(fullPath, Path.GetFullPath(input)))
                throw new ArgumentException("--report cannot overwrite an evaluation input.");
        }
        return fullPath;
    }

    internal static void ValidateDestination(string path, IReadOnlyList<string> inputs) =>
        _ = ValidateExistingDestination(ResolveSafeDestination(path, inputs));

    internal static string LockPath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        return Path.Combine(Path.GetDirectoryName(fullPath)!, "." + Path.GetFileName(fullPath) + ".lock");
    }

    internal static void InvalidateCurrentRun(string path, string runId)
    {
        AtomicOwnedFile.Delete(path, LockPath(path), existing =>
        {
            var hash = ValidateExistingDestination(existing);
            if (hash is null) return null;
            using var document = JsonDocument.Parse(File.ReadAllBytes(existing));
            if (document.RootElement.GetProperty("runId").GetString() != runId)
                throw new IOException("Report invalidation cannot remove another run's artifact.");
            return hash;
        });
    }

    private static byte[]? ValidateExistingDestination(string path)
    {
        if (!File.Exists(path))
        {
            if (Directory.Exists(path)) throw new ArgumentException("--report cannot target a directory.");
            return null;
        }
        var attributes = File.GetAttributes(path);
        if ((attributes & (FileAttributes.Directory | FileAttributes.Device | FileAttributes.ReparsePoint)) != 0)
            throw new ArgumentException("--report can overwrite only a regular report file.");
        if (new FileInfo(path).Length > 8 * 1024 * 1024)
            throw new ArgumentException("--report will not overwrite a non-report JSON file.");
        try
        {
            var bytes = File.ReadAllBytes(path);
            using var document = JsonDocument.Parse(bytes);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("schemaVersion", out var version) ||
                version.ValueKind != JsonValueKind.String || version.GetString() is not ("1" or "2") ||
                !root.TryGetProperty("runId", out var runId) || runId.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(runId.GetString()) ||
                !root.TryGetProperty("mode", out var mode) || mode.ValueKind != JsonValueKind.String ||
                mode.GetString() is not ("check" or "plan") ||
                !root.TryGetProperty("outcome", out var outcome) || outcome.ValueKind != JsonValueKind.String ||
                outcome.GetString() is not ("PASS" or "FAIL" or "INCOMPLETE" or "NOT_APPLICABLE"))
                throw new ArgumentException("--report will not overwrite a non-report JSON file.");
            return SHA256.HashData(bytes);
        }
        catch (JsonException ex)
        {
            throw new ArgumentException("--report will not overwrite a non-report JSON file.", ex);
        }
    }

    private static void Validate(EvaluationReport report)
    {
        if (report.SchemaVersion != SchemaVersion) throw new EvaluationContractException("Unsupported report schema version.");
        if (string.IsNullOrWhiteSpace(report.RunId)) throw new EvaluationContractException("A report run ID is required.");
        if (report.Evidence.Count == 0) throw new EvaluationContractException("Every report outcome requires evidence.");
        ExecutionPolicy.ValidateContract(report.Policy);
        ValidateScopePlan(report.ScopePlan, report.Selection);
        var expectedCounts = EvaluationCounts.From(report.Counts.Enumerated, report.Units);
        if (expectedCounts != report.Counts)
            throw new EvaluationContractException("Report counts do not match the unit ledger.");
        if (report.Counts.Enumerated is int enumerated && enumerated != report.Counts.Selected)
            throw new EvaluationContractException("Every enumerated unit must appear in the ledger.");
        if (report.Counts.Selected != report.Counts.Executed + report.Counts.FreshUncovered + report.Counts.Omitted)
            throw new EvaluationContractException("Report accounting does not reconcile.");
        if (report.Counts.CompileInvalid > report.Counts.Executed)
            throw new EvaluationContractException("Compile-invalid units must be a subset of executed units.");
        var ids = report.Units.Select(unit => unit.EvaluationUnitId).ToArray();
        if (ids.Distinct(StringComparer.Ordinal).Count() != ids.Length)
            throw new EvaluationContractException("Report contains duplicate evaluation-unit IDs.");
        if (report.Units.Any(unit => unit.Disposition == UnitDisposition.Pending))
            throw new EvaluationContractException("Final reports cannot contain pending units.");
        if (report.DiagnosticPartial && report.IncompleteConditions.All(condition =>
                condition.Code != MutationSelection.TargetedDiagnosticCode))
            throw new EvaluationContractException(
                "Diagnostic partial reports require their intrinsic incomplete condition.");
        if (report.Units.Any(unit => unit.DiagnosticPartial != report.DiagnosticPartial))
            throw new EvaluationContractException(
                "Report-level and unit-level diagnostic state must agree.");
        if (report.Units.Any(unit => !MutationIdentity.IsMutationId(unit.UnitId) ||
                                     !EvaluationUnitIdentity.IsEvaluationUnitId(unit.EvaluationUnitId)))
            throw new EvaluationContractException("Reports require canonical mutation and evaluation-unit IDs.");
        if (report.Units.Any(unit => unit.Evidence is null || unit.Evidence.Count is 0 or > StabilityEvidence.MaxUnitEvidence ||
                                     unit.Evidence.Any(item => item is null)))
            throw new EvaluationContractException("Every report unit requires bounded non-null evidence.");
        foreach (var reason in report.Reasons)
            ValidateText(reason.Code, reason.Message, "reason");
        foreach (var condition in report.IncompleteConditions)
            ValidateText(condition.Code, condition.Message, "incomplete condition");
        foreach (var item in report.Evidence.Concat(report.Units.SelectMany(unit => unit.Evidence))
                     .Concat(report.Suites.SelectMany(suite => suite.Evidence)))
        {
            ValidateText(item.Kind, item.Summary, "evidence");
            if (item.Diagnostics is { Count: > EvaluationEvidence.MaxDiagnostics } ||
                item.Diagnostics?.Any(value => value.Length > EvaluationEvidence.MaxDiagnosticLength) == true)
                throw new EvaluationContractException("Evidence diagnostics exceed schema bounds.");
        }
        if (report.Suites.Any(suite => string.IsNullOrWhiteSpace(suite.SuiteId) || suite.Evidence.Count == 0))
            throw new EvaluationContractException("Every suite requires an ID and evidence.");
        var suiteIds = report.Suites.Select(suite => suite.SuiteId).ToArray();
        if (suiteIds.Distinct(StringComparer.Ordinal).Count() != suiteIds.Length)
            throw new EvaluationContractException("Report contains duplicate suite execution identities.");
        if (report.Outcome != EvaluationOutcome.Incomplete)
        {
            var configurationEvidence = report.Evidence.Where(item => item.Kind == "CHECK_CONFIGURATION").ToArray();
            if (configurationEvidence.Length != 1 || configurationEvidence[0].Diagnostics is null)
                throw new EvaluationContractException(
                    "Conclusive reports require exactly one configured suite identity set.");
            var configuredCount = SingleDiagnostic(configurationEvidence[0], "suiteCount=");
            var configuredSet = SingleDiagnostic(configurationEvidence[0], "suiteSet=");
            if (!int.TryParse(configuredCount, System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out var count) || count != suiteIds.Length ||
                configuredSet != SuiteSetIdentity(suiteIds))
                throw new EvaluationContractException(
                    "Report suites do not match the configured execution identities exactly.");
            if (report.Baseline == BaselineStatus.Green &&
                report.Suites.Any(suite => suite.Baseline != BaselineStatus.Green))
                throw new EvaluationContractException(
                    "Green report baseline requires every configured suite baseline to be green.");
        }
        if (report.Outcome == EvaluationOutcome.Pass &&
            (report.Counts.Enumerated is not > 0 || report.Counts.Selected == 0 ||
             report.Counts.Executed == 0 || report.Counts.Killed == 0 || report.Suites.Count == 0))
            throw new EvaluationContractException(
                "PASS requires nonzero enumerated, selected, executed, killed, and suite evidence.");
        if (report.Outcome != EvaluationOutcome.Incomplete)
            foreach (var suite in report.Suites) ValidateConclusiveSuite(suite, report);
        var expected = EvaluationReducer.Reduce(new(report.Baseline, report.Counts.Enumerated,
            report.Units, report.Policy.AllowNotApplicable, report.IncompleteConditions));
        if (report.Outcome != expected.Outcome || report.ExitCode != expected.ExitCode)
            throw new EvaluationContractException("Report outcome contradicts its evaluation facts.");
        if (!report.Reasons.SequenceEqual(expected.Reasons))
            throw new EvaluationContractException("Report reasons do not match its evaluation facts.");
        if (report.Mode == "plan" && report.Outcome == EvaluationOutcome.Pass)
            throw new EvaluationContractException("Plan reports cannot represent reusable success.");
    }

    private static string SingleDiagnostic(EvaluationEvidence evidence, string prefix)
    {
        var values = evidence.Diagnostics!.Where(value => value.StartsWith(prefix, StringComparison.Ordinal))
            .Select(value => value[prefix.Length..]).ToArray();
        if (values.Length != 1)
            throw new EvaluationContractException("Configured suite identity evidence is incomplete or ambiguous.");
        return values[0];
    }

    private static void ValidateConclusiveSuite(SuiteEvidence suite, EvaluationReport report)
    {
        var baselines = suite.Evidence.Where(item => item.Kind == "SUITE_BASELINE").ToArray();
        var accounting = suite.Accounting;
        if (baselines.Length != 1 || suite.Evidence.Count != 1 || accounting is null ||
            suite.Baseline != BaselineStatus.Green || accounting.RunId != report.RunId || !accounting.CollectedFresh)
            throw new EvaluationContractException(
                "Conclusive suite evidence requires one bound fresh baseline record.");
        var snapshots = report.Evidence.Where(item => item.Kind == "INPUT_SNAPSHOT")
            .SelectMany(item => item.Diagnostics ?? []).Where(item => item.StartsWith("captureId=", StringComparison.Ordinal))
            .Select(item => item["captureId=".Length..]).ToArray();
        if (snapshots.Length != 1 || snapshots[0] != accounting.SnapshotId ||
            !EvaluationFingerprint.IsSha256(accounting.SnapshotId) ||
            !EvaluationFingerprint.IsSha256(accounting.CoverageReportSha256) || accounting.CoverageReportLength <= 0 ||
            !EvaluationFingerprint.IsSha256(accounting.CoverageIdentity) ||
            accounting.PathMap != "baseline-clone-to-snapshot-v1" ||
            accounting.Configuration.Runner != "vstest" || accounting.Configuration.Framework != "net10.0" ||
            !ConfigurationIdentifier.IsMatch(accounting.Configuration.Id) ||
            !ConfigurationIdentifier.IsMatch(accounting.Configuration.Configuration) ||
            accounting.Configuration.Path.Contains(':') ||
            accounting.Configuration.Path.Replace('\\', '/').Split('/').Any(item => item is "" or "." or "..") ||
            Path.IsPathRooted(accounting.Configuration.Path) ||
            CheckConfiguration.SuiteIdentity(accounting.Configuration) != suite.SuiteId)
            throw new EvaluationContractException(
                "Conclusive suite evidence requires exact configured identity, snapshot and coverage provenance.");
        var accounted = accounting.AccountedMembers;
        var expected = accounting.Configuration.ExpectedMembers;
        if (!ValidMembers(accounted) || !ValidMembers(expected) ||
            !accounted.Order(StringComparer.OrdinalIgnoreCase)
                .SequenceEqual(expected.Order(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase))
            throw new EvaluationContractException(
                "Conclusive suites must account for every configured test member exactly.");

        static bool ValidMembers(IReadOnlyList<string> members) => members.Count is > 0 and <= 256 &&
            members.Distinct(StringComparer.OrdinalIgnoreCase).Count() == members.Count &&
            members.All(member => !string.IsNullOrWhiteSpace(member) && member.Length <= 256 && member == member.Trim() &&
                !member.Contains('/') && !member.Contains('\\') && member is not ("." or ".."));
    }

    private static string SuiteSetIdentity(IEnumerable<string> suiteIds)
    {
        var ids = suiteIds.OrderBy(value => value, StringComparer.Ordinal).ToArray();
        var components = ids.Select((value, index) => ($"suite-{index:D8}", value)).ToArray();
        return "sha256:" + MutationIdentity.ComputeDigest("configured-suite-set", components);
    }

    private static void ValidateText(string code, string message, string kind)
    {
        if (!CodePattern.IsMatch(code)) throw new EvaluationContractException($"Invalid {kind} code.");
        if (string.IsNullOrWhiteSpace(message) || message.Length > 1024)
            throw new EvaluationContractException($"Invalid {kind} message length.");
    }

    private static void ValidateScopePlan(ScopePlan plan, EvaluationSelection selection)
    {
        if (plan.SchemaVersion != "1") throw new EvaluationContractException("Unsupported scope plan version.");
        if (plan.SelectionKind != selection.Kind)
            throw new EvaluationContractException("Scope plan selection does not match the report selection.");
        if (plan.Files.Count > ScopePlanner.MaxScopeItems || plan.Exclusions.Count > ScopePlanner.MaxScopeItems ||
            plan.ProjectUnits.Count > ScopePlanner.MaxScopeItems || plan.Reasons.Count > ScopePlanner.MaxScopeItems)
            throw new EvaluationContractException("Scope plan arrays exceed published bounds.");
        ValidateUnique(plan.Files.Select(file => file.Path), "scope file paths");
        ValidateUnique(plan.Exclusions.Select(item => item.Path), "scope exclusion paths");
        ValidateUnique(plan.ProjectUnits.Select(unit => unit.Project), "scope project paths");
        foreach (var reason in plan.Reasons.Concat(plan.Files.SelectMany(file => file.Reasons)))
            ValidateText(reason.Code, reason.Message, "scope reason");
        foreach (var exclusion in plan.Exclusions)
            ValidateText(exclusion.ReasonCode, exclusion.Message, "scope exclusion");
        foreach (var file in plan.Files)
        {
            if (file.Declarations.Count > ScopePlanner.MaxScopeItems ||
                file.Removals.Count > ScopePlanner.MaxScopeItems || file.Reasons.Count > ScopePlanner.MaxScopeItems)
                throw new EvaluationContractException("Scope file arrays exceed published bounds.");
            ValidateRelative(file.Path);
            if (file.BasePath is not null) ValidateRelative(file.BasePath);
            ValidateUnique(file.Declarations.Concat(file.Removals).Select(item => item.Id),
                $"declaration IDs for {file.Path}");
            foreach (var declaration in file.Declarations.Concat(file.Removals))
            {
                if (string.IsNullOrWhiteSpace(declaration.Id) || declaration.Id.Length > 512 ||
                    declaration.StartLine < 1 || declaration.EndLine < declaration.StartLine ||
                    !CodePattern.IsMatch(declaration.Reason))
                    throw new EvaluationContractException("Scope declaration is outside published bounds.");
            }
        }
        foreach (var unit in plan.ProjectUnits)
        {
            if (unit.Tests.Count > ScopePlanner.MaxScopeItems || unit.Inputs.Count > ScopePlanner.MaxScopeItems)
                throw new EvaluationContractException("Scope project arrays exceed published bounds.");
            ValidateRelative(unit.Project);
            if (unit.Tests.Count == 0) throw new EvaluationContractException("Scope project requires a test suite.");
            foreach (var path in unit.Tests.Concat(unit.Inputs)) ValidateRelative(path);
        }

        static void ValidateUnique(IEnumerable<string> values, string kind)
        {
            var items = values.ToArray();
            if (items.Distinct(StringComparer.Ordinal).Count() != items.Length)
                throw new EvaluationContractException($"Scope plan contains duplicate {kind}.");
        }

        static void ValidateRelative(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path) || path.Contains('\\') ||
                path.Split('/').Any(part => part is "" or "." or ".."))
                throw new EvaluationContractException("Scope paths must be normalized relative paths.");
        }
    }
}
