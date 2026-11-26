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

    /// <summary>드라이브별 열기 속도 기록. 느린 드라이브는 Git 감지·미리보기·썸네일을 줄인다. 앱은 <see cref="DriveSpeedTracker.Shared"/>를 넣는다.</summary>
    public DriveSpeedTracker Speed { get; init; } = new();

    /// <summary>폴더를 다 열었을 때 (새로 고침 제외). 사이드바의 최근 폴더가 쓴다.</summary>
    public Action<string>? Visited { get; set; }

    /// <summary>폴더 열기가 끝났을 때 (경로, 걸린 시간, 항목 수, 실패면 예외). 진단 기록용.</summary>
    public Action<string, TimeSpan, int, Exception?>? Loaded { get; set; }
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

    // 시각 확인 스레드가 읽는 "보이는 탭인가" (_active와 같이 바꾼다)
    private volatile bool _seen;

    [ObservableProperty]
    private string _path = "";

    /// <summary>이 폴더의 드라이브가 느린 드라이브로 판정됐는지 (경로 표시줄 옆 안내).</summary>
    [ObservableProperty]
    private bool _isSlowDrive;

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

    /// <summary>마지막 열기에서 첫 항목 묶음이 화면 목록에 들어가기까지 걸린 시간 (진단·성능 관문: 10만 개에서 100ms).</summary>
    public TimeSpan FirstBatchTime { get; private set; }

    public bool IsLoaded { get; private set; }

    // ───────────── 이동 ─────────────

    /// <summary>입력한 경로로 이동. 형식이 틀리면 오류만 표시하고 현재 위치에 머문다.</summary>
    /// <param name="focus">다 읽은 뒤 고르고 보이게 할 항목 이름 (찾기 창의 "들어 있는 폴더 보기")</param>
    public Task NavigateAsync(string input, string? focus = null)
    {
        var target = PathUtil.Normalize(input);
        if (target is null)
        {
            Error = Foldy.Core.Localization.Loc.F("tab.badPath", input);
            State = LoadState.Error;
            return Task.CompletedTask;
        }

        if (focus is not null)
        {
            FocusName = focus;
        }

        return LoadAsync(target, push: !string.Equals(target, Path, StringComparison.OrdinalIgnoreCase) && IsLoaded, focus: focus);
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
        _seen = true;
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
        _seen = false;
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
        var firstBatchSeen = false;
        string? slowNotice = null;

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
                        _streaming = true;
                    }

                    live.AddBatch(mapped);
                }

                if (!firstBatchSeen)
                {
                    firstBatchSeen = true;
                    FirstBatchTime = sw.Elapsed;
                    if (_env.Speed.Record(target, FirstBatchTime))
                    {
                        slowNotice = Foldy.Core.Localization.Loc.F("tab.slowDrive", DriveSpeedTracker.RootOf(target));
                    }

                    IsSlowDrive = _env.Speed.IsSlow(target);
                }

                if (sw.Elapsed > TimeSpan.FromSeconds(2))
                {
                    Status = Foldy.Core.Localization.Loc.F("tab.loading", all.Count);
                }
            }

            if (!committed)
            {
                Commit(target, push); // 빈 폴더
            }

            // 방금 Foldy가 한 바꾸기를 드라이브가 아직 모르면(느린 드라이브의 옛 목록) 그 바꾸기를 덮어 보인다
            if (string.Equals(target, Path, StringComparison.OrdinalIgnoreCase))
            {
                PatchWithLocalChanges(all, dropConfirmed: true);
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
            _env.Loaded?.Invoke(target, sw.Elapsed, all.Count, null);
            IsLoaded = true;
            State = LoadState.Idle;
            Status = slowNotice ?? "";
            if (!refresh)
            {
                _env.Visited?.Invoke(target);
            }

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
                Status = Foldy.Core.Localization.Loc.F("tab.cancelled", all.Count);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Security.SecurityException)
        {
            if (gen != _generation)
            {
                return;
            }

            _env.Loaded?.Invoke(target, sw.Elapsed, 0, ex);
            Error = ex switch
            {
                DirectoryNotFoundException when target.Length <= 3 => Foldy.Core.Localization.Loc.F("tab.driveOffline", target),
                DirectoryNotFoundException => Foldy.Core.Localization.Loc.F("tab.notFound", target),
                UnauthorizedAccessException => Foldy.Core.Localization.Loc.F("tab.noAccess", target),
                Core.Threading.DriveUnresponsiveException => Foldy.Core.Localization.Loc.F("tab.noResponse", target),
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
                _streaming = false;
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
            // 목록이 이미 그려진 뒤에, UI 스레드 밖에서, 다른 폴더로 가면 취소되게 (느린 드라이브에서도 탐색을 막지 않는다)
            var ct = _loadCts?.Token ?? CancellationToken.None;
            var provider = _env.FolderSizes;
            var sizes = await Task.Run(() => provider.GetAsync(folder, ct), ct);
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
        if (_env.Git is null || PathUtil.IsSftp(folder) || _env.Speed.IsSlow(folder))
        {
            Git = null; // 느린 드라이브는 git.exe를 돌리지 않는다 (Git 바가 안 보인다)
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
            IsSlowDrive = _env.Speed.IsSlow(target);
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

    // ───────────── Foldy가 직접 한 바꾸기 (드라이브 알림을 기다리지 않는다) ─────────────

    /// <summary>
    /// Foldy가 직접 한 바꾸기(이름 바꾸기·새 폴더·삭제·이동)를 목록에 붙들고 있는 시간.
    /// 느린 드라이브(회사 보안 드라이브·네트워크)는 바꾼 뒤에도 한동안 옛 목록을 돌려주기 때문이다 (사용자 2026-11-24 K: 드라이브).
    /// 드라이브 목록이 바꾼 대로 나오면 그때 바로 놓는다.
    /// </summary>
    public TimeSpan LocalChangeHold { get; set; } = TimeSpan.FromSeconds(30);

    private readonly List<LocalChange> _changes = [];

    // 처음 읽는 중이라 나오는 대로 보이는 목록(live)을 쓰는 중인가
    private bool _streaming;

    /// <summary>Added가 null이면 Name이 없어진 것, 있으면 그 항목이 생긴 것.</summary>
    private sealed record LocalChange(string Name, FsEntry? Added, DateTime At);

    /// <summary>
    /// Foldy가 이 폴더에서 한 바꾸기를 목록에 바로 반영한다. 새로 고침을 기다리지 않는다.
    /// gone: 없어진 이름, added: 생긴 항목. focus: 바꾼 뒤 포커스를 둘 이름 (null이면 그대로).
    /// </summary>
    public void ApplyLocalChanges(IReadOnlyList<string> gone, IReadOnlyList<FsEntry> added, string? focus = null)
    {
        if (gone.Count == 0 && added.Count == 0)
        {
            return;
        }

        var now = DateTime.UtcNow;
        foreach (var name in gone)
        {
            Remember(new LocalChange(name, null, now));
        }

        foreach (var e in added)
        {
            Remember(new LocalChange(e.Name, e, now));
        }

        // 처음 읽는 중이면(나오는 대로 보이는 목록) 읽기가 끝날 때 붙인다. 새로 고침 중이면 지금 목록을 바로 고친다
        if (_streaming || !IsLoaded)
        {
            return;
        }

        var list = Items.ToList();
        PatchWithLocalChanges(list, dropConfirmed: false);
        var sorted = FileSorter.Sort(list, Sort);
        Items = new ItemList(sorted);
        ItemsReplaced?.Invoke(focus ?? FocusName);
        _env.Cache.Put(Path, sorted);
        if (focus is not null)
        {
            FocusName = focus;
        }
    }

    /// <summary>같은 이름의 앞선 기록은 새 기록이 대신한다 (지웠다가 같은 이름으로 다시 만들기, 이름을 두 번 바꾸기).</summary>
    private void Remember(LocalChange c)
    {
        _changes.RemoveAll(x => string.Equals(x.Name, c.Name, StringComparison.OrdinalIgnoreCase));
        _changes.Add(c);
    }

    /// <summary>
    /// 목록에 남아 있는 바꾸기를 붙인다. dropConfirmed면 드라이브 목록이 이미 바꾼 대로인 기록은 놓는다.
    /// 오래된 기록(<see cref="LocalChangeHold"/>)은 버린다: 그 사이 다른 데서 바뀐 것을 가리지 않게.
    /// </summary>
    private void PatchWithLocalChanges(List<FileItem> list, bool dropConfirmed)
    {
        if (_changes.Count == 0)
        {
            return;
        }

        var now = DateTime.UtcNow;
        _changes.RemoveAll(c => now - c.At > LocalChangeHold);
        for (var k = 0; k < _changes.Count; k++)
        {
            var c = _changes[k];
            var at = list.FindIndex(i => string.Equals(i.Name, c.Name, StringComparison.OrdinalIgnoreCase));
            var confirmed = c.Added is null ? at < 0 : at >= 0 && string.Equals(list[at].Name, c.Name, StringComparison.Ordinal);
            if (confirmed)
            {
                if (dropConfirmed)
                {
                    _changes.RemoveAt(k--);
                }

                continue;
            }

            if (at >= 0)
            {
                list.RemoveAt(at);
            }

            if (c.Added is { } e)
            {
                list.Add(new FileItem(e, _env.TypeName));
            }
        }
    }

    // ───────────── 감시 ─────────────

    private void StartWatching()
    {
        if (_watch is not null || _env.Watcher is null || string.IsNullOrEmpty(Path))
        {
            return;
        }

        _watch = _env.Speed.IsSlow(Path) ? null : _env.Watcher.Watch(Path, () => _env.Ui.Post(ScheduleRefresh));
        if (_watch is null)
        {
            // 감시할 수 없는 드라이브(네트워크·SFTP)는 활성 탭만 주기적으로 새로 고친다 (DESIGN.md: 30~60초 폴링)
            // 시각만 보면 알 수 없는 것(파일 크기·내용만 바뀜)은 이 주기로 맞춘다
            var period = PathUtil.IsSftp(Path) ? TimeSpan.FromSeconds(60) : TimeSpan.FromSeconds(45);
            var full = new Timer(_ => _env.Ui.Post(() =>
            {
                if (_active && State == LoadState.Idle)
                {
                    _ = RefreshAsync();
                }
            }), null, period, period);
            if (PathUtil.IsSftp(Path))
            {
                _watch = full;
            }
            else
            {
                // 네트워크·회사 클라우드 드라이브: 보고 있는 폴더의 수정 시각만 1초마다 물어 파일이 생기거나 지워지면 바로 새로 고친다 (2026-11-24)
                var poller = new FolderTimePoller(Path, () => _seen, () => _env.Ui.Post(ScheduleRefresh));
                _watch = new Both(full, poller);
            }
        }
    }

    /// <summary>둘을 함께 끈다.</summary>
    private sealed class Both(IDisposable a, IDisposable b) : IDisposable
    {
        public void Dispose()
        {
            a.Dispose();
            b.Dispose();
        }
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
