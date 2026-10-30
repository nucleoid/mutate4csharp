#if MUTATE_RESOLVED_GRAPH_LOGGER
using System.Collections;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Build.Framework;

namespace Mutate4CSharp;

// Selected-SDK public event consumer for FIRST actual Csc executions throughout
// a graph build. This collects complete individual CommandLineArgs task outputs,
// not a display command line. It does not certify a custom factory, loaded task
// implementation, original native scope or same-compiler deployment by itself.
public sealed class ResolvedGraphCscLogger : ILogger
{
    private const int MaximumCallbacks = 65_536;
    private const int MaximumProjects = 1024;
    private const int MaximumTokens = 32_768;
    private const long MaximumCharacters = 16 * 1024 * 1024;
    private readonly object _gate = new();
    private readonly List<Callback> _callbacks = [];
    private readonly Dictionary<ProjectKey, ProjectRow> _projects = [];
    private readonly Dictionary<TaskKey, TaskRow> _tasks = [];
    private readonly List<PropertyEnumeration> _propertyEnumerations = [];
    private readonly List<ResolvedGraphFile> _compiledFiles = [];
    private readonly List<ResolvedGraphImagePin> _loadedImages = [];
    private ResolvedGraphBudget? _budget;
    private ResolvedGraphInputs? _inputs;
    private ResolvedGraphFile? _requestFile;
    private IEventSource? _source;
    private string? _root, _sourceRoot, _packageRoot;
    private bool _initialized, _closing, _closed, _finished, _permitEmptyTasks;
    private Exception? _failure;
    private long _characters;
    private int _tokens;
    public LoggerVerbosity Verbosity { get; set; } = LoggerVerbosity.Diagnostic;
    public string? Parameters { get; set; }

    public void Initialize(IEventSource eventSource)
    {
        lock (_gate)
        {
            if (_initialized) throw new LoggerException("Graph Csc logger cannot initialize twice.");
            _initialized = true;
            // Fixed private ABI, never an arbitrary file or caller clock issuer.
            // Parent must pin/join this exact request and actual original owner.
            var parts = Parameters?.Split('|');
            if (parts is null || parts.Length != 8 || !Path.IsPathFullyQualified(parts[0]) ||
                parts[1].Length != 64 || parts[1].Any(c => !char.IsAsciiHexDigitLower(c)))
                throw new LoggerException("Exact private graph Csc logger binding required.");
            var numbers = new long[4];
            for (var i = 0; i < numbers.Length; ++i)
                if (parts[i + 2].Length is 0 or > 19 || parts[i + 2].Any(c => c is < '0' or > '9') ||
                    !long.TryParse(parts[i + 2], NumberStyles.None, CultureInfo.InvariantCulture, out numbers[i]))
                    throw new LoggerException("Exact original graph budget integers required.");
            var original = new ResolvedGraphBudgetObservation(numbers[0], numbers[1], numbers[2], numbers[3], null);
            if (!long.TryParse(parts[6], NumberStyles.None, CultureInfo.InvariantCulture, out var ioBytes))
                throw new LoggerException("Exact child I/O grant integer required.");
            var grant = new GraphChildGrant("logger", parts[7], ioBytes);
            ResolvedGraphResources.RequireGrant(grant, "logger");
            _budget = new ResolvedGraphBudget(original, CancellationToken.None, grant);
            _inputs = new ResolvedGraphInputs(_budget);
            _requestFile = _inputs.Read(parts[0], "config", 1_048_576);
            if (_requestFile.Bytes.Length > 1_048_576 || _requestFile.Sha256 != parts[1])
                throw new LoggerException("Original private Csc request byte binding differs.");
            var request = JsonSerializer.Deserialize<CaptureRequest>(_requestFile.Bytes,
                new JsonSerializerOptions { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, MaxDepth = 8 })
                ?? throw new LoggerException("Complete Csc capture request required.");
            if (request.Version != 4 || request.OriginalBudget != original || request.Grant != grant ||
                !Path.IsPathFullyQualified(request.Root) || request.Root.Length > 4096 ||
                Path.GetFullPath(request.Root) != request.Root)
                throw new LoggerException("Private Csc request differs from actual pre-read original binding.");
            _root = request.Root; _sourceRoot = request.SourceRoot; _packageRoot = request.PackageRoot;
            var selectedSdk = typeof(ResolvedGraphCscLogger).Assembly.GetCustomAttributes(
                typeof(System.Reflection.AssemblyMetadataAttribute), false)
                .Cast<System.Reflection.AssemblyMetadataAttribute>()
                .SingleOrDefault(a => a.Key == "MutateSelectedSdk")?.Value;
            if (request.Compiler.SdkVersion != "10.0.401" || selectedSdk != request.Compiler.SdkVersion)
                throw new LoggerException("Actual logger selected SDK stamp differs from requested compiler profile.");
            foreach (var pair in new[] { (typeof(Microsoft.CodeAnalysis.Compilation).Assembly, request.Compiler.CommonSha256),
                         (typeof(Microsoft.CodeAnalysis.CSharp.CSharpCompilation).Assembly, request.Compiler.CSharpSha256) })
            {
                var loaded = _inputs.PinImage(pair.Item1.Location);
                if (loaded.Sha256 != pair.Item2) throw new LoggerException("Actual loaded logger parser differs from selected compiler image.");
                _loadedImages.Add(loaded);
            }
            _permitEmptyTasks = request.PermitEmptyTasks;
            if (eventSource is not IEventSource3 rich) throw new LoggerException("Selected task-parameter event contract required.");
            rich.IncludeTaskInputs();
            _source = eventSource;
            eventSource.ProjectStarted += ProjectStarted;
            eventSource.TaskStarted += TaskStarted;
            eventSource.TaskFinished += TaskFinished;
            eventSource.AnyEventRaised += AnyEvent;
            eventSource.BuildFinished += BuildFinished;
            _budget.CheckWork();
        }
    }

    private void ProjectStarted(object sender, ProjectStartedEventArgs value) => Observe(value, () =>
    {
        if (_projects.Count >= MaximumProjects || value.BuildEventContext is null)
            throw new SnapshotLimitException("Complete graph project event bound exceeded.");
        var key = Project(value.BuildEventContext);
        if (value.ProjectId < 0 || value.ProjectId != value.BuildEventContext.ProjectInstanceId)
            throw new EvaluationContractException("Actual project start lacks its exact request-configuration instance join.");
        var globals = new Dictionary<string, string>(StringComparer.Ordinal);
        if (value.GlobalProperties is null || value.GlobalProperties.Count > 1024)
            throw new EvaluationContractException("Complete actual project global properties required.");
        foreach (var row in value.GlobalProperties)
            if (!globals.TryAdd(Scalar(row.Key), Scalar(row.Value))) throw new EvaluationContractException("Duplicate project global property.");
        var evaluated = ReadEvaluatedContext(value.Properties);
        if (!_projects.TryAdd(key, new(Scalar(value.ProjectFile), globals, value.ProjectId,
            value.BuildEventContext.EvaluationId, evaluated)))
            throw new EvaluationContractException("Duplicate original project event identity.");
    });

    private void TaskStarted(object sender, TaskStartedEventArgs value)
    {
        if (value.TaskName != "Csc") return;
        Observe(value, () =>
        {
            if (_tasks.Count >= ResolvedGraphCapture.MaxContexts || value.BuildEventContext is null)
                throw new SnapshotLimitException("Complete actual Csc context bound exceeded.");
            var key = Task(value.BuildEventContext);
            if (!_projects.TryGetValue(Project(value.BuildEventContext), out var project) ||
                project.ProjectFile != value.ProjectFile || string.IsNullOrWhiteSpace(value.TaskAssemblyLocation))
                throw new EvaluationContractException("Actual Csc lacks its original project/task image event join.");
            if (!project.EvaluatedProperties.TryGetValue("TargetFramework", out var framework) || framework.Length == 0 ||
                !project.EvaluatedProperties.TryGetValue("Configuration", out var configuration) || configuration.Length == 0 ||
                !project.EvaluatedProperties.TryGetValue("MSBuildProjectFullPath", out var fullPath) ||
                !Path.GetFullPath(fullPath).Equals(Path.GetFullPath(project.ProjectFile),
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new EvaluationContractException("Actual Csc project instance lacks complete evaluated framework/configuration/full-path context.");
            if (!_tasks.TryAdd(key, new(project, Scalar(value.TaskAssemblyLocation), Scalar(value.TaskFile))))
                throw new EvaluationContractException("Duplicate actual Csc task event identity.");
        });
    }

    private void AnyEvent(object sender, BuildEventArgs value)
    {
        if (value is not TaskParameterEventArgs parameter) return;
        if (parameter.Kind == TaskParameterMessageKind.TaskInput && parameter.ParameterName is
            "SkipCompilerExecution" or "UseSharedCompilation" or "ProvideCommandLineArgs")
        {
            // Input observations belong to this exact task, not a later property
            // query which a target could have changed after actual execution.
            if (value.BuildEventContext is null) return;
            Observe(value, () =>
            {
                // Task events can arrive on different engine threads. Dictionary
                // reads and writes share Observe's original callback monitor.
                if (!_tasks.TryGetValue(Task(value.BuildEventContext), out var task)) return;
                if (task.Finished || task.OutputSeen || parameter.Items is null || parameter.Items.Count != 1)
                    throw new EvaluationContractException("Actual Csc execution switch is late or not one scalar.");
                var item = parameter.Items[0];
                var text = item switch { bool boolean => boolean ? "true" : "false", string scalar => scalar,
                    _ => throw new EvaluationContractException("Actual Csc execution switch is not a boolean scalar.") };
                bool actual;
                if (text.Equals("true", StringComparison.OrdinalIgnoreCase)) actual = true;
                else if (text.Equals("false", StringComparison.OrdinalIgnoreCase)) actual = false;
                else throw new EvaluationContractException("Actual Csc switch is not exactly true or false.");
                if (!task.Switches.TryAdd(Scalar(parameter.ParameterName), actual))
                    throw new EvaluationContractException("Duplicate original Csc execution switch.");
            });
            return;
        }
        if (parameter.Kind != TaskParameterMessageKind.TaskOutput || parameter.ParameterName != "CommandLineArgs") return;
        Observe(value, () =>
        {
            if (value.BuildEventContext is null || !_tasks.TryGetValue(Task(value.BuildEventContext), out var task))
                throw new EvaluationContractException("Csc token output lacks its same original task start.");
            if (task.OutputSeen || task.Finished || parameter.Items is null ||
                parameter.Items.Count is 0 || parameter.Items.Count > MaximumTokens - _tokens)
                throw new EvaluationContractException("Csc tokens are duplicate, late, empty, or exceed complete item admission.");
            task.OutputSeen = true;
            foreach (var item in parameter.Items)
            {
                _budget!.CheckWork();
                var token = item switch { ITaskItem taskItem => taskItem.ItemSpec, string text => text,
                    _ => throw new EvaluationContractException("Csc output is not an individual actual task item/string.") };
                task.Tokens.Add(Scalar(token)); ++_tokens;
            }
        });
    }

    private void TaskFinished(object sender, TaskFinishedEventArgs value)
    {
        if (value.TaskName != "Csc") return;
        Observe(value, () =>
        {
            if (value.BuildEventContext is null || !_tasks.TryGetValue(Task(value.BuildEventContext), out var task) || task.Finished)
                throw new EvaluationContractException("Csc finish lacks its unique actual start.");
            task.Finished = true; task.Succeeded = value.Succeeded;
            // Selected TaskExecutionHost omits assignment/logging for an empty
            // optional input. Missing SkipCompilerExecution stays ABSENT DATA,
            // never an inferred false. Parent must bind the exact default task
            // implementation/default before claiming actual compilation occurred.
            if (!value.Succeeded || !task.OutputSeen || task.Tokens.Count == 0 || task.Switches.Count is < 2 or > 3 ||
                !task.Switches.TryGetValue("ProvideCommandLineArgs", out var provide) || !provide ||
                !task.Switches.TryGetValue("UseSharedCompilation", out var shared) || shared ||
                task.Switches.TryGetValue("SkipCompilerExecution", out var skip) && skip)
                throw new EvaluationContractException("Actual original Csc did not complete with its full argv output.");
            task.Compiled = ResolvedGraphCompiledSources.Capture(task.Tokens, task.Project.ProjectFile, _sourceRoot!, _packageRoot!,
                _inputs!, _budget!, _compiledFiles);
        });
    }

    private void BuildFinished(object sender, BuildFinishedEventArgs value) => Observe(value, () =>
    {
        if (_finished || !value.Succeeded) throw new EvaluationContractException("Actual graph build failed or finished twice.");
        _finished = true;
    });

    private void Observe(BuildEventArgs actual, Action body)
    {
        lock (_gate)
        {
            if (_closing || _failure is not null || _budget is null || _callbacks.Count >= MaximumCallbacks)
                throw new LoggerException("Original graph callback frame unavailable or failed; no retry.");
            var row = new Callback(actual); _callbacks.Add(row);
            try { _budget.CheckWork(); row.Entered = true; body(); row.Returned = true; }
            catch (Exception primary) { row.Primary = primary; _failure ??= primary; _budget.BeginFailureCleanup(); }
            // Returned, independent post and publication share the same monitor;
            // Shutdown cannot observe a successful callback before its post.
            try { _budget.CheckWork(); row.PostReturned = true; }
            catch (Exception post) { row.Post = post; _failure ??= post; _budget.BeginFailureCleanup(); }
            if (_failure is not null) throw new LoggerException("Original graph Csc callback failed; retained frame required.", _failure);
        }
    }

    private string Scalar(string? value)
    {
        _budget!.CheckWork();
        if (value is null || value.Length > 65_536 || value.Contains('\0') || value.Length > MaximumCharacters - _characters)
            throw new SnapshotLimitException("Complete graph event scalar/character bound exceeded.");
        _characters += value.Length;
        return value;
    }

    private Dictionary<string, string> ReadEvaluatedContext(IEnumerable? properties)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (properties is null) return result; // Non-Csc outer project may have no context.
        var frame = new PropertyEnumeration(); _propertyEnumerations.Add(frame);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            _budget!.CheckWork(); frame.Entered = true;
            var iterator = properties.GetEnumerator(); frame.Resource = iterator; frame.Returned = true;
            _budget.CheckWork();
            while (true)
            {
                _budget.CheckWork(); var next = iterator.MoveNext(); _budget.CheckWork();
                if (!next) break;
                if (names.Count >= MaximumTokens || iterator.Current is not DictionaryEntry entry ||
                    entry.Key is not string name || entry.Value is not string value)
                    throw new EvaluationContractException("Complete finite actual evaluated property entries required.");
                name = Scalar(name); value = Scalar(value);
                if (!names.Add(name)) throw new EvaluationContractException("Case-aliased actual evaluated property.");
                if (name.Equals("TargetFramework", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("Configuration", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("MSBuildProjectFullPath", StringComparison.OrdinalIgnoreCase)) result.Add(name, value);
            }
            frame.Eof = true;
        }
        catch (Exception primary) { frame.Primary = primary; }
        if (frame.Returned)
        {
            try
            {
                _budget!.CheckWork(); frame.CloseEntered = true;
                if (frame.Resource is IDisposable disposable) disposable.Dispose();
                frame.Closed = true; // Non-IDisposable enumerator has no close obligation.
            }
            catch (Exception close) { frame.Post = close; }
            try { _budget!.CheckWork(); frame.PostReturned = true; }
            catch (Exception post) { frame.Post ??= post; }
        }
        if (frame.Primary is not null && frame.Post is not null) throw new SnapshotCleanupException(frame.Primary, frame.Post);
        if (frame.Primary is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(frame.Primary).Throw();
        if (frame.Post is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(frame.Post).Throw();
        if (!frame.Returned || !frame.Eof || !frame.Closed || !frame.PostReturned)
            throw new EvaluationContractException("Actual evaluated property enumeration remains incomplete or first-close unknown.");
        return result;
    }

    public void Shutdown()
    {
        lock (_gate)
        {
            if (_closing) { if (!_closed) throw new LoggerException("Original logger shutdown remains failed/unknown."); return; }
            _closing = true;
            try
            {
                // A callback already latched terminal cleanup. Preserve that
                // original cause instead of replacing it with CheckWork's
                // consequential cleanup refusal during MSBuild shutdown.
                if (_failure is not null)
                    throw new LoggerException("Original graph Csc callback failed before shutdown.", _failure);
                _budget!.CheckWork();
                if (_failure is not null || !_finished || (_tasks.Count == 0 && !_permitEmptyTasks) ||
                    _tasks.Values.Any(t => !t.Finished || !t.Succeeded || !t.OutputSeen) ||
                    _propertyEnumerations.Any(p => !p.Entered || !p.Returned || !p.Eof || !p.Closed || !p.PostReturned ||
                        p.Primary is not null || p.Post is not null) ||
                    _callbacks.Any(c => !c.Entered || !c.Returned || !c.PostReturned || c.Primary is not null || c.Post is not null))
                    throw new EvaluationContractException("Complete original Csc callback graph remains failed/unknown.");
                _source!.ProjectStarted -= ProjectStarted; _source.TaskStarted -= TaskStarted;
                _source.TaskFinished -= TaskFinished; _source.AnyEventRaised -= AnyEvent; _source.BuildFinished -= BuildFinished;
                _inputs!.Recheck(_requestFile!);
                foreach (var file in _compiledFiles) _inputs.Recheck(file);
                foreach (var image in _loadedImages) _inputs.Recheck(image);
                _inputs.ReserveAllocation(checked(_tasks.Count * 256L + _tokens * (long)IntPtr.Size));
                var data = _tasks.Select(row => new TaskData(row.Key, row.Value.Project,
                    row.Value.TaskAssemblyLocation, row.Value.TaskFile, row.Value.Tokens.ToArray(), row.Value.Switches, row.Value.Compiled)).ToArray();
                _budget.CheckWork();
                var preDelivery = _budget.Resources.ObservePreDelivery();
                // Complete counting/admission precedes output allocation. Full
                // Compiled document/path metadata is included in BOTH passes.
                var bytes = ResolvedGraphProtocol.SerializeBounded(new CaptureData(5, _requestFile!.Sha256, data, preDelivery),
                    _inputs, 32 * 1024 * 1024, 1_048_576);
                _budget.CheckWork();
                _inputs.WriteNewResult(Path.Combine(_root!, "resolved-graph-csc-events.json"), bytes);
                _inputs.Dispose(); _closed = true;
            }
            catch (Exception failure) { _failure ??= failure; _budget?.BeginFailureCleanup(); throw; }
        }
    }

    // Selected target/task event constructors preserve the four project-instance
    // fields but omit EvaluationId. Keep that ID in its actual ProjectRow only.
    private static ProjectKey Project(BuildEventContext c) => new(c.SubmissionId, c.NodeId, c.ProjectContextId,
        c.ProjectInstanceId);
    private static TaskKey Task(BuildEventContext c) => new(Project(c), c.TargetId, c.TaskId);
    private sealed record CaptureRequest(int Version, string Root, string SourceRoot, string PackageRoot, ResolvedGraphBudgetObservation OriginalBudget, GraphCompilerImages Compiler, bool PermitEmptyTasks, GraphChildGrant Grant);
    private sealed record ProjectKey(int Submission, int Node, int ProjectContext, int ProjectInstance);
    private sealed record TaskKey(ProjectKey Project, int Target, int Task);
    private sealed record ProjectRow(string ProjectFile, Dictionary<string, string> GlobalProperties,
        int ProjectInstanceId, int EvaluationId, Dictionary<string, string> EvaluatedProperties);
    private sealed record TaskData(TaskKey Identity, ProjectRow Project, string TaskAssemblyLocation, string TaskFile, string[] Tokens, Dictionary<string, bool> Switches, GraphCompiledSnapshot? Compiled);
    private sealed record CaptureData(int Version, string RequestSha256, TaskData[] Tasks, GraphResourceUsage PreDelivery);
    private sealed class TaskRow(ProjectRow project, string assembly, string file)
    {
        internal readonly ProjectRow Project = project;
        internal readonly string TaskAssemblyLocation = assembly, TaskFile = file;
        internal readonly List<string> Tokens = [];
        internal readonly Dictionary<string, bool> Switches = new(StringComparer.Ordinal);
        internal GraphCompiledSnapshot? Compiled;
        internal bool OutputSeen, Finished, Succeeded;
    }
    private sealed class Callback(BuildEventArgs actual)
    {
        internal readonly BuildEventArgs Actual = actual;
        internal bool Entered, Returned, PostReturned;
        internal Exception? Primary, Post;
    }
    private sealed class PropertyEnumeration
    {
        internal IEnumerator? Resource;
        internal bool Entered, Returned, Eof, CloseEntered, Closed, PostReturned;
        internal Exception? Primary, Post;
    }
}
#endif
