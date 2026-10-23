using System.Text;
using System.Text.Json;

namespace Mutate4CSharp.Tests;

public sealed class ResolvedGraphProtocolTests
{
    [Fact]
    public void Complete_bounded_write_positive_precedes_overflow_without_prefix_acceptance_or_budget_renewal()
    {
        var original = new ResolvedGraphBudget(TimeSpan.FromSeconds(30), CancellationToken.None);
        using var inputs = new ResolvedGraphInputs(original);
        var before = original.Observe();
        var bytes = ResolvedGraphProtocol.SerializeBounded(new { Value = "complete" }, inputs, 1024, 1_048_576);
        using var parsed = JsonDocument.Parse(bytes);
        Assert.Equal("complete", parsed.RootElement.GetProperty("Value").GetString());
        Assert.Equal(before, original.Observe());
        Assert.ThrowsAny<SnapshotCaptureException>(() =>
            ResolvedGraphProtocol.SerializeBounded(new { Value = new string('x', 2048) }, inputs, 1024, 1_048_576));
        var failed = original.Observe();
        Assert.Equal(before.Original, failed.Original);
        Assert.NotNull(failed.Cleanup);
        Assert.Throws<EvaluationContractException>(original.CheckWork);
    }

    [Fact]
    public void Duplicate_nested_protocol_members_refuse_instead_of_last_value_wins()
    {
        ResolvedGraphProtocol.RequireUnique(Encoding.UTF8.GetBytes("{\"Version\":1,\"Nested\":{\"Value\":2}}"));
        Assert.Throws<EvaluationContractException>(() => ResolvedGraphProtocol.RequireUnique(
            Encoding.UTF8.GetBytes("{\"Version\":1,\"Nested\":{\"Value\":2,\"Value\":3}}")));
    }

    [Fact]
    public void Complete_compiled_document_overflow_refuses_during_count_before_second_serialization()
    {
        var original = new ResolvedGraphBudget(TimeSpan.FromSeconds(30), CancellationToken.None);
        using var inputs = new ResolvedGraphInputs(original);
        var documents = Enumerable.Range(0, 8160).Select(i => new {
            Path = "/" + i.ToString("D4", System.Globalization.CultureInfo.InvariantCulture) + new string('a', 4084),
            Sha256 = new string('b', 64) }).ToArray();
        var passes = 0;
        Assert.ThrowsAny<SnapshotCaptureException>(() => ResolvedGraphProtocol.WriteBounded(writer =>
        {
            ++passes;
            JsonSerializer.Serialize(writer, new { Version = 5, Compiled = new { Documents = documents } });
        }, inputs, 32 * 1024 * 1024, 1_048_576));
        Assert.Equal(1, passes); // No complete output array or prefix second pass.
        Assert.NotNull(original.Observe().Cleanup);
    }
}
