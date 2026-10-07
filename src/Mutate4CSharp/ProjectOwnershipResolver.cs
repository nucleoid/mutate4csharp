namespace Mutate4CSharp;

internal static class ProjectOwnershipResolver
{
    public static OwnershipResolution Resolve(ScopeConfiguration configuration,
        IReadOnlyList<string> productionPaths)
    {
        var reasons = new List<EvaluationReason>();
        var units = new Dictionary<string, (ConfiguredProject Project, SortedSet<string> Inputs)>(StringComparer.Ordinal);
        foreach (var input in productionPaths.Distinct(StringComparer.Ordinal).OrderBy(path => path, StringComparer.Ordinal))
        {
            var candidates = configuration.Projects.Where(project => Owns(input, project)).ToArray();
            if (candidates.Length == 0)
                candidates = configuration.Projects.Where(project => project.Tests.Any(test =>
                    IsUnderTestProject(input, test))).ToArray();
            if (candidates.Length == 0)
            {
                reasons.Add(new("UNMAPPED_PROJECT",
                    $"No configured production project owns {input}; add it to mutate4csharp.json."));
                continue;
            }
            if (candidates.Length > 1)
            {
                var candidateNames = candidates.Select(item => item.Project).OrderBy(item => item,
                    StringComparer.Ordinal).ToArray();
                var shown = candidateNames.Take(20).ToArray();
                var remainder = candidateNames.Length > shown.Length ?
                    $", +{candidateNames.Length - shown.Length} more" : string.Empty;
                reasons.Add(new("AMBIGUOUS_PROJECT_OWNERSHIP",
                    $"{input} is owned by multiple configured projects: {string.Join(", ", shown)}{remainder}. Use non-overlapping project roots."));
                continue;
            }
            var candidate = candidates[0];
            if (!units.TryGetValue(candidate.Project, out var unit))
                unit = (candidate, new(StringComparer.Ordinal));
            unit.Inputs.Add(input);
            units[candidate.Project] = unit;
        }
        return new(units.Values.OrderBy(unit => unit.Project.Project, StringComparer.Ordinal)
                .Select(unit => new ProjectScopeUnit(unit.Project.Project, unit.Project.Tests,
                    unit.Inputs.ToArray(), "CHANGED_DECLARATIONS")).ToArray(),
            reasons.OrderBy(reason => reason.Code, StringComparer.Ordinal)
                .ThenBy(reason => reason.Message, StringComparer.Ordinal).ToArray());
    }

    internal static bool IsUnderProject(string path, string project)
    {
        var directory = project.Contains('/') ? project[..project.LastIndexOf('/')] : string.Empty;
        return directory.Length == 0 || path.StartsWith(directory + "/", StringComparison.Ordinal);
    }

    internal static bool IsUnderTestProject(string path, string testProject)
    {
        if (!testProject.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)) return false;
        var separator = testProject.LastIndexOf('/');
        if (separator < 0) return false;
        var directory = testProject[..separator];
        return path.StartsWith(directory + "/", StringComparison.Ordinal);
    }

    private static bool Owns(string path, ConfiguredProject project) => project.Sources.Count == 0
        ? IsUnderProject(path, project.Project)
        : project.Sources.Any(pattern => GlobMatches(path, pattern));

    internal static bool GlobMatches(string path, string pattern)
    {
        var expression = new System.Text.StringBuilder("^");
        for (var index = 0; index < pattern.Length; index++)
        {
            var character = pattern[index];
            if (character == '*' && index + 1 < pattern.Length && pattern[index + 1] == '*')
            {
                index++;
                if (index + 1 < pattern.Length && pattern[index + 1] == '/')
                {
                    index++;
                    expression.Append("(?:.*/)?");
                }
                else expression.Append(".*");
            }
            else if (character == '*') expression.Append("[^/]*");
            else if (character == '?') expression.Append("[^/]");
            else expression.Append(System.Text.RegularExpressions.Regex.Escape(character.ToString()));
        }
        expression.Append('$');
        return System.Text.RegularExpressions.Regex.IsMatch(path, expression.ToString(),
            System.Text.RegularExpressions.RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(1));
    }
}
