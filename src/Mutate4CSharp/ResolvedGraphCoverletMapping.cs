using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Text.Json;

namespace Mutate4CSharp;

// Post-compiler collector data only. No extra compiler property/import or
// SourceRoot metadata is introduced into the captured compiler context.
internal sealed class ResolvedGraphCoverletMapping
{
    internal const int MaximumMappingBytes = 65_536;
    private readonly ResolvedGraphInputs _inputs;
    private readonly ResolvedGraphFile[] _pins;
    internal ProcessTreeResult ActualBuild { get; }
    private ResolvedGraphCoverletMapping(ResolvedGraphInputs inputs, ResolvedGraphFile[] pins, ProcessTreeResult actualBuild)
    { _inputs = inputs; _pins = pins; ActualBuild = actualBuild; }

    internal static async Task<ResolvedGraphCoverletMapping> BuildAsync(string dotnet, string sourceRoot,
        string project, string packageRoot, string framework, string configuration, string results,
        IReadOnlyDictionary<string, string?> environment, bool noRestore, TimeSpan timeout,
        ResolvedGraphBudget original, ResolvedGraphInputs inputs, CancellationToken token)
    {
        original.CheckWork();
        inputs.RequireOriginal(original);
        var knownLocalCancellation = false;
        try
        {
            ValidateRoots(sourceRoot, project, packageRoot);
            var query = Path.Combine(results, "resolved-graph-coverage-build.json");
            original.CheckWork();
            if (File.Exists(query) || Directory.Exists(query))
                throw new EvaluationContractException("Fresh actual coverage build query is required; no output adoption.");
            var args = new List<string> { "build", project, "-f", framework, "-c", configuration,
                "-target:Build", "-m:1", "-nodeReuse:false", "-p:UseSharedCompilation=false",
                ResolvedGraphPathProfile.Argument(sourceRoot, packageRoot),
                "-p:NuGetPackageRoot=" + Trailing(packageRoot),
                "-getResultOutputFile:" + query,
                "-getProperty:MSBuildProjectFullPath,TargetFramework,Configuration,TargetPath,AssemblyName" };
            if (noRestore) args.Add("--no-restore");
            ProcessTreeResult built;
            try
            {
                built = await ProcessTree.RunAsync(dotnet, args, sourceRoot, timeout, token, environment,
                    requireLinuxSessionIsolation: true, originalBudget: original);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested &&
                       !original.CancellationRequested && !original.WorkEndpointExpired && original.Observe().Cleanup is null)
            {
                // Only this actual launcher await: it either refused before birth
                // or retired its exact process/observers before rethrowing OCE.
                // An unknown native/IO close throws SnapshotCleanupException instead.
                knownLocalCancellation = true;
                throw;
            }
            RequireSuccessfulBuild(built, original);
            original.CheckWork();
            var actualQuery = inputs.Read(query, "config", MaximumMappingBytes);
            inputs.ReserveAllocation(checked(8L * actualQuery.Bytes.LongLength + 8192));
            var location = ReadLocation(actualQuery.Bytes, sourceRoot, project, framework, configuration);
            var assembly = inputs.Read(location.TargetPath, "reference");
            using (var pe = new PEReader(new MemoryStream(assembly.Bytes, false)))
            {
                if (!pe.HasMetadata || !pe.GetMetadataReader().IsAssembly ||
                    pe.GetMetadataReader().GetString(pe.GetMetadataReader().GetAssemblyDefinition().Name) != location.AssemblyName)
                    throw new EvaluationContractException("Actual coverage output does not join its PE assembly name.");
            }
            var map = Path.Combine(Path.GetDirectoryName(location.TargetPath)!, "CoverletSourceRootsMapping_" + location.AssemblyName);
            original.CheckWork();
            ResolvedGraphFile? before = File.Exists(map) ? inputs.Read(map, "config", MaximumMappingBytes) : null;
            inputs.ReserveAllocation(8L * MaximumMappingBytes);
            var suffix = AppendBytes(before?.Bytes, sourceRoot, project, packageRoot);
            ResolvedGraphFile after;
            if (before is null)
            {
                inputs.WriteNewResult(map, suffix);
                after = inputs.Read(map, "config", MaximumMappingBytes);
            }
            else after = inputs.AppendKnownResult(before, suffix, MaximumMappingBytes);
            inputs.Recheck(actualQuery, MaximumMappingBytes); inputs.Recheck(assembly);
            inputs.Recheck(after, MaximumMappingBytes);
            return new(inputs, [actualQuery, assembly, after], built);
        }
        catch (OperationCanceledException) when (knownLocalCancellation && !original.CancellationRequested &&
                   !original.WorkEndpointExpired && original.Observe().Cleanup is null)
        {
            throw; // Preserve exact subordinate cancellation, not aggregate failure.
        }
        catch (KnownLocalBuildTimeoutException) when (!original.CancellationRequested && !original.WorkEndpointExpired &&
                   original.Observe().Cleanup is null)
        {
            throw; // Known returned/settled local timer; keep the SAME original available.
        }
        catch
        {
            original.BeginFailureCleanup();
            throw;
        }
    }

    internal static void RequireSuccessfulBuild(ProcessTreeResult built, ResolvedGraphBudget original)
    {
        if (built.TimedOut)
        {
            original.CheckWork(); // Cancellation/expiry/terminal cleanup is never a local timeout.
            throw new KnownLocalBuildTimeoutException(built);
        }
        if (built.ExitCode != 0)
        {
            var failure = new SnapshotCaptureException("Actual graph coverage build did not complete successfully.");
            failure.Data["ResolvedGraphCoverage.ActualBuild"] = built;
            throw failure;
        }
    }

    private sealed class KnownLocalBuildTimeoutException : TimeoutException
    {
        internal KnownLocalBuildTimeoutException(ProcessTreeResult built)
            : base("Actual graph coverage build exceeded its timeout.")
        { Data["ResolvedGraphCoverage.ActualBuild"] = built; }
    }

    internal void Recheck()
    { foreach (var pin in _pins) _inputs.Recheck(pin, pin.Role == "config" ? MaximumMappingBytes : ResolvedGraphInputs.MaximumFileBytes); }

    internal static (string TargetPath, string AssemblyName) ReadLocation(byte[] bytes, string sourceRoot,
        string project, string framework, string configuration)
    {
        if (bytes.Length > MaximumMappingBytes) throw new SnapshotLimitException("Coverage output query extent.");
        ResolvedGraphProtocol.RequireUnique(bytes);
        using var json = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 8 });
        var root = json.RootElement;
        if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 1 ||
            !root.TryGetProperty("Properties", out var properties) || properties.ValueKind != JsonValueKind.Object ||
            properties.EnumerateObject().Count() != 5 ||
            properties.EnumerateObject().Any(p => p.Value.ValueKind != JsonValueKind.String))
            throw new EvaluationContractException("Complete actual coverage output query required.");
        var names = new[] { "MSBuildProjectFullPath", "TargetFramework", "Configuration", "TargetPath", "AssemblyName" };
        if (names.Any(n => !properties.TryGetProperty(n, out _)) ||
            !Same(properties.GetProperty(names[0]).GetString()!, project) ||
            properties.GetProperty(names[1]).GetString() != framework || properties.GetProperty(names[2]).GetString() != configuration)
            throw new EvaluationContractException("Actual coverage build differs from its selected project context.");
        var target = properties.GetProperty("TargetPath").GetString()!;
        var assembly = properties.GetProperty("AssemblyName").GetString()!;
        RequireInside(sourceRoot, target);
        if (assembly.Length is < 1 or > 128 || assembly.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or '-' or '_')) ||
            Path.GetFileName(target) != assembly + ".dll")
            throw new EvaluationContractException("Actual coverage output name is unsupported.");
        return (Path.GetFullPath(target), assembly);
    }

    // Exact pinned Coverlet6.0.4 format: project|physical-root=mapped-root.
    // Its reader uses the first matching virtual prefix; never append behind a
    // foreign/conflicting alias or silently discard existing record bytes.
    internal static byte[] AppendBytes(byte[]? before, string sourceRoot, string project, string packageRoot)
    {
        ValidateRoots(sourceRoot, project, packageRoot);
        var encoding = new UnicodeEncoding(false, true, true);
        var text = "";
        if (before is not null)
        {
            if (before.Length is < 2 or > MaximumMappingBytes || before[0] != 0xff || before[1] != 0xfe)
                throw new EvaluationContractException("Pinned Coverlet UTF16 mapping profile required.");
            text = encoding.GetString(before, 2, before.Length - 2);
            using var lines = new StringReader(text); string? line; var count = 0;
            while ((line = lines.ReadLine()) is not null)
            {
                if (line.Length == 0) continue;
                if (++count > 256 || line.Length > 16_384) throw new SnapshotLimitException("Coverage mapping record extent.");
                var pipe = line.IndexOf('|'); var equals = line.IndexOf('=');
                if (pipe <= 0 || equals <= pipe + 1 || line.IndexOf('|', pipe + 1) >= 0 || line.IndexOf('=', equals + 1) >= 0)
                    throw new EvaluationContractException("Malformed existing Coverlet mapping record.");
                var declaredProject = line[..pipe]; var physical = line[(pipe + 1)..equals]; var mapped = line[(equals + 1)..];
                RequireInside(sourceRoot, declaredProject);
                if (!declaredProject.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) || physical.Contains(';') ||
                    !(Same(physical, sourceRoot) && (Same(mapped, sourceRoot) || mapped == ResolvedGraphPathProfile.Source + "/") ||
                      Same(physical, packageRoot) && (Same(mapped, packageRoot) || mapped == ResolvedGraphPathProfile.Packages + "/")))
                    throw new EvaluationContractException("Existing Coverlet mapping is outside the exact owned pair.");
            }
        }
        var append = (text.Length > 0 && !text.EndsWith('\n') ? "\r\n" : "") +
            project + "|" + Trailing(sourceRoot) + "=" + ResolvedGraphPathProfile.Source + "/\r\n" +
            project + "|" + Trailing(packageRoot) + "=" + ResolvedGraphPathProfile.Packages + "/\r\n";
        var suffix = encoding.GetBytes(append);
        if ((before?.Length ?? 2) + suffix.Length > MaximumMappingBytes)
            throw new SnapshotLimitException("Complete Coverlet mapping extent.");
        return before is null ? encoding.GetPreamble().Concat(suffix).ToArray() : suffix;
    }

    private static void ValidateRoots(string sourceRoot, string project, string packageRoot)
    {
        _ = ResolvedGraphPathProfile.Argument(sourceRoot, packageRoot);
        foreach (var path in new[] { sourceRoot, project, packageRoot })
            if (!Path.IsPathFullyQualified(path) || path.Length > 4096 || path.IndexOfAny(['|', '=', ';', '\r', '\n']) >= 0)
                throw new EvaluationContractException("Unambiguous owned Coverlet path profile required.");
        RequireInside(sourceRoot, project);
    }
    private static string Trailing(string path) => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
    private static bool Same(string actual, string expected) => Path.IsPathFullyQualified(actual) &&
        string.Equals(Path.GetFullPath(actual).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetFullPath(expected).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    private static void RequireInside(string root, string path)
    {
        if (!Path.IsPathFullyQualified(path)) throw new EvaluationContractException("Absolute owned coverage path required.");
        var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(path));
        if (relative is "." or ".." || Path.IsPathFullyQualified(relative) || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new EvaluationContractException("Actual coverage output escapes its owned clone.");
    }
}
