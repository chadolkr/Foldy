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

    /// <summary>긴 경로는 가운데를 줄여 맨 위 폴더와 끝 폴더·파일 이름이 보이게 ("00.CodeReview\…\BIZ_MC\a.pas").</summary>
    public string SourceShort => OrganizeViewModel.ShortPath(SourceRelative);

    public string DestinationRelative => Path.GetRelativePath(Root, Move.Destination);

    public string DestinationName => Move.Name;

    public bool IsRenamed => !string.Equals(SourceName, DestinationName, StringComparison.Ordinal);

    public string Reason => Move.Reason;

    public bool IsFolder => Move.IsFolder;

    /// <summary>지금 위치 전체 경로 (목록 칸에 다 안 보일 때 풍선 도움말로).</summary>
    public string SourceFull => Move.Source;

    /// <summary>같은 파일 묶음에 든 파일 (누르면 아래에 같은 파일들이 어디 있는지 보인다).</summary>
    public bool IsDuplicate => Move.DuplicateSet is not null;

    /// <summary>계획 쪽에서 바뀐 값을 화면에 옮기는 중 (그 사이 바뀐 알림으로 계획을 다시 고치지 않게).</summary>
    private bool _syncing;

    [ObservableProperty]
    private bool _isIncluded;

    [ObservableProperty]
    private string _targetFolder;

    partial void OnIsIncludedChanged(bool value)
    {
        if (_syncing)
        {
            return;
        }

        Move.Included = value;
        _owner.OnPlanEdited();
    }

    partial void OnTargetFolderChanged(string value)
    {
        if (!_syncing && !string.Equals(value, Move.TargetFolder, StringComparison.Ordinal))
        {
            _owner.Retarget(this, value);
        }
    }

    /// <summary>[모두 체크]·[모두 해제]: 한 줄씩 계획을 다시 그리지 않고 값만 바꾼다 (끝나고 한 번 그린다).</summary>
    internal void SetIncludedQuietly(bool value)
    {
        Move.Included = value;
        _syncing = true;
        try
        {
            IsIncluded = value;
        }
        finally
        {
            _syncing = false;
        }
    }

    /// <summary>이유별 묶음 이름 (옮길 폴더의 맨 위 단계, 예: _중복 후보, 코드, 2025).</summary>
    public string Category => OrganizeViewModel.CategoryOf(Move.TargetFolder);

    internal void RefreshFromMove()
    {
        _syncing = true;
        try
        {
            IsIncluded = Move.Included;
            TargetFolder = Move.TargetFolder;
        }
        finally
        {
            _syncing = false;
        }

        OnPropertyChanged(nameof(DestinationRelative));
        OnPropertyChanged(nameof(DestinationName));
        OnPropertyChanged(nameof(IsRenamed));
        OnPropertyChanged(nameof(Reason));
    }
}

/// <summary>건너뛸 파일 한 줄: 전체 경로(풍선 도움말), 줄인 경로, 이유.</summary>
public sealed record SkippedRow(string FullPath, string Short, string Reason);

/// <summary>이유별 묶음 하나의 수 (계획을 그릴 때 뒤에서 센다).</summary>
public sealed record CategoryStat(string Key, int Count, int Checked, long Bytes);

/// <summary>
/// 이유별 묶음 막대 한 줄 ("_중복 후보 252,411개 · 체크 252,411"): 많은 것부터, 누르면 그 묶음만 목록에 보인다.
/// <see cref="Key"/>가 null이면 "모두".
/// </summary>
public sealed partial class OrganizeCategoryViewModel(string? key, string name) : ObservableObject
{
    /// <summary>막대가 가장 길 때 넓이.</summary>
    public const double MaxBar = 150;

    public string? Key { get; } = key;

    public string Name { get; } = name;

    public bool IsAll => Key is null;

    [ObservableProperty]
    private int _count;

    [ObservableProperty]
    private int _checked;

    [ObservableProperty]
    private string _detail = "";

    /// <summary>가장 많은 묶음에 견준 막대 넓이.</summary>
    [ObservableProperty]
    private double _barWidth;

    internal void Set(int count, int @checked, long bytes, int max)
    {
        Count = count;
        Checked = @checked;
        Detail = Foldy.Core.Localization.Loc.F("org.cat.detail", count, @checked, Foldy.Presentation.Files.FileItem.FormatBytes(bytes));
        BarWidth = IsAll || max <= 0 ? 0 : Math.Max(2, MaxBar * count / max);
    }
}

/// <summary>같은 파일 묶음 목록의 한 줄 (이름·개수·크기·남길 파일 위치).</summary>
public sealed class DuplicateSetViewModel(DuplicateSet set, string root) : ObservableObject
{
    public DuplicateSet Set { get; } = set;

    public string Name => Set.Name;

    public int Count => Set.Members.Count;

    public string SizeText => Foldy.Presentation.Files.FileItem.FormatBytes(Set.Size);

    /// <summary>남길 파일의 위치 (정리할 폴더 기준).</summary>
    public string KeeperRelative => Path.GetRelativePath(root, Set.Keeper.FullPath);

    internal void Refresh()
    {
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(KeeperRelative));
    }
}

/// <summary>같은 파일 묶음 안의 파일 하나: 전체 경로와 [남기기] 단추, 정리하면 어떻게 되는지.</summary>
public sealed partial class DuplicateMemberViewModel : ObservableObject
{
    private readonly OrganizeViewModel _owner;
    private bool _syncing;

    public DuplicateMemberViewModel(DuplicateSet set, DuplicateMember member, OrganizeViewModel owner)
    {
        Set = set;
        Member = member;
        _owner = owner;
        _isKeeper = set.Keeper == member;
    }

    public DuplicateSet Set { get; }

    public DuplicateMember Member { get; }

    /// <summary>전체 경로 (드라이브부터).</summary>
    public string FullPath => Member.FullPath;

    public string Modified => Foldy.Core.Localization.Loc.F("org.dup.modified", Member.LastWriteUtc.ToLocalTime());

    /// <summary>이 파일을 남긴다 (묶음에서 하나만).</summary>
    [ObservableProperty]
    private bool _isKeeper;

    /// <summary>"남겨요 → 코드", "'_중복 후보' 폴더로 옮겨요" 같은 결과 한 줄.</summary>
    [ObservableProperty]
    private string _outcome = "";

    partial void OnIsKeeperChanged(bool value)
    {
        if (!_syncing && value)
        {
            _owner.ChooseKeeper(this);
        }
    }

    internal void Sync(string outcome)
    {
        _syncing = true;
        try
        {
            IsKeeper = Set.Keeper == Member;
        }
        finally
        {
            _syncing = false;
        }

        Outcome = outcome;
    }
}

/// <summary>지금/정리 후 트리의 한 칸.</summary>
public sealed class OrganizeTreeNode(string name, bool isFolder)
{
    /// <summary>폴더 하나에 보여 줄 파일 수. 나머지는 "… 외 N개" 한 줄로 (트리가 수십만 줄이 되지 않게).</summary>
    public const int MaxFilesPerFolder = 200;

    public string Name { get; } = name;

    public bool IsFolder { get; } = isFolder;

    public ObservableCollection<OrganizeTreeNode> Children { get; } = [];

    /// <summary>폴더 안 파일 수 (하위 포함).</summary>
    public int Count { get; set; }

    /// <summary>"… 외 N개" 줄.</summary>
    public bool IsMore { get; private init; }

    public string Display => IsFolder ? $"{Name}  ({Count:N0})" : Name;

    public static ObservableCollection<OrganizeTreeNode> Build(IEnumerable<string> relativePaths, ISet<string>? folderPaths = null)
    {
        ArgumentNullException.ThrowIfNull(relativePaths);
        var root = new Draft(new OrganizeTreeNode("", true));
        foreach (var rel in relativePaths)
        {
            var parts = rel.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
            var level = root;
            for (var i = 0; i < parts.Length; i++)
            {
                var isLeaf = i == parts.Length - 1;
                if (isLeaf && !(folderPaths?.Contains(rel) ?? false))
                {
                    level.AddFile(parts[i]);
                    break;
                }

                level = level.Folder(parts[i]);
                if (!isLeaf)
                {
                    level.Node.Count++;
                }
            }
        }

        root.Finish();
        return root.Node.Children;
    }

    /// <summary>만드는 동안만 쓰는 이름표 (폴더 이름 → 칸, 보여 줄 파일, 숨긴 파일 수).</summary>
    private sealed class Draft(OrganizeTreeNode node)
    {
        private readonly Dictionary<string, Draft> _folders = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> _files = [];
        private int _hidden;

        public OrganizeTreeNode Node { get; } = node;

        public Draft Folder(string name)
        {
            if (!_folders.TryGetValue(name, out var d))
            {
                d = new Draft(new OrganizeTreeNode(name, true));
                _folders[name] = d;
            }

            return d;
        }

        public void AddFile(string name)
        {
            if (_files.Count < MaxFilesPerFolder)
            {
                _files.Add(name);
            }
            else
            {
                _hidden++;
            }
        }

        public void Finish()
        {
            var items = _folders.Values.Select(f =>
            {
                f.Finish();
                return f.Node;
            }).Concat(_files.Select(f => new OrganizeTreeNode(f, false)));
            foreach (var n in items.OrderBy(n => n.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                Node.Children.Add(n);
            }

            if (_hidden > 0)
            {
                Node.Children.Add(new OrganizeTreeNode(Foldy.Core.Localization.Loc.F("org.moreFiles", _hidden), false) { IsMore = true });
            }
        }
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
    private readonly Func<string, bool, CancellationToken, OrganizeScan>? _scan;
    private CancellationTokenSource? _cts;
    private int _viewGeneration;
    private bool _offerDismissed;

    /// <summary>파일+폴더가 이만큼 넘거나 <see cref="LargeAfter"/>보다 오래 걸리면 [뒤에서 하기]를 권한다.</summary>
    public static long LargeItemCount { get; set; } = 100_000;

    public static TimeSpan LargeAfter { get; set; } = TimeSpan.FromSeconds(8);

    /// <summary>훑는 동안 화면 숫자를 고치는 간격.</summary>
    public static TimeSpan TickInterval { get; set; } = TimeSpan.FromMilliseconds(250);

    /// <summary>이보다 큰 계획은 체크를 바꿀 때 트리를 뒤에서 다시 그린다 (화면이 멈추지 않게).</summary>
    public static int SyncEditLimit { get; set; } = 20_000;

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
        _scan = scanner;
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
        new(OrganizeMode.ByType, Foldy.Core.Localization.Loc.T("org.byType"), Foldy.Core.Localization.Loc.T("org.byTypeTip")),
        new(OrganizeMode.ByDate, Foldy.Core.Localization.Loc.T("org.byDate"), Foldy.Core.Localization.Loc.T("org.byDateTip")),
        new(OrganizeMode.ByTemplate, Foldy.Core.Localization.Loc.T("org.byStyle"), Foldy.Core.Localization.Loc.T("org.byStyleTip")),
        new(OrganizeMode.Custom, Foldy.Core.Localization.Loc.T("org.custom"), Foldy.Core.Localization.Loc.T("org.customTip")),
    ];

    public ObservableCollection<FolderTemplate> Templates { get; } = [];

    public ObservableCollection<CustomRuleViewModel> CustomRules { get; } = [];

    /// <summary>옮길 목록. 계획이 바뀌면 통째로 바꿔 끼운다 (수십만 줄을 한 줄씩 넣으면 화면이 멈춘다).</summary>
    [ObservableProperty]
    private ObservableCollection<MoveItemViewModel> _moves = [];

    [ObservableProperty]
    private ObservableCollection<SkippedRow> _skipped = [];

    /// <summary>같은 파일 묶음 목록 (탭 "같은 파일").</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDuplicateSets))]
    [NotifyPropertyChangedFor(nameof(DuplicatesTabHeader))]
    private ObservableCollection<DuplicateSetViewModel> _duplicateSets = [];

    public bool HasDuplicateSets => DuplicateSets.Count > 0;

    public string DuplicatesTabHeader => Foldy.Core.Localization.Loc.F("org.dupsTab", DuplicateSets.Count);

    /// <summary>옮길 파일 목록에서 고른 줄. 같은 파일 묶음이면 아래에 묶음을 보여 준다.</summary>
    [ObservableProperty]
    private MoveItemViewModel? _selectedMove;

    /// <summary>같은 파일 탭에서 고른 묶음.</summary>
    [ObservableProperty]
    private DuplicateSetViewModel? _selectedDuplicateSet;

    /// <summary>지금 보여 주는 같은 파일 묶음의 파일들 (전체 경로와 [남기기]).</summary>
    [ObservableProperty]
    private ObservableCollection<DuplicateMemberViewModel> _duplicateMembers = [];

    [ObservableProperty]
    private bool _hasDuplicateSelection;

    [ObservableProperty]
    private string _duplicateTitle = "";

    [ObservableProperty]
    private string _duplicateTip = "";

    private DuplicateSet? _shownSet;
    private Dictionary<string, MoveItemViewModel> _bySource = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<DuplicateSet, DuplicateSetViewModel> _setViews = new();

    /// <summary>이유별 묶음 (맨 앞은 "모두", 나머지는 많은 것부터). 누르면 그 묶음만 보인다.</summary>
    [ObservableProperty]
    private ObservableCollection<OrganizeCategoryViewModel> _categories = [];

    [ObservableProperty]
    private OrganizeCategoryViewModel? _selectedCategory;

    /// <summary>목록에 보이는 줄 (고른 묶음만, "모두"면 <see cref="Moves"/> 그대로).</summary>
    [ObservableProperty]
    private ObservableCollection<MoveItemViewModel> _visibleMoves = [];

    /// <summary>위의 "지금" 트리를 보인다 ([숨기기]로 접고 [지금 다시 보기]로 연다. 둘 다 숨기지는 않는다).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowBothTrees))]
    private bool _showBeforeTree = true;

    /// <summary>위의 "정리 후" 트리를 보인다.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowBothTrees))]
    private bool _showAfterTree = true;

    public bool ShowBothTrees => ShowBeforeTree && ShowAfterTree;

    [RelayCommand]
    private void HideBeforeTree()
    {
        ShowAfterTree = true;
        ShowBeforeTree = false;
    }

    [RelayCommand]
    private void HideAfterTree()
    {
        ShowBeforeTree = true;
        ShowAfterTree = false;
    }

    [RelayCommand]
    private void ShowTreesAgain()
    {
        ShowBeforeTree = true;
        ShowAfterTree = true;
    }

    /// <summary>"252,411개 보는 중".</summary>
    [ObservableProperty]
    private string _visibleInfo = "";

    /// <summary>
    /// 긴 경로를 가운데만 줄인다: 맨 앞 폴더 + "…" + 끝의 두 단계(폴더와 파일 이름). 짧으면 그대로.
    /// 사용자 2026-11-23: 경로가 길어 보기 힘들다 → 어디서 왔는지(앞)와 무엇인지(끝)가 늘 보이게. 전체는 풍선 도움말로.
    /// </summary>
    public static string ShortPath(string path, int max = 60)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (path.Length <= max)
        {
            return path;
        }

        var parts = path.Split('\\');
        if (parts.Length <= 3)
        {
            return path;
        }

        var tail = parts[^2] + "\\" + parts[^1];
        return parts[0] + "\\…\\" + tail;
    }

    /// <summary>이유별 묶음 이름: 옮길 폴더의 맨 위 단계 (정리할 폴더 바로 아래면 빈 글).</summary>
    public static string CategoryOf(string targetFolder)
    {
        ArgumentNullException.ThrowIfNull(targetFolder);
        var i = targetFolder.IndexOfAny(['\\', '/']);
        return i < 0 ? targetFolder : targetFolder[..i];
    }

    [ObservableProperty]
    private ObservableCollection<OrganizeTreeNode> _beforeTree = [];

    [ObservableProperty]
    private ObservableCollection<OrganizeTreeNode> _afterTree = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RootIsProject))]
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

    /// <summary>정리할 폴더 전체 크기 (색인에서 바로 아는 경우). 모르면 null.</summary>
    [ObservableProperty]
    private long? _totalBytes;

    /// <summary>지금까지 센 파일·폴더·용량 ("파일 312,450개 · 폴더 41,200개 · 38 GB / 전체 93 GB").</summary>
    [ObservableProperty]
    private string _scanInfo = "";

    /// <summary>큰 폴더라 오래 걸릴 때 [뒤에서 하기]·[여기서 기다리기] 안내를 보인다.</summary>
    [ObservableProperty]
    private bool _showBackgroundOffer;

    /// <summary>창을 닫고 뒤에서 계획을 만드는 중 (끝나면 알린다).</summary>
    [ObservableProperty]
    private bool _isInBackground;

    /// <summary>색인에서 폴더 전체 크기를 알려 주는 곳 (앱이 채운다). 없거나 모르면 null.</summary>
    public Func<string, CancellationToken, Task<long?>>? SizeHint { get; init; }

    /// <summary>계획 만들기가 끝났다 (준비됨·취소·오류 모두, UI 스레드). 뒤에서 만들 때 알림에 쓴다.</summary>
    public event EventHandler? PlanFinished;

    /// <summary>[뒤에서 하기]를 눌렀다 (창은 닫히고 계획은 계속 만든다).</summary>
    public event EventHandler? BackgroundRequested;

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

    /// <summary>아래 목록 탭 (0 = 옮길 파일, 1 = 건너뛸 파일). 옮길 것이 없으면 건너뛴 이유를 바로 보여 준다.</summary>
    [ObservableProperty]
    private int _selectedListTab;

    /// <summary>프로젝트 폴더(.git 등)라도 맨 위 파일만 정리한다 (하위 폴더는 그대로). 기본 꺼짐.</summary>
    [ObservableProperty]
    private bool _organizeProjectAnyway;

    partial void OnOrganizeProjectAnywayChanged(bool value) => Invalidate();

    /// <summary>지금 폴더가 프로젝트 폴더인가 (옵션 표시용).</summary>
    public bool RootIsProject => !string.IsNullOrWhiteSpace(RootPath) && Directory.Exists(RootPath) && OrganizeScanner.IsProjectFolder(RootPath);

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
        var allowProject = OrganizeProjectAnyway;
        var deep = IncludeSubfolders && !(allowProject && RootIsProject); // 프로젝트 폴더는 맨 위 파일만
        var counter = new OrganizeScanCounter();
        var stages = new OrganizePlanProgress();
        State = OrganizeState.Planning;
        Status = Foldy.Core.Localization.Loc.T("org.scanning");
        ScanInfo = "";
        Progress = 0;
        ShowBackgroundOffer = false;
        _offerDismissed = false;
        TotalBytes = null;
        var started = System.Diagnostics.Stopwatch.StartNew();
        using var ticking = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var ticker = TickAsync(counter, stages, deep, started, ticking.Token);
        _ = LoadSizeHintAsync(root, deep, ct);
        try
        {
            var prepared = await Task.Run(
                () =>
                {
                    var blocked = OrganizeScanner.CheckRoot(root, allowProject);
                    if (blocked is not null)
                    {
                        return Prepare(new OrganizePlan { Root = root, Blocked = blocked });
                    }

                    var scan = _scan is { } custom ? custom(root, deep, ct) : OrganizeScanner.Scan(root, deep, counter, ct: ct);
                    var cache = new PathExistsCache();
                    var plan = OrganizePlanner.Plan(scan, opts, cache.Exists, _media, _hasher, stages, ct);
                    return Prepare(plan);
                },
                ct);
            await StopTicker(ticking, ticker);
            var (files, folders, bytes) = (counter.Files, counter.Folders, counter.Bytes);
            _ui.Post(() =>
            {
                ScanInfo = files + folders > 0 ? DescribeCounts(files, folders, bytes, TotalBytes) : "";
                Show(prepared);
                ShowBackgroundOffer = false;
                PlanFinished?.Invoke(this, EventArgs.Empty);
            });
        }
        catch (OperationCanceledException)
        {
            await StopTicker(ticking, ticker);
            _ui.Post(() =>
            {
                State = OrganizeState.Idle;
                Status = Foldy.Core.Localization.Loc.T("org.cancelled");
                ShowBackgroundOffer = false;
                PlanFinished?.Invoke(this, EventArgs.Empty);
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or OutOfMemoryException)
        {
            await StopTicker(ticking, ticker);
            _ui.Post(() =>
            {
                State = OrganizeState.Error;
                Status = ex is OutOfMemoryException ? Foldy.Core.Localization.Loc.T("org.tooBig") : ex.Message;
                ShowBackgroundOffer = false;
                PlanFinished?.Invoke(this, EventArgs.Empty);
            });
        }
    }

    private static async Task StopTicker(CancellationTokenSource ticking, Task ticker)
    {
        await ticking.CancelAsync();
        try
        {
            await ticker;
        }
        catch (OperationCanceledException)
        {
            // 멈춘 것
        }
    }

    /// <summary>색인이 폴더 전체 크기를 알면 바로 보여 준다 (훑는 진행률의 기준도 된다).</summary>
    private async Task LoadSizeHintAsync(string root, bool deep, CancellationToken ct)
    {
        if (SizeHint is not { } hint || !deep)
        {
            return;
        }

        try
        {
            var total = await hint(root, ct);
            if (total is > 0)
            {
                _ui.Post(() =>
                {
                    if (State == OrganizeState.Planning)
                    {
                        TotalBytes = total;
                    }
                });
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or InvalidOperationException or TimeoutException)
        {
            // 색인이 없으면 크기 없이 센 숫자만 보여 준다
        }
    }

    /// <summary>훑는 동안 가끔(0.25초마다) 센 숫자·단계·진행률을 화면에 옮긴다. 크고 오래 걸리면 [뒤에서 하기]를 권한다.</summary>
    private async Task TickAsync(OrganizeScanCounter counter, OrganizePlanProgress stages, bool deep, System.Diagnostics.Stopwatch started, CancellationToken ct)
    {
        while (true)
        {
            await Task.Delay(TickInterval, ct);
            var files = counter.Files;
            var folders = counter.Folders;
            var bytes = counter.Bytes;
            var stage = stages.Stage;
            var done = stages.Done;
            var total = stages.Total;
            var elapsed = started.Elapsed;
            _ui.Post(() =>
            {
                if (State != OrganizeState.Planning)
                {
                    return;
                }

                ScanInfo = DescribeCounts(files, folders, bytes, TotalBytes);
                switch (stage)
                {
                    case OrganizePlanStage.Duplicates when total > 0:
                        Status = Foldy.Core.Localization.Loc.F("org.stage.dups", done, total);
                        Progress = Math.Max(Progress, Math.Clamp((double)done / total, 0, 1));
                        break;
                    case OrganizePlanStage.Planning when total > 0:
                        Status = Foldy.Core.Localization.Loc.F("org.stage.plan", done, total);
                        Progress = Math.Max(Progress, Math.Clamp((double)done / total, 0, 1));
                        break;
                    default:
                        Status = Foldy.Core.Localization.Loc.T("org.scanning");
                        Progress = TotalBytes is long t && t > 0 ? Math.Max(Progress, Math.Clamp((double)bytes / t, 0, 0.99)) : 0;
                        break;
                }

                if (deep && !IsInBackground && !_offerDismissed && !ShowBackgroundOffer
                    && (files + folders >= LargeItemCount || elapsed >= LargeAfter))
                {
                    ShowBackgroundOffer = true;
                }
            });
        }
    }

    /// <summary>"파일 312,450개 · 폴더 41,200개 · 38 GB" (+ " / 전체 93 GB").</summary>
    public static string DescribeCounts(long files, long folders, long bytes, long? totalBytes)
    {
        var text = Foldy.Core.Localization.Loc.F("org.counts", files, folders, Foldy.Presentation.Files.FileItem.FormatBytes(bytes));
        return totalBytes is long t && t > 0 ? text + Foldy.Core.Localization.Loc.F("org.countsTotal", Foldy.Presentation.Files.FileItem.FormatBytes(t)) : text;
    }

    /// <summary>[뒤에서 하기]: 창을 닫고 계속 만든다. 끝나면 앱이 알려 준다 (작업 창의 [계획 보기]와 Windows 알림).</summary>
    [RelayCommand]
    private void RunInBackground()
    {
        if (State != OrganizeState.Planning)
        {
            return;
        }

        IsInBackground = true;
        ShowBackgroundOffer = false;
        BackgroundRequested?.Invoke(this, EventArgs.Empty);
        DialogResult = false;
    }

    /// <summary>[여기서 기다리기]: 안내만 닫는다.</summary>
    [RelayCommand]
    private void KeepWaiting()
    {
        _offerDismissed = true;
        ShowBackgroundOffer = false;
    }

    /// <summary>뒤에서 만든 계획을 다시 창에 띄우기 전에 부른다 (창이 바로 닫히지 않게).</summary>
    public void PrepareToShowAgain()
    {
        IsInBackground = false;
        DialogResult = null;
    }

    private bool CanPlan() => !IsBusy && !string.IsNullOrWhiteSpace(RootPath);

    /// <summary>계획을 화면에 보일 모양으로 (무거운 일이라 백그라운드에서 한다).</summary>
    private sealed record PreparedPlan(
        OrganizePlan Plan, ObservableCollection<MoveItemViewModel> Moves, ObservableCollection<SkippedRow> Skipped, PlanView? View, PlanView? Unchanged,
        Dictionary<string, MoveItemViewModel> BySource, ObservableCollection<DuplicateSetViewModel> DuplicateSets);

    /// <summary>트리 두 개와 요약 한 줄.</summary>
    private sealed record PlanView(ObservableCollection<OrganizeTreeNode> Before, ObservableCollection<OrganizeTreeNode> After, string Summary, IReadOnlyList<CategoryStat> Categories, int Total, int Included);

    private PreparedPlan Prepare(OrganizePlan plan)
    {
        var moves = new ObservableCollection<MoveItemViewModel>(plan.Moves.Select(m => new MoveItemViewModel(m, plan.Root, this)));
        var skipped = new ObservableCollection<SkippedRow>(plan.Skipped.Select(x => new SkippedRow(x.FullPath, ShortPath(Path.GetRelativePath(plan.Root, x.FullPath)), x.Reason)));
        var bySource = new Dictionary<string, MoveItemViewModel>(moves.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var m in moves)
        {
            bySource[m.Move.Source] = m;
        }

        var sets = new ObservableCollection<DuplicateSetViewModel>(plan.DuplicateSets.Select(d => new DuplicateSetViewModel(d, plan.Root)));
        if (plan.Blocked is not null)
        {
            return new(plan, moves, skipped, null, null, bySource, sets);
        }

        PlanView? unchanged = null;
        if (plan.Moves.Count == 0 && plan.Skipped.Count > 0)
        {
            // 옮길 것이 없으면 지금 구조(건너뛴 항목)를 양쪽에 그대로 보여 준다 — "지금 = 정리 후"
            var rel = plan.Skipped.Select(x => Path.GetRelativePath(plan.Root, x.FullPath)).ToList();
            var folders = new HashSet<string>(plan.Skipped.Where(x => Directory.Exists(x.FullPath)).Select(x => Path.GetRelativePath(plan.Root, x.FullPath)), StringComparer.OrdinalIgnoreCase);
            unchanged = new(OrganizeTreeNode.Build(rel, folders), OrganizeTreeNode.Build(rel, folders), "", [], 0, 0);
        }

        return new(plan, moves, skipped, ComputeView(plan), unchanged, bySource, sets);
    }

    private void Show(PreparedPlan p)
    {
        var plan = p.Plan;
        Plan = plan;
        _viewGeneration++;
        Moves = p.Moves;
        Skipped = p.Skipped;
        _bySource = p.BySource;
        Categories = [];
        _suppressFilter = true;
        SelectedCategory = null;
        _suppressFilter = false;
        ApplyFilter();
        DuplicateSets = p.DuplicateSets;
        _setViews = p.DuplicateSets.ToDictionary(v => v.Set);
        SelectedMove = null;
        SelectedDuplicateSet = null;
        ShowDuplicateSet(null);
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
        Progress = 1;
        Status = plan.Moves.Count == 0
            ? (plan.Skipped.Count == 0 || plan.Skipped.All(x => Directory.Exists(x.FullPath))
                ? Foldy.Core.Localization.Loc.T("org.noFiles")
                : Foldy.Core.Localization.Loc.F("org.noMoves", plan.Skipped.Count))
            : Foldy.Core.Localization.Loc.T("org.checkPlan");
        SelectedListTab = plan.Moves.Count == 0 && plan.Skipped.Count > 0 ? 1 : 0;
        ApplyView(p.View!);
        if (p.Unchanged is { } u)
        {
            BeforeTree = u.Before;
            AfterTree = u.After;
        }
    }

    /// <summary>계획을 화면 목록으로 (테스트에서 직접 넣을 수도 있다).</summary>
    public void ApplyPlan(OrganizePlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        Show(Prepare(plan));
    }

    private static PlanView ComputeView(OrganizePlan plan) => ComputeView(plan, plan.Moves.ToArray());

    /// <param name="moves">계획의 이동을 UI 스레드에서 떠 둔 것 (뒤에서 세는 동안 남길 파일을 바꿔 목록이 바뀌어도 안전하게).</param>
    private static PlanView ComputeView(OrganizePlan plan, PlannedMove[] moves)
    {
        var included = moves.Where(m => m.Included).ToList();
        var folderSources = new HashSet<string>(included.Where(m => m.IsFolder).Select(m => Path.GetRelativePath(plan.Root, m.Source)), StringComparer.OrdinalIgnoreCase);
        var folderDests = new HashSet<string>(included.Where(m => m.IsFolder).Select(m => Path.GetRelativePath(plan.Root, m.Destination)), StringComparer.OrdinalIgnoreCase);
        var before = OrganizeTreeNode.Build(included.Select(m => Path.GetRelativePath(plan.Root, m.Source)), folderSources);
        var after = OrganizeTreeNode.Build(included.Select(m => Path.GetRelativePath(plan.Root, m.Destination)), folderDests);
        var folders = included.Select(m => m.TargetFolder).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        var summary = Foldy.Core.Localization.Loc.F("org.summary", included.Count, folders, plan.Skipped.Count)
            + (moves.Length - included.Count > 0 ? Foldy.Core.Localization.Loc.F("org.excluded", moves.Length - included.Count) : "");
        var categories = moves.GroupBy(m => CategoryOf(m.TargetFolder), StringComparer.OrdinalIgnoreCase)
            .Select(g => new CategoryStat(g.Key, g.Count(), g.Count(m => m.Included), g.Sum(m => m.Size)))
            .OrderByDescending(c => c.Count).ThenBy(c => c.Key, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        return new(before, after, summary, categories, moves.Length, included.Count);
    }

    private void ApplyView(PlanView v)
    {
        BeforeTree = v.Before;
        AfterTree = v.After;
        Summary = v.Summary;
        ApplyCategories(v);
        ExecuteCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// 이유별 막대를 맞춘다. 묶음이 같으면 수만 고쳐 순서·고른 것을 그대로 두고(체크를 바꿀 때 줄이 뛰지 않게),
    /// 묶음이 바뀌면 많은 것부터 다시 늘어놓고 고르던 묶음을 다시 고른다.
    /// </summary>
    private void ApplyCategories(PlanView v)
    {
        var max = v.Categories.Count == 0 ? 0 : v.Categories.Max(c => c.Count);
        var stats = v.Categories.ToDictionary(c => c.Key, StringComparer.OrdinalIgnoreCase);
        var same = Categories.Count == v.Categories.Count + 1
            && Categories.Where(c => !c.IsAll).All(c => stats.ContainsKey(c.Key!));
        if (!same)
        {
            var keep = SelectedCategory?.Key;
            var list = new ObservableCollection<OrganizeCategoryViewModel> { new(null, Foldy.Core.Localization.Loc.T("org.cat.all")) };
            foreach (var c in v.Categories)
            {
                list.Add(new(c.Key, c.Key.Length == 0 ? Path.GetFileName(Plan?.Root ?? "") : c.Key));
            }

            Categories = list;
            var pick = list.FirstOrDefault(c => c.Key is not null && string.Equals(c.Key, keep, StringComparison.OrdinalIgnoreCase)) ?? list[0];
            _suppressFilter = true;
            SelectedCategory = pick;
            _suppressFilter = false;
            if (keep is not null && pick.IsAll)
            {
                ApplyFilter(); // 고르던 묶음이 없어졌다
            }
        }

        foreach (var c in Categories)
        {
            if (c.IsAll)
            {
                c.Set(v.Total, v.Included, v.Categories.Sum(x => x.Bytes), max);
            }
            else if (stats.TryGetValue(c.Key!, out var st))
            {
                c.Set(st.Count, st.Checked, st.Bytes, max);
            }
        }
    }

    private bool _suppressFilter;

    partial void OnSelectedCategoryChanged(OrganizeCategoryViewModel? value)
    {
        if (!_suppressFilter)
        {
            ApplyFilter();
        }
    }

    /// <summary>고른 묶음만 목록에 (모두면 전체 목록 그대로, 남길 파일을 바꿔 생긴 줄도 바로 보인다).</summary>
    private void ApplyFilter()
    {
        var key = SelectedCategory?.Key;
        VisibleMoves = key is null
            ? Moves
            : new ObservableCollection<MoveItemViewModel>(Moves.Where(m => string.Equals(m.Category, key, StringComparison.OrdinalIgnoreCase)));
        VisibleInfo = Foldy.Core.Localization.Loc.F("org.visible", VisibleMoves.Count);
    }

    /// <summary>보이는 줄 모두 체크 (고른 묶음만).</summary>
    [RelayCommand]
    private void CheckAllVisible() => SetVisible(true);

    /// <summary>보이는 줄 모두 해제 (고른 묶음만).</summary>
    [RelayCommand]
    private void UncheckAllVisible() => SetVisible(false);

    private void SetVisible(bool value)
    {
        if (State != OrganizeState.Ready)
        {
            return;
        }

        foreach (var m in VisibleMoves)
        {
            m.SetIncludedQuietly(value);
        }

        OnPlanEdited();
    }

    internal void OnPlanEdited()
    {
        if (Plan is not { } plan)
        {
            return;
        }

        RefreshDuplicatePanel();

        var gen = ++_viewGeneration;
        if (plan.Moves.Count <= SyncEditLimit)
        {
            ApplyView(ComputeView(plan));
            return;
        }

        // 큰 계획: 체크를 여러 개 바꾸는 동안 기다렸다가 한 번만 뒤에서 다시 그린다
        ExecuteCommand.NotifyCanExecuteChanged();
        var snapshot = plan.Moves.ToArray();
        _ = Task.Run(async () =>
        {
            await Task.Delay(300);
            if (gen != Volatile.Read(ref _viewGeneration))
            {
                return;
            }

            var v = ComputeView(plan, snapshot);
            _ui.Post(() =>
            {
                if (gen == _viewGeneration)
                {
                    ApplyView(v);
                }
            });
        });
    }

    partial void OnSelectedMoveChanged(MoveItemViewModel? value)
    {
        // 남길 파일을 바꾸다 고른 줄이 목록에서 빠지면(null) 보던 묶음을 그대로 둔다
        if (value is not null)
        {
            ShowDuplicateSet(value.Move.DuplicateSet);
        }
    }

    partial void OnSelectedDuplicateSetChanged(DuplicateSetViewModel? value)
    {
        if (value is not null)
        {
            ShowDuplicateSet(value.Set);
        }
    }

    /// <summary>같은 파일 묶음을 아래 칸에 보여 준다 (null이면 숨긴다).</summary>
    private void ShowDuplicateSet(DuplicateSet? set)
    {
        _shownSet = set;
        if (set is null || Plan is null)
        {
            HasDuplicateSelection = false;
            DuplicateMembers = [];
            DuplicateTitle = "";
            DuplicateTip = "";
            return;
        }

        DuplicateMembers = new ObservableCollection<DuplicateMemberViewModel>(set.Members.Select(m => new DuplicateMemberViewModel(set, m, this)));
        DuplicateTip = Foldy.Core.Localization.Loc.F("org.dup.tip", set.TargetFolder);
        RefreshDuplicatePanel();
        HasDuplicateSelection = true;
    }

    private void RefreshDuplicatePanel()
    {
        if (_shownSet is not { } set || Plan is not { } plan)
        {
            return;
        }

        DuplicateTitle = Foldy.Core.Localization.Loc.F("org.dup.title", set.Members.Count, set.Name, Foldy.Presentation.Files.FileItem.FormatBytes(set.Size));
        foreach (var m in DuplicateMembers)
        {
            _bySource.TryGetValue(m.FullPath, out var move);
            string outcome;
            if (set.Keeper == m.Member)
            {
                outcome = move is { IsIncluded: true }
                    ? Foldy.Core.Localization.Loc.F("org.dup.keepTo", move.Move.TargetFolder.Length == 0 ? Path.GetFileName(plan.Root) : move.Move.TargetFolder)
                    : Foldy.Core.Localization.Loc.T("org.dup.keepHere");
            }
            else
            {
                outcome = move is { IsIncluded: true }
                    ? Foldy.Core.Localization.Loc.F("org.dup.toDups", move.Move.TargetFolder)
                    : Foldy.Core.Localization.Loc.T("org.dup.stays");
            }

            m.Sync(outcome);
        }
    }

    /// <summary>같은 파일 묶음에서 남길 파일을 바꿨다: 계획을 고치고 목록·트리를 맞춘다.</summary>
    internal void ChooseKeeper(DuplicateMemberViewModel member)
    {
        if (Plan is not { } plan || State != OrganizeState.Ready)
        {
            RefreshDuplicatePanel(); // 지금은 바꿀 수 없다: 단추를 원래대로
            return;
        }

        var change = OrganizePlanner.ChooseKeeper(plan, member.Set, member.Member, p => File.Exists(p) || Directory.Exists(p));
        foreach (var r in change.Removed)
        {
            if (_bySource.Remove(r.Source, out var vm))
            {
                Moves.Remove(vm);
            }
        }

        foreach (var a in change.Added)
        {
            var vm = new MoveItemViewModel(a, plan.Root, this);
            _bySource[a.Source] = vm;
            Moves.Add(vm);
        }

        foreach (var c in change.Changed)
        {
            if (_bySource.TryGetValue(c.Source, out var vm))
            {
                vm.RefreshFromMove();
            }
        }

        if (_setViews.TryGetValue(member.Set, out var setView))
        {
            setView.Refresh();
        }

        if (SelectedCategory is { IsAll: false })
        {
            ApplyFilter(); // 묶음이 바뀐 줄을 맞춘다 (모두 보기면 목록이 그대로 바뀐다)
        }
        else
        {
            VisibleInfo = Foldy.Core.Localization.Loc.F("org.visible", VisibleMoves.Count);
        }

        OnPlanEdited(); // 트리·요약과 아래 칸을 다시 맞춘다
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
        Status = Foldy.Core.Localization.Loc.T("org.running");
        var progress = new ThrottledProgress(p => _ui.Post(() => Progress = p.Total == 0 ? 1 : (double)p.Done / p.Total));
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
                Status = Foldy.Core.Localization.Loc.F("org.moved", result.Moved)
                    + (result.Failed.Count > 0 ? Foldy.Core.Localization.Loc.F("org.failed", result.Failed.Count) : "")
                    + (result.Canceled ? Foldy.Core.Localization.Loc.T("org.midCancel") : "")
                    + Foldy.Core.Localization.Loc.T("org.undoHint");
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

    /// <summary>파일 수십만 개를 옮길 때 화면에 진행률을 초당 10번까지만 보낸다 (한 개마다 보내면 화면이 멈춘다).</summary>
    private sealed class ThrottledProgress(Action<OrganizeProgress> report) : IProgress<OrganizeProgress>
    {
        private long _last;

        public void Report(OrganizeProgress value)
        {
            var now = Environment.TickCount64;
            if (value.Done + 1 < value.Total && now - Interlocked.Read(ref _last) < 100)
            {
                return;
            }

            Interlocked.Exchange(ref _last, now);
            report(value);
        }
    }

    private bool CanExecute() => State == OrganizeState.Ready && Plan is { Blocked: null } && Plan.IncludedMoves.Any();

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private async Task UndoAsync()
    {
        var journal = LastJournalPath!;
        State = OrganizeState.Undoing;
        Status = Foldy.Core.Localization.Loc.T("org.undoing");
        try
        {
            var r = await Task.Run(() => OrganizeExecutor.Undo(journal, _ops));
            _ui.Post(() =>
            {
                LastUndo = r;
                State = OrganizeState.Undone;
                Status = r.AlreadyUndone
                    ? Foldy.Core.Localization.Loc.T("org.alreadyUndone")
                    : Foldy.Core.Localization.Loc.F("org.restored", r.Restored)
                      + (r.LeftModified.Count > 0 ? Foldy.Core.Localization.Loc.F("org.leftModified", r.LeftModified.Count) : "")
                      + (r.Missing.Count > 0 ? Foldy.Core.Localization.Loc.F("org.missing", r.Missing.Count) : "");
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
            Status = Foldy.Core.Localization.Loc.T("org.changed");
        }
    }
}
