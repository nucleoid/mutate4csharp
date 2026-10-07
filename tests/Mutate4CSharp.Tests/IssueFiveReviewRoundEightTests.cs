using System.Diagnostics;
using System.Reflection;

namespace Mutate4CSharp.Tests;

public sealed class IssueFiveReviewRoundEightTests : IDisposable
{
    private static readonly string RepositoryRoot = Path.GetFullPath("../../../../../", AppContext.BaseDirectory);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mutate4csharp-issue5-r8",
        Guid.NewGuid().ToString("N"));

    public IssueFiveReviewRoundEightTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task LegacySuccessfulRunLeavesDetachedDescendantAlive()
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/usr/bin/setsid")) return;
        var marker = Path.Combine(_root, "legacy-descendant.pid");
        var command = $"/usr/bin/setsid /bin/sh -c 'printf %s $$ > \"$1\"; exec /bin/sleep 300' child {Shell(marker)} " +
            "</dev/null >/dev/null 2>&1 & " +
            $"while [ ! -s {Shell(marker)} ]; do /bin/sleep 0.01; done; /bin/sleep 0.2";

        var result = await ProcessTree.RunAsync("/bin/sh", ["-c", command], _root, TimeSpan.FromSeconds(15),
            TestContext.Current.CancellationToken, requireLinuxSessionIsolation: false);
        var descendantPid = int.Parse(File.ReadAllText(marker), System.Globalization.CultureInfo.InvariantCulture);
        using var descendant = Process.GetProcessById(descendantPid);
        var descendantStart = descendant.StartTime;
        try
        {
            Assert.Equal(0, result.ExitCode);
            Assert.False(descendant.HasExited,
                $"Legacy successful execution unexpectedly reaped detached descendant {descendantPid}.");
        }
        finally
        {
            try
            {
                if (!descendant.HasExited && descendant.StartTime == descendantStart)
                {
                    descendant.Kill(entireProcessTree: true);
                    descendant.WaitForExit(5_000);
                }
            }
            catch (InvalidOperationException) { }
        }
    }

    [Fact]
    public void LinuxBoundaryRequiresAnExplicitIsolationMode()
    {
        var boundary = typeof(ProcessTree).GetNestedType("LinuxProcessBoundary", BindingFlags.NonPublic);
        Assert.NotNull(boundary);
        Assert.Contains(boundary!.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic),
            constructor => constructor.GetParameters().Any(parameter => parameter.ParameterType == typeof(bool)));
    }

    [Fact]
    public void ExplicitPlanningReturnsTheValidatedConfigurationSnapshot()
    {
        var method = typeof(ScopePlanner).GetMethod(nameof(ScopePlanner.PlanExplicitAsync),
            BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(method);
        var resultType = method!.ReturnType.GetGenericArguments().Single();
        Assert.NotEqual(typeof(ScopePlan), resultType);
        Assert.NotNull(resultType.GetProperty("Plan"));
        Assert.NotNull(resultType.GetProperty("ConfigurationBytes"));
    }

    [Fact]
    public async Task ExplicitPlanningConfigurationBytesRemainTheValidatedSnapshot()
    {
        var source = Path.Combine(_root, "src", "App", "A.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        File.WriteAllText(source, "class A { int Value() => 1; }\n");
        var configurationPath = Path.Combine(_root, "mutate4csharp.json");
        File.WriteAllText(configurationPath, StrictConfiguration);

        var planned = await ScopePlanner.PlanExplicitAsync([source], _root,
            TestContext.Current.CancellationToken);
        File.WriteAllText(configurationPath, StrictConfiguration.Replace("\"maxWorkers\": 3",
            "\"maxWorkers\": 31", StringComparison.Ordinal));

        var validated = CheckConfiguration.Load(_root, Assert.IsType<byte[]>(planned.ConfigurationBytes));
        Assert.Equal(3, validated.Policy.MaxWorkers);
    }

    [Fact]
    public void LinuxObservationUsesACoarseBackoffInterval()
    {
        var field = typeof(ProcessTree).GetField("LinuxGroupScanInitialDelay",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(field);
        var delay = Assert.IsType<TimeSpan>(field!.GetValue(null));
        Assert.True(delay >= TimeSpan.FromMilliseconds(100), $"Observation delay was only {delay}.");
    }

    [Fact]
    public void RealVstestFixtureCoversAllBudgetsAndPublishesPidAtomically()
    {
        var source = File.ReadAllText(Path.Combine(RepositoryRoot,
            "tests/Mutate4CSharp.Tests/IssueFiveReviewRoundTwoTests.cs"));
        Assert.Contains("Fact(Timeout = 420_000)", source, StringComparison.Ordinal);
        Assert.Contains("File.Move(markerTemporary", source, StringComparison.Ordinal);
        Assert.Contains("new FileInfo(path).Length == 0", source, StringComparison.Ordinal);
        Assert.Contains("int.TryParse", source, StringComparison.Ordinal);
    }

    private static string Shell(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    private const string StrictConfiguration = """
        {
          "version": 1,
          "projects": [{
            "id": "app", "project": "src/App/App.csproj", "targetFramework": "net10.0",
            "parseContext": "default", "sources": ["src/App/**/*.cs"], "testSuites": ["unit"]
          }],
          "testSuites": [{
            "id": "unit", "path": "tests/App.Tests/App.Tests.csproj", "runner": "vstest",
            "framework": "net10.0", "configuration": "Release", "expectedMembers": ["App.Tests.dll"]
          }],
          "policy": { "maxWorkers": 3 }
        }
        """;

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }
}
