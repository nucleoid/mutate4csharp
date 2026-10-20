using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Mutate4CSharp;

// This is an actual producer, not a loader for caller-authored graph documents. The
// returned object remains tied to this owned clone until semantic analysis finishes.
internal sealed class ResolvedGraphCapture : IAsyncDisposable
{
    internal const int MaxContexts = 256;
    internal const int MaxItems = 32_768;
    internal const int MaxQueryBytes = 16 * 1024 * 1024;
    private readonly SnapshotClone _clone;
    private readonly OwnedPackageCache _packages;
    private readonly InputSnapshot _snapshot;
    private readonly ResolvedGraphBudget _budget;
    private readonly ResolvedGraphInputs _inputs;
    private readonly List<ResolvedProjectCapture> _projects = [];
    private readonly ResolvedGraphLoggerSelection? _logger;
    private readonly List<OwnedDirectory> _producerDirectories = [];
    private readonly List<ResolvedGraphTaskData> _actualTaskFiles = [];
    private readonly List<ProcessTreeResult> _actualProducerReturns = [];
    private readonly Dictionary<ResolvedProjectSelection, ResolvedGraphObservedTask> _firstTasks = [];
    private bool _disposed;
    private bool _disposalReturned;
    private Exception? _disposalFailure;
    private System.Runtime.ExceptionServices.ExceptionDispatchInfo? _validationFailure;

    private ResolvedGraphCapture(InputSnapshot snapshot, SnapshotClone clone, OwnedPackageCache packages,
        ResolvedGraphBudget budget, ResolvedGraphLoggerSelection? logger)
    {
        _snapshot = snapshot; _clone = clone; _packages = packages;
        _budget = budget; _inputs = new ResolvedGraphInputs(budget); _logger = logger;
    }

    internal string Root => _clone.Root;
    internal string PackageRoot => _packages.Root;
    internal ResolvedGraphBudgetObservation ObserveOriginalBudget() => _budget.Observe();
    internal IReadOnlyList<ResolvedProjectCapture> Projects => _projects;

    internal static async Task<ResolvedGraphCapture> CreateAsync(InputSnapshot snapshot,
        CheckConfiguration configuration, FrozenExecutionEnvironment environment,
        TimeSpan timeout, CancellationToken cancellationToken, ResolvedGraphLoggerSelection? logger = null,
        ResolvedGraphBudget? actualOriginal = null)
    {
        if (!configuration.IsResolvedGraph || timeout <= TimeSpan.Zero)
            throw new ArgumentException("Resolved graph capture requires the explicit versioned profile and a live original deadline.");
        var budget = actualOriginal ?? new ResolvedGraphBudget(timeout, cancellationToken);
        budget.CheckWork();
        if (logger is null || environment.SdkVersion != logger.SdkVersion)
            throw new EvaluationContractException("Actual selected graph logger and restored SDK profile must match before graph effects.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        cancellationToken = deadline.Token;
        SnapshotClone? clone = null;
        OwnedPackageCache? packages = null;
        ResolvedGraphCapture? capture = null;
        try
        {
            budget.CheckWork();
            clone = await SnapshotWorkspace.CreateCloneAsync(snapshot, "resolved-graph", cancellationToken);
            budget.CheckWork();
            packages = await environment.CreateWorkerPackageCacheAsync("resolved-graph", cancellationToken);
            budget.CheckWork();
            capture = new ResolvedGraphCapture(snapshot, clone, packages, budget, logger);
            var pending = new Queue<ResolvedProjectSelection>(configuration.Projects.Select(project =>
                new ResolvedProjectSelection(project.Project, project.TargetFramework,
                    project.Configuration ?? throw new EvaluationContractException("Graph production configuration is absent."))));
            foreach (var suite in configuration.ExecutionSuites)
                pending.Enqueue(new(suite.Path, suite.Framework, suite.Configuration));
            var seen = new HashSet<ResolvedProjectSelection>();
            while (pending.Count > 0)
            {
                budget.CheckWork();
                var selection = pending.Dequeue();
                if (!seen.Add(selection)) continue;
                if (seen.Count > MaxContexts)
                    throw new SnapshotLimitException("Resolved graph exceeds the complete context limit.");
                var remaining = budget.RemainingWork;
                if (remaining <= TimeSpan.Zero) throw new TimeoutException("Resolved graph capture reached its original deadline.");
                try
                {
                    await environment.RestoreWorkerAsync(clone, selection.Project, packages, remaining,
                        cancellationToken, selection.Framework, selection.Configuration, restoreAuthoredFrameworkGraph: true);
                }
                catch (SnapshotCaptureException failure) when (failure.Data["FrozenWorkerRestore.ActualReturn"] is ProcessTreeResult)
                {
                    // Failure DATA only, after the original nonzero restore returned.
                    // No new evaluation, retry, property change or lock adoption.
                    throw capture.WithPreparedLockDiagnostic(environment, selection, failure);
                }
                remaining = budget.RemainingWork;
                if (remaining <= TimeSpan.Zero) throw new TimeoutException("Resolved graph capture reached its original deadline.");
                var actual = await capture.CompileAsync(selection, remaining, cancellationToken);
                capture._projects.Add(actual);
                foreach (var edge in actual.Edges) pending.Enqueue(edge.Selection);
            }
            await snapshot.ValidateOriginalAsync(cancellationToken);
            capture.ValidateAuthoredInputs();
            return capture;
        }
        catch (Exception primary)
        {
            budget.BeginFailureCleanup();
            try { if (capture is not null) await capture.DisposeAsync(); else await AsyncDisposal.DisposeAllAsync([packages, clone]); }
            catch (Exception close) { throw new SnapshotCleanupException(primary, close); }
            throw;
        }
    }

    // Initial single-framework compilation uses authored evaluation. A later
    // direct query may only replay this exact actual first context's global TFM.
    // The complete comparer below still compares every observed global property.
    internal static IReadOnlyList<string> CompilerFrameworkArguments(ResolvedGraphObservedTask? first, string requestedFramework)
    {
        if (first is null || !first.GlobalProperties.TryGetValue("TargetFramework", out var framework)) return [];
        if (framework != requestedFramework)
            throw new EvaluationContractException("Retained actual compiler framework differs from requested selection.");
        return ["-p:TargetFramework=" + framework];
    }

    internal static SnapshotCaptureException CreateCompilerFailure(ProcessTreeResult actualReturn,
        string? sourceRoot = null, string? packageRoot = null, string? privateRoot = null)
    {
        // Format already-returned bounded DATA only. Prefer the logger's
        // original inner cause over a consequential shutdown error or the
        // diagnostic verbosity's potentially enormous command-line preamble.
        var streams = new[] { actualReturn.StandardError, actualReturn.StandardOutput };
        string? selected = null;
        foreach (var (marker, skip) in new[] { ("---> Mutate4CSharp.", 5), ("\nMutate4CSharp.", 1), (": error ", 2), ("Logger error ", 0) })
        {
            foreach (var stream in streams)
            {
                var start = stream.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                if (start < 0) continue;
                start += skip;
                var end = stream.IndexOfAny(['\r', '\n'], start);
                if (end < 0) end = stream.Length;
                selected = SafeDiagnostic(stream.AsSpan(start, end - start));
                break;
            }
            if (selected is not null) break;
        }
        selected ??= $"no explicit error line; stdoutChars={actualReturn.StandardOutput.Length}; stderrChars={actualReturn.StandardError.Length}; raw streams withheld";
        const string prefix = "Resolved graph compiler producer failed: ";
        var failure = new SnapshotCaptureException(prefix + EvaluationTextBounds.Prefix(
            $"exit={actualReturn.ExitCode}; timedOut={actualReturn.TimedOut}; {selected}", EvaluationEvidence.MaxDiagnosticLength - prefix.Length));
        failure.Data["ResolvedGraphCompiler.ActualReturn"] = actualReturn;
        return failure;

        string SafeDiagnostic(ReadOnlySpan<char> text)
        {
            // Redact before bounding, including a root crossing the output
            // boundary. Scan the returned DATA without copying a long raw line.
            var roots = new[] { (Root: sourceRoot, Role: ResolvedGraphPathProfile.Source),
                (Root: packageRoot, Role: ResolvedGraphPathProfile.Packages), (Root: privateRoot, Role: "/_/mutate-private") }
                .Where(row => !string.IsNullOrEmpty(row.Root))
                .SelectMany(row => new[] { (Root: row.Root!.TrimEnd('\\', '/'), row.Role),
                    (Root: row.Root!.TrimEnd('\\', '/').Replace('\\', '/'), row.Role) })
                .OrderByDescending(row => row.Root.Length).ToArray();
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            var safe = new StringBuilder();
            for (var index = 0; index < text.Length && safe.Length < EvaluationEvidence.MaxDiagnosticLength;)
            {
                if (text[index..].StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
                    text[index..].StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                {
                    safe.Append("[source-uri]");
                    while (index < text.Length && !char.IsWhiteSpace(text[index])) index++;
                    continue;
                }
                var matched = false;
                foreach (var row in roots)
                    if (row.Root.Length > 0 && text[index..].StartsWith(row.Root, comparison))
                    {
                        safe.Append(row.Role); index += row.Root.Length; matched = true; break;
                    }
                if (!matched) safe.Append(text[index++]);
            }
            return EvaluationTextBounds.Prefix(safe.ToString(), EvaluationEvidence.MaxDiagnosticLength);
        }
    }

    private async Task<ResolvedProjectCapture> CompileAsync(ResolvedProjectSelection selection,
        TimeSpan remaining, CancellationToken cancellationToken)
    {
        var project = CapturedProject(selection.Project);
        var logger = _logger ?? throw new EvaluationContractException("Actual selected logger is absent.");
        _budget.CheckWork();
        var images = logger.ReadActual(_inputs);
        var taskDefinition = _inputs.Read(Path.Combine(Path.GetDirectoryName(logger.CscTask.Path)!, "Microsoft.CSharp.Core.targets"), "import");
        _budget.CheckWork();
        if (_producerDirectories.Count >= MaxContexts)
            throw new SnapshotLimitException("Complete compiler producer attempt limit exceeded.");
        var files = OwnedDirectory.Create(OwnedDirectory.PrivateParent("graph-capture"), "csc");
        _producerDirectories.Add(files);
        _budget.CheckWork();
        var requestPath = Path.Combine(files.Root, "resolved-graph-csc-request.json");
        var queryPath = Path.Combine(files.Root, "resolved-graph-query.json");
        var childGrant = _budget.Resources.ReserveChild("logger");
        var request = JsonSerializer.SerializeToUtf8Bytes(new
        {
            Version = 4, Root = files.Root, SourceRoot = Root, PackageRoot = _packages.Root, OriginalBudget = _budget.Observe(), Grant = childGrant,
            Compiler = new GraphCompilerImages(logger.SdkVersion,
                logger.LoggerClosure.Single(i => Path.GetFileName(i.Path) == "Microsoft.CodeAnalysis.dll").Sha256,
                logger.LoggerClosure.Single(i => Path.GetFileName(i.Path) == "Microsoft.CodeAnalysis.CSharp.dll").Sha256),
            PermitEmptyTasks = _firstTasks.ContainsKey(selection)
        });
        _budget.CheckWork();
        _inputs.WriteNewResult(requestPath, request);
        var requestFile = _inputs.Read(requestPath, "config", 1_048_576);
        var origin = _budget.Observe();
        _budget.CheckWork();
        if (File.Exists(queryPath) || Directory.Exists(queryPath))
            throw new EvaluationContractException("Fresh owned compiler query result leaf required; no existing output adoption.");
        _budget.CheckWork();
        var parameters = string.Join('|', requestPath, requestFile.Sha256,
            origin.Original.ToString(System.Globalization.CultureInfo.InvariantCulture),
            origin.Frequency.ToString(System.Globalization.CultureInfo.InvariantCulture),
            origin.WholeTicks.ToString(System.Globalization.CultureInfo.InvariantCulture),
            origin.WorkTicks.ToString(System.Globalization.CultureInfo.InvariantCulture),
            childGrant.IoBytes.ToString(System.Globalization.CultureInfo.InvariantCulture), childGrant.Nonce);
        // One same-owner actual compiler invocation. Later/up-to-date queries
        // may use only a task retained from an earlier ACTUAL producer in this
        // same capture, never a rebuild or synthetic command-line fallback.
        var arguments = new[] { "msbuild", project, "-target:Compile", "-nologo", "-verbosity:diagnostic", "-noconsolelogger",
            "-m:1", "-nodeReuse:false", "-p:UseSharedCompilation=false",
            "-p:ProvideCommandLineArgs=true", "-p:EmitCompilerGeneratedFiles=true", "-p:SkipCompilerExecution=false",
            "-p:Configuration=" + selection.Configuration, ResolvedGraphPathProfile.Argument(Root, _packages.Root),
            "-logger:Mutate4CSharp.ResolvedGraphCscLogger," + logger.LoggerPath + ";" + parameters,
            // Selected MSBuild intentionally suppresses all user loggers when
            // query results go to stdout. A separate fixed owned result file
            // keeps this SAME actual compilation's logger active; no rebuild.
            "-getResultOutputFile:" + queryPath,
            "-getProperty:" + string.Join(',', ResolvedProjectCapture.PropertyNames),
            "-getItem:" + string.Join(',', ResolvedProjectCapture.ItemNames) }
            .Concat(CompilerFrameworkArguments(_firstTasks.TryGetValue(selection, out var firstActual) ? firstActual : null, selection.Framework)).ToArray();
        _budget.CheckWork();
        var run = await ProcessTree.RunAsync(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet",
            arguments, Root, remaining < _budget.RemainingWork ? remaining : _budget.RemainingWork,
            cancellationToken, ExecutionEnvironment.ProcessEnvironment(_packages.Root),
            MaxQueryBytes, requireLinuxSessionIsolation: true);
        _actualProducerReturns.Add(run); // DATA; ProcessTree owns its actual process.
        _budget.CheckWork();
        if (run.TimedOut || run.ExitCode != 0)
            throw CreateCompilerFailure(run, Root, _packages.Root, Path.GetDirectoryName(files.Root));
        foreach (var image in images) _inputs.Recheck(image);
        _inputs.Recheck(taskDefinition);
        _inputs.Recheck(requestFile);
        var actualTasks = ResolvedGraphTaskCapture.Read(Path.Combine(files.Root, "resolved-graph-csc-events.json"),
            requestFile.Sha256, _inputs, _firstTasks.ContainsKey(selection));
        _budget.Resources.ObserveChildReturn(childGrant, actualTasks.PreDelivery, actualTasks.OriginalFile.Bytes.LongLength);
        _actualTaskFiles.Add(actualTasks);
        var observedContexts = new HashSet<ResolvedProjectSelection>();
        foreach (var task in actualTasks.Tasks)
        {
            _budget.CheckWork();
            if (!task.EvaluatedProperties.TryGetValue("TargetFramework", out var framework) ||
                !task.EvaluatedProperties.TryGetValue("Configuration", out var configuration) ||
                !Path.GetFullPath(task.TaskAssemblyLocation).Equals(logger.CscTask.Path,
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            {
                // Failure-only pure-memory lookups leave successful nullable flow
                // identical to the original compiled guard, without assertions.
                var hasFramework = task.EvaluatedProperties.TryGetValue("TargetFramework", out var actualFramework);
                var hasConfiguration = task.EvaluatedProperties.TryGetValue("Configuration", out var actualConfiguration);
                var taskImageMatches = Path.GetFullPath(task.TaskAssemblyLocation).Equals(logger.CscTask.Path,
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
                throw new EvaluationContractException(JoinMismatchDiagnostic(task, selection, logger,
                    hasFramework, actualFramework, hasConfiguration, actualConfiguration, taskImageMatches));
            }
            if (!Path.GetFullPath(task.TaskFile).Equals(taskDefinition.Path,
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new EvaluationContractException("Custom Csc target/factory definition is outside the selected stock task profile.");
            var taskSelection = new ResolvedProjectSelection(SnapshotInputPolicy.NormalizeRelative(
                Path.GetRelativePath(Root, task.ProjectFile).Replace('\\', '/')), framework, configuration);
            if (task.ProjectInstanceId < 0 || !observedContexts.Add(taskSelection))
                throw new EvaluationContractException("Ambiguous or unavailable actual Csc project-instance context in one producer invocation.");
            if (!_firstTasks.ContainsKey(taskSelection) && _firstTasks.Count >= MaxContexts)
                throw new SnapshotLimitException("Complete first original compiler context limit exceeded.");
            if (_firstTasks.TryGetValue(taskSelection, out var retained))
            {
                if (retained.TaskAssemblyLocation != task.TaskAssemblyLocation || retained.TaskFile != task.TaskFile ||
                    !retained.Tokens.SequenceEqual(task.Tokens, StringComparer.Ordinal) ||
                    !retained.EvaluatedProperties.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                        .SequenceEqual(task.EvaluatedProperties.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)) ||
                    !retained.GlobalProperties.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                        .SequenceEqual(task.GlobalProperties.OrderBy(pair => pair.Key, StringComparer.Ordinal)))
                {
                    var failure = new SnapshotDivergedException(DescribeRetainedContextMismatch(retained, task));
                    failure.Data["ResolvedGraphContext.FirstActualTask"] = retained;
                    failure.Data["ResolvedGraphContext.LaterActualTask"] = task;
                    failure.Data["ResolvedGraphContext.RequestedSelection"] = selection;
                    failure.Data["ResolvedGraphContext.ActualReturn"] = run;
                    throw failure;
                }
            }
            else _firstTasks.Add(taskSelection, task);
        }
        if (!_firstTasks.TryGetValue(selection, out var first))
            throw new EvaluationContractException("No first actual Csc execution for the requested graph context.");
        var queryFile = _inputs.Read(queryPath, "config", MaxQueryBytes);
        var query = new UTF8Encoding(false, true).GetString(queryFile.Bytes);
        _budget.CheckWork();
        var captured = ResolvedProjectCapture.ReadActual(Root, selection, query, cancellationToken, first,
            _firstTasks.Values.ToArray(), actualTasks.Tasks);
        _inputs.Recheck(queryFile);
        if (captured.Properties["CscToolPath"].Length != 0 || captured.Properties["CscToolExe"].Length != 0)
            throw new EvaluationContractException("Custom Csc tool selection requires a separately complete compiler profile.");
        if (captured.Properties["CscEnvironment"].Length != 0)
            throw new EvaluationContractException("Nonempty CscEnvironment requires a complete actual child-environment profile.");
        if (captured.Properties["NETCoreSdkVersion"] != logger.SdkVersion)
            throw new EvaluationContractException("Actual compiler query SDK differs from the selected logger build profile.");
        _budget.CheckWork();
        return captured;
    }
    private SnapshotCaptureException WithPreparedLockDiagnostic(FrozenExecutionEnvironment environment,
        ResolvedProjectSelection selection, SnapshotCaptureException original)
    {
        string summary;
        object? diagnostic = null;
        try
        {
            _budget.CheckWork();
            var relativeProject = SnapshotInputPolicy.NormalizeRelative(selection.Project);
            var relativeLock = Path.Combine(Path.GetDirectoryName(relativeProject) ?? "", "packages.lock.json");
            var preparedRoot = Path.GetFullPath(environment.GraphRoot);
            var preparedPath = Path.GetFullPath(relativeLock, preparedRoot);
            var relation = Path.GetRelativePath(preparedRoot, preparedPath);
            if (relation == ".." || relation.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
                Path.IsPathFullyQualified(relation))
                throw new EvaluationContractException("Prepared lock diagnostic requires an owned relative project join.");
            // The existing original input owner supplies whole-read/EOF/SHA,
            // first-close, endpoint and independent recheck; cap is restrictive.
            var prepared = _inputs.Read(preparedPath, "config", 1_048_576);
            var targets = DescribePreparedLockTargets(prepared.Bytes);
            _budget.CheckWork(); _inputs.Recheck(prepared, 1_048_576); _budget.CheckWork();
            summary = "PreparedLock " + targets + "; sha=" + prepared.Sha256[..12] + "; ";
            diagnostic = new { Version = 1, DiagnosticOnly = true, PreparedLock = new {
                Relative = relation.Replace('\\', '/'), Bytes = prepared.Bytes.LongLength, prepared.Sha256 },
                Targets = targets, Requested = selection, RestoreGlobals = new[] {
                    "Configuration=" + selection.Configuration }, RestoreAuthoredFrameworkGraph = true,
                PreparationSuppliedFrameworkOrConfiguration = false };
        }
        catch (Exception diagnosticFailure)
        {
            // Retain the actual restore cause. A failed acquired input frame is
            // still latched in the original owner and audited by outer cleanup.
            original.Data["FrozenWorkerRestore.PreparedLockDiagnosticFailure"] = diagnosticFailure;
            summary = "PreparedLock diagnostic unavailable; ";
        }
        var failure = new SnapshotCaptureException(EvaluationTextBounds.Prefix(summary + original.Message,
            EvaluationEvidence.MaxDiagnosticLength), original);
        foreach (System.Collections.DictionaryEntry entry in original.Data) failure.Data[entry.Key] = entry.Value;
        if (diagnostic is not null)
            failure.Data["FrozenWorkerRestore.PreparedLockDiagnostic"] = JsonSerializer.Serialize(diagnostic);
        return failure;
    }

    // Formatting DATA, not validation or acceptance of a dependency lock. Only
    // bounded safe target labels are public; package names/values remain private.
    internal static string DescribePreparedLockTargets(byte[] completeBytes)
    {
        if (completeBytes.Length > 1_048_576) throw new SnapshotLimitException("Prepared lock diagnostic byte extent.");
        using var document = JsonDocument.Parse(completeBytes, new JsonDocumentOptions { MaxDepth = 16 });
        if (document.RootElement.ValueKind != JsonValueKind.Object ||
            document.RootElement.EnumerateObject().Count(field => field.Name == "dependencies") != 1 ||
            !document.RootElement.TryGetProperty("dependencies", out var dependencies) || dependencies.ValueKind != JsonValueKind.Object)
            throw new EvaluationContractException("Prepared lock diagnostic needs one target dictionary.");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in dependencies.EnumerateObject())
            if (names.Count >= 128 || field.Name.Length is 0 or > 128 || !names.Add(field.Name))
                throw new EvaluationContractException("Prepared lock diagnostic target membership extent.");
        var safe = names.OrderBy(value => value, StringComparer.Ordinal).Take(8).Select(value =>
            value.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' or ',' or '=' or '+') ? value : "[target-withheld]");
        return "targetCount=" + names.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) +
            "; targets=" + string.Join(',', safe);
    }

    // Local failure DATA only: no private absolute paths, token dump or new I/O.
    // Existing public diagnostic bounds remain unchanged; cause flags lead.
    private string JoinMismatchDiagnostic(ResolvedGraphObservedTask task, ResolvedProjectSelection selection,
        ResolvedGraphLoggerSelection logger, bool hasFramework, string? framework,
        bool hasConfiguration, string? configuration, bool taskImageMatches)
    {
        _budget.CheckWork();
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var requestedProject = Path.GetFullPath(selection.Project.Replace('/', Path.DirectorySeparatorChar), Root);
        var message = "Actual Csc context join refused: TargetFrameworkPresent=" + hasFramework +
            "; ConfigurationPresent=" + hasConfiguration + "; TaskImageMatches=" + taskImageMatches + "; " +
            JsonSerializer.Serialize(new
            {
                Version = 2, EventIdentity = task.EventIdentity, ContextSource = "ActualProjectStartedEvaluatedProperties",
                task.ProjectInstanceId, task.EvaluationId,
                ProjectMatchesRequested = task.ProjectFile.Equals(requestedProject, comparison),
                ActualProject = Fingerprint(task.ProjectFile), RequestedProject = Fingerprint(requestedProject),
                ActualFramework = Scalar(framework), RequestedFramework = Scalar(selection.Framework),
                ActualConfiguration = Scalar(configuration), RequestedConfiguration = Scalar(selection.Configuration),
                FrameworkMatchesRequested = hasFramework && framework == selection.Framework,
                ConfigurationMatchesRequested = hasConfiguration && configuration == selection.Configuration,
                ActualTaskImage = Fingerprint(task.TaskAssemblyLocation), SelectedTaskImage = Fingerprint(logger.CscTask.Path),
                TaskImageFileName = Scalar(Path.GetFileName(task.TaskAssemblyLocation)),
                TaskImageParentMatches = string.Equals(Path.GetDirectoryName(task.TaskAssemblyLocation),
                    Path.GetDirectoryName(logger.CscTask.Path), comparison),
                TaskImageFullyQualified = Path.IsPathFullyQualified(task.TaskAssemblyLocation),
                GlobalPropertyCount = task.GlobalProperties.Count
            });
        _budget.CheckWork();
        return message;

        static object Fingerprint(string value) => new
        {
            Characters = value.Length,
            Sha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant()
        };
        static object Scalar(string? value) => new
        {
            Present = value is not null, Characters = value?.Length,
            // Show only a complete short closed identifier, never a path or prefix.
            Identifier = value is { Length: <= 64 } && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-')
                ? value : null,
            Complete = value is null ? null : Fingerprint(value)
        };
    }
    // Failure-only bounded memory DATA. Complete original comparison above remains authoritative.
    // Values/tokens/private paths are retained locally, never rendered here.
    internal static string DescribeRetainedContextMismatch(ResolvedGraphObservedTask first, ResolvedGraphObservedTask later)
    {
        const int maximumCharacters = 16 * 1024 * 1024;
        if (first.Tokens.Count > MaxItems || later.Tokens.Count > MaxItems ||
            first.GlobalProperties.Count > 1024 || later.GlobalProperties.Count > 1024 ||
            first.EvaluatedProperties.Count > 3 || later.EvaluatedProperties.Count > 3)
            return "Later actual Csc context differs; diagnostic input exceeds accepted finite reader profile.";
        long characters = 0;
        foreach (var value in first.Tokens.Concat(later.Tokens)
                     .Concat(first.GlobalProperties.SelectMany(pair => new[] { pair.Key, pair.Value }))
                     .Concat(later.GlobalProperties.SelectMany(pair => new[] { pair.Key, pair.Value }))
                     .Concat(first.EvaluatedProperties.SelectMany(pair => new[] { pair.Key, pair.Value }))
                     .Concat(later.EvaluatedProperties.SelectMany(pair => new[] { pair.Key, pair.Value }))
                     .Concat(new[] { first.TaskAssemblyLocation, later.TaskAssemblyLocation, first.TaskFile, later.TaskFile }))
        {
            characters += value.Length;
            if (characters > maximumCharacters)
                return "Later actual Csc context differs; diagnostic characters exceed accepted finite reader profile.";
        }
        static object Digest(string value) => new { Characters = value.Length,
            Sha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant() };
        static string SafeKey(string? key) => key is { Length: > 0 and <= 64 } &&
            key.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '-') ? key : "[key-withheld]";
        static string[] DifferentKeys(IReadOnlyDictionary<string, string> a, IReadOnlyDictionary<string, string> b)
        {
            var left = a.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            var right = b.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            return left.Keys.Concat(right.Keys).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)
                .Where(key => !left.TryGetValue(key, out var av) || !right.TryGetValue(key, out var bv) || av != bv).ToArray();
        }
        static object Map(IReadOnlyDictionary<string, string> a, IReadOnlyDictionary<string, string> b, string[] changed)
        {
            var left = a.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            var right = b.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            return new { FirstCount = a.Count, LaterCount = b.Count, ChangedKeys = changed.Length,
                FirstComplete = Digest(JsonSerializer.Serialize(a.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToArray())),
                LaterComplete = Digest(JsonSerializer.Serialize(b.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToArray())),
                FirstEightChanges = changed.Take(8).Select(key => new { Key = SafeKey(key), KeyDigest = Digest(key),
                    FirstPresent = left.ContainsKey(key), LaterPresent = right.ContainsKey(key),
                    FirstValue = left.TryGetValue(key, out var av) ? Digest(av) : null,
                    LaterValue = right.TryGetValue(key, out var bv) ? Digest(bv) : null }).ToArray() };
        }
        static object? Token(IReadOnlyList<string> tokens, int index) => index < 0 || index >= tokens.Count ? null :
            new { Classification = tokens[index].StartsWith('@') ? "response-file" :
                tokens[index].StartsWith('/') || tokens[index].StartsWith('-') ? "compiler-switch" : "argument",
                Complete = Digest(tokens[index]) };
        var evaluatedSame = first.EvaluatedProperties.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .SequenceEqual(later.EvaluatedProperties.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase));
        var globalsSame = first.GlobalProperties.OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .SequenceEqual(later.GlobalProperties.OrderBy(pair => pair.Key, StringComparer.Ordinal));
        var tokensSame = first.Tokens.SequenceEqual(later.Tokens, StringComparer.Ordinal);
        var globals = DifferentKeys(first.GlobalProperties, later.GlobalProperties);
        var evaluated = DifferentKeys(first.EvaluatedProperties, later.EvaluatedProperties);
        var index = 0;
        while (index < Math.Min(first.Tokens.Count, later.Tokens.Count) && first.Tokens[index] == later.Tokens[index]) index++;
        if (tokensSame) index = -1;
        var prefix = "Later actual Csc changed complete retained context: ImageSame=" +
            (first.TaskAssemblyLocation == later.TaskAssemblyLocation) + "; TaskFileSame=" + (first.TaskFile == later.TaskFile) +
            "; TokensSame=" + tokensSame + "; EvaluatedSame=" + evaluatedSame + "; GlobalsSame=" + globalsSame +
            "; FirstGlobalKey=" + SafeKey(globals.FirstOrDefault()) + "; FirstEvaluatedKey=" + SafeKey(evaluated.FirstOrDefault()) + "; ";
        var detail = JsonSerializer.Serialize(new { Version = 1, DiagnosticOnly = true,
            FirstTaskImage = Digest(first.TaskAssemblyLocation), LaterTaskImage = Digest(later.TaskAssemblyLocation),
            FirstTaskFile = Digest(first.TaskFile), LaterTaskFile = Digest(later.TaskFile),
            FirstTokensCount = first.Tokens.Count, LaterTokensCount = later.Tokens.Count, FirstDifferentTokenIndex = index,
            FirstToken = Token(first.Tokens, index), LaterToken = Token(later.Tokens, index),
            FirstCompleteTokens = Digest(JsonSerializer.Serialize(first.Tokens)),
            LaterCompleteTokens = Digest(JsonSerializer.Serialize(later.Tokens)),
            Evaluated = Map(first.EvaluatedProperties, later.EvaluatedProperties, evaluated),
            Globals = Map(first.GlobalProperties, later.GlobalProperties, globals) });
        return EvaluationTextBounds.Prefix(prefix + detail, 16384);
    }

    private string CapturedProject(string relative)
    {
        relative = SnapshotInputPolicy.NormalizeRelative(relative);
        if (!_snapshot.Files.Any(file => file.Exists && file.RelativePath.Equals(relative, StringComparison.Ordinal)))
            throw new SnapshotDivergedException("Evaluated project reference is outside the captured source closure: " + relative);
        return Path.GetFullPath(relative.Replace('/', Path.DirectorySeparatorChar), Root);
    }

    internal void ValidateAuthoredInputs()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _validationFailure?.Throw();
        try
        {
            foreach (var file in _snapshot.Files.Where(file => file.Exists))
            {
                var path = Path.GetFullPath(file.RelativePath.Replace('/', Path.DirectorySeparatorChar), Root);
                _budget.CheckWork();
                var actual = _inputs.Read(path, "source");
                if (actual.Bytes.LongLength != file.Length || !actual.Sha256
                        .Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new SnapshotDivergedException("Graph production changed captured input: " + file.RelativePath);
            }
        }
        catch (Exception failure)
        {
            // First actual validation failure is terminal for this SAME owner.
            // Latch before dependent cleanup; repeated checks cannot re-read or
            // replace the original failure with a fresh validation attempt.
            _budget.BeginFailureCleanup();
            _validationFailure ??= System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure);
            throw;
        }
    }
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            if (_disposalFailure is not null) throw _disposalFailure;
            if (!_disposalReturned) throw new EvaluationContractException("Original graph capture disposal is entered and unreturned; no retry.");
            return;
        }
        _disposed = true;
        try
        {
            _inputs.Dispose(); // Unknown input close prevents dependent clone/cache release.
            await AsyncDisposal.DisposeAllAsync(_producerDirectories.Cast<IAsyncDisposable?>()
                .Concat(new IAsyncDisposable?[] { _packages, _clone }));
            _disposalReturned = true;
        }
        catch (Exception failure) { _disposalFailure = failure; throw; }
    }
}

internal sealed record ResolvedProjectSelection(string Project, string Framework, string Configuration);
internal sealed record ResolvedProjectEdge(ResolvedProjectSelection Selection,
    IReadOnlyDictionary<string, string> ActualMetadata);
internal sealed record ResolvedGraphItem(string Identity, IReadOnlyDictionary<string, string> Metadata);
internal sealed record ResolvedProjectCapture(ResolvedProjectSelection Selection,
    IReadOnlyDictionary<string, string> Properties,
    IReadOnlyDictionary<string, IReadOnlyList<ResolvedGraphItem>> Items,
    IReadOnlyList<ResolvedProjectEdge> Edges, string RawSha256, ResolvedGraphObservedTask? FirstCsc = null)
{
    internal static readonly string[] PropertyNames = ["MSBuildProjectFullPath", "MSBuildToolsPath", "NETCoreSdkVersion",
        "TargetFramework", "Configuration", "LangVersion", "Nullable", "DefineConstants", "MSBuildAllProjects",
        "ProjectAssetsFile", "CscToolPath", "CscToolExe", "CscEnvironment", "UseSharedCompilation",
        "ProvideCommandLineArgs", "SkipCompilerExecution", "CompilerGeneratedFilesOutputPath", "CustomBeforeMicrosoftCSharpTargets",
        "CustomAfterMicrosoftCSharpTargets", "IntermediateAssembly", "TargetPath", "TargetRefPath",
        "ManagePackageVersionsCentrally", "DirectoryPackagesPropsPath", "DirectoryBuildPropsPath", "DirectoryBuildTargetsPath"];
    internal static readonly string[] ItemNames = ["Compile", "CscCommandLineArgs", "ReferencePathWithRefAssemblies",
        "Analyzer", "AdditionalFiles", "EditorConfigFiles", "EmbeddedFiles", "_MSBuildProjectReferenceExistent"];

    internal static ResolvedProjectCapture ReadActual(string root, ResolvedProjectSelection selection,
        string actualJson, CancellationToken cancellationToken, ResolvedGraphObservedTask? firstCsc = null,
        IReadOnlyList<ResolvedGraphObservedTask>? actualContexts = null,
        IReadOnlyList<ResolvedGraphObservedTask>? currentProducerContexts = null)
    {
        if (Encoding.UTF8.GetByteCount(actualJson) > ResolvedGraphCapture.MaxQueryBytes)
            throw new SnapshotLimitException("Complete compiler query exceeds its finite byte limit.");
        using var document = JsonDocument.Parse(actualJson, new JsonDocumentOptions { MaxDepth = 32 });
        var top = Members(document.RootElement);
        if (top.Count != 2 || !top.ContainsKey("Properties") || !top.ContainsKey("Items"))
            throw new EvaluationContractException("Compiler query must retain exactly its complete properties and items.");
        var properties = Strings(top["Properties"]);
        if (properties.Count != PropertyNames.Length || PropertyNames.Any(name => !properties.ContainsKey(name)))
            throw new EvaluationContractException("Compiler query property membership is incomplete.");
        if (properties["TargetFramework"] != selection.Framework || properties["Configuration"] != selection.Configuration ||
            !SamePath(properties["MSBuildProjectFullPath"], Path.GetFullPath(selection.Project, root)) ||
            !properties["UseSharedCompilation"].Equals("false", StringComparison.OrdinalIgnoreCase) ||
            !properties["ProvideCommandLineArgs"].Equals("true", StringComparison.OrdinalIgnoreCase) ||
            properties["SkipCompilerExecution"].Length != 0 && !properties["SkipCompilerExecution"].Equals("false", StringComparison.OrdinalIgnoreCase))
            throw new EvaluationContractException("Actual compiler query differs from its requested project context.");
        var itemProperties = Members(top["Items"]);
        if (itemProperties.Count != ItemNames.Length || ItemNames.Any(name => !itemProperties.ContainsKey(name)))
            throw new EvaluationContractException("Compiler query item membership is incomplete.");
        var items = new Dictionary<string, IReadOnlyList<ResolvedGraphItem>>(StringComparer.Ordinal);
        var total = 0;
        foreach (var name in ItemNames)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var array = itemProperties[name];
            if (array.ValueKind != JsonValueKind.Array || array.GetArrayLength() > ResolvedGraphCapture.MaxItems - total)
                throw new SnapshotLimitException("Complete compiler query item limit exceeded.");
            total += array.GetArrayLength();
            var rows = new List<ResolvedGraphItem>();
            foreach (var row in array.EnumerateArray())
            {
                var metadata = Strings(row);
                if (!metadata.TryGetValue("Identity", out var identity) || identity.Length == 0)
                    throw new EvaluationContractException("Actual compiler item has no exact identity.");
                rows.Add(new(identity, metadata));
            }
            items.Add(name, rows);
        }
        if (firstCsc is not null)
        {
            firstCsc.RequireObservedExecutionSwitch();
            if (firstCsc.ProjectInstanceId < 0 || !SamePath(firstCsc.ProjectFile, Path.GetFullPath(selection.Project, root)) ||
                !firstCsc.EvaluatedProperties.TryGetValue("TargetFramework", out var originalFramework) || originalFramework != selection.Framework ||
                !firstCsc.EvaluatedProperties.TryGetValue("Configuration", out var originalConfiguration) || originalConfiguration != selection.Configuration ||
                firstCsc.Tokens.Count == 0 || firstCsc.Tokens.Count > ResolvedGraphCapture.MaxItems - total + items["CscCommandLineArgs"].Count)
                throw new EvaluationContractException("First original Csc task differs from the complete requested query context.");
            var queried = items["CscCommandLineArgs"];
            if (queried.Count != 0 && !queried.Select(i => i.Identity).SequenceEqual(firstCsc.Tokens, StringComparer.Ordinal))
                throw new EvaluationContractException("Later actual compiler tokens differ from the first original Csc task.");
            items["CscCommandLineArgs"] = firstCsc.Tokens.Select(token => new ResolvedGraphItem(token,
                new Dictionary<string, string>(StringComparer.Ordinal) { ["Identity"] = token })).ToArray();
        }
        if (items["Compile"].Count == 0 || items["CscCommandLineArgs"].Count == 0 || items["ReferencePathWithRefAssemblies"].Count == 0)
            throw new EvaluationContractException("Successful graph capture requires actual compiler input and token output inventories.");
        var edges = items["_MSBuildProjectReferenceExistent"].Select(edge => ReadEdge(root,
            Path.GetDirectoryName(properties["MSBuildProjectFullPath"])!, selection, edge,
            firstCsc is null ? null : actualContexts ?? throw new EvaluationContractException("Actual reference context census required."),
            firstCsc, currentProducerContexts)).ToArray();
        return new(selection, properties, items, edges,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(actualJson))).ToLowerInvariant(), firstCsc);
    }

    private static ResolvedProjectEdge ReadEdge(string root, string projectDirectory,
        ResolvedProjectSelection parent, ResolvedGraphItem edge, IReadOnlyList<ResolvedGraphObservedTask>? actualContexts,
        ResolvedGraphObservedTask? actualParent, IReadOnlyList<ResolvedGraphObservedTask>? currentProducerContexts)
    {
        var metadata = edge.Metadata;
        var path = Path.GetFullPath(edge.Identity, projectDirectory);
        var relative = SnapshotInputPolicy.NormalizeRelative(Path.GetRelativePath(root, path).Replace('\\', '/'));
        var removals = metadata.TryGetValue("UndefineProperties", out var removed) && removed.Length > 0
            ? FiniteRemovals(removed) : [];
        var framework = Setting("SetTargetFramework", "TargetFramework", actualContexts is null ? parent.Framework : Inherited("TargetFramework"));
        var configuration = Setting("SetConfiguration", "Configuration", actualContexts is null ? parent.Configuration : Inherited("Configuration"));
        ResolvedGraphObservedTask? actualChild = null;
        if (actualContexts is not null)
        {
            // Inherit only actual parent GLOBALS that MSBuild passes to the
            // child, never the parent's evaluated/default framework. Explicit
            // edge assignments/removals still control the actual child join.
            bool Matches(ResolvedGraphObservedTask task) => task.ProjectInstanceId >= 0 && SamePath(task.ProjectFile, path) &&
                task.EvaluatedProperties.TryGetValue("TargetFramework", out var actualFramework) &&
                (framework.Length == 0 || actualFramework == framework) &&
                task.EvaluatedProperties.TryGetValue("Configuration", out var actualConfiguration) &&
                (configuration.Length == 0 || actualConfiguration == configuration);
            var matches = currentProducerContexts?.Where(Matches).ToArray() ?? [];
            // Up-to-date children may have no new Csc event. Reuse only the
            // already joined first tasks, with the same inherited constraints.
            if (matches.Length == 0) matches = actualContexts.Where(Matches).ToArray();
            if (matches.Length != 1)
                throw new EvaluationContractException("Exactly one actual evaluated project-reference Csc instance required; no parent fallback.");
            framework = matches[0].EvaluatedProperties["TargetFramework"];
            configuration = matches[0].EvaluatedProperties["Configuration"];
            actualChild = matches[0];
        }
        // All metadata is retained. Nontrivial global-property transforms must be
        // represented by the graph protocol rather than silently guessed from XML.
        foreach (var name in new[] { "AdditionalProperties", "GlobalPropertiesToRemove", "SetPlatform" })
            if (metadata.TryGetValue(name, out var value) && value.Length > 0)
                throw TransformRefusal(name, value, "unsupported-transform");
        if (removed is { Length: > 0 })
            RequireFiniteRemoval(removed, metadata, actualParent, actualChild);
        return new(new(relative, framework, configuration), metadata);

        string Inherited(string property) => !removals.Contains(property, StringComparer.Ordinal) &&
            actualParent is not null && actualParent.GlobalProperties.TryGetValue(property, out var value) ? value : "";

        string Setting(string metadataName, string property, string fallback)
        {
            if (!metadata.TryGetValue(metadataName, out var value) || value.Length == 0) return fallback;
            var prefix = property + "=";
            if (!value.StartsWith(prefix, StringComparison.Ordinal) || value.Length == prefix.Length || value.Contains(';'))
                throw new EvaluationContractException("Project edge global property is not one exact assignment: " + metadataName);
            return value[prefix.Length..];
        }
    }

    // Selected e34 TaskHost copies parent globals, applies explicit assignments,
    // then removes UndefineProperties. This is a closed observation profile, not
    // a substitute evaluation or a declaration that arbitrary metadata is safe.
    private static void RequireFiniteRemoval(string raw, IReadOnlyDictionary<string, string> metadata,
        ResolvedGraphObservedTask? parent, ResolvedGraphObservedTask? child)
    {
        const string field = "UndefineProperties";
        var names = FiniteRemovals(raw);
        if (parent is null || child is null || parent.ProjectInstanceId < 0 || child.ProjectInstanceId < 0)
            throw TransformRefusal(field, raw, "actual-parent-child-required");
        // EvaluationId is separately retained, including unavailable -1. It is
        // not part of the actual four-field task/project context key.
        if (names.Contains("TargetFramework", StringComparer.Ordinal) &&
            metadata.TryGetValue("SetTargetFramework", out var assignedFramework) && assignedFramework.Length > 0)
            throw TransformRefusal(field, raw, "assignment-removal-conflict");
        var expected = Projection(parent.GlobalProperties);
        var observed = Projection(child.GlobalProperties);
        foreach (var (metadataName, property) in new[] {
            ("SetConfiguration", "Configuration"), ("SetTargetFramework", "TargetFramework") })
        {
            if (metadata.TryGetValue(metadataName, out var assignment) && assignment.Length > 0)
            {
                // The caller's Setting guard already requires this exact form;
                // retain it here so this projection never parses a wider form.
                var prefix = property + "=";
                if (!assignment.StartsWith(prefix, StringComparison.Ordinal) ||
                    assignment.Length == prefix.Length || assignment.Contains(';'))
                    throw TransformRefusal(field, raw, "unsupported-assignment");
                expected[property] = assignment[prefix.Length..];
            }
        }
        foreach (var name in names)
        {
            expected.Remove(name);
            if (observed.ContainsKey(name)) throw TransformRefusal(field, raw, "removed-global-still-present");
        }
        if (expected.Count != observed.Count || expected.Any(pair =>
            !observed.TryGetValue(pair.Key, out var value) || value != pair.Value))
            throw TransformRefusal(field, raw, "complete-child-globals-mismatch");

        Dictionary<string, string> Projection(IReadOnlyDictionary<string, string> source)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in source)
                if (!result.TryAdd(pair.Key, pair.Value))
                    throw TransformRefusal(field, raw, "global-key-alias");
            return result;
        }
    }
    private static string[] FiniteRemovals(string raw)
    {
        const string field = "UndefineProperties";
        if (raw.Length > 256) throw TransformRefusal(field, raw, "literal-length");
        var pieces = raw.Split(';');
        var start = pieces[0].Length == 0 ? 1 : 0;
        if (pieces.Length > 4 || start == pieces.Length)
            throw TransformRefusal(field, raw, "literal-piece-count");
        var names = pieces[start..];
        var supported = names.SequenceEqual(new[] { "TargetFramework" }, StringComparer.Ordinal) ||
            names.SequenceEqual(new[] { "RuntimeIdentifier", "SelfContained" }, StringComparer.Ordinal) ||
            names.SequenceEqual(new[] { "TargetFramework", "RuntimeIdentifier", "SelfContained" }, StringComparer.Ordinal);
        if (!supported) throw TransformRefusal(field, raw, "unsupported-literal-profile");
        return names;
    }

    private static EvaluationContractException TransformRefusal(string field, string raw, string reason) =>
        new("Project edge needs a supported exact global-property transform: " + field +
            "; Reason=" + reason + "; ValueLength=" + raw.Length + "; ValueSha256=" +
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw))).ToLowerInvariant());

    private static Dictionary<string, JsonElement> Members(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) throw new EvaluationContractException("Expected an exact query object.");
        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
            if (!result.TryAdd(property.Name, property.Value)) throw new EvaluationContractException("Duplicate query property: " + property.Name);
        return result;
    }
    private static Dictionary<string, string> Strings(JsonElement element) => Members(element).ToDictionary(
        pair => pair.Key, pair => pair.Value.ValueKind == JsonValueKind.String ? pair.Value.GetString()! :
            throw new EvaluationContractException("Compiler query scalar must be a string."), StringComparer.Ordinal);
    private static bool SamePath(string left, string right) => Path.GetFullPath(left).Equals(Path.GetFullPath(right),
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
