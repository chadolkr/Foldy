using Foldy.Core.Descriptions;
using Foldy.Core.Organize;
using Foldy.Core.Templates;
using Foldy.Presentation.Services;
using Foldy.Presentation.Templates;

namespace Foldy.Templates.Tests;

public class TemplateDialogViewModelTests
{
    private static TemplateLibrary Library(TempDir tmp) => new TemplateLibrary(tmp["user"]).Reload();

    [Fact]
    public void First_pick_shows_guide_then_preview_and_create_works()
    {
        using var tmp = new TempDir();
        var target = tmp.Dir("work");
        var vm = new TemplateDialogViewModel(Library(tmp), target, clock: () => new DateTime(2026, 11, 18));
        vm.SelectedTemplate!.Template.Id.Should().Be("para", "일반 문서 관리 중 가장 대중적인 PARA가 처음 (2026-11-21)");
        vm.SelectedTab.Should().Be(TemplateDialogTab.Guide);

        vm.SelectedTemplate = vm.Templates.Single(t => t.Template.Id == "client-project");
        vm.SelectedTemplate = vm.Templates.Single(t => t.Template.Id == "para");
        vm.SelectedTemplate = vm.Templates.Single(t => t.Template.Id == "client-project");
        vm.SelectedTab.Should().Be(TemplateDialogTab.Preview, "두 번째부터는 미리보기");

        vm.CreateCommand.CanExecute(null).Should().BeFalse("필수 변수 비어 있음");
        vm.Customizer.Variables.Single(v => v.Key == "고객").Value = "ACME";
        vm.Customizer.Variables.Single(v => v.Key == "프로젝트").Value = "개편";
        vm.Customizer.PreviewRoots.Single().Name.Should().Be("ACME");
        vm.CreateCommand.CanExecute(null).Should().BeTrue();

        // 99_보관 체크 해제 → 만들지 않음
        var keep = vm.Customizer.PreviewRoots[0].Children[0].Children.Single(c => c.Name == "99_보관");
        keep.IsChecked = false;
        vm.CreateCommand.Execute(null);
        vm.DialogResult.Should().BeTrue();
        vm.CreatedTopFolder.Should().Be(Path.Combine(target, "ACME", "2026-11-18_개편"));
        Directory.Exists(Path.Combine(vm.CreatedTopFolder!, "01_계약")).Should().BeTrue();
        Directory.Exists(Path.Combine(vm.CreatedTopFolder!, "99_보관")).Should().BeFalse();
        File.Exists(Path.Combine(vm.CreatedTopFolder!, FolderDescription.FileName)).Should().BeTrue();
    }

    [Fact]
    public void Values_are_remembered_per_template_when_switching()
    {
        using var tmp = new TempDir();
        var vm = new TemplateDialogViewModel(Library(tmp), tmp.Path);
        vm.SelectedTemplate = vm.Templates.Single(t => t.Template.Id == "client-project");
        vm.Customizer.Variables.Single(v => v.Key == "고객").Value = "ACME";
        vm.SelectedTemplate = vm.Templates.Single(t => t.Template.Id == "para");
        vm.SelectedTemplate = vm.Templates.Single(t => t.Template.Id == "client-project");
        vm.Customizer.Variables.Single(v => v.Key == "고객").Value.Should().Be("ACME");
    }
}

public class FolderWizardViewModelTests
{
    private static FolderWizardViewModel Wizard(TempDir tmp, out TemplateLibrary lib)
    {
        lib = new TemplateLibrary(tmp["user"]).Reload();
        return new FolderWizardViewModel(TemplateLibrary.LoadWizard(), lib, tmp.Dir("target"), clock: () => new DateTime(2026, 11, 18));
    }

    [Fact]
    public void Full_flow_with_back_keeps_inputs()
    {
        using var tmp = new TempDir();
        var vm = Wizard(tmp, out var lib);
        vm.CanGoNext.Should().BeFalse();
        vm.SelectPurpose("personal");
        vm.NextCommand.Execute(null);
        vm.CurrentStep.Should().Be(WizardStep.Questions);
        vm.Questions.Should().HaveCount(3);
        vm.CanGoNext.Should().BeFalse("모든 질문에 답해야 함");
        vm.Answer("deadlines", "yes");
        vm.Answer("numbers", "no");
        vm.Answer("share", "no");
        vm.NextCommand.Execute(null);

        vm.CurrentStep.Should().Be(WizardStep.Recommend);
        vm.Recommendations[0].Template.Id.Should().Be("para");
        vm.Recommendations[0].ReasonText.Should().NotBeEmpty();
        vm.Recommendations[0].ExampleTree.Should().Contain("1_Projects/");

        // 이전으로 돌아가도 답이 남아 있다
        vm.BackCommand.Execute(null);
        vm.CurrentStep.Should().Be(WizardStep.Questions);
        vm.Questions.Single(q => q.Question.Id == "deadlines").SelectedChoiceId.Should().Be("yes");
        vm.BackCommand.Execute(null);
        vm.CurrentStep.Should().Be(WizardStep.Purpose);
        vm.NextCommand.Execute(null);
        vm.Questions.Single(q => q.Question.Id == "numbers").SelectedChoiceId.Should().Be("no");
        vm.NextCommand.Execute(null);
        vm.NextCommand.Execute(null);

        vm.CurrentStep.Should().Be(WizardStep.Customize);
        vm.Customizer.StripNumberPrefix = true;
        vm.Customizer.PreviewRoots.Select(r => r.Name).Should().Contain("Projects");
        vm.NextCommand.Execute(null);
        vm.CurrentStep.Should().Be(WizardStep.Confirm);
        vm.BackCommand.Execute(null);
        vm.Customizer.StripNumberPrefix.Should().BeTrue("맞춘 값 유지");
        vm.NextCommand.Execute(null);
        vm.SaveAsMyTemplate = true;
        vm.NextLabel.Should().Be("만들기");
        vm.NextCommand.Execute(null);

        vm.CurrentStep.Should().Be(WizardStep.Done);
        vm.Error.Should().BeNull();
        Directory.Exists(tmp[@"target\Projects"]).Should().BeTrue();
        vm.FirstSteps.Should().HaveCount(3);
        vm.SavedTemplatePath.Should().NotBeNull();
        lib.Reload().Templates.Should().Contain(t => !t.IsBuiltIn && t.Purposes.Contains("personal"));
        vm.Steps.Take(5).Should().OnlyContain(s => s.IsCompleted);
        vm.NextCommand.Execute(null);
        vm.DialogResult.Should().BeTrue();
    }

    [Fact]
    public void Organize_purpose_hands_off()
    {
        using var tmp = new TempDir();
        var vm = Wizard(tmp, out _);
        string? requested = null;
        vm.OrganizeRequested += (_, folder) => requested = folder;
        vm.SelectPurpose("organize");
        vm.NextLabel.Should().Be("정리 시작");
        vm.NextCommand.Execute(null);
        requested.Should().Be(tmp["target"]);
        vm.DialogResult.Should().BeFalse();
    }

    [Fact]
    public void Pick_template_directly_shows_all()
    {
        using var tmp = new TempDir();
        var vm = Wizard(tmp, out _);
        vm.PickTemplateDirectlyCommand.Execute(null);
        vm.CurrentStep.Should().Be(WizardStep.Recommend);
        vm.Recommendations.Should().HaveCount(TemplateLibrary.LoadBuiltIn().Count);
        vm.CanGoBack.Should().BeFalse();
    }
}

public class OrganizeViewModelTests
{
    [Fact]
    public async Task Plan_preview_exclude_execute_undo()
    {
        using var tmp = new TempDir();
        tmp.File("a.pdf", "A");
        tmp.File("b.jpg", "B");
        tmp.File("c.mp3", "C");
        var vm = new OrganizeViewModel(tmp.Path, new InlineDispatcher(), journalFolder: tmp["jobs"]);
        vm.ExecuteCommand.CanExecute(null).Should().BeFalse();
        await vm.PlanCommand.ExecuteAsync(null);
        vm.State.Should().Be(OrganizeState.Ready);
        vm.Moves.Should().HaveCount(3);
        vm.AfterTree.Select(n => n.Name).Should().BeEquivalentTo(["문서", "이미지", "음악"]);

        vm.Moves.Single(m => m.SourceName == "c.mp3").IsIncluded = false;
        vm.AfterTree.Select(n => n.Name).Should().BeEquivalentTo(["문서", "이미지"]);
        vm.Moves.Single(m => m.SourceName == "b.jpg").TargetFolder = "사진";
        vm.Summary.Should().Contain("옮김 2개");

        await vm.ExecuteCommand.ExecuteAsync(null);
        vm.State.Should().Be(OrganizeState.Done);
        File.Exists(tmp[@"문서\a.pdf"]).Should().BeTrue();
        File.Exists(tmp[@"사진\b.jpg"]).Should().BeTrue();
        File.Exists(tmp["c.mp3"]).Should().BeTrue();

        await vm.UndoCommand.ExecuteAsync(null);
        vm.State.Should().Be(OrganizeState.Undone);
        vm.LastUndo!.Restored.Should().Be(2);
        File.Exists(tmp["a.pdf"]).Should().BeTrue();
        Directory.Exists(tmp["사진"]).Should().BeFalse();
    }

    [Fact]
    public async Task Template_mode_and_blocked_root()
    {
        using var tmp = new TempDir();
        tmp.File("계약서.pdf");
        var vm = new OrganizeViewModel(tmp.Path, new InlineDispatcher(), templates: TemplateLibrary.LoadBuiltIn(), journalFolder: tmp["jobs"]);
        vm.SelectedMode = vm.Modes.Single(m => m.Mode == OrganizeMode.ByTemplate);
        vm.SelectedTemplate = vm.Templates.Single(t => t.Id == "client-project");
        await vm.PlanCommand.ExecuteAsync(null);
        vm.Moves.Single().TargetFolder.Should().Be("01_계약");

        tmp.File(".git", "gitdir: x");
        await vm.PlanCommand.ExecuteAsync(null);
        vm.State.Should().Be(OrganizeState.Error);
        vm.Status.Should().Contain("프로젝트");
    }
}

public class DescriptionEditorViewModelTests
{
    [Fact]
    public void Edit_and_save_round_trip()
    {
        using var tmp = new TempDir();
        var folder = tmp.Dir("f");
        var svc = new FolderDescriptionService(new JsonFolderNoteStore(tmp["notes.json"]));
        var vm = new DescriptionEditorViewModel(svc, folder);
        vm.Load();
        vm.Location.Should().Be(DescriptionLocation.None);
        vm.Title = "세금";
        vm.TagsText = "#진행중, 2026";
        vm.Body = "연말정산 자료";
        vm.PreviewMarkdown.Should().Contain("tags: [진행중, 2026]");
        vm.SaveCommand.Execute(null);
        vm.Location.Should().Be(DescriptionLocation.File);

        var again = new DescriptionEditorViewModel(svc, folder);
        again.Load();
        again.Title.Should().Be("세금");
        again.TagsText.Should().Be("진행중, 2026");
        again.IsDirty.Should().BeFalse();

        var banner = new DescriptionBannerViewModel();
        banner.Show(svc.Read(folder));
        banner.Headline.Should().Be("세금");
        banner.FirstParagraph.Should().Be("연말정산 자료");
    }
}
