using System.IO.Compression;
using Foldy.Core.Desktop;
using Foldy.Core.Downloads;
using Foldy.Core.Logs;
using Foldy.Core.Managed;

namespace Foldy.Core.Tests;

public sealed class CleanupToolsTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 11, 19, 3, 0, 0, DateTimeKind.Utc);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "foldy-cleanup-" + Guid.NewGuid().ToString("N"));

    public CleanupToolsTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string Make(string rel, string text, DateTime whenUtc)
    {
        var p = Path.Combine(_root, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, text);
        File.SetCreationTimeUtc(p, whenUtc);
        File.SetLastWriteTimeUtc(p, whenUtc);
        File.SetLastAccessTimeUtc(p, whenUtc);
        return p;
    }

    [Fact]
    public void Log_candidates_are_old_dot_log_files_outside_windows()
    {
        var old = Now.AddDays(-30);
        LogPlanner.IsCandidate(new LogFile(@"D:\app\logs\a.log", 10, old), Now).Should().BeTrue();
        LogPlanner.IsCandidate(new LogFile(@"D:\app\logs\a.log", 10, Now.AddDays(-2)), Now).Should().BeFalse("최근 7일 안에 바뀐 로그");
        LogPlanner.IsCandidate(new LogFile(@"C:\Windows\Logs\CBS\CBS.log", 10, old), Now).Should().BeFalse("Windows 폴더");
        LogPlanner.IsCandidate(new LogFile(@"D:\work\windows\build.log", 10, old), Now).Should().BeTrue("볼륨 바로 아래가 아닌 windows 폴더는 사용자 폴더");
        LogPlanner.IsCandidate(new LogFile(@"C:\$Recycle.Bin\S-1\x.log", 10, old), Now).Should().BeFalse();
        LogPlanner.IsCandidate(new LogFile(@"D:\app\a.log.txt", 10, old), Now).Should().BeFalse();
        LogPlanner.IsSerious(2L << 30, 500L << 30).Should().BeTrue();
        LogPlanner.IsSerious(300L << 20, 4L << 30).Should().BeTrue("남은 공간의 5%가 넘음");
        LogPlanner.IsSerious(50L << 20, 400L << 30).Should().BeFalse();
    }

    [Fact]
    public void Logs_are_zipped_by_month_verified_and_reported_for_recycling()
    {
        var oct = new DateTime(2026, 10, 10, 3, 0, 0, DateTimeKind.Utc);
        var a = Make(@"logs\a.log", new string('a', 5000), oct);
        var b = Make(@"logs\b.log", new string('b', 5000), oct);
        var busy = Make(@"logs\busy.log", "busy", oct);
        var groups = LogPlanner.Plan([new LogFile(a, 5000, oct), new LogFile(b, 5000, oct), new LogFile(busy, 4, oct)], Now);
        var g = groups.Should().ContainSingle().Subject;
        g.ZipPath.Should().EndWith("logs-2026-10.zip");

        LogArchiveResult r;
        using (new FileStream(busy, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
        {
            r = LogArchiver.Archive(g, CancellationToken.None);
        }

        r.Archived.Should().BeEquivalentTo([a, b]);
        r.Skipped.Should().ContainSingle(s => s.Path == busy);
        using var zip = ZipFile.OpenRead(g.ZipPath);
        zip.Entries.Select(e => e.Name).Should().BeEquivalentTo(["a.log", "b.log"]);
        File.Exists(a).Should().BeTrue("원본은 호출한 쪽이 휴지통으로 보낸다");
    }

    [Fact]
    public void Downloads_finds_installers_duplicates_unpacked_zips_and_old_files()
    {
        var recent = Now.AddDays(-3);
        Make(@"dl\보고서.pdf", "same", recent);
        Make(@"dl\보고서 (1).pdf", "same", recent);
        Make(@"dl\photos.zip", "zip", recent);
        Directory.CreateDirectory(Path.Combine(_root, @"dl\photos"));
        Make(@"dl\old.txt", "old", Now.AddDays(-200));
        Make(@"dl\new.txt", "new", recent);
        Make(@"dl\big.crdownload", "partial", Now.AddDays(-200));

        var items = DownloadsPlanner.Plan(Path.Combine(_root, "dl"), _ => null, Now, findDuplicates: true);
        items.Select(i => (Path.GetFileName(i.Path), i.Kind)).Should().BeEquivalentTo(new[]
        {
            ("보고서 (1).pdf", DownloadKind.Duplicate),
            ("photos.zip", DownloadKind.Unpacked),
            ("old.txt", DownloadKind.Old),
        });

        DownloadsPlanner.MatchInstalled("Git", ["Git version 2.47.0", "Notepad++"]).Should().Be("Git version 2.47.0");
        DownloadsPlanner.MatchInstalled("Setup", ["Setup Helper"]).Should().BeNull("흔한 이름은 맞추지 않는다");
        DownloadsPlanner.MatchInstalled("Visual Studio Code", ["Microsoft Visual Studio Code (User)"]).Should().BeNull("확실하지 않으면 건너뜀");
    }

    [Fact]
    public void Desktop_tidy_moves_by_type_keeps_shortcuts_and_recent_and_undo_restores()
    {
        var desk = Path.Combine(_root, "desk");
        var doc = Make(@"desk\계획.docx", "d", Now.AddDays(-40));
        var pic = Make(@"desk\사진.png", "p", Now.AddDays(-40));
        Make(@"desk\앱.lnk", "l", Now.AddDays(-40));
        Make(@"desk\오늘.txt", "t", DateTime.UtcNow);
        var archive = Path.Combine(desk, DesktopTidy.ArchiveFolderName);

        var plan = DesktopTidy.Plan(desk, archive, DateTime.UtcNow);
        plan.Select(m => (Path.GetFileName(m.Src), m.Group)).Should().BeEquivalentTo(new[] { ("계획.docx", "문서"), ("사진.png", "이미지") });

        var journal = Path.Combine(_root, "journal.json");
        DesktopTidy.Execute(plan, journal).Should().HaveCount(2);
        File.Exists(doc).Should().BeFalse();
        DesktopTidy.Plan(desk, archive, DateTime.UtcNow).Should().BeEmpty("보관 폴더는 다시 정리하지 않는다");

        DesktopTidy.Undo(journal).Should().Be(2);
        File.Exists(doc).Should().BeTrue();
        File.Exists(pic).Should().BeTrue();
    }

    [Fact]
    public void Managed_folder_resolves_nearest_method_suggests_moves_and_undo_restores()
    {
        var root = Path.Combine(_root, "work");
        Directory.CreateDirectory(Path.Combine(root, "1_Projects", "옛 프로젝트"));
        Directory.CreateDirectory(Path.Combine(root, "4_Archives"));
        var old = Now.AddDays(-200);
        Directory.SetLastWriteTimeUtc(Path.Combine(root, "1_Projects", "옛 프로젝트"), old);
        var loose = Make(@"work\메모.txt", "m", Now.AddDays(-1));
        Make(Path.Combine("work", "run.exe"), "x", Now.AddDays(-1));

        ManagedFolders.CannotManage(root).Should().BeNull();
        ManagedFolders.SetMethod(root, FolderMethod.Para);
        File.GetAttributes(Path.Combine(root, ManagedFolders.FileName)).Should().HaveFlag(FileAttributes.Hidden);
        File.ReadAllText(Path.Combine(root, ManagedFolders.FileName)).Should().Contain("정리 방법: PARA").And.Contain("지워도 괜찮아요");
        ManagedFolders.Resolve(Path.Combine(root, "1_Projects"))!.Value.Method.Should().Be(FolderMethod.Para, "하위 폴더는 가장 가까운 .foldy.md를 따른다");

        var s = ManagedFolders.Suggest(root, FolderMethod.Para, Now);
        var moves = s.SelectMany(x => x.Moves).ToList();
        moves.Select(m => Path.GetRelativePath(root, m.Dst)).Should().BeEquivalentTo([Path.Combine(ManagedFolders.Inbox, "메모.txt"), Path.Combine("4_Archives", "옛 프로젝트")]);
        moves.Should().NotContain(m => m.Src.EndsWith("run.exe", StringComparison.Ordinal), "실행 파일은 옮기지 않는다");

        var journal = Path.Combine(_root, "managed.json");
        var (done, skipped) = ManagedFolders.Apply(moves, journal);
        done.Should().HaveCount(2);
        skipped.Should().BeEmpty();
        File.Exists(loose).Should().BeFalse();
        ManagedFolders.Undo(journal).Should().Be(2);
        File.Exists(loose).Should().BeTrue();
        Directory.Exists(Path.Combine(root, "1_Projects", "옛 프로젝트")).Should().BeTrue();

        Directory.CreateDirectory(Path.Combine(root, "repo", ".git"));
        ManagedFolders.CannotManage(Path.Combine(root, "repo")).Should().Contain("Git");
    }
}
