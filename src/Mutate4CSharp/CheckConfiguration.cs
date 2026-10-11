using System.Buffers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Text;

namespace Mutate4CSharp;

internal sealed class CheckConfigurationException(string message, Exception? inner = null) :
    ArgumentException(message, inner);

internal sealed record CheckProject(string Id, string Project, string TargetFramework,
    string ParseContext, string LanguageVersion, string? Nullable,
    IReadOnlyList<string>? DefineConstants, IReadOnlyList<string> Sources,
    IReadOnlyList<string> SharedSources,
    IReadOnlyList<string> TestSuites);

internal sealed record CheckTestSuite(string Id, string Path, string Runner, string Framework,
    string Configuration, IReadOnlyList<string> ExpectedMembers);

internal sealed record CheckExclusion(string Path, string Reason);

internal sealed record SuiteExecution(string Identity, IReadOnlyList<string> Aliases, string Path,
    string Runner, string Framework, string Configuration, IReadOnlyList<string> ExpectedMembers);

internal sealed record CheckConfiguration(string SchemaVersion, string Root,
    IReadOnlyList<CheckProject> Projects, IReadOnlyList<CheckTestSuite> TestSuites,
    IReadOnlyList<SuiteExecution> ExecutionSuites, IReadOnlyList<CheckExclusion> Exclusions,
    EvaluationPolicy Policy)
{
    private const int MaxWorkers = 32;
    private const int MaxMutationCap = 100_000;
    private const int MaxPhaseTimeoutSeconds = 86_400;
    private const int MaxOverallDeadlineSeconds = 604_800;
    private static readonly Regex Identifier = new("^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$",
        RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly Regex Net10Framework = new("^net10\\.0(?:-[a-z0-9.]+)?$",
        RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly Regex ConfigurationName = new("^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$",
        RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly Regex PreprocessorSymbol = new("^[A-Za-z_][A-Za-z0-9_]{0,127}$",
        RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    private sealed record Document(int Version = 0, List<ProjectDocument>? Projects = null,
        List<SuiteDocument>? TestSuites = null, List<ExclusionDocument>? Exclusions = null,
        PolicyDocument? Policy = null);
    private sealed record ProjectDocument(string? Id = null, string? Project = null,
        string? TargetFramework = null, string? ParseContext = null,
        string? LanguageVersion = null, string? Nullable = null,
        List<string>? DefineConstants = null, List<string>? Sources = null,
        List<string>? SharedSources = null,
        List<string>? TestSuites = null);
    private sealed record SuiteDocument(string? Id = null, string? Path = null, string? Runner = null,
        string? Framework = null, string? Configuration = null, List<string>? ExpectedMembers = null);
    private sealed record ExclusionDocument(string? Path = null, string? Reason = null);
    private sealed record PolicyDocument(int? MaxWorkers = null, int? MutationCap = null,
        int? BaselineTimeoutSeconds = null, int? MutantTimeoutSeconds = null,
        int? OverallDeadlineSeconds = null, bool? AllowNotApplicable = null,
        int? StabilityRepetitions = null);

    public static CheckConfiguration Load(string root, byte[] capturedBytes)
    {
        ArgumentNullException.ThrowIfNull(capturedBytes);
        var fullRoot = Path.GetFullPath(root);
        Document document;
        try
        {
            using var parsed = JsonDocument.Parse(capturedBytes, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 32
            });
            if (parsed.RootElement.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("mutate4csharp.json must contain a JSON object.");
            RejectDuplicateProperties(parsed.RootElement, "$", 0);
            RejectNullValues(parsed.RootElement, "$", 0);
            document = JsonSerializer.Deserialize<Document>(capturedBytes, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = false,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
                Converters = { new IntegralInt32Converter() }
            }) ?? throw new ArgumentException("mutate4csharp.json must contain a JSON object.");
        }
        catch (JsonException ex)
        {
            throw new ArgumentException($"Invalid mutate4csharp.json (unknown, duplicate, or malformed setting): {ex.Message}", ex);
        }

        if (document.Version != 1)
            throw new ArgumentException("mutate4csharp.json requires version 1.");
        if (document.Projects is null || document.Projects.Count == 0)
            throw new ArgumentException("mutate4csharp.json requires at least one production project.");
        if (document.TestSuites is null || document.TestSuites.Count == 0)
            throw new ArgumentException("mutate4csharp.json requires at least one test suite.");

        if (document.Projects.Any(item => item is null) || document.TestSuites.Any(item => item is null))
            throw new CheckConfigurationException("Configured projects and testSuites cannot contain null members.");
        var projects = document.Projects.Select((item, index) => ParseProject(item!, index)).ToArray();
        var suites = document.TestSuites.Select((item, index) => ParseSuite(item!, index)).ToArray();
        RejectDuplicateIds(projects.Select(item => item.Id), "production project");
        RejectDuplicateIds(suites.Select(item => item.Id), "test suite");

        var executions = new List<SuiteExecution>();
        foreach (var pathGroup in suites.GroupBy(item => NormalizeSuiteIdentityPath(item.Path), StringComparer.Ordinal))
        {
            var identities = pathGroup.GroupBy(SuiteIdentity, StringComparer.Ordinal).ToArray();
            if (identities.Length > 1)
                throw new ArgumentException($"Test-suite aliases for {pathGroup.First().Path} conflict in runner or execution settings.");
            var values = identities[0].OrderBy(item => item.Id, StringComparer.Ordinal).ToArray();
            var canonical = values[0];
            var runnablePath = ResolveRunnableSuitePath(fullRoot, values);
            executions.Add(new(identities[0].Key, values.Select(item => item.Id).ToArray(), runnablePath,
                canonical.Runner, canonical.Framework, canonical.Configuration, canonical.ExpectedMembers));
        }

        var suiteIds = executions.SelectMany(item => item.Aliases).ToHashSet(StringComparer.Ordinal);
        foreach (var project in projects)
        {
            if (project.TestSuites.Count == 0)
                throw new ArgumentException($"Production project {project.Id} requires at least one test-suite mapping.");
            var duplicateMapping = project.TestSuites.GroupBy(value => value, StringComparer.Ordinal)
                .FirstOrDefault(group => group.Count() > 1);
            if (duplicateMapping is not null)
                throw new ArgumentException($"Duplicate test-suite mapping {duplicateMapping.Key} in project {project.Id}.");
            var missing = project.TestSuites.FirstOrDefault(id => !suiteIds.Contains(id));
            if (missing is not null)
                throw new ArgumentException($"Production project {project.Id} maps unknown test suite {missing}.");
        }

        if (document.Exclusions?.Any(item => item is null) == true)
            throw new CheckConfigurationException("Configured exclusions cannot contain null members.");
        var exclusions = (document.Exclusions ?? []).Select((item, index) => new CheckExclusion(
            NormalizePath(item!.Path, $"exclusions[{index}].path"), Required(item.Reason,
                $"exclusions[{index}].reason", 512))).OrderBy(item => item.Path, StringComparer.Ordinal).ToArray();
        var duplicateExclusion = exclusions.GroupBy(item => item.Path, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateExclusion is not null)
            throw new ArgumentException($"Duplicate exclusion path: {duplicateExclusion.Key}.");

        var policy = ParsePolicy(document.Policy);
        return new("1", fullRoot, projects.OrderBy(item => item.Id, StringComparer.Ordinal).ToArray(),
            suites.OrderBy(item => item.Id, StringComparer.Ordinal).ToArray(),
            executions.OrderBy(item => item.Identity, StringComparer.Ordinal).ToArray(), exclusions, policy);
    }

    internal IReadOnlyDictionary<string, string> ExecutionPathsByAlias() => ExecutionSuites
        .SelectMany(execution => execution.Aliases.Select(alias => (Alias: alias, execution.Path)))
        .ToDictionary(item => item.Alias, item => item.Path, StringComparer.Ordinal);

    private static string ResolveRunnableSuitePath(string root, IReadOnlyList<CheckTestSuite> aliases)
    {
        var hostPathComparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        var existing = aliases.Where(alias => File.Exists(Path.Combine(root,
                alias.Path.Replace('/', Path.DirectorySeparatorChar))))
            .Select(alias => alias.Path).ToArray();
        return SelectRunnableSuitePath(aliases, existing, hostPathComparer);
    }

    internal static string SelectRunnableSuitePath(IReadOnlyList<CheckTestSuite> aliases,
        IReadOnlyList<string> existingPaths, StringComparer hostPathComparer)
    {
        var existing = existingPaths.OrderBy(path => path, StringComparer.Ordinal)
            .Distinct(hostPathComparer).ToArray();
        if (existing.Length > 1)
            throw new ArgumentException($"Test-suite aliases for {aliases[0].Path} identify distinct existing paths: " +
                                        $"{string.Join(", ", existing)}.");
        return existing.Length == 1 ? existing[0] : aliases[0].Path;
    }

    private static CheckProject ParseProject(ProjectDocument item, int index)
    {
        var id = ParseId(item.Id, $"projects[{index}].id");
        var project = NormalizePath(item.Project, $"projects[{index}].project");
        if (!project.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"Production project {id} must use a .csproj path.");
        var framework = Required(item.TargetFramework, $"projects[{index}].targetFramework", 128);
        if (!Net10Framework.IsMatch(framework))
            throw new ArgumentException($"Production project {id} targetFramework must use the version-1 net10.0 contract.");
        var parseContext = Required(item.ParseContext, $"projects[{index}].parseContext", 256);
        var languageVersion = item.LanguageVersion is null ? "14.0" :
            Required(item.LanguageVersion, $"projects[{index}].languageVersion", 32);
        if (languageVersion != "14.0")
            throw new ArgumentException(
                $"Production project {id} languageVersion must use the version-1 C# 14.0 contract.");
        var nullable = item.Nullable is null ? null :
            Required(item.Nullable, $"projects[{index}].nullable", 32);
        if (nullable is not null and not ("enable" or "disable" or "annotations" or "warnings"))
            throw new ArgumentException(
                $"Production project {id} nullable must be enable, disable, annotations, or warnings.");
        var defineConstants = item.DefineConstants is null ? null : DistinctValues(item.DefineConstants,
            $"projects[{index}].defineConstants", requireNonEmpty: false);
        var invalidSymbol = defineConstants?.FirstOrDefault(symbol => !PreprocessorSymbol.IsMatch(symbol));
        if (invalidSymbol is not null)
            throw new ArgumentException(
                $"Production project {id} has an invalid preprocessor symbol: {invalidSymbol}.");
        var sources = NormalizeDistinct(item.Sources, $"projects[{index}].sources", requireNonEmpty: true);
        var sharedSources = NormalizeDistinct(item.SharedSources ?? [],
            $"projects[{index}].sharedSources", requireNonEmpty: false);
        var undeclaredShared = sharedSources.FirstOrDefault(shared => !sources.Contains(shared,
            StringComparer.Ordinal));
        if (undeclaredShared is not null)
            throw new ArgumentException(
                $"Production project {id} sharedSources must also appear in sources: {undeclaredShared}.");
        var suites = DistinctValues(item.TestSuites, $"projects[{index}].testSuites", requireNonEmpty: true);
        return new(id, project, framework, parseContext, languageVersion, nullable,
            defineConstants, sources, sharedSources, suites);
    }

    private static CheckTestSuite ParseSuite(SuiteDocument item, int index)
    {
        var id = ParseId(item.Id, $"testSuites[{index}].id");
        var path = NormalizePath(item.Path, $"testSuites[{index}].path");
        if (!path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) &&
            !path.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) &&
            !path.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"Test suite {id} must use a .csproj, .sln, or .slnx path.");
        var runner = Required(item.Runner, $"testSuites[{index}].runner", 64);
        if (!runner.Equals("vstest", StringComparison.Ordinal))
            throw new ArgumentException($"Unsupported test runner '{runner}' for suite {id}; version 1 supports only vstest/TRX.");
        var framework = Required(item.Framework, $"testSuites[{index}].framework", 128);
        if (!Net10Framework.IsMatch(framework))
            throw new ArgumentException($"Test suite {id} framework must use the version-1 net10.0 contract.");
        var configuration = Required(item.Configuration, $"testSuites[{index}].configuration", 128);
        if (!ConfigurationName.IsMatch(configuration))
            throw new ArgumentException($"Test suite {id} configuration must be a stable ASCII identifier.");
        var members = DistinctValues(item.ExpectedMembers, $"testSuites[{index}].expectedMembers",
            requireNonEmpty: true, StringComparer.OrdinalIgnoreCase, maximum: 256);
        if (members.Any(member => member.Contains('/') || member.Contains('\\') || member is "." or ".."))
            throw new ArgumentException($"Expected TRX member names for suite {id} must be file names.");
        return new(id, path, runner, framework, configuration, members);
    }

    private static EvaluationPolicy ParsePolicy(PolicyDocument? document)
    {
        var defaults = ExecutionPolicy.Default;
        var policy = new EvaluationPolicy(document?.MaxWorkers ?? defaults.MaxWorkers,
            document?.MutationCap ?? defaults.MutationCap,
            document?.BaselineTimeoutSeconds ?? defaults.BaselineTimeoutSeconds,
            document?.MutantTimeoutSeconds ?? defaults.MutantTimeoutSeconds,
            document?.OverallDeadlineSeconds ?? defaults.OverallDeadlineSeconds,
            document?.AllowNotApplicable ?? defaults.AllowNotApplicable,
            document?.StabilityRepetitions ?? defaults.StabilityRepetitions);
        try { ExecutionPolicy.Validate(policy); }
        catch (ArgumentException ex) { throw new ArgumentException($"Invalid execution policy: {ex.Message}", ex); }
        if (policy.MaxWorkers > MaxWorkers)
            throw new ArgumentException($"maxWorkers cannot exceed the bounded limit of {MaxWorkers}.");
        if (policy.MutationCap > MaxMutationCap)
            throw new ArgumentException($"mutationCap cannot exceed {MaxMutationCap}.");
        if (policy.BaselineTimeoutSeconds > MaxPhaseTimeoutSeconds ||
            policy.MutantTimeoutSeconds > MaxPhaseTimeoutSeconds ||
            policy.OverallDeadlineSeconds > MaxOverallDeadlineSeconds)
            throw new ArgumentException("Configured execution timeouts exceed version-1 safety limits.");
        return policy;
    }

    internal static string SuiteIdentity(CheckTestSuite suite)
    {
        var components = new List<(string Name, string Value)>
        {
            ("path", NormalizeSuiteIdentityPath(suite.Path)), ("runner", suite.Runner), ("framework", suite.Framework),
            ("configuration", suite.Configuration), ("member-count", suite.ExpectedMembers.Count.ToString(
                System.Globalization.CultureInfo.InvariantCulture))
        };
        components.AddRange(suite.ExpectedMembers.Select((member, index) => ($"member.{index}", member)));
        return "suite:v1:" + MutationIdentity.ComputeDigest("suite-execution", components);
    }

    private static string NormalizeSuiteIdentityPath(string path) =>
        path.Replace('\\', '/').ToUpperInvariant();

    private static string ParseId(string? value, string member)
    {
        var id = Required(value, member, 128);
        if (!Identifier.IsMatch(id))
            throw new ArgumentException($"Configured {member} must be a stable ASCII identifier.");
        return id;
    }

    private static string Required(string? value, string member, int maximum)
    {
        if (string.IsNullOrWhiteSpace(value) || value.EnumerateRunes().Count() > maximum ||
            value.EnumerateRunes().Any(rune => rune.Value <= 0x1f || rune.Value is >= 0x7f and <= 0x9f) ||
            IsContractWhitespace(value.EnumerateRunes().First()) ||
            IsContractWhitespace(value.EnumerateRunes().Last()))
            throw new ArgumentException($"Configured {member} must be non-empty and at most {maximum} characters.");
        return value;
    }

    private static bool IsContractWhitespace(Rune rune) => Rune.IsWhiteSpace(rune) || rune.Value == 0xfeff;

    private static string NormalizePath(string? value, string member)
    {
        var path = Required(value, member, 1024);
        if (path.Contains('\\'))
            throw new ArgumentException($"Configured {member} path must use canonical forward slashes: {value}");
        var driveQualified = path.Length >= 2 && char.IsAsciiLetter(path[0]) && path[1] == ':';
        if (driveQualified || Path.IsPathRooted(path) || path.Split('/').Any(part => part is "" or "." or ".."))
            throw new ArgumentException($"Configured {member} path must be canonical and repository-relative: {value}");
        return path;
    }

    private static IReadOnlyList<string> NormalizeDistinct(List<string>? values, string member, bool requireNonEmpty) =>
        Distinct(values?.Select(value => NormalizePath(value, member)).ToArray(), member, requireNonEmpty);

    private static IReadOnlyList<string> DistinctValues(List<string>? values, string member, bool requireNonEmpty,
        StringComparer? comparer = null, int maximum = 1024) =>
        Distinct(values?.Select(value => Required(value, member, maximum)).ToArray(), member, requireNonEmpty,
            comparer ?? StringComparer.Ordinal);

    private static IReadOnlyList<string> Distinct(string[]? values, string member, bool requireNonEmpty,
        StringComparer? comparer = null)
    {
        comparer ??= StringComparer.Ordinal;
        if (values is null || requireNonEmpty && values.Length == 0)
            throw new ArgumentException($"Configured {member} must contain at least one member.");
        var duplicate = values.GroupBy(value => value, comparer).FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null) throw new ArgumentException($"Duplicate configured {member} member: {duplicate.Key}.");
        return values.OrderBy(value => value, StringComparer.Ordinal).ToArray();
    }

    private static void RejectDuplicateIds(IEnumerable<string> ids, string kind)
    {
        var duplicate = ids.GroupBy(value => value, StringComparer.Ordinal).FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null) throw new ArgumentException($"Duplicate configured {kind} ID: {duplicate.Key}.");
    }

    private static void RejectDuplicateProperties(JsonElement element, string path, int depth)
    {
        if (depth > 32) throw new ArgumentException("mutate4csharp.json exceeds the maximum nesting depth.");
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                    throw new ArgumentException($"Duplicate JSON property at {path}.{property.Name}.");
                RejectDuplicateProperties(property.Value, $"{path}.{property.Name}", depth + 1);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var item in element.EnumerateArray())
                RejectDuplicateProperties(item, $"{path}[{index++}]", depth + 1);
        }
    }

    private static void RejectNullValues(JsonElement element, string path, int depth)
    {
        if (depth > 32) throw new CheckConfigurationException("mutate4csharp.json exceeds the maximum nesting depth.");
        if (element.ValueKind == JsonValueKind.Null)
            throw new CheckConfigurationException($"Null is not valid at {path}.");
        if (element.ValueKind == JsonValueKind.Object)
            foreach (var property in element.EnumerateObject())
                RejectNullValues(property.Value, $"{path}.{property.Name}", depth + 1);
        else if (element.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var item in element.EnumerateArray())
                RejectNullValues(item, $"{path}[{index++}]", depth + 1);
        }
    }

    private sealed class IntegralInt32Converter : JsonConverter<int>
    {
        public override int Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.Number)
                throw new JsonException("Execution policy values must be integral JSON numbers.");
            var token = reader.HasValueSequence ? reader.ValueSequence.ToArray() : reader.ValueSpan.ToArray();
            if (TryParseExactInt32(token, out var integer)) return integer;
            throw new JsonException("Execution policy value is not an exact 32-bit integer.");
        }

        public override void Write(Utf8JsonWriter writer, int value, JsonSerializerOptions options) =>
            writer.WriteNumberValue(value);

        private static bool TryParseExactInt32(ReadOnlySpan<byte> token, out int value)
        {
            value = 0;
            var negative = token[0] == (byte)'-';
            var start = negative ? 1 : 0;
            var exponentMarker = token.IndexOfAny((byte)'e', (byte)'E');
            var mantissaEnd = exponentMarker < 0 ? token.Length : exponentMarker;
            var dot = token[start..mantissaEnd].IndexOf((byte)'.');
            dot = dot < 0 ? mantissaEnd : start + dot;
            var fractionalDigits = dot == mantissaEnd ? 0 : mantissaEnd - dot - 1;
            var exponent = 0;
            if (exponentMarker >= 0)
            {
                var index = exponentMarker + 1;
                var exponentNegative = token[index] == (byte)'-';
                if (token[index] is (byte)'+' or (byte)'-') index++;
                while (index < token.Length)
                {
                    var digit = token[index] - (byte)'0';
                    if (exponent > 10_000 || exponent == 10_000 && digit > 0) return false;
                    exponent = exponent * 10 + digit;
                    index++;
                }
                if (exponentNegative) exponent = -exponent;
            }

            var digits = token[start..mantissaEnd].ToArray().Where(item => item != (byte)'.').ToArray();
            var decimalPosition = (long)digits.Length - fractionalDigits + exponent;
            var firstNonZero = Array.FindIndex(digits, item => item != (byte)'0');
            if (firstNonZero < 0) return true;
            if (decimalPosition <= firstNonZero) return false;
            for (var index = Math.Max((int)Math.Min(decimalPosition, digits.Length), 0); index < digits.Length; index++)
                if (digits[index] != (byte)'0') return false;

            var limit = negative ? 2147483648L : int.MaxValue;
            long magnitude = 0;
            for (long index = 0; index < decimalPosition; index++)
            {
                var digit = index < digits.Length ? digits[index] - (byte)'0' : 0;
                if (magnitude > (limit - digit) / 10) return false;
                magnitude = magnitude * 10 + digit;
            }
            value = negative ? (int)-magnitude : (int)magnitude;
            return true;
        }
    }
}
