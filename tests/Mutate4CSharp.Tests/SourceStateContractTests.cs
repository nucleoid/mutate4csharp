using System.Diagnostics;

namespace Mutate4CSharp.Tests;

public sealed class SourceStateContractTests
{
    [Fact]
    public async Task SourceStateDigestBindsBytesIndexFlagsAndStatusWithoutConsumerPythonImports()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The shipped gate profile is Linux Bash.");
        using var repository = new SnapshotTestRepository();
        repository.WriteText("A.cs", "class A {}\n");
        repository.Git("add", ".");
        repository.Git("commit", "-m", "source-state baseline");
        var script = Path.GetFullPath("../../../../../scripts/source-state.py", AppContext.BaseDirectory);
        async Task<string> Capture(string mode = "no-state")
        {
            var info = new ProcessStartInfo("python3") { WorkingDirectory = repository.Root,
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var value in new[] { "-I", script, repository.Root, mode }) info.ArgumentList.Add(value);
            using var process = Process.Start(info)!;
            var output = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
            var error = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
            await process.WaitForExitAsync(TestContext.Current.CancellationToken);
            Assert.True(process.ExitCode == 0, await error);
            return (await output).Trim();
        }
        var original = await Capture();
        Assert.Equal(original, await Capture());
        repository.WriteText("A.cs", "class Changed {}\n");
        Assert.NotEqual(original, await Capture());
        repository.Git("checkout", "--", "A.cs");
        repository.Git("update-index", "--assume-unchanged", "A.cs");
        Assert.NotEqual(original, await Capture());
        repository.Git("update-index", "--no-assume-unchanged", "A.cs");
        Assert.Equal(original, await Capture());
        repository.WriteText("json.py", "raise RuntimeError('consumer import must not execute')\n");
        var withModule = await Capture();
        Assert.NotEqual(original, withModule);
        Assert.Equal(withModule, await Capture());
        repository.WriteText(".mutate4csharp/.gitignore", "*\n");
        var defaultState = await Capture("default-state");
        var noState = await Capture();
        repository.WriteText(".mutate4csharp/discovery/example.json", "{}\n");
        Assert.Equal(defaultState, await Capture("default-state"));
        Assert.NotEqual(noState, await Capture());
    }
}
