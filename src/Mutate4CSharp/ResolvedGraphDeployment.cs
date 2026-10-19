using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
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
    internal static ResolvedGraphDeployment Load(ResolvedGraphInputs inputs)
    {
        var path = Environment.GetEnvironmentVariable("MUTATE4CSHARP_GRAPH_CAPTURE_MANIFEST") ??
            Path.Combine(AppContext.BaseDirectory, "graph", "sdk401", "deployment.json");
        var digest = Environment.GetEnvironmentVariable("MUTATE4CSHARP_GRAPH_CAPTURE_MANIFEST_SHA256");
        // The fixed installed location is protected by the same image/source
        // closure checks; an explicitly selected fixture manifest also needs its
        // complete exact digest. Neither path can inject graph query/result DATA.
        if (Environment.GetEnvironmentVariable("MUTATE4CSHARP_GRAPH_CAPTURE_MANIFEST") is not null &&
            (digest is null || digest.Length != 64 || digest.Any(c => !char.IsAsciiHexDigitLower(c))))
            throw new EvaluationContractException("Explicit graph deployment requires exact manifest SHA256.");
        var input = inputs.Read(Path.GetFullPath(path), "config", 1_048_576);
        if (digest is not null && input.Sha256 != digest) throw new SnapshotDivergedException("Graph deployment manifest changed.");
        ResolvedGraphProtocol.RequireUnique(input.Bytes);
        var result = JsonSerializer.Deserialize<ResolvedGraphDeployment>(input.Bytes,
            new JsonSerializerOptions { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, MaxDepth = 8 }) ??
            throw new EvaluationContractException("Complete graph deployment required.");
        var folder = Path.GetDirectoryName(input.Path)!;
        result = result with {
            Logger = result.Logger with { LoggerClosure = result.Logger.LoggerClosure.Select(i => Resolve(i, "logger")).ToArray() },
            WorkerClosure = result.WorkerClosure.Select(i => Resolve(i, "worker")).ToArray()
        };
        result.ReadActual(inputs);
        inputs.Recheck(input, 1_048_576);
        return result;

        GraphProducerImage Resolve(GraphProducerImage image, string role)
        {
            if (Path.IsPathFullyQualified(image.Path)) return image;
            // Relocatable package roles are closed, with exactly one path segment
            // for the image. No general relative/external-path exception exists.
            if (image.Path != role + "/" + Path.GetFileName(image.Path) || image.Path.Contains('\\'))
                throw new EvaluationContractException("Closed package graph image role required.");
            return image with { Path = Path.GetFullPath(image.Path, folder) };
        }
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
