using Foldy.Core.FileSystem;
using Foldy.Core.Threading;
using Foldy.Providers.Local;

namespace Foldy.Core.Tests;

public sealed class LocalProviderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "foldy-lp-" + Guid.NewGuid().ToString("N"));

    public LocalProviderTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, true);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Streams_all_entries_in_batches_of_at_most_200()
    {
        for (var i = 0; i < 1234; i++)
        {
            await File.WriteAllTextAsync(Path.Combine(_dir, $"f{i:D4}.txt"), "x", Ct);
        }

        Directory.CreateDirectory(Path.Combine(_dir, "sub"));
        await using var reg = new DriveQueueRegistry();
        var p = new LocalProvider(reg);

        var batches = new List<IReadOnlyList<FsEntry>>();
        await foreach (var b in p.ListBatchedAsync(FsPath.Local(_dir), Ct))
        {
            batches.Add(b);
        }

        batches.Should().AllSatisfy(b => b.Count.Should().BeInRange(1, LocalProvider.BatchSize));
        var all = batches.SelectMany(b => b).ToList();
        all.Should().HaveCount(1235);
        all.Single(e => e.Name == "sub").Kind.Should().Be(FsKind.Folder);
        all.First(e => e.Name == "f0000.txt").Size.Should().Be(1);
    }

    [Fact]
    public async Task Missing_folder_throws_IOException_not_hang()
    {
        await using var reg = new DriveQueueRegistry();
        var p = new LocalProvider(reg);
        var act = async () =>
        {
            await foreach (var _ in p.ListBatchedAsync(FsPath.Local(Path.Combine(_dir, "nope")), Ct))
            {
            }
        };
        await act.Should().ThrowAsync<DirectoryNotFoundException>();
    }

    [Fact]
    public async Task Cancellation_stops_enumeration()
    {
        for (var i = 0; i < 2000; i++)
        {
            await File.WriteAllTextAsync(Path.Combine(_dir, $"f{i:D4}.txt"), "", Ct);
        }

        await using var reg = new DriveQueueRegistry();
        var p = new LocalProvider(reg);
        using var cts = new CancellationTokenSource();
        var seen = 0;
        var act = async () =>
        {
            await foreach (var b in p.ListBatchedAsync(FsPath.Local(_dir), cts.Token))
            {
                seen += b.Count;
                await cts.CancelAsync();
            }
        };
        await act.Should().ThrowAsync<OperationCanceledException>();
        seen.Should().BeLessThan(2000);
    }
}
