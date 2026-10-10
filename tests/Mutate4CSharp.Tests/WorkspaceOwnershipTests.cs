using System.Diagnostics;

namespace Mutate4CSharp.Tests;

public sealed class WorkspaceOwnershipTests
{
    [Fact]
    public async Task PrivateScratchNamesStayCompactAndRetainFullOwnershipTokens()
    {
        var parent = OwnedDirectory.PrivateParent("package-caches");
        await using var owned = OwnedDirectory.Create(parent, new string('x', 100));
        var name = Path.GetFileName(owned.Root);
        Assert.True(name.Length <= 12 + 1 + 32);
        Assert.True(Guid.TryParseExact(name[(name.LastIndexOf('-') + 1)..], "N", out _));
        Assert.True(Guid.TryParseExact(File.ReadAllText(Path.Combine(owned.Root, ".mutate4csharp-owner")),
            "N", out _));
        Assert.Equal("p", Path.GetFileName(parent));
    }

    [Fact]
    public async Task OwnedCleanupLeavesUnrelatedTemporaryPathsAlone()
    {
        var parent = Path.Combine(Path.GetTempPath(), "mutate4csharp-ownership", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(parent);
        var unrelated = Path.Combine(parent, "unrelated.txt");
        File.WriteAllText(unrelated, "keep");
        var owned = OwnedDirectory.Create(parent, "worker");
        File.WriteAllText(Path.Combine(owned.Root, "owned.txt"), "delete");

        await owned.DisposeAsync();

        Assert.True(File.Exists(unrelated));
        Assert.False(Directory.Exists(owned.Root));
        Directory.Delete(parent, true);
    }

    [Fact]
    public async Task ProcessRootCleanupPreservesForeignAndActivePaths()
    {
        var foreignParent = OwnedDirectory.PrivateParent("foreign-preservation");
        var foreign = Path.Combine(foreignParent, "foreign.txt");
        Directory.CreateDirectory(foreignParent);
        File.WriteAllText(foreign, "keep");
        var first = OwnedDirectory.Create(foreignParent, "first");
        var active = OwnedDirectory.Create(OwnedDirectory.PrivateParent("active-preservation"), "second");

        await first.DisposeAsync();
        Assert.True(File.Exists(foreign));
        Assert.True(Directory.Exists(active.Root));

        File.Delete(foreign);
        await active.DisposeAsync();
        var cleanup = OwnedDirectory.Create(foreignParent, "cleanup");
        await cleanup.DisposeAsync();
        Assert.False(Directory.Exists(foreignParent));
    }

    [Fact]
    public async Task OwnedDirectoryHonorsPrivateParentAndIgnoresUnreachableAncestorConfiguration()
    {
        var outer = Path.Combine(Path.GetTempPath(), "mutate4csharp-private-parent", Guid.NewGuid().ToString("N"));
        var parent = Path.Combine(outer, "private", "workspaces");
        Directory.CreateDirectory(outer);
        File.WriteAllText(Path.Combine(outer, "global.json"), "not reachable through the private boundary");
        var owned = OwnedDirectory.Create(parent, "worker");
        var root = Path.Combine(owned.Root, "root");
        Directory.CreateDirectory(root);
        try
        {
            Assert.Equal(Path.GetFullPath(parent), Path.GetDirectoryName(owned.Root));
            if (!OperatingSystem.IsWindows())
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                    File.GetUnixFileMode(parent));
            await ExecutionEnvironment.CreateExecutionBoundaryAsync(owned.Root, Environment.CurrentDirectory,
                CancellationToken.None);
            ExecutionEnvironment.ValidateExecutionAncestors(root);
        }
        finally
        {
            await owned.DisposeAsync();
            Directory.Delete(outer, true);
        }
    }

    [Fact]
    public async Task TimeoutAwaitsChildAndGrandchildProcessTreeTermination()
    {
        if (OperatingSystem.IsWindows()) return;
        var directory = Path.Combine(Path.GetTempPath(), "mutate4csharp-process-tree", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var script = "sh -c 'sleep 60 & echo $! > grandchild.pid; wait' & echo $! > child.pid; wait";

        var result = await ProcessTree.RunAsync("/bin/sh", ["-c", script], directory,
            TimeSpan.FromMilliseconds(500), CancellationToken.None);

        Assert.True(result.TimedOut);
        foreach (var file in new[] { "child.pid", "grandchild.pid" })
        {
            var pid = int.Parse(File.ReadAllText(Path.Combine(directory, file)).Trim());
            AssertLinuxProcessTerminated(pid, file);
        }
        Directory.Delete(directory, true);
    }

    [Fact]
    public async Task TestTimeoutAwaitsChildAndGrandchildProcessTreeTermination()
    {
        if (OperatingSystem.IsWindows()) return;
        var directory = Path.Combine(Path.GetTempPath(), "mutate4csharp-test-process-tree", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var executable = Path.Combine(directory, "fake-dotnet");
        await File.WriteAllTextAsync(executable,
            "#!/bin/sh\nsh -c 'sleep 60 & echo $! > grandchild.pid; wait' & echo $! > child.pid\nwait\n",
            TestContext.Current.CancellationToken);
        File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var results = Path.Combine(directory, "results");

        var result = await TestRunner.RunAsync(directory, "ignored.csproj", results,
            TimeSpan.FromMilliseconds(500), false, TestContext.Current.CancellationToken,
            dotnetExecutable: executable);

        Assert.True(result.TimedOut);
        foreach (var file in new[] { "child.pid", "grandchild.pid" })
        {
            var pid = int.Parse(File.ReadAllText(Path.Combine(directory, file)).Trim());
            AssertLinuxProcessTerminated(pid, file);
        }
        Directory.Delete(directory, true);
    }

    [Fact]
    public async Task LegacyTestRunnerDoesNotInjectSnapshotIsolationArguments()
    {
        if (OperatingSystem.IsWindows()) return;
        var directory = Path.Combine(Path.GetTempPath(), "mutate4csharp-legacy-test-runner", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var executable = Path.Combine(directory, "fake-dotnet");
        await File.WriteAllTextAsync(executable, "#!/bin/sh\nprintf '%s\\n' \"$@\" > arguments.txt\n",
            TestContext.Current.CancellationToken);
        File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        await TestRunner.RunAsync(directory, "legacy.csproj", Path.Combine(directory, "results"),
            TimeSpan.FromSeconds(5), false, TestContext.Current.CancellationToken, dotnetExecutable: executable);

        var arguments = await File.ReadAllLinesAsync(Path.Combine(directory, "arguments.txt"),
            TestContext.Current.CancellationToken);
        Assert.DoesNotContain("-noAutoResponse", arguments);
        Assert.DoesNotContain("-nodeReuse:false", arguments);
        Assert.DoesNotContain("-p:UseSharedCompilation=false", arguments);
        Directory.Delete(directory, true);
    }

    [Fact]
    public async Task ProcessOutputIsBounded()
    {
        if (OperatingSystem.IsWindows()) return;
        var directory = Path.Combine(Path.GetTempPath(), "mutate4csharp-process-output", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        var error = await Assert.ThrowsAsync<IOException>(() => ProcessTree.RunAsync("/bin/sh",
            ["-c", "head -c 1024 /dev/zero"], directory, TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken, maxOutputBytes: 32));

        Assert.Contains("output exceeded", error.Message, StringComparison.OrdinalIgnoreCase);
        Directory.Delete(directory, true);
    }

    [Fact]
    public async Task SnapshotTestRunnerPreservesRepositoryAutoResponseDiscovery()
    {
        if (OperatingSystem.IsWindows()) return;
        using var repository = new SnapshotTestRepository();
        repository.WriteText("App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />\n");
        repository.Git("add", ".");
        repository.Git("commit", "-m", "fixture");
        await using var snapshot = await SnapshotCapture.CaptureAsync(repository.Root, "HEAD", [],
            SnapshotCaptureOptions.Default, CancellationToken.None);
        await using var clone = await SnapshotWorkspace.CreateCloneAsync(snapshot, "response-file", CancellationToken.None);
        var executable = Path.Combine(clone.Root, "fake-dotnet");
        await File.WriteAllTextAsync(executable, "#!/bin/sh\nprintf '%s\\n' \"$@\" > arguments.txt\n",
            TestContext.Current.CancellationToken);
        File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var packageRoot = Path.Combine(clone.OwnedRoot, "packages");
        await TestRunner.RunAsync(clone.Root, "App.csproj", Path.Combine(clone.Root, "results"),
            TimeSpan.FromSeconds(5), false, TestContext.Current.CancellationToken,
            ExecutionEnvironment.ProcessEnvironment(packageRoot),
            requireExecutionBoundary: true, dotnetExecutable: executable);

        var arguments = await File.ReadAllLinesAsync(Path.Combine(clone.Root, "arguments.txt"),
            TestContext.Current.CancellationToken);
        Assert.DoesNotContain("-noAutoResponse", arguments);
        Assert.Contains("-nodeReuse:false", arguments);
        Assert.Contains("-p:UseSharedCompilation=false", arguments);
        Assert.Contains($"-p:NuGetPackageRoot={packageRoot}{Path.DirectorySeparatorChar}", arguments);
    }

    [Fact]
    public async Task SnapshotTestRunnerClearsSdkResolverEnvironment()
    {
        if (OperatingSystem.IsWindows()) return;
        using var repository = new SnapshotTestRepository();
        repository.WriteText("App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />\n");
        repository.Git("add", ".");
        repository.Git("commit", "-m", "fixture");
        await using var snapshot = await SnapshotCapture.CaptureAsync(repository.Root, "HEAD", [],
            SnapshotCaptureOptions.Default, CancellationToken.None);
        await using var clone = await SnapshotWorkspace.CreateCloneAsync(snapshot, "resolver-environment",
            CancellationToken.None);
        var executable = Path.Combine(clone.Root, "fake-dotnet");
        await File.WriteAllTextAsync(executable, """
            #!/bin/sh
            printf '%s\n' "${MSBuildSDKsPath-unset}" \
              "${DOTNET_MSBUILD_SDK_RESOLVER_SDKS_DIR-unset}" \
              "${DOTNET_MSBUILD_SDK_RESOLVER_SDKS_VER-unset}" \
              "${DOTNET_MSBUILD_SDK_RESOLVER_CLI_DIR-unset}" \
              "${MSBUILDADDITIONALSDKRESOLVERSFOLDER-unset}" > environment.txt
            """, TestContext.Current.CancellationToken);
        File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var packageRoot = Path.Combine(clone.OwnedRoot, "packages");
        var environment = new Dictionary<string, string?>
        {
            ["NUGET_PACKAGES"] = packageRoot,
            ["MSBuildSDKsPath"] = "/outside/sdk",
            ["DOTNET_MSBUILD_SDK_RESOLVER_SDKS_DIR"] = "/outside/resolver",
            ["DOTNET_MSBUILD_SDK_RESOLVER_SDKS_VER"] = "outside-version",
            ["DOTNET_MSBUILD_SDK_RESOLVER_CLI_DIR"] = "/outside/cli",
            ["MSBUILDADDITIONALSDKRESOLVERSFOLDER"] = "/outside/additional"
        };

        await TestRunner.RunAsync(clone.Root, "App.csproj", Path.Combine(clone.Root, "results"),
            TimeSpan.FromSeconds(5), false, TestContext.Current.CancellationToken, environment,
            requireExecutionBoundary: true, dotnetExecutable: executable);

        Assert.Equal(["unset", "unset", "unset", "unset", "unset"], await File.ReadAllLinesAsync(
            Path.Combine(clone.Root, "environment.txt"), TestContext.Current.CancellationToken));
    }

    private static void AssertLinuxProcessTerminated(int pid, string source)
    {
        var statPath = $"/proc/{pid}/stat";
        if (!File.Exists(statPath)) return;
        var stat = File.ReadAllText(statPath);
        var commandEnd = stat.LastIndexOf(')');
        var state = commandEnd >= 0 && commandEnd + 2 < stat.Length ? stat[commandEnd + 2] : '?';
        Assert.True(state == 'Z', $"Process {pid} from {source} is still live (state {state}).");
    }

    [Fact]
    public async Task OwnedDirectoryIsPrivateAndRefusesMarkerMismatch()
    {
        var parent = OwnedDirectory.PrivateParent("permission-tests");
        var owned = OwnedDirectory.Create(parent, "permissions");
        if (!OperatingSystem.IsWindows())
        {
            var mode = File.GetUnixFileMode(owned.Root);
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, mode);
        }
        File.WriteAllText(Path.Combine(owned.Root, ".mutate4csharp-owner"), "wrong-owner");
        await Assert.ThrowsAsync<SnapshotCleanupException>(async () => await owned.DisposeAsync());
        Directory.Delete(owned.Root, true);
        var cleanup = OwnedDirectory.Create(parent, "cleanup");
        await cleanup.DisposeAsync();
        Assert.False(Directory.Exists(parent));
    }

    [Fact]
    public async Task SnapshotCloneCreatesAndValidatesStructuralExecutionBoundary()
    {
        using var repository = new SnapshotTestRepository();
        repository.WriteText("A.cs", "class A { }\n");
        repository.Git("add", ".");
        repository.Git("commit", "-m", "fixture");
        await using var snapshot = await SnapshotCapture.CaptureAsync(repository.Root, "HEAD", [],
            SnapshotCaptureOptions.Default, CancellationToken.None);
        await using var clone = await SnapshotWorkspace.CreateCloneAsync(snapshot, "boundary", CancellationToken.None);

        ExecutionEnvironment.ValidateExecutionBoundary(clone.Root);
        File.Delete(Path.Combine(clone.OwnedRoot, "Directory.Build.targets"));
        Assert.Throws<ExecutionBoundaryIntegrityException>(() =>
            ExecutionEnvironment.ValidateExecutionBoundary(clone.Root));
    }

    [Fact]
    public async Task ExecutionBoundaryRejectsGlobalConfigAbovePrivateParent()
    {
        var outer = Path.Combine(Path.GetTempPath(), "mutate4csharp-globalconfig-boundary", Guid.NewGuid().ToString("N"));
        var parent = Path.Combine(outer, "private", "workspaces");
        Directory.CreateDirectory(outer);
        var owned = OwnedDirectory.Create(parent, "worker");
        var root = Path.Combine(owned.Root, "root");
        Directory.CreateDirectory(root);
        try
        {
            await ExecutionEnvironment.CreateExecutionBoundaryAsync(owned.Root, Environment.CurrentDirectory,
                CancellationToken.None);
            File.WriteAllText(Path.Combine(outer, ".globalconfig"), "is_global = true\n");

            var error = Assert.Throws<ExecutionBoundaryIntegrityException>(() =>
                ExecutionEnvironment.ValidateExecutionBoundary(root));
            Assert.Contains(".globalconfig", error.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await owned.DisposeAsync();
            Directory.Delete(outer, true);
        }
    }

    [Fact]
    public async Task CloneFailurePreservesPrimaryAndCleanupFailures()
    {
        using var repository = new SnapshotTestRepository();
        repository.WriteText("A.cs", "class A { }\n");
        repository.Git("add", ".");
        repository.Git("commit", "-m", "fixture");
        await using var snapshot = await SnapshotCapture.CaptureAsync(repository.Root, "HEAD", [],
            SnapshotCaptureOptions.Default, CancellationToken.None);
        File.WriteAllText(Path.Combine(snapshot.CaptureRoot, "A.cs"), "class Tampered { }\n");
        var parent = OwnedDirectory.PrivateParent("workspaces");
        var before = Directory.Exists(parent)
            ? Directory.EnumerateDirectories(parent).ToHashSet(StringComparer.Ordinal)
            : [];
        string? damagedRoot = null;
        string? marker = null;
        var cloneTask = SnapshotWorkspace.CreateCloneAsync(snapshot, "dual-failure", CancellationToken.None);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            while (damagedRoot is null)
            {
                deadline.Token.ThrowIfCancellationRequested();
                damagedRoot = Directory.Exists(parent)
                    ? Directory.EnumerateDirectories(parent).FirstOrDefault(path => !before.Contains(path))
                    : null;
                if (damagedRoot is null) await Task.Delay(1, deadline.Token);
            }
            var markerPath = Path.Combine(damagedRoot, ".mutate4csharp-owner");
            while (!File.Exists(markerPath)) await Task.Delay(1, deadline.Token);
            marker = File.ReadAllText(markerPath);
            File.WriteAllText(markerPath, "wrong-owner");

            var error = await Assert.ThrowsAnyAsync<Exception>(() => cloneTask);
            Assert.Contains("Captured bytes changed", error.ToString(), StringComparison.OrdinalIgnoreCase);
            Assert.Contains("ownership marker", error.ToString(), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (damagedRoot is not null && Directory.Exists(damagedRoot))
            {
                if (marker is not null) File.WriteAllText(Path.Combine(damagedRoot, ".mutate4csharp-owner"), marker);
                Directory.Delete(damagedRoot, true);
            }
        }
    }
}
