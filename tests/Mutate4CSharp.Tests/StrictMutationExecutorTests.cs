using System.Text.Json;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace Mutate4CSharp.Tests;

public sealed class StrictMutationExecutorTests : IDisposable
{
    private readonly SnapshotTestRepository _repository = new();

    [Fact(Timeout = 420_000)]
    public async Task ExecutesOneExactMutationInAFreshFrozenWorker()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        WriteFixture();
        _repository.Git("add", ".");
        _repository.Git("commit", "-m", "fixture");
        await using var snapshot = await SnapshotCapture.CaptureAsync(_repository.Root, "HEAD", [],
            SnapshotCaptureOptions.Default, cancellationToken);
        await using var environment = await ExecutionEnvironment.PrepareDependenciesAsync(snapshot,
            ["tests/App.Tests/App.Tests.csproj"],
            DependencyPreparationOptions.Default with { Timeout = TimeSpan.FromMinutes(2) },
            cancellationToken);
        var suite = new SuiteExecution("suite-identity", ["unit"],
            "tests/App.Tests/App.Tests.csproj", "vstest", "net10.0", "Release",
            ["App.Tests.dll"]);
        await using var baseline = await new VstestSuiteExecutor(snapshot, environment)
            .RunBaselineAsync(suite, TimeSpan.FromMinutes(2), cancellationToken);
        Assert.Equal(SuiteRunDisposition.Passed, baseline.Disposition);

        var source = File.ReadAllText(Path.Combine(_repository.Root, "src/App/Flag.cs"));
        var tree = CSharpSyntaxTree.ParseText(SourceText.From(source), path: "src/App/Flag.cs",
            cancellationToken: cancellationToken);
        var root = tree.GetRoot(cancellationToken);
        var token = root.DescendantTokens().Single(item => item.ValueText == "true");
        var mutation = MutationIdentity.Create("src/App/Flag.cs", root, token.Span,
            "literal.boolean", MutationIdentity.OperatorContractVersion, "false");
        var evaluationId = EvaluationUnitIdentity.Compute(new(mutation.MutationId,
            "src/App/App.csproj", "net10.0", "net10-csharp14"));
        var candidate = new MutationCandidate(mutation.MutationId, evaluationId, mutation.Material,
            "src/App/App.csproj", "net10.0", "net10-csharp14", token.SpanStart, token.Span.Length,
            token.Text, 1);
        var executor = new StrictMutationExecutor(snapshot, environment, [candidate], [suite],
            new Dictionary<string, bool>(StringComparer.Ordinal) { [suite.Identity] = true });

        var result = await executor.ExecuteAsync(new ScheduledMutation(mutation.MutationId,
            evaluationId, [suite.Identity]), TimeSpan.FromMinutes(2), cancellationToken);

        Assert.Equal(UnitDisposition.Killed, result.Disposition);
        Assert.Contains(result.Evidence, item => item.Kind == "SUITE_MUTANT_RESULT");
        Assert.Contains(result.Evidence.SelectMany(item => item.Diagnostics ?? []),
            item => item.Contains("FlagTests.ValueIsTrue", StringComparison.Ordinal));
        Assert.Equal(source, File.ReadAllText(Path.Combine(_repository.Root, "src/App/Flag.cs")));
        Assert.False(Directory.Exists(Path.Combine(_repository.Root, "obj")));
        Assert.False(Directory.Exists(Path.Combine(_repository.Root, "bin")));
    }

    [Fact(Timeout = 420_000)]
    public async Task CoordinatorRunsFreshBaselineCoverageAndMutantBeforeFinalizationGate()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        WriteFixture();
        _repository.WriteText("src/App/Flag.cs", """
            namespace App;
            public static class Flag { public static bool Value() => false; }
            """);
        _repository.Git("add", ".");
        _repository.Git("commit", "-m", "baseline");
        _repository.WriteText("src/App/Flag.cs", """
            namespace App;
            public static class Flag { public static bool Value() => true; }
            """);
        var original = File.ReadAllBytes(Path.Combine(_repository.Root, "src/App/Flag.cs"));
        var reportPath = Path.Combine(Path.GetTempPath(), $"strict-execution-{Guid.NewGuid():N}.json");
        var previous = Environment.CurrentDirectory;
        Environment.CurrentDirectory = _repository.Root;
        try
        {
            var result = await new EvaluationCoordinator().RunAsync(
                new(false, "HEAD", [], reportPath, "strict-execution", NoState: true),
                cancellationToken);

            Assert.Equal(EvaluationOutcome.Incomplete, result.Report.Outcome);
            Assert.Equal(BaselineStatus.Green, result.Report.Baseline);
            Assert.NotNull(result.Report.Counts.Enumerated);
            Assert.True(result.Report.Counts.Executed > 0);
            Assert.True(result.Report.Counts.Killed > 0);
            Assert.Contains(result.Report.Units, item => item.Disposition == UnitDisposition.Killed);
            Assert.Contains(result.Report.IncompleteConditions,
                item => item.Code == "FINALIZATION_PENDING");
            Assert.DoesNotContain(result.Report.IncompleteConditions,
                item => item.Code == "EXECUTION_NOT_IMPLEMENTED");
            Assert.Single(result.Report.Suites);
            Assert.Equal(BaselineStatus.Green, result.Report.Suites[0].Baseline);
            Assert.Equal(original, File.ReadAllBytes(Path.Combine(_repository.Root, "src/App/Flag.cs")));
            Assert.False(Directory.Exists(Path.Combine(_repository.Root, "obj")));
            Assert.False(Directory.Exists(Path.Combine(_repository.Root, "bin")));
        }
        finally
        {
            Environment.CurrentDirectory = previous;
            try { File.Delete(reportPath); } catch { }
            try { File.Delete(ReportWriter.LockPath(reportPath)); } catch { }
        }
    }

    public void Dispose() => _repository.Dispose();

    private void WriteFixture()
    {
        _repository.WriteText("src/App/App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework><Nullable>enable</Nullable></PropertyGroup>
            </Project>
            """);
        _repository.WriteText("src/App/Flag.cs", """
            namespace App;
            public static class Flag { public static bool Value() => true; }
            """);
        _repository.WriteText("tests/App.Tests/App.Tests.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework><IsTestProject>true</IsTestProject></PropertyGroup>
              <ItemGroup><ProjectReference Include="../../src/App/App.csproj" /></ItemGroup>
              <ItemGroup>
                <PackageReference Include="Microsoft.NET.Test.Sdk" Version="18.3.0" />
                <PackageReference Include="xunit.v3" Version="3.2.2" />
                <PackageReference Include="xunit.runner.visualstudio" Version="3.1.5" />
                <PackageReference Include="coverlet.collector" Version="6.0.4" />
              </ItemGroup>
            </Project>
            """);
        _repository.WriteText("tests/App.Tests/FlagTests.cs", """
            using App;
            using Xunit;
            public sealed class FlagTests
            {
                [Fact] public void ValueIsTrue() => Assert.True(Flag.Value());
            }
            """);
        _repository.WriteText("NuGet.Config", """
            <configuration><packageSources><clear /><add key="fixture" value="local-packages" /></packageSources></configuration>
            """);
        _repository.WriteText("mutate4csharp.json", """
            {
              "version": 1,
              "projects": [{
                "id": "app",
                "project": "src/App/App.csproj",
                "targetFramework": "net10.0",
                "parseContext": "net10-csharp14",
                "languageVersion": "14.0",
                "nullable": "enable",
                "defineConstants": [],
                "sources": ["src/App/**/*.cs"],
                "testSuites": ["unit"]
              }],
              "testSuites": [{
                "id": "unit",
                "path": "tests/App.Tests/App.Tests.csproj",
                "runner": "vstest",
                "framework": "net10.0",
                "configuration": "Release",
                "expectedMembers": ["App.Tests.dll"]
              }],
              "policy": {
                "maxWorkers": 1,
                "mutationCap": 100,
                "baselineTimeoutSeconds": 120,
                "mutantTimeoutSeconds": 120,
                "overallDeadlineSeconds": 300,
                "allowNotApplicable": false,
                "stabilityRepetitions": 1
              }
            }
            """);
        CopyRestoredTestPackages(Path.Combine(_repository.Root, "local-packages"));
    }

    private static void CopyRestoredTestPackages(string destination)
    {
        var assetsPath = Path.GetFullPath("../../../obj/project.assets.json", AppContext.BaseDirectory);
        using var assets = JsonDocument.Parse(File.ReadAllBytes(assetsPath));
        var packageRoots = assets.RootElement.GetProperty("packageFolders").EnumerateObject()
            .Select(folder => Path.GetFullPath(folder.Name)).ToArray();
        Directory.CreateDirectory(destination);
        foreach (var library in assets.RootElement.GetProperty("libraries").EnumerateObject())
        {
            if (library.Value.GetProperty("type").GetString() != "package") continue;
            var separator = library.Name.LastIndexOf('/');
            var id = library.Name[..separator].ToLowerInvariant();
            var version = library.Name[(separator + 1)..].ToLowerInvariant();
            var relativePackagePath = library.Value.TryGetProperty("path", out var path)
                ? path.GetString()?.Replace('/', Path.DirectorySeparatorChar)
                : Path.Combine(id, version);
            if (string.IsNullOrWhiteSpace(relativePackagePath))
                throw new InvalidOperationException($"Restored package {library.Name} did not declare a path.");
            var archive = $"{id}.{version}.nupkg";
            var matches = packageRoots.Select(root => Path.Combine(root, relativePackagePath, archive))
                .Where(File.Exists).ToArray();
            if (matches.Length != 1)
                throw new InvalidOperationException($"Expected one restored archive for {library.Name}.");
            File.Copy(matches[0], Path.Combine(destination, archive));
        }
    }
}
