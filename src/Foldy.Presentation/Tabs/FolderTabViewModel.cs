using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using Foldy.Core.FileSystem;
using Foldy.Presentation.Files;
using Foldy.Presentation.Navigation;
using Foldy.Presentation.Services;

namespace Foldy.Presentation.Tabs;

public enum LoadState { Idle, Loading, Refreshing, Error }

/// <summary>폴더 감시 (로컬 고정 드라이브만, 느린 드라이브는 폴링 — M2).</summary>
public interface IFolderWatchService
{
    IDisposable? Watch(string folder, Action changed);
}

/// <summary>폴더의 Git 상태 (저장소 안일 때만). 브랜치 칩 문구와 항목별 배지.</summary>
public sealed record GitFolderInfo(string RepoRoot, string BranchText, Func<string, GitFileStatus> BadgeOf, int Changes);

/// <summary>Git 상태 공급자 (git.exe 실행은 백그라운드). 저장소가 아니면 null.</summary>
public interface IGitStatusProvider
{
    Task<GitFolderInfo?> GetAsync(string folder, CancellationToken ct);
}

/// <summary>폴더 크기 공급자 (색인 서비스). 색인 밖이면 null.</summary>
public interface IFolderSizeProvider
{
    Task<IReadOnlyDictionary<string, long>?> GetAsync(string folder, CancellationToken ct);
}

/// <summary>탭 공통 환경 (서비스 묶음). 패널의 모든 탭이 공유한다.</summary>
public sealed class TabEnvironment(IFileSystemProvider provider, IUiDispatcher ui)
{
    public IFileSystemProvider Provider { get; } = provider;
    public IUiDispatcher Ui { get; } = ui;
    public ListingCache Cache { get; } = new();
    public Func<FsEntry, string>? TypeName { get; init; }
    public IFolderWatchService? Watcher { get; init; }
    public Func<bool> ShowHidden { get; init; } = () => false;
    public IGitStatusProvider? Git { get; init; }
    public IFolderSizeProvider? FolderSizes { get; init; }
}

/// <summary>
/// 폴더 탭 하나의 탐색 상태 (설계서 6.1 TabViewModel).
/// 스레드 소유권: UI 스레드. 열거는 Provider(드라이브 큐), 정렬은 ThreadPool.
/// </summary>
public sealed partial class FolderTabViewModel : TabBase
{
    private readonly TabEnvironment _env;
    private CancellationTokenSource? _loadCts;
    private CancellationTokenSource? _debounceCts;
    private IDisposable? _watch;
    private int _generation;
    private bool _active;

    [ObservableProperty]
    private string _path = "";

    [ObservableProperty]
    private ItemList _items = ItemList.Empty;

    [ObservableProperty]
    private LoadState _state;

    [ObservableProperty]
    private string? _error;

    [ObservableProperty]
    private string _status = "";

    [ObservableProperty]
    private SortSpec _sort = new();

    [ObservableProperty]
    private int _loadedCount;

    /// <summary>Git 저장소 안이면 상태 (목록이 먼저 뜨고 0.1~0.3초 뒤에 채워진다, DESIGN.md).</summary>
    [ObservableProperty]
    private GitFolderInfo? _git;

    public FolderTabViewModel(TabEnvironment env, string? path = null)
    {
        _env = env;
        if (path is not null)
        {
            _path = PathUtil.Normalize(path) ?? path;
            Title = PathUtil.DisplayName(_path);
        }
    }

    public override TabKind Kind => TabKind.Folder;

    public override string? Location => Path;

    public NavigationHistory History { get; } = new();

    public bool CanGoBack => History.CanGoBack;

    public bool CanGoForward => History.CanGoForward;

    public bool CanGoUp => PathUtil.Parent(Path) is not null;

    /// <summary>목록이 통째로 바뀐 뒤 포커스를 둘 이름 (뷰가 선택·스크롤을 맞춘다).</summary>
    public event Action<string?>? ItemsReplaced;

    /// <summary>뷰가 알려주는 현재 선택. 파일 작업 명령이 쓴다.</summary>
    public IReadOnlyList<FileItem> Selection { get; set; } = [];

    /// <summary>현재 포커스 행 이름 (세션 저장·목록 교체 후 복원용).</summary>
    public string? FocusName { get; set; }

    /// <summary>탭을 다시 보일 때 복원할 세로 스크롤 위치.</summary>
    public double ScrollOffset { get; set; }

    /// <summary>이미지만 보기 (탭마다 기억, 설계서 5.12).</summary>
    public bool ImagesOnly { get; set; }

    /// <summary>마지막 열기 소요 시간 (진단용).</summary>
    public TimeSpan LastLoadTime { get; private set; }

    public bool IsLoaded { get; private set; }

    // ───────────── 이동 ─────────────

    /// <summary>입력한 경로로 이동. 형식이 틀리면 오류만 표시하고 현재 위치에 머문다.</summary>
    public Task NavigateAsync(string input)
    {
        var target = PathUtil.Normalize(input);
        if (target is null)
        {
            Error = $"경로 형식이 올바르지 않습니다: {input}";
            State = LoadState.Error;
            return Task.CompletedTask;
        }

        return LoadAsync(target, push: !string.Equals(target, Path, StringComparison.OrdinalIgnoreCase) && IsLoaded, focus: null);
    }

    public Task GoBackAsync()
    {
        var t = History.GoBack(Path);
        RaiseHistory();
        return t is null ? Task.CompletedTask : LoadAsync(t, push: false, focus: null);
    }

    public Task GoForwardAsync()
    {
        var t = History.GoForward(Path);
        RaiseHistory();
        return t is null ? Task.CompletedTask : LoadAsync(t, push: false, focus: null);
    }

    /// <summary>위로: 상위 폴더로 가면서 방금 있던 폴더에 포커스를 둔다 (탐색기와 같음).</summary>
    public Task GoUpAsync()
    {
        var parent = PathUtil.Parent(Path);
        return parent is null ? Task.CompletedTask : LoadAsync(parent, push: true, focus: PathUtil.DisplayName(Path));
    }

    public Task RefreshAsync(string? focus = null) => LoadAsync(Path, push: false, focus: focus ?? FocusName, refresh: true);

    /// <summary>처음 보일 때만 불러온다 (시작 시 활성 탭만 로드, 나머지는 지연 — DESIGN.md).</summary>
    public override void OnActivated()
    {
        _active = true;
        if (!IsLoaded && State != LoadState.Loading && !string.IsNullOrEmpty(Path))
        {
            _ = LoadAsync(Path, push: false, focus: FocusName);
        }
        else
        {
            StartWatching();
        }
    }

    public override void OnDeactivated()
    {
        _active = false;
        StopWatching();
    }

    public override void Close()
    {
        _loadCts?.Cancel();
        _debounceCts?.Cancel();
        StopWatching();
    }

    public void CancelLoad() => _loadCts?.Cancel();

    private async Task LoadAsync(string target, bool push, string? focus, bool refresh = false)
    {
        _loadCts?.Cancel();
        var cts = _loadCts = new CancellationTokenSource();
        var gen = ++_generation;
        var sw = Stopwatch.StartNew();
        var committed = false;
        ItemList? live = null;
        var all = new List<FileItem>(256);
        var showHidden = _env.ShowHidden();

        Error = null;
        IsBusy = true;
        State = LoadState.Loading;

        if (!refresh && _env.Cache.TryGet(target) is { } cached)
        {
            Commit(target, push);
            Items = new ItemList(cached);
            ItemsReplaced?.Invoke(focus);
            State = LoadState.Refreshing;
            committed = true;
        }

        try
        {
            await foreach (var batch in _env.Provider.ListBatchedAsync(PathUtil.IsSftp(target) ? new FsPath(ProviderId.Sftp, target) : FsPath.Local(target), cts.Token))
            {
                var mapped = new List<FileItem>(batch.Count);
                foreach (var e in batch)
                {
                    var fi = new FileItem(e, _env.TypeName);
                    if (showHidden || !fi.IsHidden)
                    {
                        mapped.Add(fi);
                    }
                }

                all.AddRange(mapped);
                LoadedCount = all.Count;
                if (!committed)
                {
                    Commit(target, push);
                    committed = true;
                }

                // 캐시를 보여주는 중이거나 새로 고침이면 깜빡이지 않게 끝까지 모은 뒤 한 번에 바꾼다.
                if (State == LoadState.Loading && !refresh)
                {
                    if (live is null)
                    {
                        live = new ItemList();
                        Items = live;
                    }

                    live.AddBatch(mapped);
                }

                if (sw.Elapsed > TimeSpan.FromSeconds(2))
                {
                    Status = $"불러오는 중… {all.Count:N0}개";
                }
            }

            if (!committed)
            {
                Commit(target, push); // 빈 폴더
            }

            var spec = Sort;
            var sorted = await Task.Run(() => FileSorter.Sort(all.ToArray(), spec, cts.Token), cts.Token);
            if (gen != _generation)
            {
                return;
            }

            Items = new ItemList(sorted);
            ItemsReplaced?.Invoke(focus ?? (refresh ? FocusName : null));
            _env.Cache.Put(target, sorted);
            _ = LoadGitAsync(target, gen);
            _ = LoadFolderSizesAsync(target, gen);
            LastLoadTime = sw.Elapsed;
            IsLoaded = true;
            State = LoadState.Idle;
            Status = "";
            if (_active)
            {
                StartWatching();
            }
        }
        catch (OperationCanceledException)
        {
            if (gen == _generation)
            {
                State = LoadState.Idle;
                Status = $"취소됨 ({all.Count:N0}개까지 표시)";
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Security.SecurityException)
        {
            if (gen != _generation)
            {
                return;
            }

            Error = ex switch
            {
                DirectoryNotFoundException => $"폴더를 찾을 수 없습니다: {target}",
                UnauthorizedAccessException => $"접근 권한이 없습니다: {target}",
                Core.Threading.DriveUnresponsiveException => $"드라이브가 응답하지 않습니다: {target}",
                _ => ex.Message,
            };
            State = LoadState.Error;
            if (committed && live is not null)
            {
                Items = new ItemList(all);
            }
        }
        finally
        {
            if (gen == _generation)
            {
                IsBusy = false;
            }
        }
    }

    /// <summary>폴더 크기 열: 색인에서 디스크 I/O 없이 (DESIGN.md "폴더를 열자마자 보이는 폴더 크기").</summary>
    private async Task LoadFolderSizesAsync(string folder, int gen)
    {
        if (_env.FolderSizes is null || PathUtil.IsSftp(folder))
        {
            return;
        }

        try
        {
            var sizes = await _env.FolderSizes.GetAsync(folder, CancellationToken.None);
            if (sizes is null || gen != _generation)
            {
                return;
            }

            var any = false;
            foreach (var item in Items)
            {
                if (item.IsFolder && sizes.TryGetValue(item.Name, out var s))
                {
                    item.FolderSize = s;
                    any = true;
                }
            }

            if (any)
            {
                FolderSizesLoaded?.Invoke();
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or OperationCanceledException)
        {
            // 색인이 없거나 끊기면 크기 없이 둔다
        }
    }

    /// <summary>색인 서비스가 나중에 연결됐을 때 현재 목록의 폴더 크기를 다시 채운다.</summary>
    public Task RefreshFolderSizesAsync() => IsLoaded ? LoadFolderSizesAsync(Path, _generation) : Task.CompletedTask;

    /// <summary>폴더 크기가 채워졌을 때 (뷰가 다시 그린다).</summary>
    public event Action? FolderSizesLoaded;

    private async Task LoadGitAsync(string folder, int gen)
    {
        if (_env.Git is null || PathUtil.IsSftp(folder))
        {
            Git = null;
            return;
        }

        try
        {
            var info = await _env.Git.GetAsync(folder, CancellationToken.None);
            if (gen == _generation)
            {
                Git = info;
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or OperationCanceledException)
        {
            Git = null;
        }
    }

    private void Commit(string target, bool push)
    {
        if (!string.Equals(target, Path, StringComparison.OrdinalIgnoreCase))
        {
            if (push && !string.IsNullOrEmpty(Path))
            {
                History.Push(Path);
            }

            StopWatching();
            Path = target;
            Title = PathUtil.DisplayName(target);
            Selection = [];
        }

        RaiseHistory();
    }

    private void RaiseHistory()
    {
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanGoForward));
        OnPropertyChanged(nameof(CanGoUp));
    }

    // ───────────── 정렬·찾기 ─────────────

    public async Task SetSortAsync(SortKey key)
    {
        Sort = Sort.Toggle(key);
        var spec = Sort;
        var snapshot = Items.ToArray();
        var sorted = await Task.Run(() => FileSorter.Sort(snapshot, spec));
        Items = new ItemList(sorted);
        ItemsReplaced?.Invoke(FocusName);
        _env.Cache.Put(Path, sorted);
    }

    /// <summary>Type-ahead: start 다음 항목부터 이름이 prefix로 시작하는 첫 행 (없으면 -1). 한 바퀴 돈다.</summary>
    public int FindPrefix(string prefix, int start)
    {
        var n = Items.Count;
        if (n == 0 || string.IsNullOrEmpty(prefix))
        {
            return -1;
        }

        for (var k = 0; k < n; k++)
        {
            var i = (Math.Max(start, 0) + k) % n;
            if (Items[i].Name.StartsWith(prefix, StringComparison.CurrentCultureIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    // ───────────── 감시 ─────────────

    private void StartWatching()
    {
        if (_watch is not null || _env.Watcher is null || string.IsNullOrEmpty(Path))
        {
            return;
        }

        _watch = _env.Watcher.Watch(Path, () => _env.Ui.Post(ScheduleRefresh));
    }

    private void StopWatching()
    {
        _watch?.Dispose();
        _watch = null;
    }

    /// <summary>변경 알림이 몰려와도 300ms에 한 번만 새로 고친다.</summary>
    private void ScheduleRefresh()
    {
        _debounceCts?.Cancel();
        var cts = _debounceCts = new CancellationTokenSource();
        _ = Task.Delay(300, cts.Token).ContinueWith(
            _ => _env.Ui.Post(() =>
            {
                if (!cts.IsCancellationRequested && State is LoadState.Idle or LoadState.Error)
                {
                    _ = RefreshAsync();
                }
            }),
            cts.Token,
            TaskContinuationOptions.OnlyOnRanToCompletion,
            TaskScheduler.Default);
    }
}
