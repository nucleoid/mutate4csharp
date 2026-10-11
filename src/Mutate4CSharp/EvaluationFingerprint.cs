using System.Buffers.Binary;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Mutate4CSharp;

internal sealed record FingerprintInput(string Kind, string Path, long Length, string Sha256,
    bool Exists = true, bool IsTracked = true);

internal sealed record EvaluationFingerprintMaterial(
    IReadOnlyList<FingerprintInput> Inputs,
    string SnapshotId,
    string Scope,
    string Configuration,
    string ToolVersion,
    string OperatorVersion,
    string SdkIdentity,
    string RuntimeIdentity,
    string RunnerIdentity,
    EvaluationPolicy Policy,
    bool ProvenanceComplete = false);

internal static class EvaluationFingerprint
{
    public const string Algorithm = "sha256-length-framed-v1";

    public static string Compute(EvaluationFingerprintMaterial material)
    {
        ArgumentNullException.ThrowIfNull(material);
        if (!IsSha256(material.SnapshotId))
            throw new ArgumentException("SnapshotId must be an exact lowercase SHA-256 identity.");
        ValidateText(material.Scope, nameof(material.Scope), bounded: false);
        ValidateText(material.Configuration, nameof(material.Configuration));
        ValidateText(material.ToolVersion, nameof(material.ToolVersion));
        ValidateText(material.OperatorVersion, nameof(material.OperatorVersion));
        ValidateText(material.SdkIdentity, nameof(material.SdkIdentity));
        ValidateText(material.RuntimeIdentity, nameof(material.RuntimeIdentity));
        ValidateText(material.RunnerIdentity, nameof(material.RunnerIdentity));
        if (material.Inputs.Count > 100_000) throw new ArgumentException("Fingerprint input count is unbounded.");

        var inputs = material.Inputs.OrderBy(input => input.Kind, StringComparer.Ordinal)
            .ThenBy(input => input.Path, StringComparer.Ordinal).ToArray();
        if (inputs.Select(input => $"{input.Kind}\0{input.Path}").Distinct(StringComparer.Ordinal).Count() != inputs.Length)
            throw new ArgumentException("Fingerprint inputs contain duplicate kind/path identities.");

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Add(hash, "algorithm", Algorithm);
        foreach (var input in inputs)
        {
            ValidateToken(input.Kind, nameof(input.Kind));
            ValidateRelative(input.Path);
            if (input.Length < 0 || !input.Exists && input.Length != 0)
                throw new ArgumentException("Fingerprint input length is invalid.");
            if (input.Exists ? !IsSha256(input.Sha256) : input.Sha256 != "missing")
                throw new ArgumentException("Fingerprint input requires exact file or deletion evidence.");
            Add(hash, "input-kind", input.Kind);
            Add(hash, "input-path", input.Path);
            Add(hash, "input-state", input.Exists ? "file" : "deleted");
            Add(hash, "input-tracking", input.IsTracked ? "tracked" : "untracked");
            Add(hash, "input-length", input.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Add(hash, "input-sha256", input.Exists ? input.Sha256.ToLowerInvariant() : input.Sha256);
        }
        Add(hash, "snapshot-id", material.SnapshotId);
        Add(hash, "scope", material.Scope);
        Add(hash, "configuration", material.Configuration);
        Add(hash, "tool-version", material.ToolVersion);
        Add(hash, "operator-version", material.OperatorVersion);
        Add(hash, "sdk", material.SdkIdentity);
        Add(hash, "runtime", material.RuntimeIdentity);
        Add(hash, "runner", material.RunnerIdentity);
        Add(hash, "policy", JsonSerializer.Serialize(material.Policy));
        return "sha256:" + Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    public static string ComputeForProven(EvaluationFingerprintMaterial material)
    {
        if (!material.ProvenanceComplete || material.Inputs.All(input => input.Kind != "dependency") ||
            !ContainsExactIdentity(material.RunnerIdentity, "collector", "coverlet-opencover-v1") ||
            !ContainsBoundIdentity(material.RunnerIdentity, "coverage") ||
            !ContainsBoundIdentity(material.RunnerIdentity, "plan") ||
            new[] { material.Configuration, material.ToolVersion, material.OperatorVersion, material.SdkIdentity,
                    material.RuntimeIdentity, material.RunnerIdentity }
                .Any(value => value.Contains("not-prepared", StringComparison.OrdinalIgnoreCase) ||
                              value.Contains("not-executed", StringComparison.OrdinalIgnoreCase) ||
                              value.Contains("placeholder", StringComparison.OrdinalIgnoreCase) ||
                              value.Contains("unavailable", StringComparison.OrdinalIgnoreCase)))
            throw new EvaluationContractException(
                "Proven state requires exact dependency, SDK, runtime, runner, tool, operator, and configuration identities.");
        return Compute(material);
    }

    private static bool ContainsBoundIdentity(string value, string label)
    {
        var fields = value.Split(';').Where(item => item.StartsWith(label + "=", StringComparison.Ordinal)).ToArray();
        return fields.Length == 1 && IsFingerprint(fields[0][(label.Length + 1)..]);
    }

    private static bool ContainsExactIdentity(string value, string label, string expected)
    {
        var fields = value.Split(';').Where(item => item.StartsWith(label + "=", StringComparison.Ordinal)).ToArray();
        return fields.Length == 1 && fields[0][(label.Length + 1)..] == expected;
    }

    public static string ToolIdentity(Assembly assembly)
    {
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;
        if (string.IsNullOrWhiteSpace(informational))
            informational = assembly.GetName().Version?.ToString();
        if (string.IsNullOrWhiteSpace(informational))
            throw new EvaluationContractException("The executing tool has no exact version identity.");
        return $"informational={informational};mvid={assembly.ManifestModule.ModuleVersionId:D}";
    }

    public static FingerprintInput FromBytes(string kind, string path, ReadOnlySpan<byte> bytes) =>
        new(kind, NormalizeRelative(path), bytes.Length,
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());

    public static bool IsFingerprint(string? value) => value?.StartsWith("sha256:", StringComparison.Ordinal) == true &&
        IsSha256(value[7..]);

    private static void Add(IncrementalHash hash, string label, string value)
    {
        var labelBytes = Encoding.UTF8.GetBytes(label);
        var valueBytes = Encoding.UTF8.GetBytes(value);
        Span<byte> length = stackalloc byte[16];
        BinaryPrimitives.WriteInt64BigEndian(length[..8], labelBytes.LongLength);
        BinaryPrimitives.WriteInt64BigEndian(length[8..], valueBytes.LongLength);
        hash.AppendData(length);
        hash.AppendData(labelBytes);
        hash.AppendData(valueBytes);
    }

    private static void ValidateText(string? value, string name, bool bounded = true)
    {
        if (string.IsNullOrWhiteSpace(value) || bounded && Encoding.UTF8.GetByteCount(value) > 1024 * 1024)
            throw new ArgumentException($"{name} is missing or exceeds fingerprint bounds.");
    }

    private static void ValidateToken(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 64 || value.Any(character =>
                !(char.IsAsciiLetterOrDigit(character) || character is '_' or '-')))
            throw new ArgumentException($"{name} is not a bounded token.");
    }

    private static void ValidateRelative(string path) => NormalizeRelative(path);

    private static string NormalizeRelative(string path)
    {
        var normalized = path.Replace('\\', '/');
        if (string.IsNullOrWhiteSpace(normalized) || normalized.Length > 4096 || Path.IsPathRooted(normalized) ||
            normalized.Split('/').Any(part => part is "" or "." or ".."))
            throw new ArgumentException("Fingerprint paths must be normalized relative paths.");
        return normalized;
    }

    internal static bool IsSha256(string? value) => value?.Length == 64 && value.All(character =>
        character is >= '0' and <= '9' or >= 'a' and <= 'f');
}
