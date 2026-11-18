using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Foldy.Core.Templates;

namespace Foldy.Presentation.Templates;

/// <summary>위저드 단계 (설계서 5.10 폴더 위저드).</summary>
public enum WizardStep
{
    Purpose = 0,
    Questions = 1,
    Recommend = 2,
    Customize = 3,
    Confirm = 4,
    Done = 5,
}

public sealed partial class WizardStepItem(WizardStep step, string title) : ObservableObject
{
    public WizardStep Step { get; } = step;

    public string Title { get; } = title;

    public int Number => (int)Step + 1;

    [ObservableProperty]
    private bool _isCurrent;

    [ObservableProperty]
    private bool _isCompleted;
}

public sealed partial class SelectableOption<T>(T value, string label, string? description = null) : ObservableObject
{
    public T Value { get; } = value;

    public string Label { get; } = label;

    public string? Description { get; } = description;

    [ObservableProperty]
    private bool _isSelected;
}

/// <summary>질문 하나와 선택지. 고른 답은 위저드 전체 답 사전에 남는다 ([이전]으로 돌아와도 유지).</summary>
public sealed partial class WizardQuestionViewModel : ObservableObject
{
    private readonly Action<WizardQuestionViewModel> _answered;

    public WizardQuestionViewModel(WizardQuestion question, string? answer, Action<WizardQuestionViewModel> answered)
    {
        Question = question;
        _answered = answered;
        foreach (var c in question.Choices)
        {
            var opt = new SelectableOption<WizardChoice>(c, c.Label) { IsSelected = c.Id == answer };
            opt.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(SelectableOption<WizardChoice>.IsSelected) && opt.IsSelected)
                {
                    foreach (var other in Choices.Where(o => o != opt))
                    {
                        other.IsSelected = false;
                    }

                    _answered(this);
                }
            };
            Choices.Add(opt);
        }
    }

    public WizardQuestion Question { get; }

    public string Text => Question.Text;

    public string? Hint => Question.Hint;

    public ObservableCollection<SelectableOption<WizardChoice>> Choices { get; } = [];

    public string? SelectedChoiceId => Choices.FirstOrDefault(c => c.IsSelected)?.Value.Id;

    public void Select(string choiceId)
    {
        var c = Choices.FirstOrDefault(x => x.Value.Id == choiceId);
        if (c is not null)
        {
            c.IsSelected = true;
        }
    }
}

/// <summary>추천 카드: 이유 + 사용법 요약 + 예시 트리 + 파일명 규칙 (나란히 비교).</summary>
public sealed partial class RecommendationViewModel(WizardRecommendation rec, bool recommended, string exampleTree) : ObservableObject
{
    public FolderTemplate Template { get; } = rec.Template;

    public string Name => Template.Name;

    public double Score { get; } = rec.Score;

    public bool IsRecommended { get; } = recommended;

    public IReadOnlyList<string> Reasons { get; } = rec.Reasons;

    public string ReasonText => string.Join("\n", Reasons.Select(r => "· " + r));

    public string GuideSummary => TemplateText.GuideSummary(Template.Guide);

    public string Guide => Template.Guide ?? "";

    public string NamingSummary => TemplateText.NamingSummary(Template);

    public string ExampleTree { get; } = exampleTree;

    [ObservableProperty]
    private bool _isSelected;
}

/// <summary>
/// 폴더 위저드 (단계 상태 머신). 용도 → 질문 2~3개 → 추천과 설명 → 내 상황에 맞추기 → 확인 → 완료.
/// 단계별 입력은 창을 닫기 전까지 유지되어 [이전]으로 돌아가도 사라지지 않는다.
/// 1단계에서 "이미 파일이 있는 폴더 정리하기"를 고르면 <see cref="OrganizeRequested"/>로 5.11 폴더 정리로 넘긴다.
/// </summary>
public sealed partial class FolderWizardViewModel : ObservableObject
{
    private readonly WizardDefinition _def;
    private readonly TemplateLibrary _library;
    private readonly ITemplateFileSystem _fs;
    private readonly Dictionary<string, string> _answers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<DateTime> _clock;

    public FolderWizardViewModel(WizardDefinition definition, TemplateLibrary library, string targetFolder, ITemplateFileSystem? fs = null, Func<DateTime>? clock = null)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(library);
        _def = definition;
        _library = library;
        _fs = fs ?? LocalTemplateFileSystem.Instance;
        _clock = clock ?? (() => DateTime.Now);
        Customizer = new TemplateCustomizer(_fs, _clock) { TargetFolder = targetFolder };
        Customizer.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(TemplateCustomizer.CanApply) or nameof(TemplateCustomizer.TargetFolder))
            {
                NotifyNav();
            }
        };

        Steps.Add(new WizardStepItem(WizardStep.Purpose, "용도 고르기"));
        Steps.Add(new WizardStepItem(WizardStep.Questions, "몇 가지 질문"));
        Steps.Add(new WizardStepItem(WizardStep.Recommend, "추천과 설명"));
        Steps.Add(new WizardStepItem(WizardStep.Customize, "내 상황에 맞추기"));
        Steps.Add(new WizardStepItem(WizardStep.Confirm, "확인"));
        Steps.Add(new WizardStepItem(WizardStep.Done, "완료"));

        foreach (var p in definition.Purposes)
        {
            var opt = new SelectableOption<WizardPurpose>(p, p.Label, p.Description);
            opt.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(SelectableOption<WizardPurpose>.IsSelected) && opt.IsSelected)
                {
                    foreach (var other in Purposes.Where(o => o != opt))
                    {
                        other.IsSelected = false;
                    }

                    SelectedPurpose = p;
                }
            };
            Purposes.Add(opt);
        }

        UpdateSteps();
    }

    public string PurposeQuestion => _def.PurposeQuestion;

    public ObservableCollection<WizardStepItem> Steps { get; } = [];

    public ObservableCollection<SelectableOption<WizardPurpose>> Purposes { get; } = [];

    public ObservableCollection<WizardQuestionViewModel> Questions { get; } = [];

    public ObservableCollection<RecommendationViewModel> Recommendations { get; } = [];

    public TemplateCustomizer Customizer { get; }

    [ObservableProperty]
    private WizardStep _currentStep;

    [ObservableProperty]
    private WizardPurpose? _selectedPurpose;

    /// <summary>[모두 보기]: 추천 밖의 스타일도 목록에 보인다.</summary>
    [ObservableProperty]
    private bool _showAll;

    [ObservableProperty]
    private RecommendationViewModel? _selectedRecommendation;

    /// <summary>확인 단계 선택지.</summary>
    [ObservableProperty]
    private bool _writeDescription = true;

    [ObservableProperty]
    private bool _enableNamingHints = true;

    [ObservableProperty]
    private bool _saveAsMyTemplate;

    [ObservableProperty]
    private string? _error;

    [ObservableProperty]
    private bool? _dialogResult;

    /// <summary>템플릿 바로 고르기로 들어왔는지 (용도·질문 건너뜀).</summary>
    [ObservableProperty]
    private bool _skippedQuestions;

    public TemplateApplyResult? Result { get; private set; }

    public string? SavedTemplatePath { get; private set; }

    /// <summary>"이미 파일이 있는 폴더 정리하기": 호스트가 폴더 정리 창을 연다 (인자는 대상 폴더).</summary>
    public event EventHandler<string>? OrganizeRequested;

    public IReadOnlyDictionary<string, string> Answers => _answers;

    public IReadOnlyList<string> FirstSteps => Customizer.Template?.FirstSteps.ToList() ?? [];

    public string StepTitle => Steps[(int)CurrentStep].Title;

    public bool CanGoBack => CurrentStep is > WizardStep.Purpose and < WizardStep.Done && !(SkippedQuestions && CurrentStep == WizardStep.Recommend);

    public bool CanGoNext => CurrentStep switch
    {
        WizardStep.Purpose => SelectedPurpose is not null,
        WizardStep.Questions => Questions.All(q => q.SelectedChoiceId is not null),
        WizardStep.Recommend => SelectedRecommendation is not null,
        WizardStep.Customize => Customizer.CanApply,
        WizardStep.Confirm => Customizer.CanApply,
        WizardStep.Done => true,
        _ => false,
    };

    public string NextLabel => CurrentStep switch
    {
        WizardStep.Purpose when SelectedPurpose?.Action == "organize" => "정리 시작",
        WizardStep.Confirm => "만들기",
        WizardStep.Done => "마침",
        _ => "다음",
    };

    partial void OnCurrentStepChanged(WizardStep value)
    {
        UpdateSteps();
        OnPropertyChanged(nameof(StepTitle));
        OnPropertyChanged(nameof(FirstSteps));
        NotifyNav();
    }

    partial void OnSelectedPurposeChanged(WizardPurpose? value) => NotifyNav();

    partial void OnShowAllChanged(bool value) => BuildRecommendations();

    partial void OnSelectedRecommendationChanged(RecommendationViewModel? oldValue, RecommendationViewModel? newValue)
    {
        if (oldValue is not null)
        {
            oldValue.IsSelected = false;
        }

        if (newValue is not null)
        {
            newValue.IsSelected = true;
        }

        NotifyNav();
    }

    [RelayCommand(CanExecute = nameof(CanGoNext))]
    private void Next()
    {
        Error = null;
        switch (CurrentStep)
        {
            case WizardStep.Purpose:
                if (SelectedPurpose?.Action == "organize")
                {
                    OrganizeRequested?.Invoke(this, Customizer.TargetFolder);
                    DialogResult = false;
                    return;
                }

                BuildQuestions();
                CurrentStep = Questions.Count > 0 ? WizardStep.Questions : WizardStep.Recommend;
                if (CurrentStep == WizardStep.Recommend)
                {
                    BuildRecommendations();
                }

                break;
            case WizardStep.Questions:
                BuildRecommendations();
                CurrentStep = WizardStep.Recommend;
                break;
            case WizardStep.Recommend:
                if (!ReferenceEquals(Customizer.Template, SelectedRecommendation!.Template))
                {
                    Customizer.Template = SelectedRecommendation.Template;
                }

                if (SelectedPurpose is not null)
                {
                    Customizer.Preset(WizardScorer.VariablesFrom(_def, SelectedPurpose.Id, _answers));
                }

                CurrentStep = WizardStep.Customize;
                break;
            case WizardStep.Customize:
                CurrentStep = WizardStep.Confirm;
                break;
            case WizardStep.Confirm:
                if (Create())
                {
                    CurrentStep = WizardStep.Done;
                }

                break;
            case WizardStep.Done:
                DialogResult = true;
                break;
        }
    }

    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private void Back()
    {
        Error = null;
        CurrentStep = CurrentStep switch
        {
            WizardStep.Questions => WizardStep.Purpose,
            WizardStep.Recommend => Questions.Count > 0 && !SkippedQuestions ? WizardStep.Questions : WizardStep.Purpose,
            WizardStep.Customize => WizardStep.Recommend,
            WizardStep.Confirm => WizardStep.Customize,
            _ => CurrentStep,
        };
    }

    [RelayCommand]
    private void Cancel() => DialogResult = Result is not null;

    /// <summary>[템플릿 바로 고르기]: 용도·질문을 건너뛰고 모든 스타일 목록에서 고른다.</summary>
    [RelayCommand]
    private void PickTemplateDirectly()
    {
        SkippedQuestions = true;
        ShowAll = true;
        BuildRecommendations();
        CurrentStep = WizardStep.Recommend;
    }

    /// <summary>답 고르기 (테스트·키보드용).</summary>
    public void Answer(string questionId, string choiceId) =>
        Questions.FirstOrDefault(q => q.Question.Id == questionId)?.Select(choiceId);

    public void SelectPurpose(string purposeId)
    {
        var p = Purposes.FirstOrDefault(x => string.Equals(x.Value.Id, purposeId, StringComparison.OrdinalIgnoreCase));
        if (p is not null)
        {
            p.IsSelected = true;
        }
    }

    private void BuildQuestions()
    {
        Questions.Clear();
        if (SelectedPurpose is null)
        {
            return;
        }

        foreach (var q in _def.QuestionsFor(SelectedPurpose.Id))
        {
            _answers.TryGetValue(q.Id, out var a);
            Questions.Add(new WizardQuestionViewModel(q, a, OnAnswered));
        }

        NotifyNav();
    }

    private void OnAnswered(WizardQuestionViewModel q)
    {
        if (q.SelectedChoiceId is { } id)
        {
            _answers[q.Question.Id] = id;
        }

        NotifyNav();
    }

    private void BuildRecommendations()
    {
        var keepId = SelectedRecommendation?.Template.Id ?? Customizer.Template?.Id;
        Recommendations.Clear();
        var purposeId = SkippedQuestions ? "" : SelectedPurpose?.Id ?? "";
        var ranked = WizardScorer.Rank(_def, purposeId, _answers, _library.Templates);
        var top = SkippedQuestions ? [] : WizardScorer.Top(ranked);
        var shown = ShowAll ? ranked : top;
        foreach (var r in shown)
        {
            var isTop = top.Any(t => ReferenceEquals(t.Template, r.Template));
            Recommendations.Add(new RecommendationViewModel(r, isTop, ExampleTree(r.Template)));
        }

        SelectedRecommendation = Recommendations.FirstOrDefault(r => r.Template.Id == keepId) ?? Recommendations.FirstOrDefault();
    }

    private string ExampleTree(FolderTemplate t)
    {
        // 예시 트리: 변수 자리에 라벨을 넣어 그린다 (디스크는 보지 않음)
        var values = t.Variables.ToDictionary(v => v.Key, v => v.Default ?? (v.Label ?? v.Key), StringComparer.Ordinal);
        foreach (var k in TemplateVariables.ExtractKeys(t))
        {
            values.TryAdd(k, k);
        }

        var plan = TemplatePlanner.Plan(t, Path.GetTempPath(), new TemplatePlanOptions { Values = values, Now = _clock() }, NoDisk.Instance);
        return TemplateText.RenderTree(plan.Roots, 16);
    }

    private bool Create()
    {
        var plan = Customizer.Plan;
        var template = Customizer.Template;
        if (plan is null || template is null || !plan.CanApply)
        {
            return false;
        }

        try
        {
            Result = TemplateApplier.Apply(plan, template, _fs, new TemplateApplyOptions { WriteDescription = WriteDescription });
            if (!Result.Succeeded)
            {
                Error = "일부를 만들지 못했습니다: " + string.Join(", ", Result.Failed.Select(f => Path.GetFileName(f.Path)));
            }

            if (SaveAsMyTemplate && Customizer.BuildCustomizedCopy() is { } copy)
            {
                if (SelectedPurpose is not null && !copy.Purposes.Contains(SelectedPurpose.Id, StringComparer.OrdinalIgnoreCase))
                {
                    copy.Purposes.Add(SelectedPurpose.Id);
                }

                SavedTemplatePath = _library.SaveUserTemplate(copy);
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or InvalidDataException)
        {
            Error = ex.Message;
            return Result is not null;
        }
    }

    private void UpdateSteps()
    {
        foreach (var s in Steps)
        {
            s.IsCurrent = s.Step == CurrentStep;
            s.IsCompleted = s.Step < CurrentStep;
        }
    }

    private void NotifyNav()
    {
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanGoNext));
        OnPropertyChanged(nameof(NextLabel));
        NextCommand.NotifyCanExecuteChanged();
        BackCommand.NotifyCanExecuteChanged();
    }

    /// <summary>예시 트리용: 디스크를 보지 않는 파일 시스템 (모두 "없음").</summary>
    private sealed class NoDisk : ITemplateFileSystem
    {
        public static readonly NoDisk Instance = new();

        public bool DirectoryExists(string path) => false;

        public bool FileExists(string path) => false;

        public void CreateDirectory(string path) => throw new NotSupportedException();

        public bool TryCreateFile(string path, string content) => throw new NotSupportedException();

        public string? ReadAllText(string path) => null;

        public void DeleteFile(string path) => throw new NotSupportedException();

        public bool DeleteDirectoryIfEmpty(string path) => throw new NotSupportedException();

        public void SetHidden(string path) => throw new NotSupportedException();
    }
}
