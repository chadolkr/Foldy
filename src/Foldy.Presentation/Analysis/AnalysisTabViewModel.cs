using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Foldy.Analysis;
using Foldy.Presentation.Tabs;

namespace Foldy.Presentation.Analysis;

/// <summary>
/// 디스크 분석 탭 (설계서 7.3, DESIGN.md "디스크 정리"). 분석 스냅샷 DB 하나를 읽기 전용으로 열고 트리맵, 큰 폴더·파일 Top N,
/// 확장자별 용량, 오래된 파일, 정리 제안, 지난 분석 대비 증감을 보여 준다. 화면은 Foldy.UI의 AnalysisView.
/// 이 ViewModel은 아무것도 지우지 않는다: [휴지통으로]는 <see cref="RecycleRequested"/>로 호스트에 넘긴다.
/// DB 조회는 백그라운드에서 하나씩(_gate) 하고, 결과는 await 뒤(UI 컨텍스트)에 반영한다. 스레드 소유권: UI 스레드 전용.
/// </summary>
public sealed partial class AnalysisTabViewModel : TabBase
{
    /// <summary>목록마다 보여 줄 개수.</summary>
    public const int TopN = 100;

    private readonly AnalysisCoordinator _coordinator;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, string> _ruleTitles;
    private CancellationTokenSource _cts = new();
    private AnalysisDb? _db;
    private bool _loaded;

    public AnalysisTabViewModel(AnalysisCoordinator coordinator, string root)
    {
        ArgumentNullException.ThrowIfNull(coordinator);
        ArgumentNullException.ThrowIfNull(root);
        _coordinator = coordinator;
        Root = root;
        Title = "분석: " + root;
        _ruleTitles = coordinator.Rules.Rules.ToDictionary(r => r.Id, r => r.Title ?? r.Id, StringComparer.Ordinal);
    }

    public override TabKind Kind => TabKind.Analysis;

    public override string? Location => Root;

    /// <summary>분석할 폴더나 드라이브 ("C:\", "E:\", "D:\작업").</summary>
    public string Root { get; }

    /// <summary>[휴지통으로]를 눌렀을 때. 호스트가 확인 후 휴지통으로 옮기고 <see cref="MarkRecycled"/>를 부른다.</summary>
    public event EventHandler<RecycleRequestEventArgs>? RecycleRequested;

    /// <summary>목록 항목을 두 번 눌렀을 때 (호스트가 패널에서 연다).</summary>
    public event EventHandler<OpenPathEventArgs>? OpenRequested;

    // ───────────── 상태 ─────────────

    [ObservableProperty]
    private string _status = "";

    /// <summary>0~1. 분석 중일 때만 의미 있다.</summary>
    [ObservableProperty]
    private double _progress;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string _errorText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmpty))]
    private bool _hasSnapshot;

    [ObservableProperty]
    private string _scanText = "";

    public bool HasError => ErrorText.Length > 0;

    public bool ShowEmpty => !HasSnapshot && !IsBusy;

    public bool IsIdle => !IsBusy;

    /// <summary>IsBusy는 TabBase 속성이라 partial 훅 대신 여기서 딸린 속성을 알린다.</summary>
    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName == nameof(IsBusy))
        {
            OnPropertyChanged(nameof(IsIdle));
            OnPropertyChanged(nameof(ShowEmpty));
            AnalyzeCommand.NotifyCanExecuteChanged();
        }
    }

    // ───────────── 트리맵 ─────────────

    public ObservableCollection<BreadcrumbItem> Breadcrumb { get; } = [];

    [ObservableProperty]
    private IReadOnlyList<TreemapNode> _treemapItems = [];

    [ObservableProperty]
    private long _currentFolderId;

    [ObservableProperty]
    private string _currentFolderText = "";

    // ───────────── 목록 ─────────────

    [ObservableProperty]
    private IReadOnlyList<FolderListItem> _topFolders = [];

    [ObservableProperty]
    private IReadOnlyList<FileListItem> _topFiles = [];

    [ObservableProperty]
    private IReadOnlyList<FolderListItem> _mostFilesFolders = [];

    [ObservableProperty]
    private IReadOnlyList<ExtensionListItem> _extensions = [];

    [ObservableProperty]
    private IReadOnlyList<ExtensionListItem> _groups = [];

    [ObservableProperty]
    private IReadOnlyList<FileListItem> _oldFiles = [];

    public ObservableCollection<CandidateItem> Candidates { get; } = [];

    [ObservableProperty]
    private string _reclaimText = "";

    [ObservableProperty]
    private IReadOnlyList<GrowthItem> _growth = [];

    [ObservableProperty]
    private string _growthText = "";

    [ObservableProperty]
    private IReadOnlyList<SnapshotRef> _previousSnapshots = [];

    /// <summary>비교할 이전 스냅샷 (기본: 바로 이전).</summary>
    [ObservableProperty]
    private SnapshotRef? _selectedPrevious;

    /// <summary>큰 폴더를 "자신 제외 크기"로 (하위 폴더와 중복 제거).</summary>
    [ObservableProperty]
    private bool _ownSizeOnly;

    /// <summary>오래된 파일 기준 (년).</summary>
    [ObservableProperty]
    private int _oldFileYears = 1;

    /// <summary>비교 대상 스냅샷 고르기 (화면의 날짜 버튼).</summary>
    [RelayCommand]
    public void SelectPrevious(SnapshotRef? snapshot)
    {
        if (snapshot is not null)
        {
            SelectedPrevious = snapshot;
        }
    }

    /// <summary>오래된 파일 기준 바꾸기 ("1", "2", "5" 년).</summary>
    [RelayCommand]
    public void SetOldFileYears(string? years)
    {
        if (int.TryParse(years, NumberStyles.Integer, CultureInfo.InvariantCulture, out var y) && y > 0)
        {
            OldFileYears = y;
        }
    }

    partial void OnOwnSizeOnlyChanged(bool value) => _ = RefreshListAsync(LoadTopFolders, r => TopFolders = r);

    partial void OnOldFileYearsChanged(int value) => _ = RefreshListAsync(LoadOldFiles, r => OldFiles = r);

    partial void OnSelectedPreviousChanged(SnapshotRef? value) => _ = RefreshGrowthAsync(value);

    private Task RefreshGrowthAsync(SnapshotRef? prev) => RefreshListAsync(db => LoadGrowth(db, prev), ApplyGrowth);

    // ───────────── 수명 ─────────────

    public override void OnActivated()
    {
        if (!_loaded)
        {
            _loaded = true;
            _ = OpenLatestAsync();
        }
    }

    public override void Close()
    {
        _cts.Cancel();
        _ = DisposeDbAsync();
    }

    private async Task DisposeDbAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            _db?.Dispose();
            _db = null;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>최신 스냅샷을 열고, 없으면 분석을 시작한다.</summary>
    public async Task OpenLatestAsync()
    {
        ErrorText = "";
        IsBusy = true;
        Status = "지난 분석 결과를 찾는 중…";
        try
        {
            var set = await _coordinator.FindLatestAsync(Root, _cts.Token);
            if (set is null)
            {
                IsBusy = false;
                await AnalyzeAsync();
                return;
            }

            await LoadSnapshotAsync(set);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Fail(ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanAnalyze() => !IsBusy;

    /// <summary>새로 분석한다 (서비스가 있으면 색인에서, 없으면 폴더를 직접 읽어서).</summary>
    [RelayCommand(CanExecute = nameof(CanAnalyze))]
    public async Task AnalyzeAsync()
    {
        ErrorText = "";
        IsBusy = true;
        Progress = 0;
        Status = "분석을 시작합니다…";
        var progress = new Progress<AnalysisProgress>(p =>
        {
            Progress = p.Fraction;
            Status = p.Message;
        });
        try
        {
            var set = await _coordinator.AnalyzeAsync(Root, progress, _cts.Token);
            await LoadSnapshotAsync(set);
        }
        catch (OperationCanceledException)
        {
            Status = "분석을 취소했습니다";
        }
        catch (Exception ex)
        {
            Fail(ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>분석 중이면 멈춘다 (만들던 임시 파일은 지워지고 이전 결과는 그대로).</summary>
    [RelayCommand]
    public void Cancel()
    {
        _cts.Cancel();
        _cts = new CancellationTokenSource();
    }

    private void Fail(Exception ex)
    {
        ErrorText = ex is UnauthorizedAccessException
            ? "분석 결과 파일을 읽을 권한이 없습니다: " + ex.Message
            : ex.Message;
        Status = "";
    }

    /// <summary>스냅샷 하나를 열고 모든 목록을 채운다.</summary>
    public async Task LoadSnapshotAsync(SnapshotSet set)
    {
        ArgumentNullException.ThrowIfNull(set);
        Status = "분석 결과를 여는 중…";
        var root = Root;
        var loaded = await WithDbAsync(set, db =>
        {
            var scan = db.Scan();
            var start = db.FindDir(root) ?? db.RootId;
            var r = new Loaded(
                scan,
                start,
                LoadTopFolders(db),
                [.. db.TopFiles(TopN).Select(f => new FileListItem(f))],
                [.. db.MostFilesFolders(TopN).Select(f => new FolderListItem(f, f.TotalFiles))],
                ToExtItems(db.ExtensionStats().Take(TopN).Select(e => (e.Ext.Length == 0 ? "(확장자 없음)" : "." + e.Ext, e.Files, e.Alloc, ExtensionGroups.GroupOf(e.Ext))), scan.TotalAlloc),
                ToExtItems(db.GroupStats().Select(g => (g.Group, g.Files, g.Alloc, string.Join(", ", g.Extensions.Select(x => x.Length == 0 ? "-" : "." + x)))), scan.TotalAlloc),
                LoadOldFiles(db),
                db.Candidates());
            return r;
        });
        if (loaded is null)
        {
            return;
        }

        HasSnapshot = true;
        var s = loaded.Scan;
        var source = set.FromService ? "색인 서비스" : "직접 열거";
        ScanText = string.Create(CultureInfo.CurrentCulture,
            $"{s.StartedUtc.ToLocalTime():yyyy-MM-dd HH:mm} 분석 · 파일 {s.TotalFiles:N0}개 · 폴더 {s.TotalDirs:N0}개 · {AnalysisText.FormatSize(s.TotalAlloc)} · {source}");
        TopFolders = loaded.TopFolders;
        TopFiles = loaded.TopFiles;
        MostFilesFolders = loaded.MostFiles;
        Extensions = loaded.Extensions;
        Groups = loaded.Groups;
        OldFiles = loaded.OldFiles;
        Candidates.Clear();
        foreach (var c in loaded.Candidates)
        {
            Candidates.Add(new CandidateItem(c, _ruleTitles.TryGetValue(c.Rule, out var t) ? t : c.Rule));
        }

        UpdateReclaimText();
        PreviousSnapshots = set.Previous;
        // SelectedPrevious 변경이 증감 목록을 다시 읽는다
        var first = set.Previous.Count > 0 ? set.Previous[0] : null;
        if (SelectedPrevious == first)
        {
            await RefreshGrowthAsync(SelectedPrevious);
        }
        else
        {
            SelectedPrevious = first;
        }

        await ShowFolderAsync(loaded.Start);
        Status = "";
    }

    private sealed record Loaded(
        ScanInfo Scan, long Start, IReadOnlyList<FolderListItem> TopFolders, IReadOnlyList<FileListItem> TopFiles, IReadOnlyList<FolderListItem> MostFiles,
        IReadOnlyList<ExtensionListItem> Extensions, IReadOnlyList<ExtensionListItem> Groups, IReadOnlyList<FileListItem> OldFiles, IReadOnlyList<CandidateRow> Candidates);

    private static IReadOnlyList<ExtensionListItem> ToExtItems(IEnumerable<(string Label, long Files, long Alloc, string Detail)> rows, long total) =>
        [.. rows.Select(r => new ExtensionListItem(r.Label, r.Files, r.Alloc, total > 0 ? (double)r.Alloc / total : 0, r.Detail))];

    // 아래 Load* 는 백그라운드에서 돌므로 속성에 쓰지 않고 결과만 돌려준다.
    private IReadOnlyList<FolderListItem> LoadTopFolders(AnalysisDb db)
    {
        var own = OwnSizeOnly;
        return [.. db.TopFolders(TopN, own).Select(f => new FolderListItem(f, own ? f.OwnAlloc : f.Alloc))];
    }

    private IReadOnlyList<FileListItem> LoadOldFiles(AnalysisDb db) =>
        [.. db.OldFiles(DateTime.UtcNow.AddYears(-Math.Max(1, OldFileYears)), TopN).Select(f => new FileListItem(f))];

    private sealed record GrowthResult(IReadOnlyList<GrowthItem> Items, string Text);

    private static GrowthResult LoadGrowth(AnalysisDb db, SnapshotRef? prev)
    {
        if (prev is null)
        {
            return new GrowthResult([], "비교할 지난 분석이 없습니다. 다음에 분석하면 이번 결과와 비교합니다.");
        }

        var rows = db.Growth(prev.DbPath, TopN);
        var total = db.Scan().TotalAlloc - db.PreviousTotal(prev.DbPath);
        return new GrowthResult(
            [.. rows.Where(r => r.Delta != 0).Select(r => new GrowthItem(r))],
            string.Create(CultureInfo.CurrentCulture, $"{prev.ScanTimeUtc.ToLocalTime():yyyy-MM-dd HH:mm} 분석 대비 전체 {(total >= 0 ? "+" : "")}{AnalysisText.FormatSize(total)}"));
    }

    private void ApplyGrowth(GrowthResult r)
    {
        Growth = r.Items;
        GrowthText = r.Text;
    }

    /// <summary>DB 작업을 백그라운드에서 하나씩. <paramref name="set"/>을 주면 그 스냅샷으로 바꿔 연다. 실패하면 ErrorText에 쓰고 default.</summary>
    private async Task<T?> WithDbAsync<T>(SnapshotSet? set, Func<AnalysisDb, T?> work)
        where T : class
    {
        await _gate.WaitAsync();
        try
        {
            return await Task.Run(() =>
            {
                if (set is not null && !string.Equals(_db?.FilePath, set.Latest.DbPath, StringComparison.OrdinalIgnoreCase))
                {
                    var db = AnalysisDb.Open(set.Latest.DbPath);
                    _db?.Dispose();
                    _db = db;
                }

                return _db is null ? null : work(_db);
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Fail(ex);
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>목록 하나만 다시 읽는다 (설정 토글). 조회는 백그라운드, 대입은 돌아와서(UI 스레드).</summary>
    private async Task RefreshListAsync<T>(Func<AnalysisDb, T> query, Action<T> apply)
        where T : class
    {
        if (_db is null)
        {
            return;
        }

        if (await WithDbAsync(null, query) is { } r)
        {
            apply(r);
        }
    }

    // ───────────── 트리맵 탐색 ─────────────

    /// <summary>폴더 칸을 누르면 그 폴더로 들어간다.</summary>
    [RelayCommand]
    public Task DrillDownAsync(TreemapNode? node) =>
        node is { IsDir: true, IsRemainder: false } ? ShowFolderAsync(node.Id) : Task.CompletedTask;

    /// <summary>브레드크럼 항목으로 올라간다.</summary>
    [RelayCommand]
    public Task NavigateToAsync(BreadcrumbItem? item) => item is null ? Task.CompletedTask : ShowFolderAsync(item.Id);

    /// <summary>한 단계 위로.</summary>
    [RelayCommand]
    public Task UpAsync() => Breadcrumb.Count >= 2 ? ShowFolderAsync(Breadcrumb[^2].Id) : Task.CompletedTask;

    /// <summary>목록의 폴더를 트리맵에서 본다.</summary>
    [RelayCommand]
    public Task ShowInTreemapAsync(FolderListItem? item) => item is null ? Task.CompletedTask : ShowFolderAsync(item.Row.Id);

    public async Task ShowFolderAsync(long dirId)
    {
        var r = await WithDbAsync(null, db =>
        {
            var folder = db.Folder(dirId);
            if (folder is null)
            {
                return null;
            }

            var children = db.Children(dirId);
            var chain = db.Chain(dirId);
            return new FolderView(folder, children, chain);
        });
        if (r is null)
        {
            return;
        }

        CurrentFolderId = dirId;
        TreemapItems = BuildNodes(r.Folder.Alloc, r.Children);
        Breadcrumb.Clear();
        foreach (var (id, name) in r.Chain)
        {
            Breadcrumb.Add(new BreadcrumbItem(id, name));
        }

        CurrentFolderText = string.Create(CultureInfo.CurrentCulture,
            $"{r.Folder.Path} · {AnalysisText.FormatSize(r.Folder.Alloc)} · 파일 {r.Folder.TotalFiles:N0}개");
    }

    private sealed record FolderView(FolderRow Folder, IReadOnlyList<TreemapChild> Children, IReadOnlyList<(long Id, string Name)> Chain);

    /// <summary>자식 칸 + 나머지(작은 파일·시스템 메타, 부모 크기와의 차이). 크기 0인 칸은 뺀다.</summary>
    public static IReadOnlyList<TreemapNode> BuildNodes(long folderAlloc, IReadOnlyList<TreemapChild> children)
    {
        ArgumentNullException.ThrowIfNull(children);
        var list = new List<TreemapNode>(children.Count + 1);
        var dirIndex = 0;
        long shown = 0;
        foreach (var c in children)
        {
            if (c.Alloc <= 0)
            {
                continue;
            }

            shown += c.Alloc;
            list.Add(new TreemapNode(c.Id, c.IsDir, c.Name, c.Alloc, c.IsDir ? dirIndex++ % 8 : -1, c.Tag));
        }

        var rest = folderAlloc - shown;
        if (rest > 0 && rest >= folderAlloc / 200)
        {
            list.Add(new TreemapNode(-1, false, "기타 (작은 파일·시스템)", rest, -2, null, IsRemainder: true));
            list.Sort((a, b) => b.Alloc.CompareTo(a.Alloc));
        }

        return list;
    }

    // ───────────── 정리 제안 ─────────────

    /// <summary>
    /// [휴지통으로]: 호스트에 요청만 한다. 이 ViewModel은 파일을 지우지 않는다 (설계서 8.3 "삭제는 기본 휴지통").
    /// </summary>
    [RelayCommand]
    public void Recycle(CandidateItem? item)
    {
        if (item is null || item.IsPending)
        {
            return;
        }

        item.IsPending = true;
        RecycleRequested?.Invoke(this, new RecycleRequestEventArgs([item]));
    }

    /// <summary>호스트가 휴지통으로 옮기기를 마쳤을 때 (실패하거나 취소하면 <paramref name="done"/>=false).</summary>
    public void MarkRecycled(CandidateItem item, bool done = true)
    {
        ArgumentNullException.ThrowIfNull(item);
        item.IsPending = false;
        if (done)
        {
            Candidates.Remove(item);
            UpdateReclaimText();
        }
    }

    private void UpdateReclaimText()
    {
        var total = Candidates.Sum(c => c.Row.Reclaim);
        ReclaimText = Candidates.Count == 0
            ? "정리할 만한 항목을 찾지 못했습니다."
            : string.Create(CultureInfo.CurrentCulture, $"제안 {Candidates.Count:N0}개 · 최대 {AnalysisText.FormatSize(total)} 확보 가능");
    }

    [RelayCommand]
    public void Open(object? item)
    {
        var (path, folder) = item switch
        {
            FolderListItem f => (f.Path, true),
            FileListItem f => (f.Path, false),
            CandidateItem c => (c.Path, c.IsFolder),
            GrowthItem g => (g.Path, true),
            _ => ((string?)null, false),
        };
        if (path is not null)
        {
            OpenRequested?.Invoke(this, new OpenPathEventArgs(path, folder));
        }
    }
}
