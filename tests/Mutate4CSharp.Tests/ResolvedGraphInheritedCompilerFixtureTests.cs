using System.Text.Json;

namespace Mutate4CSharp.Tests;

// New candidate integration source, NOT part of the compiled193-input checkpoint.
// UNEXECUTED. This invokes actual restore/compiler producers, never query DTOs.
public sealed class ResolvedGraphInheritedCompilerFixtureTests : IDisposable
{
    private readonly SnapshotTestRepository _repository = new();

    [Fact]
    public async Task Actual_inherited_CPM_reference_conditional_and_generated_inputs_precede_drift_refusal()
    {
        WriteFixture();
        _repository.Git("add", ".");
        _repository.Git("commit", "-m", "owned inherited compiler fixture");
        var token = TestContext.Current.CancellationToken;
        var original = new ResolvedGraphBudget(TimeSpan.FromSeconds(1100), token);
        await using var snapshot = await SnapshotCapture.CaptureAsync(_repository.Root, "HEAD", [],
            SnapshotCaptureOptions.Default, token);
        original.CheckWork();
        using var producerInputs = new ResolvedGraphInputs(original);
        var manifestPath = Environment.GetEnvironmentVariable("MUTATE4CSHARP_GRAPH_CAPTURE_MANIFEST")
            ?? throw new IOException("Root-bound actual selected401 logger deployment manifest is required.");
        var manifestSha = Environment.GetEnvironmentVariable("MUTATE4CSHARP_GRAPH_CAPTURE_MANIFEST_SHA256")
            ?? throw new IOException("Exact actual deployment manifest digest is required.");
        var manifest = producerInputs.Read(manifestPath, "config", 1_048_576);
        Assert.Equal(manifestSha, manifest.Sha256);
        original.CheckWork();
        var logger = ResolvedGraphDeployment.Load(producerInputs).Logger;
        original.CheckWork();
        var configuration = CheckConfiguration.Load(_repository.Root,
            _repository.Read(".mutate4csharp.json"));
        await using var environment = await ExecutionEnvironment.PrepareDependenciesAsync(snapshot,
            ["src/App/App.csproj", "tests/CompilerSuite/CompilerSuite.csproj"],
            DependencyPreparationOptions.Default with { Timeout = original.RemainingWork }, token);
        original.CheckWork();
        await using var capture = await ResolvedGraphCapture.CreateAsync(snapshot, configuration,
            environment, original.RemainingWork, token, logger, original);
        original.CheckWork();

        Assert.Equal(3, capture.Projects.Count);
        var app = Assert.Single(capture.Projects, project => project.Selection.Project == "src/App/App.csproj");
        var dependency = Assert.Single(capture.Projects,
            project => project.Selection.Project == "src/Dependency/Dependency.csproj");
        var suite = Assert.Single(capture.Projects,
            project => project.Selection.Project == "tests/CompilerSuite/CompilerSuite.csproj");
        Assert.All(capture.Projects, project =>
        {
            Assert.Equal("Release", project.Selection.Configuration);
            Assert.Equal("net10.0", project.Selection.Framework);
            Assert.Equal("10.0.401", project.Properties["NETCoreSdkVersion"]);
            Assert.Equal("enable", project.Properties["Nullable"]);
            Assert.Contains("INHERITED_FIXTURE", project.Properties["DefineConstants"], StringComparison.Ordinal);
            var first = Assert.IsType<ResolvedGraphObservedTask>(project.FirstCsc);
            Assert.True(first.ProjectInstanceId >= 0);
            Assert.Equal(project.Selection.Framework, first.EvaluatedProperties["TargetFramework"]);
            Assert.Equal(project.Selection.Configuration, first.EvaluatedProperties["Configuration"]);
            first.RequireObservedExecutionSwitch();
            Assert.NotEmpty(first.Tokens);
            Assert.Equal(first.Tokens,
                project.Items["CscCommandLineArgs"].Select(item => item.Identity));
        });
        Assert.True(bool.Parse(app.Properties["ManagePackageVersionsCentrally"]));
        Assert.EndsWith("Directory.Packages.props", app.Properties["DirectoryPackagesPropsPath"]);
        Assert.Contains(app.Items["ReferencePathWithRefAssemblies"], item =>
            Path.GetFileName(item.Identity) == "Microsoft.Bcl.AsyncInterfaces.dll");
        Assert.Contains(app.Edges, edge => edge.Selection == dependency.Selection);
        Assert.Contains(suite.Edges, edge => edge.Selection == app.Selection);
        Assert.Contains(app.Items["Compile"], item => item.Identity.EndsWith("GeneratedFixture.cs", StringComparison.Ordinal));
        Assert.DoesNotContain(app.Items["Compile"], item => item.Identity.EndsWith("Disabled.cs", StringComparison.Ordinal));
        capture.ValidateAuthoredInputs();
        producerInputs.Recheck(manifest);
        original.CheckWork();

        Assert.Null(original.Observe().Cleanup);
        // Alter a real captured import only AFTER the complete actual positive.
        // This must be detected as byte drift, not silently re-evaluated/adopted.
        var imported = Path.Combine(capture.Root, "Directory.Build.props");
        var before = File.ReadAllBytes(imported);
        File.AppendAllText(imported, "\n<!-- changed after original compiler capture -->\n");
        Assert.NotEqual(before, File.ReadAllBytes(imported));
        var drift = Assert.Throws<SnapshotDivergedException>(capture.ValidateAuthoredInputs);
        var terminal = original.Observe();
        Assert.NotNull(terminal.Cleanup);
        Assert.Equal(terminal, capture.ObserveOriginalBudget());
        Assert.Throws<EvaluationContractException>(original.CheckWork);
        Assert.Same(drift, Assert.Throws<SnapshotDivergedException>(capture.ValidateAuthoredInputs));
        Assert.Equal(terminal.Cleanup, original.Observe().Cleanup);
        original.CheckDisposition();
        Assert.Equal(terminal.Cleanup, original.Observe().Cleanup);
        // Do not restore the changed clone and retry the failed original.
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Actual_reference_evaluated_framework_differs_from_root_and_ambiguous_instances_refuse(bool ambiguousInstances)
    {
        WriteFixture();
        // The SDK carries netstandard2.1 reference packs. This is an authored
        // child project evaluation, never a global property injected by capture.
        _repository.WriteText("src/Dependency/Dependency.csproj", """
            <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>netstandard2.1</TargetFramework></PropertyGroup></Project>
            """);
        if (ambiguousInstances)
        {
            // Two actual request configurations share path/TFM/configuration,
            // with distinct authored intermediate outputs. They are not one
            // first compiler context that may be selected by path alone.
            var project = new System.Text.UTF8Encoding(false, true).GetString(_repository.Read("src/App/App.csproj"));
            project = project.Replace("</Project>", """
                <Target Name="ActualAmbiguousReferenceInstances" BeforeTargets="CoreCompile" Condition="'$(MSBuildProjectName)' == 'App'">
                  <MSBuild Projects="../Dependency/Dependency.csproj" Targets="Compile" BuildInParallel="false"
                    Properties="TargetFramework=netstandard2.1;Configuration=Release;ContextMarker=A;IntermediateOutputPath=obj/context-a/" />
                  <MSBuild Projects="../Dependency/Dependency.csproj" Targets="Compile" BuildInParallel="false"
                    Properties="TargetFramework=netstandard2.1;Configuration=Release;ContextMarker=B;IntermediateOutputPath=obj/context-b/" />
                </Target></Project>
                """, StringComparison.Ordinal);
            _repository.WriteText("src/App/App.csproj", project);
        }
        _repository.Git("add", "."); _repository.Git("commit", "-m", "different actual reference framework");
        var token = TestContext.Current.CancellationToken;
        var original = new ResolvedGraphBudget(TimeSpan.FromSeconds(1100), token);
        await using var snapshot = await SnapshotCapture.CaptureAsync(_repository.Root, "HEAD", [], SnapshotCaptureOptions.Default, token);
        using var producerInputs = new ResolvedGraphInputs(original);
        var manifestPath = Environment.GetEnvironmentVariable("MUTATE4CSHARP_GRAPH_CAPTURE_MANIFEST")
            ?? throw new IOException("Root-bound selected logger manifest required.");
        var manifestSha = Environment.GetEnvironmentVariable("MUTATE4CSHARP_GRAPH_CAPTURE_MANIFEST_SHA256")
            ?? throw new IOException("Root-bound selected logger digest required.");
        var manifest = producerInputs.Read(manifestPath, "config", 1_048_576);
        Assert.Equal(manifestSha, manifest.Sha256);
        var logger = ResolvedGraphDeployment.Load(producerInputs).Logger;
        var configuration = CheckConfiguration.Load(_repository.Root, _repository.Read(".mutate4csharp.json"));
        await using var environment = await ExecutionEnvironment.PrepareDependenciesAsync(snapshot,
            ["src/App/App.csproj", "tests/CompilerSuite/CompilerSuite.csproj"],
            DependencyPreparationOptions.Default with { Timeout = original.RemainingWork }, token);
        if (ambiguousInstances)
        {
            var refusal = await Assert.ThrowsAsync<EvaluationContractException>(() => ResolvedGraphCapture.CreateAsync(
                snapshot, configuration, environment, original.RemainingWork, token, logger, original));
            Assert.Contains("Ambiguous or unavailable actual Csc project-instance context", refusal.Message, StringComparison.Ordinal);
            Assert.NotNull(original.Observe().Cleanup);
            Assert.Throws<EvaluationContractException>(original.CheckWork);
            return;
        }
        await using var capture = await ResolvedGraphCapture.CreateAsync(snapshot, configuration,
            environment, original.RemainingWork, token, logger, original);
        Assert.Equal(3, capture.Projects.Count);
        var app = Assert.Single(capture.Projects, project => project.Selection.Project == "src/App/App.csproj");
        var dependency = Assert.Single(capture.Projects, project => project.Selection.Project == "src/Dependency/Dependency.csproj");
        Assert.Equal("net10.0", app.Selection.Framework);
        Assert.Equal("netstandard2.1", dependency.Selection.Framework);
        Assert.Contains(app.Edges, edge => edge.Selection == dependency.Selection);
        var first = Assert.IsType<ResolvedGraphObservedTask>(dependency.FirstCsc);
        Assert.Equal("netstandard2.1", first.EvaluatedProperties["TargetFramework"]);
        Assert.Equal("Release", first.EvaluatedProperties["Configuration"]);
        Assert.True(first.ProjectInstanceId >= 0);
        Assert.False(first.GlobalProperties.ContainsKey("TargetFramework"));
        Assert.NotEqual(app.FirstCsc!.EventIdentity, first.EventIdentity);
        first.RequireObservedExecutionSwitch();
        capture.ValidateAuthoredInputs(); producerInputs.Recheck(manifest);
        Assert.Null(original.Observe().Cleanup);
    }

    [Fact]
    public void Failed_restore_diagnostic_keeps_first_error_after_long_path_and_before_trailing_statuses()
    {
        // Formatting DATA only: this does not claim an actual NuGet invocation.
        var stream = new string('p', 1200) + ": error NU1004: Lock target framework differs; source https://user:secret@example.invalid/feed?token=hidden\n" +
            string.Concat(Enumerable.Repeat("Restored unrelated project successfully.\n", 100));
        var returned = new ProcessTreeResult(1, false, stream, "", TimeSpan.FromSeconds(1));
        var failure = FrozenExecutionEnvironment.CreateRestoreFailure(returned);
        Assert.IsType<SnapshotCaptureException>(failure);
        Assert.Contains("source=stdout; error NU1004: Lock target framework differs", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Restored unrelated", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("token=", failure.Message, StringComparison.Ordinal);
        Assert.True(failure.Message.Length <= EvaluationEvidence.MaxDiagnosticLength);
        Assert.Same(returned, failure.Data["FrozenWorkerRestore.ActualReturn"]);
        // Pure lock-format DATA. These bytes are not actual NuGet output and
        // cannot grant prepared-lock, restore or genuine-producer acceptance.
        static byte[] LockData(string text) => System.Text.Encoding.UTF8.GetBytes(text);
        Assert.Equal("targetCount=2; targets=net10.0,netstandard2.1", ResolvedGraphCapture.DescribePreparedLockTargets(
            LockData("{\"dependencies\":{\"netstandard2.1\":{},\"net10.0\":{}}}")));
        Assert.Equal("targetCount=1; targets=[target-withheld]", ResolvedGraphCapture.DescribePreparedLockTargets(
            LockData("{\"dependencies\":{\"https://user:secret@example.invalid\":{}}}")));
        Assert.Throws<EvaluationContractException>(() => ResolvedGraphCapture.DescribePreparedLockTargets(
            LockData("{\"dependencies\":{\"net10.0\":{},\"net10.0\":{}}}")));
        Assert.Throws<EvaluationContractException>(() => ResolvedGraphCapture.DescribePreparedLockTargets(
            LockData("{\"dependencies\":{},\"dependencies\":{}}")));
        Assert.Throws<ArgumentException>(() => FrozenExecutionEnvironment.CreateRestoreFailure(returned with { ExitCode = 0 }));

        // Additional synthetic formatting DATA only. No project/process is constructed.
        var root = OperatingSystem.IsWindows() ? @"C:\private-formatting-root" : "/private-formatting-root";
        var app = Path.Combine(root, "src", "App", "App.csproj");
        var dependency = Path.Combine(root, "src", "Dependency", "Dependency.csproj");
        var contextualReturn = returned with { StandardOutput = "location: error NU1004: Lock target differs [" + dependency + "]\n" };
        var contextual = FrozenExecutionEnvironment.CreateRestoreFailure(contextualReturn, app, "net10.0", "Release", root);
        Assert.Contains("requested=src/App/App.csproj", contextual.Message, StringComparison.Ordinal);
        Assert.Contains("errorProject=src/Dependency/Dependency.csproj", contextual.Message, StringComparison.Ordinal);
        Assert.Contains("projectMatches=False", contextual.Message, StringComparison.Ordinal);
        Assert.Contains("error NU1004", contextual.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(root, contextual.Message, StringComparison.Ordinal);
        Assert.True(contextual.Message.Length <= EvaluationEvidence.MaxDiagnosticLength);
        Assert.Same(contextualReturn, contextual.Data["FrozenWorkerRestore.ActualReturn"]);
        var selectionData = Assert.IsType<string>(contextual.Data["FrozenWorkerRestore.SelectionContext"]);
        Assert.DoesNotContain(root, selectionData, StringComparison.Ordinal);

        // Finite synthetic DATA controls; no restore/project/runtime is invoked.
        foreach (var suffix in new[]
        {
            "[" + dependency + "::TargetFramework=net10.0]",
            "[" + dependency + "\0.csproj]",
            "[" + dependency + "::malformed",
            "[[" + dependency + "]]",
            "[" + dependency + new string('x', 4200) + "]"
        })
        {
            var malformedReturn = returned with { StandardOutput = "location: error NU1004: Lock differs " + suffix + "\n" };
            var malformed = FrozenExecutionEnvironment.CreateRestoreFailure(malformedReturn, app, "net10.0", "Release", root);
            Assert.IsType<SnapshotCaptureException>(malformed);
            Assert.Same(malformedReturn, malformed.Data["FrozenWorkerRestore.ActualReturn"]);
            Assert.Contains("error NU1004", malformed.Message, StringComparison.Ordinal);
            Assert.Contains("projectMatches=Unknown", malformed.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(root, malformed.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(root, Assert.IsType<string>(malformed.Data["FrozenWorkerRestore.SelectionContext"]), StringComparison.Ordinal);
            Assert.InRange(malformed.Message.Length, 1, EvaluationEvidence.MaxDiagnosticLength);
        }
        foreach (var (requested, worker) in new[] { (app + "\0", root), (app, root + "\0") })
        {
            var unparsed = FrozenExecutionEnvironment.CreateRestoreFailure(contextualReturn, requested, "net10.0", "Release", worker);
            Assert.IsType<SnapshotCaptureException>(unparsed);
            Assert.Same(contextualReturn, unparsed.Data["FrozenWorkerRestore.ActualReturn"]);
            Assert.Contains("requested=[unparsed]", unparsed.Message, StringComparison.Ordinal);
            Assert.Contains("error NU1004", unparsed.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(root, unparsed.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(root, Assert.IsType<string>(unparsed.Data["FrozenWorkerRestore.SelectionContext"]), StringComparison.Ordinal);
        }

        var first = new ResolvedGraphObservedTask("1/1/1/1/1/1", app,
            new Dictionary<string, string> { ["TargetFramework"] = "net10.0" },
            Path.Combine(root, "sdk", "csc.dll"), Path.Combine(root, "sdk", "CSharp.Core.targets"),
            new[] { "/define:SECRET-CREDENTIAL-VALUE" }, false)
        { EvaluatedProperties = new Dictionary<string, string> { ["TargetFramework"] = "net10.0",
            ["Configuration"] = "Release", ["MSBuildProjectFullPath"] = app }, ProjectInstanceId = 1, EvaluationId = 1 };
        var later = first with { GlobalProperties = new Dictionary<string, string>(), Tokens = new[] { "/define:OTHER-SECRET-VALUE" } };
        var mismatch = ResolvedGraphCapture.DescribeRetainedContextMismatch(first, later);
        Assert.Contains("GlobalsSame=False", mismatch, StringComparison.Ordinal);
        Assert.Contains("TokensSame=False", mismatch, StringComparison.Ordinal);
        Assert.Contains("FirstGlobalKey=TargetFramework", mismatch, StringComparison.Ordinal);
        Assert.Contains("\"FirstDifferentTokenIndex\":0", mismatch, StringComparison.Ordinal);
        Assert.Contains("compiler-switch", mismatch, StringComparison.Ordinal);
        Assert.DoesNotContain("SECRET-CREDENTIAL-VALUE", mismatch, StringComparison.Ordinal);
        Assert.DoesNotContain("OTHER-SECRET-VALUE", mismatch, StringComparison.Ordinal);
        Assert.DoesNotContain(root, mismatch, StringComparison.Ordinal);
        Assert.InRange(mismatch.Length, 1, 16384);
        Assert.Equal("/define:SECRET-CREDENTIAL-VALUE", first.Tokens[0]);
        // Argument-shape DATA, not a genuine restore/compiler execution claim.
        Assert.Empty(ResolvedGraphCapture.CompilerFrameworkArguments(null, "net10.0"));
        Assert.Empty(ResolvedGraphCapture.CompilerFrameworkArguments(later, "net10.0"));
        Assert.Equal(new[] { "-p:TargetFramework=net10.0" }, ResolvedGraphCapture.CompilerFrameworkArguments(first, "net10.0"));
        Assert.Throws<EvaluationContractException>(() => ResolvedGraphCapture.CompilerFrameworkArguments(first, "netstandard2.1"));


    }

    [Fact]
    public async Task Public_graph_dispatch_uses_actual_capture_before_baseline_and_never_promotes_missing_suite()
    {
        WriteFixture();
        _repository.WriteText("mutate4csharp.json", System.Text.Encoding.UTF8.GetString(_repository.Read(".mutate4csharp.json")));
        _repository.Git("add", ".");
        _repository.Git("commit", "-m", "public graph dispatch fixture");
        var token = TestContext.Current.CancellationToken;
        await using var snapshot = await SnapshotCapture.CaptureAsync(_repository.Root, "HEAD", [],
            SnapshotCaptureOptions.Default, token);
        var explicitScope = await ScopePlanner.PlanExplicitAsync(["src/App/App.cs"], snapshot.CaptureRoot, token);
        var configuration = CheckConfiguration.Load(snapshot.CaptureRoot, explicitScope.ConfigurationBytes!);
        var result = await StrictExecutionPipeline.RunAsync(snapshot, snapshot.Identity.CaptureId,
            "public-graph-regression", explicitScope.Plan, configuration, [], null, token);
        Assert.True(result.EnumerationCount > 0);
        // This compiler-only fixture intentionally has no test adapter. Genuine
        // semantic enumeration does not manufacture a green baseline or PASS.
        Assert.NotEqual(BaselineStatus.Green, result.Baseline);
        Assert.All(result.Units, unit => Assert.Equal(UnitDisposition.Omitted, unit.Disposition));
    }
    [Fact]
    public async Task Actual_fresh_owned_cold_and_warm_roots_keep_one_complete_candidate_identity()
    {
        WriteFixture();
        _repository.WriteText("mutate4csharp.json", System.Text.Encoding.UTF8.GetString(_repository.Read(".mutate4csharp.json")));
        _repository.Git("add", "."); _repository.Git("commit", "-m", "portable actual graph fixture");
        var token = TestContext.Current.CancellationToken;
        var original = new ResolvedGraphBudget(TimeSpan.FromSeconds(1100), token);
        await using var snapshot = await SnapshotCapture.CaptureAsync(_repository.Root, "HEAD", [], SnapshotCaptureOptions.Default, token);
        using var inputs = new ResolvedGraphInputs(original);
        var deployment = ResolvedGraphDeployment.Load(inputs);
        var scope = await ScopePlanner.PlanExplicitAsync(["src/App/App.cs"], snapshot.CaptureRoot, token);
        var configuration = CheckConfiguration.Load(snapshot.CaptureRoot, scope.ConfigurationBytes!);
        await using var environment = await ExecutionEnvironment.PrepareDependenciesAsync(snapshot,
            ["src/App/App.csproj", "tests/CompilerSuite/CompilerSuite.csproj"],
            DependencyPreparationOptions.Default with { Timeout = original.RemainingWork }, token);
        var first = await ResolvedGraphExecution.EnumerateAsync(snapshot, configuration, scope.Plan, environment, deployment, original, token);
        var second = await ResolvedGraphExecution.EnumerateAsync(snapshot, configuration, scope.Plan, environment, deployment, original, token);
        Assert.True(first.IsComplete); Assert.True(second.IsComplete); Assert.NotEmpty(first.Candidates);
        Assert.Equal(first.SemanticContextIdentity, second.SemanticContextIdentity);
        Assert.Equal(first.Candidates.Select(c => c.EvaluationUnitId), second.Candidates.Select(c => c.EvaluationUnitId));
        Assert.Null(original.Observe().Cleanup);
    }

    [Fact]
    public async Task Actual_compiler_generated_checksum_positive_precedes_after_compile_source_replacement_refusal()
    {
        WriteFixture(); _repository.Git("add", "."); _repository.Git("commit", "-m", "actual PDB source correspondence");
        var token = TestContext.Current.CancellationToken;
        var original = new ResolvedGraphBudget(TimeSpan.FromSeconds(1100), token);
        await using var snapshot = await SnapshotCapture.CaptureAsync(_repository.Root, "HEAD", [], SnapshotCaptureOptions.Default, token);
        using var inputs = new ResolvedGraphInputs(original);
        var deployment = ResolvedGraphDeployment.Load(inputs);
        var configuration = CheckConfiguration.Load(_repository.Root, _repository.Read(".mutate4csharp.json"));
        await using var environment = await ExecutionEnvironment.PrepareDependenciesAsync(snapshot,
            ["src/App/App.csproj", "tests/CompilerSuite/CompilerSuite.csproj"],
            DependencyPreparationOptions.Default with { Timeout = original.RemainingWork }, token);
        await using var capture = await ResolvedGraphCapture.CreateAsync(snapshot, configuration, environment,
            original.RemainingWork, token, deployment.Logger, original);
        var app = Assert.Single(capture.Projects, p => p.Selection.Project == "src/App/App.csproj");
        var compiled = Assert.IsType<GraphCompiledSnapshot>(Assert.IsType<ResolvedGraphObservedTask>(app.FirstCsc).Compiled);
        Dictionary<string, byte[]> ReadDocuments() => compiled.Documents.ToDictionary(d => d.Path,
            d => inputs.Read(d.Path, "source").Bytes, StringComparer.Ordinal);
        Assert.Equal(compiled.Proof, ResolvedGraphCompiledSources.Verify(compiled, ReadDocuments(), inputs, original));
        var generated = Assert.Single(compiled.Documents, d => d.Path.EndsWith("GeneratedFixture.cs", StringComparison.Ordinal));
        original.CheckWork(); File.AppendAllText(generated.Path, "\n// after actual compiler replacement\n"); original.CheckWork();
        var changed = ReadDocuments();
        Assert.Throws<SnapshotDivergedException>(() => ResolvedGraphCompiledSources.Verify(compiled, changed, inputs, original));
        Assert.NotNull(original.Observe().Cleanup);
        Assert.Throws<EvaluationContractException>(original.CheckWork);
    }
    [Fact]
    public async Task Actual_positive_then_AfterCoreCompile_rewrite_is_rejected_before_semantic_worker()
    {
        WriteFixture(); _repository.Git("add", "."); _repository.Git("commit", "-m", "positive before compiler-output drift");
        var token = TestContext.Current.CancellationToken;
        var original = new ResolvedGraphBudget(TimeSpan.FromSeconds(1100), token);
        using var inputs = new ResolvedGraphInputs(original);
        var deployment = ResolvedGraphDeployment.Load(inputs);
        await Capture(false);
        original.CheckWork();
        var targets = System.Text.Encoding.UTF8.GetString(_repository.Read("Directory.Build.targets"));
        _repository.WriteText("Directory.Build.targets", targets.Replace("</Project>", """
            <Target Name="ReplaceAfterActualCsc" AfterTargets="CoreCompile" Condition="'$(MSBuildProjectName)' == 'App'">
              <WriteLinesToFile File="$(IntermediateOutputPath)GeneratedFixture.cs" Lines="internal static class GeneratedFixture { internal const int Value = 99%3B }" Overwrite="true" />
            </Target></Project>
            """, StringComparison.Ordinal));
        _repository.Git("add", "."); _repository.Git("commit", "-m", "replace generated bytes after actual compiler");
        await Capture(true);
        Assert.NotNull(original.Observe().Cleanup);
        Assert.Throws<EvaluationContractException>(original.CheckWork);

        async Task Capture(bool refuse)
        {
            await using var snapshot = await SnapshotCapture.CaptureAsync(_repository.Root, "HEAD", [], SnapshotCaptureOptions.Default, token);
            var configuration = CheckConfiguration.Load(_repository.Root, _repository.Read(".mutate4csharp.json"));
            await using var environment = await ExecutionEnvironment.PrepareDependenciesAsync(snapshot,
                ["src/App/App.csproj", "tests/CompilerSuite/CompilerSuite.csproj"],
                DependencyPreparationOptions.Default with { Timeout = original.RemainingWork }, token);
            if (refuse)
                await Assert.ThrowsAnyAsync<SnapshotCaptureException>(async () =>
                {
                    await using var unexpected = await ResolvedGraphCapture.CreateAsync(snapshot, configuration, environment,
                        original.RemainingWork, token, deployment.Logger, original);
                });
            else
            {
                await using var actual = await ResolvedGraphCapture.CreateAsync(snapshot, configuration, environment,
                    original.RemainingWork, token, deployment.Logger, original);
                Assert.Equal(3, actual.Projects.Count);
                Assert.All(actual.Projects, p => Assert.NotNull(p.FirstCsc!.Compiled));
                actual.ValidateAuthoredInputs();
            }
        }
    }
    private void WriteFixture()
    {
        _repository.WriteText("global.json", """
            {"sdk":{"version":"10.0.401","rollForward":"disable","allowPrerelease":false}}
            """);
        _repository.WriteText("NuGet.Config", """
            <configuration><packageSources><clear/><add key="nuget.org" value="https://api.nuget.org/v3/index.json"/></packageSources></configuration>
            """);
        _repository.WriteText("Directory.Build.props", """
            <Project><PropertyGroup><TargetFramework>net10.0</TargetFramework><Nullable>enable</Nullable><LangVersion>14.0</LangVersion><DefineConstants>$(DefineConstants);INHERITED_FIXTURE</DefineConstants><UseSharedCompilation>false</UseSharedCompilation><SkipCompilerExecution>false</SkipCompilerExecution><DebugType>portable</DebugType></PropertyGroup></Project>
            """);
        _repository.WriteText("Directory.Packages.props", """
            <Project><PropertyGroup><ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally></PropertyGroup><ItemGroup><PackageVersion Include="Microsoft.Bcl.AsyncInterfaces" Version="6.0.0"/></ItemGroup></Project>
            """);
        _repository.WriteText("Directory.Build.targets", """
            <Project><Target Name="WriteRealFixtureSource" BeforeTargets="CoreCompile" Condition="'$(MSBuildProjectName)' == 'App'"><WriteLinesToFile File="$(IntermediateOutputPath)GeneratedFixture.cs" Lines="internal static class GeneratedFixture { internal const int Value = 17%3B }" Overwrite="true"/><ItemGroup><Compile Include="$(IntermediateOutputPath)GeneratedFixture.cs"/></ItemGroup></Target></Project>
            """);
        _repository.WriteText("src/App/App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk"><ItemGroup><PackageReference Include="Microsoft.Bcl.AsyncInterfaces"/><ProjectReference Include="../Dependency/Dependency.csproj"/><Compile Remove="Disabled.cs" Condition="'$(Configuration)' == 'Release'"/></ItemGroup></Project>
            """);
        _repository.WriteText("src/App/App.cs", """
            #if !INHERITED_FIXTURE
            #error Missing real inherited compiler define
            #endif
            public static class App { public static int Value() => Dependency.Value + GeneratedFixture.Value; }
            """);
        _repository.WriteText("src/App/Disabled.cs", "#error Release conditional Compile removal was ignored\n");
        _repository.WriteText("src/Dependency/Dependency.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        _repository.WriteText("src/Dependency/Dependency.cs", "public static class Dependency { public const int Value = 3; }\n");
        _repository.WriteText("tests/CompilerSuite/CompilerSuite.csproj", """
            <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><IsTestProject>true</IsTestProject></PropertyGroup><ItemGroup><ProjectReference Include="../../src/App/App.csproj"/></ItemGroup></Project>
            """);
        _repository.WriteText("tests/CompilerSuite/CompilerSuite.cs", "public static class CompilerSuite { public static int Value() => App.Value(); }\n");
        _repository.WriteText(".mutate4csharp.json", JsonSerializer.Serialize(new
        {
            version = 2, semanticProfile = "resolved-project-graph-v1",
            projects = new[] { new { id = "app", project = "src/App/App.csproj", targetFramework = "net10.0",
                configuration = "Release", parseContext = "inherited", sources = new[] { "src/App/**/*.cs" },
                testSuites = new[] { "compiler" } } },
            testSuites = new[] { new { id = "compiler", path = "tests/CompilerSuite/CompilerSuite.csproj",
                runner = "vstest", framework = "net10.0", configuration = "Release",
                expectedMembers = new[] { "CompilerSuite.dll" } } }
        }));
    }

    public void Dispose() => _repository.Dispose();
}
