using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mutate4CSharp;

internal static class ResolvedGraphExecution
{
    internal static async Task<StrictMutationEnumerationResult> EnumerateAsync(InputSnapshot snapshot,
        CheckConfiguration configuration, ScopePlan scope, FrozenExecutionEnvironment environment,
        ResolvedGraphDeployment deployment, ResolvedGraphBudget original, CancellationToken token)
    {
        ResolvedGraphCapture? capture = null;
        Exception? failure = null;
        StrictMutationEnumerationResult? enumeration = null;
        using var inputs = new ResolvedGraphInputs(original);
        try
        {
            var images = deployment.ReadActual(inputs);
            capture = await ResolvedGraphCapture.CreateAsync(snapshot, configuration, environment,
                original.RemainingWork, token, deployment.Logger, original);
            var configurationFile = snapshot.Files.Single(f => f.Exists && f.RelativePath == "mutate4csharp.json");
            var config = inputs.Read(Path.Combine(snapshot.CaptureRoot, configurationFile.RelativePath), "config", 1_048_576);
            if (config.Bytes.LongLength != configurationFile.Length || config.Sha256 != configurationFile.Sha256)
                throw new SnapshotDivergedException("Original graph configuration differs from immutable snapshot.");
            var origin = original.Observe();
            var compiler = new GraphCompilerImages(environment.SdkVersion,
                deployment.WorkerClosure.Single(i => Path.GetFileName(i.Path) == "Microsoft.CodeAnalysis.dll").Sha256,
                deployment.WorkerClosure.Single(i => Path.GetFileName(i.Path) == "Microsoft.CodeAnalysis.CSharp.dll").Sha256);
            var childGrant = original.Resources.ReserveChild("worker");
            var request = new GraphWorkerRequest(3, capture.Root, capture.PackageRoot, config.Bytes, scope,
                snapshot.Files.Where(f => f.Exists).Select(f => f.RelativePath).ToArray(),
                capture.Projects.ToArray(), compiler, deployment.SourceRevision, origin, childGrant);
            var requestPath = Path.Combine(capture.Root, "resolved-graph-request.json");
            var resultPath = Path.Combine(capture.Root, "resolved-graph-result.json");
            original.CheckWork();
            var bytes = ResolvedGraphProtocol.SerializeBounded(request, inputs, 16 * 1024 * 1024, 4 * 1024 * 1024);
            inputs.WriteNewResult(requestPath, bytes);
            var actualRequest = inputs.Read(requestPath, "config", 16 * 1024 * 1024);
            var argv = new[] { deployment.WorkerPath, requestPath, resultPath, actualRequest.Sha256,
                origin.Original.ToString(CultureInfo.InvariantCulture), origin.Frequency.ToString(CultureInfo.InvariantCulture),
                origin.WholeTicks.ToString(CultureInfo.InvariantCulture), origin.WorkTicks.ToString(CultureInfo.InvariantCulture),
                childGrant.IoBytes.ToString(CultureInfo.InvariantCulture), childGrant.Nonce };
            var actual = await ProcessTree.RunAsync(deployment.DotnetHost.Path, argv, capture.Root,
                original.RemainingWork, token, ExecutionEnvironment.ProcessEnvironment(capture.PackageRoot),
                1_048_576, requireLinuxSessionIsolation: true);
            original.CheckWork();
            if (actual.TimedOut || actual.ExitCode is not (0 or 1))
                throw new SnapshotCaptureException("Actual graph semantic worker failed: " +
                    EvaluationTextBounds.Suffix(actual.StandardError, EvaluationEvidence.MaxDiagnosticLength));
            var result = inputs.Read(resultPath, "config");
            ResolvedGraphProtocol.RequireUnique(result.Bytes);
            var output = JsonSerializer.Deserialize<GraphWorkerResult>(result.Bytes, ResolvedGraphProtocol.Options) ??
                throw new EvaluationContractException("Complete worker result required.");
            if (output.Version != 2 || output.RequestSha256 != actualRequest.Sha256 ||
                output.SourceRevision != deployment.SourceRevision || output.Compiler != compiler || output.OriginalBudget != origin ||
                output.Enumeration is null || output.Enumeration.IsComplete != (actual.ExitCode == 0))
                throw new EvaluationContractException("Worker result does not join its SAME actual producer, request, source, compiler and original.");
            original.Resources.ObserveChildReturn(childGrant, output.PreDelivery, result.Bytes.LongLength);
            enumeration = output.Enumeration;
            if (enumeration.Candidates.Count > StrictMutationEnumerator.MaxCandidates || enumeration.Reasons.Count > 100 ||
                enumeration.IsComplete && (enumeration.SemanticContextIdentity?.Length != 64 || enumeration.Reasons.Count != 0) ||
                enumeration.Candidates.Select(c => c.EvaluationUnitId).Distinct(StringComparer.Ordinal).Count() != enumeration.Candidates.Count)
                throw new EvaluationContractException("Complete worker enumeration membership/identity invalid.");
            foreach (var candidate in enumeration.Candidates)
                if (!configuration.Projects.Any(p => p.Project == candidate.ProjectPath && p.TargetFramework == candidate.TargetFramework &&
                    p.ParseContext == candidate.ParseContext) || !request.CapturedPaths.Contains(candidate.Material.RepositoryPath, StringComparer.Ordinal))
                    throw new EvaluationContractException("Worker candidate escapes current configured source closure.");
            inputs.Recheck(result); inputs.Recheck(actualRequest); inputs.Recheck(config);
            foreach (var image in images) inputs.Recheck(image);
            capture.ValidateAuthoredInputs();
            await snapshot.ValidateOriginalAsync(token);
            original.CheckWork();
        }
        catch (Exception primary) { original.BeginFailureCleanup(); failure = primary; }
        // The capture must stay alive until every result/image/source recheck has
        // returned. Unknown actual ProcessTree state vetoes dependent deletion in
        // the existing OwnedDirectory guard; no second launcher or retry exists.
        await AsyncDisposal.DisposeAllPreservingFailureAsync([capture], failure);
        return enumeration ?? throw new EvaluationContractException("No actual worker enumeration returned.");
    }
}

internal sealed record GraphCompilerImages(string SdkVersion, string CommonSha256, string CSharpSha256);
internal sealed record GraphWorkerRequest(int Version, string Root, string PackageRoot, byte[] ConfigurationBytes,
    ScopePlan Scope, string[] CapturedPaths, ResolvedProjectCapture[] Projects, GraphCompilerImages Compiler,
    string SourceRevision, ResolvedGraphBudgetObservation OriginalBudget, GraphChildGrant Grant);
internal sealed record GraphWorkerResult(int Version, string RequestSha256, string SourceRevision,
    GraphCompilerImages Compiler, ResolvedGraphBudgetObservation OriginalBudget, StrictMutationEnumerationResult Enumeration, GraphResourceUsage PreDelivery);

internal static class ResolvedGraphProtocol
{
    internal static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, MaxDepth = 64 };
    internal static byte[] SerializeBounded<T>(T value, ResolvedGraphInputs inputs, int maximum, int temporary = 32 * 1024 * 1024) =>
        WriteBounded(writer => JsonSerializer.Serialize(writer, value, Options), inputs, maximum, temporary);

    internal static byte[] WriteBounded(Action<Utf8JsonWriter> write, ResolvedGraphInputs inputs, int maximum, int temporary)
    {
        if (maximum is <= 0 or > ResolvedGraphInputs.MaximumFileBytes || temporary is <= 0 or > ResolvedGraphInputs.MaximumFileBytes)
            throw new ArgumentOutOfRangeException(nameof(maximum));
        // Two nonexecuting serialization passes share the original ledger. The
        // first counts complete output into a bounded sink; allocation then uses
        // that exact count, not maximum-per-context buffers/refunded credits.
        // Utf8JsonWriter synchronous internals remain cooperative BCL work.
        inputs.ReserveAllocation(2L * temporary);
        using var counter = new CountStream(inputs, maximum);
        WriteOnce(counter, write, inputs);
        inputs.CheckWork();
        inputs.ReserveAllocation(2L * counter.Count);
        var buffer = new byte[counter.Count];
        inputs.CheckWork();
        // Nonexpandable MemoryStream: the complete serializer can neither grow
        // the admitted output nor accept a truncated prefix on overflow.
        using var output = new MemoryStream(buffer, 0, buffer.Length, writable: true, publiclyVisible: false);
        output.SetLength(0);
        WriteOnce(output, write, inputs);
        inputs.CheckWork();
        if (output.Length != counter.Count) throw new EvaluationContractException("Complete serialization changed between bounded passes.");
        var complete = output.ToArray();
        inputs.CheckWork();
        return complete;
    }

    private static void WriteOnce(Stream destination, Action<Utf8JsonWriter> write, ResolvedGraphInputs inputs)
    {
        Utf8JsonWriter? writer = null;
        Exception? failure = null;
        try
        {
            inputs.CheckWork(); writer = new Utf8JsonWriter(destination); inputs.CheckWork();
            write(writer); inputs.CheckWork(); writer.Flush(); inputs.CheckWork();
        }
        catch (Exception primary) { inputs.BeginFailureCleanup(); failure = primary; }
        try
        {
            inputs.CheckDisposition(); writer?.Dispose(); inputs.CheckDisposition();
        }
        catch (Exception close)
        {
            inputs.BeginFailureCleanup();
            failure = failure is null ? close : new SnapshotCleanupException(failure, close);
        }
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private sealed class CountStream(ResolvedGraphInputs inputs, int maximum) : Stream
    {
        internal int Count { get; private set; }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => Count;
        public override long Position { get => Count; set => throw new NotSupportedException(); }
        public override void Write(byte[] buffer, int offset, int count) => Admit(count);
        public override void Write(ReadOnlySpan<byte> buffer) => Admit(buffer.Length);
        private void Admit(int count)
        {
            inputs.CheckWork();
            if (count < 0 || count > maximum - Count) throw new SnapshotLimitException("Complete serialized graph exceeds its bound.");
            Count += count; inputs.CheckWork();
        }
        public override void Flush() => inputs.CheckWork();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
    internal static void RequireUnique(byte[] bytes)
    {
        if (bytes.Length > 32 * 1024 * 1024) throw new SnapshotLimitException("Complete protocol bytes exceeded.");
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 64 });
        var nodes = 0;
        Visit(document.RootElement);
        void Visit(JsonElement node)
        {
            if (++nodes > 262_144) throw new SnapshotLimitException("Complete protocol node limit exceeded.");
            if (node.ValueKind == JsonValueKind.Object)
            {
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in node.EnumerateObject())
                { if (!names.Add(property.Name)) throw new EvaluationContractException("Duplicate graph protocol key."); Visit(property.Value); }
            }
            else if (node.ValueKind == JsonValueKind.Array) foreach (var item in node.EnumerateArray()) Visit(item);
        }
    }
}
