using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Foldy.Git;

namespace Foldy.Presentation.Git;

/// <summary>
/// 초보자를 위한 흐름: 체크박스로 스테이지하는 변경 사항 목록, 탭(변경 사항 / 기록), 안내 상태, 완료 알림.
/// 스레드 소유권: UI 스레드 전용.
/// </summary>
public sealed partial class GitTabViewModel
{
    private bool _rebuildingRows;

    /// <summary>그룹(스테이지됨 / 변경됨 / 새 파일) 머리글과 파일이 섞인 한 줄 목록.</summary>
    public ObservableCollection<GitChangeRow> ChangeRows { get; } = [];

    [ObservableProperty]
    private GitChangeRow? _selectedChange;

    /// <summary>상태를 한 번이라도 읽었다 (빈 상태 안내를 너무 일찍 보이지 않게).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowNoChanges))]
    [NotifyPropertyChangedFor(nameof(ShowNoHistory))]
    private bool _hasLoadedStatus;

    /// <summary>이 폴더가 아직 Git 저장소가 아니다: [여기에 Git 저장소 만들기] 를 보인다.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSetup))]
    [NotifyPropertyChangedFor(nameof(ShowInitOffer))]
    private bool _needsInit;

    /// <summary>"기록" 탭이 열려 있다 (ShowLocalChanges 의 반대; 탭 라디오 버튼용).</summary>
    public bool IsHistoryTab
    {
        get => !ShowLocalChanges;
        set => ShowLocalChanges = !value;
    }

    partial void OnShowLocalChangesChanged(bool value) => OnPropertyChanged(nameof(IsHistoryTab));

    /// <summary>작업이 끝났을 때 잠깐 보이는 알림 ("올리기 완료: 3개 커밋").</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasToast))]
    private string _toastText = "";

    private int _toastVersion;

    public bool HasToast => ToastText.Length > 0;

    /// <summary>git 이 없거나 저장소가 아니면 본문 대신 안내 화면을 보인다.</summary>
    public bool ShowSetup => !IsGitAvailable || NeedsInit;

    public bool ShowGitMissing => !IsGitAvailable;

    public bool ShowInitOffer => IsGitAvailable && NeedsInit;

    public bool ShowNoChanges => HasLoadedStatus && ChangeCount == 0;

    public bool ShowNoHistory => HasLoadedStatus && LoadedCommitCount == 0 && !IsBusy;

    /// <summary>나란히/Inline 토글의 Inline 쪽 (SideBySide 의 반대).</summary>
    public bool InlineDiff
    {
        get => !SideBySide;
        set => SideBySide = !value;
    }

    public string ChangeCountText => ChangeCount == 0 ? Foldy.Core.Localization.Loc.T("pane.git.clean") : Foldy.Core.Localization.Loc.F("pane.git.changes", ChangeCount);

    // ── 커밋 버튼: 눌러도 되는지와 이유 ──

    /// <summary>커밋할 수 없을 때의 이유 (할 수 있으면 빈 문자열).</summary>
    public string CommitDisabledReason
    {
        get
        {
            if (!IsIdle) return Foldy.Core.Localization.Loc.T("git.busy");
            if (Amend) return "";
            if (Staged.Count == 0) return Foldy.Core.Localization.Loc.T("git.noStaged");
            if (string.IsNullOrWhiteSpace(CommitMessage)) return Foldy.Core.Localization.Loc.T("git.needMessage");
            return "";
        }
    }

    public string CommitTooltip => CommitDisabledReason is { Length: > 0 } r
        ? r
        : Amend ? Foldy.Core.Localization.Loc.T("git.amendTip") : Foldy.Core.Localization.Loc.F("git.commitTip", Staged.Count);

    public string CommitAndPushTooltip => CommitDisabledReason is { Length: > 0 } r
        ? r
        : Foldy.Core.Localization.Loc.T("git.pushAfterTip");

    private void RaiseCommitState()
    {
        OnPropertyChanged(nameof(CanCommit));
        OnPropertyChanged(nameof(CommitDisabledReason));
        OnPropertyChanged(nameof(CommitTooltip));
        OnPropertyChanged(nameof(CommitAndPushTooltip));
    }

    partial void OnCommitMessageChanged(string value) => RaiseCommitState();

    partial void OnAmendChanged(bool value) => RaiseCommitState();

    partial void OnChangeCountChanged(int value)
    {
        OnPropertyChanged(nameof(ChangeCountText));
        OnPropertyChanged(nameof(ShowNoChanges));
    }

    partial void OnSelectedChangeChanged(GitChangeRow? value)
    {
        if (_rebuildingRows || value?.Item is not { } item) return;
        PanelLoadingTask = ShowDiffForChangeAsync(item);
    }

    // ── 목록 만들기 ──

    /// <summary>Staged / Unstaged 에서 그룹별 한 줄 목록을 다시 만들고, 고르고 있던 파일을 유지한다.</summary>
    private void RebuildChangeRows()
    {
        var key = SelectedChange?.Item;
        _rebuildingRows = true;
        try
        {
            ChangeRows.Clear();
            var staged = Staged.ToList();
            var changed = Unstaged.Where(i => i.Change != GitChange.Untracked).ToList();
            var fresh = Unstaged.Where(i => i.Change == GitChange.Untracked).ToList();
            AddGroup(Foldy.Core.Localization.Loc.T("git.staged"), staged);
            AddGroup(Foldy.Core.Localization.Loc.T("git.changed"), changed);
            AddGroup(Foldy.Core.Localization.Loc.T("pane.git.untracked"), fresh);
            HasLoadedStatus = true;
        }
        finally
        {
            _rebuildingRows = false;
        }
        RaiseCommitState();
        OnPropertyChanged(nameof(ShowNoChanges));

        GitChangeRow? match = key is null ? null
            : ChangeRows.FirstOrDefault(r => r.Item is { } i && i.Path == key.Path && i.IsStaged == key.IsStaged);
        SelectedChange = null;
        if (match is not null) SelectedChange = match;
        else if (key is not null && IsDiffMode && _diffTarget is { Kind: not GitDiffKind.Commit }) ClosePanel();

        void AddGroup(string title, List<GitChangeItem> items)
        {
            if (items.Count == 0) return;
            ChangeRows.Add(GitChangeRow.Header(title, items.Count));
            foreach (var i in items) ChangeRows.Add(GitChangeRow.File(i));
        }
    }

    // ── 명령 ──

    /// <summary>체크박스: 스테이지됨이면 해제, 아니면 스테이지.</summary>
    public Task ToggleStageAsync(GitChangeItem item) =>
        item.IsStaged ? UnstageCoreAsync([item]) : StageCoreAsync([item]);

    /// <summary>[모두 선택]: 모든 변경을 스테이지.</summary>
    [RelayCommand]
    public Task SelectAllChangesAsync() => StageCoreAsync(Unstaged);

    /// <summary>[선택 해제]: 스테이지를 모두 푼다.</summary>
    [RelayCommand]
    public Task ClearAllChangesAsync() => UnstageCoreAsync(Staged);

    /// <summary>"변경 사항" 탭으로 (고른 커밋은 그대로 둔다).</summary>
    [RelayCommand]
    public void ShowChangesTab() => ShowLocalChanges = true;

    /// <summary>"기록" 탭으로.</summary>
    [RelayCommand]
    public void ShowHistoryTab() => ShowLocalChanges = false;

    /// <summary>[여기에 Git 저장소 만들기 (git init)].</summary>
    [RelayCommand]
    public Task InitRepositoryAsync() => ExecAsync(Foldy.Core.Localization.Loc.T("git.createRepo"), async (_, ct) =>
    {
        var r = await _git.InitAsync(RepoRoot, ct);
        if (r.Success) NeedsInit = false;
        return r;
    }, RefreshScope.All, Foldy.Core.Localization.Loc.T("git.repoCreated"));

    // ── 알림 ──

    /// <summary>알림을 잠깐 보이고 6초 뒤 지운다 (그 사이 새 알림이 오면 그대로 둔다).</summary>
    private void ShowToast(string text)
    {
        ToastText = text;
        var v = ++_toastVersion;
        _ = ClearToastLaterAsync(v);
    }

    private async Task ClearToastLaterAsync(int version)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(6), _cts.Token); }
        catch (OperationCanceledException) { return; }
        if (version == _toastVersion) ToastText = "";
    }

    [RelayCommand]
    public void DismissToast()
    {
        _toastVersion++;
        ToastText = "";
    }
}
