using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Foldy.Core.Organize;
using Foldy.Core.Templates;
using Foldy.Presentation.Services;

namespace Foldy.Presentation.Templates;

public enum OrganizeState { Idle, Planning, Ready, Executing, Done, Undoing, Undone, Error }

/// <summary>정리 방식 선택지.</summary>
public sealed record OrganizeModeOption(OrganizeMode Mode, string Label, string Description);

/// <summary>미리보기 목록의 이동 한 줄 (체크 해제로 제외, 대상 폴더 바꾸기).</summary>
public sealed partial class MoveItemViewModel : ObservableObject
{
    private readonly OrganizeViewModel _owner;

    public MoveItemViewModel(PlannedMove move, string root, OrganizeViewModel owner)
    {
        Move = move;
        _owner = owner;
        Root = root;
        _isIncluded = move.Included;
        _targetFolder = move.TargetFolder;
    }

    public PlannedMove Move { get; }

    public string Root { get; }

    public string SourceName => Path.GetFileName(Move.Source);

    public string SourceRelative => Path.GetRelativePath(Root, Move.Source);

    public string DestinationRelative => Path.GetRelativePath(Root, Move.Destination);

    public string DestinationName => Move.Name;

    public bool IsRenamed => !string.Equals(SourceName, DestinationName, StringComparison.Ordinal);

    public string Reason => Move.Reason;

    public bool IsFolder => Move.IsFolder;

    [ObservableProperty]
    private bool _isIncluded;

    [ObservableProperty]
    private string _targetFolder;

    partial void OnIsIncludedChanged(bool value)
    {
        Move.Included = value;
        _owner.OnPlanEdited();
    }

    partial void OnTargetFolderChanged(string value)
    {
        if (!string.Equals(value, Move.TargetFolder, StringComparison.Ordinal))
        {
            _owner.Retarget(this, value);
        }
    }

    internal void RefreshFromMove()
    {
        OnPropertyChanged(nameof(DestinationRelative));
        OnPropertyChanged(nameof(DestinationName));
        OnPropertyChanged(nameof(IsRenamed));
        OnPropertyChanged(nameof(Reason));
    }
}

/// <summary>지금/정리 후 트리의 한 칸.</summary>
public sealed class OrganizeTreeNode(string name, bool isFolder)
{
    public string Name { get; } = name;

    public bool IsFolder { get; } = isFolder;

    public ObservableCollection<OrganizeTreeNode> Children { get; } = [];

    /// <summary>폴더 안 파일 수 (하위 포함).</summary>
    public int Count { get; set; }

    public string Display => IsFolder ? $"{Name}  ({Count})" : Name;

    public static ObservableCollection<OrganizeTreeNode> Build(IEnumerable<string> relativePaths, ISet<string>? folderPaths = null)
    {
        ArgumentNullException.ThrowIfNull(relativePaths);
        var roots = new ObservableCollection<OrganizeTreeNode>();
        foreach (var rel in relativePaths.Order(StringComparer.CurrentCultureIgnoreCase))
        {
            var parts = rel.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
            var level = roots;
            for (var i = 0; i < parts.Length; i++)
            {
                var isLeaf = i == parts.Length - 1;
                var isFolder = !isLeaf || (folderPaths?.Contains(rel) ?? false);
                var node = isLeaf ? null : level.FirstOrDefault(n => n.IsFolder && string.Equals(n.Name, parts[i], StringComparison.OrdinalIgnoreCase));
                if (node is null)
                {
                    node = new OrganizeTreeNode(parts[i], isFolder);
                    level.Add(node);
                }

                if (!isLeaf)
                {
                    node.Count++;
                }

                level = node.Children;
            }
        }

        return roots;
    }
}

/// <summary>직접 규칙 편집 한 줄 ("*.pdf 이고 이름에 영수증 → 재무/영수증").</summary>
public sealed partial class CustomRuleViewModel : ObservableObject
{
    [ObservableProperty]
    private string _pattern = "";

    [ObservableProperty]
    private string _keywords = "";

    [ObservableProperty]
    private string _target = "";

    [ObservableProperty]
    private bool _applyToFolders;

    public CustomRule ToRule() => new()
    {
        Name = string.IsNullOrWhiteSpace(Pattern) ? Target : $"{Pattern} → {Target}",
        Pattern = string.IsNullOrWhiteSpace(Pattern) ? null : Pattern.Trim(),
        Keywords = Keywords.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(),
        Target = Target.Trim(),
        ApplyToFolders = ApplyToFolders,
    };
}

/// <summary>
/// 폴더 정리 (설계서 5.11): 계획 → 미리보기(지금/정리 후 트리, 제외·대상 변경) → [정리하기] → [되돌리기].
/// 스캔·계획·실행은 백그라운드, 결과는 UI 스레드로 돌려보낸다. 아무것도 지우지 않는다.
/// </summary>
public sealed partial class OrganizeViewModel : ObservableObject
{
    private readonly IUiDispatcher _ui;
    private readonly IMoveOperations _ops;
    private readonly IMediaDateProvider? _media;
    private readonly IContentHasher? _hasher;
    private readonly string _journalFolder;
    private readonly Func<string, bool, CancellationToken, OrganizeScan> _scan;
    private CancellationTokenSource? _cts;

    public OrganizeViewModel(
        string rootPath, IUiDispatcher ui, IMoveOperations? ops = null, IEnumerable<FolderTemplate>? templates = null,
        string? journalFolder = null, IMediaDateProvider? media = null, IContentHasher? hasher = null,
        Func<string, bool, CancellationToken, OrganizeScan>? scanner = null)
    {
        ArgumentNullException.ThrowIfNull(ui);
        _rootPath = rootPath;
        _ui = ui;
        _ops = ops ?? LocalMoveOperations.Instance;
        _media = media;
        _hasher = hasher;
        _journalFolder = journalFolder ?? OrganizeExecutor.DefaultJournalFolder;
        _scan = scanner ?? OrganizeScanner.Scan;
        foreach (var t in templates ?? [])
        {
            if (t.Organize.Count > 0)
            {
                Templates.Add(t);
            }
        }

        _selectedTemplate = Templates.FirstOrDefault();
        _selectedMode = Modes[0];
    }

    public IReadOnlyList<OrganizeModeOption> Modes { get; } =
    [
        new(OrganizeMode.ByType, "종류별", "문서, 이미지, 영상, 음악, 압축, 설치파일, 코드, 기타"),
        new(OrganizeMode.ByDate, "날짜별", "수정 날짜 (사진·영상은 촬영 시각) → 2026\\2026-11"),
        new(OrganizeMode.ByTemplate, "스타일대로", "폴더 템플릿의 분류 규칙 (파일명 키워드, 종류, 날짜)"),
        new(OrganizeMode.Custom, "직접 규칙", "\"조건 → 폴더\" 목록"),
    ];

    public ObservableCollection<FolderTemplate> Templates { get; } = [];

    public ObservableCollection<CustomRuleViewModel> CustomRules { get; } = [];

    public ObservableCollection<MoveItemViewModel> Moves { get; } = [];

    public ObservableCollection<SkippedEntry> Skipped { get; } = [];

    [ObservableProperty]
    private ObservableCollection<OrganizeTreeNode> _beforeTree = [];

    [ObservableProperty]
    private ObservableCollection<OrganizeTreeNode> _afterTree = [];

    [ObservableProperty]
    private string _rootPath;

    [ObservableProperty]
    private OrganizeModeOption _selectedMode;

    [ObservableProperty]
    private FolderTemplate? _selectedTemplate;

    /// <summary>하위 폴더 안까지 정리 (기본 끔: 맨 위 단계 파일만).</summary>
    [ObservableProperty]
    private bool _includeSubfolders;

    [ObservableProperty]
    private bool _detectDuplicates = true;

    [ObservableProperty]
    private bool _prefixDate;

    [ObservableProperty]
    private string _dateFormat = "yyyy/yyyy-MM";

    [ObservableProperty]
    private OrganizeState _state;

    [ObservableProperty]
    private string _status = "";

    [ObservableProperty]
    private string _summary = "";

    [ObservableProperty]
    private double _progress;

    [ObservableProperty]
    private string? _lastJournalPath;

    [ObservableProperty]
    private bool? _dialogResult;

    public OrganizePlan? Plan { get; private set; }

    public OrganizeResult? LastResult { get; private set; }

    public UndoResult? LastUndo { get; private set; }

    public bool IsTemplateMode => SelectedMode.Mode == OrganizeMode.ByTemplate;

    public bool IsCustomMode => SelectedMode.Mode == OrganizeMode.Custom;

    public bool IsDateMode => SelectedMode.Mode == OrganizeMode.ByDate;

    public bool IsBusy => State is OrganizeState.Planning or OrganizeState.Executing or OrganizeState.Undoing;

    partial void OnSelectedModeChanged(OrganizeModeOption value)
    {
        OnPropertyChanged(nameof(IsTemplateMode));
        OnPropertyChanged(nameof(IsCustomMode));
        OnPropertyChanged(nameof(IsDateMode));
        Invalidate();
    }

    partial void OnSelectedTemplateChanged(FolderTemplate? value) => Invalidate();

    partial void OnIncludeSubfoldersChanged(bool value) => Invalidate();

    partial void OnDetectDuplicatesChanged(bool value) => Invalidate();

    partial void OnPrefixDateChanged(bool value) => Invalidate();

    partial void OnStateChanged(OrganizeState value)
    {
        OnPropertyChanged(nameof(IsBusy));
        PlanCommand.NotifyCanExecuteChanged();
        ExecuteCommand.NotifyCanExecuteChanged();
        UndoCommand.NotifyCanExecuteChanged();
    }

    public OrganizeOptions BuildOptions() => new()
    {
        Mode = SelectedMode.Mode,
        DateFormat = string.IsNullOrWhiteSpace(DateFormat) ? "yyyy/yyyy-MM" : DateFormat,
        Template = SelectedTemplate,
        Rules = CustomRules.Select(r => r.ToRule()).Where(r => r.Target.Length > 0).ToList(),
        DetectDuplicates = DetectDuplicates,
        PrefixDate = PrefixDate,
    };

    [RelayCommand]
    private void AddRule() => CustomRules.Add(new CustomRuleViewModel());

    [RelayCommand]
    private void RemoveRule(CustomRuleViewModel? rule)
    {
        if (rule is not null)
        {
            CustomRules.Remove(rule);
        }
    }

    [RelayCommand(CanExecute = nameof(CanPlan))]
    private async Task PlanAsync()
    {
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        var root = RootPath;
        var opts = BuildOptions();
        var deep = IncludeSubfolders;
        State = OrganizeState.Planning;
        Status = "폴더를 살펴보는 중…";
        try
        {
            var plan = await Task.Run(
                () =>
                {
                    var blocked = OrganizeScanner.CheckRoot(root);
                    if (blocked is not null)
                    {
                        return new OrganizePlan { Root = root, Blocked = blocked };
                    }

                    var scan = _scan(root, deep, ct);
                    return OrganizePlanner.Plan(scan, opts, p => File.Exists(p) || Directory.Exists(p), _media, _hasher);
                },
                ct);
            _ui.Post(() => ApplyPlan(plan));
        }
        catch (OperationCanceledException)
        {
            _ui.Post(() =>
            {
                State = OrganizeState.Idle;
                Status = "취소했습니다.";
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _ui.Post(() =>
            {
                State = OrganizeState.Error;
                Status = ex.Message;
            });
        }
    }

    private bool CanPlan() => !IsBusy && !string.IsNullOrWhiteSpace(RootPath);

    /// <summary>계획을 화면 목록으로 (테스트에서 직접 넣을 수도 있다).</summary>
    public void ApplyPlan(OrganizePlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        Plan = plan;
        Moves.Clear();
        Skipped.Clear();
        foreach (var m in plan.Moves)
        {
            Moves.Add(new MoveItemViewModel(m, plan.Root, this));
        }

        foreach (var s in plan.Skipped)
        {
            Skipped.Add(s);
        }

        if (plan.Blocked is not null)
        {
            State = OrganizeState.Error;
            Status = plan.Blocked;
            BeforeTree = [];
            AfterTree = [];
            Summary = "";
            return;
        }

        State = OrganizeState.Ready;
        Status = plan.Moves.Count == 0 ? "옮길 파일이 없습니다. 이미 정리돼 있어요." : "계획을 확인하고 [정리하기]를 누르세요. 아무것도 삭제하지 않습니다.";
        OnPlanEdited();
    }

    internal void OnPlanEdited()
    {
        if (Plan is null)
        {
            return;
        }

        var included = Plan.IncludedMoves.ToList();
        var folderSources = new HashSet<string>(included.Where(m => m.IsFolder).Select(m => Path.GetRelativePath(Plan.Root, m.Source)), StringComparer.OrdinalIgnoreCase);
        var folderDests = new HashSet<string>(included.Where(m => m.IsFolder).Select(m => Path.GetRelativePath(Plan.Root, m.Destination)), StringComparer.OrdinalIgnoreCase);
        BeforeTree = OrganizeTreeNode.Build(included.Select(m => Path.GetRelativePath(Plan.Root, m.Source)), folderSources);
        AfterTree = OrganizeTreeNode.Build(included.Select(m => Path.GetRelativePath(Plan.Root, m.Destination)), folderDests);
        var byFolder = Plan.CountsByFolder();
        Summary = $"옮김 {included.Count}개 · 폴더 {byFolder.Count}곳 · 건너뜀 {Plan.Skipped.Count}개"
            + (Plan.Moves.Count - included.Count > 0 ? $" · 제외 {Plan.Moves.Count - included.Count}개" : "");
        ExecuteCommand.NotifyCanExecuteChanged();
    }

    internal void Retarget(MoveItemViewModel item, string folder)
    {
        if (Plan is null)
        {
            return;
        }

        OrganizePlanner.Retarget(Plan, item.Move, folder, p => File.Exists(p) || Directory.Exists(p));
        item.RefreshFromMove();
        OnPlanEdited();
    }

    [RelayCommand(CanExecute = nameof(CanExecute))]
    private async Task ExecuteAsync()
    {
        var plan = Plan!;
        var journal = OrganizeExecutor.NewJournalPath(_journalFolder);
        State = OrganizeState.Executing;
        Status = "정리하는 중…";
        var progress = new Progress<OrganizeProgress>(p => _ui.Post(() => Progress = p.Total == 0 ? 1 : (double)p.Done / p.Total));
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        try
        {
            var result = await Task.Run(() => OrganizeExecutor.Execute(plan, journal, _ops, progress, ct), CancellationToken.None);
            _ui.Post(() =>
            {
                LastResult = result;
                LastJournalPath = result.JournalPath;
                State = OrganizeState.Done;
                Progress = 1;
                Status = $"{result.Moved}개를 옮겼습니다."
                    + (result.Failed.Count > 0 ? $" 사용 중이거나 옮길 수 없는 {result.Failed.Count}개는 건너뛰었습니다." : "")
                    + (result.Canceled ? " (중간에 취소됨)" : "")
                    + " [되돌리기]로 한 번에 원래대로 돌릴 수 있습니다.";
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _ui.Post(() =>
            {
                State = OrganizeState.Error;
                Status = ex.Message;
            });
        }
    }

    private bool CanExecute() => State == OrganizeState.Ready && Plan is { Blocked: null } && Plan.IncludedMoves.Any();

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private async Task UndoAsync()
    {
        var journal = LastJournalPath!;
        State = OrganizeState.Undoing;
        Status = "되돌리는 중…";
        try
        {
            var r = await Task.Run(() => OrganizeExecutor.Undo(journal, _ops));
            _ui.Post(() =>
            {
                LastUndo = r;
                State = OrganizeState.Undone;
                Status = r.AlreadyUndone
                    ? "이미 되돌린 정리입니다."
                    : $"{r.Restored}개를 원래 자리로 돌려놓았습니다."
                      + (r.LeftModified.Count > 0 ? $" 그사이 수정된 {r.LeftModified.Count}개는 현재 위치에 두었습니다." : "")
                      + (r.Missing.Count > 0 ? $" 찾을 수 없는 {r.Missing.Count}개가 있습니다." : "");
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _ui.Post(() =>
            {
                State = OrganizeState.Error;
                Status = ex.Message;
            });
        }
    }

    private bool CanUndo() => !IsBusy && LastJournalPath is not null && State != OrganizeState.Undone;

    /// <summary>이전에 한 정리(작업 기록의 저널)를 나중에 되돌릴 때.</summary>
    public void UseJournal(string journalPath)
    {
        LastJournalPath = journalPath;
        State = OrganizeState.Done;
    }

    [RelayCommand]
    private void Cancel()
    {
        if (IsBusy)
        {
            _cts?.Cancel();
            return;
        }

        DialogResult = LastResult is not null;
    }

    private void Invalidate()
    {
        if (State is OrganizeState.Ready)
        {
            State = OrganizeState.Idle;
            Status = "설정이 바뀌었습니다. [계획 만들기]를 다시 누르세요.";
        }
    }
}
