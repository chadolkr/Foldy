using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Foldy.Core.Templates;

namespace Foldy.Presentation.Templates;

public enum TemplateDialogTab { Preview = 0, Guide = 1 }

/// <summary>
/// "템플릿으로 만들기" 대화상자 (설계서 5.10, 명령 folder.fromTemplate, Ctrl+Shift+N).
/// 왼쪽 목록 → 오른쪽 [미리보기] [사용법] 탭 → [만들기]. 처음 고르는 스타일은 [사용법]을 먼저 보여준다.
/// 만든 결과는 <see cref="Result"/>로 남겨 호스트가 Ctrl+Z(<see cref="TemplateApplier.Undo"/>)에 쓴다.
/// </summary>
public sealed partial class TemplateDialogViewModel : ObservableObject
{
    private readonly TemplateLibrary _library;
    private readonly ITemplateFileSystem _fs;
    private readonly ISet<string> _seenGuides;

    /// <param name="seenGuides">사용법을 이미 본 템플릿 id (호스트가 설정에 보관). 없으면 이 창에서만 기억.</param>
    public TemplateDialogViewModel(TemplateLibrary library, string targetFolder, ITemplateFileSystem? fs = null, ISet<string>? seenGuides = null, Func<DateTime>? clock = null)
    {
        ArgumentNullException.ThrowIfNull(library);
        _library = library;
        _fs = fs ?? LocalTemplateFileSystem.Instance;
        _seenGuides = seenGuides ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Customizer = new TemplateCustomizer(_fs, clock) { TargetFolder = targetFolder };

        // 미리보기가 바뀔 때 [만들기] 활성 상태를 갱신한다
        Customizer.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(TemplateCustomizer.CanApply))
            {
                CreateCommand.NotifyCanExecuteChanged();
            }
        };
        foreach (var item in Grouped(library.Templates.Select(t => new TemplateItemViewModel(t))))
        {
            Templates.Add(item);
        }

        SelectedTemplate = Templates.FirstOrDefault();
    }

    public ObservableCollection<TemplateItemViewModel> Templates { get; } = [];

    /// <summary>묶음(개인·업무, 개발 ...) 순서로 안정 정렬하고, 묶음의 첫 항목에 제목을 단다.</summary>
    private static IEnumerable<TemplateItemViewModel> Grouped(IEnumerable<TemplateItemViewModel> items)
    {
        string? last = null;
        foreach (var item in items.OrderBy(i => TemplateCategories.OrderOf(i.Category)).ThenBy(i => TemplateCategories.OrderOf(i.Category) == 100 ? i.Category : "", StringComparer.CurrentCulture).ThenBy(i => TemplateCategories.PopularityOf(i.Template.Id)))
        {
            item.CategoryHeader = string.Equals(last, item.Category, StringComparison.Ordinal) ? null : item.Category;
            last = item.Category;
            yield return item;
        }
    }

    public TemplateCustomizer Customizer { get; }

    [ObservableProperty]
    private TemplateItemViewModel? _selectedTemplate;

    [ObservableProperty]
    private TemplateDialogTab _selectedTab;

    /// <summary>최상위 폴더의 .folder_description.md에 사용법 넣기 (기본 켜짐).</summary>
    [ObservableProperty]
    private bool _writeDescription = true;

    [ObservableProperty]
    private string? _error;

    /// <summary>창 닫기 신호 (true = 만들었음, false = 취소). UI의 DialogCloser가 지켜본다.</summary>
    [ObservableProperty]
    private bool? _dialogResult;

    public TemplateApplyResult? Result { get; private set; }

    /// <summary>만든 뒤 이동·선택할 폴더.</summary>
    public string? CreatedTopFolder => Result?.TopFolder;

    public int SelectedTabIndex
    {
        get => (int)SelectedTab;
        set => SelectedTab = (TemplateDialogTab)value;
    }

    partial void OnSelectedTabChanged(TemplateDialogTab value) => OnPropertyChanged(nameof(SelectedTabIndex));

    partial void OnSelectedTemplateChanged(TemplateItemViewModel? value)
    {
        Customizer.Template = value?.Template;
        if (value is not null)
        {
            SelectedTab = _seenGuides.Add(value.Template.Id) && !string.IsNullOrWhiteSpace(value.Guide)
                ? TemplateDialogTab.Guide
                : TemplateDialogTab.Preview;
        }

        CreateCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanCreate))]
    private void Create()
    {
        var plan = Customizer.Plan;
        var template = Customizer.Template;
        if (plan is null || template is null || !plan.CanApply)
        {
            return;
        }

        try
        {
            Result = TemplateApplier.Apply(plan, template, _fs, new TemplateApplyOptions { WriteDescription = WriteDescription });
            if (!Result.Succeeded)
            {
                Error = Foldy.Core.Localization.Loc.T("wiz.partial") + string.Join(", ", Result.Failed.Select(f => Path.GetFileName(f.Path)));
                return;
            }

            OnPropertyChanged(nameof(CreatedTopFolder));
            DialogResult = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Error = ex.Message;
        }
    }

    private bool CanCreate() => Customizer.CanApply;

    [RelayCommand]
    private void Cancel() => DialogResult = false;

    /// <summary>템플릿 관리: 내 템플릿 가져오기.</summary>
    public FolderTemplate? Import(string file)
    {
        try
        {
            var t = _library.Import(file);
            var item = new TemplateItemViewModel(t);
            var all = Grouped(Templates.Append(item)).ToList();
            Templates.Clear();
            foreach (var x in all)
            {
                Templates.Add(x);
            }

            SelectedTemplate = item;
            return t;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or System.Text.Json.JsonException or UnauthorizedAccessException)
        {
            Error = ex.Message;
            return null;
        }
    }
}
