using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Foldy.Presentation.Files;
using Foldy.Presentation.Jobs;
using Foldy.Presentation.Navigation;
using Foldy.Presentation.Panes;
using Foldy.Presentation.Persistence;
using Foldy.Presentation.Services;
using Foldy.Presentation.Tabs;

namespace Foldy.Presentation.Shell;

/// <summary>창 전체: 두 패널, 활성 패널, 작업 목록, 세션 (설계서 6.1 ShellViewModel). UI 스레드 전용.</summary>
public sealed partial class ShellViewModel : ObservableObject
{
    private readonly TabEnvironment _env;
    private readonly ShellServices _services;
    private CancellationTokenSource? _saveDebounce;

    [ObservableProperty]
    private PaneViewModel _activePane;

    [ObservableProperty]
    private string _status = "";

    public ShellViewModel(TabEnvironment env, ShellServices services)
    {
        _env = env;
        _services = services;
        Left = new PaneViewModel(PaneSide.Left, env);
        Right = new PaneViewModel(PaneSide.Right, env);
        _activePane = Left;
        Left.IsActive = true;
        foreach (var p in new[] { Left, Right })
        {
            p.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(PaneViewModel.ActiveTab))
                {
                    HookActiveTab(p);
                    ScheduleSave();
                }
            };
            p.Tabs.CollectionChanged += (_, _) => ScheduleSave();
        }
    }

    public PaneViewModel Left { get; }

    public PaneViewModel Right { get; }

    public PaneViewModel OtherPane => ActivePane == Left ? Right : Left;

    public ObservableCollection<JobViewModel> Jobs { get; } = [];

    public AppSettings Settings { get; set; } = new();

    /// <summary>파일 이름 검색 (색인 또는 폴더 훑기).</summary>
    public IFileSearchService? Search { get; set; }

    /// <summary>검색 결과 탭을 연다. 범위는 현재 폴더 (색인 검색이면 전체 드라이브).</summary>
    public SearchTabViewModel? StartSearch(PaneViewModel pane, string query)
    {
        query = query.Trim();
        if (Search is null || query.Length == 0)
        {
            return null;
        }

        var tab = new SearchTabViewModel(Search, query, pane.ActiveFolder?.Path ?? (pane.ActiveTab as SearchTabViewModel)?.Scope, _env.TypeName);
        pane.AddTab(tab, activate: true, index: pane.ActiveTab is null ? null : pane.Tabs.IndexOf(pane.ActiveTab) + 1, assignColor: true);
        return tab;
    }

    /// <summary>세션이 바뀌면 (2초 디바운스 후) 호출된다. 앱이 파일로 저장한다.</summary>
    public event Action<SessionState>? SaveRequested;

    /// <summary>Markdown 탭 등을 열 때 뷰가 포커스를 옮기도록 알린다.</summary>
    public event Action<PaneViewModel>? PaneActivated;

    public void Activate(PaneViewModel pane)
    {
        if (ActivePane == pane)
        {
            return;
        }

        ActivePane.IsActive = false;
        ActivePane = pane;
        pane.IsActive = true;
        OnPropertyChanged(nameof(OtherPane));
        PaneActivated?.Invoke(pane);
        ScheduleSave();
    }

    public void SwitchPane() => Activate(OtherPane);

    // ───────────── 세션 ─────────────

    public void Restore(SessionState s, string fallbackPath)
    {
        RestorePane(Left, s.Left, fallbackPath);
        RestorePane(Right, s.Right, fallbackPath);
        Activate(s.Active == PaneSide.Right ? Right : Left);
        if (s.Active == PaneSide.Left)
        {
            Left.IsActive = true;
            Right.IsActive = false;
        }
    }

    private void RestorePane(PaneViewModel pane, PaneState st, string fallbackPath)
    {
        foreach (var t in st.Tabs)
        {
            TabBase tab;
            if (t.Kind is TabKind.Search or TabKind.Git or TabKind.Analysis)
            {
                continue;
            }

            if (t.Kind == TabKind.Markdown)
            {
                tab = new MarkdownTabViewModel(t.Path);
            }
            else
            {
                var f = new FolderTabViewModel(_env, t.Path) { FocusName = t.Focus, Sort = new SortSpec(t.SortKey, t.SortDescending) };
                f.History.Restore(t.Back, t.Forward);
                tab = f;
            }

            tab.ColorIndex = t.Color;
            pane.AddTab(tab, activate: false);
        }

        if (pane.Tabs.Count == 0)
        {
            pane.NewFolderTab(fallbackPath, activate: false);
        }

        var active = pane.Tabs[Math.Clamp(st.ActiveIndex, 0, pane.Tabs.Count - 1)];
        pane.ActiveTab = null;
        pane.ActiveTab = active; // 활성 탭만 로드 (나머지는 처음 열 때)
    }

    public SessionState Capture(WindowPlacement? window, double split) => new()
    {
        Left = CapturePane(Left),
        Right = CapturePane(Right),
        Active = ActivePane.Side,
        Window = window,
        SplitRatio = split,
    };

    private static PaneState CapturePane(PaneViewModel p) => new()
    {
        ActiveIndex = p.ActiveTab is null || p.ActiveTab.Kind is TabKind.Search or TabKind.Git or TabKind.Analysis ? 0 : Math.Max(0, p.Tabs.Where(t => t.Kind is not (TabKind.Search or TabKind.Git or TabKind.Analysis)).ToList().IndexOf(p.ActiveTab)),
        Tabs = [.. p.Tabs.Where(t => t.Kind is not (TabKind.Search or TabKind.Git or TabKind.Analysis)).Select(t => t switch
        {
            FolderTabViewModel f => new TabState
            {
                Kind = TabKind.Folder,
                Path = f.Path,
                Back = [.. f.History.Back],
                Forward = [.. f.History.Forward],
                SortKey = f.Sort.Key,
                SortDescending = f.Sort.Descending,
                Focus = f.FocusName,
                Color = f.ColorIndex,
            },
            _ => new TabState { Kind = t.Kind, Path = t.Location ?? "", Color = t.ColorIndex },
        })],
    };

    /// <summary>탭 이동·경로 변경 후 2초 디바운스로 저장 (강제 종료 후에도 마지막 상태 복원, 설계서 2장).</summary>
    public void ScheduleSave()
    {
        _saveDebounce?.Cancel();
        var cts = _saveDebounce = new CancellationTokenSource();
        _ = Task.Delay(2000, cts.Token).ContinueWith(
            _ => _env.Ui.Post(() =>
            {
                if (!cts.IsCancellationRequested)
                {
                    SaveRequested?.Invoke(Capture(null, double.NaN));
                }
            }),
            cts.Token,
            TaskContinuationOptions.OnlyOnRanToCompletion,
            TaskScheduler.Default);
    }

    private void HookActiveTab(PaneViewModel p)
    {
        if (p.ActiveTab is FolderTabViewModel f)
        {
            f.PropertyChanged -= OnTabPropertyChanged;
            f.PropertyChanged += OnTabPropertyChanged;
        }
    }

    private void OnTabPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(FolderTabViewModel.Path) or nameof(FolderTabViewModel.Sort))
        {
            ScheduleSave();
        }
    }

    // ───────────── 열기 ─────────────

    /// <summary>Enter·더블클릭: 폴더는 이동, Markdown은 연결 앱이 없으면 내장 뷰어 탭, 나머지는 셸로.</summary>
    public async Task OpenAsync(PaneViewModel pane, FileItem item)
    {
        if (item.IsFolder)
        {
            if (pane.ActiveTab is FolderTabViewModel f)
            {
                await f.NavigateAsync(item.FullPath);
            }
            else
            {
                pane.NewFolderTab(item.FullPath); // 검색 결과 등에서 폴더를 열면 새 폴더 탭
            }

            return;
        }

        if (MarkdownExtensions.Contains(item.Extension) &&
            (Settings.MarkdownOpen == "alwaysFoldy" || await _services.Open.ShouldUseBuiltInMarkdownAsync(item.FullPath)))
        {
            OpenMarkdown(pane, item.FullPath);
            return;
        }

        try
        {
            _services.Open.OpenWithShell(item.FullPath);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            Status = $"열 수 없습니다: {ex.Message}";
        }
    }

    public static readonly HashSet<string> MarkdownExtensions = new(StringComparer.OrdinalIgnoreCase) { ".md", ".markdown", ".mdown" };

    public static MarkdownTabViewModel OpenMarkdown(PaneViewModel pane, string path)
    {
        var existing = pane.Tabs.OfType<MarkdownTabViewModel>().FirstOrDefault(t => string.Equals(t.FilePath, path, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            pane.ActiveTab = existing;
            return existing;
        }

        var tab = new MarkdownTabViewModel(path);
        pane.AddTab(tab, activate: true, index: pane.ActiveTab is null ? null : pane.Tabs.IndexOf(pane.ActiveTab) + 1, assignColor: true);
        return tab;
    }

    public void OpenTerminal(TerminalKind kind, bool elevated)
    {
        var folder = ActivePane.ActiveFolder?.Path ?? (ActivePane.ActiveTab as MarkdownTabViewModel)?.Folder;
        if (string.IsNullOrEmpty(folder))
        {
            return;
        }

        try
        {
            _services.Terminal.Open(folder, kind, elevated);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            // 관리자 UAC 창에서 [아니요]를 누르면 Win32Exception(1223)
            Status = ex is System.ComponentModel.Win32Exception { NativeErrorCode: 1223 } ? "관리자 실행이 취소됐습니다" : $"터미널을 열 수 없습니다: {ex.Message}";
        }
    }

    // ───────────── 파일 작업 (모달 없이 작업 패널에 진행 표시) ─────────────

    public static IReadOnlyList<string> SelectedPaths(PaneViewModel pane) =>
        (pane.ActiveTab switch { FolderTabViewModel f => f.Selection, SearchTabViewModel s => s.Selection, _ => [] }).Select(i => i.FullPath).ToArray();

    public Task CopyToOtherAsync() => TransferAsync(move: false);

    public Task MoveToOtherAsync() => TransferAsync(move: true);

    private async Task TransferAsync(bool move)
    {
        var src = SelectedPaths(ActivePane);
        var dest = OtherPane.ActiveFolder?.Path;
        if (src.Count == 0 || dest is null)
        {
            Status = src.Count == 0 ? "먼저 파일을 선택하세요" : "반대 패널에 폴더 탭이 없습니다";
            return;
        }

        await TransferAsync(src, dest, move);
    }

    /// <summary>붙여넣기·드롭·F5/F6 공통.</summary>
    public async Task TransferAsync(IReadOnlyList<string> src, string dest, bool move)
    {
        var verb = move ? "이동" : "복사";
        var job = StartJob($"{JobViewModel.Describe(src)} {verb} → {PathUtil.DisplayName(dest)}", move ? Mascot.JobKind.Move : Mascot.JobKind.Copy);
        var r = move
            ? await _services.FileOps.MoveAsync(src, dest, job, job.Token)
            : await _services.FileOps.CopyAsync(src, dest, job, job.Token);
        FinishJob(job, r);
        await RefreshFoldersAsync([dest, .. src.Select(s => PathUtil.Parent(s) ?? s)]);
    }

    /// <summary>경로들을 휴지통으로 (디스크 분석의 정리 후보). 성공 여부를 돌려준다.</summary>
    public async Task<bool> RecycleAsync(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0)
        {
            return false;
        }

        var job = StartJob($"{JobViewModel.Describe(paths)} 휴지통으로", Mascot.JobKind.Delete);
        var r = await _services.FileOps.DeleteAsync(paths, recycle: true, job, job.Token);
        FinishJob(job, r);
        await RefreshFoldersAsync(paths.Select(s => PathUtil.Parent(s) ?? s));
        return r.Succeeded;
    }

    public const string SftpDeleteConfirmMessage = "SFTP에는 휴지통이 없습니다. 영구 삭제할까요?";

    /// <summary>예/아니요 확인 창 (UI가 연결한다). SFTP 삭제처럼 되돌릴 수 없는 작업 전에 쓴다.</summary>
    public Func<string, Task<bool>>? ConfirmAsync { get; set; }

    public async Task DeleteSelectionAsync(bool permanent)
    {
        var pane = ActivePane;
        var src = SelectedPaths(pane);
        if (src.Count == 0)
        {
            return;
        }

        // SFTP에는 휴지통이 없다: 지우기 전에 반드시 묻는다 (확인 창이 없으면 지우지 않는다).
        if (src.Any(PathUtil.IsSftp))
        {
            permanent = true;
            if (ConfirmAsync is null || !await ConfirmAsync(SftpDeleteConfirmMessage))
            {
                Status = "삭제를 취소했습니다";
                return;
            }
        }

        var job = StartJob($"{JobViewModel.Describe(src)} {(permanent ? "영구 삭제" : "휴지통으로")}", permanent ? Mascot.JobKind.DeletePermanent : Mascot.JobKind.Delete);
        var r = await _services.FileOps.DeleteAsync(src, recycle: !permanent, job, job.Token);
        FinishJob(job, r);
        await RefreshFoldersAsync(src.Select(s => PathUtil.Parent(s) ?? s));
    }

    public async Task<bool> RenameAsync(FolderTabViewModel tab, FileItem item, string newName)
    {
        newName = newName.Trim();
        if (newName.Length == 0 || newName == item.Name)
        {
            return false;
        }

        if (newName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            Status = "이름에 쓸 수 없는 문자가 있습니다: \\ / : * ? \" < > |";
            return false;
        }

        var r = await _services.FileOps.RenameAsync(item.FullPath, newName, CancellationToken.None);
        if (!r.Succeeded)
        {
            Status = r.Aborted ? "" : $"이름을 바꿀 수 없습니다: {r.Error}";
            return false;
        }

        await RefreshFoldersAsync([tab.Path], focus: newName);
        return true;
    }

    public async Task<string?> NewFolderAsync(FolderTabViewModel tab, string name = "새 폴더")
    {
        var r = await _services.FileOps.NewFolderAsync(tab.Path, name, CancellationToken.None);
        if (!r.Succeeded)
        {
            Status = r.Aborted ? "" : $"폴더를 만들 수 없습니다: {r.Error}";
            return null;
        }

        var created = r.CreatedPath is null ? name : Path.GetFileName(r.CreatedPath);
        await RefreshFoldersAsync([tab.Path], focus: created);
        return created;
    }

    private JobViewModel StartJob(string title, Mascot.JobKind kind = Mascot.JobKind.Copy)
    {
        var job = new JobViewModel(title, _env.Ui) { State = JobState.Running, MascotKind = kind, MascotEnabled = Settings.Animations };
        Jobs.Insert(0, job);
        while (Jobs.Count > 30)
        {
            Jobs.RemoveAt(Jobs.Count - 1);
        }

        return job;
    }

    private void FinishJob(JobViewModel job, FileOpResult r)
    {
        job.Complete(r);
        Status = r.Succeeded ? r.Error ?? "" : r.Aborted ? "취소됨" : $"{job.Title}: {r.Error}";
    }

    public void ClearFinishedJobs()
    {
        foreach (var j in Jobs.Where(j => j.IsFinished).ToList())
        {
            Jobs.Remove(j);
        }
    }

    /// <summary>작업이 끝난 폴더를 보고 있는 모든 탭을 새로 고친다 (감시가 없는 드라이브 대비).</summary>
    public async Task RefreshFoldersAsync(IEnumerable<string> folders, string? focus = null)
    {
        var set = new HashSet<string>(folders.Select(f => PathUtil.Normalize(f) ?? f), StringComparer.OrdinalIgnoreCase);
        foreach (var f in set)
        {
            _env.Cache.Invalidate(f);
        }

        var tasks = new List<Task>();
        foreach (var pane in new[] { Left, Right })
        {
            if (pane.ActiveTab is FolderTabViewModel t && set.Contains(t.Path))
            {
                tasks.Add(t.RefreshAsync(focus));
            }
        }

        await Task.WhenAll(tasks);
    }
}

/// <summary>ShellViewModel이 쓰는 플랫폼 서비스 묶음.</summary>
public sealed record ShellServices(IFileOperationService FileOps, IOpenFileService Open, ITerminalService Terminal);
