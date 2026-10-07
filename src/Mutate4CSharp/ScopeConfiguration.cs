using System.Text.Json;

namespace Mutate4CSharp;

internal sealed record ConfiguredProject(string Project, IReadOnlyList<string> Tests,
    IReadOnlyList<string> Sources);
internal sealed record ConfiguredExclusion(string Path, string Reason);

internal sealed record ScopeConfiguration(
    string Root,
    bool IncludeGenerated,
    bool IncludeTests,
    IReadOnlyList<ConfiguredProject> Projects,
    IReadOnlyList<ConfiguredExclusion> Exclusions)
{
    private sealed record Document(int Version = 0, bool IncludeGenerated = false,
        bool IncludeTests = false, List<ProjectDocument>? Projects = null,
        List<TestSuiteDocument>? TestSuites = null);
    private sealed record ProjectDocument(string? Project = null, List<string>? Tests = null,
        List<string>? Sources = null, List<string>? TestSuites = null);
    private sealed record TestSuiteDocument(string? Id = null, string? Path = null);

    public static ScopeConfiguration Load(string root)
    {
        var fullRoot = Path.GetFullPath(root);
        var path = Path.Combine(fullRoot, "mutate4csharp.json");
        return Load(fullRoot, File.Exists(path) ? File.ReadAllBytes(path) : null);
    }

    public static ScopeConfiguration Load(string root, byte[]? capturedBytes)
    {
        var fullRoot = Path.GetFullPath(root);
        if (capturedBytes is null) return new(fullRoot, false, false, [], []);
        try
        {
            using var parsed = JsonDocument.Parse(capturedBytes);
            var hasStrictSuites = parsed.RootElement.ValueKind == JsonValueKind.Object &&
                (parsed.RootElement.EnumerateObject().Any(property =>
                     property.Name.Equals("testSuites", StringComparison.OrdinalIgnoreCase)) ||
                 parsed.RootElement.TryGetProperty("projects", out var strictProjects) &&
                 strictProjects.ValueKind == JsonValueKind.Array && strictProjects.EnumerateArray().Any(project =>
                     project.ValueKind == JsonValueKind.Object && project.EnumerateObject().Any(property =>
                         property.Name.Equals("testSuites", StringComparison.OrdinalIgnoreCase))));
            if (hasStrictSuites)
            {
                var strict = CheckConfiguration.Load(fullRoot, capturedBytes);
                var strictSuitePaths = strict.ExecutionPathsByAlias();
                return new(fullRoot, false, false, strict.Projects.Select(project => new ConfiguredProject(
                    project.Project, project.TestSuites.Select(id => strictSuitePaths[id])
                        .Distinct(StringComparer.Ordinal).OrderBy(value => value,
                            StringComparer.Ordinal).ToArray(), project.Sources)).OrderBy(item => item.Project,
                            StringComparer.Ordinal).ToArray(), strict.Exclusions.Select(item =>
                                new ConfiguredExclusion(item.Path, item.Reason)).ToArray());
            }
        }
        catch (Exception ex) when (ex is ArgumentException or JsonException or NullReferenceException)
        {
            throw new CheckConfigurationException($"Strict configuration validation failed: {ex.Message}", ex);
        }
        Document document;
        try
        {
            document = JsonSerializer.Deserialize<Document>(capturedBytes,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ??
                throw new ArgumentException("mutate4csharp.json must contain a JSON object.");
        }
        catch (JsonException ex)
        {
            throw new ArgumentException($"Invalid mutate4csharp.json: {ex.Message}", ex);
        }
        if (document.Version != 1) throw new ArgumentException("mutate4csharp.json requires version 1.");
        var suitePaths = (document.TestSuites ?? []).Where(item => !string.IsNullOrWhiteSpace(item.Id))
            .ToDictionary(item => item.Id!, item => Normalize(item.Path, "test suite"), StringComparer.Ordinal);
        var projects = new List<ConfiguredProject>();
        foreach (var item in document.Projects ?? [])
        {
            var project = Normalize(item.Project, "project");
            if (!project.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException($"Configured project must be a .csproj: {project}");
            var tests = (item.Tests ?? []).Select(value => Normalize(value, "tests"))
                .Concat((item.TestSuites ?? []).Select(id => suitePaths.TryGetValue(id, out var path) ? path :
                    throw new ArgumentException($"Configured project {project} maps unknown test suite: {id}")))
                .Distinct(StringComparer.Ordinal).ToArray();
            if (tests.Length == 0)
                throw new ArgumentException($"Configured project {project} requires at least one test suite.");
            if (tests.Any(test => !test.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) &&
                                  !test.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) &&
                                  !test.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException($"Configured tests for {project} must be project or solution paths.");
            var sources = (item.Sources ?? []).Select(value => Normalize(value, "sources"))
                .OrderBy(value => value, StringComparer.Ordinal).ToArray();
            projects.Add(new(project, tests.OrderBy(value => value, StringComparer.Ordinal).ToArray(), sources));
        }
        var duplicate = projects.GroupBy(item => item.Project, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null) throw new ArgumentException($"Duplicate configured project: {duplicate.Key}");
        return new(fullRoot, document.IncludeGenerated, document.IncludeTests,
            projects.OrderBy(item => item.Project, StringComparer.Ordinal).ToArray(), []);

        static string Normalize(string? value, string member)
        {
            if (string.IsNullOrWhiteSpace(value))
                throw new ArgumentException($"Configured {member} path cannot be empty.");
            var normalized = value.Replace('\\', '/');
            if (Path.IsPathRooted(normalized) || normalized.Split('/').Any(part => part is "" or "." or ".."))
                throw new ArgumentException($"Configured {member} path must be repository-relative: {value}");
            return normalized;
        }
    }
}
