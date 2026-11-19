using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Foldy.Git;

namespace Foldy.Presentation.Git;

/// <summary>오른쪽 옆 패널이 보여 주는 것.</summary>
public enum GitSidePanelMode : byte { None, Diff, Blame }

/// <summary>
/// Git 탭의 오른쪽 옆 패널: 파일 diff (inline / 나란히)와 blame.
/// git 호출은 모두 비동기 + 취소 가능이고, 새 요청이 오면 이전 요청은 취소한다 (UI 스레드를 막지 않는다).
/// 스레드 소유권: UI 스레드 전용.
/// </summary>
public sealed partial class GitTabViewModel
{
    private const int DiffStep = GitDiffParser.DefaultMaxLines;
    private const int MaxHistoryPagesForBlameJump = 50;

    private CancellationTokenSource? _panelCts;
    private GitDiffTarget? _diffTarget;
    private int _diffLimit = DiffStep;
    private FileDiff? _diff;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSidePanel))]
    [NotifyPropertyChangedFor(nameof(IsDiffMode))]
    [NotifyPropertyChangedFor(nameof(IsBlameMode))]
    [NotifyPropertyChangedFor(nameof(ShowInlineDiff))]
    [NotifyPropertyChangedFor(nameof(ShowSideBySideDiff))]
    [NotifyPropertyChangedFor(nameof(ShowBlameList))]
    private GitSidePanelMode _panelMode;

    /// <summary>패널 머리글의 파일 이름.</summary>
    [ObservableProperty]
    private string _panelTitle = "";

    /// <summary>머리글 둘째 줄 (폴더 · 무엇과 비교하는지).</summary>
    [ObservableProperty]
    private string _panelSubtitle = "";

    /// <summary>"바이너리 파일", 오류 같은 본문 대신 보이는 안내. 비어 있으면 본문을 보인다.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPanelMessage))]
    [NotifyPropertyChangedFor(nameof(ShowInlineDiff))]
    [NotifyPropertyChangedFor(nameof(ShowSideBySideDiff))]
    [NotifyPropertyChangedFor(nameof(ShowBlameList))]
    private string _panelMessage = "";

    [ObservableProperty]
    private bool _panelLoading;

    /// <summary>true 면 나란히 보기, false 면 inline.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowInlineDiff))]
    [NotifyPropertyChangedFor(nameof(ShowSideBySideDiff))]
    [NotifyPropertyChangedFor(nameof(InlineDiff))]
    private bool _sideBySide;

    [ObservableProperty]
    private IReadOnlyList<DiffLine> _diffLines = [];

    [ObservableProperty]
    private IReadOnlyList<SideBySideRow> _sideRows = [];

    /// <summary>"+12 −3" 같은 요약.</summary>
    [ObservableProperty]
    private string _diffSummary = "";

    /// <summary>줄 수가 많아 일부만 보이는 중.</summary>
    [ObservableProperty]
    private bool _diffTruncated;

    [ObservableProperty]
    private string _diffTruncatedText = "";

    [ObservableProperty]
    private IReadOnlyList<GitBlameRow> _blameRows = [];

    /// <summary>커밋의 변경 파일 목록에서 고른 파일 (고르면 diff 를 보인다).</summary>
    [ObservableProperty]
    private GitCommitFile? _selectedCommitFile;

    public bool HasSidePanel => PanelMode != GitSidePanelMode.None;
    public bool IsDiffMode => PanelMode == GitSidePanelMode.Diff;
    public bool IsBlameMode => PanelMode == GitSidePanelMode.Blame;
    public bool HasPanelMessage => PanelMessage.Length > 0;
    public bool ShowInlineDiff => IsDiffMode && !HasPanelMessage && !SideBySide;
    public bool ShowSideBySideDiff => IsDiffMode && !HasPanelMessage && SideBySide;
    public bool ShowBlameList => IsBlameMode && !HasPanelMessage;

    /// <summary>마지막으로 시작한 패널 읽기 작업 (테스트가 기다릴 수 있게).</summary>
    public Task PanelLoadingTask { get; private set; } = Task.CompletedTask;

    partial void OnSideBySideChanged(bool value)
    {
        if (_diff is not null) SideRows = value ? GitDiffParser.BuildSideBySide(_diff.Lines) : [];
    }

    partial void OnSelectedCommitFileChanged(GitCommitFile? value)
    {
        if (value is not null) PanelLoadingTask = ShowDiffForCommitFileAsync(value);
    }

    // ───────────── diff ─────────────

    /// <summary>Local Changes 의 파일 하나의 diff 를 옆 패널에 보인다.</summary>
    public Task ShowDiffForChangeAsync(GitChangeItem item)
    {
        var target = new GitDiffTarget(item.IsStaged ? GitDiffKind.Staged : GitDiffKind.Unstaged, item.Path, item.OrigPath,
            Untracked: item.Change == GitChange.Untracked);
        return PanelLoadingTask = ShowDiffAsync(target);
    }

    /// <summary>선택한 커밋이 그 파일에서 바꾼 내용을 보인다.</summary>
    public Task ShowDiffForCommitFileAsync(GitCommitFile file)
    {
        if (SelectedCommit is not { } row) return Task.CompletedTask;
        var target = new GitDiffTarget(GitDiffKind.Commit, file.Path, file.OrigPath, row.Node.Hash);
        return PanelLoadingTask = ShowDiffAsync(target);
    }

    public async Task ShowDiffAsync(GitDiffTarget target)
    {
        _diffTarget = target;
        _diffLimit = DiffStep;
        PanelMode = GitSidePanelMode.Diff;
        SetHeader(target.Path, target.Kind switch
        {
            GitDiffKind.Staged => "Staged 변경 (HEAD와 비교)",
            GitDiffKind.Commit => $"커밋 {ShortHash(target.Hash)}의 변경",
            _ => target.Untracked ? "새 파일 (아직 추적 안 함)" : "Unstaged 변경 (Stage된 내용과 비교)",
        });
        BlameRows = [];
        if (target.Path.EndsWith('/'))
        {
            ApplyDiff(FileDiff.Empty);
            PanelMessage = "폴더는 비교할 수 없습니다. 폴더 안의 파일을 선택하세요.";
            return;
        }
        await LoadDiffAsync();
    }

    private async Task LoadDiffAsync()
    {
        var target = _diffTarget;
        if (target is null) return;
        var cts = NewPanelCts();
        PanelLoading = true;
        try
        {
            var r = await _git.GetDiffAsync(RepoRoot, target, _diffLimit, cts.Token);
            if (cts.IsCancellationRequested) return;
            if (!r.Success || r.Value is null)
            {
                ApplyDiff(FileDiff.Empty);
                PanelMessage = FailureText(r.Result, "변경 내용을 읽지 못했습니다.");
                return;
            }
            ApplyDiff(r.Value);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (ReferenceEquals(_panelCts, cts)) PanelLoading = false;
        }
    }

    private void ApplyDiff(FileDiff diff)
    {
        _diff = diff;
        DiffLines = diff.Lines;
        SideRows = SideBySide ? GitDiffParser.BuildSideBySide(diff.Lines) : [];
        DiffTruncated = diff.Truncated;
        DiffTruncatedText = diff.Truncated ? $"{diff.Lines.Count:N0} / {diff.TotalLines:N0}줄만 표시 중" : "";
        DiffSummary = diff.IsBinary || diff.IsEmpty ? "" : $"+{diff.AddedCount:N0}  −{diff.RemovedCount:N0}";
        PanelMessage = diff.IsBinary
            ? "바이너리 파일"
            : diff.IsEmpty ? "표시할 변경 내용이 없습니다 (파일 모드나 줄 끝만 바뀌었을 수 있습니다)." : "";
    }

    /// <summary>잘린 diff 를 20,000줄 더 보인다.</summary>
    [RelayCommand]
    public Task ShowMoreDiffAsync()
    {
        _diffLimit += DiffStep;
        return PanelLoadingTask = LoadDiffAsync();
    }

    // ───────────── blame ─────────────

    /// <summary>Local Changes 의 파일을 blame (작업 폴더 기준).</summary>
    public Task ShowBlameForChangeAsync(GitChangeItem item) => PanelLoadingTask = ShowBlameAsync(item.Path, null);

    /// <summary>커밋의 변경 파일을 그 커밋 시점 기준으로 blame.</summary>
    public Task ShowBlameForCommitFileAsync(GitCommitFile file) =>
        PanelLoadingTask = ShowBlameAsync(file.Path, SelectedCommit?.Node.Hash);

    /// <summary>
    /// 파일 하나의 blame 을 옆 패널에 연다. path 는 저장소 루트 기준 상대 경로('/').
    /// revision 이 null 이면 작업 폴더(커밋하지 않은 변경 포함) 기준.
    /// </summary>
    public async Task ShowBlameAsync(string path, string? revision = null)
    {
        PanelMode = GitSidePanelMode.Blame;
        SetHeader(path, revision is null ? "Blame (작업 폴더 기준)" : $"Blame (커밋 {ShortHash(revision)} 기준)");
        BlameRows = [];
        PanelMessage = "";
        DiffTruncated = false;
        DiffSummary = "";
        var cts = NewPanelCts();
        PanelLoading = true;
        try
        {
            var r = await _git.GetBlameAsync(RepoRoot, path, revision, cts.Token);
            if (cts.IsCancellationRequested) return;
            if (!r.Success || r.Value is null)
            {
                PanelMessage = FailureText(r.Result, "blame을 읽지 못했습니다.");
                return;
            }
            var now = DateTimeOffset.Now;
            BlameRows = [.. r.Value.Select(l => GitBlameRow.From(l, now))];
            if (BlameRows.Count == 0) PanelMessage = "빈 파일입니다.";
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (ReferenceEquals(_panelCts, cts)) PanelLoading = false;
        }
    }

    /// <summary>diff 머리글의 [Blame]: 지금 보는 파일을 blame 으로 바꿔 연다.</summary>
    [RelayCommand]
    public Task ShowBlameOfDiffAsync()
    {
        if (_diffTarget is not { } t) return Task.CompletedTask;
        return PanelLoadingTask = ShowBlameAsync(t.Path, t.Kind == GitDiffKind.Commit ? t.Hash : null);
    }

    /// <summary>blame 머리글의 [Diff]: 마지막으로 보던 diff 로 돌아간다.</summary>
    [RelayCommand]
    public Task ShowDiffOfBlameAsync()
    {
        if (_diffTarget is not { } t) return Task.CompletedTask;
        return PanelLoadingTask = ShowDiffAsync(t);
    }

    /// <summary>blame 에서 해시를 누르면 History 에서 그 커밋을 고른다 (아직 안 읽은 페이지는 더 읽는다).</summary>
    [RelayCommand]
    public async Task SelectCommitByHashAsync(string? hash)
    {
        if (string.IsNullOrEmpty(hash) || hash.All(c => c == '0')) return;
        var row = Find(hash);
        for (var i = 0; row is null && HasMoreHistory && i < MaxHistoryPagesForBlameJump; i++)
        {
            var before = _allCommits.Count;
            await LoadHistoryAsync(reset: false);
            if (_allCommits.Count == before) break;
            row = Find(hash);
        }
        if (row is null)
        {
            ProgressText = "그 커밋은 불러온 이력에 없습니다.";
            return;
        }
        if (!Commits.Contains(row)) SearchText = "";   // 검색 필터에 가려져 있으면 필터를 푼다
        SelectedCommit = row;

        GitCommitRow? Find(string h) => _allCommits.FirstOrDefault(r => string.Equals(r.Node.Hash, h, StringComparison.OrdinalIgnoreCase));
    }

    // ───────────── 공통 ─────────────

    [RelayCommand]
    public void ClosePanel()
    {
        _panelCts?.Cancel();
        PanelLoading = false;
        PanelMode = GitSidePanelMode.None;
        _diff = null;
        DiffLines = [];
        SideRows = [];
        BlameRows = [];
        PanelMessage = "";
        SelectedCommitFile = null;
    }

    private CancellationTokenSource NewPanelCts()
    {
        _panelCts?.Cancel();
        _panelCts?.Dispose();
        return _panelCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
    }

    private void SetHeader(string path, string subtitle)
    {
        var p = path.TrimEnd('/');
        var i = p.LastIndexOf('/');
        PanelTitle = p[(i + 1)..] + (path.EndsWith('/') ? "/" : "");
        PanelSubtitle = (i > 0 ? p[..i] + " · " : "") + subtitle;
    }

    private static string ShortHash(string? h) => h is { Length: > 7 } ? h[..7] : h ?? "";

    private static string FailureText(GitResult r, string fallback)
    {
        var t = GitErrorText.Describe(r);
        return t.Length > 0 ? t : fallback;
    }
}
