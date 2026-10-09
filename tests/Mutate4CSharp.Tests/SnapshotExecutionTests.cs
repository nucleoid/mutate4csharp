using System.Runtime.InteropServices;
using System.Text.Json;

namespace Mutate4CSharp.Tests;

public sealed class SnapshotExecutionTests : IDisposable
{
    private readonly SnapshotTestRepository _repository = new();

    [Fact]
    public async Task FreshBaselineAndMutantClonesDifferOnlyByIntendedBytesAndPreserveOriginal()
    {
        _repository.WriteText("src/A.cs", "﻿class A\r\n{\r\n    bool Value() => true;\r\n}\r\n", utf8Bom: false);
        _repository.WriteText("assets/config.json", "{\"enabled\":true}\n");
        _repository.Git("add", ".");
        _repository.Git("commit", "-m", "fixture");
        var original = _repository.Read("src/A.cs");
        await using var snapshot = await SnapshotCapture.CaptureAsync(_repository.Root, "HEAD", [],
            SnapshotCaptureOptions.Default, CancellationToken.None);
        await using var baseline = await SnapshotWorkspace.CreateCloneAsync(snapshot, "baseline", CancellationToken.None);
        Directory.CreateDirectory(Path.Combine(baseline.Root, "obj"));
        File.WriteAllText(Path.Combine(baseline.Root, "obj", "baseline-only.txt"), "not reusable");
        await using var mutant = await SnapshotWorkspace.CreateCloneAsync(snapshot, "mutant", CancellationToken.None);

        var source = File.ReadAllText(Path.Combine(mutant.Root, "src/A.cs"));
        var start = source.IndexOf("true", StringComparison.Ordinal);
        mutant.WriteTextMutation("src/A.cs", start, 4, "false");

        Assert.Equal(File.ReadAllBytes(Path.Combine(baseline.Root, "assets/config.json")),
            File.ReadAllBytes(Path.Combine(mutant.Root, "assets/config.json")));
        Assert.Equal(source[..start] + "false" + source[(start + 4)..],
            File.ReadAllText(Path.Combine(mutant.Root, "src/A.cs")));
        Assert.Equal(original, _repository.Read("src/A.cs"));
        Assert.False(File.Exists(Path.Combine(mutant.Root, "obj", "baseline-only.txt")));
        Assert.Equal(Path.GetFullPath(Path.Combine(_repository.Root, "src/A.cs")),
            mutant.ToCanonicalPath(Path.Combine(mutant.Root, "src/A.cs")));
    }

    [Fact]
    public void DependencyFingerprintChangesWithResolvedPackageContent()
    {
        var first = Path.Combine(_repository.Root, "packages-a");
        var second = Path.Combine(_repository.Root, "packages-b");
        Directory.CreateDirectory(first);
        Directory.CreateDirectory(second);
        File.WriteAllText(Path.Combine(first, "graph.json"), "same graph");
        File.WriteAllText(Path.Combine(second, "graph.json"), "same graph");
        File.WriteAllText(Path.Combine(first, "package.dll"), "one");
        File.WriteAllText(Path.Combine(second, "package.dll"), "two");

        Assert.NotEqual(ExecutionEnvironment.FingerprintTree(first), ExecutionEnvironment.FingerprintTree(second));
    }

    [Fact]
    public void FrozenRestoreEnvironmentRemovesMachineFallbackPackages()
    {
        var environment = ExecutionEnvironment.ProcessEnvironment("/private/packages");

        Assert.True(environment.ContainsKey("NUGET_FALLBACK_PACKAGES"));
        Assert.Null(environment["NUGET_FALLBACK_PACKAGES"]);
        Assert.Equal(Path.Combine(Path.GetDirectoryName(Path.GetFullPath("/private/packages"))!,
            "msbuild-user-extensions"), environment["MSBuildUserExtensionsPath"]);
        Assert.Null(environment["MSBuildExtensionsPath"]);
        Assert.Null(environment["NuGetPackageRoot"]);
        Assert.Null(environment["NuGetPackageFolders"]);
        Assert.Null(environment["ProjectAssetsFile"]);
        Assert.Null(environment["RestoreOutputPath"]);
        Assert.Null(environment["_DirectoryBuildTargetsBasePath"]);
        Assert.Null(environment["_DirectoryBuildTargetsFile"]);
        Assert.Null(environment["_DirectoryPackagesPropsBasePath"]);
        Assert.Null(environment["_DirectoryPackagesPropsFile"]);
        Assert.Null(environment["DirectoryBuildPropsPath"]);
        Assert.Null(environment["DirectoryBuildTargetsPath"]);
        Assert.Null(environment["DirectoryPackagesPropsPath"]);
        Assert.Null(environment["MSBuildProjectExtensionsPath"]);
        Assert.Null(environment["BaseIntermediateOutputPath"]);
        Assert.Null(environment["RestoreSources"]);
        Assert.Null(environment["RestoreGraphProjectInput"]);
        Assert.Null(environment["NuGetRestoreTargets"]);
        Assert.Null(environment["CSharpCoreTargetsPath"]);
        Assert.Null(environment["CSharpDesignTimeTargetsPath"]);
        Assert.Null(environment["AlternateCommonProps"]);
        Assert.Null(environment["BeforeMicrosoftNETSdkTargets"]);
        Assert.Null(environment["LanguageTargets"]);
        Assert.Null(environment["AfterMicrosoftNETSdkTargets"]);
        Assert.Null(environment["AfterMicrosoftNetSdkProps"]);
        Assert.Null(environment["BeforeTargetFrameworkInferenceTargets"]);
        Assert.Null(environment["MSBuildSDKsPath"]);
        Assert.Null(environment["DOTNET_MSBUILD_SDK_RESOLVER_SDKS_DIR"]);
        Assert.Null(environment["DOTNET_MSBUILD_SDK_RESOLVER_SDKS_VER"]);
        Assert.Null(environment["DOTNET_MSBUILD_SDK_RESOLVER_CLI_DIR"]);
        Assert.Null(environment["MSBUILDADDITIONALSDKRESOLVERSFOLDER"]);
        foreach (var name in ExecutionEnvironment.StrictSemanticPropertyEnvironmentVariableNames)
        {
            Assert.True(environment.ContainsKey(name));
            Assert.Null(environment[name]);
        }
    }

    [Fact]
    public async Task StrictChildClearsCaseVariedRedirectionPropertiesWithRealSdk()
    {
        var root = Path.Combine(_repository.Root, "case-varied-environment");
        var owned = Path.Combine(root, "owned");
        var projectRoot = Path.Combine(owned, "root");
        Directory.CreateDirectory(projectRoot);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(owned, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        File.WriteAllText(Path.Combine(projectRoot, "App.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
              <Target Name="Observe" BeforeTargets="Build">
                <WriteLinesToFile File="$(MSBuildProjectDirectory)/observed.txt"
                  Lines="NuGet=$(NuGetPackageRoot);Props=$(DirectoryBuildPropsPath);Custom=$(CustomBeforeMicrosoftCommonProps);Restore=$(RestoreSources);Core=$(CSharpCoreTargetsPath)"
                  Overwrite="true" />
              </Target>
            </Project>
            """);
        await ExecutionEnvironment.CreateExecutionBoundaryAsync(owned, projectRoot, CancellationToken.None);
        var inherited = new Dictionary<string, string>
        {
            ["nUgEtPaCkAgErOoT"] = "/outside/packages/",
            ["dIrEcToRyBuIlDpRoPsPaTh"] = "/outside/Directory.Build.props",
            ["cUsToMbEfOrEmIcRoSoFtCoMmOnPrOpS"] = "/outside/Before.props",
            ["rEsToReSoUrCeS"] = "https://example.invalid/v3/index.json",
            ["cShArPcOrEtArGeTsPaTh"] = "/outside/Microsoft.CSharp.Core.targets",
            ["aLtErNaTeCoMmOnPrOpS"] = "/outside/Alternate.Common.props",
            ["lAnGuAgEtArGeTs"] = "/outside/Language.targets",
            ["bEfOrEmIcRoSoFtNeTsDkTaRgEtS"] = "/outside/Before.targets",
            ["MSBuildSDKsPath"] = "/outside/sdk"
        };
        var dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
        var previous = inherited.Keys.ToDictionary(name => name,
            Environment.GetEnvironmentVariable, StringComparer.Ordinal);
        ProcessTreeResult run;
        try
        {
            Environment.SetEnvironmentVariable("MSBuildSDKsPath", inherited["MSBuildSDKsPath"]);
            var unsanitized = await ProcessTree.RunAsync(dotnet,
                ["build", "App.csproj", "-m:1", "--nologo", "-nodeReuse:false",
                    "-p:UseSharedCompilation=false"],
                projectRoot, TimeSpan.FromMinutes(2), CancellationToken.None);
            Assert.NotEqual(0, unsanitized.ExitCode);
            var unsanitizedOutput = unsanitized.StandardError + Environment.NewLine + unsanitized.StandardOutput;
            Assert.Contains("MSB4236", unsanitizedOutput, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Microsoft.NET.Sdk", unsanitizedOutput, StringComparison.OrdinalIgnoreCase);
            foreach (var pair in inherited) Environment.SetEnvironmentVariable(pair.Key, pair.Value);
            var variables = ExecutionEnvironment.ProcessEnvironment(Path.Combine(owned, "packages"));
            run = await ProcessTree.RunAsync(dotnet,
                ["build", "App.csproj", "-m:1", "--nologo", "-nodeReuse:false",
                    "-p:UseSharedCompilation=false"],
                projectRoot, TimeSpan.FromMinutes(2), CancellationToken.None, variables);
        }
        finally
        {
            foreach (var pair in previous) Environment.SetEnvironmentVariable(pair.Key, pair.Value);
        }

        Assert.True(run.ExitCode == 0, run.StandardError + Environment.NewLine + run.StandardOutput);
        var observed = File.ReadAllText(Path.Combine(projectRoot, "observed.txt"));
        Assert.Contains($"NuGet={Path.Combine(owned, "packages")}", observed, StringComparison.Ordinal);
        Assert.DoesNotContain("/outside", observed, StringComparison.Ordinal);
        Assert.DoesNotContain("example.invalid", observed, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RealSdkExpandsProjectReferenceForwardingBeforeSplittingButCaptureRefusesIt()
    {
        var externalRoot = Path.Combine(Directory.GetParent(_repository.Root)!.FullName,
            "forwarding-target-" + Guid.NewGuid().ToString("N"));
        var marker = Path.Combine(_repository.Root, "forwarded-marker.txt");
        try
        {
            Directory.CreateDirectory(externalRoot);
            File.WriteAllText(Path.Combine(externalRoot, "External.targets"), $"""
                <Project>
                  <Target Name="ExternalForwardingMarker" BeforeTargets="CoreCompile">
                    <WriteLinesToFile File="{marker}" Lines="executed" Overwrite="true" />
                  </Target>
                </Project>
                """);
            _repository.WriteText("App.csproj", $"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <Forwarded>value;CustomAfterMicrosoftCommonTargets={Path.Combine(externalRoot, "External.targets")}</Forwarded>
                  </PropertyGroup>
                  <ItemGroup>
                    <ProjectReference Include="Referenced/Referenced.csproj"
                      AdditionalProperties="A=$(Forwarded)" />
                  </ItemGroup>
                </Project>
                """);
            _repository.WriteText("Program.cs", "public sealed class Program;\n");
            _repository.WriteText("Referenced/Referenced.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
                </Project>
                """);
            _repository.WriteText("Referenced/Class1.cs", "public sealed class Class1;\n");
            _repository.Git("add", ".");
            _repository.Git("commit", "-m", "fixture");

            var dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
            var run = await ProcessTree.RunAsync(dotnet,
                ["build", "App.csproj", "-m:1", "--nologo", "-nodeReuse:false",
                    "-p:UseSharedCompilation=false"],
                _repository.Root, TimeSpan.FromMinutes(2), CancellationToken.None);
            Assert.True(run.ExitCode == 0, run.StandardError + Environment.NewLine + run.StandardOutput);
            Assert.True(File.Exists(marker));

            var error = await Assert.ThrowsAsync<SnapshotCaptureException>(() => SnapshotCapture.CaptureAsync(
                _repository.Root, "HEAD", [], SnapshotCaptureOptions.Default, CancellationToken.None));
            Assert.Contains("CustomAfterMicrosoftCommonTargets", error.Message,
                StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(externalRoot)) Directory.Delete(externalRoot, true);
        }
    }

    [Fact]
    public async Task RealSdkCarriesConfiguredProjectReferenceForwardingButCaptureRefusesIt()
    {
        var externalRoot = Path.Combine(Directory.GetParent(_repository.Root)!.FullName,
            "configured-reference-target-" + Guid.NewGuid().ToString("N"));
        var marker = Path.Combine(_repository.Root, "configured-reference-marker.txt");
        try
        {
            Directory.CreateDirectory(externalRoot);
            File.WriteAllText(Path.Combine(externalRoot, "External.targets"), $"""
                <Project>
                  <Target Name="ExternalConfiguredReferenceMarker" BeforeTargets="GetTargetFrameworks;Build;CoreCompile">
                    <WriteLinesToFile File="{marker}" Lines="executed" Overwrite="true" />
                  </Target>
                </Project>
                """);
            _repository.WriteText("App.csproj", $"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
                  <ItemGroup>
                    <ProjectReferenceWithConfiguration Include="Referenced/Referenced.csproj"
                      AdditionalProperties="CustomAfterMicrosoftCommonTargets={Path.Combine(externalRoot, "External.targets")}" />
                  </ItemGroup>
                </Project>
                """);
            _repository.WriteText("Program.cs", "public sealed class Program;\n");
            _repository.WriteText("Referenced/Referenced.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
                </Project>
                """);
            _repository.WriteText("Referenced/Class1.cs", "public sealed class Class1;\n");
            _repository.Git("add", ".");
            _repository.Git("commit", "-m", "fixture");

            var dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
            var restore = await ProcessTree.RunAsync(dotnet,
                ["restore", "Referenced/Referenced.csproj", "-m:1", "--nologo", "--disable-build-servers"],
                _repository.Root, TimeSpan.FromMinutes(2), CancellationToken.None);
            Assert.True(restore.ExitCode == 0,
                restore.StandardError + Environment.NewLine + restore.StandardOutput);
            var run = await ProcessTree.RunAsync(dotnet,
                ["build", "App.csproj", "-m:1", "--nologo", "-nodeReuse:false",
                    "-p:UseSharedCompilation=false"],
                _repository.Root, TimeSpan.FromMinutes(2), CancellationToken.None);
            Assert.True(run.ExitCode == 0, run.StandardError + Environment.NewLine + run.StandardOutput);
            Assert.True(File.Exists(marker));

            var error = await Assert.ThrowsAsync<SnapshotCaptureException>(() => SnapshotCapture.CaptureAsync(
                _repository.Root, "HEAD", [], SnapshotCaptureOptions.Default, CancellationToken.None));
            Assert.Contains("ProjectReferenceWithConfiguration", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("AdditionalProperties", error.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(externalRoot)) Directory.Delete(externalRoot, true);
        }
    }

    [Fact]
    public async Task RealSdkMsBuildTaskPropertiesCanImportExternalTargetsButCaptureRefusesThem()
    {
        var externalRoot = Path.Combine(Directory.GetParent(_repository.Root)!.FullName,
            "msbuild-task-target-" + Guid.NewGuid().ToString("N"));
        var marker = Path.Combine(_repository.Root, "msbuild-task-marker.txt");
        try
        {
            Directory.CreateDirectory(externalRoot);
            File.WriteAllText(Path.Combine(externalRoot, "External.targets"), $"""
                <Project><Target Name="ExternalMarker" BeforeTargets="Build;CoreCompile">
                  <WriteLinesToFile File="{marker}" Lines="executed" Overwrite="true" />
                </Target></Project>
                """);
            _repository.WriteText("App.csproj", $"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
                  <Target Name="InvokeNested" AfterTargets="Build">
                    <MSBuild Projects="Nested/Nested.csproj" Targets="Build"
                      Properties="CustomAfterMicrosoftCommonTargets={Path.Combine(externalRoot, "External.targets")}" />
                  </Target>
                </Project>
                """);
            _repository.WriteText("Program.cs", "public sealed class Program;\n");
            _repository.WriteText("Nested/Nested.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
                </Project>
                """);
            _repository.WriteText("Nested/Class1.cs", "public sealed class Class1;\n");
            _repository.Git("add", ".");
            _repository.Git("commit", "-m", "fixture");

            var dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
            var restore = await ProcessTree.RunAsync(dotnet,
                ["restore", "Nested/Nested.csproj", "-m:1", "--nologo", "--disable-build-servers"],
                _repository.Root, TimeSpan.FromMinutes(2), CancellationToken.None);
            Assert.True(restore.ExitCode == 0,
                restore.StandardError + Environment.NewLine + restore.StandardOutput);
            var run = await ProcessTree.RunAsync(dotnet,
                ["build", "App.csproj", "-m:1", "--nologo", "-nodeReuse:false",
                    "-p:UseSharedCompilation=false"],
                _repository.Root, TimeSpan.FromMinutes(2), CancellationToken.None);
            Assert.True(run.ExitCode == 0, run.StandardError + Environment.NewLine + run.StandardOutput);
            Assert.True(File.Exists(marker));

            var error = await Assert.ThrowsAsync<SnapshotCaptureException>(() => SnapshotCapture.CaptureAsync(
                _repository.Root, "HEAD", [], SnapshotCaptureOptions.Default, CancellationToken.None));
            Assert.Contains("MSBuild Properties", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("CustomAfterMicrosoftCommonTargets", error.Message,
                StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(externalRoot)) Directory.Delete(externalRoot, true);
        }
    }

    [Fact]
    public async Task RootedMsBuildProjectExecutesInOriginalTreeButCaptureRefusesBeforeAnotherWrite()
    {
        var helper = Path.Combine(_repository.Root, "tools", "Helper.csproj");
        var helperOutput = Path.Combine(_repository.Root, "tools", "obj", "live-tree-write.txt");
        _repository.WriteText("App.csproj", $"""
            <Project><Target Name="Run">
              <MSBuild Projects="{helper}" Targets="Touch" />
            </Target></Project>
            """);
        _repository.WriteText("tools/Helper.csproj", """
            <Project><Target Name="Touch">
              <MakeDir Directories="$(MSBuildProjectDirectory)/obj" />
              <WriteLinesToFile File="$(MSBuildProjectDirectory)/obj/live-tree-write.txt"
                Lines="executed" Overwrite="true" />
            </Target></Project>
            """);
        _repository.Git("add", ".");
        _repository.Git("commit", "-m", "fixture");

        var dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
        var run = await ProcessTree.RunAsync(dotnet,
            ["msbuild", "App.csproj", "-m:1", "--nologo", "-nodeReuse:false", "-t:Run"],
            _repository.Root, TimeSpan.FromMinutes(2), CancellationToken.None);
        Assert.True(run.ExitCode == 0, run.StandardError + Environment.NewLine + run.StandardOutput);
        Assert.True(File.Exists(helperOutput));
        Directory.Delete(Path.GetDirectoryName(helperOutput)!, true);

        var error = await Assert.ThrowsAsync<SnapshotCaptureException>(() => SnapshotCapture.CaptureAsync(
            _repository.Root, "HEAD", [], SnapshotCaptureOptions.Default, CancellationToken.None));

        Assert.Contains("rooted", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(helperOutput));
    }

    [Fact]
    public async Task RealSdkRestoreForwardsGenerateGraphInputPropertiesButCaptureRefusesIt()
    {
        var externalRoot = Path.Combine(Directory.GetParent(_repository.Root)!.FullName,
            "restore-forwarding-target-" + Guid.NewGuid().ToString("N"));
        var marker = Path.Combine(_repository.Root, "restore-forwarded-marker.txt");
        try
        {
            Directory.CreateDirectory(externalRoot);
            File.WriteAllText(Path.Combine(externalRoot, "External.targets"), $"""
                <Project>
                  <Target Name="ExternalRestoreForwardingMarker" BeforeTargets="_GenerateRestoreGraphProjectEntry">
                    <WriteLinesToFile File="{marker}" Lines="executed" Overwrite="true" />
                  </Target>
                </Project>
                """);
            _repository.WriteText("App.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
                </Project>
                """);
            _repository.WriteText("Program.cs", "public sealed class Program;\n");
            _repository.WriteText("Directory.Build.targets", $"""
                <Project><PropertyGroup>
                  <_GenerateRestoreGraphProjectEntryInputProperties>ExcludeRestorePackageImports=true;CustomAfterMicrosoftCommonTargets={Path.Combine(externalRoot, "External.targets")}</_GenerateRestoreGraphProjectEntryInputProperties>
                </PropertyGroup></Project>
                """);
            _repository.Git("add", ".");
            _repository.Git("commit", "-m", "fixture");

            var dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
            var restore = await ProcessTree.RunAsync(dotnet,
                ["restore", "App.csproj", "-m:1", "--nologo", "--disable-build-servers"],
                _repository.Root, TimeSpan.FromMinutes(2), CancellationToken.None);
            Assert.True(restore.ExitCode == 0,
                restore.StandardError + Environment.NewLine + restore.StandardOutput);
            Assert.True(File.Exists(marker));

            var error = await Assert.ThrowsAsync<SnapshotCaptureException>(() => SnapshotCapture.CaptureAsync(
                _repository.Root, "HEAD", [], SnapshotCaptureOptions.Default, CancellationToken.None));
            Assert.Contains("_GenerateRestoreGraphProjectEntryInputProperties", error.Message,
                StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(externalRoot)) Directory.Delete(externalRoot, true);
        }
    }

    [Fact]
    public async Task RealSdkRestoreEvaluatesRestoreGraphProjectInputItemButCaptureRefusesIt()
    {
        var marker = Path.Combine(_repository.Root, "restore-entry-marker.txt");
        _repository.WriteText("App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
              <ItemGroup><RestoreGraphProjectInputItems Include="tools/Helper.xml" /></ItemGroup>
            </Project>
            """);
        _repository.WriteText("Program.cs", "public sealed class Program;\n");
        _repository.WriteText("tools/Helper.xml", $"""
            <Project>
              <Target Name="_IsProjectRestoreSupported" Returns="@(Supported)">
                <WriteLinesToFile File="{marker}" Lines="executed" Overwrite="true" />
                <ItemGroup><Supported Include="$(MSBuildProjectFullPath)" /></ItemGroup>
              </Target>
            </Project>
            """);
        _repository.Git("add", ".");
        _repository.Git("commit", "-m", "fixture");

        var dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
        var restore = await ProcessTree.RunAsync(dotnet,
            ["restore", "App.csproj", "-m:1", "--nologo", "--disable-build-servers"],
            _repository.Root, TimeSpan.FromMinutes(2), CancellationToken.None);
        Assert.True(File.Exists(marker), restore.StandardError + Environment.NewLine + restore.StandardOutput);

        var error = await Assert.ThrowsAsync<SnapshotCaptureException>(() => SnapshotCapture.CaptureAsync(
            _repository.Root, "HEAD", [], SnapshotCaptureOptions.Default, CancellationToken.None));
        Assert.Contains("RestoreGraphProjectInputItems", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RealSdkMultiTargetedTestEvaluatesProjectToTestItemButCaptureRefusesIt()
    {
        var marker = Path.Combine(_repository.Root, "test-dispatch-marker.txt");
        _repository.WriteText("App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFrameworks>net10.0;net8.0</TargetFrameworks>
                <IsTestProject>true</IsTestProject>
              </PropertyGroup>
              <ItemGroup><_ProjectToTestWithTFM Include="tools/Helper.xml" /></ItemGroup>
            </Project>
            """);
        _repository.WriteText("Program.cs", "public sealed class Program;\n");
        _repository.WriteText("tools/Helper.xml", $"""
            <Project>
              <Target Name="VSTest">
                <WriteLinesToFile File="{marker}" Lines="executed" Overwrite="true" />
              </Target>
            </Project>
            """);
        _repository.Git("add", ".");
        _repository.Git("commit", "-m", "fixture");

        var dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
        var build = await ProcessTree.RunAsync(dotnet,
            ["build", "App.csproj", "-m:1", "--nologo", "-nodeReuse:false",
                "-p:UseSharedCompilation=false"],
            _repository.Root, TimeSpan.FromMinutes(2), CancellationToken.None);
        Assert.True(build.ExitCode == 0, build.StandardError + Environment.NewLine + build.StandardOutput);
        var run = await ProcessTree.RunAsync(dotnet,
            ["test", "App.csproj", "--no-build", "-m:1", "--nologo", "-nodeReuse:false"],
            _repository.Root, TimeSpan.FromMinutes(2), CancellationToken.None);
        Assert.True(File.Exists(marker), run.StandardError + Environment.NewLine + run.StandardOutput);

        var error = await Assert.ThrowsAsync<SnapshotCaptureException>(() => SnapshotCapture.CaptureAsync(
            _repository.Root, "HEAD", [], SnapshotCaptureOptions.Default, CancellationToken.None));
        Assert.Contains("_ProjectToTestWithTFM", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RealSdkExecutesEscapedImportButCaptureRefusesIt()
    {
        var externalName = "escaped-import-" + Guid.NewGuid().ToString("N");
        var externalRoot = Path.Combine(Directory.GetParent(_repository.Root)!.FullName, externalName);
        var marker = Path.Combine(_repository.Root, "imported.txt");
        try
        {
            Directory.CreateDirectory(externalRoot);
            File.WriteAllText(Path.Combine(externalRoot, "External.targets"), $"""
                <Project><Target Name="External" BeforeTargets="Build"><WriteLinesToFile File="{marker}" Lines="imported" Overwrite="true" /></Target></Project>
                """);
            _repository.WriteText("App.csproj", $"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
                  <Import Project="%2E%2E/{externalName}/External.targets" />
                </Project>
                """);
            _repository.WriteText("Class1.cs", "public sealed class Class1;\n");
            _repository.Git("add", ".");
            _repository.Git("commit", "-m", "fixture");

            var dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
            var run = await ProcessTree.RunAsync(dotnet, ["build", "App.csproj", "-m:1", "--nologo"],
                _repository.Root, TimeSpan.FromMinutes(2), CancellationToken.None);
            Assert.True(run.ExitCode == 0, run.StandardError + Environment.NewLine + run.StandardOutput);
            Assert.True(File.Exists(marker));
            var error = await Assert.ThrowsAsync<SnapshotCaptureException>(() => SnapshotCapture.CaptureAsync(
                _repository.Root, "HEAD", [], SnapshotCaptureOptions.Default, CancellationToken.None));
            Assert.Contains("encoded", error.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(externalRoot)) Directory.Delete(externalRoot, true);
        }
    }

    [Fact]
    public async Task RealSdkCompilesEscapedCompileItemButCaptureRefusesIt()
    {
        var externalName = "escaped-compile-" + Guid.NewGuid().ToString("N");
        var externalRoot = Path.Combine(Directory.GetParent(_repository.Root)!.FullName, externalName);
        try
        {
            Directory.CreateDirectory(externalRoot);
            File.WriteAllText(Path.Combine(externalRoot, "External.cs"), "public sealed class ExternalType;\n");
            _repository.WriteText("App.csproj", $"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net10.0</TargetFramework><EnableDefaultCompileItems>false</EnableDefaultCompileItems></PropertyGroup>
                  <ItemGroup><Compile Include="%2E%2E/{externalName}/External.cs" /></ItemGroup>
                </Project>
                """);
            _repository.Git("add", ".");
            _repository.Git("commit", "-m", "fixture");

            var dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
            var run = await ProcessTree.RunAsync(dotnet, ["build", "App.csproj", "-m:1", "--nologo"],
                _repository.Root, TimeSpan.FromMinutes(2), CancellationToken.None);
            Assert.True(run.ExitCode == 0, run.StandardError + Environment.NewLine + run.StandardOutput);
            var error = await Assert.ThrowsAsync<SnapshotCaptureException>(() => SnapshotCapture.CaptureAsync(
                _repository.Root, "HEAD", [], SnapshotCaptureOptions.Default, CancellationToken.None));
            Assert.Contains("encoded", error.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(externalRoot)) Directory.Delete(externalRoot, true);
        }
    }

    [Fact]
    public async Task RealSdkAndCaptureResolveStaticImportedBuildFileDirectorySuffixFromDeclaringFile()
    {
        _repository.WriteText("App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
              <Target Name="ObserveImportedContent" BeforeTargets="Build">
                <WriteLinesToFile File="$(MSBuildProjectDirectory)/observed.txt"
                  Lines="@(Content->'%(FullPath)')" Overwrite="true" />
              </Target>
            </Project>
            """);
        _repository.WriteText("Class1.cs", "public sealed class Class1;\n");
        _repository.WriteText("build/Directory.Build.props", """
            <Project><ItemGroup>
              <Content Include="$(MSBuildThisFileDirectory)assets/input.txt" />
            </ItemGroup></Project>
            """);
        _repository.WriteText("build/assets/input.txt", "captured\n");
        _repository.WriteText("Directory.Build.props",
            "<Project><Import Project=\"build/Directory.Build.props\" /></Project>\n");
        _repository.Git("add", ".");
        _repository.Git("commit", "-m", "fixture");

        var dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
        var run = await ProcessTree.RunAsync(dotnet,
            ["build", "App.csproj", "-m:1", "--nologo", "-nodeReuse:false", "-p:UseSharedCompilation=false"],
            _repository.Root, TimeSpan.FromMinutes(2), CancellationToken.None);

        Assert.Equal(0, run.ExitCode);
        Assert.Contains(Path.Combine(_repository.Root, "build", "assets", "input.txt"),
            File.ReadAllText(Path.Combine(_repository.Root, "observed.txt")), StringComparison.Ordinal);
        await using var snapshot = await SnapshotCapture.CaptureAsync(
            _repository.Root, "HEAD", [], SnapshotCaptureOptions.Default, CancellationToken.None);
        Assert.Contains(snapshot.Files, file => file.RelativePath == "build/assets/input.txt");
    }

    [Fact]
    public async Task ExecutionBoundaryTerminatesSdk103AncestorConfigurationSearches()
    {
        var outer = Path.Combine(_repository.Root, "sdk-ancestor");
        var owned = Path.Combine(outer, "owned");
        var root = Path.Combine(owned, "root");
        var projectDirectory = Path.Combine(root, "App");
        Directory.CreateDirectory(projectDirectory);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(owned, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        File.WriteAllText(Path.Combine(outer, "Directory.Packages.props"),
            "<Project><PropertyGroup><AncestorPackages>outer</AncestorPackages></PropertyGroup></Project>");
        File.WriteAllText(Path.Combine(outer, "Directory.Build.rsp"), "-p:AncestorRsp=outer\n");
        File.WriteAllText(Path.Combine(root, "Directory.Build.rsp"), "-p:AncestorRsp=repository\n");
        File.WriteAllText(Path.Combine(outer, "Directory.Solution.props"),
            "<Project><Target Name=\"OuterProps\" BeforeTargets=\"Build\"><WriteLinesToFile File=\"$(MSBuildThisFileDirectory)solution-props-marker.txt\" Lines=\"imported\" /></Target></Project>");
        File.WriteAllText(Path.Combine(outer, "Directory.Solution.targets"),
            "<Project><Target Name=\"OuterTargets\" BeforeTargets=\"Build\"><WriteLinesToFile File=\"$(MSBuildThisFileDirectory)solution-targets-marker.txt\" Lines=\"imported\" /></Target></Project>");
        var externalUserExtensions = Path.Combine(outer, "user-extensions");
        var importBefore = Path.Combine(externalUserExtensions, "Current", "Imports",
            "Microsoft.Common.props", "ImportBefore");
        Directory.CreateDirectory(importBefore);
        File.WriteAllText(Path.Combine(importBefore, "External.props"),
            "<Project><PropertyGroup><ExternalUserExtension>loaded</ExternalUserExtension></PropertyGroup></Project>");
        File.WriteAllText(Path.Combine(projectDirectory, "App.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
              <Target Name="ObserveAncestors" BeforeTargets="Build">
                <WriteLinesToFile File="$(MSBuildProjectDirectory)/observed.txt"
                                  Lines="Packages=$(AncestorPackages);Rsp=$(AncestorRsp);User=$(MSBuildUserExtensionsPath);External=$(ExternalUserExtension)" Overwrite="true" />
              </Target>
            </Project>
            """);
        File.WriteAllText(Path.Combine(projectDirectory, "Class1.cs"), "public sealed class Class1;\n");
        var solution = Path.Combine(root, "App.slnx");
        File.WriteAllText(solution, "<Solution><Project Path=\"App/App.csproj\" /></Solution>\n");

        await ExecutionEnvironment.CreateExecutionBoundaryAsync(owned, root, CancellationToken.None);
        ExecutionEnvironment.ValidateExecutionBoundary(root);
        var dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
        var inheritedUserExtensions = Environment.GetEnvironmentVariable("MSBuildUserExtensionsPath");
        ProcessTreeResult run;
        try
        {
            Environment.SetEnvironmentVariable("MSBuildUserExtensionsPath", externalUserExtensions);
            run = await ProcessTree.RunAsync(dotnet,
                ["build", solution, "-m:1", "--nologo", "-nodeReuse:false", "-p:UseSharedCompilation=false"],
                root, TimeSpan.FromMinutes(2), CancellationToken.None,
                ExecutionEnvironment.ProcessEnvironment(Path.Combine(owned, "packages")));
        }
        finally
        {
            Environment.SetEnvironmentVariable("MSBuildUserExtensionsPath", inheritedUserExtensions);
        }

        Assert.Equal(0, run.ExitCode);
        Assert.Equal("<Project />\n", File.ReadAllText(Path.Combine(owned, "Directory.Packages.props")));
        Assert.True(File.Exists(Path.Combine(owned, "Directory.Build.rsp")));
        Assert.Equal("<Project />\n", File.ReadAllText(Path.Combine(owned, "Directory.Solution.props")));
        Assert.Equal("<Project />\n", File.ReadAllText(Path.Combine(owned, "Directory.Solution.targets")));
        var observed = File.ReadAllText(Path.Combine(projectDirectory, "observed.txt"));
        Assert.Contains("Rsp=repository", observed, StringComparison.Ordinal);
        Assert.Contains($"User={Path.Combine(owned, "msbuild-user-extensions")}", observed,
            StringComparison.Ordinal);
        Assert.Contains("External=", observed, StringComparison.Ordinal);
        Assert.DoesNotContain("External=loaded", observed, StringComparison.Ordinal);
        Assert.DoesNotContain("Packages=outer", observed, StringComparison.Ordinal);
        Assert.DoesNotContain("Rsp=outer", observed, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(outer, "solution-props-marker.txt")));
        Assert.False(File.Exists(Path.Combine(outer, "solution-targets-marker.txt")));
    }

    [Fact]
    public async Task DependencyPreparationFreezesLocalResolvedGraphWithoutWritingOriginalTree()
    {
        _repository.WriteText("App.Tests.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
                <IsTestProject>true</IsTestProject>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Microsoft.NET.Test.Sdk" Version="18.3.0" />
                <PackageReference Include="xunit.v3" Version="3.2.2" />
                <PackageReference Include="xunit.runner.visualstudio" Version="3.1.5" />
              </ItemGroup>
            </Project>
            """);
        _repository.WriteText("NuGet.Config", """
            <configuration><packageSources><clear /><add key="fixture" value="local-packages" /></packageSources></configuration>
            """);
        _repository.WriteText("Subject.cs", """
            public static class Subject
            {
                public static bool Covered() => true;
                public static bool Uncovered() => true;
            }
            """);
        _repository.WriteText("SubjectTests.cs", """
            using Xunit;

            public sealed class SubjectTests
            {
                [Fact]
                public void CoveredMutationIsKilled() => Assert.True(Subject.Covered());
            }
            """);
        CopyRestoredTestPackages(Path.Combine(_repository.Root, "local-packages"));
        _repository.Git("add", ".");
        _repository.Git("commit", "-m", "dependency fixture");
        await using var snapshot = await SnapshotCapture.CaptureAsync(_repository.Root, "HEAD", [],
            SnapshotCaptureOptions.Default, CancellationToken.None);

        await using var environment = await ExecutionEnvironment.PrepareDependenciesAsync(snapshot,
            ["App.Tests.csproj"], DependencyPreparationOptions.Default with { Timeout = TimeSpan.FromMinutes(2) },
            CancellationToken.None);
        await using var first = await environment.CreateWorkerPackageCacheAsync("first", CancellationToken.None);
        await using var second = await environment.CreateWorkerPackageCacheAsync("second", CancellationToken.None);
        await using var worker = await SnapshotWorkspace.CreateCloneAsync(snapshot, "restore-worker", CancellationToken.None);
        await environment.RestoreWorkerAsync(worker, "App.Tests.csproj", second, TimeSpan.FromMinutes(2), CancellationToken.None);

        Assert.Equal(64, environment.Fingerprint.Length);
        Assert.Equal(environment.Fingerprint, first.Fingerprint);
        Assert.Equal(ExecutionEnvironment.FingerprintTree(first.Root), ExecutionEnvironment.FingerprintTree(second.Root));
        Assert.False(Directory.Exists(Path.Combine(_repository.Root, "obj")));
        var firstPackageFile = Directory.EnumerateFiles(first.Root, "*", SearchOption.AllDirectories).First();
        File.AppendAllText(firstPackageFile, "worker-only");
        Assert.NotEqual(ExecutionEnvironment.FingerprintTree(first.Root), ExecutionEnvironment.FingerprintTree(second.Root));

        var legacyResults = Path.Combine(Path.GetTempPath(), "mutate4csharp", "results");
        Directory.CreateDirectory(legacyResults);
        var sentinel = Path.Combine(legacyResults, $"sentinel-{Guid.NewGuid():N}");
        File.WriteAllText(sentinel, "unrelated");
        try
        {
            var source = File.ReadAllText(Path.Combine(_repository.Root, "Subject.cs"));
            var firstStart = source.IndexOf("true", StringComparison.Ordinal);
            var secondStart = source.IndexOf("true", firstStart + 4, StringComparison.Ordinal);
            var sites = new[]
            {
                new MutationSite(0, firstStart, 4, 3, "false", "covered", "scope", "hash-1"),
                new MutationSite(1, secondStart, 4, 4, "false", "uncovered", "scope", "hash-2")
            };
            var results = await MutationExecutor.ExecuteFromSnapshotAsync(snapshot, "Subject.cs", "App.Tests.csproj", sites,
                TimeSpan.FromMinutes(2), 1, environment, false, CancellationToken.None);
            Assert.Collection(results,
                result => Assert.Equal(MutantStatus.Killed, result.Status),
                result => Assert.Equal(MutantStatus.Survived, result.Status));
            Assert.Equal("unrelated", File.ReadAllText(sentinel));
        }
        finally { File.Delete(sentinel); }
    }

    [Fact]
    public async Task DependencyPreparationWithoutCapturedNuGetConfigRefusesPublicFallback()
    {
        _repository.WriteText("App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>\n");
        _repository.WriteText("packages.lock.json", "{\"version\":1,\"dependencies\":{\"net10.0\":{}}}\n");
        _repository.Git("add", ".");
        _repository.Git("commit", "-m", "fixture");
        await using var snapshot = await SnapshotCapture.CaptureAsync(_repository.Root, "HEAD", [],
            SnapshotCaptureOptions.Default, CancellationToken.None);

        var error = await Assert.ThrowsAsync<SnapshotCaptureException>(() =>
            ExecutionEnvironment.PrepareDependenciesAsync(snapshot, ["App.csproj"],
                DependencyPreparationOptions.Default, CancellationToken.None));

        Assert.Contains("NuGet.Config", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("refus", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DependencyPreparationDoesNotSubstituteUnrelatedNestedNuGetConfig()
    {
        _repository.WriteText("src/App/App.csproj",
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>\n");
        _repository.WriteText("fixtures/NuGet.Config",
            "<configuration><packageSources><clear /></packageSources></configuration>\n");
        _repository.Git("add", ".");
        _repository.Git("commit", "-m", "fixture");
        await using var snapshot = await SnapshotCapture.CaptureAsync(_repository.Root, "HEAD", [],
            SnapshotCaptureOptions.Default, CancellationToken.None);

        var error = await Assert.ThrowsAsync<SnapshotCaptureException>(() =>
            ExecutionEnvironment.PrepareDependenciesAsync(snapshot, ["src/App/App.csproj"],
                DependencyPreparationOptions.Default, CancellationToken.None));

        Assert.Contains("applicable NuGet.Config", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DependencyPreparationIgnoresNestedConfigOutsideProjectAncestorChain()
    {
        _repository.WriteText("src/App/App.csproj",
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>\n");
        _repository.WriteText("NuGet.Config",
            "<configuration><packageSources><clear /></packageSources></configuration>\n");
        _repository.WriteText("fixtures/NuGet.Config",
            "<configuration><packageSources><clear /><add key=\"unrelated\" value=\"missing\" /></packageSources></configuration>\n");
        _repository.Git("add", ".");
        _repository.Git("commit", "-m", "fixture");
        await using var snapshot = await SnapshotCapture.CaptureAsync(_repository.Root, "HEAD", [],
            SnapshotCaptureOptions.Default, CancellationToken.None);

        await using var environment = await ExecutionEnvironment.PrepareDependenciesAsync(snapshot,
            ["src/App/App.csproj"], DependencyPreparationOptions.Default with { Timeout = TimeSpan.FromMinutes(2) },
            CancellationToken.None);

        Assert.Equal(64, environment.Fingerprint.Length);
    }

    [Fact]
    public async Task DependencyPreparationRejectsMergedRootAndNestedNuGetConfigHierarchy()
    {
        _repository.WriteText("src/App/App.csproj",
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>\n");
        _repository.WriteText("NuGet.Config", """
            <configuration>
              <packageSources><clear /><add key="private" value="private-packages" /></packageSources>
              <packageSourceMapping><packageSource key="private"><package pattern="Internal.*" /></packageSource></packageSourceMapping>
            </configuration>
            """);
        _repository.WriteText("src/NuGet.Config",
            "<configuration><packageSources><clear /><add key=\"fixture\" value=\"local-packages\" /></packageSources></configuration>\n");
        _repository.Git("add", ".");
        _repository.Git("commit", "-m", "fixture");
        await using var snapshot = await SnapshotCapture.CaptureAsync(_repository.Root, "HEAD", [],
            SnapshotCaptureOptions.Default, CancellationToken.None);

        var error = await Assert.ThrowsAsync<SnapshotCaptureException>(() =>
            ExecutionEnvironment.PrepareDependenciesAsync(snapshot, ["src/App/App.csproj"],
                DependencyPreparationOptions.Default, CancellationToken.None));

        Assert.Contains("multiple", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("NuGet.Config", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DependencyPreparationRequiresPackageSourceClearInSingleApplicableConfig()
    {
        _repository.WriteText("App.csproj",
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>\n");
        _repository.WriteText("NuGet.Config",
            "<configuration><packageSources><add key=\"private\" value=\"private-packages\" /></packageSources></configuration>\n");
        _repository.Git("add", ".");
        _repository.Git("commit", "-m", "fixture");
        await using var snapshot = await SnapshotCapture.CaptureAsync(_repository.Root, "HEAD", [],
            SnapshotCaptureOptions.Default, CancellationToken.None);

        var error = await Assert.ThrowsAsync<SnapshotCaptureException>(() =>
            ExecutionEnvironment.PrepareDependenciesAsync(snapshot, ["App.csproj"],
                DependencyPreparationOptions.Default, CancellationToken.None));

        Assert.Contains("clear", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("packageSources", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("NuGet.Config", "App.csproj", "local-packages")]
    [InlineData("src/NuGet.Config", "src/App/App.csproj", "local-packages")]
    public async Task DependencyPreparationPreservesSingleConfigSourceMapping(string configPath,
        string projectPath, string packageSource)
    {
        _repository.WriteText(projectPath,
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>\n");
        _repository.WriteText(configPath, $"""
            <configuration>
              <packageSources><clear /><add key="fixture" value="{packageSource}" /></packageSources>
              <packageSourceMapping><packageSource key="fixture"><package pattern="*" /></packageSource></packageSourceMapping>
            </configuration>
            """);
        var configDirectory = Path.GetDirectoryName(configPath)?.Replace('\\', '/') ?? string.Empty;
        var sourcePath = string.IsNullOrEmpty(configDirectory)
            ? packageSource
            : configDirectory + "/" + packageSource;
        _repository.WriteText(sourcePath + "/sentinel.txt", "keeps the local source in the snapshot\n");
        _repository.Git("add", ".");
        _repository.Git("commit", "-m", "fixture");
        await using var snapshot = await SnapshotCapture.CaptureAsync(_repository.Root, "HEAD", [],
            SnapshotCaptureOptions.Default, CancellationToken.None);

        await using var environment = await ExecutionEnvironment.PrepareDependenciesAsync(snapshot, [projectPath],
            DependencyPreparationOptions.Default with { Timeout = TimeSpan.FromMinutes(2) }, CancellationToken.None);

        Assert.Equal(64, environment.Fingerprint.Length);
    }

    [Fact]
    public async Task DependencyOwnerIsDisposedWhenPreparationCloneFails()
    {
        _repository.WriteText("App.csproj",
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>\n");
        _repository.WriteText("NuGet.Config",
            "<configuration><packageSources><clear /></packageSources></configuration>\n");
        _repository.Git("add", ".");
        _repository.Git("commit", "-m", "fixture");
        await using var snapshot = await SnapshotCapture.CaptureAsync(_repository.Root, "HEAD", [],
            SnapshotCaptureOptions.Default, CancellationToken.None);
        File.WriteAllText(Path.Combine(snapshot.CaptureRoot, "App.csproj"), "tampered");
        var parent = OwnedDirectory.PrivateParent("environments");
        var before = Directory.Exists(parent)
            ? Directory.EnumerateDirectories(parent).ToHashSet(StringComparer.Ordinal)
            : [];

        try
        {
            await Assert.ThrowsAsync<SnapshotDivergedException>(() => ExecutionEnvironment.PrepareDependenciesAsync(
                snapshot, ["App.csproj"], DependencyPreparationOptions.Default, CancellationToken.None));
            var after = Directory.Exists(parent)
                ? Directory.EnumerateDirectories(parent).ToHashSet(StringComparer.Ordinal)
                : [];
            Assert.Equal(before, after);
        }
        finally
        {
            if (Directory.Exists(parent))
                foreach (var directory in Directory.EnumerateDirectories(parent).Where(path => !before.Contains(path)))
                    Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void ResolvedDependencyGraphRejectsAnyPackageFolderOutsidePrivateCache()
    {
        var workspace = Path.Combine(Path.GetTempPath(), "mutate4csharp-assets", Guid.NewGuid().ToString("N"));
        var packages = Path.Combine(workspace, "private-packages");
        var external = Path.Combine(workspace, "external-fallback");
        var obj = Path.Combine(workspace, "obj");
        Directory.CreateDirectory(obj);
        Directory.CreateDirectory(packages);
        Directory.CreateDirectory(external);
        File.WriteAllText(Path.Combine(obj, "project.assets.json"), JsonSerializer.Serialize(new
        {
            packageFolders = new Dictionary<string, object>
            {
                [packages + Path.DirectorySeparatorChar] = new { },
                [external + Path.DirectorySeparatorChar] = new { }
            }
        }));

        try
        {
            var error = Assert.Throws<ExecutionBoundaryIntegrityException>(() =>
                ExecutionEnvironment.ValidateResolvedPackageRoots(workspace, packages));
            Assert.Contains("unfrozen", error.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(workspace, true);
        }
    }

    [Fact]
    public void ResolvedDependencyGraphRejectsSourcesOutsideSelectedNuGetConfig()
    {
        var workspace = Path.Combine(Path.GetTempPath(), "mutate4csharp-assets", Guid.NewGuid().ToString("N"));
        var packages = Path.Combine(workspace, "private-packages");
        var obj = Path.Combine(workspace, "obj");
        Directory.CreateDirectory(obj);
        Directory.CreateDirectory(packages);
        File.WriteAllText(Path.Combine(workspace, "NuGet.Config"),
            "<configuration><packageSources><clear /><add key=\"private\" value=\"private-feed\" /></packageSources></configuration>");
        File.WriteAllText(Path.Combine(obj, "project.assets.json"), JsonSerializer.Serialize(new
        {
            packageFolders = new Dictionary<string, object> { [packages + Path.DirectorySeparatorChar] = new { } },
            project = new
            {
                restore = new
                {
                    sources = new Dictionary<string, object>
                    {
                        [Path.Combine(workspace, "private-feed")] = new { },
                        ["https://api.nuget.org/v3/index.json"] = new { }
                    }
                }
            }
        }));

        try
        {
            var error = Assert.Throws<ExecutionBoundaryIntegrityException>(() =>
                ExecutionEnvironment.ValidateResolvedPackageRoots(workspace, packages));
            Assert.Contains("source", error.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(workspace, true);
        }
    }

    [Fact]
    public void ResolvedDependencyGraphAllowsPinnedRuntimeLibraryPacksSource()
    {
        var workspace = Path.Combine(Path.GetTempPath(), "mutate4csharp-assets", Guid.NewGuid().ToString("N"));
        var packages = Path.Combine(workspace, "private-packages");
        var obj = Path.Combine(workspace, "obj");
        var selectedConfig = Path.Combine(workspace, "NuGet.Config");
        var runtimeLibraryPacks = GetExpectedRuntimeLibraryPacksPath();
        Directory.CreateDirectory(obj);
        Directory.CreateDirectory(packages);
        File.WriteAllText(selectedConfig,
            "<configuration><packageSources><clear /><add key=\"private\" value=\"private-feed\" /></packageSources></configuration>");
        File.WriteAllText(Path.Combine(obj, "project.assets.json"), JsonSerializer.Serialize(new
        {
            packageFolders = new Dictionary<string, object> { [packages + Path.DirectorySeparatorChar] = new { } },
            project = new
            {
                restore = new
                {
                    sources = new Dictionary<string, object>
                    {
                        [Path.Combine(workspace, "private-feed")] = new { },
                        [runtimeLibraryPacks] = new { }
                    }
                }
            }
        }));

        try
        {
            ExecutionEnvironment.ValidateResolvedPackageRoots(workspace, packages, selectedConfig);
        }
        finally
        {
            Directory.Delete(workspace, true);
        }
    }

    [Fact]
    public void ResolvedDependencyGraphRejectsNearMissRuntimeLibraryPacksSources()
    {
        var workspace = Path.Combine(Path.GetTempPath(), "mutate4csharp-assets", Guid.NewGuid().ToString("N"));
        var packages = Path.Combine(workspace, "private-packages");
        var obj = Path.Combine(workspace, "obj");
        var selectedConfig = Path.Combine(workspace, "NuGet.Config");
        var runtimeLibraryPacks = GetExpectedRuntimeLibraryPacksPath();
        var otherRootLibraryPacks = Path.Combine(Path.GetTempPath(), "mutate4csharp-other-dotnet",
            Guid.NewGuid().ToString("N"), "library-packs");
        var refusedSources = new[]
        {
            runtimeLibraryPacks + "2",
            Path.Combine(runtimeLibraryPacks, "x"),
            otherRootLibraryPacks,
            new Uri(otherRootLibraryPacks + Path.DirectorySeparatorChar).AbsoluteUri
        };
        Directory.CreateDirectory(obj);
        Directory.CreateDirectory(packages);
        File.WriteAllText(selectedConfig,
            "<configuration><packageSources><clear /><add key=\"private\" value=\"private-feed\" /></packageSources></configuration>");

        try
        {
            foreach (var refusedSource in refusedSources)
            {
                File.WriteAllText(Path.Combine(obj, "project.assets.json"), JsonSerializer.Serialize(new
                {
                    packageFolders = new Dictionary<string, object>
                    {
                        [packages + Path.DirectorySeparatorChar] = new { }
                    },
                    project = new
                    {
                        restore = new
                        {
                            sources = new Dictionary<string, object>
                            {
                                [Path.Combine(workspace, "private-feed")] = new { },
                                [refusedSource] = new { }
                            }
                        }
                    }
                }));

                var error = Assert.Throws<ExecutionBoundaryIntegrityException>(() =>
                    ExecutionEnvironment.ValidateResolvedPackageRoots(workspace, packages, selectedConfig));
                Assert.Contains("outside the selected NuGet.Config", error.Message,
                    StringComparison.OrdinalIgnoreCase);
            }
        }
        finally
        {
            Directory.Delete(workspace, true);
        }
    }

    [Fact]
    public void ResolvedDependencyGraphUsesTheExplicitSelectedConfigForSourceAuthorization()
    {
        var workspace = Path.Combine(Path.GetTempPath(), "mutate4csharp-assets", Guid.NewGuid().ToString("N"));
        var packages = Path.Combine(workspace, "private-packages");
        var obj = Path.Combine(workspace, "obj");
        var selectedRoot = Path.Combine(Path.GetTempPath(), "mutate4csharp-selected-config",
            Guid.NewGuid().ToString("N"));
        var selectedConfig = Path.Combine(selectedRoot, "NuGet.Config");
        Directory.CreateDirectory(obj);
        Directory.CreateDirectory(packages);
        Directory.CreateDirectory(Path.GetDirectoryName(selectedConfig)!);
        File.WriteAllText(Path.Combine(workspace, "NuGet.Config"),
            "<configuration><packageSources><clear /><add key=\"permissive\" value=\"https://api.nuget.org/v3/index.json\" /></packageSources></configuration>");
        File.WriteAllText(selectedConfig,
            "<configuration><packageSources><clear /><add key=\"private\" value=\"private-feed\" /></packageSources></configuration>");
        File.WriteAllText(Path.Combine(obj, "project.assets.json"), JsonSerializer.Serialize(new
        {
            packageFolders = new Dictionary<string, object> { [packages + Path.DirectorySeparatorChar] = new { } },
            project = new { restore = new { sources = new Dictionary<string, object>
            {
                ["https://api.nuget.org/v3/index.json"] = new { }
            } } }
        }));

        try
        {
            ExecutionEnvironment.ValidateResolvedPackageRoots(workspace, packages);
            var error = Assert.Throws<ExecutionBoundaryIntegrityException>(() =>
                ExecutionEnvironment.ValidateResolvedPackageRoots(workspace, packages, selectedConfig));
            Assert.Contains("source", error.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(workspace, true);
            Directory.Delete(selectedRoot, true);
        }
    }

    [Fact]
    public void PackageBearingGraphWithoutSourcesIsRefused()
    {
        var workspace = Path.Combine(Path.GetTempPath(), "mutate4csharp-assets", Guid.NewGuid().ToString("N"));
        var packages = Path.Combine(workspace, "private-packages");
        var obj = Path.Combine(workspace, "obj");
        Directory.CreateDirectory(obj);
        Directory.CreateDirectory(packages);
        File.WriteAllText(Path.Combine(workspace, "NuGet.Config"),
            "<configuration><packageSources><clear /><add key=\"private\" value=\"private-feed\" /></packageSources></configuration>");
        File.WriteAllText(Path.Combine(obj, "project.assets.json"), JsonSerializer.Serialize(new
        {
            packageFolders = new Dictionary<string, object> { [packages + Path.DirectorySeparatorChar] = new { } },
            libraries = new Dictionary<string, object>
            {
                ["Example.Package/1.0.0"] = new { type = "package" }
            },
            project = new { restore = new { } }
        }));

        try
        {
            var error = Assert.Throws<ExecutionBoundaryIntegrityException>(() =>
                ExecutionEnvironment.ValidateResolvedPackageRoots(workspace, packages));
            Assert.Contains("sources", error.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(workspace, true);
        }
    }

    [Fact]
    public void PackageDownloadGraphWithoutSourcesIsRefused()
    {
        var workspace = Path.Combine(Path.GetTempPath(), "mutate4csharp-assets", Guid.NewGuid().ToString("N"));
        var packages = Path.Combine(workspace, "private-packages");
        var obj = Path.Combine(workspace, "obj");
        Directory.CreateDirectory(obj);
        Directory.CreateDirectory(packages);
        File.WriteAllText(Path.Combine(workspace, "NuGet.Config"),
            "<configuration><packageSources><clear /><add key=\"private\" value=\"private-feed\" /></packageSources></configuration>");
        File.WriteAllText(Path.Combine(obj, "project.assets.json"), JsonSerializer.Serialize(new
        {
            packageFolders = new Dictionary<string, object> { [packages + Path.DirectorySeparatorChar] = new { } },
            project = new
            {
                restore = new { },
                frameworks = new Dictionary<string, object>
                {
                    ["net10.0"] = new
                    {
                        downloadDependencies = new[] { new { name = "Example.Download", version = "[1.0.0]" } }
                    }
                }
            }
        }));

        try
        {
            var error = Assert.Throws<ExecutionBoundaryIntegrityException>(() =>
                ExecutionEnvironment.ValidateResolvedPackageRoots(workspace, packages));
            Assert.Contains("sources", error.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(workspace, true);
        }
    }

    [Fact]
    public async Task CancelledSnapshotExecutionAccountsForEveryRequestedMutation()
    {
        _repository.WriteText("Subject.cs", "public static class Subject { public static bool Value() => true; }\n");
        _repository.WriteText("App.Tests.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework><IsTestProject>true</IsTestProject></PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Microsoft.NET.Test.Sdk" Version="18.3.0" />
                <PackageReference Include="xunit.v3" Version="3.2.2" />
                <PackageReference Include="xunit.runner.visualstudio" Version="3.1.5" />
              </ItemGroup>
            </Project>
            """);
        _repository.WriteText("NuGet.Config", "<configuration><packageSources><clear /><add key=\"fixture\" value=\"local-packages\" /></packageSources></configuration>");
        CopyRestoredTestPackages(Path.Combine(_repository.Root, "local-packages"));
        _repository.Git("add", ".");
        _repository.Git("commit", "-m", "fixture");
        await using var snapshot = await SnapshotCapture.CaptureAsync(_repository.Root, "HEAD", [],
            SnapshotCaptureOptions.Default, CancellationToken.None);
        await using var environment = await ExecutionEnvironment.PrepareDependenciesAsync(snapshot,
            ["App.Tests.csproj"], DependencyPreparationOptions.Default with { Timeout = TimeSpan.FromMinutes(2) },
            CancellationToken.None);
        var start = File.ReadAllText(Path.Combine(_repository.Root, "Subject.cs")).IndexOf("true", StringComparison.Ordinal);
        var sites = Enumerable.Range(0, 3).Select(index =>
            new MutationSite(index, start, 4, 1, "false", "fixture", "scope", $"hash-{index}")).ToArray();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        var results = await MutationExecutor.ExecuteFromSnapshotAsync(snapshot, "Subject.cs", "App.Tests.csproj",
            sites, TimeSpan.FromSeconds(1), 1, environment, false, cancelled.Token);

        Assert.Equal(3, results.Count);
        Assert.All(results, result =>
        {
            Assert.Equal(MutantStatus.Error, result.Status);
            Assert.Equal("cancelled", result.Detail);
        });
    }

    [Fact]
    public void RestoredTestPackageLookupSelectsUniqueArchiveAndRejectsAmbiguity()
    {
        var root = Path.Combine(Path.GetTempPath(), "mutate4csharp-test-packages", Guid.NewGuid().ToString("N"));
        var first = Path.Combine(root, "first");
        var second = Path.Combine(root, "second");
        var relativePackagePath = Path.Combine("example.package", "1.2.3");
        var archiveName = "example.package.1.2.3.nupkg";
        var intendedArchive = Path.Combine(second, relativePackagePath, archiveName);

        try
        {
            var missing = Assert.Throws<InvalidOperationException>(() =>
                ResolveRestoredPackageArchive([first, second], relativePackagePath, archiveName));
            Assert.Contains("not found", missing.Message, StringComparison.OrdinalIgnoreCase);

            Directory.CreateDirectory(Path.GetDirectoryName(intendedArchive)!);
            File.WriteAllText(intendedArchive, "package");
            Assert.Equal(intendedArchive,
                ResolveRestoredPackageArchive([first, second], relativePackagePath, archiveName));

            var duplicateArchive = Path.Combine(first, relativePackagePath, archiveName);
            Directory.CreateDirectory(Path.GetDirectoryName(duplicateArchive)!);
            File.WriteAllText(duplicateArchive, "duplicate");
            var error = Assert.Throws<InvalidOperationException>(() =>
                ResolveRestoredPackageArchive([first, second], relativePackagePath, archiveName));
            Assert.Contains("ambiguous", error.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    public void Dispose() => _repository.Dispose();

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
            var archiveName = $"{id}.{version}.nupkg";
            var package = ResolveRestoredPackageArchive(packageRoots, relativePackagePath, archiveName);
            File.Copy(package, Path.Combine(destination, Path.GetFileName(package)));
        }
    }

    private static string ResolveRestoredPackageArchive(IEnumerable<string> packageRoots,
        string relativePackagePath, string archiveName)
    {
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var candidates = packageRoots.Select(root => Path.Combine(root, relativePackagePath, archiveName))
            .Where(File.Exists).Distinct(comparer).ToArray();
        return candidates.Length switch
        {
            1 => candidates[0],
            0 => throw new InvalidOperationException($"Restored package archive was not found: {archiveName}"),
            _ => throw new InvalidOperationException($"Restored package archive is ambiguous: {archiveName}")
        };
    }

    private static string GetExpectedRuntimeLibraryPacksPath()
    {
        var versionDirectory = new DirectoryInfo(RuntimeEnvironment.GetRuntimeDirectory().TrimEnd(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var runtimeRoot = versionDirectory.Parent?.Parent?.Parent ??
            throw new InvalidOperationException(
                "The runtime directory is not beneath the expected dotnet shared framework hierarchy.");
        return Path.Combine(runtimeRoot.FullName, "library-packs");
    }
}
