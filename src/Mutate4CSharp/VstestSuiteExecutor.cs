using System.Xml.Linq;

namespace Mutate4CSharp;

internal sealed class VstestSuiteExecutor(InputSnapshot snapshot, FrozenExecutionEnvironment environment,
    TimeProvider? timeProvider = null) : ISuiteExecutor
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async Task<SuiteRunResult> RunBaselineAsync(SuiteExecution suite, TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (!suite.Runner.Equals("vstest", StringComparison.Ordinal))
            throw new EvaluationContractException(
                $"Unsupported test runner '{suite.Runner}'; version 1 supports only VSTest/TRX.");
        var started = _timeProvider.GetUtcNow();
        using var timeoutSource = new CancellationTokenSource(timeout, _timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
        try
        {
            return await ExecuteWithOwnedResourcesAsync();
        }
        catch (TimeoutException)
        {
            return TimedOut();
        }
        catch (OperationCanceledException) when (timeoutSource.IsCancellationRequested &&
                                                 !cancellationToken.IsCancellationRequested)
        {
            return TimedOut();
        }

        TimeSpan Remaining() => timeout - (_timeProvider.GetUtcNow() - started);
        SuiteRunResult TimedOut() => new(SuiteRunDisposition.TimedOut,
            _timeProvider.GetUtcNow() - started, [], [], ["The complete baseline operation exceeded its timeout."], []);

        async Task<SuiteRunResult> ExecuteWithOwnedResourcesAsync()
        {
            SnapshotClone? worker = null;
            OwnedPackageCache? packages = null;
            OwnedCoverageReports? coverage = null;
            SuiteRunResult? completed = null;
            Exception? failure = null;
            try
            {
                worker = await SnapshotWorkspace.CreateCloneAsync(snapshot,
                    $"baseline-{Sanitize(suite.Aliases[0])}", linked.Token);
                packages = await environment.CreateWorkerPackageCacheAsync(
                    $"baseline-{Sanitize(suite.Aliases[0])}", linked.Token);
                var remaining = Remaining();
                if (remaining <= TimeSpan.Zero) throw new TimeoutException();
                await environment.RestoreWorkerAsync(worker, suite.Path, packages, remaining, linked.Token,
                    suite.Framework, suite.Configuration);
                remaining = Remaining();
                if (remaining <= TimeSpan.Zero) throw new TimeoutException();
                var results = Path.Combine(worker.OwnedRoot, "results");
                var testPath = Path.Combine(worker.Root, suite.Path.Replace('/', Path.DirectorySeparatorChar));
                var run = await TestRunner.RunAsync(worker.Root, testPath, results, remaining, collectCoverage: true,
                    linked.Token, ExecutionEnvironment.ProcessEnvironment(packages.Root), noRestore: true,
                    requireExecutionBoundary: true, framework: suite.Framework, configuration: suite.Configuration);
                if (!string.Equals(environment.PackageFingerprint,
                        ExecutionEnvironment.FingerprintPackages(packages.Root), StringComparison.Ordinal))
                    throw new SnapshotDivergedException("Baseline execution changed the frozen package cache.");
                var disposition = ClassifyBaseline(run);
                var rawCoverage = TestRunner.FindCoverage(results);
                var coverageMap = disposition == SuiteRunDisposition.Passed
                    ? CoverageMap.Load(rawCoverage, worker) : null;
                coverage = disposition == SuiteRunDisposition.Passed
                    ? await PreserveOpenCoverReportsAsync(rawCoverage,
                        $"baseline-{Sanitize(suite.Aliases[0])}") : null;
                var diagnostics = BaselineDiagnostics(run, disposition == SuiteRunDisposition.Passed &&
                    coverage is null).ToList();
                if (disposition == SuiteRunDisposition.Passed && coverage is null)
                    diagnostics.Add(CoverageInventory(results));
                completed = new(disposition, _timeProvider.GetUtcNow() - started, AccountedMembers(run.TrxPaths),
                    run.FailedTestIds ?? [], diagnostics.Take(20).ToArray(), coverage?.Reports ?? [])
                    {
                        CoverageOwner = coverage,
                        CoverageMap = coverageMap,
                        CoverageSha256 = coverage is null ? null : HashReports(coverage.Reports),
                        CoverageLength = coverage?.Reports.Sum(path => new FileInfo(path).Length) ?? 0,
                        HealthyControl = disposition == SuiteRunDisposition.Passed
                    };
            }
            catch (Exception ex) { failure = ex; }

            try { await AsyncDisposal.DisposeAllAsync([packages, worker]); }
            catch (Exception cleanupFailure)
            {
                failure = failure is null ? cleanupFailure : new SnapshotCleanupException(failure, cleanupFailure);
            }

            if (failure is not null)
            {
                if (coverage is not null)
                {
                    try { await coverage.DisposeAsync(); }
                    catch (Exception cleanupFailure)
                    {
                        failure = new SnapshotCleanupException(failure, cleanupFailure);
                    }
                }
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
            }
            return completed ?? throw new EvaluationContractException("Baseline execution produced no result.");
        }
    }

    private static SuiteRunDisposition ClassifyBaseline(TestRunResult run)
    {
        if (run.TimedOut) return SuiteRunDisposition.TimedOut;
        if (!run.TrxValid || run.HasRunErrors) return SuiteRunDisposition.Error;
        if (!run.TestsDiscovered) return SuiteRunDisposition.Empty;
        if (run.ExitCode != 0 && run.HasFailedTests) return SuiteRunDisposition.Failed;
        if (run.ExitCode == 0 && !run.HasFailedTests) return SuiteRunDisposition.Passed;
        return SuiteRunDisposition.Error;
    }

    private static IReadOnlyList<string> BaselineDiagnostics(TestRunResult run, bool coverageMissing)
    {
        var diagnostics = (run.Diagnostics ?? []).Take(18).ToList();
        if (ClassifyBaseline(run) != SuiteRunDisposition.Error && !coverageMissing) return diagnostics;
        foreach (var value in new[] { run.StandardError, run.StandardOutput })
        {
            var bounded = string.Join(' ', (value ?? string.Empty).Split((char[]?)null,
                StringSplitOptions.RemoveEmptyEntries));
            if (bounded.Length > 512) bounded = bounded[..512];
            if (bounded.Length > 0) diagnostics.Add(bounded);
        }
        return diagnostics.Take(20).ToArray();
    }

    private static string CoverageInventory(string results)
    {
        var files = Directory.Exists(results)
            ? ProjectLocator.EnumerateFilesSafe(results).Select(Path.GetFileName).OrderBy(value => value,
                StringComparer.Ordinal).Take(20).ToArray()
            : [];
        var value = "Coverage output was missing; result files: " +
            (files.Length == 0 ? "<none>" : string.Join(", ", files));
        return value.Length <= 512 ? value : value[..512];
    }

    internal static IReadOnlyList<string> AccountedMembers(IReadOnlyList<string> trxPaths)
    {
        var members = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in trxPaths)
        {
            try
            {
                var document = XDocument.Load(path);
                foreach (var storage in document.Descendants().Where(item => item.Name.LocalName == "UnitTest")
                             .Select(item => item.Attribute("storage")?.Value)
                             .Where(value => !string.IsNullOrWhiteSpace(value)))
                {
                    var member = Path.GetFileName(storage!.Replace('\\', Path.DirectorySeparatorChar));
                    if (!string.IsNullOrWhiteSpace(member)) members.Add(member);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
            {
                return [];
            }
        }
        return members.ToArray();
    }

    private static async Task<OwnedCoverageReports?> PreserveOpenCoverReportsAsync(IEnumerable<string> reports,
        string purpose)
    {
        var valid = reports.Where(File.Exists).Distinct(StringComparer.Ordinal).ToArray();
        if (valid.Length == 0) return null;
        var owner = OwnedDirectory.Create(OwnedDirectory.PrivateParent("coverage"), purpose);
        var preserved = new List<string>();
        try
        {
            for (var index = 0; index < valid.Length; index++)
            {
                var target = Path.Combine(owner.Root, $"coverage-{index:D4}.opencover.xml");
                File.Copy(valid[index], target, overwrite: false);
                preserved.Add(target);
            }
            return new(owner, preserved);
        }
        catch (Exception preserveFailure)
        {
            try { await owner.DisposeAsync(); }
            catch (Exception cleanupFailure)
            {
                throw new SnapshotCleanupException(preserveFailure, cleanupFailure);
            }
            throw;
        }
    }

    private static string HashReports(IReadOnlyList<string> reports)
    {
        using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(
            System.Security.Cryptography.HashAlgorithmName.SHA256);
        Span<byte> length = stackalloc byte[8];
        foreach (var path in reports.OrderBy(value => value, StringComparer.Ordinal))
        {
            var bytes = File.ReadAllBytes(path);
            System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(length, bytes.LongLength);
            hash.AppendData(length);
            hash.AppendData(bytes);
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static string Sanitize(string value)
    {
        var safe = new string(value.Where(character => char.IsAsciiLetterOrDigit(character) ||
            character is '-' or '_').Take(64).ToArray());
        return safe.Length == 0 ? "suite" : safe;
    }
}

internal sealed class OwnedCoverageReports(OwnedDirectory owner,
    IReadOnlyList<string> reports) : IAsyncDisposable
{
    public IReadOnlyList<string> Reports { get; } = reports;
    public ValueTask DisposeAsync() => owner.DisposeAsync();
}
