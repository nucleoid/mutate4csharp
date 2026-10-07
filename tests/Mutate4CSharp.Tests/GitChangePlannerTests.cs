namespace Mutate4CSharp.Tests;

public sealed class GitChangePlannerTests : IDisposable
{
    private readonly SnapshotTestRepository _repository = new();

    [Fact]
    public async Task ComparesDirectBaseToCapturedWorkingBytesWithoutDuplicatePaths()
    {
        _repository.WriteText("src/App/App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />\n");
        _repository.WriteText("src/App/Changed.cs", "class Changed { int Value() => 1; }\n");
        _repository.WriteText("src/App/Deleted.cs", "class Deleted { bool Value() => true; }\n");
        _repository.WriteText("src/App/Renamed.cs", "class Renamed { }\n");
        _repository.Git("add", ".");
        _repository.Git("commit", "-m", "base");
        var baseCommit = _repository.Git("rev-parse", "HEAD").Trim();

        _repository.WriteText("src/App/Changed.cs", "class Changed { int Value() => 2; }\n");
        _repository.Git("add", "src/App/Changed.cs");
        _repository.WriteText("src/App/Changed.cs", "class Changed { int Value() => 3; }\n");
        File.Delete(Path.Combine(_repository.Root, "src/App/Deleted.cs"));
        _repository.Git("mv", "src/App/Renamed.cs", "src/App/Unicode λ.cs");
        _repository.WriteText("src/App/New file.cs", "class NewFile { }\n");

        await using var snapshot = await SnapshotCapture.CaptureAsync(_repository.Root, baseCommit, [],
            SnapshotCaptureOptions.Default, CancellationToken.None);
        var changes = await GitChangePlanner.DiscoverAsync(snapshot, CancellationToken.None);

        Assert.Equal(baseCommit, changes.BaseCommit);
        Assert.Equal(changes.Files.Count, changes.Files.Select(file => file.CurrentPath ?? file.BasePath)
            .Distinct(StringComparer.Ordinal).Count());
        Assert.Contains(changes.Files, file => file.CurrentPath == "src/App/Changed.cs" &&
            file.Kind == ChangeKind.Modified);
        Assert.Contains(changes.Files, file => file.CurrentPath == "src/App/New file.cs" &&
            file.Kind == ChangeKind.Added);
        Assert.Contains(changes.Files, file => file.BasePath == "src/App/Deleted.cs" &&
            file.Kind == ChangeKind.Deleted);
        Assert.Contains(changes.Files, file => file.BasePath == "src/App/Renamed.cs" &&
            file.CurrentPath == "src/App/Unicode λ.cs" && file.Kind == ChangeKind.Renamed);
    }

    public void Dispose() => _repository.Dispose();
}
