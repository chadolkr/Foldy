using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Foldy.Core.Templates;

namespace Foldy.Presentation.Templates;

/// <summary>템플릿 목록의 한 줄.</summary>
public sealed class TemplateItemViewModel(FolderTemplate template)
{
    public FolderTemplate Template { get; } = template;

    public string Name => Template.Name;

    public string? Description => Template.Description;

    public string Badge => Template.DisplayBadge;

    public string Category => Template.DisplayCategory;

    /// <summary>묶음의 첫 항목이면 묶음 이름 (목록에서 제목줄로 보인다), 아니면 null.</summary>
    public string? CategoryHeader { get; set; }

    public bool HasCategoryHeader => CategoryHeader is not null;

    public bool IsBuiltIn => Template.IsBuiltIn;

    public string Origin => Template.IsBuiltIn ? "내장" : "내 템플릿";

    public string Guide => Template.Guide ?? "";

    public string GuideSummary => TemplateText.GuideSummary(Template.Guide);

    public string NamingSummary => TemplateText.NamingSummary(Template);
}

/// <summary>변수 입력칸 하나 (고객사 이름 등).</summary>
public sealed partial class VariableInputViewModel : ObservableObject
{
    private readonly Action _changed;

    public VariableInputViewModel(TemplateVariable variable, string? initial, Action changed)
    {
        Variable = variable;
        _changed = changed;
        _value = initial ?? variable.Default ?? "";
    }

    public TemplateVariable Variable { get; }

    public string Key => Variable.Key;

    public string Label => string.IsNullOrWhiteSpace(Variable.Label) ? Variable.Key : Variable.Label!;

    public bool Required => Variable.Required;

    public bool HasChoices => Variable.Choices.Count > 0;

    public IList<TemplateChoice> Choices => Variable.Choices;

    [ObservableProperty]
    private string _value;

    [ObservableProperty]
    private string? _error;

    partial void OnValueChanged(string value)
    {
        Error = TemplateVariables.ValidateValue(value);
        _changed();
    }
}

/// <summary>미리보기 트리 한 칸. 체크 해제하면 그 아래도 만들지 않는다.</summary>
public sealed partial class PreviewNodeViewModel : ObservableObject
{
    private readonly Action<PreviewNodeViewModel, bool> _toggled;
    private bool _suppress;

    public PreviewNodeViewModel(PlannedNode node, Action<PreviewNodeViewModel, bool> toggled)
    {
        Node = node;
        _toggled = toggled;
        _isChecked = node.State != PlannedState.Excluded;
        foreach (var c in node.Children)
        {
            Children.Add(new PreviewNodeViewModel(c, toggled));
        }
    }

    public PlannedNode Node { get; }

    public string Key => Node.Key;

    public string Name => Node.Name;

    public bool IsFile => Node.IsFile;

    /// <summary>이름 옆 설명 ("— 지금 하는 일"). 없으면 빈 문자열.</summary>
    public string PurposeText => string.IsNullOrEmpty(Node.Purpose) ? "" : "— " + Node.Purpose;

    public PlannedState State => Node.State;

    /// <summary>오른쪽 작은 표시: 새로 만듦 / 이미 있음 / 만들 수 없음.</summary>
    public string StateText => Node.State switch
    {
        PlannedState.Exists => "이미 있음",
        PlannedState.Blocked => Node.Problem ?? "만들 수 없음",
        PlannedState.Excluded => "",
        _ => "",
    };

    public bool IsExisting => Node.State == PlannedState.Exists;

    public bool IsBlocked => Node.State == PlannedState.Blocked;

    public ObservableCollection<PreviewNodeViewModel> Children { get; } = [];

    [ObservableProperty]
    private bool _isChecked;

    [ObservableProperty]
    private bool _isExpanded = true;

    internal void SetCheckedSilently(bool value)
    {
        _suppress = true;
        IsChecked = value;
        _suppress = false;
    }

    partial void OnIsCheckedChanged(bool value)
    {
        if (!_suppress)
        {
            _toggled(this, value);
        }
    }
}

/// <summary>
/// 템플릿 맞추기 공용 부품 (템플릿 선택 창과 위저드 "내 상황에 맞추기"가 함께 쓴다):
/// 변수 입력, 체크 해제, 번호 접두어·영문 이름 → 미리보기 트리를 즉시 다시 계산.
/// </summary>
public sealed partial class TemplateCustomizer : ObservableObject
{
    private readonly ITemplateFileSystem _fs;
    private readonly Func<DateTime> _clock;
    private readonly HashSet<string> _excluded = new(StringComparer.Ordinal);
    private readonly HashSet<string> _includedOptional = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<string, string>> _valuesByTemplate = new(StringComparer.OrdinalIgnoreCase);
    private bool _loading;

    public TemplateCustomizer(ITemplateFileSystem fs, Func<DateTime>? clock = null)
    {
        _fs = fs;
        _clock = clock ?? (() => DateTime.Now);
    }

    [ObservableProperty]
    private FolderTemplate? _template;

    [ObservableProperty]
    private string _targetFolder = "";

    [ObservableProperty]
    private bool _stripNumberPrefix;

    [ObservableProperty]
    private bool _useEnglishNames;

    [ObservableProperty]
    private TemplatePlan? _plan;

    [ObservableProperty]
    private string _problemText = "";

    [ObservableProperty]
    private bool _canApply;

    [ObservableProperty]
    private string _summary = "";

    public ObservableCollection<VariableInputViewModel> Variables { get; } = [];

    public ObservableCollection<PreviewNodeViewModel> PreviewRoots { get; } = [];

    /// <summary>영문 이름이 있는 템플릿인지 (토글 표시 여부).</summary>
    public bool HasEnglishNames => Template is not null && HasNameEn(Template.Tree);

    public IReadOnlySet<string> Excluded => _excluded;

    public IReadOnlyDictionary<string, string> CurrentValues =>
        Variables.ToDictionary(v => v.Key, v => v.Value, StringComparer.Ordinal);

    /// <summary>위저드 답에서 온 값 (예: language). 입력칸 값보다 먼저 채운다.</summary>
    public void Preset(IReadOnlyDictionary<string, string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        foreach (var (k, v) in values)
        {
            var input = Variables.FirstOrDefault(x => x.Key == k);
            if (input is not null)
            {
                input.Value = v;
            }
            else if (Template is not null)
            {
                Remember(Template.Id)[k] = v;
            }
        }

        Refresh();
    }

    partial void OnTemplateChanged(FolderTemplate? oldValue, FolderTemplate? newValue)
    {
        if (oldValue is not null)
        {
            var mem = Remember(oldValue.Id);
            foreach (var v in Variables)
            {
                mem[v.Key] = v.Value;
            }
        }

        _loading = true;
        _excluded.Clear();
        _includedOptional.Clear();
        Variables.Clear();
        if (newValue is not null)
        {
            var mem = Remember(newValue.Id);
            var declared = newValue.Variables.ToList();

            // 선언하지 않았지만 트리에 쓰인 변수도 입력칸을 만든다
            foreach (var k in TemplateVariables.ExtractKeys(newValue))
            {
                if (!declared.Any(d => d.Key == k))
                {
                    declared.Add(new TemplateVariable { Key = k, Required = true });
                }
            }

            foreach (var v in declared)
            {
                Variables.Add(new VariableInputViewModel(v, mem.GetValueOrDefault(v.Key), Refresh));
            }
        }

        _loading = false;
        OnPropertyChanged(nameof(HasEnglishNames));
        Refresh();
    }

    partial void OnTargetFolderChanged(string value) => Refresh();

    partial void OnStripNumberPrefixChanged(bool value) => Refresh();

    partial void OnUseEnglishNamesChanged(bool value) => Refresh();

    public TemplatePlanOptions BuildOptions() => new()
    {
        Values = CurrentValues,
        Now = _clock(),
        Excluded = new HashSet<string>(_excluded, StringComparer.Ordinal),
        IncludedOptional = new HashSet<string>(_includedOptional, StringComparer.Ordinal),
        StripNumberPrefix = StripNumberPrefix,
        UseEnglishNames = UseEnglishNames,
    };

    /// <summary>미리보기 다시 계산.</summary>
    public void Refresh()
    {
        if (_loading)
        {
            return;
        }

        PreviewRoots.Clear();
        if (Template is null || string.IsNullOrWhiteSpace(TargetFolder))
        {
            Plan = null;
            CanApply = false;
            ProblemText = Template is null ? "템플릿을 고르세요." : "만들 위치를 정하세요.";
            Summary = "";
            return;
        }

        TemplatePlan plan;
        try
        {
            plan = TemplatePlanner.Plan(Template, TargetFolder, BuildOptions(), _fs);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException)
        {
            Plan = null;
            CanApply = false;
            ProblemText = "만들 위치가 올바르지 않습니다: " + ex.Message;
            return;
        }

        Plan = plan;
        foreach (var r in plan.Roots)
        {
            PreviewRoots.Add(new PreviewNodeViewModel(r, OnToggled));
        }

        var problems = new List<string>();
        if (plan.MissingVariables.Count > 0)
        {
            var labels = plan.MissingVariables.Select(k => Variables.FirstOrDefault(v => v.Key == k)?.Label ?? k);
            problems.Add("입력이 필요합니다: " + string.Join(", ", labels));
        }

        problems.AddRange(plan.Issues.Select(i => i.Message).Distinct(StringComparer.Ordinal).Take(3));
        ProblemText = string.Join("\n", problems);
        CanApply = plan.CanApply && plan.CreateCount > 0;
        Summary = plan.CreateCount == 0 && plan.CanApply
            ? "모두 이미 있습니다. 새로 만들 것이 없습니다."
            : $"새로 만듦 {plan.CreateCount}개" + (plan.ExistingCount > 0 ? $" · 이미 있어 그대로 둠 {plan.ExistingCount}개" : "");
    }

    private void OnToggled(PreviewNodeViewModel node, bool isChecked)
    {
        if (isChecked)
        {
            _excluded.Remove(node.Key);
            if (node.Node.Optional)
            {
                _includedOptional.Add(node.Key);
            }
        }
        else
        {
            _excluded.Add(node.Key);
            _includedOptional.Remove(node.Key);
        }

        Refresh();
    }

    /// <summary>테스트·단축 동작: 키로 체크 바꾸기.</summary>
    public void SetIncluded(string key, bool included)
    {
        var vm = Find(PreviewRoots, key);
        if (vm is not null)
        {
            vm.IsChecked = included;
        }
    }

    private static PreviewNodeViewModel? Find(IEnumerable<PreviewNodeViewModel> nodes, string key)
    {
        foreach (var n in nodes)
        {
            if (n.Key == key)
            {
                return n;
            }

            if (Find(n.Children, key) is { } c)
            {
                return c;
            }
        }

        return null;
    }

    private Dictionary<string, string> Remember(string id)
    {
        if (!_valuesByTemplate.TryGetValue(id, out var d))
        {
            d = new Dictionary<string, string>(StringComparer.Ordinal);
            _valuesByTemplate[id] = d;
        }

        return d;
    }

    private static bool HasNameEn(IEnumerable<TemplateNode> nodes) =>
        nodes.Any(n => !string.IsNullOrWhiteSpace(n.NameEn) || HasNameEn(n.Children));

    /// <summary>지금 맞춘 상태 그대로 "내 템플릿"으로 저장할 복사본.</summary>
    public FolderTemplate? BuildCustomizedCopy()
    {
        if (Template is null)
        {
            return null;
        }

        var c = TemplateText.Clone(Template);
        TemplateText.Prune(c, _excluded);
        c.Id = Template.Id + "-my";
        c.Name = Template.Name + " (내 템플릿)";
        c.IsBuiltIn = false;
        c.SourcePath = null;
        foreach (var v in c.Variables)
        {
            var input = Variables.FirstOrDefault(x => x.Key == v.Key);
            if (input is not null && v.Choices.Count > 0)
            {
                v.Default = input.Value; // 선택형만 기본값으로 기억 (고객명 등은 매번 입력)
            }
        }

        return c;
    }
}
