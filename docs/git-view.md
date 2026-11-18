# Fork식 Git 뷰

설계서 "Git 통합"의 Git 뷰 구현. 서비스 계층은 `docs/git-service.md`(Foldy.Git), 여기서는 화면과 호스트 연결을 다룬다.

| 파일 | 역할 |
| --- | --- |
| `src/Foldy.Presentation/Git/GitTabViewModel.cs` | `TabBase`(Kind = `TabKind.Git`, 제목 `Git: <저장소 이름>`). 상태·Stage/Commit·원격·Stash·브랜치·History 전부 |
| `src/Foldy.Presentation/Git/GitCommitFiles.cs` | `ICommitFileSource` / `GitCliCommitFiles`: `git show --name-status` 로 커밋 변경 파일 (IGitService 에 없어 GitCli 를 직접 사용, Foldy.Git 은 수정하지 않음) |
| `src/Foldy.Presentation/Git/GitErrorText.cs` | `GitFailure` → 사용자 문장 |
| `src/Foldy.UI/Git/GitView.xaml(.cs)` | UserControl. `DataContext = GitTabViewModel` |
| `src/Foldy.UI/Git/CommitGraphList.cs` | 직접 그리는 가상화 커밋 그래프 (FastFileList 와 같은 방식) |
| `tests/Foldy.GitView.Tests` | 가짜 `IGitService` 로 ViewModel 시험 + GitView 렌더 스모크 |

## 호스트가 여는 법

```csharp
var git = new GitService();                          // 앱 전체에서 하나를 공유해도 된다
var repo = git.FindRepo(currentFolder);              // null 이면 Git 요소를 숨긴다
if (repo is not null)
{
    var tab = new GitTabViewModel(git, repo.Root);   // 필요하면 3번째 인수로 ICommitFileSource 교체
    pane.Tabs.Add(tab);                              // 같은 탭 줄을 쓴다 (폴더/Markdown 탭과 동일)
    pane.ActiveTab = tab;                            // 활성화 시 OnActivated() 가 전체 새로 고침
}
```

탭 종류별 화면 선택 (Markdown 탭과 같은 자리):

```csharp
TabKind.Git => new Foldy.UI.Git.GitView { DataContext = (GitTabViewModel)tab },
```

- 탭이 활성화될 때 `OnActivated()` 가 `RefreshAsync()` 를 부른다. 호스트가 `OnActivated()` 를 부르지 않는다면 탭을 만든 직후 `await tab.RefreshAsync()` 를 한 번 호출한다.
- 탭을 닫을 때 `Close()` 를 부르면 진행 중인 git 호출이 취소된다.
- 강제 Push 확인: 뷰가 `Confirm` 콜백을 비어 있을 때 `MessageBox`(예/아니오)로 채운다. 호스트가 자기 대화상자를 쓰려면 `tab.Confirm = msg => ...` 를 먼저 지정한다. 콜백이 없으면 `--force-with-lease` 푸시는 실행되지 않는다.
- 뷰가 창 활성화(`Window.Activated`) 때 파일 상태만 가볍게 다시 읽는다. 자동 fetch 스케줄은 호스트 몫.
- 테마 토큰만 쓴다 (`Brush.*`, `Font.*`, `Radius`, `Foldy.CommandBar/InputBorder/ListBorder`). Modern.Controls.xaml 이 앱 리소스에 병합돼 있어야 한다. 그래프 레인 색만 라이트/다크 공용 고정 중간 톤 팔레트.

## ViewModel 요약

- 상태: `BranchName`, `Ahead/Behind`(`AheadBehindText`), `Upstream`, `ChangeCount`, `ProgressText`(`IProgress<GitProgress>` 단계·퍼센트), `ErrorText`, `CanTrustRepo`.
- Local Changes: `Unstaged`/`Staged`(`GitChangeItem`: 경로, 변경 종류, 배지 글자), `CommitMessage`, `Amend`, `CanCommit`. 명령: `StageSelected`(선택 없으면 전체)/`StageAll`/`UnstageSelected`/`UnstageAll`/`Commit`/`CommitAndPush`. 뷰가 `SetSelectedUnstaged/Staged` 로 목록 선택을 알려 준다. 이름 변경 Unstage 는 원본 경로도 함께 되돌린다.
- 원격: `Fetch`(사용자가 눌렀으므로 interactive), `PullMerge`/`PullRebase`, `Push`(업스트림이 없으면 `--set-upstream origin HEAD`), `PushForceWithLease`(확인 콜백 필수).
- Stash/브랜치: `StashPush`, `StashPop`(`SelectedStash` 또는 최신), `LocalBranches`/`RemoteBranches`/`Stashes`, `Checkout(item)`, `CreateBranch`(`NewBranchName`).
- History: `Commits`(검색으로 걸러진 `GitCommitRow` = 커밋 + `GraphRow`), 1000개씩 `LoadMoreHistory`, `MaxLaneCount`, `SearchText`(메시지·작성자·이메일·해시 앞부분, 이미 읽은 커밋 대상), `SelectedCommit` → `CommitFiles`. 검색 중에는 레인이 끊기므로 그래프 선은 그리지 않는다 (`ShowGraph`).
- 쓰기 작업은 한 번에 하나(`IsBusy`), 실패해도(병합 충돌 등) 상태를 다시 읽는다. dubious ownership 은 `CanTrustRepo` 로 [이 저장소 신뢰하기](`AddSafeDirectoryAsync` 후 재시도)를 보인다.

## 한계 / 이후 작업

- Diff 화면, 파일·줄 단위 Stage, 변경 취소(discard), Blame, 태그·리모트 관리, 충돌 해결 UI 없음.
- 커밋 본문(제목 아래 설명)은 읽지 않는다 (`CommitNode` 에 제목만 있음).
- 검색은 이미 읽은 페이지 안에서만 한다. 그래프 끝까지 스크롤하면 계속 이어 읽는다.
- 병합 커밋의 변경 파일은 첫 부모 기준 (`-m --first-parent`).
- 그래프는 열 14개까지만 그린다 (그 이상은 마지막 열에 겹침).
