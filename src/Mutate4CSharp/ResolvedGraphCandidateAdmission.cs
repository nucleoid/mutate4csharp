using Microsoft.CodeAnalysis.Text;

namespace Mutate4CSharp;

// Worker output is DATA, including units which coverage may complete without
// ever entering the mutant writer. Recheck parent-owned scope and byte spans
// before any binding/coverage decision, under the same original input ledger.
internal static class ResolvedGraphCandidateAdmission
{
    internal static SnapshotFile ConfigurationFile(InputSnapshot snapshot)
    {
        var files = snapshot.Files.Where(f => f.Exists &&
            f.RelativePath.Equals("mutate4csharp.json", StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
        return files.Length == 1 ? files[0] : throw new EvaluationContractException("Exactly one captured strict configuration is required.");
    }

    internal static void Validate(InputSnapshot snapshot, CheckConfiguration configuration,
        ScopePlan scope, IReadOnlyList<MutationCandidate> candidates, ResolvedGraphInputs inputs)
    {
        var captured = snapshot.Files.Where(f => f.Exists).ToDictionary(f => f.RelativePath, StringComparer.Ordinal);
        var sources = new Dictionary<string, (ResolvedGraphFile File, string Text, SourceText Physical)>(StringComparer.Ordinal);
        var targets = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var declarations = new Dictionary<string, IReadOnlyList<TextSpan>>(StringComparer.Ordinal);
        foreach (var candidate in candidates)
        {
            inputs.CheckWork();
            if (candidate is null || candidate.Material is null || string.IsNullOrEmpty(candidate.Material.RepositoryPath))
                throw new EvaluationContractException("Worker candidate has no exact source material.");
            var path = candidate.Material.RepositoryPath;
            if (!captured.TryGetValue(path, out var capturedFile) ||
                !path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                throw new EvaluationContractException("Worker candidate is not an exact captured C# source.");
            var projects = configuration.Projects.Where(p => p.Project == candidate.ProjectPath &&
                p.TargetFramework == candidate.TargetFramework && p.ParseContext == candidate.ParseContext).Take(2).ToArray();
            if (projects.Length != 1) throw new EvaluationContractException("Worker candidate has no exact configured production selection.");
            var project = projects[0];
            var units = scope.ProjectUnits.Where(u => u.Project == project.Project).Take(2).ToArray();
            if (units.Length != 1 || !project.Sources.Any(pattern => ProjectOwnershipResolver.GlobMatches(path, pattern)) ||
                configuration.Exclusions.Any(e => ProjectOwnershipResolver.GlobMatches(path, e.Path)) ||
                scope.Exclusions.Any(e => !e.Overridden && e.Path == path) ||
                ScopePlanner.IsTestSource(path, configuration.TestSuites.Select(s => s.Path)))
                throw new EvaluationContractException("Worker candidate escapes configured source ownership or scope.");
            if (!targets.TryGetValue(project.Project, out var selected))
            {
                var owned = captured.Keys.Where(p => project.Sources.Any(pattern => ProjectOwnershipResolver.GlobMatches(p, pattern))).ToArray();
                selected = StrictMutationEnumerator.SelectTargetPaths(units[0], scope, owned).ToHashSet(StringComparer.Ordinal);
                targets.Add(project.Project, selected);
            }
            if (!selected.Contains(path)) throw new EvaluationContractException("Worker candidate is outside the parent target paths.");
            if (!sources.TryGetValue(path, out var source))
            {
                var file = inputs.Read(Path.Combine(snapshot.CaptureRoot, path.Replace('/', Path.DirectorySeparatorChar)), "source");
                if (file.Bytes.LongLength != capturedFile.Length || file.Sha256 != capturedFile.Sha256)
                    throw new SnapshotDivergedException("Candidate source differs from immutable snapshot: " + path);
                // String, physical lines, and declaration/generated parsing all
                // share the existing cumulative finite allocation allowance.
                inputs.ReserveAllocation(checked(8L * file.Bytes.LongLength + 8192));
                var text = ResolvedGraphTokenPreflight.DecodeSupportedSource(file.Bytes);
                if (ScopePlanner.IsGeneratedSource(path, text))
                    throw new EvaluationContractException("Worker candidate targets generated source.");
                source = (file, text, SourceText.From(text)); sources.Add(path, source);
            }
            if (candidate.SourceStart < 0 || candidate.SourceLength <= 0 ||
                candidate.SourceStart > source.Text.Length || candidate.SourceLength > source.Text.Length - candidate.SourceStart ||
                candidate.Original is null || candidate.Original.Length != candidate.SourceLength ||
                !source.Text.AsSpan(candidate.SourceStart, candidate.SourceLength).SequenceEqual(candidate.Original.AsSpan()))
                throw new EvaluationContractException("Worker candidate original span differs from the actual snapshot source.");
            var span = new TextSpan(candidate.SourceStart, candidate.SourceLength);
            var start = source.Physical.Lines.GetLinePosition(span.Start);
            var end = source.Physical.Lines.GetLinePosition(span.End);
            if (candidate.SourceLine != start.Line + 1 || candidate.SourceColumn != start.Character + 1 ||
                candidate.SourceEndLine != end.Line + 1 || candidate.SourceEndColumn != end.Character + 1)
                throw new EvaluationContractException("Worker candidate physical line/column differs from its actual source span.");
            if (units[0].Expansion == "CHANGED_DECLARATIONS")
            {
                if (!declarations.TryGetValue(path, out var spans))
                {
                    var files = scope.Files.Where(f => f.Path == path).Take(2).ToArray();
                    if (files.Length != 1) throw new EvaluationContractException("Parent declaration scope is missing or ambiguous.");
                    spans = DeclarationCatalog.ResolveSelectionSpans(path, source.Text, files[0].Declarations.Select(d => d.Id).ToArray());
                    declarations.Add(path, spans);
                }
                if (!spans.Any(s => s.Contains(span)))
                    throw new EvaluationContractException("Worker candidate escapes its parent declaration selection.");
            }
        }
        foreach (var source in sources.Values) inputs.Recheck(source.File);
    }

    internal static ResolvedProjectCapture[] ProductionContexts(CheckConfiguration configuration,
        IReadOnlyList<ResolvedProjectCapture> captured)
    {
        return configuration.Projects.Select(project =>
        {
            var matches = captured.Where(c => c.Selection.Project == project.Project &&
                c.Selection.Framework == project.TargetFramework && c.Selection.Configuration == project.Configuration).Take(2).ToArray();
            return matches.Length == 1 ? matches[0] : throw new EvaluationContractException("Exactly one actual context per configured production selection is required.");
        }).ToArray();
    }
}
