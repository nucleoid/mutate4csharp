using System.Xml.Linq;

namespace Mutate4CSharp;

internal sealed class TrxAccountingException : Exception
{
    public TrxAccountingException(string path, Exception innerException) :
        base($"Failed to read TRX membership from '{Path.GetFileName(path)}': {innerException.Message}",
            innerException) { }

    public TrxAccountingException(string path, string message) :
        base($"Failed to account TRX membership from '{Path.GetFileName(path)}': {message}") { }
}

internal sealed class VstestSuiteExecutor(InputSnapshot snapshot, FrozenExecutionEnvironment environment,
    TimeProvider? timeProvider = null,
    Action<SnapshotClone, IReadOnlyList<string>>? beforeCoverageParsing = null, bool resolvedGraph = false,
    ResolvedGraphBudget? originalBudget = null) : ISuiteExecutor
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private sealed record RetainedOriginal(ResolvedGraphBudget? Original, ResolvedGraphInputs? Inputs,
        SnapshotClone? Worker, OwnedPackageCache? Packages, Exception? Failure, bool InputsClosed);
    private static readonly List<RetainedOriginal> Retained = [];

    public async Task<SuiteRunResult> RunBaselineAsync(SuiteExecution suite, TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (!suite.Runner.Equals("vstest", StringComparison.Ordinal))
            throw new EvaluationContractException(
                $"Unsupported test runner '{suite.Runner}'; version 1 supports only VSTest/TRX.");
        if (resolvedGraph)
            (originalBudget ?? throw new EvaluationContractException("Same live graph original required before baseline birth.")).CheckWork();
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
            lock (Retained)
                if (resolvedGraph && Retained.Count >= 256) throw new SnapshotLimitException("Retained baseline originals exhausted; no further baseline birth.");
            SnapshotClone? worker = null;
            OwnedPackageCache? packages = null;
            OwnedCoverageReports? coverage = null;
            ResolvedGraphInputs? coverageInputs = null;
            SuiteRunResult? completed = null;
            Exception? failure = null;
            try
            {
                worker = await SnapshotWorkspace.CreateCloneAsync(snapshot,
                    $"baseline-{Sanitize(suite.Aliases[0])}", linked.Token, originalBudget);
                packages = await environment.CreateWorkerPackageCacheAsync(
                    $"baseline-{Sanitize(suite.Aliases[0])}", linked.Token, originalBudget);
                if (resolvedGraph)
                    coverageInputs = new(originalBudget ?? throw new EvaluationContractException("Same graph original required for baseline coverage inputs."));
                var remaining = Remaining();
                if (remaining <= TimeSpan.Zero) throw new TimeoutException();
                await environment.RestoreWorkerAsync(worker, suite.Path, packages, remaining, linked.Token,
                    suite.Framework, suite.Configuration, originalBudget: originalBudget);
                remaining = Remaining();
                if (remaining <= TimeSpan.Zero) throw new TimeoutException();
                var results = Path.Combine(worker.OwnedRoot, "results");
                var testPath = Path.Combine(worker.Root, suite.Path.Replace('/', Path.DirectorySeparatorChar));
                var run = await TestRunner.RunAsync(worker.Root, testPath, results, remaining, collectCoverage: true,
                    linked.Token, ExecutionEnvironment.ProcessEnvironment(packages.Root), noRestore: true,
                    requireExecutionBoundary: true, framework: suite.Framework, configuration: suite.Configuration, resolvedGraph: resolvedGraph,
                    originalBudget: originalBudget, coverageInputs: coverageInputs);
                if (!string.Equals(environment.PackageFingerprint,
                        ExecutionEnvironment.FingerprintPackages(packages.Root,
                            cancellationToken: linked.Token, originalBudget: originalBudget), StringComparison.Ordinal))
                    throw new ExecutionBoundaryIntegrityException(
                        "Baseline execution changed its private frozen package-cache copy.");
                var disposition = ClassifyBaseline(run);
                var pinnedCoverage = resolvedGraph ? new Dictionary<string, ResolvedGraphFile>(StringComparer.Ordinal) : null;
                var rawCoverage = TestRunner.FindCoverage(results, coverageInputs, pinnedCoverage);
                if (disposition == SuiteRunDisposition.Passed) beforeCoverageParsing?.Invoke(worker, rawCoverage);
                if (pinnedCoverage is not null)
                    foreach (var captured in pinnedCoverage.Values) coverageInputs!.Recheck(captured);
                var coverageMap = disposition == SuiteRunDisposition.Passed
                    ? CoverageMap.Load(rawCoverage, worker, snapshot, resolvedGraph, resolvedGraph ? packages.Root : null, pinnedCoverage) : null;
                coverage = disposition == SuiteRunDisposition.Passed
                    ? pinnedCoverage is null
                        ? await PreserveOpenCoverReportsAsync(rawCoverage, $"baseline-{Sanitize(suite.Aliases[0])}")
                        : await PreservePinnedOpenCoverReportsAsync(pinnedCoverage.Values, coverageInputs!, $"baseline-{Sanitize(suite.Aliases[0])}")
                    : null;
                var diagnostics = BaselineDiagnostics(run, disposition == SuiteRunDisposition.Passed &&
                    coverage is null, worker.Root, packages.Root).ToList();
                diagnostics.AddRange(coverageMap?.ProjectionDiagnostics ?? []);
                if (disposition == SuiteRunDisposition.Passed && coverage is null)
                {
                    diagnostics.Add(CoverageInventory(results));
                    if (resolvedGraph && originalBudget is not null)
                        diagnostics.Insert(0, RetainedCoverageFailure.Preserve(results, originalBudget, coverageInputs!));
                }
                completed = new(disposition, _timeProvider.GetUtcNow() - started, AccountedMembers(run.TrxPaths),
                    run.FailedTestIds ?? [], diagnostics.Take(20).ToArray(), coverage?.Reports ?? [])
                    {
                        CoverageOwner = coverage,
                        CoverageMap = coverageMap,
                        CoverageSha256 = coverage is null ? null : coverage.Pins is null ? HashReports(coverage.Reports) : HashPinnedReports(coverage.Pins),
                        CoverageLength = coverage?.Pins?.Sum(pin => pin.Bytes.LongLength) ?? coverage?.Reports.Sum(path => new FileInfo(path).Length) ?? 0,
                        HealthyControl = disposition == SuiteRunDisposition.Passed
                    };
            }
            catch (Exception ex) { failure = ex; }

            var inputsClosed = false;
            try { coverageInputs?.Dispose(); inputsClosed = true; }
            catch (Exception inputCleanup)
            {
                failure = AsyncDisposal.CombineFailure(failure, inputCleanup);
            }
            // Every graph failure retains its exact namespace, including a
            // native cleanup-unknown before any collector file was opened and
            // a failure-data bound/refusal before the raw bundle completed.
            // Successful first-close is required before dependent retirement.
            if (resolvedGraph && (!inputsClosed || failure is not null || completed?.Disposition != SuiteRunDisposition.Passed))
            {
                var retained = new RetainedOriginal(originalBudget, coverageInputs, worker, packages, failure, inputsClosed);
                lock (Retained) Retained.Add(retained);
                if (failure is not null) failure.Data["ResolvedGraphCoverage.RetainedOriginal"] = retained;
            }
            else
            {
                try { await AsyncDisposal.DisposeAllAsync([packages, worker]); }
                catch (Exception cleanupFailure)
                {
                    failure = AsyncDisposal.CombineFailure(failure, cleanupFailure);
                }
            }

            if (failure is not null)
            {
                if (coverage is not null)
                {
                    try { await coverage.DisposeAsync(); }
                    catch (Exception cleanupFailure)
                    {
                        failure = AsyncDisposal.CombineFailure(failure, cleanupFailure);
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

    private static IReadOnlyList<string> BaselineDiagnostics(TestRunResult run, bool coverageMissing,
        string workerRoot, string packageRoot)
    {
        var diagnostics = (run.Diagnostics ?? []).Take(12).ToList();
        if (ClassifyBaseline(run) != SuiteRunDisposition.Error && !coverageMissing) return diagnostics;
        foreach (var line in (run.StandardOutput ?? string.Empty).Split('\n').Where(line =>
                     line.Contains("warning CS", StringComparison.Ordinal) ||
                     line.Contains("error CS", StringComparison.Ordinal)).Take(6))
            diagnostics.Add(EvaluationTextBounds.Prefix(Sanitize(line.Trim()), EvaluationEvidence.MaxDiagnosticLength));
        foreach (var value in new[] { run.StandardError, run.StandardOutput })
        {
            var bounded = string.Join(' ', Sanitize(value ?? string.Empty).Split((char[]?)null,
                StringSplitOptions.RemoveEmptyEntries));
            bounded = EvaluationTextBounds.Suffix(bounded, EvaluationEvidence.MaxDiagnosticLength);
            if (bounded.Length > 0) diagnostics.Add(bounded);
        }
        return diagnostics.Take(20).ToArray();

        string Sanitize(string value) => value.Replace(workerRoot, "<snapshot>", StringComparison.OrdinalIgnoreCase)
            .Replace(packageRoot, "<packages>", StringComparison.OrdinalIgnoreCase)
            .Replace(Path.GetTempPath(), "<temp>/", StringComparison.OrdinalIgnoreCase);
    }

    private static string CoverageInventory(string results)
    {
        var files = Directory.Exists(results)
            ? ProjectLocator.EnumerateFilesSafe(results).Select(Path.GetFileName).OrderBy(value => value,
                StringComparer.Ordinal).Take(20).ToArray()
            : [];
        var value = "Coverage output was missing; result files: " +
            (files.Length == 0 ? "<none>" : string.Join(", ", files));
        return EvaluationTextBounds.Prefix(value, EvaluationEvidence.MaxDiagnosticLength);
    }

    internal static IReadOnlyList<string> AccountedMembers(IReadOnlyList<string> trxPaths)
    {
        var members = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in trxPaths)
        {
            try
            {
                var document = XDocument.Load(path);
                var unitTests = document.Descendants()
                    .Where(item => item.Name.LocalName == "UnitTest").ToArray();
                if (unitTests.Any(item => string.IsNullOrWhiteSpace(item.Attribute("storage")?.Value)))
                    throw new TrxAccountingException(path,
                        "one or more UnitTest definitions did not declare storage.");
                foreach (var storage in unitTests.Select(item => item.Attribute("storage")!.Value))
                {
                    var member = Path.GetFileName(storage.Replace('\\', Path.DirectorySeparatorChar));
                    if (!string.IsNullOrWhiteSpace(member)) members.Add(member);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
            {
                throw new TrxAccountingException(path, ex);
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

    internal static async Task<OwnedCoverageReports?> PreservePinnedOpenCoverReportsAsync(
        IEnumerable<ResolvedGraphFile> reports, ResolvedGraphInputs inputs, string purpose)
    {
        var captured = reports.OrderBy(report => report.Path, StringComparer.Ordinal).ToArray();
        if (captured.Length == 0) return null;
        var owner = OwnedDirectory.Create(OwnedDirectory.PrivateParent("coverage"), purpose);
        var pins = new List<ResolvedGraphFile>();
        try
        {
            for (var index = 0; index < captured.Length; index++)
            {
                var target = Path.Combine(owner.Root, $"coverage-{index:D4}.opencover.xml");
                inputs.WriteNewResult(target, captured[index].Bytes);
                var actual = inputs.Read(target, "config");
                if (actual.Sha256 != captured[index].Sha256 || !actual.Bytes.AsSpan().SequenceEqual(captured[index].Bytes))
                    throw new SnapshotDivergedException("Preserved coverage differs from its complete captured bytes.");
                pins.Add(actual);
            }
            return new(owner, pins.Select(pin => pin.Path).ToArray(), pins);
        }
        catch (Exception preserveFailure)
        {
            try { await owner.DisposeAsync(); }
            catch (Exception cleanupFailure) { throw new SnapshotCleanupException(preserveFailure, cleanupFailure); }
            throw;
        }
    }

    internal static string HashPinnedReports(IReadOnlyList<ResolvedGraphFile> reports)
    {
        using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
        Span<byte> length = stackalloc byte[8];
        foreach (var report in reports.OrderBy(value => value.Path, StringComparer.Ordinal))
        {
            System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(length, report.Bytes.LongLength);
            hash.AppendData(length); hash.AppendData(report.Bytes);
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
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
    IReadOnlyList<string> reports, IReadOnlyList<ResolvedGraphFile>? pins = null) : IAsyncDisposable
{
    public IReadOnlyList<string> Reports { get; } = reports;
    internal IReadOnlyList<ResolvedGraphFile>? Pins { get; } = pins;
    public ValueTask DisposeAsync() => owner.DisposeAsync();
}
