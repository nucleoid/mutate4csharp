using System.Diagnostics;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Mutate4CSharp.Tests;

public sealed class SnapshotCaptureTests : IDisposable
{
    private readonly SnapshotTestRepository _repository = new();

    [Fact]
    public async Task CapturesTrackedStagedUnstagedAndEligibleUntrackedBytesExactly()
    {
        _repository.Write("src/space name/Bom.cs", new byte[] { 0xef, 0xbb, 0xbf }.Concat(
            Encoding.UTF8.GetBytes("class Before { }\r\n")).ToArray());
        _repository.WriteText("config/settings.json", "{\n  \"value\": 1\n}\n");
        _repository.Git("add", ".");
        _repository.Git("commit", "-m", "fixture");
        _repository.WriteText("src/space name/Bom.cs", "class Staged { }\r\n", utf8Bom: true);
        _repository.Git("add", "src/space name/Bom.cs");
        _repository.WriteText("src/space name/Bom.cs", "class Working { }\n", utf8Bom: true);
        _repository.WriteText("assets/new asset.txt", "asset\r\n");

        await using var snapshot = await SnapshotCapture.CaptureAsync(_repository.Root, "HEAD", [],
            SnapshotCaptureOptions.Default, CancellationToken.None);

        Assert.Equal(_repository.Read("src/space name/Bom.cs"),
            File.ReadAllBytes(Path.Combine(snapshot.CaptureRoot, "src/space name/Bom.cs")));
        Assert.Equal(_repository.Read("assets/new asset.txt"),
            File.ReadAllBytes(Path.Combine(snapshot.CaptureRoot, "assets/new asset.txt")));
        Assert.Contains(snapshot.Files, file => file.RelativePath == "assets/new asset.txt" && !file.IsTracked);
        Assert.Equal(64, snapshot.Identity.CaptureId.Length);
        Assert.Equal(_repository.Git("rev-parse", "HEAD").Trim(), snapshot.Identity.BaseCommit);
    }

    [Fact]
    public async Task ExcludesSecretsWithoutOpeningThemAndRejectsRequiredExcludedInput()
    {
        _repository.WriteText("src/A.cs", "class A { }\n");
        _repository.WriteText("App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />\n");
        _repository.Git("add", ".");
        _repository.Git("commit", "-m", "fixture");
        _repository.WriteText(".env", "DO_NOT_READ=this-is-secret\n");
        _repository.WriteText("bin/generated.txt", "ignored\n");

        await using var snapshot = await SnapshotCapture.CaptureAsync(_repository.Root, "HEAD", [],
            SnapshotCaptureOptions.Default, CancellationToken.None);
        Assert.DoesNotContain(snapshot.Files, file => file.RelativePath is ".env" or "bin/generated.txt");

        var error = await Assert.ThrowsAsync<SnapshotCaptureException>(() => SnapshotCapture.CaptureAsync(
            _repository.Root, "HEAD", [Path.Combine(_repository.Root, ".env")],
            SnapshotCaptureOptions.Default, CancellationToken.None));
        Assert.Contains("excluded", error.Message, StringComparison.OrdinalIgnoreCase);

        _repository.WriteText("tools/bin/tracked.txt", "tracked ordinary input\n");
        _repository.WriteText(".env.example", "SECRET_SHAPED=do-not-open\n");
        _repository.WriteText("signing/library.snk", "private-key-shaped\n");
        _repository.WriteText("src/App/App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />\n");
        _repository.WriteText("src/App/bin/generated.dll", "build output\n");
        _repository.Git("add", ".");

        await using (var trackedExclusions = await SnapshotCapture.CaptureAsync(
                         _repository.Root, "HEAD", [], SnapshotCaptureOptions.Default, CancellationToken.None))
        {
            Assert.Contains(trackedExclusions.Files, file => file.RelativePath == "tools/bin/tracked.txt");
            Assert.DoesNotContain(trackedExclusions.Files, file => file.RelativePath is ".env.example" or
                "signing/library.snk" or "src/App/bin/generated.dll");
        }

        _repository.WriteText("src/App/App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><AssemblyOriginatorKeyFile>../../signing/library.snk</AssemblyOriginatorKeyFile></PropertyGroup>
            </Project>
            """);
        var signingError = await Assert.ThrowsAsync<SnapshotCaptureException>(() => SnapshotCapture.CaptureAsync(
            _repository.Root, "HEAD", [], SnapshotCaptureOptions.Default, CancellationToken.None));
        Assert.Contains("excluded or secret", signingError.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DetectsContentAndInventoryDriftDuringCaptureAndAtCompletion()
    {
        _repository.WriteText("src/A.cs", "class A { }\n");
        _repository.Git("add", ".");
        _repository.Git("commit", "-m", "fixture");
        var changed = false;
        var originalTimestamp = File.GetLastWriteTimeUtc(Path.Combine(_repository.Root, "src/A.cs"));
        var options = SnapshotCaptureOptions.Default with
        {
            Hook = (stage, _) =>
            {
                if (stage == SnapshotCaptureStage.BeforeCaptureValidation && !changed)
                {
                    changed = true;
                    _repository.WriteText("src/A.cs", "class B { }\n");
                    File.SetLastWriteTimeUtc(Path.Combine(_repository.Root, "src/A.cs"), originalTimestamp);
                }
            }
        };
        await Assert.ThrowsAsync<SnapshotDivergedException>(() => SnapshotCapture.CaptureAsync(
            _repository.Root, "HEAD", [], options, CancellationToken.None));

        _repository.WriteText("src/A.cs", "class C { }\n");
        await using var snapshot = await SnapshotCapture.CaptureAsync(_repository.Root, "HEAD", [],
            SnapshotCaptureOptions.Default, CancellationToken.None);
        _repository.WriteText("new.txt", "late\n");
        await Assert.ThrowsAsync<SnapshotDivergedException>(() => snapshot.ValidateOriginalAsync(CancellationToken.None));
    }

    [Fact]
    public async Task CaptureRechecksTheOriginallyRequestedSymbolicBaseRevision()
    {
        _repository.WriteText("src/A.cs", "class A { }\n");
        _repository.Git("add", ".");
        _repository.Git("commit", "-m", "fixture");
        var options = SnapshotCaptureOptions.Default with
        {
            Hook = (stage, _) =>
            {
                if (stage == SnapshotCaptureStage.BeforeCaptureValidation)
                    _repository.Git("commit", "--allow-empty", "-m", "move HEAD without changing inputs");
            }
        };

        var error = await Assert.ThrowsAsync<SnapshotDivergedException>(() => SnapshotCapture.CaptureAsync(
            _repository.Root, "HEAD", [], options, CancellationToken.None));

        Assert.Contains("base", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FirstCompletionReadFailureIsSnapshotDivergence()
    {
        _repository.WriteText("src/A.cs", "class A { }\n");
        _repository.Git("add", ".");
        _repository.Git("commit", "-m", "fixture");
        var options = SnapshotCaptureOptions.Default with
        {
            Hook = (stage, relativePath) =>
            {
                if (stage == SnapshotCaptureStage.BeforeOriginalFileHashed && relativePath == "src/A.cs")
                    throw new IOException("simulated mid-read failure");
            }
        };

        await Assert.ThrowsAsync<SnapshotDivergedException>(() => SnapshotCapture.CaptureAsync(
            _repository.Root, "HEAD", [], options, CancellationToken.None));
    }

    [Fact]
    public async Task CaptureFailurePreservesPrimaryFailureWhenCleanupAlsoFails()
    {
        _repository.WriteText("src/A.cs", "class A { }\n");
        _repository.Git("add", ".");
        _repository.Git("commit", "-m", "fixture");
        var parent = OwnedDirectory.PrivateParent("snapshots");
        var before = Directory.Exists(parent)
            ? Directory.EnumerateDirectories(parent).ToHashSet(StringComparer.Ordinal)
            : [];
        string? damagedRoot = null;
        var options = SnapshotCaptureOptions.Default with
        {
            Hook = (stage, _) =>
            {
                if (stage != SnapshotCaptureStage.BeforeCaptureValidation) return;
                damagedRoot = Directory.EnumerateDirectories(parent).Single(path => !before.Contains(path));
                File.WriteAllText(Path.Combine(damagedRoot, ".mutate4csharp-owner"), "wrong-owner");
                throw new SnapshotDivergedException("primary capture failure");
            }
        };

        try
        {
            var error = await Assert.ThrowsAnyAsync<SnapshotCaptureException>(() => SnapshotCapture.CaptureAsync(
                _repository.Root, "HEAD", [], options, CancellationToken.None));
            Assert.Contains("primary capture failure", error.ToString(), StringComparison.Ordinal);
            Assert.Contains("ownership marker", error.ToString(), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (damagedRoot is not null && Directory.Exists(damagedRoot)) Directory.Delete(damagedRoot, true);
        }
    }

    [Fact]
    public async Task DeletionAfterInventoryIsReportedAsSnapshotDivergence()
    {
        _repository.WriteText("src/A.cs", "class A { }\n");
        _repository.Git("add", ".");
        _repository.Git("commit", "-m", "fixture");
        var options = SnapshotCaptureOptions.Default with
        {
            Hook = (stage, _) =>
            {
                if (stage == SnapshotCaptureStage.AfterInventory)
                    File.Delete(Path.Combine(_repository.Root, "src/A.cs"));
            }
        };

        await Assert.ThrowsAsync<SnapshotDivergedException>(() => SnapshotCapture.CaptureAsync(
            _repository.Root, "HEAD", [], options, CancellationToken.None));
    }

    [Fact]
    public async Task RejectsLinksExternalReferencesAndSmallConfiguredLimits()
    {
        _repository.WriteText("src/A.cs", "class A { }\n");
        _repository.Git("add", ".");
        _repository.Git("commit", "-m", "fixture");
        if (!OperatingSystem.IsWindows())
        {
            File.CreateSymbolicLink(Path.Combine(_repository.Root, "linked.cs"), "/etc/hosts");
            _repository.Git("add", "linked.cs");
            await Assert.ThrowsAsync<SnapshotCaptureException>(() => SnapshotCapture.CaptureAsync(
                _repository.Root, "HEAD", [], SnapshotCaptureOptions.Default, CancellationToken.None));
            _repository.Git("reset", "HEAD", "linked.cs");
            File.Delete(Path.Combine(_repository.Root, "linked.cs"));
        }

        _repository.WriteText("App.csproj", "<Project><ItemGroup><ProjectReference Include=\"../Outside.csproj\" /></ItemGroup></Project>");
        await Assert.ThrowsAsync<SnapshotCaptureException>(() => SnapshotCapture.CaptureAsync(
            _repository.Root, "HEAD", [], SnapshotCaptureOptions.Default, CancellationToken.None));
        File.Delete(Path.Combine(_repository.Root, "App.csproj"));

        var bounded = SnapshotCaptureOptions.Default with { MaxFiles = 1 };
        _repository.WriteText("extra.txt", "extra");
        await Assert.ThrowsAsync<SnapshotLimitException>(() => SnapshotCapture.CaptureAsync(
            _repository.Root, "HEAD", [], bounded, CancellationToken.None));
    }

    [Fact]
    public async Task RejectsUncapturedImplicitMsBuildInputsWithoutReadingSecretUserContent()
    {
        _repository.WriteText("src/App/App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />\n");
        _repository.WriteText("src/App/App.csproj.user", "DO_NOT_REPORT=this-is-secret\n");
        _repository.WriteText("Directory.Build.props", "<Project><PropertyGroup><Hidden>true</Hidden></PropertyGroup></Project>\n");
        _repository.WriteText(".gitignore", "*.user\nDirectory.Build.props\n");
        _repository.Git("add", ".");
        _repository.Git("commit", "-m", "fixture");

        var error = await Assert.ThrowsAsync<SnapshotCaptureException>(() => SnapshotCapture.CaptureAsync(
            _repository.Root, "HEAD", [], SnapshotCaptureOptions.Default, CancellationToken.None));

        Assert.Contains("implicit", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("this-is-secret", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RejectsIgnoredBuildInputsUnderCapturedProjectWithoutReadingSafeExclusions()
    {
        _repository.WriteText("src/App/App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />\n");
        _repository.WriteText("src/App/Generated/Generated.cs", "class IgnoredGenerated { }\n");
        _repository.WriteText("src/App/bin/generated.dll", "build output\n");
        _repository.WriteText("src/App/obj/generated.cs", "build output\n");
        _repository.WriteText("src/App/.vs/state.json", "ide state\n");
        _repository.WriteText("src/App/.env", "DO_NOT_READ=this-is-secret\n");
        _repository.WriteText(".gitignore", "src/App/Generated/\nsrc/App/bin/\nsrc/App/obj/\nsrc/App/.vs/\nsrc/App/.env\n");
        _repository.Git("add", ".");
        _repository.Git("commit", "-m", "fixture");

        var error = await Assert.ThrowsAsync<SnapshotCaptureException>(() => SnapshotCapture.CaptureAsync(
            _repository.Root, "HEAD", [], SnapshotCaptureOptions.Default, CancellationToken.None));

        Assert.Contains("src/App/Generated", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("this-is-secret", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RejectsIgnoredInputsReachedByStaticGlobOutsideProjectDirectory()
    {
        _repository.WriteText("src/App/App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup><Compile Include="../Shared/**/*.cs" /></ItemGroup>
            </Project>
            """);
        _repository.WriteText("src/Shared/Generated.cs", "class IgnoredShared { }\n");
        _repository.WriteText(".gitignore", "src/Shared/\n");
        _repository.Git("add", ".");
        _repository.Git("commit", "-m", "fixture");

        var error = await Assert.ThrowsAsync<SnapshotCaptureException>(() => SnapshotCapture.CaptureAsync(
            _repository.Root, "HEAD", [], SnapshotCaptureOptions.Default, CancellationToken.None));

        Assert.Contains("ignored", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("glob", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RejectsStaticGlobBelowCollapsedIgnoredAncestor()
    {
        _repository.WriteText("src/App/App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup><Compile Include="../../artifacts/gen/**/*.cs" /></ItemGroup>
            </Project>
            """);
        _repository.WriteText("artifacts/gen/Generated.cs", "class IgnoredGenerated { }\n");
        _repository.WriteText(".gitignore", "artifacts/\n");
        _repository.Git("add", ".");
        _repository.Git("commit", "-m", "fixture");

        var error = await Assert.ThrowsAsync<SnapshotCaptureException>(() => SnapshotCapture.CaptureAsync(
            _repository.Root, "HEAD", [], SnapshotCaptureOptions.Default, CancellationToken.None));

        Assert.Contains("ignored", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("glob", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RejectsIgnoredInputReachedBySemicolonIncludeList()
    {
        _repository.WriteText("src/App/App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup><Compile Include="Program.cs;../../artifacts/gen/**/*.cs" /></ItemGroup>
            </Project>
            """);
        _repository.WriteText("src/App/Program.cs", "class Program { }\n");
        _repository.WriteText("artifacts/gen/Generated.cs", "class Generated { }\n");
        _repository.WriteText(".gitignore", "artifacts/\n");
        _repository.Git("add", ".");
        _repository.Git("commit", "-m", "fixture");

        var error = await Assert.ThrowsAsync<SnapshotCaptureException>(() => SnapshotCapture.CaptureAsync(
            _repository.Root, "HEAD", [], SnapshotCaptureOptions.Default, CancellationToken.None));

        Assert.Contains("ignored", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RejectsSecretReachedByGlobWithoutFixedPrefix()
    {
        _repository.WriteText("src/App/App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup><EmbeddedResource Include="**/*.pfx" /></ItemGroup>
            </Project>
            """);
        _repository.WriteText("src/App/test.pfx", "DO_NOT_READ=certificate\n");
        _repository.Git("add", ".");
        _repository.Git("commit", "-m", "fixture");

        var error = await Assert.ThrowsAsync<SnapshotCaptureException>(() => SnapshotCapture.CaptureAsync(
            _repository.Root, "HEAD", [], SnapshotCaptureOptions.Default, CancellationToken.None));

        Assert.Contains("excluded or secret", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("certificate", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RejectsSecretReachedByGlobWithoutFixedPrefixAtRepositoryRoot()
    {
        _repository.WriteText("App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup><EmbeddedResource Include="**/*.pfx" /></ItemGroup>
            </Project>
            """);
        _repository.WriteText("certs/dev.pfx", "DO_NOT_READ=certificate\n");
        _repository.Git("add", ".");
        _repository.Git("commit", "-m", "fixture");

        var error = await Assert.ThrowsAsync<SnapshotCaptureException>(() => SnapshotCapture.CaptureAsync(
            _repository.Root, "HEAD", [], SnapshotCaptureOptions.Default, CancellationToken.None));

        Assert.Contains("excluded or secret", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("certificate", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task GlobWithoutFixedPrefixDoesNotInspectSiblingProjectOutputs()
    {
        _repository.WriteText("src/App/App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup><Content Include="*.json" /></ItemGroup>
            </Project>
            """);
        _repository.WriteText("src/Other/Other.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />\n");
        _repository.WriteText("src/Other/bin/generated.json", "ignored output\n");
        _repository.WriteText(".gitignore", "src/Other/bin/\n");
        _repository.Git("add", ".");
        _repository.Git("commit", "-m", "fixture");

        await using var snapshot = await SnapshotCapture.CaptureAsync(_repository.Root, "HEAD", [],
            SnapshotCaptureOptions.Default, CancellationToken.None);

        Assert.DoesNotContain(snapshot.Files, file => file.RelativePath.Contains("/bin/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RejectsIgnoredInputReachedByRepositoryRootGlob()
    {
        _repository.WriteText("src/App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup><Compile Include="../**/*.cs" /></ItemGroup>
            </Project>
            """);
        _repository.WriteText("generated/Ignored.cs", "class Ignored { }\n");
        _repository.WriteText(".gitignore", "generated/\n");
        _repository.Git("add", ".");
        _repository.Git("commit", "-m", "fixture");

        var error = await Assert.ThrowsAsync<SnapshotCaptureException>(() => SnapshotCapture.CaptureAsync(
            _repository.Root, "HEAD", [], SnapshotCaptureOptions.Default, CancellationToken.None));

        Assert.Contains("ignored", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RejectsRelativeItemGlobDeclaredByImportedProps()
    {
        _repository.WriteText("src/App/App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />\n");
        _repository.WriteText("src/Directory.Build.props", """
            <Project><ItemGroup><Compile Include="Generated/**/*.cs" /></ItemGroup></Project>
            """);
        _repository.WriteText("src/Generated/Ignored.cs", "class IgnoredGenerated { }\n");
        _repository.WriteText(".gitignore", "src/Generated/\n");
        _repository.Git("add", ".");
        _repository.Git("commit", "-m", "fixture");

        var error = await Assert.ThrowsAsync<SnapshotCaptureException>(() => SnapshotCapture.CaptureAsync(
            _repository.Root, "HEAD", [], SnapshotCaptureOptions.Default, CancellationToken.None));

        Assert.Contains("project-relative", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RejectsTrackedSecretReachedByStaticGlob()
    {
        _repository.WriteText("src/App/App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup><EmbeddedResource Include="Certs/*.pfx" /></ItemGroup>
            </Project>
            """);
        _repository.WriteText("src/App/Certs/test.pfx", "DO_NOT_READ=certificate\n");
        _repository.Git("add", ".");
        _repository.Git("commit", "-m", "fixture");

        var error = await Assert.ThrowsAsync<SnapshotCaptureException>(() => SnapshotCapture.CaptureAsync(
            _repository.Root, "HEAD", [], SnapshotCaptureOptions.Default, CancellationToken.None));

        Assert.Contains("excluded or secret", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("certificate", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task StaticGlobHonorsSafeStaticExcludeFolders()
    {
        _repository.WriteText("src/App/App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup><Compile Include="../Common/**/*.cs" Exclude="../Common/obj/**;../Common/bin/**" /></ItemGroup>
            </Project>
            """);
        _repository.WriteText("src/Common/Kept.cs", "class Kept { }\n");
        _repository.WriteText("src/Common/obj/Ignored.cs", "class IgnoredObj { }\n");
        _repository.WriteText("src/Common/bin/Ignored.cs", "class IgnoredBin { }\n");
        _repository.WriteText(".gitignore", "src/Common/obj/\nsrc/Common/bin/\n");
        _repository.Git("add", ".");
        _repository.Git("commit", "-m", "fixture");

        await using var snapshot = await SnapshotCapture.CaptureAsync(_repository.Root, "HEAD", [],
            SnapshotCaptureOptions.Default, CancellationToken.None);

        Assert.Contains(snapshot.Files, file => file.RelativePath == "src/Common/Kept.cs");
    }

    [Fact]
    public async Task StaticGlobDoesNotTreatTrailingSlashExcludeAsRecursive()
    {
        _repository.WriteText("src/App/App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup><Content Include="../Common/**/*.txt" Exclude="../Common/Generated/" /></ItemGroup>
            </Project>
            """);
        _repository.WriteText("src/Common/Kept.txt", "kept\n");
        _repository.WriteText("src/Common/Generated/Nested/Ignored.txt", "ignored input\n");
        _repository.WriteText(".gitignore", "src/Common/Generated/\n");
        _repository.Git("add", ".");
        _repository.Git("commit", "-m", "fixture");

        var error = await Assert.ThrowsAsync<SnapshotCaptureException>(() => SnapshotCapture.CaptureAsync(
            _repository.Root, "HEAD", [], SnapshotCaptureOptions.Default, CancellationToken.None));

        Assert.Contains("ignored", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StaticGlobDoesNotBroadenPatternedExcludeIntoWholeDirectory()
    {
        _repository.WriteText("src/App/App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup><Compile Include="../Common/**/*.cs" Exclude="../Common/Ignored*.cs" /></ItemGroup>
            </Project>
            """);
        _repository.WriteText("src/Common/Other.cs", "class Other { }\n");
        _repository.WriteText(".gitignore", "src/Common/Other.cs\n");
        _repository.Git("add", ".");
        _repository.Git("commit", "-m", "fixture");

        var error = await Assert.ThrowsAsync<SnapshotCaptureException>(() => SnapshotCapture.CaptureAsync(
            _repository.Root, "HEAD", [], SnapshotCaptureOptions.Default, CancellationToken.None));

        Assert.Contains("ignored", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StaticGlobDoesNotTreatNonRecursiveExcludeAsWholeDirectory()
    {
        _repository.WriteText("src/App/App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup><Compile Include="../Common/**/*.cs" Exclude="../Common/Generated/*.cs" /></ItemGroup>
            </Project>
            """);
        _repository.WriteText("src/Common/Generated/Nested/Ignored.cs", "class Ignored { }\n");
        _repository.WriteText(".gitignore", "src/Common/Generated/\n");
        _repository.Git("add", ".");
        _repository.Git("commit", "-m", "fixture");

        var error = await Assert.ThrowsAsync<SnapshotCaptureException>(() => SnapshotCapture.CaptureAsync(
            _repository.Root, "HEAD", [], SnapshotCaptureOptions.Default, CancellationToken.None));

        Assert.Contains("ignored", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StaticGlobExcludeComparisonMatchesPlatformCaseSemantics()
    {
        if (OperatingSystem.IsWindows()) return;
        _repository.WriteText("src/App/App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup><Compile Include="../Common/**/*.cs" Exclude="../common/**" /></ItemGroup>
            </Project>
            """);
        _repository.WriteText("src/Common/Ignored.cs", "class Ignored { }\n");
        _repository.WriteText(".gitignore", "src/Common/\n");
        _repository.Git("add", ".");
        _repository.Git("commit", "-m", "fixture");

        var error = await Assert.ThrowsAsync<SnapshotCaptureException>(() => SnapshotCapture.CaptureAsync(
            _repository.Root, "HEAD", [], SnapshotCaptureOptions.Default, CancellationToken.None));

        Assert.Contains("ignored", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RejectsExternalGlobalAnalyzerConfigItem()
    {
        _repository.WriteText("App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup><GlobalAnalyzerConfigFiles Include="../policy.globalconfig" /></ItemGroup>
            </Project>
            """);
        _repository.Git("add", ".");
        _repository.Git("commit", "-m", "fixture");

        var error = await Assert.ThrowsAsync<SnapshotCaptureException>(() => SnapshotCapture.CaptureAsync(
            _repository.Root, "HEAD", [], SnapshotCaptureOptions.Default, CancellationToken.None));

        Assert.Contains("Build input", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RejectsExternalApplicationManifestProperty()
    {
        _repository.WriteText("App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><ApplicationManifest>../app.manifest</ApplicationManifest></PropertyGroup>
            </Project>
            """);
        _repository.Git("add", ".");
        _repository.Git("commit", "-m", "fixture");

        var error = await Assert.ThrowsAsync<SnapshotCaptureException>(() => SnapshotCapture.CaptureAsync(
            _repository.Root, "HEAD", [], SnapshotCaptureOptions.Default, CancellationToken.None));

        Assert.Contains("Build input", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("@/outside/extra.rsp")]
    [InlineData("-p:RestoreAdditionalProjectSources=https://api.nuget.org/v3/index.json")]
    [InlineData("-p:DirectoryBuildTargetsPath=/outside/Directory.Build.targets")]
    public async Task RejectsNonEmptyDirectoryBuildResponseFile(string response)
    {
        _repository.WriteText("App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />\n");
        _repository.WriteText("Directory.Build.rsp", response + "\n");
        _repository.Git("add", ".");
        _repository.Git("commit", "-m", "fixture");

        var error = await Assert.ThrowsAsync<SnapshotCaptureException>(() => SnapshotCapture.CaptureAsync(
            _repository.Root, "HEAD", [], SnapshotCaptureOptions.Default, CancellationToken.None));

        Assert.Contains("Directory.Build.rsp", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AllowsWhitespaceOnlyDirectoryBuildResponseFile()
    {
        _repository.WriteText("App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />\n");
        _repository.WriteText("Directory.Build.rsp", " \t\r\n\n");
        _repository.Git("add", ".");
        _repository.Git("commit", "-m", "fixture");

        await using var snapshot = await SnapshotCapture.CaptureAsync(_repository.Root, "HEAD", [],
            SnapshotCaptureOptions.Default, CancellationToken.None);

        Assert.Contains(snapshot.Files, file => file.RelativePath == "Directory.Build.rsp");
    }

    [Theory]
    [InlineData("<applicationmanifest>/outside/app.manifest</applicationmanifest>")]
    [InlineData("<restorefallbackfolders>/outside/packages</restorefallbackfolders>")]
    [InlineData("<RestoreSources>https://api.nuget.org/v3/index.json</RestoreSources>")]
    [InlineData("<RestoreAdditionalProjectSources>https://api.nuget.org/v3/index.json</RestoreAdditionalProjectSources>")]
    [InlineData("<RestorePackagesPath>/outside/packages</RestorePackagesPath>")]
    [InlineData("<DirectoryBuildTargetsPath>/outside/build.targets</DirectoryBuildTargetsPath>")]
    [InlineData("<CustomAfterMicrosoftCommonTargets>/outside/after.targets</CustomAfterMicrosoftCommonTargets>")]
    [InlineData("<MSBuildProjectExtensionsPath>/outside/obj</MSBuildProjectExtensionsPath>")]
    public async Task RejectsCaseInsensitiveRestorePackageAndImportRedirectionProperties(string property)
    {
        _repository.WriteText("App.csproj", $"<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup>{property}</PropertyGroup></Project>\n");
        _repository.Git("add", ".");
        _repository.Git("commit", "-m", "fixture");

        var error = await Assert.ThrowsAsync<SnapshotCaptureException>(() => SnapshotCapture.CaptureAsync(
            _repository.Root, "HEAD", [], SnapshotCaptureOptions.Default, CancellationToken.None));

        Assert.True(error.Message.Contains("cannot be frozen", StringComparison.OrdinalIgnoreCase) ||
                    error.Message.Contains("Build input", StringComparison.OrdinalIgnoreCase), error.Message);
    }

    [Theory]
    [InlineData("DirectoryPackagesPropsPath")]
    [InlineData("BaseIntermediateOutputPath")]
    [InlineData("MSBuildUserExtensionsPath")]
    [InlineData("MSBuildExtensionsPath")]
    public async Task RejectsAdditionalMsBuildImportRedirectionProperties(string propertyName)
    {
        _repository.WriteText("App.csproj", $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><{propertyName}>/outside/redirected</{propertyName}></PropertyGroup>
            </Project>
            """);
        _repository.Git("add", ".");
        _repository.Git("commit", "-m", "fixture");

        var error = await Assert.ThrowsAsync<SnapshotCaptureException>(() => SnapshotCapture.CaptureAsync(
            _repository.Root, "HEAD", [], SnapshotCaptureOptions.Default, CancellationToken.None));

        Assert.Contains("cannot be frozen", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("NuGetRestoreTargets")]
    [InlineData("CSharpCoreTargetsPath")]
    [InlineData("CSharpDesignTimeTargetsPath")]
    public async Task RejectsSdkCompilerAndRestoreTargetRedirectionProperties(string propertyName)
    {
        _repository.WriteText("App.csproj", $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><{propertyName}>/outside/redirected.targets</{propertyName}></PropertyGroup>
            </Project>
            """);
        _repository.Git("add", ".");
        _repository.Git("commit", "-m", "fixture");

        var error = await Assert.ThrowsAsync<SnapshotCaptureException>(() => SnapshotCapture.CaptureAsync(
            _repository.Root, "HEAD", [], SnapshotCaptureOptions.Default, CancellationToken.None));

        Assert.Contains("cannot be frozen", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("/outside/future.targets")]
    [InlineData("../../../outside/future.targets")]
    public async Task RejectsStaticPropertyValuesThatEscapeTheRepository(string value)
    {
        _repository.WriteText("src/App/App.csproj", $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><FutureSdkImport>{value}</FutureSdkImport></PropertyGroup>
            </Project>
            """);
        _repository.Git("add", ".");
        _repository.Git("commit", "-m", "fixture");

        var error = await Assert.ThrowsAsync<SnapshotCaptureException>(() => SnapshotCapture.CaptureAsync(
            _repository.Root, "HEAD", [], SnapshotCaptureOptions.Default, CancellationToken.None));

        Assert.Contains("static property", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RejectsImportRedirectorPairsIndependentOfPropertyName()
    {
        const string secret = "property-forwarding-secret";
        _repository.WriteText("App.csproj", $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <FutureSdkForwarding>Ordinary=true;CustomAfterMicrosoftCommonTargets=/outside/{secret}.targets</FutureSdkForwarding>
              </PropertyGroup>
            </Project>
            """);
        _repository.Git("add", ".");
        _repository.Git("commit", "-m", "fixture");

        var error = await Assert.ThrowsAsync<SnapshotCaptureException>(() => SnapshotCapture.CaptureAsync(
            _repository.Root, "HEAD", [], SnapshotCaptureOptions.Default, CancellationToken.None));

        Assert.Contains("CustomAfterMicrosoftCommonTargets", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(secret, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RejectsNonEmptyGenerateRestoreGraphProjectEntryInputProperties()
    {
        _repository.WriteText("Directory.Build.targets", """
            <Project><PropertyGroup>
              <_GenerateRestoreGraphProjectEntryInputProperties>ExcludeRestorePackageImports=true</_GenerateRestoreGraphProjectEntryInputProperties>
            </PropertyGroup></Project>
            """);
        _repository.WriteText("App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />\n");
        _repository.Git("add", ".");
        _repository.Git("commit", "-m", "fixture");

        var error = await Assert.ThrowsAsync<SnapshotCaptureException>(() => SnapshotCapture.CaptureAsync(
            _repository.Root, "HEAD", [], SnapshotCaptureOptions.Default, CancellationToken.None));

        Assert.Contains("_GenerateRestoreGraphProjectEntryInputProperties", error.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AllowsOrdinaryStaticScalarVersionBooleanAndInternalPathProperties()
    {
        _repository.WriteText("src/App/App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <Enabled>true</Enabled>
                <Version>1.2.3</Version>
                <Label>ordinary-value</Label>
                <FutureSdkImport>build/internal.targets</FutureSdkImport>
              </PropertyGroup>
            </Project>
            """);
        _repository.WriteText("src/App/build/internal.targets", "<Project />\n");
        _repository.Git("add", ".");
        _repository.Git("commit", "-m", "fixture");

        await using var snapshot = await SnapshotCapture.CaptureAsync(_repository.Root, "HEAD", [],
            SnapshotCaptureOptions.Default, CancellationToken.None);

        Assert.Contains(snapshot.Files, file => file.RelativePath == "src/App/build/internal.targets");
    }

    [Theory]
    [InlineData("_DirectoryBuildTargetsBasePath", "/outside/build")]
    [InlineData("_DirectoryBuildTargetsFile", "Custom.targets")]
    [InlineData("_DirectoryPackagesPropsBasePath", "/outside/packages")]
    [InlineData("_DirectoryPackagesPropsFile", "Custom.props")]
    [InlineData("NuGetPackageRoot", "/outside/packages")]
    [InlineData("NuGetPackageFolders", "/outside/packages")]
    [InlineData("ProjectAssetsFile", "/outside/project.assets.json")]
    [InlineData("RestoreOutputPath", "/outside/obj")]
    public async Task RejectsSdkImportAndResolvedGraphRedirectionProperties(string propertyName, string value)
    {
        _repository.WriteText("App.csproj", $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><{propertyName}>{value}</{propertyName}></PropertyGroup>
            </Project>
            """);
        _repository.Git("add", ".");
        _repository.Git("commit", "-m", "fixture");

        var error = await Assert.ThrowsAsync<SnapshotCaptureException>(() => SnapshotCapture.CaptureAsync(
            _repository.Root, "HEAD", [], SnapshotCaptureOptions.Default, CancellationToken.None));

        Assert.Contains("cannot be frozen", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RejectsExternalUsingTaskAssemblyFile()
    {
        _repository.WriteText("App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <UsingTask TaskName="ExternalTask" AssemblyFile="../outside/External.Tasks.dll" />
            </Project>
            """);
        _repository.Git("add", ".");
        _repository.Git("commit", "-m", "fixture");

        var error = await Assert.ThrowsAsync<SnapshotCaptureException>(() => SnapshotCapture.CaptureAsync(
            _repository.Root, "HEAD", [], SnapshotCaptureOptions.Default, CancellationToken.None));

        Assert.Contains("Build input", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RejectsExternalRoslynCodeTaskFactorySource()
    {
        _repository.WriteText("App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <UsingTask TaskName="ExternalTask"
                         TaskFactory="RoslynCodeTaskFactory"
                         AssemblyName="Microsoft.Build.Tasks.Core">
                <Task>
                  <Code Type="Fragment" Language="cs" Source="../outside/ExternalTask.cs" />
                </Task>
              </UsingTask>
            </Project>
            """);
        _repository.Git("add", ".");
        _repository.Git("commit", "-m", "fixture");

        var error = await Assert.ThrowsAsync<SnapshotCaptureException>(() => SnapshotCapture.CaptureAsync(
            _repository.Root, "HEAD", [], SnapshotCaptureOptions.Default, CancellationToken.None));

        Assert.Contains("Build input", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("/outside/Helper.dll")]
    [InlineData("../outside/Helper.dll")]
    [InlineData("$(ExternalRoot)/Helper.dll")]
    public async Task RejectsExternalOrDynamicRoslynCodeTaskFactoryReference(string include)
    {
        _repository.WriteText("App.csproj", $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <UsingTask TaskName="ExternalTask"
                         TaskFactory="RoslynCodeTaskFactory"
                         AssemblyName="Microsoft.Build.Tasks.Core">
                <Task>
                  <Reference Include="{include}" />
                  <Code Type="Fragment" Language="cs">Log.LogMessage("test");</Code>
                </Task>
              </UsingTask>
            </Project>
            """);
        _repository.Git("add", ".");
        _repository.Git("commit", "-m", "fixture");

        var error = await Assert.ThrowsAsync<SnapshotCaptureException>(() => SnapshotCapture.CaptureAsync(
            _repository.Root, "HEAD", [], SnapshotCaptureOptions.Default, CancellationToken.None));

        Assert.Contains("build input", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AllowsNameOnlyRoslynCodeTaskFactoryReference()
    {
        _repository.WriteText("App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <UsingTask TaskName="InlineTask"
                         TaskFactory="RoslynCodeTaskFactory"
                         AssemblyName="Microsoft.Build.Tasks.Core">
                <Task>
                  <Reference Include="System.Text.Json" />
                  <Code Type="Fragment" Language="cs">Log.LogMessage("test");</Code>
                </Task>
              </UsingTask>
            </Project>
            """);
        _repository.Git("add", ".");
        _repository.Git("commit", "-m", "fixture");

        await using var snapshot = await SnapshotCapture.CaptureAsync(_repository.Root, "HEAD", [],
            SnapshotCaptureOptions.Default, CancellationToken.None);

        Assert.Contains(snapshot.Files, file => file.RelativePath == "App.csproj");
    }

    [Fact]
    public async Task AllowsCapturedStaticRoslynCodeTaskFactoryReferencePath()
    {
        _repository.WriteText("App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <UsingTask TaskName="InlineTask"
                         TaskFactory="RoslynCodeTaskFactory"
                         AssemblyName="Microsoft.Build.Tasks.Core">
                <Task>
                  <Reference Include="tools/Helper.dll" />
                  <Code Type="Fragment" Language="cs">Log.LogMessage("test");</Code>
                </Task>
              </UsingTask>
            </Project>
            """);
        _repository.WriteText("tools/Helper.dll", "captured fixture");
        _repository.Git("add", ".");
        _repository.Git("commit", "-m", "fixture");

        await using var snapshot = await SnapshotCapture.CaptureAsync(_repository.Root, "HEAD", [],
            SnapshotCaptureOptions.Default, CancellationToken.None);

        Assert.Contains(snapshot.Files, file => file.RelativePath == "tools/Helper.dll");
    }

    [Fact]
    public async Task RemovePatternsDoNotCreateBuildInputDependencies()
    {
        _repository.WriteText("App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <Compile Remove="obj/**;bin/**" />
                <None Remove="**/*.pfx" />
              </ItemGroup>
            </Project>
            """);
        _repository.WriteText("certs/dev.pfx", "DO_NOT_READ=certificate\n");
        _repository.Git("add", ".");
        _repository.Git("commit", "-m", "fixture");

        await using var snapshot = await SnapshotCapture.CaptureAsync(_repository.Root, "HEAD", [],
            SnapshotCaptureOptions.Default, CancellationToken.None);

        Assert.DoesNotContain(snapshot.Files, file => file.RelativePath.EndsWith(".pfx", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CustomUpdateGlobDoesNotCreateExcludedBuildInputDependencies()
    {
        _repository.WriteText("App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup><CustomThing Update="**/*.pfx"><UnrelatedMetadata>value</UnrelatedMetadata></CustomThing></ItemGroup>
            </Project>
            """);
        _repository.WriteText("certs/dev.pfx", "DO_NOT_READ=certificate\n");
        _repository.Git("add", ".");
        _repository.Git("commit", "-m", "fixture");

        await using var snapshot = await SnapshotCapture.CaptureAsync(_repository.Root, "HEAD", [],
            SnapshotCaptureOptions.Default, CancellationToken.None);

        Assert.DoesNotContain(snapshot.Files, file => file.RelativePath.EndsWith(".pfx", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DefaultNoneUpdateGlobCopyingExcludedSecretIsRefused()
    {
        _repository.WriteText("App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup><None Update="certs/*.pfx"><copytooutputdirectory>PreserveNewest</copytooutputdirectory></None></ItemGroup>
            </Project>
            """);
        _repository.WriteText("certs/dev.pfx", "DO_NOT_READ=certificate\n");
        _repository.Git("add", ".");
        _repository.Git("commit", "-m", "fixture");

        var error = await Assert.ThrowsAsync<SnapshotCaptureException>(() => SnapshotCapture.CaptureAsync(
            _repository.Root, "HEAD", [], SnapshotCaptureOptions.Default, CancellationToken.None));

        Assert.Contains("excluded or secret", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("certificate", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task DynamicNonPathAndTargetScopedItemsDoNotCauseFalseRefusals()
    {
        _repository.WriteText("App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><AssemblyName>App</AssemblyName></PropertyGroup>
              <ItemGroup><InternalsVisibleTo Include="$(AssemblyName).Tests" /></ItemGroup>
              <Target Name="TrackGeneratedFiles" BeforeTargets="Build">
                <ItemGroup>
                  <FileWrites Include="$(IntermediateOutputPath)generated.txt" />
                  <Compile Include="$(IntermediateOutputPath)Generated.cs" />
                  <X Include="@(ReferencePath)" />
                </ItemGroup>
              </Target>
            </Project>
            """);
        _repository.Git("add", ".");
        _repository.Git("commit", "-m", "fixture");

        await using var snapshot = await SnapshotCapture.CaptureAsync(_repository.Root, "HEAD", [],
            SnapshotCaptureOptions.Default, CancellationToken.None);

        Assert.Contains(snapshot.Files, file => file.RelativePath == "App.csproj");
    }

    [Theory]
    [InlineData("ContentWithTargetPath")]
    [InlineData("ReferenceCopyLocalPaths")]
    [InlineData("EmbeddedFiles")]
    [InlineData("ApplicationDefinition")]
    public async Task RejectsStaticExternalIncludesForSdkConsumedItems(string itemName)
    {
        _repository.WriteText("App.csproj", $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup><{itemName} Include="../outside/input.bin" /></ItemGroup>
            </Project>
            """);
        _repository.Git("add", ".");
        _repository.Git("commit", "-m", "fixture");

        var error = await Assert.ThrowsAsync<SnapshotCaptureException>(() => SnapshotCapture.CaptureAsync(
            _repository.Root, "HEAD", [], SnapshotCaptureOptions.Default, CancellationToken.None));

        Assert.Contains("Build input", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("InternalsVisibleTo", "$(AssemblyName).Tests")]
    [InlineData("AssemblyMetadata", "%(Identity)")]
    [InlineData("CustomNonPathItem", "@(Compile)")]
    public async Task AllowsDynamicIncludesForKnownNonPathItems(string itemName, string include)
    {
        _repository.WriteText("App.csproj", $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><AssemblyName>App</AssemblyName></PropertyGroup>
              <ItemGroup><{itemName} Include="{include}" /></ItemGroup>
            </Project>
            """);
        _repository.Git("add", ".");
        _repository.Git("commit", "-m", "fixture");

        await using var snapshot = await SnapshotCapture.CaptureAsync(_repository.Root, "HEAD", [],
            SnapshotCaptureOptions.Default, CancellationToken.None);

        Assert.Contains(snapshot.Files, file => file.RelativePath == "App.csproj");
    }

    [Fact]
    public async Task UpdateGlobIgnoresOnlyProjectOutputEntries()
    {
        _repository.WriteText("App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup><None Update="appsettings*.json"><CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory></None></ItemGroup>
            </Project>
            """);
        _repository.WriteText("appsettings.json", "{}\n");
        _repository.WriteText(".gitignore", "obj/\nbin/\nTestResults/\n");
        _repository.Git("add", ".");
        _repository.Git("commit", "-m", "fixture");
        _repository.WriteText("obj/generated.json", "{}\n");
        _repository.WriteText("bin/generated.json", "{}\n");
        _repository.WriteText("TestResults/result.json", "{}\n");

        await using var snapshot = await SnapshotCapture.CaptureAsync(_repository.Root, "HEAD", [],
            SnapshotCaptureOptions.Default, CancellationToken.None);

        Assert.Contains(snapshot.Files, file => file.RelativePath == "appsettings.json");
    }

    [Fact]
    public async Task UpdateGlobStillRejectsExcludedSecretInput()
    {
        _repository.WriteText("App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup><None Update="**/*.json"><CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory></None></ItemGroup>
            </Project>
            """);
        _repository.WriteText("config/secrets.json", "DO_NOT_READ\n");
        _repository.Git("add", ".");
        _repository.Git("commit", "-m", "fixture");

        var error = await Assert.ThrowsAsync<SnapshotCaptureException>(() => SnapshotCapture.CaptureAsync(
            _repository.Root, "HEAD", [], SnapshotCaptureOptions.Default, CancellationToken.None));

        Assert.Contains("excluded or secret", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DO_NOT_READ", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task StaticUpdateOfExcludedInputIsRefused()
    {
        _repository.WriteText("App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup><None Update="certs/dev.pfx"><CopyToOutputDirectory>Always</CopyToOutputDirectory></None></ItemGroup>
            </Project>
            """);
        _repository.WriteText("certs/dev.pfx", "DO_NOT_READ=certificate\n");
        _repository.Git("add", ".");
        _repository.Git("commit", "-m", "fixture");

        var error = await Assert.ThrowsAsync<SnapshotCaptureException>(() => SnapshotCapture.CaptureAsync(
            _repository.Root, "HEAD", [], SnapshotCaptureOptions.Default, CancellationToken.None));

        Assert.Contains("excluded or secret", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("certificate", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RejectsNonCSharpProjectReference()
    {
        _repository.WriteText("App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup><ProjectReference Include="Other.fsproj" /></ItemGroup>
            </Project>
            """);
        _repository.WriteText("Other.fsproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />\n");
        _repository.Git("add", ".");
        _repository.Git("commit", "-m", "fixture");

        var error = await Assert.ThrowsAsync<SnapshotCaptureException>(() => SnapshotCapture.CaptureAsync(
            _repository.Root, "HEAD", [], SnapshotCaptureOptions.Default, CancellationToken.None));

        Assert.Contains("C#", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RejectsCaseInsensitiveNonCSharpProjectReference()
    {
        _repository.WriteText("App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup><projectreference Include="Other.vbproj" /></ItemGroup>
            </Project>
            """);
        _repository.WriteText("Other.vbproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />\n");
        _repository.Git("add", ".");
        _repository.Git("commit", "-m", "fixture");

        var error = await Assert.ThrowsAsync<SnapshotCaptureException>(() => SnapshotCapture.CaptureAsync(
            _repository.Root, "HEAD", [], SnapshotCaptureOptions.Default, CancellationToken.None));

        Assert.Contains("C#", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SolutionFolderWithDottedNameIsSkippedButExtensionlessProjectIsRefused()
    {
        const string folderGuid = "{2150E333-8FDC-42A3-9474-1A3956D46DE8}";
        const string csharpGuid = "{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}";
        _repository.WriteText("FolderOnly.sln", $"Microsoft Visual Studio Solution File, Format Version 12.00\nProject(\"{folderGuid}\") = \"MyApp.Core\", \"MyApp.Core\", \"{{11111111-1111-1111-1111-111111111111}}\"\nEndProject\n");
        _repository.Git("add", ".");
        _repository.Git("commit", "-m", "folder fixture");

        await using (var snapshot = await SnapshotCapture.CaptureAsync(_repository.Root, "HEAD", [],
                         SnapshotCaptureOptions.Default, CancellationToken.None))
            Assert.Contains(snapshot.Files, file => file.RelativePath == "FolderOnly.sln");

        _repository.WriteText("FolderOnly.sln", $"Microsoft Visual Studio Solution File, Format Version 12.00\nProject(\"{csharpGuid}\") = \"BuildProject\", \"BuildProject\", \"{{22222222-2222-2222-2222-222222222222}}\"\nEndProject\n");
        _repository.WriteText("BuildProject", "<Project />\n");
        _repository.Git("add", ".");
        _repository.Git("commit", "-m", "extensionless fixture");

        var error = await Assert.ThrowsAsync<SnapshotCaptureException>(() => SnapshotCapture.CaptureAsync(
            _repository.Root, "HEAD", [], SnapshotCaptureOptions.Default, CancellationToken.None));
        Assert.Contains("C#", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("App.sln", "Microsoft Visual Studio Solution File, Format Version 12.00\nProject(\"{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}\") = \"Other\", \"Other.vbproj\", \"{11111111-1111-1111-1111-111111111111}\"\nEndProject\n")]
    [InlineData("App.slnx", "<Solution><Project Path=\"Other.vbproj\" /></Solution>\n")]
    public async Task RejectsNonCSharpSolutionEntry(string solutionName, string solutionContent)
    {
        _repository.WriteText(solutionName, solutionContent);
        _repository.WriteText("Other.vbproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />\n");
        _repository.Git("add", ".");
        _repository.Git("commit", "-m", "fixture");

        var error = await Assert.ThrowsAsync<SnapshotCaptureException>(() => SnapshotCapture.CaptureAsync(
            _repository.Root, "HEAD", [], SnapshotCaptureOptions.Default, CancellationToken.None));

        Assert.Contains("C#", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RejectsImportedBuildFileExtensionThatIsNotInspected()
    {
        _repository.WriteText("src/App/App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk"><Import Project="../Shared/Shared.projitems" /></Project>
            """);
        _repository.WriteText("src/Shared/Shared.projitems", """
            <Project><ItemGroup><Compile Include="Generated/**/*.cs" /></ItemGroup></Project>
            """);
        _repository.Git("add", ".");
        _repository.Git("commit", "-m", "fixture");

        var error = await Assert.ThrowsAsync<SnapshotCaptureException>(() => SnapshotCapture.CaptureAsync(
            _repository.Root, "HEAD", [], SnapshotCaptureOptions.Default, CancellationToken.None));

        Assert.Contains("import", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("extension", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AllowsCapturedImportedPropsGlobBecauseMatchesAreInspected()
    {
        _repository.WriteText("src/App/App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk"><Import Project="../Shared/*.props" /></Project>
            """);
        _repository.WriteText("src/Shared/Safe.props", "<Project><PropertyGroup><Safe>true</Safe></PropertyGroup></Project>\n");
        _repository.Git("add", ".");
        _repository.Git("commit", "-m", "fixture");

        await using var snapshot = await SnapshotCapture.CaptureAsync(_repository.Root, "HEAD", [],
            SnapshotCaptureOptions.Default, CancellationToken.None);

        Assert.Contains(snapshot.Files, file => file.RelativePath == "src/Shared/Safe.props");
    }

    [Fact]
    public async Task SafeIgnoredProjectOutputsIdeStateAndSecretsRemainOmitted()
    {
        _repository.WriteText("src/App/App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />\n");
        _repository.WriteText("src/App/bin/generated.dll", "build output\n");
        _repository.WriteText("src/App/obj/generated.cs", "build output\n");
        _repository.WriteText("src/App/.vs/state.json", "ide state\n");
        _repository.WriteText("src/App/.env", "DO_NOT_READ=this-is-secret\n");
        _repository.WriteText(".gitignore", "src/App/bin/\nsrc/App/obj/\nsrc/App/.vs/\nsrc/App/.env\n");
        _repository.Git("add", ".");
        _repository.Git("commit", "-m", "fixture");

        await using var snapshot = await SnapshotCapture.CaptureAsync(_repository.Root, "HEAD", [],
            SnapshotCaptureOptions.Default, CancellationToken.None);

        Assert.DoesNotContain(snapshot.Files, file => file.RelativePath.StartsWith("src/App/bin/", StringComparison.Ordinal) ||
            file.RelativePath.StartsWith("src/App/obj/", StringComparison.Ordinal) ||
            file.RelativePath.StartsWith("src/App/.vs/", StringComparison.Ordinal) ||
            file.RelativePath == "src/App/.env");
    }

    [Fact]
    public async Task IgnoredExplicitReportPathDoesNotBecomeAnImplicitBuildInput()
    {
        _repository.WriteText("App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />\n");
        _repository.WriteText("reports/result.json", "old report\n");
        _repository.WriteText(".gitignore", "reports/\n");
        _repository.Git("add", ".");
        _repository.Git("commit", "-m", "fixture");
        var options = SnapshotCaptureOptions.Default with
        {
            ExcludedRelativePaths = new HashSet<string>(StringComparer.Ordinal) { "reports/result.json" }
        };

        await using var snapshot = await SnapshotCapture.CaptureAsync(_repository.Root, "HEAD", [], options,
            CancellationToken.None);

        Assert.DoesNotContain(snapshot.Files, file => file.RelativePath == "reports/result.json");
    }

    [Fact]
    public async Task IgnoredExplicitReportDirectoryValidationIsBounded()
    {
        _repository.WriteText("App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />\n");
        _repository.WriteText("reports/result.json", "old report\n");
        for (var index = 0; index < 4; index++)
            _repository.WriteText($"reports/extra-{index}.json", "extra\n");
        _repository.WriteText(".gitignore", "reports/\n");
        _repository.Git("add", ".");
        _repository.Git("commit", "-m", "fixture");
        var options = SnapshotCaptureOptions.Default with
        {
            MaxFiles = 3,
            ExcludedRelativePaths = new HashSet<string>(StringComparer.Ordinal) { "reports/result.json" }
        };

        var error = await Assert.ThrowsAsync<SnapshotLimitException>(() => SnapshotCapture.CaptureAsync(
            _repository.Root, "HEAD", [], options, CancellationToken.None));

        Assert.Contains("Ignored directory validation", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task IgnoredInventoryIsBoundedByConfiguredFileLimit()
    {
        _repository.WriteText("App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />\n");
        _repository.WriteText(".gitignore", "*.tmp\n");
        for (var index = 0; index < 4; index++) _repository.WriteText($"ignored-{index}.tmp", "ignored\n");
        _repository.Git("add", ".");
        _repository.Git("commit", "-m", "fixture");
        var options = SnapshotCaptureOptions.Default with { MaxFiles = 3 };

        var error = await Assert.ThrowsAsync<SnapshotLimitException>(() => SnapshotCapture.CaptureAsync(
            _repository.Root, "HEAD", [], options, CancellationToken.None));

        Assert.Contains("ignored", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("limit", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GitInventoryOutputIsBoundedBeforeBufferingTheRepositoryListing()
    {
        _repository.WriteText("App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />\n");
        _repository.Git("add", ".");
        _repository.Git("commit", "-m", "fixture");
        var options = SnapshotCaptureOptions.Default with { MaxGitOutputBytes = 8 };

        var error = await Assert.ThrowsAsync<SnapshotLimitException>(() => SnapshotCapture.CaptureAsync(
            _repository.Root, "HEAD", [], options, CancellationToken.None));

        Assert.Contains("Git output", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RejectsNuGetAndMsBuildFallbackPackageFolders()
    {
        _repository.WriteText("App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><RestoreFallbackFolders>external-packages</RestoreFallbackFolders></PropertyGroup>
            </Project>
            """);
        _repository.WriteText("NuGet.Config", """
            <configuration>
              <packageSources><clear /></packageSources>
              <fallbackPackageFolders><clear /><add key="external" value="external-packages" /></fallbackPackageFolders>
            </configuration>
            """);
        _repository.Git("add", ".");
        _repository.Git("commit", "-m", "fixture");

        var error = await Assert.ThrowsAsync<SnapshotCaptureException>(() => SnapshotCapture.CaptureAsync(
            _repository.Root, "HEAD", [], SnapshotCaptureOptions.Default, CancellationToken.None));

        Assert.Contains("fallback", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("RestoreFallbackFolders")]
    [InlineData("RestoreAdditionalProjectFallbackFolders")]
    [InlineData("RestoreAdditionalProjectFallbackFoldersExcludes")]
    public async Task RejectsEachMsBuildFallbackPackageProperty(string propertyName)
    {
        _repository.WriteText("App.csproj", $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><{propertyName}>external-packages</{propertyName}></PropertyGroup>
            </Project>
            """);
        _repository.Git("add", ".");
        _repository.Git("commit", "-m", "fixture");

        var error = await Assert.ThrowsAsync<SnapshotCaptureException>(() => SnapshotCapture.CaptureAsync(
            _repository.Root, "HEAD", [], SnapshotCaptureOptions.Default, CancellationToken.None));

        Assert.Contains("fallback", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("NuGet.Config")]
    [InlineData("nuget.config")]
    public async Task RejectsIgnoredNuGetConfigOnProjectAncestorChain(string configName)
    {
        _repository.WriteText("src/App/App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />\n");
        _repository.WriteText($"src/{configName}",
            "<configuration><packageSources><clear /></packageSources></configuration>\n");
        _repository.WriteText(".gitignore", $"src/{configName}\n");
        _repository.Git("add", ".");
        _repository.Git("commit", "-m", "fixture");

        var error = await Assert.ThrowsAsync<SnapshotCaptureException>(() => SnapshotCapture.CaptureAsync(
            _repository.Root, "HEAD", [], SnapshotCaptureOptions.Default, CancellationToken.None));

        Assert.Contains("NuGet.Config", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not captured", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RejectsGlobalConfigAboveRepositoryRoot()
    {
        var outer = Path.Combine(Path.GetTempPath(), "mutate4csharp globalconfig fixtures", Guid.NewGuid().ToString("N"));
        var repositoryRoot = Path.Combine(outer, "repo");
        Directory.CreateDirectory(repositoryRoot);
        File.WriteAllText(Path.Combine(outer, ".globalconfig"), "is_global = true\n");
        using var repository = new SnapshotTestRepository(repositoryRoot);
        repository.WriteText("src/App/App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />\n");
        repository.WriteText("src/App/A.cs", "class A { }\n");
        repository.Git("add", ".");
        repository.Git("commit", "-m", "fixture");

        try
        {
            var error = await Assert.ThrowsAsync<SnapshotCaptureException>(() => SnapshotCapture.CaptureAsync(
                repository.Root, "HEAD", [], SnapshotCaptureOptions.Default, CancellationToken.None));
            Assert.Contains(".globalconfig", error.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try { Directory.Delete(outer, true); } catch { }
        }
    }

    [Theory]
    [InlineData(".editorconfig", false)]
    [InlineData(".globalconfig", false)]
    [InlineData("Directory.Solution.props", true)]
    [InlineData("Directory.Solution.targets", true)]
    public async Task RejectsUncapturedCompilerAndSolutionConfiguration(string implicitName, bool needsSolution)
    {
        _repository.WriteText("src/App/App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />\n");
        _repository.WriteText("src/App/A.cs", "class A { }\n");
        if (needsSolution)
            _repository.WriteText("src/App.slnx", "<Solution><Project Path=\"App/App.csproj\" /></Solution>\n");
        _repository.WriteText(implicitName, "# ignored configuration\n");
        _repository.WriteText(".gitignore", implicitName + "\n");
        _repository.Git("add", ".");
        _repository.Git("commit", "-m", "fixture");

        var error = await Assert.ThrowsAsync<SnapshotCaptureException>(() => SnapshotCapture.CaptureAsync(
            _repository.Root, "HEAD", [], SnapshotCaptureOptions.Default, CancellationToken.None));

        Assert.Contains("implicit", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(implicitName, error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LinuxArm64IsRefusedUntilItsNativeStatLayoutIsValidated()
    {
        var method = typeof(SnapshotCapture).GetMethod("IsSupportedLinuxArchitecture",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

        Assert.NotNull(method);
        Assert.False((bool)method!.Invoke(null, [Architecture.Arm64])!);
        Assert.True((bool)method.Invoke(null, [Architecture.X64])!);
    }

    [Fact]
    public async Task CloneRefusesCaptureBytesTamperedAfterIdentityWasRecorded()
    {
        _repository.WriteText("src/A.cs", "class A { }\n");
        _repository.Git("add", ".");
        _repository.Git("commit", "-m", "fixture");
        await using var snapshot = await SnapshotCapture.CaptureAsync(_repository.Root, "HEAD", [],
            SnapshotCaptureOptions.Default, CancellationToken.None);
        File.WriteAllText(Path.Combine(snapshot.CaptureRoot, "src/A.cs"), "class T { }\n");

        await Assert.ThrowsAsync<SnapshotDivergedException>(() =>
            SnapshotWorkspace.CreateCloneAsync(snapshot, "tampered", CancellationToken.None));
    }

    [Fact]
    public async Task LinuxSocketReplacingTrackedInputIsRefusedWithoutOpeningContent()
    {
        if (!OperatingSystem.IsLinux()) return;
        _repository.WriteText("src/A.cs", "class A { }\n");
        _repository.Git("add", ".");
        _repository.Git("commit", "-m", "fixture");
        var path = Path.Combine(_repository.Root, "src/A.cs");
        File.Delete(path);
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        socket.Bind(new UnixDomainSocketEndPoint(path));

        var error = await Assert.ThrowsAsync<SnapshotCaptureException>(() => SnapshotCapture.CaptureAsync(
            _repository.Root, "HEAD", [], SnapshotCaptureOptions.Default, CancellationToken.None));

        Assert.Contains("special", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InvalidUtf8GitFilenameIsAReportedSnapshotRefusal()
    {
        if (!OperatingSystem.IsLinux()) return;
        _repository.WriteText("src/A.cs", "class A { }\n");
        _repository.Git("add", ".");
        _repository.Git("commit", "-m", "fixture");
        var prefix = Encoding.UTF8.GetBytes(Path.Combine(_repository.Root, "invalid-"));
        var rawPath = prefix.Concat(new byte[] { 0xff, (byte)'.', (byte)'c', (byte)'s', 0 }).ToArray();
        var descriptor = Open(rawPath, 0x41, 0x180);
        Assert.True(descriptor >= 0, $"open failed with errno {Marshal.GetLastPInvokeError()}");
        Assert.Equal(0, Close(descriptor));

        var error = await Assert.ThrowsAsync<SnapshotCaptureException>(() => SnapshotCapture.CaptureAsync(
            _repository.Root, "HEAD", [], SnapshotCaptureOptions.Default, CancellationToken.None));

        Assert.Contains("UTF-8", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InvalidUtf8GitFilenameFollowedByMoreThanPipeCapacityRefusesWithoutHanging()
    {
        if (!OperatingSystem.IsLinux()) return;
        _repository.WriteText("src/A.cs", "class A { }\n");
        var suffix = new string('x', 96);
        for (var index = 0; index < 620; index++)
            _repository.WriteText($"zzzz/{index:D4}-{suffix}.cs", string.Empty);
        _repository.Git("add", ".");
        _repository.Git("commit", "-m", "large fixture");
        var prefix = Encoding.UTF8.GetBytes(Path.Combine(_repository.Root, "invalid-"));
        var rawPath = prefix.Concat(new byte[] { 0xff, (byte)'.', (byte)'c', (byte)'s', 0 }).ToArray();
        var descriptor = Open(rawPath, 0x41, 0x180);
        Assert.True(descriptor >= 0, $"open failed with errno {Marshal.GetLastPInvokeError()}");
        Assert.Equal(0, Close(descriptor));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var error = await Assert.ThrowsAsync<SnapshotCaptureException>(() => SnapshotCapture.CaptureAsync(
            _repository.Root, "HEAD", [], SnapshotCaptureOptions.Default, deadline.Token));

        Assert.False(deadline.IsCancellationRequested, "Git inventory reached the external hang guard.");
        Assert.Contains("UTF-8", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CancellationAfterOwnedCaptureCreationRemovesTheCaptureDirectory()
    {
        _repository.WriteText("src/A.cs", "class A { }\n");
        _repository.Git("add", ".");
        _repository.Git("commit", "-m", "fixture");
        var parent = OwnedDirectory.PrivateParent("snapshots");
        var before = Directory.Exists(parent)
            ? Directory.EnumerateDirectories(parent).ToHashSet(StringComparer.Ordinal)
            : [];
        using var cancelled = new CancellationTokenSource();
        var options = SnapshotCaptureOptions.Default with
        {
            Hook = (stage, _) =>
            {
                if (stage == SnapshotCaptureStage.AfterFileCopied) cancelled.Cancel();
            }
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SnapshotCapture.CaptureAsync(
            _repository.Root, "HEAD", [], options, cancelled.Token));

        var after = Directory.Exists(parent)
            ? Directory.EnumerateDirectories(parent).ToHashSet(StringComparer.Ordinal)
            : [];
        Assert.Equal(before, after);
    }

    [Theory]
    [InlineData("AlternateCommonProps")]
    [InlineData("BeforeMicrosoftNETSdkTargets")]
    [InlineData("LanguageTargets")]
    [InlineData("AfterMicrosoftNETSdkTargets")]
    [InlineData("AfterMicrosoftNetSdkProps")]
    [InlineData("BeforeTargetFrameworkInferenceTargets")]
    public async Task RejectsSdkImportPropertyRedirectorsEvenWhenDynamic(string propertyName)
    {
        _repository.WriteText("App.csproj", $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><{propertyName}>$(HOME)/outside.targets</{propertyName}></PropertyGroup>
            </Project>
            """);
        _repository.Git("add", ".");
        _repository.Git("commit", "-m", "fixture");

        var error = await Assert.ThrowsAsync<SnapshotCaptureException>(() => SnapshotCapture.CaptureAsync(
            _repository.Root, "HEAD", [], SnapshotCaptureOptions.Default, CancellationToken.None));

        Assert.Contains("cannot be frozen", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PinnedSdkPurePropertyImportRedirectorsAreAllCoveredByStrictRule()
    {
        string[] names =
        [
            "AdditionalImport", "AfterMicrosoftNetSdkProps", "AfterMicrosoftNETSdkTargets",
            "AfterTargetFrameworkInferenceTargets", "AlternateCommonProps", "BeforeMicrosoftNETSdkTargets",
            "BeforeTargetFrameworkInferenceTargets", "CodeAnalysisTargets", "CommonTargetsPath",
            "CSharpCoreTargetsPath", "CSharpDesignTimeTargetsPath", "CSharpTargetsPath",
            "CustomAfterBlazorWebAssemblySdkTargets", "CustomAfterDirectoryBuildTargets",
            "CustomAfterMicrosoftCommonCrossTargetingTargets", "CustomAfterMicrosoftCommonTargets",
            "CustomAfterMicrosoftCSharpTargets", "CustomAfterMicrosoftVisualBasicTargets",
            "CustomAfterRazorSdkTargets", "CustomAfterStaticWebAssetsSdkTargets",
            "CustomBeforeBlazorWebAssemblySdkTargets", "CustomBeforeDirectoryBuildTargets",
            "CustomBeforeMicrosoftCommonCrossTargetingTargets", "CustomBeforeMicrosoftCommonTargets",
            "CustomBeforeMicrosoftCSharpTargets", "CustomBeforeMicrosoftVisualBasicTargets",
            "CustomBeforeRazorSdkTargets", "CustomBeforeStaticWebAssetsSdkTargets",
            "DirectoryBuildTargetsPath", "DirectoryPackagesPropsPath", "FSharpDesignTimeTargetsPath",
            "FSharpOverridesTargetsShim", "FSharpPropsShim", "FSharpTargetsShim", "ILCompilerTargetsPath",
            "ILLinkTargetsPath", "LanguageTargets", "MsAppxPackageTargets", "MsTestToolsTargets",
            "NETCoreSdkBundledCliToolsProps", "NETCoreSdkBundledMSBuildInformationProps",
            "NETCoreSdkBundledVersionsProps", "NetFrameworkPropsPath", "NetFrameworkTargetsPath",
            "NuGetBuildTasksPackTargets", "NuGetRestoreTargets", "RazorDesignTimeTargets",
            "RazorSdkCurrentVersionProps", "RazorSdkCurrentVersionTargets", "ReportingServicesTargets",
            "StaticWebAssetsSdkCurrentVersionProps", "StaticWebAssetsSdkCurrentVersionTargets",
            "VisualBasicCoreTargetsPath", "VisualBasicDesignTimeTargetsPath", "VisualBasicTargetsPath",
            "WebPublishProfileFile", "_BlazorWebAssemblyPropsFile", "_BlazorWebAssemblyTargetsFile",
            "_BlazorWebAssemblyVersionedTargetsFile", "_WebAssemblyPropsFile", "_WebAssemblyTargetsFile"
        ];

        Assert.All(names, name => Assert.True(SnapshotCapture.IsRestoreOrImportRedirectionProperty(name), name));
    }

    [Theory]
    [InlineData("%2Foutside%2Fredirected.targets")]
    [InlineData("%2E%2E%2Foutside%2Fredirected.targets")]
    public async Task RejectsEscapedOctetsInPathBearingPropertyValues(string value)
    {
        _repository.WriteText("App.csproj", $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><FutureSdkImport>{value}</FutureSdkImport></PropertyGroup>
            </Project>
            """);
        _repository.Git("add", ".");
        _repository.Git("commit", "-m", "fixture");

        var error = await Assert.ThrowsAsync<SnapshotCaptureException>(() => SnapshotCapture.CaptureAsync(
            _repository.Root, "HEAD", [], SnapshotCaptureOptions.Default, CancellationToken.None));

        Assert.Contains("encoded", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RejectsEscapedOctetsInUsingTaskAssemblyPath()
    {
        _repository.WriteText("App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <UsingTask TaskName="ExternalTask" AssemblyFile="%2E%2E/outside/External.Tasks.dll" />
            </Project>
            """);
        _repository.Git("add", ".");
        _repository.Git("commit", "-m", "fixture");

        var error = await Assert.ThrowsAsync<SnapshotCaptureException>(() => SnapshotCapture.CaptureAsync(
            _repository.Root, "HEAD", [], SnapshotCaptureOptions.Default, CancellationToken.None));

        Assert.Contains("encoded", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RejectsAttributeFormHintPathMetadataOutsideTargets()
    {
        _repository.WriteText("App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup><Reference Include="External" HintPath="../outside/External.dll" /></ItemGroup>
            </Project>
            """);
        _repository.Git("add", ".");
        _repository.Git("commit", "-m", "fixture");

        var error = await Assert.ThrowsAsync<SnapshotCaptureException>(() => SnapshotCapture.CaptureAsync(
            _repository.Root, "HEAD", [], SnapshotCaptureOptions.Default, CancellationToken.None));

        Assert.Contains("snapshot root", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("AdditionalProperties", true)]
    [InlineData("AdditionalProperties", false)]
    [InlineData("Properties", true)]
    [InlineData("SetConfiguration", false)]
    [InlineData("SetPlatform", false)]
    [InlineData("SetTargetFramework", false)]
    public async Task RejectsProjectReferenceMetadataThatInjectsImportRedirectors(string metadataName,
        bool attributeForm)
    {
        var metadata = $"{metadataName}=\"LanguageTargets=../outside/redirect.targets\"";
        var item = attributeForm
            ? $"<ProjectReference Include=\"Referenced/Referenced.csproj\" {metadata} />"
            : $"<ProjectReference Include=\"Referenced/Referenced.csproj\"><{metadataName}>LanguageTargets=../outside/redirect.targets</{metadataName}></ProjectReference>";
        _repository.WriteText("App.csproj", $"""
            <Project Sdk="Microsoft.NET.Sdk"><ItemGroup>{item}</ItemGroup></Project>
            """);
        _repository.WriteText("Referenced/Referenced.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />\n");
        _repository.Git("add", ".");
        _repository.Git("commit", "-m", "fixture");

        var error = await Assert.ThrowsAsync<SnapshotCaptureException>(() => SnapshotCapture.CaptureAsync(
            _repository.Root, "HEAD", [], SnapshotCaptureOptions.Default, CancellationToken.None));

        Assert.Contains("LanguageTargets", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RejectsProjectReferenceRedirectorMetadataFromItemDefinitionGroup()
    {
        _repository.WriteText("App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemDefinitionGroup>
                <ProjectReference>
                  <AdditionalProperties>LanguageTargets=../outside/redirect.targets</AdditionalProperties>
                </ProjectReference>
              </ItemDefinitionGroup>
            </Project>
            """);
        _repository.Git("add", ".");
        _repository.Git("commit", "-m", "fixture");

        var error = await Assert.ThrowsAsync<SnapshotCaptureException>(() => SnapshotCapture.CaptureAsync(
            _repository.Root, "HEAD", [], SnapshotCaptureOptions.Default, CancellationToken.None));

        Assert.Contains("LanguageTargets", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    public static IEnumerable<object[]> DynamicProjectReferenceForwardingCases()
    {
        var metadataNames = new[]
        {
            "AdditionalProperties", "Properties", "SetConfiguration", "SetPlatform", "SetTargetFramework"
        };
        foreach (var metadataName in metadataNames)
        foreach (var attributeForm in new[] { true, false })
        foreach (var itemDefinition in new[] { true, false })
            yield return [metadataName, attributeForm, itemDefinition];
    }

    public static IEnumerable<object[]> SdkProjectReferencePipelineItemCases()
    {
        var itemNames = new[]
        {
            "ProjectReferenceWithConfiguration", "_ProjectReferenceWithConfiguration",
            "_MSBuildProjectReference", "_MSBuildProjectReferenceExistent"
        };
        var metadataNames = new[]
        {
            "AdditionalProperties", "Properties", "SetConfiguration", "SetPlatform", "SetTargetFramework"
        };
        foreach (var itemName in itemNames)
        foreach (var metadataName in metadataNames)
        foreach (var form in new[] { "attribute", "child", "item-definition" })
        foreach (var dynamicValue in new[] { false, true })
            yield return [itemName, metadataName, form, dynamicValue];
    }

    public static IEnumerable<object[]> AdditionalSdkProjectReferencePipelineItemCases()
    {
        var itemNames = new[]
        {
            "AnnotatedProjects", "UpdatedAnnotatedProjects", "ProjectsWithNearestPlatform", "FutureSdkProjectList"
        };
        var metadataNames = new[]
        {
            "AdditionalProperties", "Properties", "SetConfiguration", "SetPlatform", "SetTargetFramework"
        };
        foreach (var itemName in itemNames)
        foreach (var metadataName in metadataNames)
        foreach (var form in new[] { "attribute", "child", "item-definition" })
        foreach (var dynamicValue in new[] { false, true })
            yield return [itemName, metadataName, form, dynamicValue];
    }

    [Theory]
    [MemberData(nameof(AdditionalSdkProjectReferencePipelineItemCases))]
    public async Task RejectsForwardingMetadataIndependentOfSdkItemName(string itemName,
        string metadataName, string form, bool dynamicValue)
    {
        var value = dynamicValue
            ? "Ordinary=$(Forwarded)"
            : "CustomAfterMicrosoftCommonTargets=../outside/redirect.targets";
        var metadata = form == "attribute"
            ? $" {metadataName}=\"{value}\""
            : $"><{metadataName}>{value}</{metadataName}>";
        var close = form == "attribute" ? " />" : $"</{itemName}>";
        var include = form == "item-definition" ? "" : " Include=\"Referenced/Referenced.csproj\"";
        var item = $"<{itemName}{include}{metadata}{close}";
        var group = form == "item-definition"
            ? $"<ItemDefinitionGroup>{item}</ItemDefinitionGroup>"
            : $"<ItemGroup>{item}</ItemGroup>";
        _repository.WriteText("App.csproj", $"<Project Sdk=\"Microsoft.NET.Sdk\">{group}</Project>\n");
        _repository.WriteText("Referenced/Referenced.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />\n");
        _repository.Git("add", ".");
        _repository.Git("commit", "-m", "fixture");

        var error = await Assert.ThrowsAsync<SnapshotCaptureException>(() => SnapshotCapture.CaptureAsync(
            _repository.Root, "HEAD", [], SnapshotCaptureOptions.Default, CancellationToken.None));

        Assert.Contains(metadataName, error.Message, StringComparison.OrdinalIgnoreCase);
    }

    public static IEnumerable<object[]> SdkProjectReferenceIncludeItemNames()
    {
        yield return ["ProjectReferenceWithConfiguration"];
        yield return ["_ProjectReferenceWithConfiguration"];
        yield return ["_MSBuildProjectReference"];
        yield return ["_MSBuildProjectReferenceExistent"];
        yield return ["AnnotatedProjects"];
        yield return ["UpdatedAnnotatedProjects"];
        yield return ["ProjectsWithNearestPlatform"];
        yield return ["_ProjectsWithPlatformAssignment"];
        yield return ["_ProjectReferencePlatformPossibilities"];
        yield return ["_ProjectReferenceTargetFrameworkPossibilities"];
    }

    public static IEnumerable<object[]> SdkProjectEvaluationSinkItemNames()
    {
        yield return ["_AllProjects"];
        yield return ["AssembliestoCrossgen"];
        yield return ["PackageReferencesToStore"];
        yield return ["StaticWebAssetProjectConfiguration"];
        yield return ["RestoreGraphProjectInputItems"];
        yield return ["FilteredRestoreGraphProjectInputItems"];
        yield return ["FilteredRestoreGraphProjectInputItemsWithoutDuplicates"];
        yield return ["_CurrentRestoreProjectPathItems"];
        yield return ["_GenerateRestoreGraphProjectEntryInput"];
        yield return ["_InnerBuild"];
        yield return ["_InnerBuildProjects"];
        yield return ["_MSBuildProjectReferenceExistent"];
        yield return ["_ProjectConfigurationsWithEmbeddedPublishTargets"];
        yield return ["_ProjectConfigurationsWithPublishTargets"];
        yield return ["_ProjectReferencesFromAssetsFile"];
        yield return ["_ProjectsWithTFM"];
        yield return ["_ProjectsWithTFMNoBuild"];
        yield return ["_ProjectToTestWithTFM"];
        yield return ["_RestoreProjectPathItems"];
        yield return ["_RestoreProjectPathItemsWithoutDupes"];
        yield return ["_RidSpecificToolPackageProject"];
        yield return ["_StaticWebAssetProjectReference"];
        yield return ["_StaticWebAssetsEmbeddedProjectAssetConfigurations"];
        yield return ["_StaticWebAssetsProjectReference"];
        yield return ["_WatchProjects"];
    }

    [Fact]
    public async Task ProjectEvaluationSinkBoundaryMatchesPinnedSdkProjectsInputs()
    {
        var dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
        var listed = await ProcessTree.RunAsync(dotnet, ["--list-sdks"], _repository.Root,
            TimeSpan.FromMinutes(1), CancellationToken.None);
        Assert.Equal(0, listed.ExitCode);
        var sdkLine = listed.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Single(line => line.StartsWith("10.0.103 ", StringComparison.Ordinal));
        var openBracket = sdkLine.LastIndexOf('[', sdkLine.Length - 1);
        var sdkRoot = Path.Combine(sdkLine[(openBracket + 1)..].TrimEnd(']', '\r'), "10.0.103");
        var itemListPattern = new Regex(@"^@\((?<name>[^\)-]+)(?:->[^\)]*)?\)$",
            RegexOptions.CultureInvariant);
        var batchingIdentityPattern = new Regex(@"^%\((?<name>[^\.)]+)\.Identity\)$",
            RegexOptions.CultureInvariant);
        var reservedSelfProjects = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "$(MSBuildProjectFile)",
            "$(MSBuildProjectFullPath)",
            "$(MSBuildThisFileFullPath)"
        };
        var projectExpressions = Directory.EnumerateFiles(sdkRoot, "*", SearchOption.AllDirectories)
            .Where(path => Path.GetExtension(path) is ".targets" or ".props" or ".proj")
            .SelectMany(path => XDocument.Load(path).Descendants()
                .Where(element => element.Name.LocalName.Equals("MSBuild", StringComparison.OrdinalIgnoreCase))
                .Select(element => element.Attributes().SingleOrDefault(attribute =>
                    attribute.Name.LocalName.Equals("Projects", StringComparison.OrdinalIgnoreCase))?.Value)
                .Where(value => value is not null))
            .Select(value => value!.Trim())
            .ToArray();
        var discovered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var discoveredReservedSelfProjects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var expression in projectExpressions)
        {
            var itemList = itemListPattern.Match(expression);
            var batchingIdentity = batchingIdentityPattern.Match(expression);
            if (itemList.Success)
                discovered.Add(itemList.Groups["name"].Value);
            else if (batchingIdentity.Success)
                discovered.Add(batchingIdentity.Groups["name"].Value);
            else if (reservedSelfProjects.Contains(expression))
                discoveredReservedSelfProjects.Add(expression);
            else
                Assert.Fail($"Unclassified SDK 10.0.103 MSBuild Projects expression: {expression}");
        }
        Assert.Equal(reservedSelfProjects.Order(StringComparer.OrdinalIgnoreCase),
            discoveredReservedSelfProjects.Order(StringComparer.OrdinalIgnoreCase));
        var expected = SdkProjectEvaluationSinkItemNames().Select(row => (string)row[0])
            .Where(itemName => !itemName.Equals("RestoreGraphProjectInputItems", StringComparison.OrdinalIgnoreCase))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.Equal(expected.Order(StringComparer.OrdinalIgnoreCase),
            discovered.Order(StringComparer.OrdinalIgnoreCase));
        var predicate = typeof(SnapshotCapture).GetMethod("IsProjectEvaluationPipelineItem",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(predicate);
        foreach (var itemName in discovered)
            Assert.True((bool)predicate.Invoke(null, [itemName])!,
                $"SDK 10.0.103 MSBuild Projects sink is outside the capture boundary: {itemName}");
    }

    [Theory]
    [InlineData("Include", "$(UnsafeProject)")]
    [InlineData("Update", "@(UnsafeProjects)")]
    [InlineData("Include", "tools/Helper.xml")]
    [InlineData("Update", "tools/Helper.xml")]
    [InlineData("Include", "../Outside.csproj")]
    [InlineData("Update", "../Outside.csproj")]
    public async Task RejectsUnsafeTargetScopedProjectEvaluationSinkValues(string operation, string value)
    {
        _repository.WriteText("App.csproj", $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <Target Name="InjectProjects" BeforeTargets="Build">
                <ItemGroup><_AllProjects {operation}="{value}" /></ItemGroup>
              </Target>
            </Project>
            """);
        _repository.WriteText("tools/Helper.xml", "<Project />\n");
        _repository.Git("add", ".");
        _repository.Git("commit", "-m", "fixture");

        var error = await Assert.ThrowsAsync<SnapshotCaptureException>(() => SnapshotCapture.CaptureAsync(
            _repository.Root, "HEAD", [], SnapshotCaptureOptions.Default, CancellationToken.None));

        Assert.Contains("_AllProjects", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("$(MSBuildProjectFullPath)")]
    [InlineData("$(MSBuildThisFileFullPath)")]
    public async Task AllowsReservedSelfReferencesOnTargetScopedProjectEvaluationSinks(string value)
    {
        _repository.WriteText("App.csproj", $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <Target Name="Self" BeforeTargets="Build">
                <ItemGroup><_AllProjects Include="{value}" /></ItemGroup>
              </Target>
            </Project>
            """);
        _repository.Git("add", ".");
        _repository.Git("commit", "-m", "fixture");

        await using var snapshot = await SnapshotCapture.CaptureAsync(
            _repository.Root, "HEAD", [], SnapshotCaptureOptions.Default, CancellationToken.None);

        Assert.Contains(snapshot.Files, file => file.RelativePath == "App.csproj");
    }

    [Theory]
    [InlineData("props", "$(UnsafeProject)")]
    [InlineData("targets", "tools/Helper.xml")]
    [InlineData("targets", "../../Outside.csproj")]
    public async Task ImportedTargetScopedProjectEvaluationSinksShareTheBoundary(string extension, string value)
    {
        _repository.WriteText("App.csproj", $"""
            <Project Sdk="Microsoft.NET.Sdk"><Import Project="build/Injected.{extension}" /></Project>
            """);
        _repository.WriteText($"build/Injected.{extension}", $"""
            <Project><Target Name="InjectProjects" BeforeTargets="Build">
              <ItemGroup><_AllProjects Include="{value}" /></ItemGroup>
            </Target></Project>
            """);
        _repository.WriteText("tools/Helper.xml", "<Project />\n");
        _repository.Git("add", ".");
        _repository.Git("commit", "-m", "fixture");

        var error = await Assert.ThrowsAsync<SnapshotCaptureException>(() => SnapshotCapture.CaptureAsync(
            _repository.Root, "HEAD", [], SnapshotCaptureOptions.Default, CancellationToken.None));

        Assert.Contains("_AllProjects", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AllowsStaticCapturedCSharpProjectOnTargetScopedProjectEvaluationSink()
    {
        _repository.WriteText("App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <Target Name="Nested"><ItemGroup>
                <_AllProjects Include="tools/Helper.csproj" />
              </ItemGroup></Target>
            </Project>
            """);
        _repository.WriteText("tools/Helper.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />\n");
        _repository.Git("add", ".");
        _repository.Git("commit", "-m", "fixture");

        await using var snapshot = await SnapshotCapture.CaptureAsync(
            _repository.Root, "HEAD", [], SnapshotCaptureOptions.Default, CancellationToken.None);

        Assert.Contains(snapshot.Files, file => file.RelativePath == "tools/Helper.csproj");
    }

    [Theory]
    [InlineData(false, "../Outside.csproj")]
    [InlineData(false, "tools/Helper.xml")]
    [InlineData(false, "$(UnsafeProject)")]
    [InlineData(false, "@(UnsafeProjects)")]
    [InlineData(true, "../../Outside.csproj")]
    [InlineData(true, "../tools/Helper.xml")]
    [InlineData(true, "$(UnsafeProject)")]
    public async Task RejectsUnsafeDirectMsBuildProjectsValues(bool imported, string value)
    {
        var task = $"<Target Name=\"Nested\"><MSBuild Projects=\"{value}\" Targets=\"Build\" /></Target>";
        _repository.WriteText("App.csproj", imported
            ? "<Project Sdk=\"Microsoft.NET.Sdk\"><Import Project=\"build/Injected.targets\" /></Project>\n"
            : $"<Project Sdk=\"Microsoft.NET.Sdk\">{task}</Project>\n");
        if (imported) _repository.WriteText("build/Injected.targets", $"<Project>{task}</Project>\n");
        _repository.WriteText("tools/Helper.xml", "<Project />\n");
        _repository.Git("add", ".");
        _repository.Git("commit", "-m", "fixture");

        var error = await Assert.ThrowsAsync<SnapshotCaptureException>(() => SnapshotCapture.CaptureAsync(
            _repository.Root, "HEAD", [], SnapshotCaptureOptions.Default, CancellationToken.None));

        Assert.Contains("MSBuild Projects", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(false, "$(MSBuildProjectFullPath)")]
    [InlineData(false, "$(MSBuildProjectFile)")]
    [InlineData(true, "$(MSBuildThisFileFullPath)")]
    [InlineData(true, "$(MSBuildProjectFullPath)")]
    public async Task AllowsReservedSelfReferencesForDirectMsBuildProjects(bool imported, string value)
    {
        var task = $"<Target Name=\"Nested\"><MSBuild Projects=\"{value}\" Targets=\"Build\" /></Target>";
        _repository.WriteText("App.csproj", imported
            ? "<Project Sdk=\"Microsoft.NET.Sdk\"><Import Project=\"build/Injected.targets\" /></Project>\n"
            : $"<Project Sdk=\"Microsoft.NET.Sdk\">{task}</Project>\n");
        if (imported) _repository.WriteText("build/Injected.targets", $"<Project>{task}</Project>\n");
        _repository.Git("add", ".");
        _repository.Git("commit", "-m", "fixture");

        await using var snapshot = await SnapshotCapture.CaptureAsync(
            _repository.Root, "HEAD", [], SnapshotCaptureOptions.Default, CancellationToken.None);

        Assert.Contains(snapshot.Files, file => file.RelativePath == "App.csproj");
    }

    [Fact]
    public async Task AllowsStaticCapturedCSharpProjectForDirectMsBuildProjects()
    {
        _repository.WriteText("App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <Target Name="Nested"><MSBuild Projects="tools/Helper.csproj" Targets="Build" /></Target>
            </Project>
            """);
        _repository.WriteText("tools/Helper.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />\n");
        _repository.Git("add", ".");
        _repository.Git("commit", "-m", "fixture");

        await using var snapshot = await SnapshotCapture.CaptureAsync(
            _repository.Root, "HEAD", [], SnapshotCaptureOptions.Default, CancellationToken.None);

        Assert.Contains(snapshot.Files, file => file.RelativePath == "tools/Helper.csproj");
    }

    [Theory]
    [InlineData("$(UnsafeProjects)")]
    [InlineData("tools/Helper.xml")]
    [InlineData("../Outside.csproj")]
    public async Task RejectsUnsafeAdditionalProjectsValues(string value)
    {
        _repository.WriteText("App.csproj", $"""
            <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup>
              <AdditionalProjects>{value}</AdditionalProjects>
            </PropertyGroup></Project>
            """);
        _repository.WriteText("tools/Helper.xml", "<Project />\n");
        _repository.Git("add", ".");
        _repository.Git("commit", "-m", "fixture");

        var error = await Assert.ThrowsAsync<SnapshotCaptureException>(() => SnapshotCapture.CaptureAsync(
            _repository.Root, "HEAD", [], SnapshotCaptureOptions.Default, CancellationToken.None));

        Assert.Contains("AdditionalProjects", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AllowsStaticCapturedCSharpAdditionalProjects()
    {
        _repository.WriteText("App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup>
              <AdditionalProjects>tools/One.csproj;tools/Two.csproj</AdditionalProjects>
            </PropertyGroup></Project>
            """);
        _repository.WriteText("tools/One.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />\n");
        _repository.WriteText("tools/Two.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />\n");
        _repository.Git("add", ".");
        _repository.Git("commit", "-m", "fixture");

        await using var snapshot = await SnapshotCapture.CaptureAsync(
            _repository.Root, "HEAD", [], SnapshotCaptureOptions.Default, CancellationToken.None);

        Assert.Contains(snapshot.Files, file => file.RelativePath == "tools/One.csproj");
        Assert.Contains(snapshot.Files, file => file.RelativePath == "tools/Two.csproj");
    }

    [Theory]
    [InlineData("tools/Helper.csproj")]
    [InlineData("$(DynamicRestoreEntry)")]
    public async Task RejectsNonEmptyRestoreGraphProjectInputProperty(string value)
    {
        _repository.WriteText("App.csproj", $"""
            <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup>
              <RestoreGraphProjectInput>{value}</RestoreGraphProjectInput>
            </PropertyGroup></Project>
            """);
        _repository.WriteText("tools/Helper.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />\n");
        _repository.Git("add", ".");
        _repository.Git("commit", "-m", "fixture");

        var error = await Assert.ThrowsAsync<SnapshotCaptureException>(() => SnapshotCapture.CaptureAsync(
            _repository.Root, "HEAD", [], SnapshotCaptureOptions.Default, CancellationToken.None));

        Assert.Contains("RestoreGraphProjectInput", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [MemberData(nameof(SdkProjectEvaluationSinkItemNames))]
    public async Task RejectsNonCSharpFilesOnSdkProjectEvaluationSinks(string itemName)
    {
        _repository.WriteText("App.csproj", $"""
            <Project Sdk="Microsoft.NET.Sdk"><ItemGroup>
              <{itemName} Include="tools/Helper.xml" />
            </ItemGroup></Project>
            """);
        _repository.WriteText("tools/Helper.xml",
            "<Project><Import Project=\"/outside/External.targets\" /></Project>\n");
        _repository.Git("add", ".");
        _repository.Git("commit", "-m", "fixture");

        var error = await Assert.ThrowsAsync<SnapshotCaptureException>(() => SnapshotCapture.CaptureAsync(
            _repository.Root, "HEAD", [], SnapshotCaptureOptions.Default, CancellationToken.None));

        Assert.Contains("C#", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [MemberData(nameof(SdkProjectEvaluationSinkItemNames))]
    public async Task RejectsDynamicIncludesOnSdkProjectEvaluationSinks(string itemName)
    {
        _repository.WriteText("App.csproj", $"""
            <Project Sdk="Microsoft.NET.Sdk"><ItemGroup>
              <{itemName} Include="$(DynamicRestoreEntry)" />
            </ItemGroup></Project>
            """);
        _repository.Git("add", ".");
        _repository.Git("commit", "-m", "fixture");

        var error = await Assert.ThrowsAsync<SnapshotCaptureException>(() => SnapshotCapture.CaptureAsync(
            _repository.Root, "HEAD", [], SnapshotCaptureOptions.Default, CancellationToken.None));

        Assert.Contains(itemName, error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [MemberData(nameof(SdkProjectEvaluationSinkItemNames))]
    public async Task RejectsDynamicUpdatesOnSdkProjectEvaluationSinks(string itemName)
    {
        _repository.WriteText("App.csproj", $"""
            <Project Sdk="Microsoft.NET.Sdk"><ItemGroup>
              <{itemName} Update="$(DynamicRestoreEntry)" />
            </ItemGroup></Project>
            """);
        _repository.Git("add", ".");
        _repository.Git("commit", "-m", "fixture");

        var error = await Assert.ThrowsAsync<SnapshotCaptureException>(() => SnapshotCapture.CaptureAsync(
            _repository.Root, "HEAD", [], SnapshotCaptureOptions.Default, CancellationToken.None));

        Assert.Contains(itemName, error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [MemberData(nameof(SdkProjectEvaluationSinkItemNames))]
    public async Task AllowsStaticCapturedCSharpProjectsOnSdkProjectEvaluationSinks(string itemName)
    {
        _repository.WriteText("App.csproj", $"""
            <Project Sdk="Microsoft.NET.Sdk"><ItemGroup>
              <{itemName} Include="Referenced/Referenced.csproj" />
            </ItemGroup></Project>
            """);
        _repository.WriteText("Referenced/Referenced.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />\n");
        _repository.Git("add", ".");
        _repository.Git("commit", "-m", "fixture");

        await using var snapshot = await SnapshotCapture.CaptureAsync(
            _repository.Root, "HEAD", [], SnapshotCaptureOptions.Default, CancellationToken.None);

        Assert.Contains(snapshot.Files, file => file.RelativePath == "Referenced/Referenced.csproj");
    }

    [Theory]
    [InlineData("_InnerBuildProjects", "$(DynamicInnerBuild)")]
    [InlineData("_ProjectToTestWithTFM", "tools/Helper.xml")]
    [InlineData("StaticWebAssetProjectConfiguration", "tools/Helper.xml")]
    public async Task ImportedSdkProjectEvaluationSinkDeclarationsRemainFailClosed(string itemName, string value)
    {
        _repository.WriteText("App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk"><Import Project="build/Injected.targets" /></Project>
            """);
        _repository.WriteText("build/Injected.targets", $"""
            <Project><ItemGroup><{itemName} Include="{value}" /></ItemGroup></Project>
            """);
        _repository.WriteText("tools/Helper.xml", "<Project />\n");
        _repository.Git("add", ".");
        _repository.Git("commit", "-m", "fixture");

        var error = await Assert.ThrowsAsync<SnapshotCaptureException>(() => SnapshotCapture.CaptureAsync(
            _repository.Root, "HEAD", [], SnapshotCaptureOptions.Default, CancellationToken.None));

        Assert.Contains(itemName, error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AllowsOrdinaryPrivateItemsAndContainerLogicalPaths()
    {
        _repository.WriteText("App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><ContainerWorkingDirectory>/app</ContainerWorkingDirectory></PropertyGroup>
              <ItemGroup>
                <_PackFiles Include="asset.txt" />
                <ContainerEnvironmentVariable Include="LOG_PATH" Value="/var/log/app" />
              </ItemGroup>
            </Project>
            """);
        _repository.WriteText("asset.txt", "captured\n");
        _repository.Git("add", ".");
        _repository.Git("commit", "-m", "fixture");

        await using var snapshot = await SnapshotCapture.CaptureAsync(
            _repository.Root, "HEAD", [], SnapshotCaptureOptions.Default, CancellationToken.None);

        Assert.Contains(snapshot.Files, file => file.RelativePath == "asset.txt");
    }

    [Theory]
    [InlineData("Include")]
    [InlineData("Update")]
    public async Task RejectsNonCSharpProjectPathsIndependentOfItemName(string operation)
    {
        _repository.WriteText("App.csproj", $"""
            <Project Sdk="Microsoft.NET.Sdk"><ItemGroup>
              <FutureSdkBuildList {operation}="tools/Helper.proj" />
            </ItemGroup></Project>
            """);
        _repository.WriteText("tools/Helper.proj",
            "<Project><Import Project=\"/outside/External.targets\" /></Project>\n");
        _repository.Git("add", ".");
        _repository.Git("commit", "-m", "fixture");

        var error = await Assert.ThrowsAsync<SnapshotCaptureException>(() => SnapshotCapture.CaptureAsync(
            _repository.Root, "HEAD", [], SnapshotCaptureOptions.Default, CancellationToken.None));

        Assert.Contains("C#", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AllowsOrdinaryStaticCSharpProjectReference()
    {
        _repository.WriteText("App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk"><ItemGroup>
              <ProjectReference Include="Referenced/Referenced.csproj" />
            </ItemGroup></Project>
            """);
        _repository.WriteText("Referenced/Referenced.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />\n");
        _repository.Git("add", ".");
        _repository.Git("commit", "-m", "fixture");

        await using var snapshot = await SnapshotCapture.CaptureAsync(
            _repository.Root, "HEAD", [], SnapshotCaptureOptions.Default, CancellationToken.None);

        Assert.Contains(snapshot.Files, file => file.RelativePath == "Referenced/Referenced.csproj");
    }

    [Fact]
    public async Task RejectsImportRedirectorPairsIndependentOfMetadataName()
    {
        const string secret = "metadata-forwarding-secret";
        _repository.WriteText("App.csproj", $"""
            <Project Sdk="Microsoft.NET.Sdk"><ItemGroup>
              <FutureSdkItem Include="input.txt"
                FutureMetadata="Ordinary=true;CustomAfterMicrosoftCommonTargets=/outside/{secret}.targets" />
            </ItemGroup></Project>
            """);
        _repository.WriteText("input.txt", "input\n");
        _repository.Git("add", ".");
        _repository.Git("commit", "-m", "fixture");

        var error = await Assert.ThrowsAsync<SnapshotCaptureException>(() => SnapshotCapture.CaptureAsync(
            _repository.Root, "HEAD", [], SnapshotCaptureOptions.Default, CancellationToken.None));

        Assert.Contains("CustomAfterMicrosoftCommonTargets", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(secret, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EncodedPropertyRefusalDoesNotExposeRawValue()
    {
        const string propertyName = "FutureSdkImport";
        const string value = "Token=encoded-secret%40example.invalid";
        _repository.WriteText("App.csproj", $"""
            <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup>
              <{propertyName}>{value}</{propertyName}>
            </PropertyGroup></Project>
            """);
        _repository.Git("add", ".");
        _repository.Git("commit", "-m", "fixture");

        var error = await Assert.ThrowsAsync<SnapshotCaptureException>(() => SnapshotCapture.CaptureAsync(
            _repository.Root, "HEAD", [], SnapshotCaptureOptions.Default, CancellationToken.None));

        Assert.DoesNotContain(value, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DynamicExternalBuildInputRefusalDoesNotExposeRawValue()
    {
        const string value = "prefix-dynamic-secret$(Unknown).cs";
        _repository.WriteText("App.csproj", $"""
            <Project Sdk="Microsoft.NET.Sdk"><ItemGroup>
              <Compile Include="{value}" />
            </ItemGroup></Project>
            """);
        _repository.Git("add", ".");
        _repository.Git("commit", "-m", "fixture");

        var error = await Assert.ThrowsAsync<SnapshotCaptureException>(() => SnapshotCapture.CaptureAsync(
            _repository.Root, "HEAD", [], SnapshotCaptureOptions.Default, CancellationToken.None));

        Assert.DoesNotContain(value, error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Token=metadata-secret%40example.invalid")]
    [InlineData("malformed-metadata-secret")]
    [InlineData("Token=dynamic-metadata-secret$(Unknown)")]
    public async Task ForwardingMetadataRefusalsDoNotExposeRawValues(string value)
    {
        _repository.WriteText("App.csproj", $"""
            <Project Sdk="Microsoft.NET.Sdk"><ItemGroup>
              <ProjectReference Include="Referenced/Referenced.csproj" AdditionalProperties="{value}" />
            </ItemGroup></Project>
            """);
        _repository.WriteText("Referenced/Referenced.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />\n");
        _repository.Git("add", ".");
        _repository.Git("commit", "-m", "fixture");

        var error = await Assert.ThrowsAsync<SnapshotCaptureException>(() => SnapshotCapture.CaptureAsync(
            _repository.Root, "HEAD", [], SnapshotCaptureOptions.Default, CancellationToken.None));

        Assert.DoesNotContain(value, error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(SdkProjectReferenceIncludeItemNames))]
    public async Task RejectsDynamicIncludesOnSdkProjectReferencePipelineItems(string itemName)
    {
        _repository.WriteText("App.csproj", $"""
            <Project Sdk="Microsoft.NET.Sdk"><ItemGroup>
              <{itemName} Include="$(DynamicProjectPath)" />
            </ItemGroup></Project>
            """);
        _repository.Git("add", ".");
        _repository.Git("commit", "-m", "fixture");

        var error = await Assert.ThrowsAsync<SnapshotCaptureException>(() => SnapshotCapture.CaptureAsync(
            _repository.Root, "HEAD", [], SnapshotCaptureOptions.Default, CancellationToken.None));

        Assert.Contains(itemName, error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [MemberData(nameof(SdkProjectReferenceIncludeItemNames))]
    public async Task RejectsNonCSharpIncludesOnSdkProjectReferencePipelineItems(string itemName)
    {
        _repository.WriteText("App.csproj", $"""
            <Project Sdk="Microsoft.NET.Sdk"><ItemGroup>
              <{itemName} Include="tools/Helper.proj" />
            </ItemGroup></Project>
            """);
        _repository.WriteText("tools/Helper.proj", "<Project><Import Project=\"/outside/External.targets\" /></Project>\n");
        _repository.Git("add", ".");
        _repository.Git("commit", "-m", "fixture");

        var error = await Assert.ThrowsAsync<SnapshotCaptureException>(() => SnapshotCapture.CaptureAsync(
            _repository.Root, "HEAD", [], SnapshotCaptureOptions.Default, CancellationToken.None));

        Assert.Contains("C#", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ForwardingRefusalDoesNotExposeRawMetadataValue()
    {
        const string secret = "literal-secret-value";
        _repository.WriteText("App.csproj", $"""
            <Project Sdk="Microsoft.NET.Sdk"><ItemGroup>
              <FutureSdkProjectList Include="Referenced/Referenced.csproj"
                AdditionalProperties="Token={secret}$(Forwarded)" />
            </ItemGroup></Project>
            """);
        _repository.WriteText("Referenced/Referenced.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />\n");
        _repository.Git("add", ".");
        _repository.Git("commit", "-m", "fixture");

        var error = await Assert.ThrowsAsync<SnapshotCaptureException>(() => SnapshotCapture.CaptureAsync(
            _repository.Root, "HEAD", [], SnapshotCaptureOptions.Default, CancellationToken.None));

        Assert.DoesNotContain(secret, error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(SdkProjectReferencePipelineItemCases))]
    public async Task RejectsEvaluationTimeSdkProjectReferencePipelineItems(string itemName,
        string metadataName, string form, bool dynamicValue)
    {
        var value = dynamicValue
            ? "Ordinary=$(Forwarded)"
            : "CustomAfterMicrosoftCommonTargets=../outside/redirect.targets";
        var metadata = form == "attribute"
            ? $" {metadataName}=\"{value}\""
            : $"><{metadataName}>{value}</{metadataName}>";
        var close = form == "attribute" ? " />" : $"</{itemName}>";
        var include = form == "item-definition" ? "" : " Include=\"Referenced/Referenced.csproj\"";
        var item = $"<{itemName}{include}{metadata}{close}";
        var group = form == "item-definition"
            ? $"<ItemDefinitionGroup>{item}</ItemDefinitionGroup>"
            : $"<ItemGroup>{item}</ItemGroup>";
        _repository.WriteText("App.csproj", $"<Project Sdk=\"Microsoft.NET.Sdk\">{group}</Project>\n");
        _repository.WriteText("Referenced/Referenced.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />\n");
        _repository.Git("add", ".");
        _repository.Git("commit", "-m", "fixture");

        var error = await Assert.ThrowsAsync<SnapshotCaptureException>(() => SnapshotCapture.CaptureAsync(
            _repository.Root, "HEAD", [], SnapshotCaptureOptions.Default, CancellationToken.None));

        Assert.Contains(itemName, error.Message, StringComparison.OrdinalIgnoreCase);
    }

    public static IEnumerable<object[]> RedirectingProjectReferenceItemCases()
    {
        var itemNames = new[]
        {
            "ProjectReferenceWithConfiguration", "_ProjectReferenceWithConfiguration",
            "_MSBuildProjectReference", "_MSBuildProjectReferenceExistent"
        };
        var metadataNames = new[]
        {
            "AdditionalProperties", "Properties", "SetConfiguration", "SetPlatform", "SetTargetFramework"
        };
        foreach (var itemName in itemNames)
        foreach (var metadataName in metadataNames)
        foreach (var attributeForm in new[] { true, false })
        foreach (var itemDefinition in new[] { true, false })
            yield return [itemName, metadataName, attributeForm, itemDefinition];
    }

    [Theory]
    [MemberData(nameof(RedirectingProjectReferenceItemCases))]
    public async Task RejectsDynamicForwardingOnSdkProjectReferenceItemRoutes(string itemName,
        string metadataName, bool attributeForm, bool itemDefinition)
    {
        var metadata = attributeForm
            ? $"{metadataName}=\"Ordinary=$(Forwarded)\""
            : $"<{metadataName}>Ordinary=$(Forwarded)</{metadataName}>";
        var item = attributeForm
            ? $"<{itemName}{(itemDefinition ? "" : " Include=\"Referenced/Referenced.csproj\"")} {metadata} />"
            : $"<{itemName}{(itemDefinition ? "" : " Include=\"Referenced/Referenced.csproj\"")}>{metadata}</{itemName}>";
        var group = itemDefinition
            ? $"<ItemDefinitionGroup>{item}</ItemDefinitionGroup>"
            : $"<ItemGroup>{item}</ItemGroup>";
        _repository.WriteText("App.csproj", $"<Project Sdk=\"Microsoft.NET.Sdk\">{group}</Project>\n");
        _repository.WriteText("Referenced/Referenced.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />\n");
        _repository.Git("add", ".");
        _repository.Git("commit", "-m", "fixture");

        var error = await Assert.ThrowsAsync<SnapshotCaptureException>(() => SnapshotCapture.CaptureAsync(
            _repository.Root, "HEAD", [], SnapshotCaptureOptions.Default, CancellationToken.None));

        Assert.Contains("dynamic", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(metadataName, error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("ProjectReferenceWithConfiguration")]
    [InlineData("_ProjectReferenceWithConfiguration")]
    [InlineData("_MSBuildProjectReference")]
    [InlineData("_MSBuildProjectReferenceExistent")]
    public async Task RejectsStaticImportRedirectorsOnSdkProjectReferenceItemRoutes(string itemName)
    {
        _repository.WriteText("App.csproj", $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <{itemName} Include="Referenced/Referenced.csproj"
                  AdditionalProperties="LanguageTargets=../outside/redirect.targets" />
              </ItemGroup>
            </Project>
            """);
        _repository.WriteText("Referenced/Referenced.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />\n");
        _repository.Git("add", ".");
        _repository.Git("commit", "-m", "fixture");

        var error = await Assert.ThrowsAsync<SnapshotCaptureException>(() => SnapshotCapture.CaptureAsync(
            _repository.Root, "HEAD", [], SnapshotCaptureOptions.Default, CancellationToken.None));

        Assert.Contains("LanguageTargets", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [MemberData(nameof(DynamicProjectReferenceForwardingCases))]
    public async Task RejectsDynamicProjectReferenceForwardingBeforePairSplitting(string metadataName,
        bool attributeForm, bool itemDefinition)
    {
        var metadata = attributeForm
            ? $"{metadataName}=\"Ordinary=$(Forwarded)\""
            : $"<{metadataName}>Ordinary=$(Forwarded)</{metadataName}>";
        var projectReference = attributeForm
            ? $"<ProjectReference{(itemDefinition ? "" : " Include=\"Referenced/Referenced.csproj\"")} {metadata} />"
            : $"<ProjectReference{(itemDefinition ? "" : " Include=\"Referenced/Referenced.csproj\"")}>{metadata}</ProjectReference>";
        var group = itemDefinition
            ? $"<ItemDefinitionGroup>{projectReference}</ItemDefinitionGroup>"
            : $"<ItemGroup>{projectReference}</ItemGroup>";
        _repository.WriteText("App.csproj", $"<Project Sdk=\"Microsoft.NET.Sdk\">{group}</Project>\n");
        _repository.WriteText("Referenced/Referenced.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />\n");
        _repository.Git("add", ".");
        _repository.Git("commit", "-m", "fixture");

        var error = await Assert.ThrowsAsync<SnapshotCaptureException>(() => SnapshotCapture.CaptureAsync(
            _repository.Root, "HEAD", [], SnapshotCaptureOptions.Default, CancellationToken.None));

        Assert.Contains("dynamic", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(metadataName, error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("$(Forwarded)")]
    [InlineData("@(Forwarded)")]
    [InlineData("%(Forwarded)")]
    public async Task RejectsEveryDynamicExpressionKindInProjectReferenceForwarding(string expression)
    {
        _repository.WriteText("App.csproj", $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <ProjectReference Include="Referenced/Referenced.csproj"
                  AdditionalProperties="Ordinary={expression}" />
              </ItemGroup>
            </Project>
            """);
        _repository.WriteText("Referenced/Referenced.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />\n");
        _repository.Git("add", ".");
        _repository.Git("commit", "-m", "fixture");

        var error = await Assert.ThrowsAsync<SnapshotCaptureException>(() => SnapshotCapture.CaptureAsync(
            _repository.Root, "HEAD", [], SnapshotCaptureOptions.Default, CancellationToken.None));

        Assert.Contains("dynamic", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RejectsEncodedOctetsInOrdinaryItemMetadata(bool attributeForm)
    {
        var item = attributeForm
            ? "<Content Include=\"asset.txt\" Description=\"100%25 covered\" />"
            : "<Content Include=\"asset.txt\"><Description>100%25 covered</Description></Content>";
        _repository.WriteText("App.csproj", $"<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup>{item}</ItemGroup></Project>\n");
        _repository.WriteText("asset.txt", "captured\n");
        _repository.Git("add", ".");
        _repository.Git("commit", "-m", "fixture");

        var error = await Assert.ThrowsAsync<SnapshotCaptureException>(() => SnapshotCapture.CaptureAsync(
            _repository.Root, "HEAD", [], SnapshotCaptureOptions.Default, CancellationToken.None));

        Assert.Contains("encoded", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task EncodedOctetsInOrdinaryPropertiesRemainFailClosed()
    {
        _repository.WriteText("App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><NoWarn>CS0168%3BCS0219</NoWarn></PropertyGroup>
            </Project>
            """);
        _repository.Git("add", ".");
        _repository.Git("commit", "-m", "fixture");

        var error = await Assert.ThrowsAsync<SnapshotCaptureException>(() => SnapshotCapture.CaptureAsync(
            _repository.Root, "HEAD", [], SnapshotCaptureOptions.Default, CancellationToken.None));

        Assert.Contains("encoded", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OrdinaryNonPathItemMetadataRemainsSupported()
    {
        _repository.WriteText("App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <Content Include="asset.txt" CopyToOutputDirectory="PreserveNewest" Pack="true" PackagePath="/">
                  <Link>assets/linked.txt</Link>
                  <RepositoryUrl>https://example.invalid/project</RepositoryUrl>
                </Content>
              </ItemGroup>
            </Project>
            """);
        _repository.WriteText("asset.txt", "captured\n");
        _repository.Git("add", ".");
        _repository.Git("commit", "-m", "fixture");

        await using var snapshot = await SnapshotCapture.CaptureAsync(_repository.Root, "HEAD", [],
            SnapshotCaptureOptions.Default, CancellationToken.None);

        Assert.Contains(snapshot.Files, file => file.RelativePath == "asset.txt");
    }

    [Fact]
    public async Task OrdinaryProjectReferencePropertyMetadataRemainsSupported()
    {
        _repository.WriteText("App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <ProjectReference Include="Referenced/Referenced.csproj"
                  Properties="Configuration=Release;Platform=AnyCPU">
                  <AdditionalProperties>TargetFramework=net10.0</AdditionalProperties>
                  <SetConfiguration>Configuration=Release</SetConfiguration>
                  <SetPlatform>Platform=AnyCPU</SetPlatform>
                  <SetTargetFramework>TargetFramework=net10.0</SetTargetFramework>
                </ProjectReference>
              </ItemGroup>
            </Project>
            """);
        _repository.WriteText("Referenced/Referenced.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />\n");
        _repository.Git("add", ".");
        _repository.Git("commit", "-m", "fixture");

        await using var snapshot = await SnapshotCapture.CaptureAsync(_repository.Root, "HEAD", [],
            SnapshotCaptureOptions.Default, CancellationToken.None);

        Assert.Contains(snapshot.Files, file => file.RelativePath == "Referenced/Referenced.csproj");
    }

    [Theory]
    [InlineData("Property", "Rooted")]
    [InlineData("Property", "FileUri")]
    [InlineData("Property", "DriveQualified")]
    [InlineData("Metadata", "Rooted")]
    [InlineData("Metadata", "FileUri")]
    [InlineData("Metadata", "DriveQualified")]
    [InlineData("Properties", "Rooted")]
    [InlineData("Properties", "FileUri")]
    [InlineData("Properties", "DriveQualified")]
    [InlineData("AdditionalProperties", "Rooted")]
    [InlineData("AdditionalProperties", "FileUri")]
    [InlineData("AdditionalProperties", "DriveQualified")]
    public async Task RejectsRootedStaticValuesAcrossOrdinaryAndForwardingSurfaces(
        string surface, string pathKind)
    {
        var rooted = Path.Combine(_repository.Root, "captured", "value.txt");
        var value = pathKind switch
        {
            "FileUri" => new Uri(rooted).GetComponents(UriComponents.AbsoluteUri, UriFormat.Unescaped),
            "DriveQualified" => "C:/captured/value.txt",
            _ => rooted
        };
        var fragment = surface switch
        {
            "Property" => $"<PropertyGroup><OrdinaryValue>{value}</OrdinaryValue></PropertyGroup>",
            "Metadata" => $"<ItemGroup><Content Include=\"asset.txt\" OrdinaryValue=\"{value}\" /></ItemGroup>",
            _ => $"<Target Name=\"Nested\"><MSBuild Projects=\"$(MSBuildProjectFullPath)\" {surface}=\"Ordinary={value}\" /></Target>"
        };
        _repository.WriteText("App.csproj", $"<Project Sdk=\"Microsoft.NET.Sdk\">{fragment}</Project>\n");
        _repository.WriteText("asset.txt", "captured\n");
        _repository.WriteText("captured/value.txt", "captured\n");
        _repository.Git("add", ".");
        _repository.Git("commit", "-m", "fixture");

        var error = await Assert.ThrowsAsync<SnapshotCaptureException>(() => SnapshotCapture.CaptureAsync(
            _repository.Root, "HEAD", [], SnapshotCaptureOptions.Default, CancellationToken.None));

        Assert.Contains("rooted", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("Property", "https://example.invalid/captured/value")]
    [InlineData("Metadata", "https://example.invalid/captured/value")]
    [InlineData("Properties", "https://example.invalid/captured/value")]
    [InlineData("AdditionalProperties", "https://example.invalid/captured/value")]
    [InlineData("Property", "label:value")]
    [InlineData("Metadata", "label:value")]
    [InlineData("Properties", "label:value")]
    [InlineData("AdditionalProperties", "label:value")]
    public async Task NonFileUrlsRemainSupportedAcrossOrdinaryAndForwardingSurfaces(string surface, string url)
    {
        var fragment = surface switch
        {
            "Property" => $"<PropertyGroup><OrdinaryValue>{url}</OrdinaryValue></PropertyGroup>",
            "Metadata" => $"<ItemGroup><Content Include=\"asset.txt\" OrdinaryValue=\"{url}\" /></ItemGroup>",
            _ => $"<Target Name=\"Nested\"><MSBuild Projects=\"$(MSBuildProjectFullPath)\" {surface}=\"Ordinary={url}\" /></Target>"
        };
        _repository.WriteText("App.csproj", $"<Project Sdk=\"Microsoft.NET.Sdk\">{fragment}</Project>\n");
        _repository.WriteText("asset.txt", "captured\n");
        _repository.Git("add", ".");
        _repository.Git("commit", "-m", "fixture");

        await using var snapshot = await SnapshotCapture.CaptureAsync(
            _repository.Root, "HEAD", [], SnapshotCaptureOptions.Default, CancellationToken.None);

        Assert.Contains(snapshot.Files, file => file.RelativePath == "App.csproj");
    }

    [Theory]
    [InlineData("Property")]
    [InlineData("Metadata")]
    [InlineData("Properties")]
    [InlineData("AdditionalProperties")]
    public async Task WindowsDriveRelativeValuesRemainRejectedAcrossOrdinaryAndForwardingSurfaces(string surface)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows drive-relative path semantics require Windows.");
        const string value = "x:y";
        var fragment = surface switch
        {
            "Property" => $"<PropertyGroup><OrdinaryValue>{value}</OrdinaryValue></PropertyGroup>",
            "Metadata" => $"<ItemGroup><Content Include=\"asset.txt\" OrdinaryValue=\"{value}\" /></ItemGroup>",
            _ => $"<Target Name=\"Nested\"><MSBuild Projects=\"$(MSBuildProjectFullPath)\" {surface}=\"Ordinary={value}\" /></Target>"
        };
        _repository.WriteText("App.csproj", $"<Project Sdk=\"Microsoft.NET.Sdk\">{fragment}</Project>\n");
        _repository.WriteText("asset.txt", "captured\n");
        _repository.Git("add", ".");
        _repository.Git("commit", "-m", "fixture");

        var error = await Assert.ThrowsAsync<SnapshotCaptureException>(() => SnapshotCapture.CaptureAsync(
            _repository.Root, "HEAD", [], SnapshotCaptureOptions.Default, CancellationToken.None));

        Assert.Contains("rooted", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("nested/Second.proj", "build/nested/Second.proj")]
    [InlineData("nested/Second.projitems", "build/nested/Second.projitems")]
    [InlineData("nested/Second.xml", "build/nested/Second.xml")]
    [InlineData("nested/Second", "build/nested/Second")]
    [InlineData("nested/*", "build/nested/Second.targets")]
    [InlineData("*", "build/Other.targets")]
    public async Task RejectsUnsupportedMsBuildThisFileDirectoryImportSuffixes(
        string suffix, string capturedPath)
    {
        _repository.WriteText("App.csproj",
            "<Project Sdk=\"Microsoft.NET.Sdk\"><Import Project=\"build/Imported.targets\" /></Project>\n");
        _repository.WriteText("build/Imported.targets",
            $"<Project><Import Project=\"$(MSBuildThisFileDirectory){suffix}\" /></Project>\n");
        _repository.WriteText(capturedPath, "<Project />\n");
        _repository.Git("add", ".");
        _repository.Git("commit", "-m", "fixture");

        var error = await Assert.ThrowsAsync<SnapshotCaptureException>(() => SnapshotCapture.CaptureAsync(
            _repository.Root, "HEAD", [], SnapshotCaptureOptions.Default, CancellationToken.None));

        Assert.Contains("import", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("props")]
    [InlineData("targets")]
    public async Task AllowsPropsAndTargetsMsBuildThisFileDirectoryImports(string extension)
    {
        _repository.WriteText("App.csproj",
            "<Project Sdk=\"Microsoft.NET.Sdk\"><Import Project=\"build/Imported.targets\" /></Project>\n");
        _repository.WriteText("build/Imported.targets",
            $"<Project><Import Project=\"$(MSBuildThisFileDirectory)nested/Second.{extension}\" /></Project>\n");
        _repository.WriteText($"build/nested/Second.{extension}", "<Project />\n");
        _repository.Git("add", ".");
        _repository.Git("commit", "-m", "fixture");

        await using var snapshot = await SnapshotCapture.CaptureAsync(
            _repository.Root, "HEAD", [], SnapshotCaptureOptions.Default, CancellationToken.None);

        Assert.Contains(snapshot.Files,
            file => file.RelativePath == $"build/nested/Second.{extension}");
    }

    [Theory]
    [InlineData("build/Second.proj;build/First.props", false)]
    [InlineData("$(MSBuildThisFileDirectory)Second.proj;Other.targets", true)]
    public async Task RejectsUnsupportedExtensionHiddenInImportList(string import, bool nestedImport)
    {
        _repository.WriteText("App.csproj", nestedImport
            ? "<Project Sdk=\"Microsoft.NET.Sdk\"><Import Project=\"build/Imported.targets\" /></Project>\n"
            : $"<Project Sdk=\"Microsoft.NET.Sdk\"><Import Project=\"{import}\" /></Project>\n");
        _repository.WriteText("build/First.props", "<Project />\n");
        _repository.WriteText("build/Second.proj", "<Project />\n");
        _repository.WriteText("build/Other.targets", "<Project />\n");
        if (nestedImport)
            _repository.WriteText("build/Imported.targets", $"<Project><Import Project=\"{import}\" /></Project>\n");
        _repository.Git("add", ".");
        _repository.Git("commit", "-m", "fixture");

        var error = await Assert.ThrowsAsync<SnapshotCaptureException>(() => SnapshotCapture.CaptureAsync(
            _repository.Root, "HEAD", [], SnapshotCaptureOptions.Default, CancellationToken.None));

        Assert.Contains("extension", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RejectsRootedSegmentHiddenAfterSafeImport(bool useFileUri)
    {
        var rooted = Path.Combine(_repository.Root, "build", "Rooted.targets");
        var unsafeSegment = useFileUri ? "file:///outside/Rooted.targets" : rooted;
        _repository.WriteText("App.csproj",
            $"<Project Sdk=\"Microsoft.NET.Sdk\"><Import Project=\"build/Safe.props;{unsafeSegment}\" /></Project>\n");
        _repository.WriteText("build/Safe.props", "<Project />\n");
        _repository.WriteText("build/Rooted.targets", "<Project />\n");
        _repository.Git("add", ".");
        _repository.Git("commit", "-m", "fixture");

        var error = await Assert.ThrowsAsync<SnapshotCaptureException>(() => SnapshotCapture.CaptureAsync(
            _repository.Root, "HEAD", [], SnapshotCaptureOptions.Default, CancellationToken.None));

        Assert.Contains("rooted", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RejectsEscapingSegmentHiddenAfterSafeImport()
    {
        _repository.WriteText("App.csproj",
            "<Project Sdk=\"Microsoft.NET.Sdk\"><Import Project=\"build/Safe.props;../Outside.targets\" /></Project>\n");
        _repository.WriteText("build/Safe.props", "<Project />\n");
        _repository.Git("add", ".");
        _repository.Git("commit", "-m", "fixture");

        await Assert.ThrowsAsync<SnapshotCaptureException>(() => SnapshotCapture.CaptureAsync(
            _repository.Root, "HEAD", [], SnapshotCaptureOptions.Default, CancellationToken.None));
    }

    [Theory]
    [InlineData("build/Safe.props;;build/Other.targets")]
    [InlineData("build/Safe.props;$(DynamicImport)")]
    public async Task RejectsEmptyOrDynamicSegmentHiddenInImportList(string import)
    {
        _repository.WriteText("App.csproj",
            $"<Project Sdk=\"Microsoft.NET.Sdk\"><Import Project=\"{import}\" /></Project>\n");
        _repository.WriteText("build/Safe.props", "<Project />\n");
        _repository.WriteText("build/Other.targets", "<Project />\n");
        _repository.Git("add", ".");
        _repository.Git("commit", "-m", "fixture");

        await Assert.ThrowsAsync<SnapshotCaptureException>(() => SnapshotCapture.CaptureAsync(
            _repository.Root, "HEAD", [], SnapshotCaptureOptions.Default, CancellationToken.None));
    }

    [Theory]
    [InlineData("build/First.props")]
    [InlineData("build/First.props;build/Second.targets")]
    public async Task AllowsSingleAndMultipleCapturedPropsAndTargetsImports(string import)
    {
        _repository.WriteText("App.csproj",
            $"<Project Sdk=\"Microsoft.NET.Sdk\"><Import Project=\"{import}\" /></Project>\n");
        _repository.WriteText("build/First.props", "<Project />\n");
        _repository.WriteText("build/Second.targets", "<Project />\n");
        _repository.Git("add", ".");
        _repository.Git("commit", "-m", "fixture");

        await using var snapshot = await SnapshotCapture.CaptureAsync(
            _repository.Root, "HEAD", [], SnapshotCaptureOptions.Default, CancellationToken.None);

        Assert.Contains(snapshot.Files, file => file.RelativePath == "build/First.props");
    }

    [Fact]
    public async Task LogicalNonFilesystemValuesRemainSupported()
    {
        _repository.WriteText("App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><ContainerWorkingDirectory>/app</ContainerWorkingDirectory></PropertyGroup>
              <ItemGroup>
                <Content Include="asset.txt" PackagePath="/package/content" />
                <ContainerEnvironmentVariable Include="HOME" Value="/container/home" />
              </ItemGroup>
            </Project>
            """);
        _repository.WriteText("asset.txt", "captured\n");
        _repository.Git("add", ".");
        _repository.Git("commit", "-m", "fixture");

        await using var snapshot = await SnapshotCapture.CaptureAsync(
            _repository.Root, "HEAD", [], SnapshotCaptureOptions.Default, CancellationToken.None);

        Assert.Contains(snapshot.Files, file => file.RelativePath == "asset.txt");
    }

    [Theory]
    [InlineData("Properties", "CustomAfterMicrosoftCommonTargets=../outside/External.targets")]
    [InlineData("AdditionalProperties", "CustomAfterMicrosoftCommonTargets=../outside/External.targets")]
    [InlineData("RemoveProperties", "$(PropertiesToRemove)")]
    [InlineData("RemoveProperties", "CustomAfterMicrosoftCommonTargets")]
    public async Task RejectsUnsafeMsBuildTaskPropertyParameters(string parameter, string value)
    {
        _repository.WriteText("App.csproj", $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <Target Name="Nested"><MSBuild Projects="$(MSBuildProjectFullPath)"
                Targets="Build" {parameter}="{value}" /></Target>
            </Project>
            """);
        _repository.Git("add", ".");
        _repository.Git("commit", "-m", "fixture");

        var error = await Assert.ThrowsAsync<SnapshotCaptureException>(() => SnapshotCapture.CaptureAsync(
            _repository.Root, "HEAD", [], SnapshotCaptureOptions.Default, CancellationToken.None));

        Assert.Contains(parameter, error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("Properties", "Configuration=Release;Platform=AnyCPU")]
    [InlineData("AdditionalProperties", "TargetFramework=net10.0")]
    [InlineData("RemoveProperties", "Configuration;Platform")]
    public async Task AllowsOrdinaryStaticMsBuildTaskPropertyParameters(string parameter, string value)
    {
        _repository.WriteText("App.csproj", $"""
            <Project Sdk="Microsoft.NET.Sdk"><Target Name="Nested">
              <MSBuild Projects="$(MSBuildProjectFullPath)" Targets="Build" {parameter}="{value}" />
            </Target></Project>
            """);
        _repository.Git("add", ".");
        _repository.Git("commit", "-m", "fixture");

        await using var snapshot = await SnapshotCapture.CaptureAsync(
            _repository.Root, "HEAD", [], SnapshotCaptureOptions.Default, CancellationToken.None);

        Assert.Contains(snapshot.Files, file => file.RelativePath == "App.csproj");
    }

    [Theory]
    [InlineData("AdditionalProperties")]
    [InlineData("Properties")]
    [InlineData("SetConfiguration")]
    [InlineData("SetPlatform")]
    [InlineData("SetTargetFramework")]
    public async Task RejectsForwardingMetadataOnTargetScopedProjectEvaluationSinks(string metadataName)
    {
        _repository.WriteText("App.csproj", $"""
            <Project Sdk="Microsoft.NET.Sdk"><Target Name="Nested"><ItemGroup>
              <_AllProjects Include="tools/Helper.csproj"
                {metadataName}="CustomAfterMicrosoftCommonTargets=../outside/External.targets" />
            </ItemGroup></Target></Project>
            """);
        _repository.WriteText("tools/Helper.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />\n");
        _repository.Git("add", ".");
        _repository.Git("commit", "-m", "fixture");

        var error = await Assert.ThrowsAsync<SnapshotCaptureException>(() => SnapshotCapture.CaptureAsync(
            _repository.Root, "HEAD", [], SnapshotCaptureOptions.Default, CancellationToken.None));

        Assert.Contains(metadataName, error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("ItemName", "_ProjectToTestWithTFM")]
    [InlineData("PropertyName", "AdditionalProjects")]
    [InlineData("PropertyName", "CustomAfterMicrosoftCommonTargets")]
    [InlineData("ItemName", "$(GeneratedItemName)")]
    [InlineData("PropertyName", "$(GeneratedPropertyName)")]
    public async Task RejectsTaskOutputsThatPopulateProjectEvaluationInputs(string outputKind, string outputName)
    {
        _repository.WriteText("App.csproj", $"""
            <Project Sdk="Microsoft.NET.Sdk"><Target Name="Nested">
              <CreateItem Include="tools/Helper.csproj">
                <Output TaskParameter="Include" {outputKind}="{outputName}" />
              </CreateItem>
            </Target></Project>
            """);
        _repository.WriteText("tools/Helper.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />\n");
        _repository.Git("add", ".");
        _repository.Git("commit", "-m", "fixture");

        var error = await Assert.ThrowsAsync<SnapshotCaptureException>(() => SnapshotCapture.CaptureAsync(
            _repository.Root, "HEAD", [], SnapshotCaptureOptions.Default, CancellationToken.None));

        Assert.Contains(outputName.Contains("$(", StringComparison.Ordinal) ? outputKind : outputName,
            error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AllowsTaskOutputsUnrelatedToProjectEvaluationInputs()
    {
        _repository.WriteText("App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk"><Target Name="Nested">
              <CreateProperty Value="ordinary">
                <Output TaskParameter="Value" PropertyName="GeneratedValue" />
              </CreateProperty>
            </Target></Project>
            """);
        _repository.Git("add", ".");
        _repository.Git("commit", "-m", "fixture");

        await using var snapshot = await SnapshotCapture.CaptureAsync(
            _repository.Root, "HEAD", [], SnapshotCaptureOptions.Default, CancellationToken.None);

        Assert.Contains(snapshot.Files, file => file.RelativePath == "App.csproj");
    }

    [Fact]
    public async Task PropertyNamedOutputInsideTargetReceivesOrdinaryPropertyValidation()
    {
        _repository.WriteText("App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk"><Target Name="Nested"><PropertyGroup>
              <Output>../outside/value.txt</Output>
            </PropertyGroup></Target></Project>
            """);
        _repository.Git("add", ".");
        _repository.Git("commit", "-m", "fixture");

        await Assert.ThrowsAsync<SnapshotCaptureException>(() => SnapshotCapture.CaptureAsync(
            _repository.Root, "HEAD", [], SnapshotCaptureOptions.Default, CancellationToken.None));
    }

    [Fact]
    public async Task TargetScopedItemNamedOutputRetainsOrdinaryGeneratedItemBehavior()
    {
        _repository.WriteText("App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk"><Target Name="Nested"><ItemGroup>
              <Output Include="$(GeneratedAtBuildTime)" />
            </ItemGroup></Target></Project>
            """);
        _repository.Git("add", ".");
        _repository.Git("commit", "-m", "fixture");

        await using var snapshot = await SnapshotCapture.CaptureAsync(
            _repository.Root, "HEAD", [], SnapshotCaptureOptions.Default, CancellationToken.None);

        Assert.Contains(snapshot.Files, file => file.RelativePath == "App.csproj");
    }

    [Theory]
    [InlineData("Import")]
    [InlineData("ProjectReference")]
    [InlineData("MSBuild")]
    [InlineData("HintPath")]
    public async Task RejectsRootedBuildInputsEvenWhenTheyAreInsideTheOriginalRepository(string surface)
    {
        var imported = Path.Combine(_repository.Root, "build", "Internal.targets");
        var referenced = Path.Combine(_repository.Root, "tools", "Helper.csproj");
        var assembly = Path.Combine(_repository.Root, "lib", "Helper.dll");
        var fragment = surface switch
        {
            "Import" => $"<Import Project=\"{imported}\" />",
            "ProjectReference" => $"<ItemGroup><ProjectReference Include=\"{referenced}\" /></ItemGroup>",
            "MSBuild" => $"<Target Name=\"Nested\"><MSBuild Projects=\"{referenced}\" /></Target>",
            _ => $"<ItemGroup><Reference Include=\"Helper\"><HintPath>{assembly}</HintPath></Reference></ItemGroup>"
        };
        _repository.WriteText("App.csproj", $"<Project Sdk=\"Microsoft.NET.Sdk\">{fragment}</Project>\n");
        _repository.WriteText("build/Internal.targets", "<Project />\n");
        _repository.WriteText("tools/Helper.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />\n");
        _repository.Write("lib/Helper.dll", [0]);
        _repository.Git("add", ".");
        _repository.Git("commit", "-m", "fixture");

        var error = await Assert.ThrowsAsync<SnapshotCaptureException>(() => SnapshotCapture.CaptureAsync(
            _repository.Root, "HEAD", [], SnapshotCaptureOptions.Default, CancellationToken.None));

        Assert.Contains("rooted", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("Import", "../outside/External.targets")]
    [InlineData("UsingTask", "../outside/External.Tasks.dll")]
    [InlineData("MSBuild", "../outside/External.dll")]
    public async Task PropertyNamesThatMatchBuildSyntaxStillReceiveNormalPropertyValidation(
        string propertyName, string value)
    {
        _repository.WriteText("App.csproj", $"""
            <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup>
              <{propertyName}>{value}</{propertyName}>
            </PropertyGroup></Project>
            """);
        _repository.Git("add", ".");
        _repository.Git("commit", "-m", "fixture");

        await Assert.ThrowsAsync<SnapshotCaptureException>(() => SnapshotCapture.CaptureAsync(
            _repository.Root, "HEAD", [], SnapshotCaptureOptions.Default, CancellationToken.None));
    }

    [Theory]
    [InlineData("Import")]
    [InlineData("UsingTask")]
    [InlineData("MSBuild")]
    [InlineData("HintPath")]
    [InlineData("AssemblyOriginatorKeyFile")]
    [InlineData("CodeAnalysisRuleSet")]
    [InlineData("ApplicationIcon")]
    [InlineData("ApplicationManifest")]
    [InlineData("Win32Resource")]
    [InlineData("RunSettingsFilePath")]
    public async Task ItemNamesThatMatchBuildSyntaxStillReceiveNormalItemValidation(string itemName)
    {
        _repository.WriteText("App.csproj", $"""
            <Project Sdk="Microsoft.NET.Sdk"><ItemGroup>
              <{itemName} Include="../outside/External.dll" />
            </ItemGroup></Project>
            """);
        _repository.Git("add", ".");
        _repository.Git("commit", "-m", "fixture");

        await Assert.ThrowsAsync<SnapshotCaptureException>(() => SnapshotCapture.CaptureAsync(
            _repository.Root, "HEAD", [], SnapshotCaptureOptions.Default, CancellationToken.None));
    }

    [Fact]
    public async Task SameNamedHintPathItemStillValidatesOrdinaryMetadata()
    {
        _repository.WriteText("App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk"><ItemGroup>
              <HintPath Include="asset.txt" Destination="../outside/value.txt" />
            </ItemGroup></Project>
            """);
        _repository.WriteText("asset.txt", "captured\n");
        _repository.Git("add", ".");
        _repository.Git("commit", "-m", "fixture");

        await Assert.ThrowsAsync<SnapshotCaptureException>(() => SnapshotCapture.CaptureAsync(
            _repository.Root, "HEAD", [], SnapshotCaptureOptions.Default, CancellationToken.None));
    }

    [Theory]
    [InlineData("HintPath")]
    [InlineData("ApplicationManifest")]
    [InlineData("Analyzer")]
    [InlineData("Compile")]
    [InlineData("ProjectReference")]
    [InlineData("PipelineProject")]
    [InlineData("UsingTask")]
    [InlineData("CodeSource")]
    [InlineData("TaskReference")]
    [InlineData("Import")]
    [InlineData("MSBuild")]
    public async Task AllowsStaticMsBuildThisFileDirectorySuffixInImportedBuildFiles(string surface)
    {
        var declaration = surface switch
        {
            "HintPath" => "<ItemGroup><Reference Include=\"Helper\" HintPath=\"$(MSBuildThisFileDirectory)../lib/Helper.dll\" /></ItemGroup>",
            "ApplicationManifest" => "<PropertyGroup><ApplicationManifest>$(MSBuildThisFileDirectory)../assets/app.manifest</ApplicationManifest></PropertyGroup>",
            "Analyzer" => "<ItemGroup><Analyzer Include=\"$(MSBuildThisFileDirectory)../lib/Helper.dll\" /></ItemGroup>",
            "Compile" => "<ItemGroup><Compile Include=\"$(MSBuildThisFileDirectory)../src/Extra.cs\" /></ItemGroup>",
            "ProjectReference" => "<ItemGroup><ProjectReference Include=\"$(MSBuildThisFileDirectory)../tools/Helper.csproj\" /></ItemGroup>",
            "PipelineProject" => "<ItemGroup><_AllProjects Include=\"$(MSBuildThisFileDirectory)../tools/Helper.csproj\" /></ItemGroup>",
            "UsingTask" => "<UsingTask TaskName=\"Helper\" AssemblyFile=\"$(MSBuildThisFileDirectory)../lib/Helper.dll\" />",
            "CodeSource" => "<UsingTask TaskName=\"Helper\" TaskFactory=\"RoslynCodeTaskFactory\"><Task><Code Source=\"$(MSBuildThisFileDirectory)../src/Task.cs\" /></Task></UsingTask>",
            "TaskReference" => "<UsingTask TaskName=\"Helper\" TaskFactory=\"RoslynCodeTaskFactory\"><Task><Reference Include=\"$(MSBuildThisFileDirectory)../lib/Helper.dll\" /></Task></UsingTask>",
            "Import" => "<Import Project=\"$(MSBuildThisFileDirectory)nested/Second.targets\" />",
            _ => "<Target Name=\"Nested\"><MSBuild Projects=\"$(MSBuildThisFileDirectory)../tools/Helper.csproj\" /></Target>"
        };
        _repository.WriteText("App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><Import Project=\"build/Imported.targets\" /></Project>\n");
        _repository.WriteText("build/Imported.targets", $"<Project>{declaration}</Project>\n");
        _repository.WriteText("build/nested/Second.targets", "<Project />\n");
        _repository.Write("lib/Helper.dll", [0]);
        _repository.WriteText("assets/app.manifest", "<assembly />\n");
        _repository.WriteText("src/Extra.cs", "public sealed class Extra;\n");
        _repository.WriteText("src/Task.cs", "public sealed class TaskSource;\n");
        _repository.WriteText("tools/Helper.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />\n");
        _repository.Git("add", ".");
        _repository.Git("commit", "-m", "fixture");

        await using var snapshot = await SnapshotCapture.CaptureAsync(
            _repository.Root, "HEAD", [], SnapshotCaptureOptions.Default, CancellationToken.None);

        Assert.Contains(snapshot.Files, file => file.RelativePath == "build/Imported.targets");
    }

    [Theory]
    [InlineData("$(MSBuildThisFileDirectory)../../outside/Helper.dll")]
    [InlineData("$(MSBuildThisFileDirectory)$(HelperPath)")]
    [InlineData("$(OtherDirectory)../lib/Helper.dll")]
    public async Task RefusesEscapingOrDynamicImportedBuildFileDirectoryForms(string value)
    {
        _repository.WriteText("App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><Import Project=\"build/Imported.props\" /></Project>\n");
        _repository.WriteText("build/Imported.props", $"<Project><ItemGroup><Analyzer Include=\"{value}\" /></ItemGroup></Project>\n");
        _repository.Write("lib/Helper.dll", [0]);
        _repository.Git("add", ".");
        _repository.Git("commit", "-m", "fixture");

        await Assert.ThrowsAsync<SnapshotCaptureException>(() => SnapshotCapture.CaptureAsync(
            _repository.Root, "HEAD", [], SnapshotCaptureOptions.Default, CancellationToken.None));
    }

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int Open(byte[] pathname, int flags, int mode);

    [DllImport("libc", EntryPoint = "close", SetLastError = true)]
    private static extern int Close(int descriptor);

    public void Dispose() => _repository.Dispose();
}

internal sealed class SnapshotTestRepository : IDisposable
{
    public string Root { get; }

    public SnapshotTestRepository(string? root = null)
    {
        Root = root ?? Path.Combine(Path.GetTempPath(), "mutate4csharp snapshot fixtures", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
        Git("init");
        Git("config", "user.email", "fixture@example.invalid");
        Git("config", "user.name", "Fixture");
    }

    public void WriteText(string relative, string value, bool utf8Bom = false) =>
        Write(relative, new UTF8Encoding(utf8Bom).GetBytes(value));

    public void Write(string relative, byte[] bytes)
    {
        var path = Path.Combine(Root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
    }

    public byte[] Read(string relative) => File.ReadAllBytes(Path.Combine(Root, relative));

    public string Git(params string[] arguments)
    {
        var info = new ProcessStartInfo("git") { WorkingDirectory = Root, RedirectStandardOutput = true,
            RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0) throw new InvalidOperationException(error);
        return output;
    }

    public void Dispose() { try { Directory.Delete(Root, true); } catch { } }
}
