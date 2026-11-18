using Foldy.Core.Descriptions;
using Foldy.Core.Templates;

namespace Foldy.Templates.Tests;

public class VariableTests
{
    private static readonly DateTime Now = new(2026, 11, 18, 14, 32, 5);

    [Fact]
    public void Substitutes_keys_and_dates()
    {
        var values = new Dictionary<string, string> { ["고객"] = "ACME", ["프로젝트"] = "홈페이지" };
        TemplateVariables.Substitute("{{고객}}/{{date:yyyy-MM-dd}}_{{프로젝트}}", values, Now)
            .Should().Be("ACME/2026-11-18_홈페이지");
    }

    [Fact]
    public void Shorthand_date_keys_are_dates() =>
        TemplateVariables.Substitute("{{yyyy}}/{{yyyy-MM-dd}}_x", new Dictionary<string, string>(), Now).Should().Be("2026/2026-11-18_x");

    [Fact]
    public void Missing_values_are_reported_and_left_visible()
    {
        var missing = new List<string>();
        TemplateVariables.Substitute("{{고객}}_{{ 프로젝트 }}", new Dictionary<string, string> { ["고객"] = " " }, Now, missing)
            .Should().Be("{고객}_{프로젝트}");
        missing.Should().Equal("고객", "프로젝트");
    }

    [Fact]
    public void Extract_keys_skips_dates_and_dedupes() =>
        TemplateVariables.ExtractKeys("{{a}}/{{date:yyyy}}_{{b}}_{{a}}").Should().Equal("a", "b");

    [Theory]
    [InlineData("ACME", true)]
    [InlineData("A/B", false)]
    [InlineData("..", false)]
    [InlineData("a:b", false)]
    [InlineData("a*", false)]
    public void Values_with_path_characters_are_rejected(string value, bool ok) =>
        (TemplateVariables.ValidateValue(value) is null).Should().Be(ok);
}

public class ValidatorTests
{
    private static FolderTemplate With(params TemplateNode[] nodes) => new() { Id = "t", Name = "t", Tree = nodes.ToList() };

    [Theory]
    [InlineData("/abs", TemplateIssueKind.AbsolutePath)]
    [InlineData("\\abs", TemplateIssueKind.AbsolutePath)]
    [InlineData("C:\\Windows", TemplateIssueKind.DriveLetter)]
    [InlineData("c:x", TemplateIssueKind.DriveLetter)]
    [InlineData("a/../b", TemplateIssueKind.ParentSegment)]
    [InlineData("..", TemplateIssueKind.ParentSegment)]
    [InlineData("CON", TemplateIssueKind.ReservedName)]
    [InlineData("nul.txt", TemplateIssueKind.ReservedName)]
    [InlineData("docs/COM1", TemplateIssueKind.ReservedName)]
    [InlineData("a<b", TemplateIssueKind.InvalidChar)]
    [InlineData("a|b", TemplateIssueKind.InvalidChar)]
    [InlineData("a?", TemplateIssueKind.InvalidChar)]
    [InlineData("trailing.", TemplateIssueKind.TrailingDotOrSpace)]
    [InlineData("a//b", TemplateIssueKind.EmptyName)]
    public void Rejects_unsafe_names(string name, TemplateIssueKind kind) =>
        TemplateValidator.Validate(With(new TemplateNode { Name = name })).Select(i => i.Kind).Should().Contain(kind);

    [Fact]
    public void Accepts_placeholders_with_colons_and_dotfiles() =>
        TemplateValidator.Validate(With(
            new TemplateNode { Name = "{{고객}}/{{date:yyyy-MM-dd}}_{{프로젝트}}", Children = [new TemplateNode { Name = ".gitignore", File = "bin/\n" }] }))
            .Should().BeEmpty();

    [Fact]
    public void Rejects_large_binary_and_executable_files()
    {
        var issues = TemplateValidator.Validate(With(
            new TemplateNode { Name = "big.txt", File = new string('a', 64 * 1024 + 1) },
            new TemplateNode { Name = "bin.txt", File = "a\0b" },
            new TemplateNode { Name = "run.exe", File = "MZ" },
            new TemplateNode { Name = "link.lnk", File = "" },
            new TemplateNode { Name = "s.ps1", File = "" })).Select(i => i.Kind).ToList();
        issues.Should().Contain(TemplateIssueKind.FileTooLarge);
        issues.Should().Contain(TemplateIssueKind.BinaryContent);
        issues.Count(k => k == TemplateIssueKind.ForbiddenFileType).Should().Be(3);
    }

    [Fact]
    public void File_of_exactly_64KB_is_allowed() =>
        TemplateValidator.Validate(With(new TemplateNode { Name = "ok.md", File = new string('a', 64 * 1024) })).Should().BeEmpty();

    [Fact]
    public void Too_many_nodes_rejected()
    {
        var nodes = Enumerable.Range(0, 501).Select(i => new TemplateNode { Name = "d" + i }).ToArray();
        TemplateValidator.Validate(With(nodes)).Select(i => i.Kind).Should().Contain(TemplateIssueKind.TooManyNodes);
    }

    [Fact]
    public void Duplicate_names_rejected_unless_conditions_differ()
    {
        TemplateValidator.Validate(With(new TemplateNode { Name = "a" }, new TemplateNode { Name = "A" }))
            .Select(i => i.Kind).Should().Contain(TemplateIssueKind.DuplicateName);
        TemplateValidator.Validate(With(new TemplateNode { Name = ".gitignore", File = "", When = "l=a" }, new TemplateNode { Name = ".gitignore", File = "", When = "l=b" }))
            .Should().BeEmpty();
    }

    [Fact]
    public void Imported_json_with_traversal_is_refused()
    {
        using var tmp = new TempDir();
        var f = tmp.File("evil.foldy-template.json", """{ "schemaVersion": 1, "name": "evil", "tree": [ { "name": "../../escape" } ] }""");
        var lib = new TemplateLibrary(tmp["user"]);
        var act = () => lib.Import(f);
        act.Should().Throw<InvalidDataException>();
        Directory.Exists(tmp["user"]).Should().BeFalse();
    }
}

public class PlannerTests
{
    private static readonly DateTime Now = new(2026, 11, 18);

    private static FolderTemplate Client() => TemplateLibrary.LoadBuiltIn().Single(t => t.Id == "client-project");

    private static TemplatePlanOptions Values(params (string K, string V)[] kv) =>
        new() { Values = kv.ToDictionary(x => x.K, x => x.V), Now = Now };

    [Fact]
    public void Plans_multi_segment_name_and_children()
    {
        using var tmp = new TempDir();
        var plan = TemplatePlanner.Plan(Client(), tmp.Path, Values(("고객", "ACME"), ("프로젝트", "홈페이지")), LocalTemplateFileSystem.Instance);
        plan.CanApply.Should().BeTrue();
        plan.Roots.Should().ContainSingle().Which.Name.Should().Be("ACME");
        var proj = plan.Roots[0].Children.Single();
        proj.Name.Should().Be("2026-11-18_홈페이지");
        proj.Children.Select(c => c.Name).Should().Contain(["01_계약", "04_산출물", "99_보관", "README.md"]);
        plan.TopFolder.Should().Be(Path.Combine(tmp.Path, "ACME", "2026-11-18_홈페이지"));
    }

    [Fact]
    public void Missing_required_variables_block_apply()
    {
        using var tmp = new TempDir();
        var plan = TemplatePlanner.Plan(Client(), tmp.Path, Values(("고객", "ACME")), LocalTemplateFileSystem.Instance);
        plan.CanApply.Should().BeFalse();
        plan.MissingVariables.Should().Equal("프로젝트");
    }

    [Fact]
    public void Value_with_separator_is_blocked()
    {
        using var tmp = new TempDir();
        var plan = TemplatePlanner.Plan(Client(), tmp.Path, Values(("고객", "..\\..\\x"), ("프로젝트", "p")), LocalTemplateFileSystem.Instance);
        plan.CanApply.Should().BeFalse();
    }

    [Fact]
    public void Creates_only_missing_and_never_overwrites()
    {
        using var tmp = new TempDir();
        var proj = Path.Combine("ACME", "2026-11-18_홈페이지");
        tmp.Dir(Path.Combine(proj, "01_계약"));
        tmp.File(Path.Combine(proj, "01_계약", "keep.pdf"), "mine");
        tmp.File(Path.Combine(proj, "README.md"), "내가 쓴 README");
        var t = Client();
        var plan = TemplatePlanner.Plan(t, tmp.Path, Values(("고객", "ACME"), ("프로젝트", "홈페이지")), LocalTemplateFileSystem.Instance);
        plan.Flatten().Single(n => n.Name == "01_계약").State.Should().Be(PlannedState.Exists);
        plan.Flatten().Single(n => n.Name == "README.md").State.Should().Be(PlannedState.Exists);

        var result = TemplateApplier.Apply(plan, t, LocalTemplateFileSystem.Instance);
        result.Succeeded.Should().BeTrue();
        File.ReadAllText(tmp[Path.Combine(proj, "README.md")]).Should().Be("내가 쓴 README");
        File.ReadAllText(tmp[Path.Combine(proj, "01_계약", "keep.pdf")]).Should().Be("mine");
        Directory.Exists(tmp[Path.Combine(proj, "02_자료")]).Should().BeTrue();
        result.Created.Should().NotContain(c => c.Path.EndsWith("01_계약", StringComparison.Ordinal));
    }

    [Fact]
    public void File_where_folder_exists_is_blocked_not_replaced()
    {
        using var tmp = new TempDir();
        tmp.File("2_Areas", "a file squatting the name");
        var t = TemplateLibrary.LoadBuiltIn().Single(x => x.Id == "para");
        var plan = TemplatePlanner.Plan(t, tmp.Path, new TemplatePlanOptions { Now = Now }, LocalTemplateFileSystem.Instance);
        plan.Flatten().Single(n => n.Name == "2_Areas").State.Should().Be(PlannedState.Blocked);
        TemplateApplier.Apply(plan, t, LocalTemplateFileSystem.Instance);
        File.ReadAllText(tmp["2_Areas"]).Should().Be("a file squatting the name");
    }

    [Fact]
    public void Excluded_folder_excludes_children()
    {
        using var tmp = new TempDir();
        var t = TemplateLibrary.LoadBuiltIn().Single(x => x.Id == "johnny-decimal");
        var plan = TemplatePlanner.Plan(t, tmp.Path, new TemplatePlanOptions { Now = Now, Excluded = new HashSet<string> { "1" } }, LocalTemplateFileSystem.Instance);
        var life = plan.Roots[1];
        life.State.Should().Be(PlannedState.Excluded);
        life.Children.Should().OnlyContain(c => c.State == PlannedState.Excluded);
        TemplateApplier.Apply(plan, t, LocalTemplateFileSystem.Instance, new TemplateApplyOptions { WriteDescription = false });
        Directory.Exists(tmp["10-19 생활"]).Should().BeFalse();
        Directory.Exists(tmp["20-29 돈"]).Should().BeTrue();
    }

    [Fact]
    public void Language_condition_and_default_pick_one_gitignore()
    {
        using var tmp = new TempDir();
        var t = TemplateLibrary.LoadBuiltIn().Single(x => x.Id == "software-project");
        var plan = TemplatePlanner.Plan(t, tmp.Path, Values(("프로젝트", "app"), ("language", "rust")), LocalTemplateFileSystem.Instance);
        var gi = plan.Flatten().Where(n => n.Name == ".gitignore").ToList();
        gi.Should().ContainSingle().Which.Content.Should().Contain("/target/");

        var defaulted = TemplatePlanner.Plan(t, tmp.Path, Values(("프로젝트", "app")), LocalTemplateFileSystem.Instance);
        defaulted.Flatten().Single(n => n.Name == ".gitignore").Content.Should().Contain("obj/");
        defaulted.Flatten().Single(n => n.Name == "assets").State.Should().Be(PlannedState.Excluded, "optional 폴더는 기본 해제");
    }

    [Fact]
    public void Strip_number_prefix_and_english_names()
    {
        using var tmp = new TempDir();
        var plan = TemplatePlanner.Plan(Client(), tmp.Path, new TemplatePlanOptions
        {
            Values = new Dictionary<string, string> { ["고객"] = "A", ["프로젝트"] = "B" }, Now = Now, StripNumberPrefix = true, UseEnglishNames = true,
        }, LocalTemplateFileSystem.Instance);
        plan.Roots[0].Children[0].Children.Select(c => c.Name).Should().Contain(["contract", "archive", "README.md"]);
    }

    [Fact]
    public void Apply_writes_hidden_description_with_template_and_undo_removes_all()
    {
        using var tmp = new TempDir();
        var t = Client();
        var plan = TemplatePlanner.Plan(t, tmp.Path, Values(("고객", "ACME"), ("프로젝트", "P")), LocalTemplateFileSystem.Instance);
        var r = TemplateApplier.Apply(plan, t, LocalTemplateFileSystem.Instance);
        var desc = Path.Combine(r.TopFolder, FolderDescription.FileName);
        File.Exists(desc).Should().BeTrue();
        (File.GetAttributes(desc) & FileAttributes.Hidden).Should().Be(FileAttributes.Hidden);
        var parsed = FolderDescriptionParser.Parse(File.ReadAllText(desc));
        parsed.Template.Should().Be("client-project");
        parsed.Body.Should().Contain("04_산출물");

        TemplateApplier.Undo(r, LocalTemplateFileSystem.Instance).Should().BeEmpty();
        Directory.Exists(tmp["ACME"]).Should().BeFalse();
    }

    [Fact]
    public void Undo_keeps_user_changes()
    {
        using var tmp = new TempDir();
        var t = Client();
        var plan = TemplatePlanner.Plan(t, tmp.Path, Values(("고객", "ACME"), ("프로젝트", "P")), LocalTemplateFileSystem.Instance);
        var r = TemplateApplier.Apply(plan, t, LocalTemplateFileSystem.Instance, new TemplateApplyOptions { WriteDescription = false });
        File.WriteAllText(Path.Combine(r.TopFolder, "README.md"), "edited");
        File.WriteAllText(Path.Combine(r.TopFolder, "03_작업", "new.txt"), "user file");
        var left = TemplateApplier.Undo(r, LocalTemplateFileSystem.Instance);
        File.Exists(Path.Combine(r.TopFolder, "README.md")).Should().BeTrue();
        File.Exists(Path.Combine(r.TopFolder, "03_작업", "new.txt")).Should().BeTrue();
        Directory.Exists(Path.Combine(r.TopFolder, "01_계약")).Should().BeFalse();
        left.Should().Contain(Path.Combine(r.TopFolder, "README.md"));
    }
}

public class LibraryAndCaptureTests
{
    [Fact]
    public void Five_builtins_with_guides_load_and_validate()
    {
        var all = TemplateLibrary.LoadBuiltIn();
        all.Select(t => t.Id).Should().BeEquivalentTo(["para", "johnny-decimal", "software-project", "client-project", "photo-archive"]);
        foreach (var t in all)
        {
            TemplateValidator.Validate(t).Should().BeEmpty(t.Id);
            t.Guide.Should().NotBeNullOrWhiteSpace(t.Id);
            t.Guide.Should().Contain("이럴 땐 다른 스타일을", t.Id);
            t.Naming.Should().NotBeEmpty(t.Id);
            t.FirstSteps.Should().HaveCount(3, t.Id);
            t.IsBuiltIn.Should().BeTrue();
        }
    }

    [Fact]
    public void Wizard_json_references_existing_templates()
    {
        var wiz = TemplateLibrary.LoadWizard();
        var ids = TemplateLibrary.LoadBuiltIn().Select(t => t.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        wiz.Purposes.Should().Contain(p => p.Action == "organize");
        foreach (var w in wiz.Purposes.SelectMany(p => p.Weights.Keys).Concat(wiz.Questions.SelectMany(q => q.Choices).SelectMany(c => c.Weights.Keys)))
        {
            ids.Should().Contain(w);
        }

        wiz.QuestionsFor("unsure").Count.Should().BeGreaterThan(wiz.QuestionsFor("personal").Count, "잘 모르겠어요는 질문을 하나 더");
    }

    [Fact]
    public void Scoring_recommends_by_answers()
    {
        var wiz = TemplateLibrary.LoadWizard();
        var all = TemplateLibrary.LoadBuiltIn();
        var top = WizardScorer.Top(WizardScorer.Rank(wiz, "personal", new Dictionary<string, string> { ["numbers"] = "yes", ["deadlines"] = "no" }, all));
        top[0].Template.Id.Should().Be("johnny-decimal");
        top[0].Reasons.Should().NotBeEmpty();
        WizardScorer.Top(WizardScorer.Rank(wiz, "work", new Dictionary<string, string> { ["clients"] = "yes" }, all))[0].Template.Id.Should().Be("client-project");
        WizardScorer.VariablesFrom(wiz, "dev", new Dictionary<string, string> { ["language"] = "python" }).Should().Contain("language", "python");
    }

    [Fact]
    public void User_templates_roundtrip_and_invalid_ones_are_listed_as_errors()
    {
        using var tmp = new TempDir();
        var lib = new TemplateLibrary(tmp["user"]);
        var t = new FolderTemplate { Name = "내 구조", Tree = [new TemplateNode { Name = "a", Children = [new TemplateNode { Name = "b.md", File = "# 안녕" }] }] };
        var path = lib.SaveUserTemplate(t);
        File.ReadAllText(path).Should().Contain("안녕"); // 한글 이스케이프 안 함
        tmp.File(Path.Combine("user", "bad.foldy-template.json"), """{ "name": "bad", "tree": [ { "name": "C:\\x" } ] }""");
        lib.Reload();
        lib.Templates.Should().Contain(x => x.Name == "내 구조" && !x.IsBuiltIn);
        lib.LoadErrors.Should().ContainSingle(e => e.Path.EndsWith("bad.foldy-template.json", StringComparison.Ordinal));
    }

    [Fact]
    public void Capture_existing_folder_with_limits_and_date_suggestions()
    {
        using var tmp = new TempDir();
        tmp.Dir(@"src\2026-11-18_회의");
        tmp.Dir(@"a\b\c\d\e\f\g");
        tmp.File(@"README.md", "# hi");
        tmp.File(@"big.txt", new string('x', 70 * 1024));
        tmp.File(@"run.exe", "MZ");
        var hidden = tmp.Dir(".hidden");
        File.SetAttributes(hidden, FileAttributes.Hidden | FileAttributes.Directory);

        var r = TemplateCapture.Capture(tmp.Path, "내 폴더", new TemplateCaptureOptions { IncludeFileContent = _ => true });
        var tree = r.Template.Tree;
        tree.Select(n => n.Name).Should().Contain(["src", "a", "README.md"]).And.NotContain([".hidden", "big.txt", "run.exe"]);
        r.SkippedFiles.Should().Contain(["big.txt", "run.exe"]);

        // 깊이 5에서 멈춤: a/b/c/d/e 까지
        var n = tree.Single(x => x.Name == "a");
        var depth = 1;
        while (n.Children.Count > 0)
        {
            n = n.Children[0];
            depth++;
        }

        depth.Should().Be(5);
        r.Truncated.Should().BeTrue();

        r.DateSuggestions.Should().ContainSingle().Which.Replacement.Should().Be("{{date:yyyy-MM-dd}}_회의");
        TemplateCapture.ApplyDateSuggestions(r.Template, r.DateSuggestions);
        tree.Single(x => x.Name == "src").Children.Single().Name.Should().Be("{{date:yyyy-MM-dd}}_회의");
        TemplateValidator.Validate(r.Template).Should().BeEmpty();
    }

    [Fact]
    public void Capture_stops_at_max_items()
    {
        using var tmp = new TempDir();
        for (var i = 0; i < 30; i++)
        {
            tmp.Dir("d" + i);
        }

        var r = TemplateCapture.Capture(tmp.Path, "x", new TemplateCaptureOptions { MaxItems = 10 });
        r.Template.Tree.Should().HaveCount(10);
        r.Truncated.Should().BeTrue();
    }

    [Theory]
    [InlineData("2026-11-18_x", "{{date:yyyy-MM-dd}}_x")]
    [InlineData("20261118 회의", "{{date:yyyyMMdd}} 회의")]
    [InlineData("2026-13-45", "2026-13-45")]
    [InlineData("v20250101", "v{{date:yyyyMMdd}}")]
    [InlineData("12345678", "12345678")]
    [InlineData("99999999", "99999999")]
    public void Date_suggestion(string input, string expected) => TemplateCapture.SuggestDateVariables(input).Should().Be(expected);
}

public class DescriptionTests
{
    [Fact]
    public void Parses_design_example_front_matter()
    {
        const string md = "---\ntitle: ACME 홈페이지 개편\ntags: [고객, 2026, 진행중]\ntemplate: client-project      # 폴더 템플릿으로 만든 경우만\nowner: 홍길동\n---\n# ACME 홈페이지 개편\n\n2026-11-18 시작. 최종 산출물은 04_산출물에만 둔다.\n";
        var d = FolderDescriptionParser.Parse(md);
        d.Title.Should().Be("ACME 홈페이지 개편");
        d.Tags.Should().Equal("고객", "2026", "진행중");
        d.Template.Should().Be("client-project");
        d.ExtraFrontMatter.Should().ContainSingle().Which.Should().Be("owner: 홍길동");
        d.FirstParagraph().Should().Be("2026-11-18 시작. 최종 산출물은 04_산출물에만 둔다.");

        var again = FolderDescriptionParser.Parse(FolderDescriptionParser.Serialize(d));
        again.Title.Should().Be(d.Title);
        again.Tags.Should().Equal(d.Tags);
        again.Template.Should().Be(d.Template);
        again.ExtraFrontMatter.Should().Equal(d.ExtraFrontMatter);
        again.Body.Should().Be(d.Body);
    }

    [Fact]
    public void No_front_matter_uses_first_heading_as_headline()
    {
        var d = FolderDescriptionParser.Parse("# 세금 자료\n\n연말정산 모음");
        d.Title.Should().BeNull();
        d.Headline.Should().Be("세금 자료");
        FolderDescriptionParser.Parse("---\ntags:\n  - a\n  - b\n---\nbody").Tags.Should().Equal("a", "b");
    }

    [Fact]
    public void Service_writes_hidden_file_atomically_and_reads_back()
    {
        using var tmp = new TempDir();
        var folder = tmp.Dir("proj");
        var svc = new FolderDescriptionService(new JsonFolderNoteStore(tmp["notes.json"]));
        svc.Write(folder, new FolderDescription { Title = "제목", Body = "본문" }).Should().Be(DescriptionLocation.File);
        var file = Path.Combine(folder, FolderDescription.FileName);
        (File.GetAttributes(file) & FileAttributes.Hidden).Should().Be(FileAttributes.Hidden);
        svc.Write(folder, new FolderDescription { Title = "바뀜", Body = "본문2" });
        (File.GetAttributes(file) & FileAttributes.Hidden).Should().Be(FileAttributes.Hidden, "교체해도 숨김 유지");
        var r = svc.Read(folder);
        r.Location.Should().Be(DescriptionLocation.File);
        r.Description!.Title.Should().Be("바뀜");
        File.Exists(file + ".tmp").Should().BeFalse();
    }

    [Fact]
    public void Drive_root_and_system_folders_use_local_store()
    {
        FolderDescriptionService.MustUseLocalStore(Path.GetPathRoot(Path.GetTempPath())!).Should().BeTrue();
        FolderDescriptionService.MustUseLocalStore(Environment.GetFolderPath(Environment.SpecialFolder.Windows)).Should().BeTrue();
        using var tmp = new TempDir();
        FolderDescriptionService.MustUseLocalStore(tmp.Path).Should().BeFalse();

        var store = new JsonFolderNoteStore(tmp["notes.json"]);
        var id = new FolderIdentity(7, 42);
        store.SetNote(@"Z:\old\name", id, "# 메모");
        store.GetNote(@"Z:\renamed", id).Should().Be("# 메모", "파일 ID로 이름이 바뀌어도 따라간다");
        store.SetNote(@"Z:\renamed", id, null);
        store.GetNote(@"Z:\old\name", id).Should().BeNull();
    }
}
