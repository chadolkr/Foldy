using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Foldy.Git;
using Foldy.Presentation.Tabs;

namespace Foldy.Presentation.Git;

/// <summary>
/// Fork식 Git 뷰 탭 (설계서 "Git 통합"). Local Changes(Stage/Commit), 툴바(Fetch/Pull/Push/Stash/Branch),
/// History(커밋 그래프 + 검색 + 변경 파일)를 한 탭에서 다룬다. 화면은 Foldy.UI 의 GitView.
/// 모든 git 호출은 async 이며 UI 스레드를 막지 않는다. 상태 갱신은 await 이후(UI 컨텍스트)에 한다.
/// 스레드 소유권: UI 스레드 전용.
/// </summary>
public sealed partial class GitTabViewModel : TabBase
{
    /// <summary>한 번에 읽는 커밋 수.</summary>
    public const int PageSize = 1000;

    private enum RefreshScope { None, Status, All }

    private readonly IGitService _git;
    private readonly ICommitFileSource _commitFiles;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _historyGate = new(1, 1);
    private readonly List<GitCommitRow> _allCommits = [];
    private CancellationTokenSource? _detailCts;
    private GraphLaneCalculator _lanes = new();
    private bool _opActive;
    private IReadOnlyList<GitChangeItem> _selectedUnstaged = [];
    private IReadOnlyList<GitChangeItem> _selectedStaged = [];

    public GitTabViewModel(IGitService git, string repoRoot, ICommitFileSource? commitFiles = null)
    {
        _git = git;
        RepoRoot = repoRoot;
        _commitFiles = commitFiles
            ?? (git is GitService gs ? new GitCliCommitFiles(gs.Cli) : new NoCommitFiles());
        var name = System.IO.Path.GetFileName(repoRoot.TrimEnd('\\', '/'));
        Title = "Git: " + (name.Length > 0 ? name : repoRoot);
    }

    public override TabKind Kind => TabKind.Git;

    public override string? Location => RepoRoot;

    public string RepoRoot { get; }

    /// <summary>강제 푸시 같은 위험한 동작 전에 사용자에게 묻는 콜백 (메시지 → 허락 여부). 없으면 실행하지 않는다.</summary>
    public Func<string, Task<bool>>? Confirm { get; set; }

    // ───────────── 상태 ─────────────

    [ObservableProperty]
    private string? _branchName;

    [ObservableProperty]
    private bool _isDetached;

    [ObservableProperty]
    private string? _upstream;

    [ObservableProperty]
    private int _ahead;

    [ObservableProperty]
    private int _behind;

    /// <summary>진행 단계 문장 ("Fetch: Receiving objects 42%").</summary>
    [ObservableProperty]
    private string _progressText = "";

    /// <summary>실패 메시지 (여러 줄 가능). 비어 있으면 숨긴다.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string _errorText = "";

    /// <summary>dubious ownership 이면 [이 저장소 신뢰하기] 버튼을 보인다.</summary>
    [ObservableProperty]
    private bool _canTrustRepo;

    [ObservableProperty]
    private string? _safeDirectoryCandidate;

    public bool HasError => ErrorText.Length > 0;

    public bool HasUpstream => Upstream is not null;

    public bool IsIdle => !IsBusy;

    public bool IsGitAvailable => _git.IsGitAvailable;

    /// <summary>"↑2 ↓1" (없으면 빈 문자열).</summary>
    public string AheadBehindText => (Ahead > 0 ? $"↑{Ahead}" : "") + (Ahead > 0 && Behind > 0 ? " " : "") + (Behind > 0 ? $"↓{Behind}" : "");

    // ───────────── Local Changes ─────────────

    public ObservableCollection<GitChangeItem> Unstaged { get; } = [];

    public ObservableCollection<GitChangeItem> Staged { get; } = [];

    /// <summary>사이드바 Local Changes 옆 숫자 (무시 파일 제외, 파일 기준).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasChanges))]
    private int _changeCount;

    public bool HasChanges => ChangeCount > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCommit))]
    private string _commitMessage = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCommit))]
    private bool _amend;

    /// <summary>true 면 아래 칸에 Local Changes, false 면 선택한 커밋의 변경 파일을 보인다.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowCommitDetails))]
    private bool _showLocalChanges = true;

    public bool ShowCommitDetails => !ShowLocalChanges;

    /// <summary>Amend 이거나 (Stage된 파일이 있고 메시지가 있을 때).</summary>
    public bool CanCommit => IsIdle && (Amend || (Staged.Count > 0 && !string.IsNullOrWhiteSpace(CommitMessage)));

    /// <summary>뷰가 Unstaged 목록의 선택을 알려 준다 (Stage 가 "선택 또는 전체" 로 동작하게).</summary>
    public void SetSelectedUnstaged(IEnumerable<GitChangeItem> items) => _selectedUnstaged = [.. items];

    public void SetSelectedStaged(IEnumerable<GitChangeItem> items) => _selectedStaged = [.. items];

    // ───────────── 브랜치 / Stash ─────────────

    public ObservableCollection<GitBranchItem> LocalBranches { get; } = [];

    public ObservableCollection<GitBranchItem> RemoteBranches { get; } = [];

    public ObservableCollection<GitStashItem> Stashes { get; } = [];

    [ObservableProperty]
    private GitStashItem? _selectedStash;

    [ObservableProperty]
    private string _newBranchName = "";

    // ───────────── History ─────────────

    /// <summary>화면에 보이는 커밋 (검색어가 있으면 걸러진 목록). 갱신할 때마다 새 목록으로 교체한다.</summary>
    [ObservableProperty]
    private IReadOnlyList<GitCommitRow> _commits = [];

    /// <summary>지금까지 읽은 커밋 수 (필터와 무관).</summary>
    public int LoadedCommitCount => _allCommits.Count;

    /// <summary>마지막 페이지가 꽉 찼다 = 더 읽을 수 있다.</summary>
    [ObservableProperty]
    private bool _hasMoreHistory;

    /// <summary>그래프에 필요한 가장 많은 열 수 (그래프 영역 너비 계산용).</summary>
    [ObservableProperty]
    private int _maxLaneCount = 1;

    /// <summary>검색어 (메시지·작성자·해시). 필터가 켜지면 레인이 끊겨 그래프는 숨긴다.</summary>
    [ObservableProperty]
    private string _searchText = "";

    public bool ShowGraph => string.IsNullOrWhiteSpace(SearchText);

    [ObservableProperty]
    private GitCommitRow? _selectedCommit;

    public ObservableCollection<GitCommitFile> CommitFiles { get; } = [];

    /// <summary>마지막으로 시작한 변경 파일 읽기 작업 (테스트가 기다릴 수 있게).</summary>
    public Task CommitFilesLoading { get; private set; } = Task.CompletedTask;

    partial void OnSearchTextChanged(string value)
    {
        ApplyFilter();
        OnPropertyChanged(nameof(ShowGraph));
    }

    partial void OnSelectedCommitChanged(GitCommitRow? value)
    {
        if (value is not null) ShowLocalChanges = false;
        CommitFilesLoading = LoadCommitFilesAsync(value);
    }

    partial void OnUpstreamChanged(string? value) => OnPropertyChanged(nameof(HasUpstream));

    partial void OnAheadChanged(int value) => OnPropertyChanged(nameof(AheadBehindText));

    partial void OnBehindChanged(int value) => OnPropertyChanged(nameof(AheadBehindText));

    // ───────────── 갱신 ─────────────

    /// <summary>상태·브랜치·Stash·History 를 모두 다시 읽는다. 호스트가 탭을 연 뒤와 창 포커스 때 부른다.</summary>
    [RelayCommand]
    public async Task RefreshAsync()
    {
        ErrorText = "";
        CanTrustRepo = false;
        await RefreshCoreAsync(RefreshScope.All);
    }

    /// <summary>파일 상태만 다시 읽는다 (창 포커스 복귀 때 가볍게). 쓰기 작업 중에는 건너뛴다.</summary>
    public async Task RefreshStatusOnlyAsync()
    {
        if (_opActive) return;
        await RefreshCoreAsync(RefreshScope.Status);
    }

    private async Task RefreshCoreAsync(RefreshScope scope)
    {
        if (scope == RefreshScope.None) return;
        var ct = _cts.Token;
        try
        {
            var status = await RefreshStatusAsync(ct);
            if (scope == RefreshScope.All && status)
            {
                await Task.WhenAll(RefreshBranchesAsync(ct), RefreshStashesAsync(ct));
                await LoadHistoryAsync(reset: true);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task<bool> RefreshStatusAsync(CancellationToken ct)
    {
        var st = await _git.GetStatusAsync(RepoRoot, false, ct);
        if (!st.Success || st.Value is null)
        {
            Report(st.Result);
            return false;
        }
        ApplyStatus(st.Value);
        return true;
    }

    private void ApplyStatus(RepoStatus s)
    {
        BranchName = s.IsDetached ? "(detached " + (s.HeadOid is { Length: > 7 } o ? o[..7] : s.HeadOid) + ")" : s.BranchName;
        IsDetached = s.IsDetached;
        Upstream = s.Upstream;
        Ahead = s.Ahead;
        Behind = s.Behind;
        ChangeCount = s.ChangeCount;
        NeedsInit = false;

        Unstaged.Clear();
        Staged.Clear();
        foreach (var f in s.Files)
        {
            if (f.IsIgnored) continue;
            if (f.IsConflicted)
            {
                Unstaged.Add(new GitChangeItem(f.Path, f.OrigPath, GitChange.Conflicted, false));
                continue;
            }
            if (f.Staged != GitChange.None) Staged.Add(new GitChangeItem(f.Path, f.OrigPath, f.Staged, true));
            if (f.Unstaged != GitChange.None) Unstaged.Add(new GitChangeItem(f.Path, f.OrigPath, f.Unstaged, false));
        }
        _selectedUnstaged = [];
        _selectedStaged = [];
        RebuildChangeRows();
    }

    private async Task RefreshBranchesAsync(CancellationToken ct)
    {
        var r = await _git.GetBranchesAsync(RepoRoot, ct);
        if (!r.Success || r.Value is null)
        {
            if (!HasError) Report(r.Result);
            return;
        }
        LocalBranches.Clear();
        RemoteBranches.Clear();
        foreach (var b in r.Value)
        {
            var item = new GitBranchItem(b.Name, b.IsCurrent, b.IsRemote, b.Ahead, b.Behind);
            (b.IsRemote ? RemoteBranches : LocalBranches).Add(item);
        }
    }

    private async Task RefreshStashesAsync(CancellationToken ct)
    {
        var r = await _git.GetStashesAsync(RepoRoot, ct);
        if (!r.Success || r.Value is null)
        {
            if (!HasError) Report(r.Result);
            return;
        }
        Stashes.Clear();
        foreach (var s in r.Value) Stashes.Add(new GitStashItem(s.Name, s.Message, s.Date));
        SelectedStash = null;
    }

    // ───────────── History (페이징 + 레인) ─────────────

    /// <summary>
    /// 커밋을 PageSize 개씩 읽는다. reset 이면 처음부터 (레인 계산도 새로), 아니면 이어서.
    /// 레인 계산기는 상태를 유지하므로 페이지 경계에서도 선이 끊기지 않는다.
    /// </summary>
    public async Task LoadHistoryAsync(bool reset)
    {
        // 스크롤 끝에서 이어 읽기 요청이 겹쳐도 한 번에 한 페이지만 읽는다
        if (!reset && _historyGate.CurrentCount == 0) return;
        var entered = false;
        try
        {
            await _historyGate.WaitAsync(_cts.Token);
            entered = true;
            if (reset)
            {
                _allCommits.Clear();
                _lanes = new GraphLaneCalculator();
                MaxLaneCount = 1;
            }
            else if (!HasMoreHistory)
            {
                return;
            }

            var count = 0;
            var max = MaxLaneCount;
            var skip = _allCommits.Count;
            await foreach (var c in _git.GetLogAsync(RepoRoot, new LogFilter { Skip = skip, Count = PageSize }, _cts.Token))
            {
                var row = _lanes.Next(c.Hash, c.Parents);
                _allCommits.Add(new GitCommitRow(c, row));
                max = Math.Max(max, Math.Max(row.LaneCount, row.Column + 1));
                count++;
            }
            MaxLaneCount = max;
            HasMoreHistory = count >= PageSize;
            OnPropertyChanged(nameof(LoadedCommitCount));
            OnPropertyChanged(nameof(ShowNoHistory));
            ApplyFilter();
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (entered) _historyGate.Release();
        }
    }

    [RelayCommand]
    public Task LoadMoreHistoryAsync() => LoadHistoryAsync(reset: false);

    private void ApplyFilter()
    {
        var q = SearchText.Trim();
        if (q.Length == 0)
        {
            Commits = _allCommits.ToArray();
            return;
        }
        Commits = _allCommits.Where(r => Matches(r.Node, q)).ToArray();
    }

    private static bool Matches(CommitNode c, string q) =>
        c.Subject.Contains(q, StringComparison.OrdinalIgnoreCase)
        || c.AuthorName.Contains(q, StringComparison.OrdinalIgnoreCase)
        || c.AuthorEmail.Contains(q, StringComparison.OrdinalIgnoreCase)
        || c.Hash.StartsWith(q, StringComparison.OrdinalIgnoreCase);

    /// <summary>선택한 커밋의 변경 파일을 읽는다 (새 선택이 오면 이전 읽기는 취소).</summary>
    public async Task LoadCommitFilesAsync(GitCommitRow? row)
    {
        _detailCts?.Cancel();
        _detailCts?.Dispose();
        var cts = _detailCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        SelectedCommitFile = null;
        CommitFiles.Clear();
        if (row is null) return;
        try
        {
            var r = await _commitFiles.GetChangedFilesAsync(RepoRoot, row.Node.Hash, cts.Token);
            if (cts.IsCancellationRequested) return;
            if (!r.Success || r.Value is null)
            {
                Report(r.Result);
                return;
            }
            foreach (var f in r.Value) CommitFiles.Add(f);
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>사이드바 Local Changes 를 눌렀을 때: 커밋 선택을 풀고 아래 칸을 Local Changes 로.</summary>
    [RelayCommand]
    public void SelectLocalChanges()
    {
        SelectedCommit = null;
        ShowLocalChanges = true;
    }

    // ───────────── Stage / Commit ─────────────

    /// <summary>선택한 Unstaged 파일을 Stage. 선택이 없으면 전체.</summary>
    [RelayCommand]
    public Task StageSelectedAsync() => StageCoreAsync(_selectedUnstaged.Count > 0 ? _selectedUnstaged : Unstaged);

    [RelayCommand]
    public Task StageAllAsync() => StageCoreAsync(Unstaged);

    /// <summary>선택한 Staged 파일을 Unstage. 선택이 없으면 전체.</summary>
    [RelayCommand]
    public Task UnstageSelectedAsync() => UnstageCoreAsync(_selectedStaged.Count > 0 ? _selectedStaged : Staged);

    [RelayCommand]
    public Task UnstageAllAsync() => UnstageCoreAsync(Staged);

    private async Task StageCoreAsync(IEnumerable<GitChangeItem> items)
    {
        var paths = items.Select(i => i.Path).Distinct().ToList();
        if (paths.Count == 0) return;
        await ExecAsync(Foldy.Core.Localization.Loc.T("git.op.stage"), (_, ct) => _git.StageAsync(RepoRoot, paths, ct), RefreshScope.Status, "");
    }

    private async Task UnstageCoreAsync(IEnumerable<GitChangeItem> items)
    {
        // 이름 변경은 새 경로와 원본 경로를 함께 되돌려야 인덱스에서 완전히 빠진다
        var paths = items.SelectMany(i => i.OrigPath is null ? [i.Path] : new[] { i.Path, i.OrigPath }).Distinct().ToList();
        if (paths.Count == 0) return;
        await ExecAsync(Foldy.Core.Localization.Loc.T("git.op.unstage"), (_, ct) => _git.UnstageAsync(RepoRoot, paths, ct), RefreshScope.Status, "");
    }

    [RelayCommand]
    public async Task CommitAsync()
    {
        if (!CanCommit) return;
        var (msg, amend) = (CommitMessage, Amend);
        if (await ExecAsync(Foldy.Core.Localization.Loc.T("git.op.commit"), (_, ct) => _git.CommitAsync(RepoRoot, msg, amend, ct), RefreshScope.All, Foldy.Core.Localization.Loc.T("git.op.commitDone")))
        {
            CommitMessage = "";
            Amend = false;
        }
    }

    /// <summary>커밋하고 이어서 Push. 업스트림이 없으면 origin 에 새로 만든다.</summary>
    [RelayCommand]
    public async Task CommitAndPushAsync()
    {
        if (!CanCommit) return;
        var (msg, amend, setUpstream) = (CommitMessage, Amend, !HasUpstream);
        var ok = await ExecAsync(Foldy.Core.Localization.Loc.T("git.op.commitPush"), async (p, ct) =>
        {
            var c = await _git.CommitAsync(RepoRoot, msg, amend, ct);
            if (!c.Success) return c;
            // 커밋은 이미 만들어졌으므로 Push 가 실패해도 메시지 칸은 비운다
            CommitMessage = "";
            Amend = false;
            // amend 한 커밋은 이미 올라간 경우가 많아 일반 push 가 거절될 수 있다 (강제 push 는 별도 확인 경로)
            return await _git.PushAsync(RepoRoot, setUpstream, false, p, ct);
        });
        if (ok) { ProgressText = Foldy.Core.Localization.Loc.T("git.op.commitPushDone"); ShowToast(ProgressText); }
    }

    // ───────────── 원격 ─────────────

    [RelayCommand]
    public Task FetchAsync() => ExecAsync(Foldy.Core.Localization.Loc.T("git.op.fetch"), (p, ct) => _git.FetchAsync(RepoRoot, p, interactive: true, ct), RefreshScope.All, Foldy.Core.Localization.Loc.T("git.op.fetchDone"));

    [RelayCommand]
    public Task PullMergeAsync() => PullCoreAsync(Foldy.Core.Localization.Loc.T("sw.git.pull"), PullMode.Merge);

    [RelayCommand]
    public Task PullRebaseAsync() => PullCoreAsync(Foldy.Core.Localization.Loc.T("git.op.pullRebase"), PullMode.Rebase);

    private Task<bool> PullCoreAsync(string label, PullMode mode)
    {
        var n = Behind;
        return ExecAsync(label, (p, ct) => _git.PullAsync(RepoRoot, mode, p, ct), RefreshScope.All,
            n > 0 ? Foldy.Core.Localization.Loc.F("git.op.pulledN", n) : Foldy.Core.Localization.Loc.T("git.op.pullUpToDate"));
    }

    public Task PullAsync(PullMode mode) => mode == PullMode.Rebase ? PullRebaseAsync() : PullMergeAsync();

    [RelayCommand]
    public Task PushAsync()
    {
        var setUpstream = !HasUpstream;
        var n = Ahead;
        return ExecAsync(Foldy.Core.Localization.Loc.T("sw.git.push"), (p, ct) => _git.PushAsync(RepoRoot, setUpstream, false, p, ct), RefreshScope.All,
            n > 0 ? Foldy.Core.Localization.Loc.F("git.op.pushedN", n) : Foldy.Core.Localization.Loc.T("git.op.pushDone"));
    }

    /// <summary>--force-with-lease 푸시. Confirm 콜백이 true 를 돌려줄 때만 실행한다 (콜백이 없으면 실행하지 않음).</summary>
    [RelayCommand]
    public async Task PushForceWithLeaseAsync()
    {
        if (Confirm is null || !await Confirm(
                Foldy.Core.Localization.Loc.T("git.op.forceAsk")))
        {
            ProgressText = Foldy.Core.Localization.Loc.T("git.op.forceCancelled");
            return;
        }
        var setUpstream = !HasUpstream;
        await ExecAsync(Foldy.Core.Localization.Loc.T("git.op.force"), (p, ct) => _git.PushAsync(RepoRoot, setUpstream, true, p, ct));
    }

    // ───────────── Stash / 브랜치 ─────────────

    [RelayCommand]
    public Task StashPushAsync() => ExecAsync(Foldy.Core.Localization.Loc.T("git.op.stash"), (_, ct) => _git.StashPushAsync(RepoRoot, null, true, ct), RefreshScope.All, Foldy.Core.Localization.Loc.T("git.op.stashDone"));

    /// <summary>선택한 Stash 를 적용하고 지운다. 선택이 없으면 가장 최근 것.</summary>
    [RelayCommand]
    public Task StashPopAsync()
    {
        var name = SelectedStash?.Name;
        return ExecAsync(Foldy.Core.Localization.Loc.T("git.op.stashPop"), (_, ct) => _git.StashPopAsync(RepoRoot, name, ct));
    }

    [RelayCommand]
    public async Task CheckoutAsync(GitBranchItem? branch)
    {
        if (branch is null || (branch.IsCurrent && !branch.IsRemote)) return;
        await ExecAsync(Foldy.Core.Localization.Loc.T("git.op.switch"), (_, ct) => _git.CheckoutAsync(RepoRoot, branch.Name, ct));
    }

    /// <summary>NewBranchName 으로 새 브랜치를 만들어 이동한다.</summary>
    [RelayCommand]
    public async Task CreateBranchAsync()
    {
        var name = NewBranchName.Trim();
        if (name.Length == 0) return;
        if (name.Any(char.IsWhiteSpace))
        {
            ErrorText = Foldy.Core.Localization.Loc.T("git.op.noSpaces");
            return;
        }
        if (await ExecAsync(Foldy.Core.Localization.Loc.T("git.op.newBranch"), (_, ct) => _git.CreateBranchAsync(RepoRoot, name, null, true, ct)))
            NewBranchName = "";
    }

    /// <summary>dubious ownership 안내의 [이 저장소 신뢰하기].</summary>
    [RelayCommand]
    public async Task TrustRepositoryAsync()
    {
        var path = SafeDirectoryCandidate ?? RepoRoot;
        if (await ExecAsync(Foldy.Core.Localization.Loc.T("git.op.trust"), (_, ct) => _git.AddSafeDirectoryAsync(path, ct), RefreshScope.None))
        {
            CanTrustRepo = false;
            await RefreshAsync();
        }
    }

    // ───────────── 공통 실행기 ─────────────

    /// <summary>
    /// 쓰기 작업 하나를 실행한다. 한 번에 하나만 (이미 실행 중이면 false). 진행은 IProgress 로 ProgressText 에,
    /// 실패는 ErrorText 에 담는다. 실패해도 (병합 충돌 등) 상태를 다시 읽는다.
    /// </summary>
    private async Task<bool> ExecAsync(string label, Func<IProgress<GitProgress>, CancellationToken, Task<GitResult>> op,
        RefreshScope refresh = RefreshScope.All, string? doneText = null)
    {
        if (_opActive) return false;
        _opActive = true;
        SetBusy(true);
        ErrorText = "";
        CanTrustRepo = false;
        ProgressText = label + "…";
        var ok = false;
        try
        {
            // Progress<T> 는 만든 곳(UI)의 컨텍스트로 알림을 돌려 준다
            var progress = new Progress<GitProgress>(p =>
                ProgressText = p.Percent >= 0 ? $"{label}: {p.Stage} {p.Percent}%" : $"{label}: {p.Stage}");
            var r = await op(progress, _cts.Token);
            ok = r.Success;
            if (ok)
            {
                // doneText "" = 알림 없이 조용히 (스테이지 같은 잦은 동작)
                ProgressText = doneText is { Length: > 0 } ? doneText : Foldy.Core.Localization.Loc.F("git.op.done", label);
                if (doneText != "") ShowToast(ProgressText);
            }
            else if (r.Failure == GitFailure.Cancelled) ProgressText = Foldy.Core.Localization.Loc.F("git.op.cancelled", label);
            else
            {
                ProgressText = Foldy.Core.Localization.Loc.F("git.op.failed", label);
                ToastText = "";
                Report(r);
            }
        }
        catch (OperationCanceledException)
        {
            ProgressText = Foldy.Core.Localization.Loc.F("git.op.cancelled", label);
        }
        finally
        {
            _opActive = false;
            SetBusy(false);
        }
        await RefreshCoreAsync(refresh);
        return ok;
    }

    private void SetBusy(bool busy)
    {
        IsBusy = busy;
        OnPropertyChanged(nameof(IsIdle));
        RaiseCommitState();
        OnPropertyChanged(nameof(ShowNoHistory));
    }

    /// <summary>실패 결과를 문장으로 바꿔 ErrorText 에 올리고, dubious ownership 이면 신뢰 버튼을 켠다.</summary>
    private void Report(GitResult r)
    {
        ErrorText = GitErrorText.Describe(r);
        if (r.Failure == GitFailure.NotARepository) NeedsInit = true;
        if (r.Failure == GitFailure.DubiousOwnership)
        {
            CanTrustRepo = true;
            SafeDirectoryCandidate = r.SafeDirectoryCandidate;
        }
    }

    public override void OnActivated() => _ = RefreshAsync();

    public override void Close()
    {
        _cts.Cancel();
        _detailCts?.Cancel();
    }
}
