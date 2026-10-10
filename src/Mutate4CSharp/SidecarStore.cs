using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mutate4CSharp;

internal sealed class SidecarStore
{
    public const int MaxRecordBytes = 1024 * 1024;
    private readonly string _stateRoot;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly byte[] Utf8Preamble = Encoding.UTF8.GetPreamble();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.Default,
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseUpper, allowIntegerValues: false) }
    };

    public SidecarStore(string repositoryRoot)
    {
        var root = Path.GetFullPath(repositoryRoot);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException("Sidecar repository root does not exist.");
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Sidecar repository root cannot be a reparse point.");
        _stateRoot = Path.Combine(root, ".mutate4csharp");
    }

    public string PublishDiscovery(DiscoverySidecar record)
    {
        ValidateDiscovery(record);
        return Publish("discovery", DiscoveryKey(record), record);
    }

    internal string ReplaceDiscovery(DiscoverySidecar record)
    {
        ValidateDiscovery(record);
        return Publish("discovery", DiscoveryKey(record), record, replaceExisting: true);
    }

    internal void InvalidateProven(string fingerprint)
    {
        if (!EvaluationFingerprint.IsFingerprint(fingerprint))
            throw new EvaluationContractException("Invalid proven fingerprint for revocation.");
        var directory = Path.Combine(_stateRoot, "proven");
        if (!Directory.Exists(directory))
        {
            if (File.Exists(directory)) throw new IOException("Owned proven directory collides with a file.");
            return;
        }
        EnsureOrdinaryDirectory(directory);
        EnsureOrdinaryDirectory(Path.Combine(_stateRoot, "locks"));
        var key = FingerprintKey(fingerprint);
        AtomicOwnedFile.Delete(StatePath("proven", key), LockPath("proven", key), path =>
        {
            if (InspectExistingOwnedRecord(path) is null) return null;
            var bytes = ReadBoundedRegular(path);
            ValidateRawProvenSchema(bytes);
            var previous = JsonSerializer.Deserialize<ProvenEvaluationSidecar>(bytes, JsonOptions)
                ?? throw new EvaluationContractException("Existing proven state is empty.");
            ValidateProvenEnvelope(previous, fingerprint);
            return SHA256.HashData(bytes);
        });
    }

    public void PrepareForPublication()
    {
        EnsureOrdinaryDirectory(_stateRoot);
        EnsureIgnoreContract();
    }

    public void PrepareDefaultReportRoot()
    {
        PrepareForPublication();
        EnsureOrdinaryDirectory(Path.Combine(_stateRoot, "reports"));
    }

    public string PublishProven(ProvenEvaluationSidecar record, EvaluationReport report,
        EvaluationFingerprintMaterial fingerprintMaterial, MutationSelectionPlan finalizingPlan)
    {
        ValidateProvenPublication(record, report, fingerprintMaterial, finalizingPlan);
        return Publish("proven", FingerprintKey(record.EvaluationFingerprint), record, replaceExisting: true);
    }

    internal static void ValidateProvenPublication(ProvenEvaluationSidecar record, EvaluationReport report,
        EvaluationFingerprintMaterial material, MutationSelectionPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        plan.RequireProvenPublicationEligibility(report, material);
        ValidateProven(record, report, material);
    }

    public SidecarReadResult<ProvenEvaluationSidecar> ReadProvenForInspection(string fingerprint)
    {
        if (!EvaluationFingerprint.IsFingerprint(fingerprint))
            return new(null, "Invalid evaluation fingerprint.");
        var path = StatePath("proven", FingerprintKey(fingerprint));
        try
        {
            var bytes = ReadBoundedRegular(path);
            ValidateRawProvenSchema(bytes);
            var record = JsonSerializer.Deserialize<ProvenEvaluationSidecar>(bytes, JsonOptions)
                ?? throw new JsonException("Empty sidecar record.");
            ValidateProvenEnvelope(record, fingerprint);
            return new(record, null);
        }
        catch (Exception ex) when (!IsFatal(ex))
        {
            return new(null, ex.Message);
        }
    }

    internal string DiscoveryPath(DiscoverySidecar record) => StatePath("discovery", DiscoveryKey(record));
    internal string ProvenPath(string fingerprint) => StatePath("proven", FingerprintKey(fingerprint));
    internal string LockPath(string category, string key) => Path.Combine(_stateRoot, "locks", $"{category}-{key}.lock");

    private string Publish<T>(string category, string key, T record, bool replaceExisting = false)
    {
        PrepareOwnedDirectory(category);
        var path = StatePath(category, key);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(record, JsonOptions);
        if (bytes.Length > MaxRecordBytes) throw new EvaluationContractException("Sidecar record exceeds size bounds.");
        AtomicOwnedFile.Write(path, LockPath(category, key), bytes, existing =>
        {
            var hash = InspectExistingOwnedRecord(existing);
            if (hash is not null && category == "proven")
            {
                if (record is not ProvenEvaluationSidecar proposed)
                    throw new EvaluationContractException("Proven publication requires a proven record.");
                var existingBytes = ReadBoundedRegular(existing);
                ValidateRawProvenSchema(existingBytes);
                var previous = JsonSerializer.Deserialize<ProvenEvaluationSidecar>(existingBytes, JsonOptions)
                    ?? throw new EvaluationContractException("Existing proven state is empty.");
                ValidateProvenEnvelope(previous, proposed.EvaluationFingerprint);
                if (!CryptographicOperations.FixedTimeEquals(hash, SHA256.HashData(existingBytes)))
                    throw new IOException("Existing proven state changed during validation.");
            }
            else if (hash is not null && replaceExisting && record is DiscoverySidecar proposedDiscovery)
            {
                var previousBytes = ReadBoundedRegular(existing);
                var previous = JsonSerializer.Deserialize<DiscoverySidecar>(previousBytes, JsonOptions)
                    ?? throw new EvaluationContractException("Existing discovery state is empty.");
                ValidateDiscovery(previous);
                if (previous.RunId != proposedDiscovery.RunId ||
                    previous.EvaluationFingerprint != proposedDiscovery.EvaluationFingerprint ||
                    !CryptographicOperations.FixedTimeEquals(hash, SHA256.HashData(previousBytes)))
                    throw new IOException("Discovery replacement requires the same owned run and fingerprint.");
            }
            return hash;
        }, replaceExisting);
        return path;
    }

    private byte[]? InspectExistingOwnedRecord(string path)
    {
        if (!File.Exists(path))
        {
            if (Directory.Exists(path)) throw new IOException("Sidecar destination must be a regular file.");
            return null;
        }
        var bytes = ReadBoundedRegular(path);
        try
        {
            using var document = JsonDocument.Parse(bytes);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("schemaVersion", out var schema) ||
                schema.ValueKind != JsonValueKind.String || schema.GetString() != "1" ||
                !document.RootElement.TryGetProperty("recordKind", out var kind) ||
                kind.ValueKind != JsonValueKind.String ||
                kind.GetString() is not ("DISCOVERY" or "PROVEN"))
                throw new IOException("Sidecar destination contains unrecognized state.");
        }
        catch (JsonException ex) { throw new IOException("Sidecar destination contains malformed state.", ex); }
        return SHA256.HashData(bytes);
    }

    private static byte[] ReadBoundedRegular(string path)
    {
        AtomicOwnedFile.RejectSpecialPath(path, "Sidecar");
        if (!File.Exists(path)) throw new FileNotFoundException("Sidecar record does not exist.", path);
        var info = new FileInfo(path);
        if (info.Length is < 1 or > MaxRecordBytes) throw new IOException("Sidecar record exceeds size bounds.");
        var bytes = File.ReadAllBytes(path);
        if (bytes.LongLength != info.Length) throw new IOException("Sidecar record changed while being read.");
        return bytes;
    }

    private void PrepareOwnedDirectory(string category)
    {
        PrepareForPublication();
        EnsureOrdinaryDirectory(Path.Combine(_stateRoot, category));
        EnsureOrdinaryDirectory(Path.Combine(_stateRoot, "locks"));
    }

    private void EnsureIgnoreContract()
    {
        var path = Path.Combine(_stateRoot, ".gitignore");
        AtomicOwnedFile.RejectSpecialPath(path, "Sidecar ignore contract");
        if (!File.Exists(path))
        {
            try
            {
                using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read,
                    4096, FileOptions.WriteThrough);
                stream.Write("*\n"u8);
                stream.Flush(flushToDisk: true);
            }
            catch (IOException) when (File.Exists(path))
            {
                AtomicOwnedFile.RejectSpecialPath(path, "Sidecar ignore contract");
            }
        }
        var info = new FileInfo(path);
        if (info.Length is < 2 or > 64 * 1024)
            throw new IOException("Sidecar ignore contract is missing or outside safe bounds.");
        var bytes = File.ReadAllBytes(path);
        if (bytes.LongLength != info.Length)
            throw new IOException("Sidecar ignore contract changed while being read.");
        var contents = bytes.AsSpan();
        if (contents.StartsWith(Utf8Preamble)) contents = contents[Utf8Preamble.Length..];
        string text;
        try { text = StrictUtf8.GetString(contents); }
        catch (DecoderFallbackException ex)
        {
            throw new IOException(
                "Existing .mutate4csharp/.gitignore conflicts with the required owned-state contract.", ex);
        }
        var lines = text.Split('\n');
        var lastPattern = lines.Select(line => line.EndsWith('\r') ? line[..^1] : line)
            .Select(line => line.TrimEnd(' '))
            .LastOrDefault(line => line.Length > 0 && !line.StartsWith('#'));
        if (lastPattern != "*")
            throw new IOException("Existing .mutate4csharp/.gitignore conflicts with the required owned-state contract.");
    }

    private static void EnsureOrdinaryDirectory(string path)
    {
        if (File.Exists(path)) throw new IOException("Owned sidecar path collides with a file.");
        if (Directory.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Owned sidecar directories cannot be reparse points.");
        Directory.CreateDirectory(path);
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Owned sidecar directories cannot be reparse points.");
    }

    private string StatePath(string category, string key) => Path.Combine(_stateRoot, category, key + ".json");
    private static string DiscoveryKey(DiscoverySidecar record) => HashKey(record.EvaluationFingerprint + "\0" + record.RunId);
    private static string FingerprintKey(string fingerprint) => HashKey(fingerprint);
    private static string HashKey(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))
        .ToLowerInvariant();

    private static void ValidateDiscovery(DiscoverySidecar record)
    {
        ValidateCommon(record.SchemaVersion, record.RecordKind, SidecarRecordKind.Discovery, record.RunId,
            record.EvaluationFingerprint, record.SnapshotId, record.ReportSha256, record.ReportLength);
        if (record.ScopeExclusions.Count > ScopePlanner.MaxScopeItems ||
            record.ScopeExclusions.Any(item => string.IsNullOrWhiteSpace(item) || item.Length > 4096))
            throw new EvaluationContractException("Discovery exclusions exceed sidecar bounds.");
    }

    private static void ValidateProven(ProvenEvaluationSidecar record, EvaluationReport report,
        EvaluationFingerprintMaterial fingerprintMaterial)
    {
        var expectedFingerprint = EvaluationFingerprint.ComputeForProven(fingerprintMaterial);
        ValidateProvenEnvelope(record, expectedFingerprint);
        var reportBytes = ReportWriter.Serialize(report);
        var reportHash = Convert.ToHexString(SHA256.HashData(reportBytes)).ToLowerInvariant();
        var reportSnapshotId = ReportSnapshotId(report);
        var reportScope = ReportWriter.SerializeCanonicalScope(report.ScopePlan);
        var reportSuites = report.Suites.Select(suite => suite.SuiteId).ToArray();
        if (report.Outcome != EvaluationOutcome.Pass || report.DiagnosticPartial ||
            report.Mode != "check" || report.Baseline != BaselineStatus.Green ||
            !report.ScopePlan.IsComplete || report.IncompleteConditions.Count != 0 || report.Reasons.Count != 0 ||
            report.Counts.Enumerated is null || report.Counts.Selected != report.Counts.Enumerated ||
            report.Counts.FreshUncovered != 0 || report.Counts.Omitted != 0 || report.Counts.Errors != 0 ||
            report.Suites.Count == 0 || report.Suites.Any(suite => suite.Baseline != BaselineStatus.Green) ||
            reportSuites.Distinct(StringComparer.Ordinal).Count() != reportSuites.Length ||
            report.Policy != fingerprintMaterial.Policy ||
            fingerprintMaterial.Scope != reportScope || fingerprintMaterial.SnapshotId != reportSnapshotId ||
            record.EvaluationFingerprint != expectedFingerprint || record.SnapshotId != reportSnapshotId ||
            !record.PolicyComplete || record.RunId != report.RunId || record.Counts != report.Counts ||
            record.ReportLength != reportBytes.LongLength || record.ReportSha256 != reportHash)
            throw new EvaluationContractException("Only complete, internally validated PASS reports may publish proven state.");
        var coverageSuites = record.Coverage.Select(item => item.SuiteId).Order(StringComparer.Ordinal).ToArray();
        if (!coverageSuites.SequenceEqual(reportSuites.Order(StringComparer.Ordinal), StringComparer.Ordinal))
            throw new EvaluationContractException("Proven coverage must bind every report suite exactly once.");
        foreach (var coverage in record.Coverage)
        {
            if (coverage.RunnerIdentity != fingerprintMaterial.RunnerIdentity)
                throw new EvaluationContractException("Coverage runner identity does not match the evaluation fingerprint.");
            coverage.Validate(expectedFingerprint, reportSnapshotId, report.RunId, coverage.SuiteId);
        }
    }

    private static void ValidateProvenEnvelope(ProvenEvaluationSidecar record, string expectedFingerprint)
    {
        ValidateCommon(record.SchemaVersion, record.RecordKind, SidecarRecordKind.Proven, record.RunId,
            record.EvaluationFingerprint, record.SnapshotId, record.ReportSha256, record.ReportLength);
        if (record.EvaluationFingerprint != expectedFingerprint || !record.PolicyComplete || record.Coverage is null ||
            record.Counts is null || record.Coverage.Count == 0 ||
            record.Coverage.Any(item => item is null) ||
            record.Coverage.Select(item => item.SuiteId).Distinct(StringComparer.Ordinal).Count() != record.Coverage.Count ||
            record.Counts.Enumerated is null || record.Counts.Enumerated < 1 ||
            record.Counts.Enumerated != record.Counts.Selected ||
            record.Counts.Selected != record.Counts.Executed + record.Counts.FreshUncovered + record.Counts.Omitted ||
            record.Counts.FreshUncovered != 0 || record.Counts.Omitted != 0 || record.Counts.Errors != 0 ||
            record.Counts.Survived != 0 || record.Counts.CompileInvalid > record.Counts.Executed)
            throw new EvaluationContractException("Proven state does not match the requested fingerprint.");
        foreach (var coverage in record.Coverage)
            coverage.Validate(expectedFingerprint, record.SnapshotId, record.RunId, coverage.SuiteId);
    }

    private static void ValidateRawProvenSchema(byte[] bytes)
    {
        using var document = JsonDocument.Parse(bytes);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("recordKind", out var kind) ||
            kind.ValueKind != JsonValueKind.String || kind.GetString() != "PROVEN" ||
            !root.TryGetProperty("coverage", out var coverage) || coverage.ValueKind != JsonValueKind.Array)
            throw new EvaluationContractException("Proven state does not use the exact published schema enums.");
        foreach (var item in coverage.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object ||
                !item.TryGetProperty("baseline", out var baseline) ||
                baseline.ValueKind != JsonValueKind.String || baseline.GetString() != "GREEN")
                throw new EvaluationContractException("Proven state does not use the exact published schema enums.");
        }
    }

    private static bool IsFatal(Exception exception) =>
        exception is OutOfMemoryException or StackOverflowException or AccessViolationException;

    private static string ReportSnapshotId(EvaluationReport report)
    {
        var values = report.Evidence.Where(item => item.Kind == "INPUT_SNAPSHOT")
            .SelectMany(item => item.Diagnostics ?? [])
            .Where(value => value.StartsWith("captureId=", StringComparison.Ordinal))
            .Select(value => value["captureId=".Length..]).ToArray();
        if (values.Length != 1 || !EvaluationFingerprint.IsSha256(values[0]))
            throw new EvaluationContractException("A proven report requires exactly one lowercase snapshot capture ID.");
        return values[0];
    }

    private static void ValidateCommon(string? schemaVersion, SidecarRecordKind actualKind,
        SidecarRecordKind expectedKind, string? runId, string? fingerprint, string? snapshotId,
        string? reportSha256, long reportLength)
    {
        if (schemaVersion != "1" || actualKind != expectedKind || string.IsNullOrWhiteSpace(runId) || runId.Length > 256 ||
            !EvaluationFingerprint.IsFingerprint(fingerprint) || !EvaluationFingerprint.IsSha256(snapshotId) ||
            !EvaluationFingerprint.IsSha256(reportSha256) || reportLength < 1)
            throw new EvaluationContractException("Sidecar record is incomplete or uses an unsupported schema.");
    }
}
