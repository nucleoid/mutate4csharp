#if MUTATE_RESOLVED_GRAPH_WORKER
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace Mutate4CSharp;

// Private same-source worker entry. Its output is DATA which the creating parent
// must join to its own actual capture, immutable request and original process.
internal static class ResolvedGraphWorkerProgram
{
    private const int MaxRequestBytes = 16 * 1024 * 1024;
    internal static int Main(string[] args)
    {
        if (args.Length != 7 || !Path.IsPathFullyQualified(args[0]) || !Path.IsPathFullyQualified(args[1]) ||
            args[2].Length != 64 || args[2].Any(c => !char.IsAsciiHexDigitLower(c))) return 1;
        var numeric = new long[4];
        for (var index = 0; index < numeric.Length; ++index)
            if (args[index + 3].Length is 0 or > 19 || args[index + 3].Any(c => c is < '0' or > '9') ||
                !long.TryParse(args[index + 3], System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out numeric[index])) return 1;
        var observedBudget = new ResolvedGraphBudgetObservation(numeric[0], numeric[1], numeric[2], numeric[3], null);
        var originalBudget = new ResolvedGraphBudget(observedBudget, CancellationToken.None);
        using var inputs = new ResolvedGraphInputs(originalBudget);
        var input = inputs.Read(args[0], "config", MaxRequestBytes);
        if (input.Bytes.Length > MaxRequestBytes || input.Sha256 != args[2]) return 1;
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, MaxDepth = 64 };
        var request = JsonSerializer.Deserialize<GraphWorkerRequest>(input.Bytes, options) ??
            throw new EvaluationContractException("Complete private graph worker request required.");
        if (request.Version != 1 || request.Projects.Length is < 1 or > ResolvedGraphCapture.MaxContexts ||
            request.CapturedPaths.Length is < 1 or > ResolvedGraphInputs.MaximumFiles ||
            request.CapturedPaths.Distinct(StringComparer.Ordinal).Count() != request.CapturedPaths.Length)
            throw new EvaluationContractException("Private worker request membership/version limit.");
        if (request.OriginalBudget != observedBudget)
            throw new EvaluationContractException("Private worker request and pre-read original budget differ.");
        var configuration = CheckConfiguration.Load(request.Root, request.ConfigurationBytes);
        if (!configuration.IsResolvedGraph) throw new EvaluationContractException("No implicit legacy worker profile.");
        CheckLoadedCompiler(request.Compiler, inputs);
        var contexts = new Dictionary<string, ResolvedCompilerContext>(StringComparer.Ordinal);
        foreach (var project in configuration.Projects)
        {
            var actual = request.Projects.Where(c => c.Selection.Project == project.Project &&
                c.Selection.Framework == project.TargetFramework && c.Selection.Configuration == project.Configuration).ToArray();
            if (actual.Length != 1) throw new EvaluationContractException("Exactly one original compiler context per configured production selection required.");
            contexts.Add(project.Project, BuildContext(request.Root, project, configuration, actual[0], inputs));
        }
        var result = StrictMutationEnumerator.EnumerateCaptured(configuration, request.Scope,
            request.CapturedPaths, contexts, CancellationToken.None);
        CheckLoadedCompiler(request.Compiler, inputs);
        inputs.Recheck(input);
        var output = JsonSerializer.SerializeToUtf8Bytes(result);
        if (output.Length > 32 * 1024 * 1024) throw new SnapshotLimitException("Complete worker result exceeds finite output bound.");
        RequireWithin(request.Root, args[1]);
        if (Path.GetFileName(args[1]) != "resolved-graph-result.json")
            throw new EvaluationContractException("Fixed owned graph result leaf required.");
        inputs.WriteNewResult(args[1], output);
        return result.IsComplete ? 0 : 1;
    }

    private static void CheckLoadedCompiler(GraphCompilerImages compiler, ResolvedGraphInputs inputs)
    {
        if (compiler.SdkVersion.Length > 128 || compiler.CommonSha256.Length != 64 || compiler.CSharpSha256.Length != 64)
            throw new EvaluationContractException("Exact selected compiler deployment identity required.");
        foreach (var pair in new[] { (typeof(Compilation).Assembly, compiler.CommonSha256),
                     (typeof(CSharpCompilation).Assembly, compiler.CSharpSha256) })
        {
            var image = inputs.Read(pair.Item1.Location, "compiler");
            if (image.Sha256 != pair.Item2) throw new EvaluationContractException("Loaded compiler deployment differs from original selected compiler.");
        }
        var sdk = typeof(ResolvedGraphWorkerProgram).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .SingleOrDefault(a => a.Key == "MutateSelectedSdk")?.Value;
        if (sdk != compiler.SdkVersion)
            throw new EvaluationContractException("Worker build selection differs from actual graph SDK selection.");
    }

    private static ResolvedCompilerContext BuildContext(string root, CheckProject project,
        CheckConfiguration configuration, ResolvedProjectCapture actual, ResolvedGraphInputs inputs)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(project.Project, root))!;
        var originalTask = actual.FirstCsc ?? throw new EvaluationContractException("Original first-Csc capture is required; later synthetic/query-only tokens are insufficient.");
        originalTask.RequireObservedExecutionSwitch();
        var tokens = actual.Items["CscCommandLineArgs"].Select(item => item.Identity).ToArray();
        inputs.CheckWork();
        ResolvedGraphTokenPreflight.RequireClosedParserInputs(tokens);
        inputs.CheckWork();
        var parsed = CSharpCommandLineParser.Default.Parse(tokens, directory, sdkDirectory: null);
        inputs.CheckWork();
        if (parsed.Errors.Any(d => d.Severity == DiagnosticSeverity.Error))
            throw new EvaluationContractException("Actual selected Csc tokens did not parse in the same compiler.");
        if (parsed.CompilationOptions.CryptoKeyFile is not null || parsed.CompilationOptions.CryptoKeyContainer is not null ||
            parsed.CompilationOptions.PublicSign || parsed.CompilationOptions.DelaySign == true)
            throw new EvaluationContractException("Signing inputs require a separately closed compiler-source effect profile.");
        if (project.HasLanguageVersionAssertion && parsed.ParseOptions.LanguageVersion != LanguageVersion.CSharp14 ||
            project.Nullable is not null && !parsed.CompilationOptions.NullableContextOptions.ToString().Equals(project.Nullable, StringComparison.OrdinalIgnoreCase) ||
            project.DefineConstants is not null && !project.DefineConstants.Order(StringComparer.Ordinal)
                .SequenceEqual(parsed.ParseOptions.PreprocessorSymbolNames.Order(StringComparer.Ordinal), StringComparer.Ordinal))
            throw new EvaluationContractException("Evaluated compiler options violate explicit configured assertions.");
        var sourceRows = actual.Items["Compile"];
        var compilerPaths = parsed.SourceFiles.Select(s => Path.GetFullPath(s.Path, directory)).ToArray();
        var compilePaths = sourceRows.Select(item => Path.GetFullPath(item.Identity, directory)).ToArray();
        if (compilePaths.Distinct(PathComparer).Count() != compilePaths.Length ||
            !compilePaths.Order(PathComparer).SequenceEqual(compilerPaths.Order(PathComparer), PathComparer))
            throw new EvaluationContractException("Csc token sources differ from complete actual Compile inventory.");
        var retained = new List<ResolvedGraphFile>();
        var trees = new Dictionary<string, SyntaxTree>(StringComparer.Ordinal);
        var sourceTexts = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var path in compilerPaths) AddSource(path, false);
        var generated = actual.Properties["CompilerGeneratedFilesOutputPath"];
        if (generated.Length > 0)
        {
            var generatedRoot = Path.GetFullPath(generated, directory);
            RequireWithin(root, generatedRoot);
            if (inputs.GeneratedDirectoryPresent(generatedRoot))
            {
                var queue = new Queue<(string Path, int Depth)>(); queue.Enqueue((generatedRoot, 0));
                var count = 0;
                while (queue.Count > 0)
                {
                    var (parent, depth) = queue.Dequeue();
                    if (depth > 24) throw new SnapshotLimitException("Generated source traversal depth exceeded.");
                    foreach (var entry in inputs.EnumerateGeneratedDirectory(parent))
                    {
                        if (++count > 8192) throw new SnapshotLimitException("Complete generated entry limit exceeded.");
                        inputs.CheckWork();
                        var path = entry.Path;
                        var attributes = entry.Attributes;
                        if ((attributes & FileAttributes.ReparsePoint) != 0) throw new SnapshotCaptureException("Generated source redirect refused.");
                        if ((attributes & FileAttributes.Directory) != 0) queue.Enqueue((path, depth + 1));
                        else if (path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) AddSource(path, true);
                        else throw new SnapshotCaptureException("Unaccounted generated-output entry.");
                    }
                }
            }
        }
        // Bind every effectful compiler input category to its actual token and
        // evaluated item inventories before semantic work. Analyzer images and
        // additional files are read as complete immutable bytes, never loaded.
        var configurations = new List<(string Path, string Text)>();
        CaptureAuxiliary(parsed.AnalyzerConfigPaths, "EditorConfigFiles", "config", true);
        CaptureAuxiliary(parsed.AnalyzerReferences.Select(a => a.FilePath), "Analyzer", "analyzer", false);
        CaptureAuxiliary(parsed.AdditionalFiles.Select(a => a.Path), "AdditionalFiles", "source", false);
        foreach (var embedded in actual.Items["EmbeddedFiles"])
        {
            inputs.CheckWork();
            var file = inputs.Read(Path.GetFullPath(embedded.Identity, directory), "source");
            retained.Add(file);
        }
        var treeOptions = new ResolvedGraphTreeOptions(inputs, configurations, trees.Values);
        var references = new List<MetadataReference>();
        var referencePaths = new List<string>();
        foreach (var reference in parsed.MetadataReferences)
        {
            var path = Path.GetFullPath(reference.Reference, directory);
            var matches = actual.Items["ReferencePathWithRefAssemblies"].Where(item =>
                PathComparer.Equals(Path.GetFullPath(item.Identity, directory), path)).ToArray();
            if (matches.Length != 1) throw new EvaluationContractException("Csc reference does not uniquely join resolved graph reference output.");
            var file = inputs.Read(path, "reference"); retained.Add(file); referencePaths.Add(path);
            references.Add(MetadataReference.CreateFromImage(file.Bytes, reference.Properties, filePath: path));
        }
        if (referencePaths.Distinct(PathComparer).Count() != actual.Items["ReferencePathWithRefAssemblies"].Count)
            throw new EvaluationContractException("Complete graph reference inventory differs from actual Csc references.");
        // A provider-free first assembly pass cannot acquire signing keyfiles via
        // aliased source assembly attributes. Reject those effects before normal
        // same-options diagnostics/semantic queries; never rewrite consumer attrs.
        var safeOptions = parsed.CompilationOptions.WithSyntaxTreeOptionsProvider(treeOptions).WithStrongNameProvider(null)
            .WithMetadataReferenceResolver(null).WithSourceReferenceResolver(null).WithXmlReferenceResolver(null);
        inputs.CheckWork();
        var compilation = CSharpCompilation.Create(parsed.CompilationName, trees.Values, references, safeOptions);
        inputs.CheckWork();
        var signingAttributes = new HashSet<string>(StringComparer.Ordinal) {
            "System.Reflection.AssemblyKeyFileAttribute", "System.Reflection.AssemblyKeyNameAttribute",
            "System.Reflection.AssemblyDelaySignAttribute" };
        if (compilation.Assembly.GetAttributes().Any(a => a.AttributeClass is null ||
                signingAttributes.Contains(a.AttributeClass.ToDisplayString())))
            throw new EvaluationContractException("Source assembly signing/unknown attribute effect is unsupported.");
        if (compilation.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error))
            throw new EvaluationContractException("Captured complete selected-compiler context has semantic errors.");
        foreach (var file in retained) inputs.Recheck(file);
        var owned = sourceTexts.Keys.Where(path => project.Sources.Any(pattern =>
            ProjectOwnershipResolver.GlobMatches(path, pattern))).Order(StringComparer.Ordinal).ToArray();
        var excluded = owned.Where(path => configuration.Exclusions.Any(e => ProjectOwnershipResolver.GlobMatches(path, e.Path)) ||
            ScopePlanner.IsGeneratedSource(path, sourceTexts[path]) ||
            ScopePlanner.IsTestSource(path, configuration.TestSuites.Select(s => s.Path))).ToHashSet(StringComparer.Ordinal);
        var identity = MutationIdentity.ComputeDigest("resolved-compiler-context-v1", new[] {
            ("project", project.Project), ("framework", project.TargetFramework), ("configuration", project.Configuration!),
            ("parse", project.ParseContext), ("completeActualQuery", actual.RawSha256),
            ("completeInputs", MutationIdentity.ComputeDigest("graph-physical-inputs", retained.Select((f, i) =>
                ("input-" + i.ToString("D8", System.Globalization.CultureInfo.InvariantCulture), f.Path + "|" + f.Sha256)).ToArray())) });
        return new(project.TargetFramework, project.Configuration!, owned, excluded, sourceTexts, trees, compilation, identity);

        void CaptureAuxiliary(IEnumerable<string> tokenPaths, string itemName, string role, bool text)
        {
            inputs.CheckWork();
            var paths = tokenPaths.Select(p => Path.GetFullPath(p, directory)).ToArray();
            var items = actual.Items[itemName].Select(p => Path.GetFullPath(p.Identity, directory)).ToArray();
            if (paths.Length > 4096 || paths.Distinct(PathComparer).Count() != paths.Length ||
                !paths.Order(PathComparer).SequenceEqual(items.Order(PathComparer), PathComparer))
                throw new EvaluationContractException("Actual compiler token/item membership differs: " + itemName);
            foreach (var path in paths)
            {
                inputs.CheckWork();
                var file = inputs.Read(path, role); retained.Add(file);
                if (text)
                {
                    inputs.CheckWork();
                    using var bytes = new MemoryStream(file.Bytes, writable: false);
                    var source = SourceText.From(bytes, encoding: null, parsed.ChecksumAlgorithm);
                    inputs.CheckWork();
                    configurations.Add((path, source.ToString()));
                    inputs.CheckWork();
                }
            }
        }
        void AddSource(string path, bool generatedSource)
        {
            path = Path.GetFullPath(path, directory); RequireWithin(root, path);
            var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
            if (trees.ContainsKey(relative)) throw new EvaluationContractException("Duplicate actual compiler/generated source.");
            var file = inputs.Read(path, generatedSource ? "generated-source" : "source"); retained.Add(file);
            using var bytes = new MemoryStream(file.Bytes, writable: false);
            inputs.CheckWork();
            var text = SourceText.From(bytes, parsed.Encoding, parsed.ChecksumAlgorithm);
            inputs.CheckWork();
            var tree = CSharpSyntaxTree.ParseText(text, parsed.ParseOptions, path);
            inputs.CheckWork();
            trees.Add(relative, tree); sourceTexts.Add(relative, text.ToString());
        }
    }
    private static void RequireWithin(string root, string path)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(path));
        if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new SnapshotCaptureException("Semantic source escapes actual owned graph clone.");
    }
    private static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}
internal sealed record GraphCompilerImages(string SdkVersion, string CommonSha256, string CSharpSha256);
internal sealed record GraphWorkerRequest(int Version, string Root, byte[] ConfigurationBytes,
    ScopePlan Scope, string[] CapturedPaths, ResolvedProjectCapture[] Projects, GraphCompilerImages Compiler,
    ResolvedGraphBudgetObservation OriginalBudget);
#endif
