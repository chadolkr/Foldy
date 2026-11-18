using Foldy.Core.Descriptions;
using Foldy.Core.Organize;
using Foldy.Core.Templates;

namespace Foldy.Templates.Tests;

public class OrganizePlannerTests
{
    private static readonly DateTime Nov = new(2026, 11, 18, 3, 0, 0, DateTimeKind.Utc);

    private static OrganizePlan PlanOf(TempDir tmp, OrganizeOptions o, bool deep = false, IMediaDateProvider? media = null) =>
        OrganizePlanner.Plan(OrganizeScanner.Scan(tmp.Path, deep), o, p => File.Exists(p) || Directory.Exists(p), media);

    private static string Rel(TempDir tmp, string p) => Path.GetRelativePath(tmp.Path, p);

    [Fact]
    public void By_type_groups_files()
    {
        using var tmp = new TempDir();
        tmp.File("보고서.pdf");
        tmp.File("사진.JPG");
        tmp.File("노래.mp3");
        tmp.File("setup.msi");
        tmp.File("이상한.xyz");
        var plan = PlanOf(tmp, new OrganizeOptions { Mode = OrganizeMode.ByType });
        plan.Moves.ToDictionary(m => Path.GetFileName(m.Source), m => m.TargetFolder).Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["보고서.pdf"] = "문서", ["사진.JPG"] = "이미지", ["노래.mp3"] = "음악", ["setup.msi"] = "설치파일", ["이상한.xyz"] = "기타",
        });
        plan.Moves.Should().OnlyContain(m => m.Reason.StartsWith("종류", StringComparison.Ordinal));
    }

    [Fact]
    public void By_date_uses_modified_time_or_exif_hook()
    {
        using var tmp = new TempDir();
        tmp.File("a.txt", "a", new DateTime(2025, 3, 9, 12, 0, 0, DateTimeKind.Utc));
        tmp.File("p.jpg", "p", new DateTime(2025, 3, 9, 12, 0, 0, DateTimeKind.Utc));
        var plan = PlanOf(tmp, new OrganizeOptions { Mode = OrganizeMode.ByDate }, media: new FakeExif(new DateTime(2024, 7, 1)));
        plan.Moves.Single(m => m.Source.EndsWith("a.txt", StringComparison.Ordinal)).TargetFolder.Should().Be(@"2025\2025-03");
        var photo = plan.Moves.Single(m => m.Source.EndsWith("p.jpg", StringComparison.Ordinal));
        photo.TargetFolder.Should().Be(@"2024\2024-07");
        photo.Reason.Should().Contain("촬영 시각");
    }

    [Fact]
    public void By_template_rules_and_unmatched_go_to_needs_sorting()
    {
        using var tmp = new TempDir();
        tmp.File("ACME_계약서.pdf");
        tmp.File("최종_시안.pptx");
        tmp.File("music.flac");
        var client = TemplateLibrary.LoadBuiltIn().Single(t => t.Id == "client-project");
        var plan = PlanOf(tmp, new OrganizeOptions { Mode = OrganizeMode.ByTemplate, Template = client });
        var map = plan.Moves.ToDictionary(m => Path.GetFileName(m.Source), m => m.TargetFolder);
        map["ACME_계약서.pdf"].Should().Be("01_계약");
        map["최종_시안.pptx"].Should().Be("04_산출물");
        map["music.flac"].Should().Be(OrganizeOptions.UnclassifiedFolder);
    }

    [Fact]
    public void Para_old_files_become_archive_candidates()
    {
        using var tmp = new TempDir();
        tmp.File("old.docx", "x", new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        tmp.File("new.docx", "y", Nov.AddDays(-3));
        var para = TemplateLibrary.LoadBuiltIn().Single(t => t.Id == "para");
        var plan = PlanOf(tmp, new OrganizeOptions { Mode = OrganizeMode.ByTemplate, Template = para, NowUtc = Nov });
        plan.Moves.Single(m => m.Source.EndsWith("old.docx", StringComparison.Ordinal)).TargetFolder.Should().Be("4_Archives");
        plan.Moves.Single(m => m.Source.EndsWith("new.docx", StringComparison.Ordinal)).TargetFolder.Should().Be(OrganizeOptions.UnclassifiedFolder);
    }

    [Fact]
    public void Custom_rules_pattern_and_keyword()
    {
        using var tmp = new TempDir();
        tmp.File("3월_영수증.pdf");
        tmp.File("계획.pdf");
        var rules = new[] { new CustomRule { Pattern = "*.pdf", Keywords = ["영수증"], Target = "재무/영수증" } };
        var plan = PlanOf(tmp, new OrganizeOptions { Mode = OrganizeMode.Custom, Rules = rules });
        plan.Moves.Single(m => m.Source.EndsWith("영수증.pdf", StringComparison.Ordinal)).TargetFolder.Should().Be(@"재무\영수증");
        plan.Moves.Single(m => m.Source.EndsWith("계획.pdf", StringComparison.Ordinal)).TargetFolder.Should().Be(OrganizeOptions.UnclassifiedFolder);
    }

    [Fact]
    public void Unsafe_custom_target_is_refused()
    {
        using var tmp = new TempDir();
        tmp.File("a.pdf");
        var rules = new[] { new CustomRule { Pattern = "*.pdf", Target = @"..\..\escape" } };
        var plan = PlanOf(tmp, new OrganizeOptions { Mode = OrganizeMode.Custom, Rules = rules });
        plan.Moves.Should().BeEmpty();
        plan.Skipped.Should().Contain(s => s.Reason.Contains("대상 폴더", StringComparison.Ordinal));

        var abs = PlanOf(tmp, new OrganizeOptions { Mode = OrganizeMode.Custom, Rules = [new CustomRule { Pattern = "*", Target = @"C:\Windows" }] });
        abs.Moves.Should().BeEmpty();
    }

    [Fact]
    public void Conflicts_get_numbered_names()
    {
        using var tmp = new TempDir();
        tmp.File(@"문서\보고서.pdf", "existing");
        tmp.File(@"문서\보고서 (2).pdf", "existing2");
        tmp.File("보고서.pdf", "new one");
        var plan = PlanOf(tmp, new OrganizeOptions { Mode = OrganizeMode.ByType, DetectDuplicates = false });
        plan.Moves.Single().Destination.Should().Be(tmp[@"문서\보고서 (3).pdf"]);
    }

    [Fact]
    public void Conflicts_between_planned_files_from_subfolders()
    {
        using var tmp = new TempDir();
        tmp.File(@"x\메모.txt", "1");
        tmp.File(@"y\메모.txt", "22");
        var plan = PlanOf(tmp, new OrganizeOptions { Mode = OrganizeMode.ByType }, deep: true);
        plan.Moves.Select(m => Rel(tmp, m.Destination)).Should().BeEquivalentTo([@"문서\메모.txt", @"문서\메모 (2).txt"]);
    }

    [Fact]
    public void Duplicates_go_to_candidates_never_deleted()
    {
        using var tmp = new TempDir();
        tmp.File("사진.jpg", "same-bytes", new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        tmp.File("사진 (1).jpg", "same-bytes", new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc));
        tmp.File("사진 - 복사본.jpg", "different", new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc));
        var plan = PlanOf(tmp, new OrganizeOptions { Mode = OrganizeMode.ByType });
        var map = plan.Moves.ToDictionary(m => Path.GetFileName(m.Source), m => m.TargetFolder);
        map["사진.jpg"].Should().Be("이미지");
        map["사진 (1).jpg"].Should().Be(OrganizeOptions.DuplicatesFolder);
        map["사진 - 복사본.jpg"].Should().Be("이미지", "크기는 달라 내용이 다르면 중복 아님");
        plan.Moves.Should().HaveCount(3);
    }

    [Fact]
    public void Safety_skips_hidden_system_project_and_description()
    {
        using var tmp = new TempDir();
        tmp.File("ok.txt");
        var h = tmp.File("hidden.txt");
        File.SetAttributes(h, FileAttributes.Hidden);
        var s = tmp.File("sys.dat");
        File.SetAttributes(s, FileAttributes.System);
        tmp.File(FolderDescription.FileName, "# 설명");
        tmp.File(@"myapp\package.json", "{}");
        tmp.File(@"myapp\index.js", "x");
        tmp.File(@"repo\.git\HEAD", "ref");
        tmp.File(@"repo\main.rs", "fn main(){}");
        tmp.File(@"plain\inner.pdf", "pdf");

        var plan = PlanOf(tmp, new OrganizeOptions { Mode = OrganizeMode.ByType }, deep: true);
        var sources = plan.Moves.Select(m => Rel(tmp, m.Source)).ToList();
        sources.Should().BeEquivalentTo(["ok.txt", @"plain\inner.pdf"]);
        plan.Skipped.Select(x => x.Reason).Should().Contain(r => r.Contains("프로젝트", StringComparison.Ordinal));
        plan.Skipped.Should().Contain(x => x.FullPath == h);
        plan.Skipped.Should().Contain(x => x.FullPath == s);
    }

    [Fact]
    public void Top_level_only_by_default()
    {
        using var tmp = new TempDir();
        tmp.File("a.txt");
        tmp.File(@"sub\b.txt");
        PlanOf(tmp, new OrganizeOptions()).Moves.Select(m => Path.GetFileName(m.Source)).Should().Equal("a.txt");
    }

    [Fact]
    public void Junctions_are_not_followed()
    {
        using var tmp = new TempDir();
        var target = tmp.Dir("real");
        tmp.File(@"real\in.txt");
        var link = tmp["link"];
        // 정션은 관리자 권한 없이 만들 수 있다 (심볼릭 링크는 개발자 모드 필요)
        using (var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"") { CreateNoWindow = true, UseShellExecute = false }))
        {
            p!.WaitForExit();
        }

        Directory.Exists(link).Should().BeTrue();
        var scan = OrganizeScanner.Scan(tmp.Path, includeSubfolders: true);
        scan.Entries.Should().NotContain(e => e.FullPath.StartsWith(link, StringComparison.OrdinalIgnoreCase));
        scan.Skipped.Should().Contain(x => x.FullPath == link);
    }

    [Fact]
    public void Root_checks_refuse_system_and_project_folders()
    {
        OrganizeScanner.CheckRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows)).Should().NotBeNull();
        using var tmp = new TempDir();
        OrganizeScanner.CheckRoot(tmp.Path).Should().BeNull();
        tmp.File("Cargo.toml", "[package]");
        OrganizeScanner.CheckRoot(tmp.Path).Should().NotBeNull();
    }

    [Fact]
    public void Folder_rules_move_project_folders_whole()
    {
        using var tmp = new TempDir();
        tmp.File(@"myapp\package.json", "{}");
        tmp.File(@"myapp\a.js", "x");
        var rules = new[] { new CustomRule { Keywords = ["app"], Target = "개발", ApplyToFolders = true } };
        var plan = PlanOf(tmp, new OrganizeOptions { Mode = OrganizeMode.Custom, Rules = rules }, deep: true);
        plan.Moves.Should().ContainSingle().Which.IsFolder.Should().BeTrue();
    }

    [Fact]
    public void Already_in_place_files_are_not_moved_and_outputs_not_reclassified()
    {
        using var tmp = new TempDir();
        tmp.File(@"문서\a.pdf");
        tmp.Dir("_분류 필요");
        var plan = PlanOf(tmp, new OrganizeOptions(), deep: true);
        plan.Moves.Should().BeEmpty();
    }

    [Fact]
    public void Prefix_date_option_renames()
    {
        using var tmp = new TempDir();
        tmp.File("견적.xlsx", "x", new DateTime(2026, 11, 18, 3, 0, 0, DateTimeKind.Utc));
        tmp.File("2026-01-02_이미날짜.xlsx");
        var plan = PlanOf(tmp, new OrganizeOptions { PrefixDate = true });
        plan.Moves.Select(m => m.Name).Should().BeEquivalentTo(["2026-11-18_견적.xlsx", "2026-01-02_이미날짜.xlsx"]);
    }

    [Fact]
    public void Retarget_recomputes_conflicts()
    {
        using var tmp = new TempDir();
        tmp.File("a.txt");
        tmp.File(@"보관\a.txt", "existing");
        var plan = PlanOf(tmp, new OrganizeOptions());
        OrganizePlanner.Retarget(plan, plan.Moves[0], "보관", p => File.Exists(p) || Directory.Exists(p));
        plan.Moves[0].Destination.Should().Be(tmp[@"보관\a (2).txt"]);
    }

    private sealed class FakeExif(DateTime taken) : IMediaDateProvider
    {
        public DateTime? GetTakenDate(string path) => taken;
    }
}

public class OrganizeExecutorTests
{
    [Fact]
    public void Execute_then_undo_restores_everything_and_removes_created_folders()
    {
        using var tmp = new TempDir();
        tmp.File("a.pdf", "A");
        tmp.File("b.jpg", "B");
        tmp.File("c.zip", "C");
        tmp.Dir("이미지"); // 원래 있던 폴더는 되돌려도 지우지 않는다
        var plan = OrganizePlanner.Plan(OrganizeScanner.Scan(tmp.Path, false), new OrganizeOptions(), p => File.Exists(p) || Directory.Exists(p));
        var journal = tmp["jobs\\o.journal"];

        var r = OrganizeExecutor.Execute(plan, journal, LocalMoveOperations.Instance);
        r.Moved.Should().Be(3);
        File.Exists(tmp[@"문서\a.pdf"]).Should().BeTrue();
        File.Exists(tmp["a.pdf"]).Should().BeFalse();

        var records = OrganizeJournalReader.Read(journal, out var truncated);
        truncated.Should().BeFalse();
        records[0].Type.Should().Be(JournalRecordType.Header);
        records[^1].Type.Should().Be(JournalRecordType.Done);
        records.Count(x => x.Type == JournalRecordType.Move).Should().Be(3);
        records.Count(x => x.Type == JournalRecordType.Mkdir).Should().Be(2, "문서, 압축만 새로 만듦");

        var u = OrganizeExecutor.Undo(journal, LocalMoveOperations.Instance);
        u.Restored.Should().Be(3);
        u.RemovedFolders.Should().Be(2);
        File.ReadAllText(tmp["a.pdf"]).Should().Be("A");
        File.ReadAllText(tmp["b.jpg"]).Should().Be("B");
        Directory.Exists(tmp["문서"]).Should().BeFalse();
        Directory.Exists(tmp["이미지"]).Should().BeTrue();

        OrganizeExecutor.Undo(journal, LocalMoveOperations.Instance).AlreadyUndone.Should().BeTrue();
    }

    [Fact]
    public void Undo_leaves_modified_files_where_they_are()
    {
        using var tmp = new TempDir();
        tmp.File("a.txt", "A");
        tmp.File("b.txt", "B");
        var plan = OrganizePlanner.Plan(OrganizeScanner.Scan(tmp.Path, false), new OrganizeOptions(), p => File.Exists(p) || Directory.Exists(p));
        var journal = tmp["o.journal"];
        OrganizeExecutor.Execute(plan, journal, LocalMoveOperations.Instance);
        File.WriteAllText(tmp[@"문서\a.txt"], "changed after organize");

        var u = OrganizeExecutor.Undo(journal, LocalMoveOperations.Instance);
        u.Restored.Should().Be(1);
        u.LeftModified.Should().ContainSingle().Which.Should().Be(tmp[@"문서\a.txt"]);
        File.Exists(tmp["b.txt"]).Should().BeTrue();
        Directory.Exists(tmp["문서"]).Should().BeTrue("수정된 파일이 남아 있어 빈 폴더가 아님");
    }

    [Fact]
    public void Excluded_moves_are_not_executed_and_locked_files_are_skipped()
    {
        using var tmp = new TempDir();
        tmp.File("keep.txt");
        tmp.File("move.pdf");
        var locked = tmp.File("locked.zip");
        var plan = OrganizePlanner.Plan(OrganizeScanner.Scan(tmp.Path, false), new OrganizeOptions(), p => File.Exists(p) || Directory.Exists(p));
        plan.Moves.Single(m => m.Source.EndsWith("keep.txt", StringComparison.Ordinal)).Included = false;

        OrganizeResult r;
        using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            r = OrganizeExecutor.Execute(plan, tmp["o.journal"], LocalMoveOperations.Instance);
        }

        r.Moved.Should().Be(1);
        r.Failed.Should().ContainSingle().Which.Path.Should().Be(locked);
        File.Exists(tmp["keep.txt"]).Should().BeTrue();
        File.Exists(locked).Should().BeTrue();
    }

    [Fact]
    public void Never_overwrites_if_destination_appears_after_planning()
    {
        using var tmp = new TempDir();
        tmp.File("a.pdf", "mine");
        var plan = OrganizePlanner.Plan(OrganizeScanner.Scan(tmp.Path, false), new OrganizeOptions(), p => File.Exists(p) || Directory.Exists(p));
        tmp.File(@"문서\a.pdf", "appeared later");
        OrganizeExecutor.Execute(plan, tmp["o.journal"], LocalMoveOperations.Instance);
        File.ReadAllText(tmp[@"문서\a.pdf"]).Should().Be("appeared later");
        File.ReadAllText(tmp[@"문서\a (2).pdf"]).Should().Be("mine");
    }

    [Fact]
    public void Journal_reader_drops_torn_tail()
    {
        using var tmp = new TempDir();
        var path = tmp["j.journal"];
        using (var w = new OrganizeJournalWriter(path))
        {
            w.Append(new JournalRecord { Type = JournalRecordType.Header, Root = tmp.Path });
            w.Append(new JournalRecord { Type = JournalRecordType.Move, Src = "a", Dst = "b", Size = 3 });
        }

        using (var fs = new FileStream(path, FileMode.Append))
        {
            fs.Write([50, 0, 0, 0, 1, 2, 3, 4, 9]); // 잘린 레코드
        }

        var recs = OrganizeJournalReader.Read(path, out var truncated);
        recs.Should().HaveCount(2);
        truncated.Should().BeTrue();
        recs[1].Src.Should().Be("a");
    }

    [Fact]
    public void Crc32_matches_known_vector() =>
        Crc32.Compute("123456789"u8).Should().Be(0xCBF43926u);
}
