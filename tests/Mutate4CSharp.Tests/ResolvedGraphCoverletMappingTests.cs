using System.Text;
using System.Text.Json;

namespace Mutate4CSharp.Tests;

// Pure bounded mapping/output-join controls; genuine instrumentation is tested
// separately by Actual_resolved_graph_baseline_retains_real_covered_modules.
public sealed class ResolvedGraphCoverletMappingTests
{
    static readonly string Root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "coverlet-mapping-data", "source"));
    static readonly string Packages = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "coverlet-mapping-data", "packages"));
    static readonly string Project = Path.Combine(Root, "tests", "Actual.Name.csproj");
    static string Slash(string value) => value.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
    static byte[] Unicode(string value) => Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(value)).ToArray();

    [Fact]
    public void Original_complete_records_are_preserved_and_exact_owned_reverse_pair_is_appended()
    {
        var original = Unicode(Project + "|" + Slash(Packages) + "=" + Slash(Packages) + "\r\n");
        var before = original.ToArray();
        var suffix = ResolvedGraphCoverletMapping.AppendBytes(original, Root, Project, Packages);
        Assert.Equal(before, original);
        var text = Encoding.Unicode.GetString(suffix);
        Assert.Equal(Project + "|" + Slash(Root) + "=/_/mutate-source/\r\n" +
            Project + "|" + Slash(Packages) + "=/_/mutate-packages/\r\n", text);
        var created = ResolvedGraphCoverletMapping.AppendBytes(null, Root, Project, Packages);
        Assert.Equal(Encoding.Unicode.GetPreamble(), created[..2]);
        Assert.Equal(suffix, created[2..]);
    }

    [Theory]
    [InlineData("foreign-physical")]
    [InlineData("foreign-project")]
    [InlineData("overlapping-prefix")]
    [InlineData("wrong-role")]
    [InlineData("malformed")]
    [InlineData("unsupported-encoding")]
    public void Foreign_or_ambiguous_existing_mapping_is_never_hidden_by_a_later_valid_pair(string kind)
    {
        var outside = Path.Combine(Path.GetTempPath(), "foreign-coverlet-root") + Path.DirectorySeparatorChar;
        var line = kind switch
        {
            "foreign-physical" => Project + "|" + outside + "=/_/mutate-source/\r\n",
            "foreign-project" => Path.Combine(outside, "Other.csproj") + "|" + Slash(Root) + "=/_/mutate-source/\r\n",
            "overlapping-prefix" => Project + "|" + Slash(Root) + "=/_/\r\n",
            "wrong-role" => Project + "|" + Slash(Packages) + "=/_/mutate-source/\r\n",
            _ => "malformed"
        };
        var bytes = kind == "unsupported-encoding" ? Encoding.UTF8.GetBytes(line) : Unicode(line);
        Assert.Throws<EvaluationContractException>(() => ResolvedGraphCoverletMapping.AppendBytes(bytes, Root, Project, Packages));
    }

    [Theory]
    [InlineData("project")]
    [InlineData("framework")]
    [InlineData("configuration")]
    [InlineData("output")]
    [InlineData("name")]
    public void Output_locator_must_join_the_actual_selected_context_before_any_mapping_write(string changed)
    {
        var values = new Dictionary<string, string> {
            ["MSBuildProjectFullPath"] = Project, ["TargetFramework"] = "net10.0", ["Configuration"] = "Release",
            ["TargetPath"] = Path.Combine(Root, "custom-output", "Actual.Name.dll"), ["AssemblyName"] = "Actual.Name" };
        Assert.Equal(values["TargetPath"], ResolvedGraphCoverletMapping.ReadLocation(
            JsonSerializer.SerializeToUtf8Bytes(new { Properties = values }), Root, Project, "net10.0", "Release").TargetPath);
        switch (changed)
        {
            case "project": values["MSBuildProjectFullPath"] = Path.Combine(Root, "Other.csproj"); break;
            case "framework": values["TargetFramework"] = "net9.0"; break;
            case "configuration": values["Configuration"] = "Debug"; break;
            case "output": values["TargetPath"] = Path.Combine(Path.GetTempPath(), "foreign", "Actual.Name.dll"); break;
            case "name": values["AssemblyName"] = "Other"; break;
        }
        Assert.Throws<EvaluationContractException>(() => ResolvedGraphCoverletMapping.ReadLocation(
            JsonSerializer.SerializeToUtf8Bytes(new { Properties = values }), Root, Project, "net10.0", "Release"));
    }

    [Fact]
    public void Exclusive_append_preserves_original_bytes_and_rechecks_complete_result_under_same_original()
    {
        var directory = Path.Combine(Path.GetTempPath(), "coverlet-append-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "map");
        try
        {
            var before = Unicode("original\r\n"); File.WriteAllBytes(path, before);
            var original = new ResolvedGraphBudget(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            using var inputs = new ResolvedGraphInputs(original);
            var expected = inputs.Read(path, "config", 65_536);
            var suffix = Encoding.Unicode.GetBytes("suffix\r\n");
            var after = inputs.AppendKnownResult(expected, suffix, 65_536);
            Assert.Equal(before.Concat(suffix).ToArray(), after.Bytes);
            inputs.Recheck(after, 65_536);
            Assert.Null(original.Observe().Cleanup);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void Changed_original_bytes_refuse_before_append_and_first_failure_cannot_be_retried()
    {
        var directory = Path.Combine(Path.GetTempPath(), "coverlet-drift-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory); var path = Path.Combine(directory, "map");
        try
        {
            File.WriteAllBytes(path, Unicode("original\r\n"));
            var original = new ResolvedGraphBudget(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            var inputs = new ResolvedGraphInputs(original);
            var expected = inputs.Read(path, "config", 65_536);
            var changed = Unicode("replaced\r\n"); File.WriteAllBytes(path, changed);
            Assert.Throws<AggregateException>(() => inputs.AppendKnownResult(expected, Encoding.Unicode.GetBytes("suffix"), 65_536));
            Assert.Equal(changed, File.ReadAllBytes(path));
            Assert.NotNull(original.Observe().Cleanup);
            var first = Assert.Throws<AggregateException>(inputs.Dispose);
            Assert.Same(first, Assert.Throws<AggregateException>(inputs.Dispose));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void Empty_raw_OpenCover_and_TRX_are_preserved_as_failure_data_after_original_results_are_removed()
    {
        var directory = Path.Combine(Path.GetTempPath(), "coverlet-empty-failure-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var xml = Encoding.UTF8.GetBytes("<CoverageSession><Modules /></CoverageSession>");
        var trx = Encoding.UTF8.GetBytes("<TestRun />");
        File.WriteAllBytes(Path.Combine(directory, "coverage.opencover.xml"), xml);
        File.WriteAllBytes(Path.Combine(directory, "actual.trx"), trx);
        var original = new ResolvedGraphBudget(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        using var inputs = new ResolvedGraphInputs(original);
        Assert.Empty(TestRunner.FindCoverage(directory, inputs));
        var summary = RetainedCoverageFailure.Preserve(directory, original, inputs);
        Directory.Delete(directory, true);
        var leaf = summary.Split(';')[0]["missing-coverage-private-bundle=".Length..];
        var retained = Path.Combine(OwnedDirectory.PrivateParent("coverage"), leaf);
        var raw = Directory.GetFiles(retained, "artifact-*");
        Assert.Contains(raw, path => File.ReadAllBytes(path).AsSpan().SequenceEqual(xml));
        Assert.Contains(raw, path => File.ReadAllBytes(path).AsSpan().SequenceEqual(trx));
        using var manifest = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(retained, "failure.json")));
        Assert.False(manifest.RootElement.GetProperty("AcceptedCoverage").GetBoolean());
        Assert.False(manifest.RootElement.GetProperty("ProductionAuthority").GetBoolean());
        Assert.Null(original.Observe().Cleanup);
    }

    [Fact]
    public void Borrowed_inputs_cannot_freshen_or_substitute_the_actual_graph_original()
    {
        var original = new ResolvedGraphBudget(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        var other = new ResolvedGraphBudget(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        using var inputs = new ResolvedGraphInputs(original);
        inputs.RequireOriginal(original);
        Assert.Throws<EvaluationContractException>(() => inputs.RequireOriginal(other));
        Assert.Null(original.Observe().Cleanup);
        original.BeginFailureCleanup();
        Assert.Throws<EvaluationContractException>(() => inputs.RequireOriginal(original));
    }
}
