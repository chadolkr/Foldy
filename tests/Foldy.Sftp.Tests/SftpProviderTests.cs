using Foldy.Core.FileSystem;
using Foldy.Providers.Sftp;

namespace Foldy.Sftp.Tests;

public sealed class SftpProviderTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly SftpAuth Auth = new(SftpAuthKind.Password, "pw");

    private sealed class FakeClient(FakeFactory owner) : ISftpClient
    {
        public bool Connected { get; set; }
        public bool IsConnected => Connected;

        public void Connect(CancellationToken ct)
        {
            owner.ConnectCount++;
            Connected = true;
        }

        public IEnumerable<SftpEntryInfo> ListDirectory(string path, CancellationToken ct)
        {
            owner.ListCalls++;
            owner.ListedPaths.Add(path);
            if (owner.DropOnNextList)
            {
                owner.DropOnNextList = false;
                Connected = false;
                throw new SftpConnectionLostException("dropped");
            }

            yield return new SftpEntryInfo(".", true, false, 0, DateTime.UtcNow);
            yield return new SftpEntryInfo("..", true, false, 0, DateTime.UtcNow);
            for (var i = 0; i < owner.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                if (owner.DropAfter is { } n && i == n)
                {
                    Connected = false;
                    throw new SftpConnectionLostException("dropped mid-way");
                }

                yield return new SftpEntryInfo(
                    i % 10 == 0 ? $"dir{i}" : $"f{i}.txt", i % 10 == 0, i % 50 == 1, 100 + i, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
                if (owner.Delay > TimeSpan.Zero)
                {
                    Thread.Sleep(owner.Delay);
                }
            }
        }

        public Stream OpenRead(string path) => new MemoryStream([1, 2, 3]);

        public void Download(string path, Stream destination, Action<long> progress, CancellationToken ct)
        {
            destination.Write([1, 2, 3, 4]);
            progress(2);
            progress(4);
        }

        public void Upload(Stream source, string path, Action<long> progress, CancellationToken ct)
        {
            progress(source.Length);
            owner.Uploaded = path;
        }

        public void Dispose() => Connected = false;
    }

    private sealed class FakeFactory : ISftpClientFactory
    {
        public int Count { get; set; }
        public int ConnectCount { get; set; }
        public int ListCalls { get; set; }
        public bool DropOnNextList { get; set; }
        public int? DropAfter { get; set; }
        public TimeSpan Delay { get; set; }
        public string? Uploaded { get; set; }
        public List<string> ListedPaths { get; } = [];
        public List<SftpConnectionRequest> Requests { get; } = [];

        public ISftpClient Create(SftpConnectionRequest request)
        {
            Requests.Add(request);
            return new FakeClient(this);
        }
    }

    private static async Task<List<IReadOnlyList<FsEntry>>> Collect(SftpProvider p, string uri, CancellationToken ct)
    {
        var batches = new List<IReadOnlyList<FsEntry>>();
        await foreach (var b in p.ListBatchedAsync(new FsPath(ProviderId.Sftp, uri), ct))
        {
            batches.Add(b);
        }

        return batches;
    }

    [Fact]
    public async Task Streams_entries_in_batches_of_at_most_200_without_dot_entries()
    {
        var f = new FakeFactory { Count = 1234 };
        await using var p = new SftpProvider(f, _ => Auth);

        var batches = await Collect(p, "sftp://me@h/data", Ct);

        batches.Should().AllSatisfy(b => b.Count.Should().BeInRange(1, SftpProvider.BatchSize));
        batches.Should().HaveCountGreaterThanOrEqualTo(7);
        var all = batches.SelectMany(b => b).ToList();
        all.Should().HaveCount(1234);
        all.Select(e => e.Name).Should().NotContain([".", ".."]);
        f.ListedPaths.Should().Equal("/data");
    }

    [Fact]
    public async Task Maps_entry_fields_and_builds_child_paths()
    {
        var f = new FakeFactory { Count = 3 };
        await using var p = new SftpProvider(f, _ => Auth);

        var all = (await Collect(p, "sftp://me@h:2200/data/", Ct)).SelectMany(b => b).ToList();

        var dir = all.Single(e => e.Name == "dir0");
        dir.Kind.Should().Be(FsKind.Folder);
        dir.Size.Should().Be(0);
        dir.Path.Should().Be(new FsPath(ProviderId.Sftp, "sftp://me@h:2200/data/dir0"));
        var link = all.Single(e => e.Name == "f1.txt");
        link.Kind.Should().Be(FsKind.Link);
        var file = all.Single(e => e.Name == "f2.txt");
        file.Kind.Should().Be(FsKind.File);
        file.Size.Should().Be(102);
        file.Modified.Should().Be(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public async Task Reuses_connection_and_passes_credentials_and_handler()
    {
        var f = new FakeFactory { Count = 1 };
        Func<HostKeyCheck, HostKeyDecision> handler = _ => HostKeyDecision.Reject;
        await using var p = new SftpProvider(f, _ => Auth, handler);

        await Collect(p, "sftp://me@h/a", Ct);
        await Collect(p, "sftp://me@h/b", Ct);

        f.ConnectCount.Should().Be(1);
        f.Requests.Should().ContainSingle().Which.Auth.Should().Be(Auth);
        f.Requests[0].HostKeyHandler.Should().BeSameAs(handler);
    }

    [Fact]
    public async Task Separate_connections_for_different_users_and_hosts()
    {
        var f = new FakeFactory { Count = 1 };
        await using var p = new SftpProvider(f, _ => Auth);

        await Collect(p, "sftp://a@h/", Ct);
        await Collect(p, "sftp://b@h/", Ct);
        await Collect(p, "sftp://a@h2/", Ct);

        f.ConnectCount.Should().Be(3);
    }

    [Fact]
    public async Task Reconnects_once_when_connection_dropped_before_first_batch()
    {
        var f = new FakeFactory { Count = 5, DropOnNextList = true };
        await using var p = new SftpProvider(f, _ => Auth);

        var all = (await Collect(p, "sftp://me@h/x", Ct)).SelectMany(b => b).ToList();

        all.Should().HaveCount(5);
        f.ConnectCount.Should().Be(2);
        f.ListCalls.Should().Be(2);
    }

    [Fact]
    public async Task Reconnects_when_connection_was_closed_between_listings()
    {
        var f = new FakeFactory { Count = 2 };
        await using var p = new SftpProvider(f, _ => Auth);
        await Collect(p, "sftp://me@h/x", Ct);

        p.Disconnect(SftpLocation.Parse("sftp://me@h/"));
        (await Collect(p, "sftp://me@h/x", Ct)).SelectMany(b => b).Should().HaveCount(2);

        f.ConnectCount.Should().Be(2);
    }

    [Fact]
    public async Task Drop_after_data_was_emitted_is_reported_not_retried()
    {
        var f = new FakeFactory { Count = 1000, DropAfter = 400 };
        await using var p = new SftpProvider(f, _ => Auth);

        var act = async () => await Collect(p, "sftp://me@h/x", Ct);

        await act.Should().ThrowAsync<SftpConnectionLostException>();
        f.ConnectCount.Should().Be(1);
    }

    [Fact]
    public async Task Persistent_failure_after_one_retry_throws()
    {
        // 재연결해도 계속 끊기는 서버: 한 번만 재시도하고 예외를 올린다.
        await using var p = new SftpProvider(new AlwaysDropFactory(), _ => Auth);

        var act = async () => await Collect(p, "sftp://me@h/x", Ct);

        await act.Should().ThrowAsync<SftpConnectionLostException>();
    }

    private sealed class AlwaysDropFactory : ISftpClientFactory
    {
        public ISftpClient Create(SftpConnectionRequest request) => new DropClient();

        private sealed class DropClient : ISftpClient
        {
            private bool _c;
            public bool IsConnected => _c;
            public void Connect(CancellationToken ct) => _c = true;
            public IEnumerable<SftpEntryInfo> ListDirectory(string path, CancellationToken ct) =>
                throw new SftpConnectionLostException("always");
            public Stream OpenRead(string path) => throw new SftpConnectionLostException("always");
            public void Download(string path, Stream destination, Action<long> progress, CancellationToken ct) { }
            public void Upload(Stream source, string path, Action<long> progress, CancellationToken ct) { }
            public void Dispose() => _c = false;
        }
    }

    [Fact]
    public async Task Cancellation_stops_streaming()
    {
        var f = new FakeFactory { Count = 100_000, Delay = TimeSpan.FromMilliseconds(1) };
        await using var p = new SftpProvider(f, _ => Auth);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        var act = async () =>
        {
            await foreach (var _ in p.ListBatchedAsync(new FsPath(ProviderId.Sftp, "sftp://me@h/x"), cts.Token))
            {
                await cts.CancelAsync();
            }
        };

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Missing_credentials_fail_clearly()
    {
        await using var p = new SftpProvider(new FakeFactory(), _ => null);
        var act = async () => await Collect(p, "sftp://me@h/x", Ct);
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Rejects_non_sftp_paths()
    {
        await using var p = new SftpProvider(new FakeFactory(), _ => Auth);
        var act = async () => await Collect(p, "C:\\x", Ct).ConfigureAwait(false);
        // 잘못된 Provider ID로 만든 경로
        var bad = async () =>
        {
            await foreach (var _ in p.ListBatchedAsync(FsPath.Local(@"C:\x"), Ct))
            {
            }
        };
        await bad.Should().ThrowAsync<ArgumentException>();
        await act.Should().ThrowAsync<FormatException>();
    }

    [Fact]
    public async Task OpenRead_download_and_upload_helpers_report_progress()
    {
        var f = new FakeFactory();
        await using var p = new SftpProvider(f, _ => Auth);
        var path = new FsPath(ProviderId.Sftp, "sftp://me@h/file.bin");

        await using (var s = await p.OpenReadAsync(path, Ct))
        {
            s.Length.Should().Be(3);
        }

        var reports = new List<long>();
        using var dest = new MemoryStream();
        await p.DownloadAsync(path, dest, new SyncProgress(reports.Add), Ct);
        dest.ToArray().Should().Equal(1, 2, 3, 4);
        reports.Should().Equal(2, 4);

        var up = new List<long>();
        await p.UploadAsync(new MemoryStream(new byte[10]), new FsPath(ProviderId.Sftp, "sftp://me@h/up.bin"), new SyncProgress(up.Add), Ct);
        up.Should().Equal(10);
        f.Uploaded.Should().Be("/up.bin");
    }

    private sealed class SyncProgress(Action<long> a) : IProgress<long>
    {
        public void Report(long value) => a(value);
    }
}
