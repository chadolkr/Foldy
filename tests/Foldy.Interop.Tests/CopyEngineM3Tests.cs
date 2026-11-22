using System.Diagnostics;
using Foldy.Copy.Native;
using Foldy.Presentation.Services;

namespace Foldy.Interop.Tests;

/// <summary>M3 병렬 복사 엔진: 폴더 트리, 긴 경로, 충돌, 저널 이어하기, 이동 검증, IFileOperationService 어댑터.</summary>
public sealed class CopyEngineM3Tests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "foldy-m3-" + Guid.NewGuid().ToString("N"));

    public CopyEngineM3Tests()
    {
        Directory.CreateDirectory(_dir);
        NativeRequirement.Ensure(NativeCopyEngine.IsAvailable, "foldy_copy.dll (" + NativeCopyEngine.UnavailableReason + ")");
    }

    public void Dispose()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(_dir, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(f, FileAttributes.Normal);
            }

            Directory.Delete(_dir, true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static NativeProgress WaitUntil(ICopyJob job, Func<NativeProgress, bool> cond, TimeSpan? timeout = null)
    {
        var sw = Stopwatch.StartNew();
        NativeProgress p;
        while (!cond(p = job.Poll()))
        {
            if (sw.Elapsed > (timeout ?? TimeSpan.FromSeconds(60)))
            {
                throw new TimeoutException(p.ToString());
            }

            Thread.Sleep(5);
        }

        return p;
    }

    private static NativeProgress WaitDone(ICopyJob job) => WaitUntil(job, p => p.IsTerminal);

    private string MakeTree(string name)
    {
        var root = Path.Combine(_dir, name);
        var deep = root;
        for (var i = 0; i < 5; i++)
        {
            deep = Path.Combine(deep, $"{i}-한글 폴더 이름-" + new string('가', 50));
        }

        Directory.CreateDirectory(deep);
        Directory.CreateDirectory(Path.Combine(root, "빈 폴더"));
        File.WriteAllText(Path.Combine(deep, "깊은 파일 😀.txt"), "깊다");
        File.WriteAllText(Path.Combine(root, "보고서.txt"), "안녕하세요");
        File.WriteAllBytes(Path.Combine(root, "중간", "데이터.bin").Also(p => Directory.CreateDirectory(Path.GetDirectoryName(p)!)), new byte[300_000]);
        deep.Length.Should().BeGreaterThan(260);
        return root;
    }

    [Fact]
    public void Copies_nested_tree_with_korean_names_and_paths_over_260_chars()
    {
        var src = MakeTree("원본");
        var dest = Path.Combine(_dir, "대상");
        using var job = NativeCopyEngine.CreateJob(new CopySpec { Sources = [src], DestDir = dest, Verify = VerifyMode.Hash });
        job.Start();
        var p = WaitDone(job);

        p.State.Should().Be(NativeJobState.Done, p.ToString());
        p.Phase.Should().Be(CopyPhase.Finished);
        p.FilesDone.Should().Be(3);
        p.FilesFailed.Should().Be(0);
        p.BytesDone.Should().Be(p.BytesTotal);
        var outRoot = Path.Combine(dest, "원본");
        var deepFile = Directory.EnumerateFiles(outRoot, "깊은 파일 😀.txt", SearchOption.AllDirectories).Single();
        deepFile.Length.Should().BeGreaterThan(260);
        File.ReadAllText(deepFile).Should().Be("깊다");
        Directory.Exists(Path.Combine(outRoot, "빈 폴더")).Should().BeTrue();
        job.CurrentName().Should().NotBeEmpty();
        job.GetIssues().Should().BeEmpty();
    }

    [Fact]
    public void Pause_holds_progress_and_resume_finishes_the_copy()
    {
        var big = Path.Combine(_dir, "큰 파일.bin");
        using (var fs = File.Create(big))
        {
            fs.SetLength(512L * 1024 * 1024);
        }

        using var job = NativeCopyEngine.CreateJob(new CopySpec { Sources = [big], DestDir = Path.Combine(_dir, "out"), LargeThreshold = 1 });
        job.Start();
        WaitUntil(job, p => p.BytesDone > 0 || p.IsTerminal);
        job.Pause();
        var paused = job.Poll();
        if (paused.State == NativeJobState.Paused)
        {
            Thread.Sleep(300);
            job.Poll().BytesDone.Should().BeLessThanOrEqualTo(paused.BytesDone + (8 << 20), "일시정지 중에는 덩어리 하나 이상 진행하지 않음");
            job.CurrentName().Should().EndWith("큰 파일.bin");
            job.Resume();
        }

        var done = WaitDone(job);
        done.State.Should().Be(NativeJobState.Done);
        new FileInfo(Path.Combine(_dir, "out", "큰 파일.bin")).Length.Should().Be(512L * 1024 * 1024);
    }

    [Fact]
    public void Cancel_stops_and_removes_partial_destination()
    {
        var big = Path.Combine(_dir, "big.bin");
        using (var fs = File.Create(big))
        {
            fs.SetLength(1024L * 1024 * 1024);
        }

        using var job = NativeCopyEngine.CreateJob(new CopySpec { Sources = [big], DestDir = Path.Combine(_dir, "out") });
        job.Start();
        WaitUntil(job, p => p.BytesDone > 0 || p.IsTerminal);
        job.Cancel();
        var p = WaitDone(job);
        if (p.State == NativeJobState.Canceled)
        {
            // 반쪽 파일이 남으면 안 된다. 빠른 디스크(CI)에서는 취소가 닿기 전에 복사가 끝나 온전한 파일이 남을 수 있다 (그건 괜찮다).
            var dest = new FileInfo(Path.Combine(_dir, "out", "big.bin"));
            if (dest.Exists)
            {
                dest.Length.Should().Be(1024L * 1024 * 1024, "취소 뒤에 남은 파일은 다 복사된 것이어야 한다");
            }

            var outDir = Path.Combine(_dir, "out");
            (Directory.Exists(outDir) ? Directory.EnumerateFiles(outDir) : []).Where(f => !string.Equals(Path.GetFileName(f), "big.bin", StringComparison.OrdinalIgnoreCase))
                .Should().BeEmpty("임시 반쪽 파일이 남으면 안 된다");
        }
    }

    [Fact]
    public void Resumes_from_journal_after_simulated_crash()
    {
        var src = Path.Combine(_dir, "many");
        Directory.CreateDirectory(Path.Combine(src, "하위"));
        for (var i = 0; i < 60; i++)
        {
            File.WriteAllText(Path.Combine(src, i % 2 == 0 ? "" : "하위", $"{i:000}.txt"), i.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        var dest = Path.Combine(_dir, "out");
        var journal = Path.Combine(_dir, "jobs", "crash.journal");
        using (var crashed = NativeCopyEngine.CreateJob(new CopySpec { Sources = [src], DestDir = dest, JournalPath = journal, Threads = 2, TestAbortAfterFiles = 15 }))
        {
            crashed.Start();
            var p = WaitDone(crashed);
            p.State.Should().Be(NativeJobState.Failed);
            p.LastError.Should().Be(NativeCopyEngine.ErrAborted);
            p.FilesDone.Should().BeInRange(15UL, 59UL);
        }

        File.Exists(journal).Should().BeTrue("크래시면 저널이 남는다");
        NativeCopyEngine.ReadJournalSpec(journal).Should().Contain("\"dest_dir\"").And.NotContain("_test_abort");

        using var resumed = NativeCopyEngine.ResumeFromJournal(journal);
        resumed.Start();
        var r = WaitDone(resumed);
        r.State.Should().Be(NativeJobState.Done, r.ToString());
        r.FilesDone.Should().Be(60);
        r.FilesSkipped.Should().BeGreaterThanOrEqualTo(15, "이미 끝난 파일은 크기+시간이 같으면 건너뜀");
        Directory.EnumerateFiles(Path.Combine(dest, "many"), "*.txt", SearchOption.AllDirectories).Should().HaveCount(60);
        File.Exists(journal).Should().BeFalse("성공하면 저널을 지운다");
    }

    [Fact]
    public void Move_verifies_before_deleting_source()
    {
        var src = MakeTree("옮길 폴더");
        var dest = Path.Combine(_dir, "이동 대상");
        using (var job = NativeCopyEngine.CreateJob(new CopySpec { Sources = [src], DestDir = dest, Mode = CopyMode.Move, MoveByCopy = true, Verify = VerifyMode.Hash }))
        {
            job.Start();
            var p = WaitDone(job);
            p.State.Should().Be(NativeJobState.Done, p.ToString());
            p.FilesDone.Should().Be(3);
        }

        Directory.Exists(src).Should().BeFalse("검증이 끝난 파일만 원본을 지우고, 빈 원본 폴더도 지운다");
        File.ReadAllText(Path.Combine(dest, "옮길 폴더", "보고서.txt")).Should().Be("안녕하세요");

        // 대상에 같은 이름의 폴더가 있어 복사가 안 되면 원본을 지우지 않는다
        Directory.CreateDirectory(src);
        File.WriteAllText(Path.Combine(src, "보고서.txt"), "두 번째");
        File.Delete(Path.Combine(dest, "옮길 폴더", "보고서.txt"));
        Directory.CreateDirectory(Path.Combine(dest, "옮길 폴더", "보고서.txt", "막음"));
        using var job2 = NativeCopyEngine.CreateJob(new CopySpec
        {
            Sources = [src], DestDir = dest, Mode = CopyMode.Move, MoveByCopy = true, Conflict = ConflictPolicy.Overwrite,
        });
        job2.Start();
        var p2 = WaitDone(job2);
        p2.State.Should().Be(NativeJobState.Done);
        p2.FilesFailed.Should().Be(1);
        p2.LastError.Should().Be(NativeCopyEngine.ErrIo);
        job2.GetIssues().Should().ContainSingle(i => i.Kind == CopyIssueKind.Failed);
        File.ReadAllText(Path.Combine(src, "보고서.txt")).Should().Be("두 번째");
    }

    [Fact]
    public void Ask_conflicts_appear_as_issues_and_resolve_across_the_boundary()
    {
        var src = Path.Combine(_dir, "s");
        var dest = Path.Combine(_dir, "d");
        Directory.CreateDirectory(src);
        Directory.CreateDirectory(Path.Combine(dest, "s"));
        File.WriteAllText(Path.Combine(src, "a.txt"), "새것");
        File.WriteAllText(Path.Combine(src, "b.txt"), "새것 b");
        File.WriteAllText(Path.Combine(src, "c.txt"), "충돌 없음");
        File.WriteAllText(Path.Combine(dest, "s", "a.txt"), "옛것");
        File.WriteAllText(Path.Combine(dest, "s", "b.txt"), "옛것 b");

        using var job = NativeCopyEngine.CreateJob(new CopySpec { Sources = [src], DestDir = dest, Conflict = ConflictPolicy.Ask });
        job.Start();
        var p = WaitUntil(job, x => x.State == NativeJobState.NeedsAttention && x.FilesDone == 1);
        p.IssuesPending.Should().Be(2);
        var issues = job.GetIssues();
        issues.Should().HaveCount(2).And.OnlyContain(i => i.Kind == CopyIssueKind.Conflict && !i.Resolved);
        var a = issues.Single(i => i.Source.EndsWith("a.txt", StringComparison.Ordinal));
        job.Resolve(a.Id, ConflictAction.Rename).Should().BeTrue();
        job.Resolve(a.Id, ConflictAction.Rename).Should().BeFalse("이미 해결한 항목");
        job.Resolve(0, ConflictAction.Skip, applyToAll: true).Should().BeTrue();
        var done = WaitDone(job);
        done.State.Should().Be(NativeJobState.Done);
        done.FilesSkipped.Should().Be(1);
        File.ReadAllText(Path.Combine(dest, "s", "a (2).txt")).Should().Be("새것");
        File.ReadAllText(Path.Combine(dest, "s", "b.txt")).Should().Be("옛것 b");
        job.GetIssues().Should().OnlyContain(i => i.Resolved);
    }

    [Fact]
    public async Task Adapter_copies_with_progress_and_asks_conflicts()
    {
        var src = MakeTree("어댑터");
        var dest = Path.Combine(_dir, "어댑터 대상");
        Directory.CreateDirectory(Path.Combine(dest, "어댑터"));
        File.WriteAllText(Path.Combine(dest, "어댑터", "보고서.txt"), "기존");
        var fallback = new FakeFileOps();
        var asked = new List<CopyIssue>();
        var svc = new NativeCopyFileOperationService(fallback, options: new NativeCopyFileOperationOptions
        {
            JournalDirectory = Path.Combine(_dir, "jobs"),
            TuningPath = Path.Combine(_dir, "copy_tuning.json"),
            PollInterval = TimeSpan.FromMilliseconds(10),
            ResolveConflict = (issue, _) =>
            {
                asked.Add(issue);
                return Task.FromResult(new ConflictDecision(ConflictAction.Rename));
            },
        });
        svc.UsesNativeEngine.Should().BeTrue();
        var progress = new ListProgress();

        var r = await svc.CopyAsync([src], dest, progress, CancellationToken.None);

        r.Succeeded.Should().BeTrue(r.Error);
        asked.Should().ContainSingle(i => i.Source.EndsWith("보고서.txt", StringComparison.Ordinal));
        File.ReadAllText(Path.Combine(dest, "어댑터", "보고서 (2).txt")).Should().Be("안녕하세요");
        File.ReadAllText(Path.Combine(dest, "어댑터", "보고서.txt")).Should().Be("기존");
        progress.Items.Should().NotBeEmpty();
        progress.Items[^1].Done.Should().Be(progress.Items[^1].Total);
        File.Exists(Path.Combine(_dir, "copy_tuning.json")).Should().BeTrue("드라이브 조합별 최적값 저장");
        Directory.EnumerateFiles(Path.Combine(_dir, "jobs")).Should().BeEmpty("성공한 작업의 저널은 지운다");

        var moved = await svc.MoveAsync([Path.Combine(dest, "어댑터")], Path.Combine(_dir, "옮김"), null, CancellationToken.None);
        moved.Succeeded.Should().BeTrue(moved.Error);
        Directory.Exists(Path.Combine(dest, "어댑터")).Should().BeFalse();
        File.Exists(Path.Combine(_dir, "옮김", "어댑터", "보고서 (2).txt")).Should().BeTrue();

        (await svc.DeleteAsync(["x"], true, null, CancellationToken.None)).Succeeded.Should().BeTrue();
        fallback.Calls.Should().Equal("delete");
    }

    [Fact]
    public async Task Adapter_cancellation_returns_canceled_and_cleans_journal()
    {
        var big = Path.Combine(_dir, "big.bin");
        using (var fs = File.Create(big))
        {
            fs.SetLength(1024L * 1024 * 1024);
        }

        var jobs = Path.Combine(_dir, "jobs");
        var svc = new NativeCopyFileOperationService(new FakeFileOps(), options: new NativeCopyFileOperationOptions
        {
            JournalDirectory = jobs, TuningPath = null, PollInterval = TimeSpan.FromMilliseconds(10),
        });
        using var cts = new CancellationTokenSource();
        var progress = new ListProgress(p =>
        {
            if (p.Done > 0)
            {
                cts.Cancel();
            }
        });
        var r = await svc.CopyAsync([big], Path.Combine(_dir, "out"), progress, cts.Token);
        if (r.Aborted)
        {
            File.Exists(Path.Combine(_dir, "out", "big.bin")).Should().BeFalse();
            Directory.EnumerateFiles(jobs).Should().BeEmpty();
        }
        else
        {
            r.Succeeded.Should().BeTrue("취소가 늦으면 끝까지 복사될 수 있다");
        }
    }

    [Fact]
    public async Task Adapter_without_engine_uses_fallback()
    {
        var fallback = new FakeFileOps();
        var svc = new NativeCopyFileOperationService(fallback, new UnavailableEngine());
        svc.UsesNativeEngine.Should().BeFalse();
        (await svc.CopyAsync(["a"], "b", null, CancellationToken.None)).Succeeded.Should().BeTrue();
        (await svc.MoveAsync(["a"], "b", null, CancellationToken.None)).Succeeded.Should().BeTrue();
        fallback.Calls.Should().Equal("copy", "move");
    }

    private sealed class ListProgress(Action<FileOpProgress>? onReport = null) : IProgress<FileOpProgress>
    {
        public List<FileOpProgress> Items { get; } = [];

        public void Report(FileOpProgress value)
        {
            lock (Items)
            {
                Items.Add(value);
            }

            onReport?.Invoke(value);
        }
    }

    [Fact]
    public async Task Adapter_reads_thread_setting_at_each_job()
    {
        var engine = new CapturingEngine();
        var threads = 0;
        var svc = new NativeCopyFileOperationService(new FakeFileOps(), engine, new NativeCopyFileOperationOptions
        {
            JournalDirectory = null,
            ThreadsProvider = () => threads,
        });

        await svc.CopyAsync([@"C:\a"], @"C:\b", null, CancellationToken.None);
        threads = 6; // 설정 창에서 바꿈
        await svc.CopyAsync([@"C:\a"], @"C:\b", null, CancellationToken.None);
        threads = 500;
        await svc.CopyAsync([@"C:\a"], @"C:\b", null, CancellationToken.None);
        threads = -3;
        await svc.CopyAsync([@"C:\a"], @"C:\b", null, CancellationToken.None);

        engine.Specs.Select(s => s.Threads).Should().Equal(0u, 6u, 64u, 0u);
    }

    /// <summary>받은 CopySpec만 기록하고 작업은 만들지 않는다 (어댑터가 실패로 돌려준다).</summary>
    private sealed class CapturingEngine : ICopyEngine
    {
        public List<CopySpec> Specs { get; } = [];

        public bool IsAvailable => true;

        public string? UnavailableReason => null;

        public ICopyJob CreateJob(CopySpec spec)
        {
            Specs.Add(spec);
            throw new NativeCopyException(-1, "시험");
        }

        public ICopyJob ResumeFromJournal(string journalPath) => throw new InvalidOperationException();
    }

    private sealed class UnavailableEngine : ICopyEngine
    {
        public bool IsAvailable => false;

        public string? UnavailableReason => "시험";

        public ICopyJob CreateJob(CopySpec spec) => throw new InvalidOperationException();

        public ICopyJob ResumeFromJournal(string journalPath) => throw new InvalidOperationException();
    }

    private sealed class FakeFileOps : IFileOperationService
    {
        public List<string> Calls { get; } = [];

        private Task<FileOpResult> Record(string name)
        {
            Calls.Add(name);
            return Task.FromResult(FileOpResult.Ok());
        }

        public Task<FileOpResult> CopyAsync(IReadOnlyList<string> sources, string destinationFolder, IProgress<FileOpProgress>? progress, CancellationToken ct) => Record("copy");

        public Task<FileOpResult> MoveAsync(IReadOnlyList<string> sources, string destinationFolder, IProgress<FileOpProgress>? progress, CancellationToken ct) => Record("move");

        public Task<FileOpResult> DeleteAsync(IReadOnlyList<string> paths, bool recycle, IProgress<FileOpProgress>? progress, CancellationToken ct) => Record("delete");

        public Task<FileOpResult> RenameAsync(string path, string newName, CancellationToken ct) => Record("rename");

        public Task<FileOpResult> NewFolderAsync(string parent, string name, CancellationToken ct) => Record("newfolder");
    }
}

internal static class TestExtensions
{
    public static T Also<T>(this T value, Action<T> action)
    {
        action(value);
        return value;
    }
}
