using System.Globalization;
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
        Assert.Equal("1", Source(snapshot, candidate.Material.RepositoryPath)
            .Substring(candidate.SourceStart, candidate.SourceLength));
        Assert.Equal("0", candidate.Material.Replacement);
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
    public async Task FullProjectScopeKeepsGeneratedSourcesForCompilationButNeverMutatesThem()
    {
        WriteProject("src/App", "src/App/**/*.cs");
        _repository.WriteText("src/App/Generated.g.cs",
            "public static class Generated { public const bool Enabled = true; }\n");
        _repository.WriteText("src/App/Flag.cs",
            "public sealed class Flag { public bool Value() => Generated.Enabled; }\n");
        Commit();
        await using var snapshot = await SnapshotCapture.CaptureAsync(_repository.Root, "HEAD", [],
            SnapshotCaptureOptions.Default, CancellationToken.None);
        var configuration = LoadConfiguration(snapshot);
        var scope = new ScopePlan("1", "base", ".", snapshot.Identity.BaseCommit, [], [],
            [new("src/App/App.csproj", ["tests/App.Tests/App.Tests.csproj"],
                ["src/App/Flag.cs"], "FULL_PROJECT")], [], true);

        var result = StrictMutationEnumerator.Enumerate(snapshot, configuration, scope,
            CancellationToken.None);

        Assert.True(result.IsComplete, string.Join(Environment.NewLine, result.Reasons));
        Assert.DoesNotContain(result.Candidates,
            candidate => candidate.Material.RepositoryPath == "src/App/Generated.g.cs");
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
        Assert.Single(result.Candidates,
            candidate => candidate.Material.OperatorId == "binary.AddExpression");
        Assert.Contains(result.Candidates, candidate => candidate.Material.OperatorId == "rvalue.null");
    }

    [Fact]
    public async Task ReleaseConfigurationIncludesTraceButNotDebugBranches()
    {
        WriteProject("src/App", "src/App/**/*.cs");
        _repository.WriteText("src/App/Conditional.cs", """
            public sealed class Conditional
            {
            #if TRACE
                public bool Traced() => true;
            #endif
            #if DEBUG
                public bool DebugOnly() => true;
            #endif
            }
            """);
        Commit();
        await using var snapshot = await SnapshotCapture.CaptureAsync(_repository.Root, "HEAD", [],
            SnapshotCaptureOptions.Default, CancellationToken.None);
        var result = StrictMutationEnumerator.Enumerate(snapshot, LoadConfiguration(snapshot),
            FullProjectScope(snapshot, "src/App/Conditional.cs"), CancellationToken.None);

        Assert.True(result.IsComplete, string.Join(Environment.NewLine, result.Reasons));
        var candidate = Assert.Single(result.Candidates);
        Assert.Contains("Traced", candidate.Material.DeclarationIdentity, StringComparison.Ordinal);
        Assert.DoesNotContain(result.Candidates,
            item => item.Material.DeclarationIdentity.Contains("DebugOnly", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DebugConfigurationIncludesTraceAndDebugBranches()
    {
        WriteProject("src/App", "src/App/**/*.cs", suiteConfiguration: "Debug");
        _repository.WriteText("src/App/Conditional.cs", """
            public sealed class Conditional
            {
            #if TRACE
                public bool Traced() => true;
            #endif
            #if DEBUG
                public bool DebugOnly() => true;
            #endif
            }
            """);
        Commit();
        await using var snapshot = await SnapshotCapture.CaptureAsync(_repository.Root, "HEAD", [],
            SnapshotCaptureOptions.Default, CancellationToken.None);
        var result = StrictMutationEnumerator.Enumerate(snapshot, LoadConfiguration(snapshot),
            FullProjectScope(snapshot, "src/App/Conditional.cs"), CancellationToken.None);

        Assert.True(result.IsComplete, string.Join(Environment.NewLine, result.Reasons));
        Assert.Equal(2, result.Candidates.Count);
        Assert.Contains(result.Candidates,
            item => item.Material.DeclarationIdentity.Contains("Traced", StringComparison.Ordinal));
        Assert.Contains(result.Candidates,
            item => item.Material.DeclarationIdentity.Contains("DebugOnly", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RefusesInheritedBuildConfigurationInsteadOfIgnoringIt()
    {
        WriteProject("src/App", "src/App/**/*.cs");
        _repository.WriteText("Directory.Build.props",
            "<Project><PropertyGroup><DefineConstants>FEATURE</DefineConstants></PropertyGroup></Project>\n");
        _repository.WriteText("src/App/Flag.cs",
            "public sealed class Flag { public bool Value() => true; }\n");
        Commit();
        await using var snapshot = await SnapshotCapture.CaptureAsync(_repository.Root, "HEAD", [],
            SnapshotCaptureOptions.Default, CancellationToken.None);
        var result = StrictMutationEnumerator.Enumerate(snapshot, LoadConfiguration(snapshot),
            FullProjectScope(snapshot, "src/App/Flag.cs"), CancellationToken.None);

        Assert.False(result.IsComplete);
        Assert.Empty(result.Candidates);
        Assert.Contains(result.Reasons,
            reason => reason.Code == "ENUMERATION_INHERITED_BUILD_UNSUPPORTED");
    }

    [Fact]
    public async Task MarkerTextInsideAStringDoesNotMakeProductionSourceGenerated()
    {
        WriteProject("src/App", "src/App/**/*.cs");
        _repository.WriteText("src/App/Flag.cs", """
            public sealed class Flag
            {
                private const string Marker = "<auto-generated";
                public bool Value() => true;
            }
            """);
        Commit();
        await using var snapshot = await SnapshotCapture.CaptureAsync(_repository.Root, "HEAD", [],
            SnapshotCaptureOptions.Default, CancellationToken.None);
        var result = StrictMutationEnumerator.Enumerate(snapshot, LoadConfiguration(snapshot),
            FullProjectScope(snapshot, "src/App/Flag.cs"), CancellationToken.None);

        Assert.True(result.IsComplete, string.Join(Environment.NewLine, result.Reasons));
        Assert.Contains(result.Candidates,
            candidate => candidate.Material.RepositoryPath == "src/App/Flag.cs");
    }

    [Fact]
    public async Task SupportsTopLevelStatementsAndImplicitUsingsForPlainSdkExe()
    {
        WriteProject("src/App", "src/App/**/*.cs");
        _repository.WriteText("src/App/App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup>
            </Project>
            """);
        _repository.WriteText("src/App/Program.cs", "Console.WriteLine(true);\n");
        Commit();
        await using var snapshot = await SnapshotCapture.CaptureAsync(_repository.Root, "HEAD", [],
            SnapshotCaptureOptions.Default, CancellationToken.None);
        var result = StrictMutationEnumerator.Enumerate(snapshot, LoadConfiguration(snapshot),
            FullProjectScope(snapshot, "src/App/Program.cs"), CancellationToken.None);

        Assert.True(result.IsComplete, string.Join(Environment.NewLine, result.Reasons));
        Assert.Single(result.Candidates,
            candidate => candidate.Material.RepositoryPath == "src/App/Program.cs" &&
                candidate.Material.OperatorId == "literal.boolean");
    }

    [Fact]
    public async Task RefusesUnsupportedSdkWithSpecificReason()
    {
        WriteProject("src/App", "src/App/**/*.cs");
        _repository.WriteText("src/App/App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk.Web">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework><Nullable>enable</Nullable></PropertyGroup>
            </Project>
            """);
        _repository.WriteText("src/App/Flag.cs",
            "public sealed class Flag { public bool Value() => true; }\n");
        Commit();
        await using var snapshot = await SnapshotCapture.CaptureAsync(_repository.Root, "HEAD", [],
            SnapshotCaptureOptions.Default, CancellationToken.None);
        var result = StrictMutationEnumerator.Enumerate(snapshot, LoadConfiguration(snapshot),
            FullProjectScope(snapshot, "src/App/Flag.cs"), CancellationToken.None);

        Assert.False(result.IsComplete);
        Assert.Contains(result.Reasons, reason => reason.Code == "ENUMERATION_SDK_UNSUPPORTED");
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

    [Fact]
    public async Task SharedSourceEnumerationIsStableAcrossProjectOrderAndCulture()
    {
        WriteSharedProjects();
        Commit();
        await using var snapshot = await SnapshotCapture.CaptureAsync(_repository.Root, "HEAD", [],
            SnapshotCaptureOptions.Default, CancellationToken.None);
        var configuration = LoadConfiguration(snapshot);
        ProjectScopeUnit[] units =
        [
            new("src/One/One.csproj", ["tests/App.Tests/App.Tests.csproj"], ["shared/Flag.cs"], "FULL_PROJECT"),
            new("src/Two/Two.csproj", ["tests/App.Tests/App.Tests.csproj"], ["shared/Flag.cs"], "FULL_PROJECT")
        ];
        var forward = new ScopePlan("1", "base", ".", snapshot.Identity.BaseCommit, [], [],
            units, [], true);
        var reverse = forward with { ProjectUnits = units.Reverse().ToArray() };
        var first = StrictMutationEnumerator.Enumerate(snapshot, configuration, forward,
            CancellationToken.None);
        var priorCulture = CultureInfo.CurrentCulture;
        var priorUiCulture = CultureInfo.CurrentUICulture;
        StrictMutationEnumerationResult second;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("tr-TR");
            second = StrictMutationEnumerator.Enumerate(snapshot, configuration, reverse,
                CancellationToken.None);
        }
        finally
        {
            CultureInfo.CurrentCulture = priorCulture;
            CultureInfo.CurrentUICulture = priorUiCulture;
        }

        Assert.True(first.IsComplete, string.Join(Environment.NewLine, first.Reasons));
        Assert.True(second.IsComplete, string.Join(Environment.NewLine, second.Reasons));
        Assert.Equal(first.SemanticContextIdentity, second.SemanticContextIdentity);
        Assert.Equal(first.Candidates.Select(CandidateIdentity), second.Candidates.Select(CandidateIdentity));

        static string CandidateIdentity(MutationCandidate candidate) =>
            $"{candidate.MutationId}|{candidate.EvaluationUnitId}|{candidate.SourceStart}|{candidate.SourceLength}";
    }

    [Fact]
    public async Task DuplicateProjectUnitIsRefusedWithSpecificReason()
    {
        WriteProject("src/App", "src/App/**/*.cs");
        _repository.WriteText("src/App/Flag.cs",
            "public sealed class Flag { public bool Value() => true; }\n");
        Commit();
        await using var snapshot = await SnapshotCapture.CaptureAsync(_repository.Root, "HEAD", [],
            SnapshotCaptureOptions.Default, CancellationToken.None);
        var unit = new ProjectScopeUnit("src/App/App.csproj", ["tests/App.Tests/App.Tests.csproj"],
            ["src/App/Flag.cs"], "FULL_PROJECT");
        var scope = new ScopePlan("1", "base", ".", snapshot.Identity.BaseCommit, [], [],
            [unit, unit], [], true);

        var result = StrictMutationEnumerator.Enumerate(snapshot, LoadConfiguration(snapshot), scope,
            CancellationToken.None);

        Assert.False(result.IsComplete);
        Assert.Empty(result.Candidates);
        Assert.Contains(result.Reasons, reason => reason.Code == "ENUMERATION_DUPLICATE_UNIT");
    }

    [Fact]
    public async Task UnsupportedReferenceContextReturnsNoTrustedPartialCandidates()
    {
        WriteProject("src/App", "src/App/**/*.cs");
        _repository.WriteText("src/App/App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework><Nullable>enable</Nullable></PropertyGroup>
              <ItemGroup><PackageReference Include="Example.Package" Version="1.0.0" /></ItemGroup>
            </Project>
            """);
        _repository.WriteText("src/App/Flag.cs", "public sealed class Flag { public bool Value() => true; }\n");
        Commit();
        await using var snapshot = await SnapshotCapture.CaptureAsync(_repository.Root, "HEAD", [],
            SnapshotCaptureOptions.Default, CancellationToken.None);
        var configuration = LoadConfiguration(snapshot);
        var scope = new ScopePlan("1", "base", ".", snapshot.Identity.BaseCommit, [], [],
            [new("src/App/App.csproj", ["tests/App.Tests/App.Tests.csproj"],
                ["src/App/Flag.cs"], "FULL_PROJECT")], [], true);

        var result = StrictMutationEnumerator.Enumerate(snapshot, configuration, scope,
            CancellationToken.None);

        Assert.False(result.IsComplete);
        Assert.Empty(result.Candidates);
        Assert.Contains(result.Reasons, reason => reason.Code == "ENUMERATION_REFERENCE_UNSUPPORTED" &&
            reason.Message.Contains("reference", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ConfigurationCannotInventACompileInput()
    {
        WriteProject("src/App", "src/**/*.cs");
        _repository.WriteText("src/App/Flag.cs", "public sealed class Flag { public bool Value() => true; }\n");
        _repository.WriteText("src/Other.cs", "public sealed class Other { public bool Value() => true; }\n");
        Commit();
        await using var snapshot = await SnapshotCapture.CaptureAsync(_repository.Root, "HEAD", [],
            SnapshotCaptureOptions.Default, CancellationToken.None);
        var configuration = LoadConfiguration(snapshot);
        var scope = new ScopePlan("1", "base", ".", snapshot.Identity.BaseCommit, [], [],
            [new("src/App/App.csproj", ["tests/App.Tests/App.Tests.csproj"],
                ["src/App/Flag.cs"], "FULL_PROJECT")], [], true);

        var result = StrictMutationEnumerator.Enumerate(snapshot, configuration, scope,
            CancellationToken.None);

        Assert.False(result.IsComplete);
        Assert.Empty(result.Candidates);
        Assert.Contains(result.Reasons, reason => reason.Code == "ENUMERATION_COMPILE_INVENTORY_MISMATCH" &&
            reason.Message.Contains("Compile inventory", StringComparison.Ordinal));
    }

    [Fact]
    public async Task StaleChangedDeclarationReturnsNoTrustedPartialCandidates()
    {
        WriteProject("src/App", "src/App/**/*.cs");
        _repository.WriteText("src/App/Flag.cs", "public sealed class Flag { public bool Value() => true; }\n");
        Commit();
        await using var snapshot = await SnapshotCapture.CaptureAsync(_repository.Root, "HEAD", [],
            SnapshotCaptureOptions.Default, CancellationToken.None);
        var configuration = LoadConfiguration(snapshot);
        var stale = new DeclarationScope("Flag.method:Missing`0()", "MethodDeclaration",
            "method Missing", 1, 1, "DECLARATION_CHANGED");
        var scope = new ScopePlan("1", "base", ".", snapshot.Identity.BaseCommit,
            [new("src/App/Flag.cs", null, ChangeKind.Modified, [stale], [], false, [])], [],
            [new("src/App/App.csproj", ["tests/App.Tests/App.Tests.csproj"],
                ["src/App/Flag.cs"], "CHANGED_DECLARATIONS")], [], true);

        var result = StrictMutationEnumerator.Enumerate(snapshot, configuration, scope,
            CancellationToken.None);

        Assert.False(result.IsComplete);
        Assert.Empty(result.Candidates);
        Assert.Contains(result.Reasons, reason => reason.Code == "ENUMERATION_SCOPE_STALE" &&
            reason.Message.Contains("stale", StringComparison.OrdinalIgnoreCase));
    }

    private void WriteProject(string directory, string sourceGlob,
        IReadOnlyList<string>? defineConstants = null, string suiteConfiguration = "Release")
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
        ], suiteConfiguration);
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
        sharedSources = sources.StartsWith("shared/", StringComparison.Ordinal)
            ? new[] { sources } : Array.Empty<string>(),
        testSuites = new[] { "unit" }
    };

    private void WriteConfiguration(IReadOnlyList<object> projects,
        string suiteConfiguration = "Release") => _repository.WriteText(
        "mutate4csharp.json", System.Text.Json.JsonSerializer.Serialize(new
        {
            version = 1,
            projects,
            testSuites = new[]
            {
                new { id = "unit", path = "tests/App.Tests/App.Tests.csproj", runner = "vstest",
                    framework = "net10.0", configuration = suiteConfiguration,
                    expectedMembers = new[] { "App.Tests.dll" } }
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

    private static ScopePlan FullProjectScope(InputSnapshot snapshot, string input) =>
        new("1", "base", ".", snapshot.Identity.BaseCommit, [], [],
            [new("src/App/App.csproj", ["tests/App.Tests/App.Tests.csproj"], [input], "FULL_PROJECT")],
            [], true);

    public void Dispose() => _repository.Dispose();
}
