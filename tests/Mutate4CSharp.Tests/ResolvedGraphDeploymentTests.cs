using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace Mutate4CSharp.Tests;

public sealed class ResolvedGraphDeploymentTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mutate-deployment-" + Guid.NewGuid().ToString("N"));
    private readonly Dictionary<string, string?> _environment = new[] { "MUTATE4CSHARP_GRAPH_CAPTURE_MANIFEST",
        "MUTATE4CSHARP_GRAPH_CAPTURE_MANIFEST_SHA256", "DOTNET_HOST_PATH" }
        .ToDictionary(key => key, Environment.GetEnvironmentVariable);

    public ResolvedGraphDeploymentTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void AbsentManifestHasGraphUnavailableReasonAfterFirstDisposition()
    {
        Select(Path.Combine(_root, "absent.json"), new string('0', 64));
        var failure = Refusal("GRAPH_DEPLOYMENT_UNAVAILABLE");
        Assert.Contains("FileNotFoundException", failure.InnerException!.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void WrongRevisionHasGraphStaleReason()
    {
        WriteDeployment(new string('0', 40));
        Refusal("GRAPH_DEPLOYMENT_STALE");
    }

    [Fact]
    public void HostMismatchHasGraphStaleReasonBeforeAnyHelperCanRun()
    {
        WriteDeployment(ActualRevision());
        Environment.SetEnvironmentVariable("DOTNET_HOST_PATH", Path.Combine(_root, "other", "dotnet"));
        var failure = Refusal("GRAPH_DEPLOYMENT_STALE");
        Assert.Contains("host selections must be identical", failure.InnerException!.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void MissingExplicitHostHasGraphUnavailableReason()
    {
        WriteDeployment(ActualRevision());
        Environment.SetEnvironmentVariable("DOTNET_HOST_PATH", null);
        Refusal("GRAPH_DEPLOYMENT_UNAVAILABLE");
    }

    [Fact]
    public void ChangedImageIsGraphStaleRatherThanSourceSnapshotDivergence()
    {
        WriteDeployment(ActualRevision());
        File.AppendAllText(Path.Combine(_root, "logger", "Mutate4CSharp.GraphCapture.dll"), "drift");
        var failure = Refusal("GRAPH_DEPLOYMENT_STALE");
        Assert.Contains("deployment bytes changed", failure.InnerException!.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void CancellationIsNotRelabeledAsDeploymentDrift()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => ResolvedGraphDeployment.LoadForExecution(
            new ResolvedGraphBudget(TimeSpan.FromSeconds(30), cancellation.Token)));
    }

    private static string ActualRevision() => typeof(ResolvedGraphDeployment).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == "MutateSourceRevision").Value!;

    private StrictExecutionRefusalException Refusal(string code)
    {
        var failure = Assert.Throws<StrictExecutionRefusalException>(() => ResolvedGraphDeployment.LoadForExecution(
            new ResolvedGraphBudget(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken)));
        Assert.Equal(code, failure.Reason.Code);
        Assert.All(failure.Reasons, reason => Assert.Equal(code, reason.Code));
        Assert.NotNull(failure.InnerException);
        return failure;
    }

    private void WriteDeployment(string revision)
    {
        var host = Path.Combine(_root, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
        Environment.SetEnvironmentVariable("DOTNET_HOST_PATH", host);
        var logger = new[] { "Mutate4CSharp.GraphCapture.dll", "Mutate4CSharp.GraphCapture.pdb",
            "Mutate4CSharp.GraphCapture.deps.json", "Microsoft.CodeAnalysis.dll", "Microsoft.CodeAnalysis.CSharp.dll" }
            .Select(name => Pin("logger/" + name)).ToArray();
        var worker = new[] { "Mutate4CSharp.GraphWorker.dll", "Mutate4CSharp.GraphWorker.pdb",
            "Mutate4CSharp.GraphWorker.deps.json", "Mutate4CSharp.GraphWorker.runtimeconfig.json",
            "Microsoft.CodeAnalysis.dll", "Microsoft.CodeAnalysis.CSharp.dll" }.Select(name => Pin("worker/" + name)).ToArray();
        var deployment = new ResolvedGraphDeployment(1, revision,
            new("10.0.401", logger, Pin("Microsoft.Build.Tasks.CodeAnalysis.dll"), Pin("Microsoft.CSharp.Core.targets")),
            Pin(Path.GetFileName(host)), worker, []);
        var manifest = Path.Combine(_root, "deployment.json");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(deployment);
        File.WriteAllBytes(manifest, bytes);
        Select(manifest, Convert.ToHexStringLower(SHA256.HashData(bytes)));
        // These are deliberately unexecutable test DATA, never a valid installed
        // deployment. Every case refuses before source stamps/native dispatch.
    }

    private GraphProducerImage Pin(string relative)
    {
        var path = Path.GetFullPath(relative, _root);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "unexecutable refusal fixture");
        var bytes = File.ReadAllBytes(path);
        return new(path, bytes.Length, Convert.ToHexStringLower(SHA256.HashData(bytes)));
    }

    private static void Select(string manifest, string digest)
    {
        Environment.SetEnvironmentVariable("MUTATE4CSHARP_GRAPH_CAPTURE_MANIFEST", manifest);
        Environment.SetEnvironmentVariable("MUTATE4CSHARP_GRAPH_CAPTURE_MANIFEST_SHA256", digest);
    }

    public void Dispose()
    {
        foreach (var (key, value) in _environment) Environment.SetEnvironmentVariable(key, value);
        Directory.Delete(_root, recursive: true);
    }
}
