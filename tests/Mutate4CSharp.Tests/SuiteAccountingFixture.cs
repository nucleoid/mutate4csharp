namespace Mutate4CSharp.Tests;

// A structural fixture for report/authority unit tests; never installed-CLI certification.
internal static class SuiteAccountingFixture
{
    internal const string Runner = "runner=vstest;collector=coverlet-opencover-v1;" +
        "coverage=sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa;" +
        "plan=sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    internal static SuiteEvidence Create(string runId, string snapshotId, string coverageHash, long coverageLength)
    {
        var configuration = new CheckTestSuite("unit", "tests/App.Tests.csproj", "vstest", "net10.0", "Release", ["Tests.dll"]);
        return new(CheckConfiguration.SuiteIdentity(configuration), BaselineStatus.Green,
            [new("SUITE_BASELINE", "Bound structural baseline fixture.")])
        {
            Accounting = new(runId, snapshotId, configuration, ["Tests.dll"], coverageHash, coverageLength,
                new string('a', 64), "baseline-clone-to-snapshot-v1", true)
        };
    }
}
