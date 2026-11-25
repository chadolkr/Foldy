using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Foldy.Core.FileSystem;
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

    /// <summary>
    /// 안 될 때의 이유를 눈길이 머무는 곳(마우스면 포인터 아래, 키보드면 대상 아래)에 보여 주는 방법 (UI가 연결, 디자인 원칙 11).
    /// 보여 주지 못하면 false를 돌려주고, 그때는 상태 칸에 쓴다.
    /// </summary>
    public Func<string, bool>? ShowHint { get; set; }

    /// <summary>안 되는 이유·다음 할 일을 마우스 포인터 바로 아래에 알린다 (못 하면 상태 칸).</summary>
    public void Hint(string text)
    {
        if (ShowHint?.Invoke(text) != true)
        {
            Status = text;
        }
    }

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

        if (OpenSpecial?.Invoke(item.FullPath) == true)
        {
            return; // 잠긴 파일·금고처럼 Foldy가 직접 여는 파일
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
            Hint(Foldy.Core.Localization.Loc.F("ops.cantOpen", ex.Message));
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
            Hint(ex is System.ComponentModel.Win32Exception { NativeErrorCode: 1223 } ? Foldy.Core.Localization.Loc.T("ops.adminCancelled") : Foldy.Core.Localization.Loc.F("ops.cantOpenTerminal", ex.Message));
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
            Hint(src.Count == 0 ? Foldy.Core.Localization.Loc.T("ops.selectFirst") : Foldy.Core.Localization.Loc.T("ops.noOtherFolder"));
            return;
        }

        await TransferAsync(src, dest, move);
    }

    /// <summary>붙여넣기·드롭·F5/F6 공통.</summary>
    public async Task TransferAsync(IReadOnlyList<string> src, string dest, bool move)
    {
        var verb = move ? Foldy.Core.Localization.Loc.T("ops.move") : Foldy.Core.Localization.Loc.T("ops.copy");
        var job = StartJob($"{JobViewModel.Describe(src)} {verb} → {PathUtil.DisplayName(dest)}", move ? Mascot.JobKind.Move : Mascot.JobKind.Copy);
        job.Retry = () => TransferAsync(src, dest, move);
        var r = move
            ? await _services.FileOps.MoveAsync(src, dest, job, job.Token)
            : await _services.FileOps.CopyAsync(src, dest, job, job.Token);
        FinishJob(job, r, src);
        await ReflectTransferAsync(src, dest, move);
        await RefreshFoldersAsync([dest, .. src.Select(s => PathUtil.Parent(s) ?? s)]);
    }

    /// <summary>경로들을 휴지통으로 (폴더 분석의 정리 후보). 성공 여부를 돌려준다.</summary>
    public async Task<bool> RecycleAsync(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0)
        {
            return false;
        }

        var job = StartJob(Foldy.Core.Localization.Loc.F("ops.job.recycle", JobViewModel.Describe(paths)), Mascot.JobKind.Delete);
        var r = await _services.FileOps.DeleteAsync(paths, recycle: true, job, job.Token);
        FinishJob(job, r, paths);
        await ReflectGoneAsync(paths);
        await RefreshFoldersAsync(paths.Select(s => PathUtil.Parent(s) ?? s));
        return r.Succeeded;
    }

    /// <summary>경로들을 휴지통을 거치지 않고 지운다 (금고·잠그기 뒤 암호 없는 원본). 사용자가 고른 뒤에만 부른다.</summary>
    public async Task<bool> DeletePermanentlyAsync(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0)
        {
            return false;
        }

        var job = StartJob(Foldy.Core.Localization.Loc.F("ops.job.deletePermanent", JobViewModel.Describe(paths)), Mascot.JobKind.DeletePermanent);
        var r = await _services.FileOps.DeleteAsync(paths, recycle: false, job, job.Token);
        FinishJob(job, r, paths);
        await ReflectGoneAsync(paths);
        await RefreshFoldersAsync(paths.Select(s => PathUtil.Parent(s) ?? s));
        return r.Succeeded;
    }

    /// <summary>
    /// SFTP 삭제 확인 문구: 몇 개를 지우는지와 이름(최대 5개)을 보여 주고, 되돌릴 수 없다는 걸 친근하게 알린다.
    /// 이 확인은 설정으로 끌 수 없다.
    /// </summary>
    public static string SftpDeleteConfirmMessage(IReadOnlyList<string> paths)
    {
        const int Shown = 5;
        var names = paths.Take(Shown).Select(p => "  · " + PathUtil.DisplayName(p));
        var more = paths.Count > Shown ? Foldy.Core.Localization.Loc.F("ops.more", paths.Count - Shown) : "";
        return Foldy.Core.Localization.Loc.T("ops.sftpDeleteWarn")
            + Foldy.Core.Localization.Loc.F("ops.deleteList", paths.Count, string.Join("\n", names), more);
    }

    /// <summary>Foldy가 직접 여는 파일(잠긴 메모·잠긴 파일·금고)이면 열고 true (UI가 연결한다).</summary>
    public Func<string, bool>? OpenSpecial { get; set; }

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
            if (ConfirmAsync is null || !await ConfirmAsync(SftpDeleteConfirmMessage(src)))
            {
                Status = Foldy.Core.Localization.Loc.T("ops.deleteCancelled");
                return;
            }
        }

        // 파일이 아주 많으면: 휴지통(기본, 되살릴 수 있음)과 빨리 지우기(되살릴 수 없음) 중 고르게 한다
        else if (AskBigDeleteAsync is not null && src.Any(Directory.Exists))
        {
            var count = await Task.Run(() => FastDelete.Count(src, TimeSpan.FromSeconds(2)));
            if (count.Files >= FastDelete.AskThreshold)
            {
                var canFast = !src.Any(FastDelete.IsProtected);
                switch (await AskBigDeleteAsync(new BigDeleteQuestion(src, count, canFast)))
                {
                    case BigDeleteChoice.Cancel:
                        Status = Foldy.Core.Localization.Loc.T("ops.deleteCancelled");
                        return;
                    case BigDeleteChoice.Fast when canFast:
                        await FastDeleteAsync(src, count);
                        return;
                    default:
                        permanent = false;
                        break;
                }
            }
        }

        var job = StartJob($"{JobViewModel.Describe(src)} {(permanent ? Foldy.Core.Localization.Loc.T("cmd.deletePermanent") : Foldy.Core.Localization.Loc.T("ops.toRecycle"))}", permanent ? Mascot.JobKind.DeletePermanent : Mascot.JobKind.Delete);
        var r = await _services.FileOps.DeleteAsync(src, recycle: !permanent, job, job.Token);
        FinishJob(job, r, src);
        await ReflectGoneAsync(src);
        await RefreshFoldersAsync(src.Select(s => PathUtil.Parent(s) ?? s));
    }

    /// <summary>파일이 많은 폴더를 지우기 전에 묻는다 (UI가 연결한다). 없으면 묻지 않고 평소대로.</summary>
    public Func<BigDeleteQuestion, Task<BigDeleteChoice>>? AskBigDeleteAsync { get; set; }

    /// <summary>빨리 지우기: 휴지통을 거치지 않고 병렬로 지운다. 사용자가 되살릴 수 없다는 안내를 보고 고른 뒤에만.</summary>
    public async Task FastDeleteAsync(IReadOnlyList<string> src, DeleteCount count)
    {
        var job = StartJob(Foldy.Core.Localization.Loc.F("ops.job.fastDelete", JobViewModel.Describe(src)), Mascot.JobKind.DeletePermanent);
        FastDeleteResult res;
        try
        {
            res = await Task.Run(() => FastDelete.Delete(src, (n, _) =>
                job.Report(new FileOpProgress(n, Math.Max(count.Files, n + 1), Foldy.Core.Localization.Loc.F("ops.deletedN", n))), ct: job.Token));
        }
        catch (InvalidOperationException e)
        {
            FinishJob(job, FileOpResult.Fail(e.Message));
            return;
        }

        var r = res.Canceled
            ? FileOpResult.Canceled
            : res.FailedCount > 0
                ? FileOpResult.Fail(Foldy.Core.Localization.Loc.F("ops.deleteFailed", res.FailedCount, string.Join(", ", res.Failed.Take(3).Select(PathUtil.DisplayName))))
                : new FileOpResult(true, false, Foldy.Core.Localization.Loc.F("ops.deletedAll", res.DeletedFiles));
        FinishJob(job, r, res.FailedCount > 0 ? res.Failed : null);
        if (res.Canceled)
        {
            Status = Foldy.Core.Localization.Loc.F("ops.deleteStopped", res.DeletedFiles);
        }

        await ReflectGoneAsync(src);
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
            Hint(Foldy.Core.Localization.Loc.T("ops.badName"));
            return false;
        }

        var r = await _services.FileOps.RenameAsync(item.FullPath, newName, CancellationToken.None);
        if (!r.Succeeded)
        {
            if (!r.Aborted)
            {
                Hint(Foldy.Core.Localization.Loc.F("ops.cantRename", r.Error));
            }

            return false;
        }

        var newPath = PathUtil.IsSftp(item.FullPath)
            ? item.FullPath[..(item.FullPath.LastIndexOf('/') + 1)] + newName
            : Path.Combine(Path.GetDirectoryName(item.FullPath) ?? "", newName);
        Foldy.Core.Tags.FileTags.Moved(item.FullPath, newPath);

        // 드라이브 알림·새로 고침을 기다리지 않고 목록을 바로 고친다 (느린 드라이브는 새로 고침해도 한동안 옛 이름을 돌려준다, 2026-11-24)
        var renamed = item.Entry with { Name = newName, Path = item.Entry.Path with { Value = newPath } };
        ReflectLocal(tab.Path, [item.Name], [renamed], tab, newName);
        _ = RefreshFoldersAsync([tab.Path]);
        return true;
    }

    public async Task<string?> NewFolderAsync(FolderTabViewModel tab, string? name = null)
    {
        name ??= Foldy.Core.Localization.Loc.T("cmd.newFolder");
        var r = await _services.FileOps.NewFolderAsync(tab.Path, name, CancellationToken.None);
        if (!r.Succeeded)
        {
            if (!r.Aborted)
            {
                Hint(Foldy.Core.Localization.Loc.F("ops.cantCreateFolder", r.Error));
            }

            return null;
        }

        var created = r.CreatedPath is null ? name : PathUtil.DisplayName(r.CreatedPath);
        var full = r.CreatedPath ?? (PathUtil.IsSftp(tab.Path) ? tab.Path.TrimEnd('/') + "/" + created : Path.Combine(tab.Path, created));
        var now = DateTime.UtcNow;
        var entry = new FsEntry(created, PathUtil.IsSftp(full) ? new FsPath(ProviderId.Sftp, full) : FsPath.Local(full), FsKind.Folder, 0, null, now, now, FileAttributes.Directory, null);
        ReflectLocal(tab.Path, [], [entry], tab, created);
        _ = RefreshFoldersAsync([tab.Path]);
        return created;
    }

    // ───────────── Foldy가 한 바꾸기를 목록에 바로 (드라이브 알림을 기다리지 않는다) ─────────────

    /// <summary>그 폴더를 보여 주는 모든 폴더 탭 (양쪽 패널, 안 보이는 탭 포함).</summary>
    private IEnumerable<FolderTabViewModel> TabsAt(string folder)
    {
        var key = PathUtil.Normalize(folder) ?? folder;
        return new[] { Left, Right }.SelectMany(p => p.Tabs).OfType<FolderTabViewModel>()
            .Where(t => string.Equals(t.Path, key, StringComparison.OrdinalIgnoreCase));
    }

    private void ReflectLocal(string folder, IReadOnlyList<string> gone, IReadOnlyList<FsEntry> added, FolderTabViewModel? focusTab = null, string? focus = null)
    {
        foreach (var t in TabsAt(folder).ToList())
        {
            t.ApplyLocalChanges(gone, added, ReferenceEquals(t, focusTab) ? focus : null);
        }
    }

    /// <summary>한 번에 바로 반영하는 최대 개수 (그 이상은 평소처럼 새로 고침만).</summary>
    private const int MaxReflect = 200; // 느린 드라이브에서 하나씩 묻는 시간이 길어지지 않게

    /// <summary>지우기·옮기기 뒤: 실제로 없어진 것만 목록에서 바로 뺀다 (일부만 실패하거나 멈췄을 수 있다).</summary>
    private async Task ReflectGoneAsync(IReadOnlyList<string> paths)
    {
        var local = paths.Where(p => !PathUtil.IsSftp(p)).Take(MaxReflect).ToArray();
        if (local.Length == 0)
        {
            return;
        }

        var gone = await Task.Run(() => local.Where(p => !File.Exists(p) && !Directory.Exists(p)).ToArray());
        foreach (var g in gone.GroupBy(p => PathUtil.Parent(p) ?? p, StringComparer.OrdinalIgnoreCase))
        {
            ReflectLocal(g.Key, [.. g.Select(PathUtil.DisplayName)], []);
        }
    }

    /// <summary>복사·이동 뒤: 받는 폴더에 생긴 것을 넣고, 이동이면 보낸 폴더에서 뺀다.</summary>
    private async Task ReflectTransferAsync(IReadOnlyList<string> src, string dest, bool move)
    {
        if (PathUtil.IsSftp(dest) || src.Count > MaxReflect)
        {
            if (move)
            {
                await ReflectGoneAsync(src);
            }

            return;
        }

        var arrived = await Task.Run(() => src.Select(s => Stat(Path.Combine(dest, PathUtil.DisplayName(s)))).OfType<FsEntry>().ToArray());
        ReflectLocal(dest, [], arrived);
        if (move)
        {
            await ReflectGoneAsync(src);
        }
    }

    /// <summary>방금 생긴 파일·폴더 하나의 정보. 없거나 못 읽으면 null.</summary>
    private static FsEntry? Stat(string path)
    {
        try
        {
            var fi = new FileInfo(path);
            if (fi.Exists)
            {
                return new FsEntry(fi.Name, FsPath.Local(path), FsKind.File, fi.Length, null, fi.LastWriteTimeUtc, fi.CreationTimeUtc, fi.Attributes, null);
            }

            var di = new DirectoryInfo(path);
            return di.Exists
                ? new FsEntry(di.Name, FsPath.Local(path), FsKind.Folder, 0, null, di.LastWriteTimeUtc, di.CreationTimeUtc, di.Attributes, null)
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            return null;
        }
    }

    /// <summary>앱 쪽 작업(뒤에서 만드는 정리 계획 등)을 작업 창에 올린다. 끝내는 것은 부른 쪽이 <see cref="JobViewModel.Finish"/>로.</summary>
    public JobViewModel AddJob(string title, Mascot.JobKind kind) => StartJob(title, kind);

    /// <summary>끝난 작업을 잠깐 뒤 작업 창에서 내린다.</summary>
    public void RemoveJobLater(JobViewModel job) => _ = RemoveLaterAsync(job);

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

    /// <summary>끝난 작업이 작업 창에 남아 있는 시간 (마스코트 마무리 장면이 끝날 만큼). 실패한 작업은 닫을 때까지 남는다.</summary>
    public static TimeSpan FinishedJobLinger { get; set; } = TimeSpan.FromSeconds(6);

    /// <summary>
    /// 파일을 쓰고 있는 프로그램 이름을 찾는다 (앱이 Windows Restart Manager로 연결한다, 사용자 2026-11-21 "누가 이 파일을 쓰고 있나요?").
    /// 없으면(시험 등) 찾지 않는다. 묻기만 하고 프로그램을 끄지 않는다.
    /// </summary>
    public static Func<IReadOnlyList<string>, string?>? FindLockers { get; set; }

    private void FinishJob(JobViewModel job, FileOpResult r, IReadOnlyList<string>? sources = null)
    {
        job.Complete(r);
        Status = r.Succeeded ? r.Error ?? "" : r.Aborted ? Foldy.Core.Localization.Loc.T("ops.cancelled") : $"{job.Title}: {r.Error}";
        if (job.State != JobState.Failed)
        {
            _ = RemoveLaterAsync(job);
        }

        // 실패했으면 누가 그 파일을 쓰고 있는지 알려 준다 (공간이 모자라 시작하지 않은 것은 빼고)
        if (!r.Aborted && !r.Succeeded && r.Space is null && sources is { Count: > 0 } && FindLockers is { } find)
        {
            _ = ExplainLockAsync(job, sources, find);
        }
    }

    private async Task ExplainLockAsync(JobViewModel job, IReadOnlyList<string> sources, Func<IReadOnlyList<string>, string?> find)
    {
        string? who;
        try
        {
            who = await Task.Run(() => find(sources));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return;
        }

        if (who is null)
        {
            return;
        }

        job.Detail = $"{job.Detail} · {who}";
        Status = $"{job.Title}: {who}";
    }

    // 작업 창은 지금 하는 일만 보여 준다: 끝난 작업은 잠깐 뒤 사라지고, 비면 창도 접힌다
    private async Task RemoveLaterAsync(JobViewModel job)
    {
        await Task.Delay(FinishedJobLinger);
        Jobs.Remove(job);
    }

    /// <summary>
    /// 그 드라이브의 "공간 늘리기"(폴더 분석)를 연다 (UI가 연결한다). 공간이 모자라 멈춘 작업·안내에서 부른다
    /// (사용자 아이디어 2026-11-22: 모자라면 폴더 분석 → 정리로 바로 이어지게).
    /// </summary>
    public Action<string>? FreeUpSpace { get; set; }

    /// <summary>작업 창의 [공간 늘리기].</summary>
    public void FreeUpSpaceFor(JobViewModel job)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (job.Space?.Root is { } root)
        {
            FreeUpSpace?.Invoke(root);
        }
    }

    /// <summary>작업 창의 [다시 시도]: 멈춘 줄을 지우고 같은 작업을 다시 한다 (다시 확인부터).</summary>
    public async Task RetryAsync(JobViewModel job)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (job.Retry is not { } retry || !job.IsFinished)
        {
            return;
        }

        Jobs.Remove(job);
        await retry();
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
