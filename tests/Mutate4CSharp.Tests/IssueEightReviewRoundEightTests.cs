using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Mutate4CSharp.Tests;

public sealed class IssueEightReviewRoundEightTests : IDisposable
{
    private const int OrchestrationRefusal = 73;
    private static readonly string RepositoryRoot = Path.GetFullPath("../../../../../", AppContext.BaseDirectory);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mutate4csharp-round-eight", Guid.NewGuid().ToString("N"));

    public IssueEightReviewRoundEightTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void EveryBashLaunchingTestExplicitlySkipsWindows()
    {
        foreach (var path in Directory.EnumerateFiles(Path.Combine(RepositoryRoot, "tests"), "*.cs", SearchOption.AllDirectories))
        {
            var source = File.ReadAllText(path);
            var root = CSharpSyntaxTree.ParseText(source, cancellationToken: TestContext.Current.CancellationToken)
                .GetRoot(TestContext.Current.CancellationToken);
            foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
            {
                var attributes = method.AttributeLists.SelectMany(list => list.Attributes)
                    .Select(attribute => attribute.Name.ToString()).ToArray();
                if (!attributes.Any(name => name is "Fact" or "Theory" or "FactAttribute" or "TheoryAttribute") ||
                    !MethodLaunchesBash(root, method)) continue;
                var launch = FirstBashLaunch(root, method);
                var skip = method.DescendantNodes().OfType<InvocationExpressionSyntax>().FirstOrDefault(invocation =>
                    invocation.Expression.ToString() == "Assert.SkipWhen" &&
                    invocation.ArgumentList.Arguments.FirstOrDefault()?.Expression.ToString() == "OperatingSystem.IsWindows()");
                Assert.True(skip is not null && skip.SpanStart < launch,
                    $"{path}: {method.Identifier.ValueText} must call Assert.SkipWhen(OperatingSystem.IsWindows(), ...) before its first Bash/Unix-only launch.");
            }
        }
    }

    [Theory]
    [InlineData("[Theory] public async Task T() { Assert.SkipWhen(OperatingSystem.IsWindows(), \"bash\"); await RunAsync(\"x\", \"bash\", \"a\"); }")]
    [InlineData("[Fact] public async Task T() { Assert.SkipWhen(OperatingSystem.IsWindows(), \"bash\"); await RunAsync(\"x\", \"/bin/bash\", \"a\"); }")]
    [InlineData("[Fact] public async Task T() { const string Shell = \"/bin/bash\"; Assert.SkipWhen(OperatingSystem.IsWindows(), \"bash\"); await RunAsync(\"x\", Shell, \"a\"); }")]
    [InlineData("[Fact] public void T() { Assert.SkipWhen(OperatingSystem.IsWindows(), \"bash\"); Process.Start(new ProcessStartInfo(\"bash\")); }")]
    [InlineData("[Fact] public void T() { Assert.SkipWhen(OperatingSystem.IsWindows(), \"bash\"); var p = new ProcessStartInfo { FileName = \"bash\" }; Process.Start(p); }")]
    [InlineData("[Fact] public async Task T() { Assert.SkipWhen(OperatingSystem.IsWindows(), \"bash\"); await RunBashAsync(); } private async Task RunBashAsync() { await RunAsync(\"x\", \"/bin/bash\"); }")]
    [InlineData("[Fact] public async Task T() { Assert.SkipWhen(OperatingSystem.IsWindows(), \"bash\"); await RunScriptAsync(); } private async Task RunScriptAsync() { var text = \"#!/usr/bin/env bash\"; await RunAsync(\"x\", \"./script\"); }")]
    [InlineData("[Fact] public async Task T() { Assert.SkipWhen(OperatingSystem.IsWindows(), \"bash\"); await WriteExecutableAsync(\"x\", \"y\"); }")]
    [InlineData("[Fact] public void T() { Assert.SkipWhen(OperatingSystem.IsWindows(), \"bash\"); _ = PayloadSha256(\"x\"); }")]
    public void BashLaunchDetectionBindsTheoryAndAlternateLaunchForms(string member)
    {
        var root = CSharpSyntaxTree.ParseText("class C { " + member + " }",
                cancellationToken: TestContext.Current.CancellationToken)
            .GetRoot(TestContext.Current.CancellationToken);
        var method = root.DescendantNodes().OfType<MethodDeclarationSyntax>().First();
        Assert.True(MethodLaunchesBash(root, method));
    }

    [Fact]
    public void BashLaunchPolicyRequiresSkipBeforeLaunchWithoutCrossMethodInheritanceOrFalsePositives()
    {
        var root = CSharpSyntaxTree.ParseText("""
            class C {
              [Fact] void Earlier() { Assert.SkipWhen(OperatingSystem.IsWindows(), "bash"); }
              [Fact] async Task Unskipped() { await RunAsync("x", "/bin/bash"); }
              [Fact] async Task Late() { await RunAsync("x", "bash"); Assert.SkipWhen(OperatingSystem.IsWindows(), "late"); }
              [Fact] void MentionOnly() { var text = "bash"; }
            }
            """, cancellationToken: TestContext.Current.CancellationToken).GetRoot(TestContext.Current.CancellationToken);
        var methods = root.DescendantNodes().OfType<MethodDeclarationSyntax>()
            .ToDictionary(method => method.Identifier.ValueText, StringComparer.Ordinal);
        Assert.True(MethodLaunchesBash(root, methods["Unskipped"]));
        Assert.DoesNotContain(methods["Unskipped"].DescendantNodes().OfType<InvocationExpressionSyntax>(),
            IsWindowsSkip);
        Assert.True(FirstBashLaunch(root, methods["Late"]) < methods["Late"].DescendantNodes()
            .OfType<InvocationExpressionSyntax>().Single(IsWindowsSkip).SpanStart);
        Assert.False(MethodLaunchesBash(root, methods["MentionOnly"]));
    }

    private static bool MethodLaunchesBash(SyntaxNode root, MethodDeclarationSyntax method) =>
        FirstBashLaunch(root, method) != int.MaxValue;

    private static int FirstBashLaunch(SyntaxNode root, MethodDeclarationSyntax method)
    {
        var methods = root.DescendantNodes().OfType<MethodDeclarationSyntax>()
            .GroupBy(candidate => candidate.Identifier.ValueText, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        return FirstBashLaunch(method, methods, root, []);
    }

    private static int FirstBashLaunch(MethodDeclarationSyntax method,
        IReadOnlyDictionary<string, MethodDeclarationSyntax[]> methods, SyntaxNode root, HashSet<MethodDeclarationSyntax> visiting)
    {
        if (!visiting.Add(method)) return int.MaxValue;
        var first = int.MaxValue;
        var hasBashShebang = method.DescendantNodes().OfType<LiteralExpressionSyntax>().Any(literal =>
            literal.IsKind(SyntaxKind.StringLiteralExpression) &&
            literal.Token.ValueText.StartsWith("#!", StringComparison.Ordinal) &&
            literal.Token.ValueText.Contains("bash", StringComparison.Ordinal));
        foreach (var node in method.DescendantNodes())
        {
            if (node is ObjectCreationExpressionSyntax creation &&
                creation.Type.ToString().EndsWith("ProcessStartInfo", StringComparison.Ordinal) &&
                ContainsBashExpression(creation, root))
                first = Math.Min(first, creation.SpanStart);
            if (node is AssignmentExpressionSyntax assignment &&
                assignment.Left.ToString().EndsWith("FileName", StringComparison.Ordinal) &&
                ContainsBashExpression(assignment.Right, root))
                first = Math.Min(first, assignment.SpanStart);
            if (node is not InvocationExpressionSyntax invocation) continue;
            var name = invocation.Expression switch
            {
                IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
                MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
                _ => invocation.Expression.ToString()
            };
            var runner = name is "Run" or "RunAsync" or "Start";
            if (runner && (ContainsBashExpression(invocation.ArgumentList, root) || hasBashShebang))
                first = Math.Min(first, invocation.SpanStart);
            if (name is "WriteExecutableAsync" or "PayloadSha256")
                first = Math.Min(first, invocation.SpanStart);
            if (!methods.TryGetValue(name, out var helpers)) continue;
            if (helpers.Any(helper => FirstBashLaunch(helper, methods, root, visiting) != int.MaxValue))
                first = Math.Min(first, invocation.SpanStart);
        }
        visiting.Remove(method);
        return first;
    }

    private static bool ContainsBashExpression(SyntaxNode node, SyntaxNode root) =>
        node.DescendantNodesAndSelf().OfType<ExpressionSyntax>().Any(expression => IsBashExpression(expression, root));

    private static bool IsBashExpression(ExpressionSyntax expression, SyntaxNode root)
    {
        if (expression is LiteralExpressionSyntax literal && literal.IsKind(SyntaxKind.StringLiteralExpression))
            return literal.Token.ValueText is "bash" or "/bin/bash";
        if (expression is not IdentifierNameSyntax identifier) return false;
        return root.DescendantNodes().OfType<VariableDeclaratorSyntax>()
            .Where(variable => variable.Identifier.ValueText == identifier.Identifier.ValueText)
            .Select(variable => variable.Initializer?.Value)
            .Any(value => value is not null && IsBashExpression(value, root));
    }

    private static bool IsWindowsSkip(InvocationExpressionSyntax invocation) =>
        invocation.Expression.ToString() == "Assert.SkipWhen" &&
        invocation.ArgumentList.Arguments.FirstOrDefault()?.Expression.ToString() == "OperatingSystem.IsWindows()";

    [Fact]
    public void TestsContainNoMachineSpecificDotnetRootOrPathSeparatorMutation()
    {
        var tests = string.Join('\n', Directory.EnumerateFiles(Path.Combine(RepositoryRoot, "tests"), "*.cs", SearchOption.AllDirectories).Select(File.ReadAllText));
        Assert.DoesNotContain("/home/" + "fuego/.dotnet", tests, StringComparison.Ordinal);
        Assert.Contains("DOTNET_HOST_PATH", tests, StringComparison.Ordinal);
    }

    [Fact]
    public void ShippedIsolationContractIsFailClosedAndDocumented()
    {
        var project = Read("src/Mutate4CSharp/Mutate4CSharp.csproj");
        var script = Read("scripts/agent-gate.sh");
        Assert.Contains("Mutate4CSharpPackIsolation", project, StringComparison.Ordinal);
        Assert.Contains("MSBuildProjectExtensionsPath", project, StringComparison.Ordinal);
        Assert.Contains("Directory.Build.props", project, StringComparison.Ordinal);
        Assert.Contains("Directory.Packages.props", project, StringComparison.Ordinal);
        Assert.Contains(".editorconfig", project, StringComparison.Ordinal);
        Assert.Contains("Mutate4CSharpPackIsolation=true", script, StringComparison.Ordinal);
        Assert.Contains("direct pack", Read("docs/agent-workflow.md"), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("fail closed", Read("README.md"), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WorkflowBindsFreshReportRuntimeAndTrustedEnvironment()
    {
        var script = Read("scripts/agent-gate.sh");
        var docs = Read("docs/agent-workflow.md");
        Assert.Contains("ORCHESTRATION_REFUSAL=73", script, StringComparison.Ordinal);
        Assert.Contains("DOTNET_STARTUP_HOOKS", script, StringComparison.Ordinal);
        Assert.Contains("DOTNET_ADDITIONAL_DEPS", script, StringComparison.Ordinal);
        Assert.Contains("runtime_version", script, StringComparison.Ordinal);
        Assert.Contains("FINALIZATION_PENDING", script, StringComparison.Ordinal);
        Assert.Contains("exitCode", script, StringComparison.Ordinal);
        Assert.Contains("outcome", script, StringComparison.Ordinal);
        Assert.Contains("73", docs, StringComparison.Ordinal);
        Assert.Contains("trusted runtime", docs, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(Path.Combine(RepositoryRoot, ".editorconfig")));
        Assert.Contains("root = true", Read(".editorconfig"), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GateRejectsCanonicalReportEscapesSymlinksExistingTargetsPayloadDriftAndCrashExitFour()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The shipped workflow is a Bash integration.");
        var target = Path.Combine(_root, "consumer");
        await CreateRepositoryAsync(target);
        var head = (await RunAsync(target, "git", "rev-parse", "HEAD")).StandardOutput.Trim();
        var fixture = await CreateToolFixtureAsync();
        var script = Path.Combine(RepositoryRoot, "scripts", "agent-gate.sh");

        await AssertRefusalAsync("report must remain outside", script, target, fixture, head,
            Path.Combine(target, "..", Path.GetFileName(target), "dot-dot.json"));
        var linkParent = Path.Combine(_root, "consumer-link");
        Directory.CreateSymbolicLink(linkParent, target);
        await AssertRefusalAsync("report parent must not be symbolic", script, target, fixture, head,
            Path.Combine(linkParent, "linked.json"));
        var existingReport = Path.Combine(_root, "existing.json");
        await File.WriteAllTextAsync(existingReport, "preserve", TestContext.Current.CancellationToken);
        await AssertRefusalAsync("report must not already exist", script, target, fixture, head, existingReport);
        var reportLink = Path.Combine(_root, "report-link.json");
        File.CreateSymbolicLink(reportLink, Path.Combine(_root, "missing-report-target.json"));
        await AssertRefusalAsync("report must not already exist", script, target, fixture, head, reportLink);
        await File.AppendAllTextAsync(fixture.Command, "# payload drift\n", TestContext.Current.CancellationToken);
        await AssertRefusalAsync("payload SHA256 mismatch", script, target, fixture, head, Path.Combine(_root, "drift.json"));
        fixture = await CreateToolFixtureAsync("exit 4");
        await AssertRefusalAsync("fresh report", script, target, fixture, head, Path.Combine(_root, "crash.json"));
    }

    [Fact]
    public async Task ToolFixtureDerivesRuntimeFromNeutralSdkWhenCallerSdkIsUnavailable()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The shipped workflow is a Bash integration.");
        await File.WriteAllTextAsync(Path.Combine(_root, "global.json"),
            "{\"sdk\":{\"version\":\"99.99.999\",\"rollForward\":\"disable\"}}\n",
            TestContext.Current.CancellationToken);
        var target = Path.Combine(_root, "neutral-sdk-target");
        await CreateRepositoryAsync(target);
        var head = (await RunAsync(target, "git", "rev-parse", "HEAD")).StandardOutput.Trim();
        var fixture = await CreateToolFixtureAsync();

        var result = await RunAsync(target, "bash", Path.Combine(RepositoryRoot, "scripts", "agent-gate.sh"),
            "gate", target, fixture.Receipt, fixture.PackageSha, fixture.PayloadSha, fixture.ToolCommit,
            head, head, Path.Combine(_root, "neutral-sdk-report.json"), "no-state");

        Assert.Equal(4, result.ExitCode);
        Assert.DoesNotContain("trusted runtime version does not match receipt", result.Diagnostic,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReceiptWriterValidatesBeforeExclusivePublication()
    {
        var script = Read("scripts/agent-gate.sh");
        var function = script[script.IndexOf("write_receipt()", StringComparison.Ordinal)..script.IndexOf("read_receipt()", StringComparison.Ordinal)];
        Assert.Contains("mktemp", function, StringComparison.Ordinal);
        Assert.Contains("mv -n", function, StringComparison.Ordinal);
        Assert.Contains("rm -f", function, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PrepareRejectsPreExistingWorkspaceAndSymlinkReceiptWithoutPartialPublication()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The shipped workflow is a Bash integration.");
        var script = Path.Combine(RepositoryRoot, "scripts", "agent-gate.sh");
        var existingWorkspace = Path.Combine(_root, "existing-workspace");
        Directory.CreateDirectory(existingWorkspace);
        var untouchedReceipt = Path.Combine(_root, "untouched.tsv");
        var workspaceResult = await RunAsync(RepositoryRoot, "bash", script, "prepare", RepositoryRoot,
            existingWorkspace, untouchedReceipt);
        Assert.Equal(OrchestrationRefusal, workspaceResult.ExitCode);
        Assert.Contains("workspace must not already exist", workspaceResult.Diagnostic, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(untouchedReceipt));

        var newWorkspace = Path.Combine(_root, "new-workspace");
        var receiptLink = Path.Combine(_root, "receipt-link.tsv");
        File.CreateSymbolicLink(receiptLink, Path.Combine(_root, "missing-receipt.tsv"));
        var receiptResult = await RunAsync(RepositoryRoot, "bash", script, "prepare", RepositoryRoot,
            newWorkspace, receiptLink);
        Assert.Equal(OrchestrationRefusal, receiptResult.ExitCode);
        Assert.Contains("receipt must not already exist", receiptResult.Diagnostic, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(newWorkspace));
        Assert.NotNull(new FileInfo(receiptLink).LinkTarget);
    }

    [Theory]
    [InlineData("src/.editorconfig", "ignored MSBuild, NuGet, editorconfig")]
    [InlineData("src/Mutate4CSharp/.globalconfig", "ignored project inputs")]
    [InlineData(".globalconfig", "ignored MSBuild, NuGet, editorconfig")]
    [InlineData("src/Mutate4CSharp/obj/Mutate4CSharp.csproj.hostile.targets", "ignored MSBuild, NuGet, editorconfig")]
    public async Task IsolatedPackRejectsIgnoredBuildControlInputs(string relativePath, string diagnostic)
    {
        var source = Path.Combine(_root, "pack-" + Guid.NewGuid().ToString("N"));
        var clone = await RunAsync(_root, "git", "clone", "--quiet", "--no-local", RepositoryRoot, source);
        Assert.True(clone.ExitCode == 0, clone.Diagnostic);
        File.Copy(Path.Combine(RepositoryRoot, "src", "Mutate4CSharp", "Mutate4CSharp.csproj"),
            Path.Combine(source, "src", "Mutate4CSharp", "Mutate4CSharp.csproj"), overwrite: true);
        File.Copy(Path.Combine(RepositoryRoot, ".editorconfig"), Path.Combine(source, ".editorconfig"), overwrite: true);
        await RunRequiredAsync(source, "git", "config", "user.email", "fixture@example.invalid");
        await RunRequiredAsync(source, "git", "config", "user.name", "fixture");
        await RunRequiredAsync(source, "git", "add", "src/Mutate4CSharp/Mutate4CSharp.csproj", ".editorconfig");
        if (!string.IsNullOrWhiteSpace((await RunAsync(source, "git", "status", "--porcelain=v1")).StandardOutput))
            await RunRequiredAsync(source, "git", "commit", "--quiet", "-m", "fixture implementation");
        var revision = (await RunAsync(source, "git", "rev-parse", "HEAD")).StandardOutput.Trim();
        var hostile = Path.Combine(source, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(hostile)!);
        await File.WriteAllTextAsync(hostile,
            relativePath.EndsWith(".globalconfig", StringComparison.Ordinal) ? "is_global = true\n" : "<Project />",
            TestContext.Current.CancellationToken);
        await File.AppendAllTextAsync(Path.Combine(source, ".git", "info", "exclude"), $"/{relativePath}\n",
            TestContext.Current.CancellationToken);
        Assert.Equal(0, (await RunAsync(source, "git", "check-ignore", hostile)).ExitCode);
        var isolationRoot = Path.Combine(_root, "isolation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(isolationRoot, "obj"));
        Directory.CreateDirectory(Path.Combine(isolationRoot, "bin"));
        await File.WriteAllTextAsync(Path.Combine(isolationRoot, "empty.props"), "<Project />", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(isolationRoot, "empty.targets"), "<Project />", TestContext.Current.CancellationToken);
        var pack = await RunAsync(source, DotnetHost(), "pack", "src/Mutate4CSharp/Mutate4CSharp.csproj", "-c", "Release", "-m:1",
            $"-p:Version=0.1.0-local.{revision}", $"-p:SourceRevisionId={revision}", "-p:Mutate4CSharpPackIsolation=true",
            "-p:ImportDirectoryBuildProps=false", "-p:ImportDirectoryBuildTargets=false", "-p:ImportDirectoryPackagesProps=false",
            $"-p:DirectoryBuildPropsPath={Path.Combine(isolationRoot, "empty.props")}",
            $"-p:DirectoryBuildTargetsPath={Path.Combine(isolationRoot, "empty.targets")}",
            "-p:CustomBeforeMicrosoftCommonProps=", "-p:CustomAfterMicrosoftCommonProps=",
            "-p:CustomBeforeMicrosoftCommonTargets=", "-p:CustomAfterMicrosoftCommonTargets=",
            "-p:CustomBeforeMicrosoftCSharpTargets=", "-p:CustomAfterMicrosoftCSharpTargets=",
            "-p:ImportUserLocationsByWildcardBeforeMicrosoftCommonProps=false",
            "-p:ImportUserLocationsByWildcardAfterMicrosoftCommonProps=false",
            "-p:ImportUserLocationsByWildcardBeforeMicrosoftCommonTargets=false",
            "-p:ImportUserLocationsByWildcardAfterMicrosoftCommonTargets=false",
            "-p:ImportUserLocationsByWildcardBeforeMicrosoftCSharpTargets=false",
            "-p:ImportUserLocationsByWildcardAfterMicrosoftCSharpTargets=false",
            $"-p:MSBuildUserExtensionsPath={Path.Combine(isolationRoot, "user-extensions")}",
            $"-p:MSBuildProjectExtensionsPath={Path.Combine(isolationRoot, "obj")}{Path.DirectorySeparatorChar}",
            $"-p:BaseIntermediateOutputPath={Path.Combine(isolationRoot, "obj")}{Path.DirectorySeparatorChar}",
            $"-p:BaseOutputPath={Path.Combine(isolationRoot, "bin")}{Path.DirectorySeparatorChar}",
            $"-p:OutputPath={Path.Combine(isolationRoot, "bin")}{Path.DirectorySeparatorChar}",
            "-p:Mutate4CSharpNoAutoResponse=true");
        Assert.NotEqual(0, pack.ExitCode);
        Assert.True(pack.Diagnostic.Contains(diagnostic, StringComparison.OrdinalIgnoreCase), pack.Diagnostic);
    }

    private async Task AssertRefusalAsync(string diagnostic, string script, string target, ToolFixture fixture, string head, string report)
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The shipped workflow is a Bash integration.");
        var result = await RunAsync(target, "bash", script, "gate", target, fixture.Receipt, fixture.PackageSha,
            fixture.PayloadSha, fixture.ToolCommit, head, head, report, "no-state");
        Assert.Equal(OrchestrationRefusal, result.ExitCode);
        Assert.Contains(diagnostic, result.Diagnostic, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<ToolFixture> CreateToolFixtureAsync(string? checkBody = null)
    {
        var id = Guid.NewGuid().ToString("N");
        var package = Path.Combine(_root, $"candidate-{id}.nupkg");
        var payload = Path.Combine(_root, $"payload-{id}");
        var command = Path.Combine(payload, "mutate4csharp");
        var receipt = Path.Combine(_root, $"receipt-{id}.tsv");
        var toolCommit = new string('a', 40);
        var version = $"0.1.0-local.{toolCommit}";
        Directory.CreateDirectory(payload);
        var sdk = Path.Combine(Path.GetDirectoryName(payload)!, "sdk");
        Directory.CreateDirectory(sdk);
        File.Copy(Path.Combine(RepositoryRoot, "global.json"), Path.Combine(sdk, "global.json"), overwrite: true);
        await File.WriteAllTextAsync(package, "package", TestContext.Current.CancellationToken);
        var body = checkBody ?? "printf '%s\\n' '{\"schemaVersion\":\"2\",\"mode\":\"check\",\"baseline\":\"UNKNOWN\",\"units\":[],\"reasons\":[],\"outcome\":\"INCOMPLETE\",\"exitCode\":4,\"incompleteConditions\":[{\"code\":\"BASELINE_INCONCLUSIVE\"}],\"counts\":{\"enumerated\":0,\"selected\":0,\"executed\":0,\"freshUncovered\":0,\"omitted\":0,\"compileInvalid\":0,\"killed\":0,\"survived\":0,\"errors\":0}}' > \"$5\"\nexit 4";
        await WriteExecutableAsync(command, $"#!/usr/bin/env bash\nif [[ ${{1:-}} = --version ]]; then echo '{version}+{toolCommit}'; exit 0; fi\n{body}\n");
        var runtime = (await RunAsync(sdk, DotnetHost(), "--version")).StandardOutput.Trim();
        await File.WriteAllTextAsync(receipt, $"format\tmutate4csharp-agent-gate-v4\nlocal_tool_version\t{version}\ntool_package\t{package}\ntool_payload\t{payload}\nruntime_version\t{runtime}\ndotnet_host\t{CanonicalDotnetHost()}\n", TestContext.Current.CancellationToken);
        return new(receipt, command, Sha256File(package), PayloadSha256(payload), toolCommit);
    }

    private static string Read(string path) => File.ReadAllText(Path.Combine(RepositoryRoot, path));
    private static string DotnetHost() => Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
    private static string CanonicalDotnetHost() => new FileInfo(DotnetHost()).ResolveLinkTarget(true)?.FullName ?? Path.GetFullPath(DotnetHost());
    private static string Sha256File(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    private static string PayloadSha256(string root)
    {
        if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        using var aggregate = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            aggregate.AppendData(Encoding.UTF8.GetBytes("./" + Path.GetRelativePath(root, path).Replace('\\', '/')));
            aggregate.AppendData([0]);
            aggregate.AppendData(Encoding.ASCII.GetBytes(Convert.ToString((int)File.GetUnixFileMode(path), 8) + "\n"));
            aggregate.AppendData(Encoding.ASCII.GetBytes(Sha256File(path) + "\n"));
        }
        return Convert.ToHexString(aggregate.GetHashAndReset()).ToLowerInvariant();
    }
    private static async Task WriteExecutableAsync(string path, string content)
    {
        if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        await File.WriteAllTextAsync(path, content, TestContext.Current.CancellationToken);
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }
    private static async Task CreateRepositoryAsync(string path)
    {
        Directory.CreateDirectory(path);
        await RunRequiredAsync(path, "git", "init", "--quiet");
        await RunRequiredAsync(path, "git", "config", "user.email", "fixture@example.invalid");
        await RunRequiredAsync(path, "git", "config", "user.name", "fixture");
        await File.WriteAllTextAsync(Path.Combine(path, "README.md"), "fixture", TestContext.Current.CancellationToken);
        await RunRequiredAsync(path, "git", "add", ".");
        await RunRequiredAsync(path, "git", "commit", "--quiet", "-m", "fixture");
    }
    private static async Task RunRequiredAsync(string directory, string executable, params string[] args)
    {
        var result = await RunAsync(directory, executable, args);
        Assert.True(result.ExitCode == 0, result.Diagnostic);
    }
    private static async Task<ProcessResult> RunAsync(string directory, string executable, params string[] args)
    {
        var start = new ProcessStartInfo(executable) { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        return new(process.ExitCode, await stdout, await stderr);
    }
    private sealed record ToolFixture(string Receipt, string Command, string PackageSha, string PayloadSha, string ToolCommit);
    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError)
    { public string Diagnostic => $"exit={ExitCode}\nstdout:\n{StandardOutput}\nstderr:\n{StandardError}"; }
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }
}
