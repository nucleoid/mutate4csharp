namespace Mutate4CSharp.Tests;

public sealed class IssueFiveWindowsCiRegressionTests
{
    [Fact]
    public async Task LinuxIsolationRequestOnNonLinuxUsesNormalProcessResultAndCleanup()
    {
        var executable = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh";
        string[] arguments = OperatingSystem.IsWindows()
            ? ["/d", "/s", "/c", "(echo normal-output)&(echo normal-error 1>&2)&exit /b 23"]
            : ["-c", "printf normal-output; printf normal-error >&2; exit 23"];

        var result = await ProcessTree.RunAsync(new ProcessPlatformCapabilities(IsLinux: false),
            executable, arguments, AppContext.BaseDirectory, TimeSpan.FromSeconds(30),
            TestContext.Current.CancellationToken, requireLinuxSessionIsolation: true);

        Assert.Equal(23, result.ExitCode);
        Assert.Equal("normal-output", result.StandardOutput.Trim());
        Assert.Equal("normal-error", result.StandardError.Trim());
        Assert.False(result.TimedOut);
    }
}
