using System.Text;

namespace Mutate4CSharp.Tests;

public sealed class StrictMutationEnumeratorTests : IDisposable
{
    private readonly SnapshotTestRepository _repository = new();

    [Fact]
    public async Task EnumeratesOnlyTheChangedDeclarationFromFrozenBytes()
    {
        WriteProject("src/App", "src/App/**/*.cs");
        _repository.WriteText("src/App/Flag.cs", """
            public sealed class Flag
            {
                public int Changed() => 0;
                public int Unchanged() => 1;
            }
            """);
        Commit();
        _repository.WriteText("src/App/Flag.cs", """
            public sealed class Flag
            {
                public int Changed() => 1;
                public int Unchanged() => 1;
            }
            """);

        await using var snapshot = await SnapshotCapture.CaptureAsync(_repository.Root, "HEAD", [],
            SnapshotCaptureOptions.Default, CancellationToken.None);
        var scope = await ScopePlanner.PlanGitAsync(snapshot, CancellationToken.None);
        var configuration = LoadConfiguration(snapshot);

        var result = StrictMutationEnumerator.Enumerate(snapshot, configuration, scope,
            CancellationToken.None);

        Assert.True(result.IsComplete, string.Join(Environment.NewLine, result.Reasons));
        var candidate = Assert.Single(result.Candidates);
        Assert.Contains("method:Changed", candidate.Material.DeclarationIdentity, StringComparison.Ordinal);
        Assert.Equal("0", Source(snapshot, candidate.Material.RepositoryPath)
            .Substring(candidate.SourceStart, candidate.SourceLength));
        Assert.Equal("1", candidate.Material.Replacement);
    }

    [Fact]
    public async Task FullProjectScopeEnumeratesEveryConfiguredCompileItemButNotTests()
    {
        WriteProject("src/App", "src/App/**/*.cs");
        _repository.WriteText("src/App/A.cs", "public sealed class A { public int Value() => 0; }\n");
        _repository.WriteText("src/App/B.cs", "public sealed class B { public bool Value() => true; }\n");
        Commit();
        await using var snapshot = await SnapshotCapture.CaptureAsync(_repository.Root, "HEAD", [],
            SnapshotCaptureOptions.Default, CancellationToken.None);
        var configuration = LoadConfiguration(snapshot);
        var scope = new ScopePlan("1", "base", ".", snapshot.Identity.BaseCommit, [], [],
            [new("src/App/App.csproj", ["tests/App.Tests/App.Tests.csproj"], ["src/App/A.cs"], "FULL_PROJECT")],
            [], true);

        var result = StrictMutationEnumerator.Enumerate(snapshot, configuration, scope,
            CancellationToken.None);

        Assert.True(result.IsComplete, string.Join(Environment.NewLine, result.Reasons));
        Assert.Equal(["src/App/A.cs", "src/App/B.cs"], result.Candidates
            .Select(candidate => candidate.Material.RepositoryPath).Distinct(StringComparer.Ordinal));
        Assert.DoesNotContain(result.Candidates,
            candidate => candidate.Material.RepositoryPath.StartsWith("tests/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UsesOneSemanticContextForStringNumericNullableAndConditionalSites()
    {
        WriteProject("src/App", "src/App/**/*.cs", defineConstants: ["FEATURE"]);
        _repository.WriteText("src/App/Operators.cs", """
            #if FEATURE
            public sealed class Operators
            {
                public int Numeric(int left, int right) => left + right;
                public string Text(string left, string right) => left + right;
                public string? Maybe(string value) => value;
            }
            #endif
            """);
        Commit();
        await using var snapshot = await SnapshotCapture.CaptureAsync(_repository.Root, "HEAD", [],
            SnapshotCaptureOptions.Default, CancellationToken.None);
        var configuration = LoadConfiguration(snapshot);
        var scope = new ScopePlan("1", "base", ".", snapshot.Identity.BaseCommit, [], [],
            [new("src/App/App.csproj", ["tests/App.Tests/App.Tests.csproj"],
                ["src/App/Operators.cs"], "FULL_PROJECT")], [], true);

        var result = StrictMutationEnumerator.Enumerate(snapshot, configuration, scope,
            CancellationToken.None);

        Assert.True(result.IsComplete, string.Join(Environment.NewLine, result.Reasons));
        Assert.Single(result.Candidates.Where(candidate => candidate.Material.OperatorId == "binary.AddExpression"));
        Assert.Contains(result.Candidates, candidate => candidate.Material.OperatorId == "rvalue.null");
    }

    [Fact]
    public async Task SharedSourceHasStableMutationIdentityAndDistinctEvaluationUnits()
    {
        WriteSharedProjects();
        Commit();
        await using var snapshot = await SnapshotCapture.CaptureAsync(_repository.Root, "HEAD", [],
            SnapshotCaptureOptions.Default, CancellationToken.None);
        var configuration = LoadConfiguration(snapshot);
        var scope = new ScopePlan("1", "base", ".", snapshot.Identity.BaseCommit, [], [],
            [
                new("src/One/One.csproj", ["tests/App.Tests/App.Tests.csproj"], ["shared/Flag.cs"], "FULL_PROJECT"),
                new("src/Two/Two.csproj", ["tests/App.Tests/App.Tests.csproj"], ["shared/Flag.cs"], "FULL_PROJECT")
            ], [], true);

        var result = StrictMutationEnumerator.Enumerate(snapshot, configuration, scope,
            CancellationToken.None);

        Assert.True(result.IsComplete, string.Join(Environment.NewLine, result.Reasons));
        Assert.Equal(2, result.Candidates.Count);
        Assert.Single(result.Candidates.Select(candidate => candidate.MutationId).Distinct(StringComparer.Ordinal));
        Assert.Equal(2, result.Candidates.Select(candidate => candidate.EvaluationUnitId)
            .Distinct(StringComparer.Ordinal).Count());
    }

    private void WriteProject(string directory, string sourceGlob,
        IReadOnlyList<string>? defineConstants = null)
    {
        _repository.WriteText($"{directory}/App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework><Nullable>enable</Nullable></PropertyGroup>
            </Project>
            """);
        _repository.WriteText("tests/App.Tests/App.Tests.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework><IsTestProject>true</IsTestProject></PropertyGroup>
            </Project>
            """);
        WriteConfiguration([
            Project("app", $"{directory}/App.csproj", sourceGlob, defineConstants ?? [])
        ]);
    }

    private void WriteSharedProjects()
    {
        _repository.WriteText("src/One/One.csproj", ProjectXml("../../shared/Flag.cs"));
        _repository.WriteText("src/Two/Two.csproj", ProjectXml("../../shared/Flag.cs"));
        _repository.WriteText("shared/Flag.cs", "public sealed class Flag { public bool Value() => true; }\n");
        _repository.WriteText("tests/App.Tests/App.Tests.csproj", """
            <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework><IsTestProject>true</IsTestProject></PropertyGroup></Project>
            """);
        WriteConfiguration([
            Project("one", "src/One/One.csproj", "shared/*.cs", []),
            Project("two", "src/Two/Two.csproj", "shared/*.cs", [])
        ]);

        static string ProjectXml(string linked) => $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework><Nullable>enable</Nullable><EnableDefaultCompileItems>false</EnableDefaultCompileItems></PropertyGroup>
              <ItemGroup><Compile Include="{linked}" Link="Flag.cs" /></ItemGroup>
            </Project>
            """;
    }

    private static object Project(string id, string path, string sources,
        IReadOnlyList<string> defineConstants) => new
    {
        id,
        project = path,
        targetFramework = "net10.0",
        parseContext = "net10-csharp14",
        languageVersion = "14.0",
        nullable = "enable",
        defineConstants,
        sources = new[] { sources },
        testSuites = new[] { "unit" }
    };

    private void WriteConfiguration(IReadOnlyList<object> projects) => _repository.WriteText(
        "mutate4csharp.json", System.Text.Json.JsonSerializer.Serialize(new
        {
            version = 1,
            projects,
            testSuites = new[]
            {
                new { id = "unit", path = "tests/App.Tests/App.Tests.csproj", runner = "vstest",
                    framework = "net10.0", configuration = "Release", expectedMembers = new[] { "App.Tests.dll" } }
            }
        }));

    private void Commit()
    {
        _repository.Git("add", ".");
        _repository.Git("commit", "-m", "fixture");
    }

    private static CheckConfiguration LoadConfiguration(InputSnapshot snapshot)
    {
        var bytes = File.ReadAllBytes(Path.Combine(snapshot.CaptureRoot, "mutate4csharp.json"));
        return CheckConfiguration.Load(snapshot.CaptureRoot, bytes);
    }

    private static string Source(InputSnapshot snapshot, string relative) =>
        File.ReadAllText(Path.Combine(snapshot.CaptureRoot,
            relative.Replace('/', Path.DirectorySeparatorChar)), Encoding.UTF8);

    public void Dispose() => _repository.Dispose();
}
