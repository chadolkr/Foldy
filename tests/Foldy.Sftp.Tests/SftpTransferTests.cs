using Foldy.Presentation.Services;
using Foldy.Providers.Sftp;

namespace Foldy.Sftp.Tests;

public sealed class SftpTransferTests : IDisposable
{
    private const string Root = "sftp://me@h/";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "foldy-sftp-test-" + Guid.NewGuid().ToString("N"));
    private readonly MemoryRemote _remote = new();

    public SftpTransferTests() => Directory.CreateDirectory(_tmp);

    public void Dispose() => Directory.Delete(_tmp, recursive: true);

    // ───────────── 라우팅·이름 규칙 ─────────────

    [Theory]
    [InlineData(new[] { @"C:\a" }, @"D:\b", TransferRoute.Local)]
    [InlineData(new[] { @"C:\a" }, "sftp://u@h/x", TransferRoute.Upload)]
    [InlineData(new[] { "sftp://u@h/a" }, @"D:\b", TransferRoute.Download)]
    [InlineData(new[] { "sftp://u@h/a", "sftp://u@h/b" }, "sftp://u@h2/x", TransferRoute.Remote)]
    [InlineData(new[] { @"C:\a", "sftp://u@h/a" }, @"D:\b", TransferRoute.Mixed)]
    [InlineData(new[] { @"C:\a", "sftp://u@h/a" }, "sftp://u@h/x", TransferRoute.Mixed)]
    public void Classifies_routes(string[] sources, string dest, TransferRoute expected) =>
        TransferRouting.Classify(sources, dest).Should().Be(expected);

    [Fact]
    public void Same_connection_compares_user_host_port()
    {
        TransferRouting.SameConnection("sftp://u@h/a", "sftp://u@H/b").Should().BeTrue();
        TransferRouting.SameConnection("sftp://u@h/a", "sftp://u@h:2200/b").Should().BeFalse();
        TransferRouting.SameConnection("sftp://u@h/a", "sftp://v@h/b").Should().BeFalse();
    }

    [Theory]
    [InlineData("a.txt", 2, false, "a (2).txt")]
    [InlineData("a.tar.gz", 3, false, "a.tar (3).gz")]
    [InlineData("noext", 2, false, "noext (2)")]
    [InlineData(".bashrc", 2, false, ".bashrc (2)")]
    [InlineData("dir.v1", 2, true, "dir.v1 (2)")]
    public void Names_conflicts_with_a_counter(string name, int n, bool folder, string expected) =>
        ConflictNaming.Candidate(name, n, folder).Should().Be(expected);

    [Fact]
    public async Task Unique_name_skips_taken_candidates()
    {
        var taken = new HashSet<string> { "a (2).txt", "a (3).txt" };
        var name = await ConflictNaming.UniqueAsync("a.txt", false, c => Task.FromResult(taken.Contains(c)));
        name.Should().Be("a (4).txt");
    }

    [Fact]
    public async Task Pure_local_operations_go_to_the_inner_service()
    {
        var inner = new InnerRecorder();
        await using var p = NewProvider();
        var svc = new SftpAwareFileOperationService(inner, p);

        await svc.CopyAsync([@"C:\a"], @"D:\b", null, Ct);
        await svc.MoveAsync([@"C:\a"], @"D:\b", null, Ct);
        await svc.DeleteAsync([@"C:\a"], true, null, Ct);
        await svc.RenameAsync(@"C:\a", "x", Ct);
        await svc.NewFolderAsync(@"C:\", "n", Ct);

        inner.Calls.Should().Equal("copy", "move", "delete", "rename", "newfolder");
        _remote.Ops.Should().BeEmpty();
    }

    // ───────────── 업로드 / 다운로드 ─────────────

    [Fact]
    public async Task Uploads_folders_recursively_and_reports_bytes()
    {
        var src = Path.Combine(_tmp, "proj");
        Directory.CreateDirectory(Path.Combine(src, "sub", "deep"));
        File.WriteAllBytes(Path.Combine(src, "a.bin"), new byte[100]);
        File.WriteAllBytes(Path.Combine(src, "sub", "b.bin"), new byte[250]);
        File.WriteAllBytes(Path.Combine(src, "sub", "deep", "c.bin"), new byte[50]);
        _remote.AddDir("/up");
        var reports = new List<FileOpProgress>();
        await using var p = NewProvider();
        var svc = new SftpAwareFileOperationService(new InnerRecorder(), p, new SftpTransferOptions { ProgressInterval = TimeSpan.Zero });

        var r = await svc.CopyAsync([src], Root + "up", new SyncProgress<FileOpProgress>(reports.Add), Ct);

        r.Succeeded.Should().BeTrue(r.Error);
        _remote.IsDir("/up/proj/sub/deep").Should().BeTrue();
        _remote.Length("/up/proj/a.bin").Should().Be(100);
        _remote.Length("/up/proj/sub/b.bin").Should().Be(250);
        _remote.Length("/up/proj/sub/deep/c.bin").Should().Be(50);
        Directory.Exists(src).Should().BeTrue("복사는 원본을 남긴다");
        reports.Should().NotBeEmpty();
        reports.Select(x => x.Total).Distinct().Should().Equal(400);
        reports[^1].Done.Should().Be(400);
        reports.Select(x => x.Done).Should().BeInAscendingOrder();
        reports.Select(x => x.CurrentItem).Should().Contain("b.bin");
    }

    [Fact]
    public async Task Downloads_folders_recursively()
    {
        _remote.AddFile("/data/set/x.txt", [1, 2, 3]);
        _remote.AddFile("/data/set/in/y.txt", [9, 9]);
        var dest = Path.Combine(_tmp, "out");
        Directory.CreateDirectory(dest);
        await using var p = NewProvider();
        var svc = new SftpAwareFileOperationService(new InnerRecorder(), p);

        var r = await svc.CopyAsync([Root + "data/set"], dest, null, Ct);

        r.Succeeded.Should().BeTrue(r.Error);
        File.ReadAllBytes(Path.Combine(dest, "set", "x.txt")).Should().Equal(1, 2, 3);
        File.ReadAllBytes(Path.Combine(dest, "set", "in", "y.txt")).Should().Equal(9, 9);
        _remote.Exists("/data/set/x.txt").Should().BeTrue();
    }

    [Fact]
    public async Task Move_to_sftp_deletes_local_sources_only_after_success()
    {
        var f = Path.Combine(_tmp, "m.txt");
        File.WriteAllText(f, "hello");
        var dir = Path.Combine(_tmp, "md");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "z.txt"), "z");
        _remote.AddDir("/d");
        await using var p = NewProvider();
        var svc = new SftpAwareFileOperationService(new InnerRecorder(), p);

        var r = await svc.MoveAsync([f, dir], Root + "d", null, Ct);

        r.Succeeded.Should().BeTrue(r.Error);
        File.Exists(f).Should().BeFalse();
        Directory.Exists(dir).Should().BeFalse();
        _remote.Exists("/d/m.txt").Should().BeTrue();
        _remote.Exists("/d/md/z.txt").Should().BeTrue();
    }

    [Fact]
    public async Task Failed_move_keeps_every_source()
    {
        var a = Path.Combine(_tmp, "a.txt");
        var b = Path.Combine(_tmp, "b.txt");
        File.WriteAllText(a, "a");
        File.WriteAllText(b, "b");
        _remote.AddDir("/d");
        _remote.FailUploadOf = "/d/.b.txt.foldy-part";
        await using var p = NewProvider();
        var svc = new SftpAwareFileOperationService(new InnerRecorder(), p);

        var r = await svc.MoveAsync([a, b], Root + "d", null, Ct);

        r.Succeeded.Should().BeFalse();
        File.Exists(a).Should().BeTrue("모두 성공하기 전에는 원본을 지우지 않는다");
        File.Exists(b).Should().BeTrue();
        _remote.Exists("/d/b.txt").Should().BeFalse();
        _remote.Exists("/d/.b.txt.foldy-part").Should().BeFalse("반쯤 쓴 임시 파일은 지운다");
    }

    [Fact]
    public async Task Move_from_sftp_deletes_remote_sources()
    {
        _remote.AddFile("/s/k.txt", [5]);
        _remote.AddFile("/s/dir/q.txt", [6]);
        await using var p = NewProvider();
        var svc = new SftpAwareFileOperationService(new InnerRecorder(), p);

        var r = await svc.MoveAsync([Root + "s/k.txt", Root + "s/dir"], _tmp, null, Ct);

        r.Succeeded.Should().BeTrue(r.Error);
        File.Exists(Path.Combine(_tmp, "k.txt")).Should().BeTrue();
        File.Exists(Path.Combine(_tmp, "dir", "q.txt")).Should().BeTrue();
        _remote.Exists("/s/k.txt").Should().BeFalse();
        _remote.Exists("/s/dir").Should().BeFalse();
    }

    // ───────────── SFTP → SFTP ─────────────

    [Fact]
    public async Task Move_inside_one_connection_is_a_server_side_rename()
    {
        _remote.AddFile("/a/big.bin", new byte[1000]);
        _remote.AddDir("/b");
        await using var p = NewProvider();
        var svc = new SftpAwareFileOperationService(new InnerRecorder(), p);

        var r = await svc.MoveAsync([Root + "a/big.bin"], Root + "b", null, Ct);

        r.Succeeded.Should().BeTrue(r.Error);
        _remote.Exists("/b/big.bin").Should().BeTrue();
        _remote.Exists("/a/big.bin").Should().BeFalse();
        _remote.Ops.Should().Contain("rename:/a/big.bin>/b/big.bin");
        _remote.Ops.Should().NotContain(o => o.StartsWith("upload:", StringComparison.Ordinal) || o.StartsWith("download:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Copy_inside_one_connection_streams_through_a_temp_file()
    {
        _remote.AddFile("/a/f.txt", [1, 2, 3, 4]);
        _remote.AddDir("/b");
        var reports = new List<FileOpProgress>();
        await using var p = NewProvider();
        var svc = new SftpAwareFileOperationService(new InnerRecorder(), p, new SftpTransferOptions { ProgressInterval = TimeSpan.Zero });

        var r = await svc.CopyAsync([Root + "a/f.txt"], Root + "b", new SyncProgress<FileOpProgress>(reports.Add), Ct);

        r.Succeeded.Should().BeTrue(r.Error);
        _remote.Data("/b/f.txt").Should().Equal(1, 2, 3, 4);
        _remote.Exists("/a/f.txt").Should().BeTrue();
        _remote.Ops.Should().ContainInOrder("download:/a/f.txt", "upload:/b/.f.txt.foldy-part", "replace:/b/.f.txt.foldy-part>/b/f.txt");
        reports[^1].Done.Should().Be(reports[^1].Total);
    }

    [Fact]
    public async Task Refuses_to_move_a_folder_into_itself()
    {
        _remote.AddFile("/a/x.txt", [1]);
        await using var p = NewProvider();
        var svc = new SftpAwareFileOperationService(new InnerRecorder(), p);

        var r = await svc.CopyAsync([Root + "a"], Root + "a/sub", null, Ct);

        r.Succeeded.Should().BeFalse();
    }

    // ───────────── 충돌 ─────────────

    private async Task<(FileOpResult Result, List<TransferConflict> Asked)> UploadWithConflict(
        Func<TransferConflict, TransferConflictDecision>? decide, params string[] names)
    {
        var asked = new List<TransferConflict>();
        foreach (var n in names)
        {
            File.WriteAllText(Path.Combine(_tmp, n), "NEW");
            _remote.AddFile("/c/" + n, [1]);
        }

        var options = new SftpTransferOptions
        {
            ResolveConflict = decide is null ? null : (c, _) => { asked.Add(c); return Task.FromResult(decide(c)); },
        };
        await using var p = NewProvider();
        var svc = new SftpAwareFileOperationService(new InnerRecorder(), p, options);
        var r = await svc.CopyAsync([.. names.Select(n => Path.Combine(_tmp, n))], Root + "c", null, Ct);
        return (r, asked);
    }

    [Fact]
    public async Task Conflict_overwrite_replaces_the_remote_file()
    {
        var (r, asked) = await UploadWithConflict(_ => new(TransferConflictAction.Overwrite), "a.txt");
        r.Succeeded.Should().BeTrue(r.Error);
        asked.Should().ContainSingle();
        _remote.Data("/c/a.txt").Should().Equal("NEW"u8.ToArray());
    }

    [Fact]
    public async Task Conflict_skip_keeps_the_remote_file()
    {
        var (r, _) = await UploadWithConflict(_ => new(TransferConflictAction.Skip), "a.txt");
        r.Succeeded.Should().BeTrue(r.Error);
        _remote.Data("/c/a.txt").Should().Equal(1);
    }

    [Fact]
    public async Task Conflict_keep_both_uses_a_numbered_name()
    {
        var (r, _) = await UploadWithConflict(_ => new(TransferConflictAction.Rename), "a.txt");
        r.Succeeded.Should().BeTrue(r.Error);
        _remote.Data("/c/a.txt").Should().Equal(1);
        _remote.Data("/c/a (2).txt").Should().Equal("NEW"u8.ToArray());
    }

    [Fact]
    public async Task Apply_to_all_asks_only_once()
    {
        var (r, asked) = await UploadWithConflict(_ => new(TransferConflictAction.Overwrite, ApplyToAll: true), "a.txt", "b.txt", "c.txt");
        r.Succeeded.Should().BeTrue(r.Error);
        asked.Should().ContainSingle();
        _remote.Data("/c/c.txt").Should().Equal("NEW"u8.ToArray());
    }

    [Fact]
    public async Task Without_a_resolver_conflicts_keep_both()
    {
        var (r, _) = await UploadWithConflict(null, "a.txt");
        r.Succeeded.Should().BeTrue(r.Error);
        _remote.Data("/c/a.txt").Should().Equal(1);
        _remote.Exists("/c/a (2).txt").Should().BeTrue();
    }

    [Fact]
    public async Task Skipped_item_is_not_deleted_by_a_move()
    {
        File.WriteAllText(Path.Combine(_tmp, "s.txt"), "x");
        _remote.AddFile("/c/s.txt", [1]);
        await using var p = NewProvider();
        var svc = new SftpAwareFileOperationService(new InnerRecorder(), p,
            new SftpTransferOptions { ResolveConflict = (_, _) => Task.FromResult(new TransferConflictDecision(TransferConflictAction.Skip)) });

        var r = await svc.MoveAsync([Path.Combine(_tmp, "s.txt")], Root + "c", null, Ct);

        r.Succeeded.Should().BeTrue(r.Error);
        File.Exists(Path.Combine(_tmp, "s.txt")).Should().BeTrue();
    }

    [Fact]
    public async Task Existing_folders_are_merged_without_asking()
    {
        var src = Path.Combine(_tmp, "mix");
        Directory.CreateDirectory(src);
        File.WriteAllText(Path.Combine(src, "new.txt"), "n");
        _remote.AddFile("/c/mix/old.txt", [7]);
        var asked = 0;
        await using var p = NewProvider();
        var svc = new SftpAwareFileOperationService(new InnerRecorder(), p,
            new SftpTransferOptions { ResolveConflict = (_, _) => { asked++; return Task.FromResult(default(TransferConflictDecision)); } });

        var r = await svc.CopyAsync([src], Root + "c", null, Ct);

        r.Succeeded.Should().BeTrue(r.Error);
        asked.Should().Be(0);
        _remote.Exists("/c/mix/old.txt").Should().BeTrue();
        _remote.Exists("/c/mix/new.txt").Should().BeTrue();
    }

    // ───────────── 취소 ─────────────

    [Fact]
    public async Task Cancel_during_upload_removes_the_partial_remote_file()
    {
        var f = Path.Combine(_tmp, "big.bin");
        File.WriteAllBytes(f, new byte[500]);
        _remote.AddDir("/d");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        _remote.OnUploadStarted = cts.Cancel;
        await using var p = NewProvider();
        var svc = new SftpAwareFileOperationService(new InnerRecorder(), p);

        var r = await svc.CopyAsync([f], Root + "d", null, cts.Token);

        r.Aborted.Should().BeTrue();
        _remote.Exists("/d/big.bin").Should().BeFalse();
        _remote.Exists("/d/.big.bin.foldy-part").Should().BeFalse();
    }

    [Fact]
    public async Task Cancel_during_overwrite_upload_keeps_the_old_remote_content()
    {
        var f = Path.Combine(_tmp, "o.bin");
        File.WriteAllBytes(f, new byte[500]);
        _remote.AddFile("/d/o.bin", [7, 7, 7]);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        _remote.OnUploadStarted = cts.Cancel;
        await using var p = NewProvider();
        var svc = new SftpAwareFileOperationService(new InnerRecorder(), p,
            new SftpTransferOptions { ResolveConflict = (_, _) => Task.FromResult(new TransferConflictDecision(TransferConflictAction.Overwrite)) });

        var r = await svc.CopyAsync([f], Root + "d", null, cts.Token);

        r.Aborted.Should().BeTrue();
        _remote.Data("/d/o.bin").Should().Equal(7, 7, 7);
        _remote.Exists("/d/.o.bin.foldy-part").Should().BeFalse();
    }

    [Fact]
    public async Task Failed_overwrite_upload_keeps_the_old_remote_content()
    {
        var f = Path.Combine(_tmp, "o2.bin");
        File.WriteAllBytes(f, new byte[500]);
        _remote.AddFile("/d/o2.bin", [7]);
        _remote.FailUploadOf = "/d/.o2.bin.foldy-part";
        await using var p = NewProvider();
        var svc = new SftpAwareFileOperationService(new InnerRecorder(), p,
            new SftpTransferOptions { ResolveConflict = (_, _) => Task.FromResult(new TransferConflictDecision(TransferConflictAction.Overwrite)) });

        var r = await svc.CopyAsync([f], Root + "d", null, Ct);

        r.Succeeded.Should().BeFalse();
        _remote.Data("/d/o2.bin").Should().Equal(7);
        _remote.Exists("/d/.o2.bin.foldy-part").Should().BeFalse();
    }

    [Fact]
    public async Task Cancel_during_overwrite_download_keeps_the_old_local_file()
    {
        _remote.AddFile("/s/k.bin", new byte[500]);
        File.WriteAllBytes(Path.Combine(_tmp, "k.bin"), [1, 2, 3]);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        _remote.OnDownloadStarted = cts.Cancel;
        await using var p = NewProvider();
        var svc = new SftpAwareFileOperationService(new InnerRecorder(), p,
            new SftpTransferOptions { ResolveConflict = (_, _) => Task.FromResult(new TransferConflictDecision(TransferConflictAction.Overwrite)) });

        var r = await svc.CopyAsync([Root + "s/k.bin"], _tmp, null, cts.Token);

        r.Aborted.Should().BeTrue();
        File.ReadAllBytes(Path.Combine(_tmp, "k.bin")).Should().Equal(1, 2, 3);
        File.Exists(Path.Combine(_tmp, ".k.bin.foldy-part")).Should().BeFalse();
    }

    [Fact]
    public async Task Overwrite_replaces_the_old_content_once_the_copy_is_complete()
    {
        _remote.AddFile("/s/n.bin", [9, 9]);
        File.WriteAllBytes(Path.Combine(_tmp, "n.bin"), [1]);
        await using var p = NewProvider();
        var svc = new SftpAwareFileOperationService(new InnerRecorder(), p,
            new SftpTransferOptions { ResolveConflict = (_, _) => Task.FromResult(new TransferConflictDecision(TransferConflictAction.Overwrite)) });

        var r = await svc.CopyAsync([Root + "s/n.bin"], _tmp, null, Ct);

        r.Succeeded.Should().BeTrue(r.Error);
        File.ReadAllBytes(Path.Combine(_tmp, "n.bin")).Should().Equal(9, 9);
        Directory.GetFiles(_tmp, "*.foldy-part").Should().BeEmpty();
    }

    [Fact]
    public async Task Skipped_symlinks_are_reported_and_keep_the_source_on_move()
    {
        _remote.AddFile("/s/dir/real.txt", [1]);
        _remote.AddLink("/s/dir/link");
        _remote.AddLink("/s/dir/link2");
        await using var p = NewProvider();
        var svc = new SftpAwareFileOperationService(new InnerRecorder(), p);

        var r = await svc.MoveAsync([Root + "s/dir"], _tmp, null, Ct);

        r.Succeeded.Should().BeTrue();
        r.Error.Should().Be("심볼릭 링크 2개는 건너뛰었습니다");
        File.Exists(Path.Combine(_tmp, "dir", "real.txt")).Should().BeTrue();
        _remote.Exists("/s/dir/link").Should().BeTrue("링크가 든 원본은 지우지 않는다");
    }

    [Fact]
    public async Task Cancel_during_download_removes_the_partial_local_file()
    {
        _remote.AddFile("/s/big.bin", new byte[500]);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        _remote.OnDownloadStarted = cts.Cancel;
        await using var p = NewProvider();
        var svc = new SftpAwareFileOperationService(new InnerRecorder(), p);

        var r = await svc.CopyAsync([Root + "s/big.bin"], _tmp, null, cts.Token);

        r.Aborted.Should().BeTrue();
        File.Exists(Path.Combine(_tmp, "big.bin")).Should().BeFalse();
    }

    // ───────────── 삭제 / 이름 바꾸기 / 새 폴더 ─────────────

    [Fact]
    public async Task Delete_is_recursive()
    {
        _remote.AddFile("/x/t/a.txt", [1]);
        _remote.AddFile("/x/t/n/b.txt", [2]);
        _remote.AddFile("/x/keep.txt", [3]);
        await using var p = NewProvider();
        var svc = new SftpAwareFileOperationService(new InnerRecorder(), p);

        var r = await svc.DeleteAsync([Root + "x/t"], recycle: true, null, Ct);

        r.Succeeded.Should().BeTrue(r.Error);
        _remote.Exists("/x/t").Should().BeFalse();
        _remote.Exists("/x/keep.txt").Should().BeTrue();
    }

    [Fact]
    public async Task Rename_works_and_refuses_existing_names()
    {
        _remote.AddFile("/r/old.txt", [1]);
        _remote.AddFile("/r/taken.txt", [2]);
        await using var p = NewProvider();
        var svc = new SftpAwareFileOperationService(new InnerRecorder(), p);

        (await svc.RenameAsync(Root + "r/old.txt", "taken.txt", Ct)).Succeeded.Should().BeFalse();
        var ok = await svc.RenameAsync(Root + "r/old.txt", "new.txt", Ct);

        ok.Succeeded.Should().BeTrue(ok.Error);
        _remote.Exists("/r/new.txt").Should().BeTrue();
        _remote.Exists("/r/old.txt").Should().BeFalse();
    }

    [Fact]
    public async Task New_folder_picks_an_unused_name()
    {
        _remote.AddDir("/n/새 폴더");
        await using var p = NewProvider();
        var svc = new SftpAwareFileOperationService(new InnerRecorder(), p);

        var r = await svc.NewFolderAsync(Root + "n", "새 폴더", Ct);

        r.Succeeded.Should().BeTrue(r.Error);
        r.CreatedPath.Should().EndWith("/n/" + "새 폴더 (2)");
        _remote.IsDir("/n/새 폴더 (2)").Should().BeTrue();
    }

    // ───────────── 준비물 ─────────────

    private SftpProvider NewProvider() => new(new MemoryFactory(_remote), _ => new SftpAuth(SftpAuthKind.Password, "pw"));

    private sealed class SyncProgress<T>(Action<T> on) : IProgress<T>
    {
        public void Report(T value) => on(value);
    }

    private sealed class InnerRecorder : IFileOperationService
    {
        public List<string> Calls { get; } = [];

        public Task<FileOpResult> CopyAsync(IReadOnlyList<string> sources, string destinationFolder, IProgress<FileOpProgress>? progress, CancellationToken ct) => Rec("copy");

        public Task<FileOpResult> MoveAsync(IReadOnlyList<string> sources, string destinationFolder, IProgress<FileOpProgress>? progress, CancellationToken ct) => Rec("move");

        public Task<FileOpResult> DeleteAsync(IReadOnlyList<string> paths, bool recycle, IProgress<FileOpProgress>? progress, CancellationToken ct) => Rec("delete");

        public Task<FileOpResult> RenameAsync(string path, string newName, CancellationToken ct) => Rec("rename");

        public Task<FileOpResult> NewFolderAsync(string parent, string name, CancellationToken ct) => Rec("newfolder");

        private Task<FileOpResult> Rec(string s)
        {
            Calls.Add(s);
            return Task.FromResult(FileOpResult.Ok());
        }
    }

    /// <summary>메모리 안의 원격 파일 시스템(POSIX 경로). 폴더는 null 데이터.</summary>
    private sealed class MemoryRemote
    {
        private readonly Dictionary<string, byte[]?> _nodes = new(StringComparer.Ordinal) { ["/"] = null };
        private readonly object _lock = new();

        public List<string> Ops { get; } = [];

        public string? FailUploadOf { get; set; }

        public Action? OnUploadStarted { get; set; }

        public Action? OnDownloadStarted { get; set; }

        public void AddDir(string path)
        {
            lock (_lock)
            {
                var cur = "";
                foreach (var part in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
                {
                    cur += "/" + part;
                    _nodes.TryAdd(cur, null);
                }
            }
        }

        private readonly HashSet<string> _links = [];

        public void AddLink(string path)
        {
            AddFile(path, [0]);
            lock (_lock)
            {
                _links.Add(path);
            }
        }

        public void AddFile(string path, byte[] data)
        {
            AddDir(path[..path.LastIndexOf('/')]);
            lock (_lock)
            {
                _nodes[path] = data;
            }
        }

        public bool Exists(string path)
        {
            lock (_lock)
            {
                return _nodes.ContainsKey(path);
            }
        }

        public bool IsDir(string path)
        {
            lock (_lock)
            {
                return _nodes.TryGetValue(path, out var d) && d is null;
            }
        }

        public long Length(string path) => Data(path).Length;

        public byte[] Data(string path)
        {
            lock (_lock)
            {
                return _nodes[path] ?? throw new InvalidOperationException("폴더");
            }
        }

        public IEnumerable<SftpEntryInfo> List(string path)
        {
            lock (_lock)
            {
                var prefix = path == "/" ? "/" : path + "/";
                return [.. _nodes
                    .Where(kv => kv.Key.Length > prefix.Length && kv.Key.StartsWith(prefix, StringComparison.Ordinal) && !kv.Key[prefix.Length..].Contains('/', StringComparison.Ordinal))
                    .Select(kv => new SftpEntryInfo(kv.Key[prefix.Length..], kv.Value is null, _links.Contains(kv.Key), kv.Value?.Length ?? 0, DateTime.UtcNow))];
            }
        }

        public SftpEntryInfo? Stat(string path)
        {
            lock (_lock)
            {
                return _nodes.TryGetValue(path, out var d) ? new SftpEntryInfo(path[(path.LastIndexOf('/') + 1)..], d is null, false, d?.Length ?? 0, DateTime.UtcNow) : null;
            }
        }

        public void Put(string path, byte[] data)
        {
            lock (_lock)
            {
                var parent = path[..path.LastIndexOf('/')];
                if (parent.Length > 0 && !_nodes.ContainsKey(parent))
                {
                    throw new IOException("부모 폴더가 없습니다: " + parent);
                }

                _nodes[path] = data;
            }
        }

        public void Mkdir(string path)
        {
            lock (_lock)
            {
                var parent = path[..path.LastIndexOf('/')];
                if (parent.Length > 0 && !_nodes.ContainsKey(parent))
                {
                    throw new IOException("부모 폴더가 없습니다: " + parent);
                }

                if (!_nodes.TryAdd(path, null))
                {
                    throw new IOException("이미 있습니다: " + path);
                }
            }
        }

        public void Remove(string path, bool dir)
        {
            lock (_lock)
            {
                if (!_nodes.TryGetValue(path, out var d) || (d is null) != dir)
                {
                    throw new IOException("지울 수 없습니다: " + path);
                }

                if (dir && _nodes.Keys.Any(k => k.StartsWith(path + "/", StringComparison.Ordinal)))
                {
                    throw new IOException("폴더가 비어 있지 않습니다: " + path);
                }

                _nodes.Remove(path);
            }
        }

        public void Move(string from, string to)
        {
            lock (_lock)
            {
                if (_nodes.ContainsKey(to) || !_nodes.TryGetValue(from, out var d))
                {
                    throw new IOException("이름을 바꿀 수 없습니다");
                }

                _nodes.Remove(from);
                _nodes[to] = d;
            }
        }
    }

    private sealed class MemoryFactory(MemoryRemote remote) : ISftpClientFactory
    {
        public ISftpClient Create(SftpConnectionRequest request) => new MemoryClient(remote);
    }

    private sealed class MemoryClient(MemoryRemote r) : ISftpClient
    {
        private bool _connected;

        public bool IsConnected => _connected;

        public void Connect(CancellationToken ct) => _connected = true;

        public IEnumerable<SftpEntryInfo> ListDirectory(string path, CancellationToken ct) => r.List(path);

        public Stream OpenRead(string path) => new MemoryStream(r.Data(path));

        public void Download(string path, Stream destination, Action<long> progress, CancellationToken ct)
        {
            lock (r.Ops)
            {
                r.Ops.Add("download:" + path);
            }

            var data = r.Data(path);
            destination.Write(data, 0, data.Length / 2);
            r.OnDownloadStarted?.Invoke();
            ct.ThrowIfCancellationRequested();
            destination.Write(data, data.Length / 2, data.Length - data.Length / 2);
            progress(data.Length);
        }

        public void Upload(Stream source, string path, Action<long> progress, CancellationToken ct)
        {
            lock (r.Ops)
            {
                r.Ops.Add("upload:" + path);
            }

            using var ms = new MemoryStream();
            source.CopyTo(ms);
            var data = ms.ToArray();
            r.Put(path, data[..(data.Length / 2)]); // 반쯤 쓴 상태가 보인다
            r.OnUploadStarted?.Invoke();
            ct.ThrowIfCancellationRequested();
            if (r.FailUploadOf == path)
            {
                throw new IOException("업로드 실패");
            }

            r.Put(path, data);
            progress(data.Length);
        }

        public SftpEntryInfo? Stat(string path) => r.Stat(path);

        public void CreateDirectory(string path) => r.Mkdir(path);

        public void DeleteFile(string path) => r.Remove(path, dir: false);

        public void DeleteDirectory(string path) => r.Remove(path, dir: true);

        public void Rename(string from, string to)
        {
            lock (r.Ops)
            {
                r.Ops.Add($"rename:{from}>{to}");
            }

            r.Move(from, to);
        }

        public void Replace(string source, string target)
        {
            lock (r.Ops)
            {
                r.Ops.Add($"replace:{source}>{target}");
            }

            if (r.Exists(target))
            {
                r.Remove(target, dir: false);
            }

            r.Move(source, target);
        }

        public void Dispose() => _connected = false;
    }
}
