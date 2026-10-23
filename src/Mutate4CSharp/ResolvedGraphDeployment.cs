using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mutate4CSharp;

// An installation/setup manifest selects finite complete files. It is not native
// authority: the same parent rereads images before/after each existing owner call.
internal sealed record ResolvedGraphDeployment(int Version, string SourceRevision,
    ResolvedGraphLoggerSelection Logger, GraphProducerImage DotnetHost,
    IReadOnlyList<GraphProducerImage> WorkerClosure, IReadOnlyList<GraphProducerImage> CompilerClosure)
{
    private static readonly string[] WorkerNames = ["Mutate4CSharp.GraphWorker.dll", "Mutate4CSharp.GraphWorker.pdb",
        "Mutate4CSharp.GraphWorker.deps.json", "Mutate4CSharp.GraphWorker.runtimeconfig.json",
        "Microsoft.CodeAnalysis.dll", "Microsoft.CodeAnalysis.CSharp.dll"];
    internal static ResolvedGraphDeployment LoadForExecution(ResolvedGraphBudget budget)
        => LoadForExecution(budget, AppContext.BaseDirectory);

    internal static ResolvedGraphDeployment LoadForExecution(ResolvedGraphBudget budget, string installationDirectory)
    {
        var inputs = new ResolvedGraphInputs(budget);
        ResolvedGraphDeployment? result = null;
        Exception? primary = null, disposition = null;
        try { result = Load(inputs, requireSelectedHost: true, installationDirectory); }
        catch (Exception error) { primary = error; budget.BeginFailureCleanup(); }
        // Dispose exactly once before classifying. A using-finally here could
        // replace the graph-specific refusal with a retained input error.
        try { inputs.Dispose(); }
        catch (Exception error) { disposition = error; }
        if (primary is null && disposition is null) return result!;
        var failures = new[] { primary, disposition }.OfType<Exception>().ToArray();
        var retained = new GraphDeploymentLoadFailureException(inputs, failures);
        var leaves = failures.SelectMany(Leaves).Distinct(ReferenceEqualityComparer.Instance).Cast<Exception>().ToArray();
        var control = leaves.FirstOrDefault(e => e is OperationCanceledException or TimeoutException or
            SnapshotLimitException or ExecutionBoundaryIntegrityException or SnapshotCleanupException);
        if (control is not null)
        {
            if (disposition is not null || leaves.Length > 1) throw new SnapshotCleanupException(primary ?? control, retained);
            ExceptionDispatchInfo.Capture(control).Throw();
        }
        var unavailable = leaves.Any(e => e is FileNotFoundException or DirectoryNotFoundException or
            UnauthorizedAccessException or GraphHostUnavailableException);
        var code = unavailable ? "GRAPH_DEPLOYMENT_UNAVAILABLE" : "GRAPH_DEPLOYMENT_STALE";
        throw new StrictExecutionRefusalException(new(code, unavailable
            ? "The exact selected graph deployment is unavailable."
            : "The selected graph deployment does not match this tool and SDK profile."),
            leaves.Select(e => new EvaluationReason(code, $"{e.GetType().Name}: {e.Message}")).ToArray(),
            retained);

        static IEnumerable<Exception> Leaves(Exception error) => error is AggregateException aggregate
            ? aggregate.InnerExceptions.SelectMany(Leaves) : [error];
    }

    internal static ResolvedGraphDeployment Load(ResolvedGraphInputs inputs) => Load(inputs, requireSelectedHost: false);

    private static ResolvedGraphDeployment Load(ResolvedGraphInputs inputs, bool requireSelectedHost,
        string? installationDirectory = null)
    {
        var installation = installationDirectory ?? AppContext.BaseDirectory;
        var packagedPath = Path.GetFullPath(Path.Combine(installation, "graph", "sdk401", "deployment.json"));
        var packagedPresent = HasPackagedManifest(packagedPath, inputs);
        if (!packagedPresent && HasPackagedManifest(Path.Combine(installation, "DotnetToolSettings.xml"), inputs))
            throw new GraphHostUnavailableException("This installed tool has no packaged graph deployment; external helpers are unavailable.");
        var path = Environment.GetEnvironmentVariable("MUTATE4CSHARP_GRAPH_CAPTURE_MANIFEST") ??
            packagedPath;
        var digest = Environment.GetEnvironmentVariable("MUTATE4CSHARP_GRAPH_CAPTURE_MANIFEST_SHA256");
        // The fixed installed location is protected by the same image/source
        // closure checks; an explicitly selected fixture manifest also needs its
        // complete exact digest. Neither path can inject graph query/result DATA.
        if (Environment.GetEnvironmentVariable("MUTATE4CSHARP_GRAPH_CAPTURE_MANIFEST") is not null &&
            (digest is null || digest.Length != 64 || digest.Any(c => !char.IsAsciiHexDigitLower(c))))
            throw new EvaluationContractException("Explicit graph deployment requires exact manifest SHA256.");
        var input = inputs.Read(Path.GetFullPath(path), "config", 1_048_576);
        if (digest is not null && input.Sha256 != digest) throw new SnapshotDivergedException("Graph deployment manifest changed.");
        var result = ReadManifest(input, packagedPresent && SamePath(input.Path, packagedPath));
        ResolvedGraphFile? packagedInput = null;
        if (packagedPresent)
        {
            packagedInput = SamePath(input.Path, packagedPath) ? input : inputs.Read(packagedPath, "config", 1_048_576);
            var packaged = SamePath(input.Path, packagedPath) ? result : ReadManifest(packagedInput, requirePackageRoles: true);
            RequirePackagedHelpers(result, packaged, Path.GetDirectoryName(packagedPath)!);
        }
        if (requireSelectedHost)
        {
            var host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ??
                throw new GraphHostUnavailableException("Supported graph setup requires explicit selected DOTNET_HOST_PATH.");
            if (!Path.IsPathFullyQualified(host) || !Path.GetFullPath(host).Equals(result.DotnetHost.Path,
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new EvaluationContractException("Dependency and compiler/worker host selections must be identical.");
        }
        result.ReadActual(inputs);
        inputs.Recheck(input, 1_048_576);
        if (packagedInput is not null && !ReferenceEquals(packagedInput, input)) inputs.Recheck(packagedInput, 1_048_576);
        return result;
    }

    private static ResolvedGraphDeployment ReadManifest(ResolvedGraphFile input, bool requirePackageRoles)
    {
        ResolvedGraphProtocol.RequireUnique(input.Bytes);
        var result = JsonSerializer.Deserialize<ResolvedGraphDeployment>(input.Bytes,
            new JsonSerializerOptions { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, MaxDepth = 8 }) ??
            throw new EvaluationContractException("Complete graph deployment required.");
        if (result.Logger?.LoggerClosure is null || result.WorkerClosure is null)
            throw new EvaluationContractException("Complete graph helper closures required.");
        var folder = Path.GetDirectoryName(input.Path)!;
        return result with {
            Logger = result.Logger with { LoggerClosure = result.Logger.LoggerClosure.Select(i => Resolve(i, "logger")).ToArray() },
            WorkerClosure = result.WorkerClosure.Select(i => Resolve(i, "worker")).ToArray()
        };

        GraphProducerImage Resolve(GraphProducerImage image, string role)
        {
            if (image is null || string.IsNullOrEmpty(image.Path)) throw new EvaluationContractException("Complete graph helper pin required.");
            if (Path.IsPathFullyQualified(image.Path))
            {
                if (requirePackageRoles) throw new EvaluationContractException("Packaged helpers require fixed relative installation roles.");
                return image;
            }
            // Relocatable package roles are closed, with exactly one path segment
            // for the image. No general relative/external-path exception exists.
            if (image.Path != role + "/" + Path.GetFileName(image.Path) || image.Path.Contains('\\'))
                throw new EvaluationContractException("Closed package graph image role required.");
            return image with { Path = Path.GetFullPath(image.Path, folder) };
        }
    }

    // A replacement selection may rebind host/SDK pins, never helper code. The
    // package's exact roles and byte tuples, then actual ReadActual hashes, bind
    // both helpers. A copied caller-writable source stamp is not that authority.
    internal static void RequirePackagedHelpers(ResolvedGraphDeployment selected,
        ResolvedGraphDeployment packaged, string packageFolder)
    {
        if (packaged.Version != 1 || selected.SourceRevision != packaged.SourceRevision ||
            selected.Logger.SdkVersion != packaged.Logger.SdkVersion)
            throw new EvaluationContractException("Replacement graph selection differs from the installed package profile.");
        RequireRole(selected.Logger.LoggerClosure, packaged.Logger.LoggerClosure, "logger");
        RequireRole(selected.WorkerClosure, packaged.WorkerClosure, "worker");

        void RequireRole(IReadOnlyList<GraphProducerImage> chosen, IReadOnlyList<GraphProducerImage> installed, string role)
        {
            if (chosen.Count != installed.Count) throw new EvaluationContractException("Complete installed helper role membership required.");
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var pin in installed)
            {
                var name = Path.GetFileName(pin.Path);
                var expected = Path.GetFullPath(Path.Combine(packageFolder, role, name));
                var matches = chosen.Where(p => Path.GetFileName(p.Path) == name).Take(2).ToArray();
                if (!seen.Add(name) || !SamePath(pin.Path, expected) || matches.Length != 1 ||
                    !SamePath(matches[0].Path, expected) || matches[0].Bytes != pin.Bytes || matches[0].Sha256 != pin.Sha256)
                    throw new EvaluationContractException("Replacement graph helpers must retain exact packaged paths and bytes.");
            }
        }
    }

    private static bool HasPackagedManifest(string path, ResolvedGraphInputs inputs)
    {
        inputs.CheckWork();
        try
        {
            var attributes = File.GetAttributes(path); inputs.CheckWork();
            if ((attributes & (FileAttributes.Directory | FileAttributes.Device | FileAttributes.ReparsePoint)) != 0)
                throw new EvaluationContractException("Packaged graph manifest must be a plain regular file.");
            return true;
        }
        catch (FileNotFoundException) { inputs.CheckWork(); return false; }
        catch (DirectoryNotFoundException) { inputs.CheckWork(); return false; }
    }

    private static bool SamePath(string actual, string expected) => Path.IsPathFullyQualified(actual) &&
        Path.GetFullPath(actual).Equals(Path.GetFullPath(expected),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private sealed class GraphHostUnavailableException(string message) : IOException(message);
    private sealed class GraphDeploymentLoadFailureException(ResolvedGraphInputs inputs, IEnumerable<Exception> failures)
        : AggregateException("Graph deployment loading and first disposition outcomes retained.", failures)
    {
        // Failed/unknown first disposition must retain its actual original,
        // including streams/frames; exception metadata never certifies closure.
        internal ResolvedGraphInputs RetainedInputs { get; } = inputs;
    }

    internal IReadOnlyList<ResolvedGraphImagePin> ReadActual(ResolvedGraphInputs inputs)
    {
        inputs.CheckWork();
        var revision = typeof(ResolvedGraphDeployment).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .SingleOrDefault(a => a.Key == "MutateSourceRevision")?.Value;
        if (Version != 1 || SourceRevision.Length != 40 || SourceRevision.Any(c => !char.IsAsciiHexDigitLower(c)) ||
            revision != SourceRevision || WorkerClosure.Count != WorkerNames.Length)
            throw new EvaluationContractException("Graph deployment must be built from the SAME official tool source revision.");
        var retained = new List<ResolvedGraphImagePin>(Logger.ReadActual(inputs));
        var folder = Path.GetDirectoryName(WorkerClosure[0].Path);
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var image in WorkerClosure)
        {
            if (!WorkerNames.Contains(Path.GetFileName(image.Path), StringComparer.Ordinal) ||
                !names.Add(Path.GetFileName(image.Path)) || Path.GetDirectoryName(image.Path) != folder)
                throw new EvaluationContractException("Exact colocated worker closure required.");
            retained.Add(Read(image));
        }
        var host = Read(DotnetHost); retained.Add(host);
        if (Path.GetFileName(host.Path) != (OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"))
            throw new EvaluationContractException("Exact selected dotnet host role required.");
        var selectedTask = Path.Combine(Path.GetDirectoryName(host.Path)!, "sdk", Logger.SdkVersion,
            "Roslyn", "Microsoft.Build.Tasks.CodeAnalysis.dll");
        if (!Path.GetFullPath(selectedTask).Equals(Logger.CscTask.Path,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new EvaluationContractException("Actual Csc task must belong to the selected host SDK profile.");
        var compilerNames = new[] { "csc.dll", "csc.deps.json", "csc.runtimeconfig.json", "Microsoft.CodeAnalysis.dll", "Microsoft.CodeAnalysis.CSharp.dll" };
        var compilerRoot = Path.Combine(Path.GetDirectoryName(selectedTask)!, "bincore");
        if (CompilerClosure.Count != compilerNames.Length ||
            CompilerClosure.Select(i => Path.GetFileName(i.Path)).Distinct(StringComparer.Ordinal).Count() != compilerNames.Length)
            throw new EvaluationContractException("Complete selected compiler driver/runtime/dependency closure required.");
        foreach (var image in CompilerClosure)
        {
            if (!compilerNames.Contains(Path.GetFileName(image.Path), StringComparer.Ordinal) ||
                !Path.GetFullPath(image.Path).Equals(Path.Combine(compilerRoot, Path.GetFileName(image.Path)),
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new EvaluationContractException("Compiler image is outside the SAME selected host SDK driver role.");
            retained.Add(Read(image));
        }
        foreach (var image in retained.Where(f => Path.GetFileName(f.Path) is
                     "Mutate4CSharp.GraphCapture.dll" or "Mutate4CSharp.GraphWorker.dll"))
            {
                var semanticImage = inputs.Read(image.Path, "compiler");
                RequireSourceStamp(semanticImage.Bytes, SourceRevision, Logger.SdkVersion);
                inputs.Recheck(semanticImage);
            }
        var common = retained.Where(f => Path.GetFileName(f.Path) == "Microsoft.CodeAnalysis.dll").ToArray();
        var csharp = retained.Where(f => Path.GetFileName(f.Path) == "Microsoft.CodeAnalysis.CSharp.dll").ToArray();
        if (common.Length != 3 || csharp.Length != 3 || common.Any(i => i.Sha256 != common[0].Sha256) || csharp.Any(i => i.Sha256 != csharp[0].Sha256))
            throw new EvaluationContractException("Logger and worker must use identical selected SDK compiler bytes.");
        return retained;

        ResolvedGraphImagePin Read(GraphProducerImage image)
        {
            if (!Path.IsPathFullyQualified(image.Path) || Path.GetFullPath(image.Path) != image.Path ||
                image.Bytes is <= 0 or > ResolvedGraphInputs.MaximumFileBytes || image.Sha256.Length != 64 ||
                image.Sha256.Any(c => !char.IsAsciiHexDigitLower(c)))
                throw new EvaluationContractException("Exact finite deployment image tuple required.");
            var file = inputs.PinImage(image.Path, "compiler");
            if (file.Bytes != image.Bytes || file.Sha256 != image.Sha256)
                throw new SnapshotDivergedException("Actual graph deployment image changed.");
            return file;
        }
    }

    internal string WorkerPath => WorkerClosure.Single(i => Path.GetFileName(i.Path) == "Mutate4CSharp.GraphWorker.dll").Path;

    internal static void RequireSourceStamp(byte[] bytes, string source, string sdk)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        using var image = new PEReader(stream);
        var reader = image.GetMetadataReader();
        var stamps = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var handle in reader.GetAssemblyDefinition().GetCustomAttributes())
        {
            var attribute = reader.GetCustomAttribute(handle);
            if (attribute.Constructor.Kind != HandleKind.MemberReference) continue;
            var ctor = reader.GetMemberReference((MemberReferenceHandle)attribute.Constructor);
            if (ctor.Parent.Kind != HandleKind.TypeReference) continue;
            var type = reader.GetTypeReference((TypeReferenceHandle)ctor.Parent);
            if (reader.GetString(type.Namespace) != "System.Reflection" || reader.GetString(type.Name) != "AssemblyMetadataAttribute") continue;
            var blob = reader.GetBlobReader(attribute.Value);
            if (blob.ReadUInt16() != 1) throw new EvaluationContractException("Malformed assembly metadata attribute.");
            var key = blob.ReadSerializedString(); var value = blob.ReadSerializedString();
            if (key is null || value is null || !stamps.TryAdd(key, value))
                throw new EvaluationContractException("Duplicate or null graph build metadata.");
        }
        if (!stamps.TryGetValue("MutateSourceRevision", out var revision) || revision != source ||
            !stamps.TryGetValue("MutateSelectedSdk", out var selected) || selected != sdk)
            throw new EvaluationContractException("Graph helper source/SDK build metadata differs from installed tool selection.");
    }
}
