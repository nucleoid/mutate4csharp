using System.Text.Json;

namespace Mutate4CSharp;

// Strict reader of the actual first-Csc logger's complete output. Returned rows
// remain DATA, not task-factory/native authority; their original producer, image,
// retained source file and private creation owner must be joined independently.
internal static class ResolvedGraphTaskCapture
{
    internal static ResolvedGraphTaskData Read(string path, string requestSha256, ResolvedGraphInputs inputs, bool permitEmptyTasks = false)
    {
        if (requestSha256.Length != 64 || requestSha256.Any(c => !char.IsAsciiHexDigitLower(c)))
            throw Refuse("Exact private first-Csc request digest required.");
        var actual = inputs.Read(path, "config");
        inputs.CheckWork();
        ResolvedGraphProtocol.RequireUnique(actual.Bytes);
        using var document = JsonDocument.Parse(actual.Bytes, new JsonDocumentOptions { MaxDepth = 16 });
        inputs.CheckWork();
        var root = Fields(document.RootElement, "Version", "RequestSha256", "Tasks", "PreDelivery");
        if (root["Version"].ValueKind != JsonValueKind.Number || !root["Version"].TryGetInt32(out var version) || version != 5 || Text(root["RequestSha256"]) != requestSha256)
            throw Refuse("First-Csc result differs from its original request/version.");
        var tasks = root["Tasks"];
        if (tasks.ValueKind != JsonValueKind.Array || (!permitEmptyTasks && tasks.GetArrayLength() == 0) ||
            tasks.GetArrayLength() > ResolvedGraphCapture.MaxContexts)
            throw Refuse("Complete first-Csc task count required.");
        var rows = new List<ResolvedGraphObservedTask>();
        var identities = new HashSet<string>(StringComparer.Ordinal);
        var totalTokens = 0;
        long totalCharacters = 0;
        foreach (var task in tasks.EnumerateArray())
        {
            inputs.CheckWork();
            var fields = Fields(task, "Identity", "Project", "TaskAssemblyLocation", "TaskFile", "Tokens", "Switches", "Compiled");
            var identity = Fields(fields["Identity"], "Project", "Target", "Task");
            var projectIdentity = Fields(identity["Project"], "Submission", "Node", "ProjectContext", "ProjectInstance");
            var instance = Number(projectIdentity["ProjectInstance"]);
            var key = string.Join("/", new[] { Number(projectIdentity["Submission"]), Number(projectIdentity["Node"]),
                Number(projectIdentity["ProjectContext"]), instance, Number(identity["Target"]), Number(identity["Task"]) }
                .Select(n => n.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            if (!identities.Add(key)) throw Refuse("Duplicate original first-Csc event identity.");
            var project = Fields(fields["Project"], "ProjectFile", "GlobalProperties", "ProjectInstanceId", "EvaluationId", "EvaluatedProperties");
            var evaluation = EvaluationNumber(project["EvaluationId"]);
            if (Number(project["ProjectInstanceId"]) != instance)
                throw Refuse("Actual Csc project instance differs from its project-start identity.");
            var evaluatedFields = Members(project["EvaluatedProperties"], 3);
            var evaluated = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var value in evaluatedFields)
            {
                inputs.CheckWork();
                if (!evaluated.TryAdd(Charge(value.Key), Charge(Text(value.Value))))
                    throw Refuse("Case-aliased actual evaluated context property.");
            }
            if (evaluated.Count != 3 || !evaluated.TryGetValue("TargetFramework", out var framework) || framework.Length == 0 ||
                !evaluated.TryGetValue("Configuration", out var configuration) || configuration.Length == 0 ||
                !evaluated.TryGetValue("MSBuildProjectFullPath", out var projectPath) ||
                !Absolute(projectPath).Equals(Absolute(Text(project["ProjectFile"])),
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw Refuse("Complete actual evaluated Csc framework/configuration/project path required.");
            var globals = Members(project["GlobalProperties"]);
            if (globals.Count > 1024) throw Refuse("Complete project globals exceed their finite profile.");
            var retainedGlobals = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var value in globals)
            {
                inputs.CheckWork();
                if (!retainedGlobals.TryAdd(Charge(value.Key), Charge(Text(value.Value))))
                    throw Refuse("Case-aliased original project global property.");
            }
            var tokens = fields["Tokens"];
            if (tokens.ValueKind != JsonValueKind.Array || tokens.GetArrayLength() == 0 ||
                tokens.GetArrayLength() > ResolvedGraphCapture.MaxItems - totalTokens)
                throw Refuse("Complete first-Csc individual token bound exceeded.");
            var argv = new List<string>(tokens.GetArrayLength());
            foreach (var token in tokens.EnumerateArray())
            {
                inputs.CheckWork();
                var text = Charge(Text(token));
                if (text.Length == 0) throw Refuse("Empty original Csc token.");
                argv.Add(text); ++totalTokens;
            }
            var switches = Members(fields["Switches"], 3);
            if (switches.Count is < 2 or > 3 || switches.Keys.Any(k =>
                k is not ("ProvideCommandLineArgs" or "UseSharedCompilation" or "SkipCompilerExecution")))
                throw Refuse("Closed actual Csc execution switch set required.");
            foreach (var flag in switches.Values)
                if (flag.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    throw Refuse("Actual task switch must be a typed JSON boolean.");
            if (!switches.TryGetValue("ProvideCommandLineArgs", out var provide) || !provide.GetBoolean() ||
                !switches.TryGetValue("UseSharedCompilation", out var shared) || shared.GetBoolean() ||
                switches.TryGetValue("SkipCompilerExecution", out var skip) && skip.GetBoolean())
                throw Refuse("Original Csc switched away from supported actual argv/nonshared execution.");
            var compiled = JsonSerializer.Deserialize<GraphCompiledSnapshot>(fields["Compiled"].GetRawText(), ResolvedGraphProtocol.Options)
                ?? throw Refuse("Complete actual Csc compiled document/output snapshot required.");
            Absolute(compiled.PePath); Absolute(compiled.PdbPath); Hash(compiled.PeSha256); Hash(compiled.PdbSha256);
            if (compiled.Documents is null || compiled.Documents.Length is < 1 or > ResolvedGraphInputs.MaximumFiles ||
                compiled.PathMap is null || compiled.PathMap.Count > 1024 || compiled.Proof is null)
                throw Refuse("Complete bounded compiled source/path-map membership required.");
            foreach (var compiledDocument in compiled.Documents)
            {
                inputs.CheckWork();
                if (compiledDocument is null) throw Refuse("Compiled document is null.");
                Absolute(compiledDocument.Path); Hash(compiledDocument.Sha256);
            }
            foreach (var mapping in compiled.PathMap) { inputs.CheckWork(); Absolute(mapping.Key); Charge(mapping.Value); }
            Hash(compiled.Proof.PeSha256); Hash(compiled.Proof.PdbSha256);
            rows.Add(new(key, Absolute(Text(project["ProjectFile"])), retainedGlobals,
                Absolute(Text(fields["TaskAssemblyLocation"])), Absolute(Text(fields["TaskFile"])),
                argv, switches.TryGetValue("SkipCompilerExecution", out skip) ? skip.GetBoolean() : null)
                        { EvaluatedProperties = evaluated, ProjectInstanceId = instance, EvaluationId = evaluation,
                Compiled = compiled });
        }
        inputs.Recheck(actual);
        inputs.CheckWork();
        var usage = JsonSerializer.Deserialize<GraphResourceUsage>(root["PreDelivery"].GetRawText(), ResolvedGraphProtocol.Options)
            ?? throw Refuse("Complete logger pre-delivery resource observations required.");
        if (usage.Grant is null) throw Refuse("Logger resource grant missing.");
        ResolvedGraphResources.RequireGrant(usage.Grant, "logger");
        return new(actual, requestSha256, rows, usage);

        string Charge(string value)
        {
            if (value is null || value.Length > 65_536 || value.Contains('\0') || value.Length > 16 * 1024 * 1024 - totalCharacters)
                throw new SnapshotLimitException("Complete original Csc character limit exceeded.");
            totalCharacters += value.Length; return value;
        }
        void Hash(string value)
        {
            Charge(value);
            if (value.Length != 64 || value.Any(character => !char.IsAsciiHexDigitLower(character))) throw Refuse("Complete lower-case compiled SHA256 required.");
        }
        string Absolute(string value)
        {
            Charge(value);
            if (value.Length is 0 or > 4096 || !Path.IsPathFullyQualified(value) || Path.GetFullPath(value) != value)
                throw Refuse("Exact absolute original task/project/source location required.");
            return value;
        }
    }

    private static Dictionary<string, JsonElement> Members(JsonElement value, int maximum = 1024)
    {
        if (value.ValueKind != JsonValueKind.Object) throw Refuse("Exact original Csc object required.");
        var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            if (fields.Count >= maximum) throw Refuse("Original Csc object member bound exceeded.");
            if (!fields.TryAdd(property.Name, property.Value)) throw Refuse("Duplicate original Csc property.");
        }
        return fields;
    }
    private static Dictionary<string, JsonElement> Fields(JsonElement value, params string[] names)
    {
        var fields = Members(value, names.Length);
        if (fields.Count != names.Length || names.Any(n => !fields.ContainsKey(n)))
            throw Refuse("Complete original Csc object membership differs.");
        return fields;
    }
    private static string Text(JsonElement value) => value.ValueKind == JsonValueKind.String ? value.GetString()! :
        throw Refuse("Actual Csc scalar must be a string.");
    private static int Number(JsonElement value) => value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) && number >= 0 ? number :
        throw Refuse("Actual Csc event ID must be a nonnegative Int32.");
    private static int EvaluationNumber(JsonElement value) => value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) && number >= -1 ? number :
        throw Refuse("Actual evaluation ID must be its original Int32, with only -1 permitted as unavailable.");
    private static EvaluationContractException Refuse(string message) => new(message);
}

internal sealed record ResolvedGraphTaskData(ResolvedGraphFile OriginalFile, string RequestSha256,
    IReadOnlyList<ResolvedGraphObservedTask> Tasks, GraphResourceUsage PreDelivery)
{
    internal ResolvedGraphObservedTask Select(string root, ResolvedProjectSelection selection)
    {
        var path = Path.GetFullPath(selection.Project, root);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var matches = Tasks.Where(task => task.ProjectFile.Equals(path, comparison) &&
            task.EvaluatedProperties.TryGetValue("TargetFramework", out var framework) && framework == selection.Framework &&
            task.EvaluatedProperties.TryGetValue("Configuration", out var configuration) && configuration == selection.Configuration).ToArray();
        if (matches.Length != 1) throw new EvaluationContractException("Exactly one first original Csc task must join the complete graph context.");
        return matches[0];
    }
}

internal sealed record ResolvedGraphObservedTask(string EventIdentity, string ProjectFile,
    IReadOnlyDictionary<string, string> GlobalProperties, string TaskAssemblyLocation, string TaskFile,
    IReadOnlyList<string> Tokens, bool? SkipCompilerExecution)
{
    // Public accessors on this internal DTO enroll these observations in the
    // same-source private worker JSON. They remain DATA, not capabilities.
    public IReadOnlyDictionary<string, string> EvaluatedProperties { get; init; } = new Dictionary<string, string>();
    public GraphCompiledSnapshot? Compiled { get; init; }
    public int ProjectInstanceId { get; init; } = -1;
    public int EvaluationId { get; init; } = -1;
    internal void RequireObservedExecutionSwitch()
    {
        if (SkipCompilerExecution != false)
            throw new EvaluationContractException("Absent skip input needs exact selected default-task/factory proof; it is not inferred false.");
    }
}
