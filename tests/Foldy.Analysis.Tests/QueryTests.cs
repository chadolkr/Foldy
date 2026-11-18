using Foldy.Analysis;
using Microsoft.Data.Sqlite;

namespace Foldy.Analysis.Tests;

/// <summary>생성한 스냅샷 DB에 대한 쿼리 (설계서 7.3 "같은 DB로 답하는 것").</summary>
public class QueryTests
{
    [Fact]
    public void Schema_matches_design_and_is_read_only()
    {
        using var tmp = new TempDir();
        var path = TestData.Build(tmp.Path, TestData.Sample());
        Path.GetFileName(path).Should().Be("test.20261119T000000Z.db");
        using var db = AnalysisDb.Open(path);
        var scan = db.Scan();
        scan.Volume.Should().Be("D:");
        scan.TotalFiles.Should().Be(6);
        scan.TotalDirs.Should().Be(10);
        scan.StartedUtc.Should().Be(TestData.Now);
        db.RootId.Should().Be(5);

        // mode=ro: 쓰기는 거부된다
        using var c = AnalysisSqlite.OpenReadOnly(path);
        using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM file";
        FluentActions.Invoking(() => cmd.ExecuteNonQuery()).Should().Throw<SqliteException>();
        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name NOT LIKE 'sqlite_%'";
        Convert.ToInt32(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture).Should().Be(7);
    }

    [Fact]
    public void Top_folders_files_and_most_files()
    {
        using var tmp = new TempDir();
        using var db = AnalysisDb.Open(TestData.Build(tmp.Path, TestData.Sample()));
        var top = db.TopFolders(3);
        top.Take(2).Select(t => t.Path).Should().BeEquivalentTo(@"D:\Windows", @"D:\Windows\Temp");
        top[0].Alloc.Should().Be(500L << 20);
        top[2].Alloc.Should().Be(83L << 20);
        // 자신 제외 크기: Windows 자신은 파일이 없으므로 Temp가 먼저
        var own = db.TopFolders(2, ownSizeOnly: true);
        own[0].Name.Should().Be("Temp");
        own[0].OwnAlloc.Should().Be(500L << 20);
        own[1].Name.Should().Be("Downloads");

        var files = db.TopFiles(2);
        files.Select(f => f.Path).Should().Equal(@"D:\Windows\Temp\junk.tmp", @"D:\Users\me\Downloads\setup.exe");
        files[1].Ext.Should().Be("exe");

        db.MostFilesFolders(1)[0].Name.Should().Be("proj");
        db.MostFilesFolders(1)[0].TotalFiles.Should().Be(3);
        var ownFiles = db.MostFilesFolders(1, ownFilesOnly: true)[0];
        ownFiles.Name.Should().Be("Downloads");
        ownFiles.OwnFiles.Should().Be(2);
    }

    [Fact]
    public void Extensions_groups_and_old_files()
    {
        using var tmp = new TempDir();
        using var db = AnalysisDb.Open(TestData.Build(tmp.Path, TestData.Sample()));
        var ext = db.ExtensionStats();
        ext[0].Ext.Should().Be("tmp");
        ext.Single(e => e.Ext == "jpg").Files.Should().Be(1, "확장자는 소문자로 모은다");
        ext.Sum(e => e.Files).Should().Be(6);
        var groups = db.GroupStats();
        groups.Single(g => g.Group == "설치 파일").Alloc.Should().Be(80 << 20);
        groups.Single(g => g.Group == "이미지").Extensions.Should().Equal("jpg");
        db.FilesWithExtension("js", 10).Single().Name.Should().Be("big.js");

        var old = db.OldFiles(TestData.Now.AddDays(-100), 10);
        old.Select(f => f.Name).Should().Equal("junk.tmp", "setup.exe");
        db.OldFiles(TestData.Now.AddYears(-5), 10).Should().BeEmpty();
    }

    [Fact]
    public void Treemap_children_chain_and_find()
    {
        using var tmp = new TempDir();
        using var db = AnalysisDb.Open(TestData.Build(tmp.Path, TestData.Sample()));
        var kids = db.Children(db.RootId);
        kids.Select(k => k.Name).Should().Equal("Windows", "Users", "proj");
        kids.Should().OnlyContain(k => k.IsDir);
        var proj = db.Children(20);
        proj[0].Name.Should().Be("node_modules");
        proj.Skip(1).Select(k => (k.Name, k.IsDir)).Should().BeEquivalentTo([("package.json", false), ("src", true)]);
        proj[0].Tag.Should().Be("node_modules");
        db.Children(42, maxFiles: 1).Should().ContainSingle().Which.Name.Should().Be("setup.exe");

        db.Chain(23).Select(c => c.Name).Should().Equal(@"D:\", "proj", "node_modules", "lib");
        db.PathOf(23).Should().Be(@"D:\proj\node_modules\lib");
        db.PathOf(5).Should().Be(@"D:\");
        db.FindDir(@"d:\PROJ\node_modules").Should().Be(22);
        db.FindDir(@"D:\").Should().Be(5);
        db.FindDir(@"D:\nope").Should().BeNull();
        db.FindDir(@"E:\proj").Should().BeNull();
        db.Folder(22)!.Tag.Should().Be("node_modules");
    }

    [Fact]
    public void Cleanup_candidates_follow_rules_and_skip_system_folders()
    {
        using var tmp = new TempDir();
        using var db = AnalysisDb.Open(TestData.Build(tmp.Path, TestData.Sample()));
        var c = db.Candidates();
        c.Select(x => (x.Kind, x.Rule, x.Path)).Should().Equal(
            ("file", "old-installers", @"D:\Users\me\Downloads\setup.exe"),
            ("dir", "node-modules", @"D:\proj\node_modules"));
        c[0].Reclaim.Should().Be(80 << 20);
        c[1].Tag.Should().Be("node_modules");
        c[1].Reason.Should().NotBeEmpty();
        c.Should().NotContain(x => x.Path.Contains(@"\Windows", StringComparison.OrdinalIgnoreCase), "시스템 폴더는 절대 제안하지 않는다");
        db.Folder(31)!.Tag.Should().Be("temp", "태그는 붙지만 제안은 없다");
    }

    [Fact]
    public void Growth_against_previous_snapshot_via_attach()
    {
        using var tmp = new TempDir();
        var prev = TestData.Build(tmp.Path, TestData.Sample(TestData.Now.AddDays(-7)));
        var cur = TestData.Build(tmp.Path, TestData.Sample(TestData.Now, extraDownloads: 2L << 30));
        var snaps = AnalysisPaths.ListSnapshots(tmp.Path, "test");
        snaps.Select(s => s.DbPath).Should().Equal(cur, prev);

        using var db = AnalysisDb.Open(cur);
        db.PreviousScanTime(prev).Should().Be(TestData.Now.AddDays(-7));
        var g = db.Growth(prev, 10);
        g.Where(x => x.Delta == 2L << 30).Select(x => x.Path).Should().BeEquivalentTo(
            @"D:\Users", @"D:\Users\me", @"D:\Users\me\Downloads", @"D:\Users\me\Downloads\new-stuff");
        g.Single(x => x.Name == "Users").IsNew.Should().BeFalse();
        var created = g.Single(x => x.Name == "new-stuff");
        created.IsNew.Should().BeTrue();
        created.PreviousAlloc.Should().Be(0);
        g.Should().NotContain(x => x.Path.EndsWith("Windows", StringComparison.Ordinal) && x.Delta != 0);
        db.PreviousTotal(prev).Should().Be(db.Scan().TotalAlloc - (2L << 30));
        db.Growth(prev, 1, shrunk: true).Single().Delta.Should().Be(0);
    }

    [Fact]
    public void Retention_keeps_latest_previous_and_weekly()
    {
        var t0 = TestData.Now;
        var daily = Enumerable.Range(0, 20).Select(d => new SnapshotRef("s" + d, t0.AddDays(-d))).ToList();
        var del = AnalysisPaths.RetentionPlan(daily).Select(s => s.DbPath).ToHashSet();
        daily.Select(s => s.DbPath).Where(p => !del.Contains(p)).Should().Equal("s0", "s1", "s8", "s15");
        AnalysisPaths.SnapshotFileName("k", new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).Should().Be("k.19700101T000000Z.db");
        AnalysisPaths.ParseStamp("20261119T010203Z").Should().Be(new DateTime(2026, 11, 19, 1, 2, 3, DateTimeKind.Utc));
        AnalysisPaths.ParseStamp("bad").Should().BeNull();
    }

    [Fact]
    public void Volume_keys_match_rust()
    {
        // engine/foldy-index/src/analysis/tests.rs volume_keys 와 같은 값
        AnalysisPaths.VolumeKey(@"C:\Temp\foldy").Should().Be("dir-6f71d92211e373a8");
        AnalysisPaths.VolumeKey(@"D:\Work\").Should().Be(AnalysisPaths.VolumeKey("d:/work"));
        var c = AnalysisPaths.VolumeKey("C:");
        (c.StartsWith('{') || c == "C").Should().BeTrue();
        AnalysisPaths.VolumeKey(@"C:\").Should().Be(c);
        AnalysisText.Extension("a.TXT").Should().Be("txt");
        AnalysisText.Extension(".gitignore").Should().Be("");
        AnalysisText.Extension("noext.").Should().Be("");
        AnalysisText.Extension("x.verylongextension123").Should().Be("");
        AnalysisText.FormatSize(1536).Should().Be("1.5 KB");
        AnalysisText.FormatSize(-(5L << 30)).Should().Be("-5 GB");
    }
}
