# Foldy.Git 서비스 API

`src/Foldy.Git` (net10.0, WPF 의존 없음, Foldy.Core만 참조). 설계서 "Git 통합"과 5.7절의 구현이다.
읽기는 `git` 출력 파서, 쓰기는 `git.exe` CLI. UI 연결은 별도 작업.

## 구성

| 타입 | 역할 |
| --- | --- |
| `GitRepoLocator` | 위로 올라가며 `.git` 탐색. `.git`이 파일이면 `gitdir:` 추적. 폴더별 캐시(없음 포함), `Invalidate()`로 무효화. 네트워크 경로(UNC·매핑 드라이브)는 `AllowNetwork=true`일 때만 탐색 (기본 꺼짐, 꺼져 있으면 `null`) |
| `GitCli` | git.exe 탐색(PATH → Program Files\Git → LocalAppData\Programs\Git)과 실행. `LC_ALL=C`, `-c core.quotepath=false -c i18n.logOutputEncoding=UTF-8`, UTF-8 입출력, `GIT_EDITOR=true`, 기본 `GIT_TERMINAL_PROMPT=0`. 취소 시 프로세스 트리 종료. 쓰기는 저장소별 `SemaphoreSlim`으로 직렬화 |
| `ProgressParser` | `--progress` stderr 줄 → `GitProgress(Stage, Percent, Raw)` |
| `GitStatusParser` | `status --porcelain=v2 -z --branch` → `RepoStatus` |
| `GitBadgeMap` | `RepoStatus` → 전체 경로별 `GitFileStatus` 배지. 폴더로 변경 전파, 미추적/무시 폴더 하위는 상속 |
| `GitLogParser` | `log --topo-order -z --format=...` → `CommitNode` (부모, 작성자, 시각, ref 장식) |
| `GraphLaneCalculator` | 열린 부모 해시 → 열 번호 배열로 그래프 레인 계산. 순수 함수적, 상태를 유지해 페이지 단위로 이어서 호출 가능 |
| `IGitService` / `GitService` | 단일 진입점 |

## IGitService

모든 메서드는 예외 대신 `GitResult`(`Success`, `Failure`, `ExitCode`, `Output`, `Error`)를 돌려준다.
`repo`는 워킹트리 루트 경로.

```csharp
bool IsGitAvailable;                     // false면 쓰기 버튼에 Git for Windows 설치 안내
GitRepoInfo? FindRepo(string folder);    // Root, GitDir, IsLinkedGitDir

// 읽기 (백그라운드, --no-optional-locks)
Task<GitResult<RepoStatus>> GetStatusAsync(repo, includeIgnored = false, ct);
IAsyncEnumerable<CommitNode> GetLogAsync(repo, LogFilter, ct);          // Skip/Count 페이징, Author/MessageContains/Path/Revision
Task<GitResult<IReadOnlyList<GitBranch>>> GetBranchesAsync(repo, ct);
Task<GitResult<IReadOnlyList<GitStash>>> GetStashesAsync(repo, ct);

// 쓰기 (저장소당 한 번에 하나)
StageAsync / UnstageAsync(repo, paths, ct);
CommitAsync(repo, message, amend = false, ct);                          // amend + 빈 메시지 = --no-edit
FetchAsync(repo, IProgress<GitProgress>?, interactive = false, ct);     // 자동 fetch는 interactive=false (프롬프트 없음)
PullAsync(repo, PullMode.Merge | Rebase, progress, ct);
PushAsync(repo, setUpstream, forceWithLease, progress, ct);             // --force 는 제공하지 않음
StashPushAsync(repo, message?, includeUntracked = true, ct); StashPopAsync(repo, stashName?, ct);
CreateBranchAsync(repo, name, startPoint?, checkout = true, ct); CheckoutAsync(repo, branch, ct);
AddSafeDirectoryAsync(path, ct);                                        // git config --global --add safe.directory
```

사용자가 직접 누른 pull/push는 인증 창(Git Credential Manager)이 뜰 수 있게 `GIT_TERMINAL_PROMPT=0`을 걸지 않는다.
fetch는 `interactive`로 선택한다.

## 실패 유형 (`GitFailure`)

`GitNotFound`, `DubiousOwnership`(`SafeDirectoryCandidate`에 git이 안내한 경로), `AuthenticationFailed`, `MergeConflict`,
`NothingToCommit`, `NotARepository`, `Cancelled`, `Other`. 판정은 git stderr/stdout 문구(LC_ALL=C 기준)로 한다.

## 사용 예

```csharp
var git = new GitService();
var repo = git.FindRepo(currentFolder);
if (repo is null) return;                              // Git 요소 숨김
var st = await git.GetStatusAsync(repo.Root, ct: ct);
if (st.Result.Failure == GitFailure.DubiousOwnership)
    await git.AddSafeDirectoryAsync(st.Result.SafeDirectoryCandidate ?? repo.Root);   // [이 저장소 신뢰하기]
var badges = new GitBadgeMap(repo.Root, st.Value!);
var badge = badges.Get(fullPath);                      // FsEntry.Git 에 대응하는 GitFileStatus

var calc = new GraphLaneCalculator();
await foreach (var c in git.GetLogAsync(repo.Root, new LogFilter { Count = 1000 }, ct))
{
    var row = calc.Next(c.Hash, c.Parents);            // Column, IncomingColumns, ParentColumns, PassThroughColumns
}
```

## 구현 메모와 한계

- git이 띄운 백그라운드 자식(gc --auto, fsmonitor)이 stdout/stderr 핸들을 물고 있어도 종료 후 2초만 기다리고 진행한다.
- 이 개발 머신에서는 git 호출 하나가 약 2초 걸려 통합 테스트가 느리다 (Defender 영향).
- LibGit2Sharp 폴백(git.exe 없을 때 읽기 전용)은 구현하지 않았다. `IsGitAvailable == false`면 읽기도 `GitNotFound`를 돌려준다.
- 파일·줄 단위 Stage(패치 적용), diff, blame, 충돌 해결, 태그/리모트 목록은 이후 작업.
- 자동 fetch 스케줄링과 파일 변경 디바운스(0.5초)는 호출하는 쪽(Presentation)의 몫이다.
