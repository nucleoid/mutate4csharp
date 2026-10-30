using System.Text.Json;

namespace Mutate4CSharp;

// Deliberately retained private failure DATA, never accepted coverage. Every
// file is completely bounded/read/written/first-closed under the SAME original.
// Retaining directory ownership preserves the on-disk bundle after clone/cache
// retirement; it retains no native process or open file handle.
internal static class RetainedCoverageFailure
{
    private static readonly List<OwnedDirectory> Retained = [];

    internal static string Preserve(string results, ResolvedGraphBudget original, ResolvedGraphInputs inputs)
    {
        original.CheckWork();
        inputs.RequireOriginal(original);
        OwnedDirectory owner;
        lock (Retained)
        {
            original.CheckWork();
            if (Retained.Count >= 8) throw new SnapshotLimitException("Finite missing-coverage bundle count exceeded.");
            owner = OwnedDirectory.Create(OwnedDirectory.PrivateParent("coverage"), "missing-coverage");
            Retained.Add(owner); // Retain before any later failure; no retry/adoption.
        }
        var rows = new List<object>(); var visited = 0; long total = 0;
        foreach (var path in EnumerateResultFiles(results, inputs))
        {
            original.CheckWork();
            if (++visited > 4096) throw new SnapshotLimitException("Finite failed-coverage inventory exceeded.");
            var name = Path.GetFileName(path);
            var isCoverage = name.Contains("coverage", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase);
            if (!isCoverage && !name.EndsWith(".trx", StringComparison.OrdinalIgnoreCase)) continue;
            if (rows.Count >= 64) throw new SnapshotLimitException("Finite failed-coverage artifact count exceeded.");
            var file = inputs.Read(path, "config", 1_048_576);
            if (file.Bytes.LongLength > 4_194_304 - total) throw new SnapshotLimitException("Finite failed-coverage byte extent exceeded.");
            total += file.Bytes.LongLength;
            var local = $"artifact-{rows.Count:D2}" + (isCoverage ? ".coverage.xml" : ".trx");
            inputs.WriteNewResult(Path.Combine(owner.Root, local), file.Bytes);
            inputs.Recheck(file, 1_048_576);
            rows.Add(new { File = local, Bytes = file.Bytes.LongLength, file.Sha256 });
        }
        inputs.ReserveAllocation(131_072);
        var manifest = JsonSerializer.SerializeToUtf8Bytes(new { Version = 1, Kind = "MissingCoverageFailureData",
            Files = rows, TotalBytes = total, AcceptedCoverage = false, ProductionAuthority = false });
        inputs.WriteNewResult(Path.Combine(owner.Root, "failure.json"), manifest);
        original.CheckWork();
        return "missing-coverage-private-bundle=" + Path.GetFileName(owner.Root) + ";files=" + rows.Count + ";bytes=" + total;
    }

    internal static IEnumerable<string> EnumerateResultFiles(string root, ResolvedGraphInputs inputs)
    {
        if (!inputs.GeneratedDirectoryPresent(root)) yield break;
        var pending = new Queue<(string Path, int Depth)>(); pending.Enqueue((root, 0)); var count = 0;
        while (pending.Count > 0)
        {
            inputs.CheckWork(); var current = pending.Dequeue();
            if (current.Depth > 24) throw new SnapshotLimitException("Coverage result directory depth exceeded.");
            foreach (var entry in inputs.EnumerateGeneratedDirectory(current.Path))
            {
                inputs.CheckWork(); if (++count > 4096) throw new SnapshotLimitException("Coverage result directory count exceeded.");
                if ((entry.Attributes & FileAttributes.Directory) != 0) pending.Enqueue((entry.Path, current.Depth + 1));
                else yield return entry.Path;
            }
        }
    }
}
