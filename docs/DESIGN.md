# Foldy 설계 문서

작성일: 2026-11-18 · 원본: https://claude.ai/artifact/LJcbU1v9q3mgAezWGZZRtW

## 개요

Foldy는 C# .NET 10 + WPF로 만드는 Windows 듀얼 패널 파일 탐색기입니다. 목표는 "절대 멈춘 것처럼 보이지 않는 빠른 탐색기"이며, 좌우 패널마다 탭을 여러 개 열 수 있습니다.

핵심 기능:

- 듀얼 패널 + 패널별 다중 탭, 디렉터리 트리와 파일 목록
- MFT/USN 기반 색인: Everything식 즉시 검색, 폴더 크기, 디스크 정리 제안
- 자체 병렬 복사 엔진(스레드 자동 조절, 수동 모드 기본 4)
- Git 저장소 자동 감지와 Fork식 Git 뷰(commit, pull, push, history)
- SFTP 탐색과 SSH 터미널, 로컬 폴더의 CMD / PowerShell / 관리자 셸 아이콘
- 작업 상태를 직관적으로 보여주는 진행 표시와 Foldy 캐릭터 애니메이션

설계 원칙:

1. UI 스레드는 디스크와 네트워크를 절대 직접 건드리지 않는다.
2. 바쁜 곳만 바쁘게 표시한다. 앱 전체를 막는 모달 진행창은 쓰지 않는다.
3. 체감 속도 예산을 지킨다: 입력 반응 16ms, 로컬 폴더 열기 100ms, 실행 후 조작 가능 300ms.
4. 데이터와 화면 상태가 항상 일치한다. 중단된 작업도 어디까지 됐는지 보인다.

## 화면 구조

좌우 두 패널이 같은 구조를 갖고, 각 패널은 아이콘 바 → 탭 → 경로바 → 트리+목록 순서로 쌓입니다.

```
MainWindow
├─ 상단 공통 툴바 (복사 F5 / 이동 F6 / 삭제 / 새 폴더 / 패널 동기화)
├─ PaneHost (좌) ─┬─ 드라이브 바 (C: D: \\NAS SFTP…) + 기능 아이콘
│                 ├─ TabStrip (드래그로 순서 변경·반대 패널로 이동)
│                 ├─ 경로바 (진행선, Git 브랜치 칩)
│                 └─ TabContent ─ TreeView | FileList
├─ PaneHost (우) ─ 동일
├─ 작업 패널 (복사·이동·Git·디스크 분석 진행)
└─ 상태바 (선택 개수/용량, Git 브랜치, 연결 상태)
```

- **기능 아이콘 바의 상황별 고정 자리**: 탭 종류(로컬 / SFTP / Git 저장소)에 따라 터미널·Git 아이콘이 바뀌되, 자리는 오른쪽 끝에 고정해 다른 아이콘이 밀리지 않게 한다.
- **TabState**: Provider + CurrentPath + History(뒤로/앞으로) + 정렬·선택 상태. 종료 시 JSON으로 저장하고, 시작 시 활성 탭만 먼저 로드한다.
- **키보드 우선**: Tab으로 패널 전환, 타이핑하면 해당 파일로 이동(Type-ahead), ``Ctrl+` `` 기본 셸, ``Ctrl+Shift+` `` 관리자 셸.

## 기술 스택과 솔루션 구성

본체는 C# .NET 10(LTS) + WPF, 디스크 색인은 별도 네이티브 서비스(C++ 또는 Rust)로 구성합니다. 파일 탐색기의 병목은 언어가 아니라 I/O와 그리기이므로, 생산성과 안정성이 높은 C#을 주 언어로 하고 권한이 필요한 MFT 읽기만 분리합니다.

| 역할 | 선택 | 비고 |
| --- | --- | --- |
| UI 본체 | C# .NET 10 + WPF | ReadyToRun 빌드. WPF는 Native AOT 미지원. 모던 스타일은 WPF-UI 등 |
| 디스크 색인 서비스 | C++ 또는 Rust (미정) | SYSTEM 권한 Windows 서비스, Named Pipe로 UI와 통신 |
| SFTP/SSH | SSH.NET | known_hosts는 Foldy가 직접 읽고 씀 |
| Git 읽기 | LibGit2Sharp 또는 git CLI 파싱 | status, log, diff, 그래프 |
| Git 쓰기 | git.exe CLI | commit, pull, push, fetch. Credential Manager·ssh config 그대로 사용 |
| 파일 복사 | 자체 병렬 엔진 + CopyFileEx/CopyFile2 | robocopy는 벤치마크 기준선으로만 사용 |
| 파일 작업(삭제 등) | IFileOperation COM | 휴지통, 덮어쓰기 확인, UAC가 탐색기와 동일 |
| 아이콘·셸 메뉴 | SHGetFileInfo, IContextMenu | 우클릭 메뉴를 Windows에서 그대로 가져옴 |
| 애니메이션 | Lottie(SkiaSharp Skottie) + WPF Storyboard | 진행률로 프레임 지정 |

솔루션 프로젝트 분리(제안):

- `Foldy.Core`: 모델, TabState, 작업 큐, 설정
- `Foldy.Providers`: Local, Smb, Cloud, Sftp Provider
- `Foldy.Copy`: 병렬 복사 엔진, 자동 스레드 조절, 작업 저널
- `Foldy.Git`: IGitService, 그래프 레인 계산
- `Foldy.Indexer`: 네이티브 색인 서비스 + C# 파이프 클라이언트
- `Foldy.UI`: WPF 화면, 애니메이션

안정성 원칙: Git은 git.exe 프로세스, 색인은 별도 서비스로 돌려 이 쪽이 죽어도 본체는 살아 있게 한다. 전역 예외는 DispatcherUnhandledException과 UnobservedTaskException에서 로그를 남기고 해당 탭만 복구한다.

## 파일시스템 추상화와 느린 드라이브 처리

모든 드라이브는 비동기 스트리밍 Provider 뒤에 있고, 드라이브마다 전용 작업 큐가 있어 끊긴 네트워크 드라이브 하나가 앱 전체를 멈추지 못합니다.

```csharp
interface IFileSystemProvider {
  IAsyncEnumerable<IReadOnlyList<FsEntry>> ListBatchedAsync(string path, CancellationToken ct);
  Task<Stream> OpenReadAsync(string path);
  Task CopyAsync(...); Task DeleteAsync(...);
  IObservable<FsChange> Watch(string path);
  DriveSpeedClass SpeedClass { get; }   // Fast / Slow / Offline (실측값 학습)
}
// LocalProvider, SmbProvider, CloudProvider, SftpProvider(SSH.NET), 이후 ZipProvider
```

| 분류 | 판별 | 처리 |
| --- | --- | --- |
| Local | GetDriveType = FIXED, NTFS | MFT 색인 사용 |
| Removable | REMOVABLE | 일반 열거 |
| Network | REMOTE, UNC 경로 | 느린 드라이브 정책 |
| Cloud | Google Drive, OneDrive 등 (라벨·파일시스템명 추정 + 사용자 지정) | 느린 드라이브 + 다운로드 방지 |
| Offline | 응답 시간 초과 | 회색 아이콘, 재시도 버튼 |

드라이브 바는 GetLogicalDrives와 GetDriveType만으로 즉시 그리고, 볼륨 이름·남은 용량·아이콘은 캐시를 먼저 보여준 뒤 백그라운드에서 갱신한다.

드라이브별 작업 큐:

- 드라이브마다 전용 스레드 1~2개. ThreadPool은 쓰지 않는다(블로킹 호출이 풀을 고갈시킴).
- 우선순위: 활성 탭 > 아이콘 > 용량 계산.
- 블로킹된 Win32 호출은 CancelSynchronousIo로 끊고, 안 되면 그 스레드는 버리고 새로 만든다.

폴더 열기 흐름(Stale-While-Revalidate):

1. 캐시된 목록이 있으면 즉시 표시하고 "새로 고치는 중" 표시
2. 드라이브 큐에 스트리밍 열거 요청
3. 200개씩 묶어 100ms 간격으로 UI에 추가
4. 1초 이상이면 진행 표시와 [취소]
5. 다른 폴더로 이동하면 이전 요청 즉시 취소
6. 완료되면 차이만 반영해 스크롤 위치 유지

느린 드라이브에서 하지 않을 것:

- 아이콘은 확장자 기준만(SHGFI_USEFILEATTRIBUTES). 썸네일·미리보기·해시는 기본 끔.
- FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS / OFFLINE 파일은 내용을 읽지 않음(읽는 순간 클라우드 다운로드 시작).
- FileSystemWatcher 대신 활성 탭만 30~60초 폴링.
- 폴더 용량 분석은 사용자 요청 시에만, 낮은 우선순위로.

시작 시: 활성 탭만 로드(나머지는 Lazy), 지난번 오프라인이었던 네트워크 경로는 "연결 확인 중" 상태로 띄우고, NetworkAvailabilityChanged 이벤트에서 재확인한다.

## MFT/USN 기반 디스크 색인과 디스크 정리

로컬 NTFS는 폴더를 하나씩 열지 않고 $MFT를 통째로 읽어 메모리 색인을 만들고, USN 저널로 실시간 갱신합니다. Everything과 WizTree가 빠른 이유와 같은 방식입니다.

| 대상 | 역할 | 비고 |
| --- | --- | --- |
| $MFT 원본 파싱 | 최초 전체 색인(이름, 부모, 크기, 시간) | 크기가 필요하므로 FSCTL_ENUM_USN_DATA가 아닌 원본 파싱 |
| USN 저널 | 생성·삭제·이름변경·크기변경 반영 | FSCTL_READ_USN_JOURNAL |

색인 서비스 구조:

- Foldy.Indexer = SYSTEM 권한 Windows 서비스. UI는 일반 권한으로 실행되고 Named Pipe로 조회한다(UAC 창 없음).
- 색인 스냅샷 + 마지막 USN을 저장해 재부팅 후 1초 안에 복원한다.
- 저장한 USN이 저널 범위를 벗어났거나 Journal ID가 바뀌면 전체 재스캔.

메모리 색인(노드당 약 40바이트, 100만 개에 약 40MB):

```c
struct Node {
  uint32 parent;        // 배열 인덱스
  uint32 nameOffset;    // 문자열 풀 위치
  uint16 nameLen; uint16 flags;
  uint64 size, allocSize, mtime;
  uint64 subtreeSize;   // 폴더 누적 크기
};
```

$MFT 파싱 순서:

1. 부트 섹터에서 클러스터 크기와 MFT 시작 LCN
2. 레코드 0번의 $DATA 런리스트로 MFT 조각 위치
3. 4~16MB 단위 순차 읽기(속도의 핵심)
4. 레코드마다 Fixup 적용 후 $FILE_NAME, $DATA 추출
5. 8.3 짧은 이름 제외, 하드링크와 $ATTRIBUTE_LIST 별도 처리

주의 사항:

- NTFS 전용. FAT/exFAT, 네트워크, SFTP는 FindFirstFileEx(FIND_FIRST_EX_LARGE_FETCH)로 폴백.
- 디스크 정리 화면은 allocSize(실제 차지 공간) 기준.
- 색인에는 권한 없는 폴더의 파일 이름도 들어 있다. 업무 PC용이라면 ACL 필터링 또는 관리자 전용 옵션이 필요.

같은 색인으로 얻는 기능:

- 전체 드라이브 즉시 검색(이름 풀 병렬 부분 문자열 스캔)
- 폴더를 열자마자 보이는 폴더 크기 컬럼, 로컬 폴더 목록을 디스크 I/O 없이 표시
- 대용량·오래된 파일 Top N, 트리맵
- 삭제 가능 후보 제안(규칙 기반): node_modules, %TEMP%, bin/obj, 오래된 다운로드, 중복 파일. 삭제는 항상 휴지통 + 확인 단계
- Git 저장소(.git) 즉시 조회

### 색인이 없는 드라이브: 천천히 모으기 (0.9.20~)

네트워크·회사 보안 드라이브, Google Drive, NTFS가 아닌 드라이브는 MFT를 읽을 수 없어서, Foldy(사용자 권한)가 켜져 있는 동안 뒤에서 폴더를 하나씩 살살 열어 목록을 모은다(`SlowCrawler`).

- 한 번에 한 요청, 목록은 64KB씩 크게, 파일을 하나씩 열지 않음. 속도 조절(AIMD): 평소보다 두 배 넘게 느려지면 쉬는 비율 2배(최대 8배), 아니면 조금씩 줄임. 복사 같은 작업 중엔 쉼.
- 다시 모을 때(1시간마다)는 폴더 수정 시각만 묻고 같으면 지난 목록을 그대로 쓴다. 하위 폴더는 계속 확인. 오래 안 바뀐 폴더는 1·2·4·8·16번에 한 번.
- 드라이브 뿌리에 변경 알림 하나: 생성·삭제·이름 변경은 바로 반영, 알림이 넘치거나 끊기면 곧 다시 모음(0.9.21). SMB는 알림을 놓칠 수 있어 주기 확인은 유지.
- 저장: `%LOCALAPPDATA%\Foldy\crawl\드라이브.txt` + `.idx`. 암호화하지 않는다(Everything·Windows Search처럼, 디스크는 BitLocker). 모으는 중에도 모은 데까지 찾는다.

### 색인·분석 데이터 보안

- 색인 스냅샷(`%ProgramData%\Foldy\Index`): SYSTEM·Administrators만.
- 폴더 분석 DB(`%ProgramData%\Foldy\Analysis`): SYSTEM·Administrators·서비스 계정 + **분석을 요청한 사용자만 읽기**(0.9.22, 예전 Interactive 읽기 제거). 사용자마다 처음 한 번만 권한을 쓴다.
- 검색 결과는 파이프로 연결한 사용자 기준으로 거른다(다른 사용자 프로필 숨김).

## 다국어

- 글은 `assets/i18n/<언어>.json` (원본 `build/i18n/strings.py`에서 만든다). Foldy.Core에 포함 리소스로 들어가고 `Foldy.Core.Localization.Loc`이 읽는다: `Loc.T("키")`, `Loc.F("키", 값)`, XAML `{loc:T 키}` (`Foldy.UI.Localization.TExtension`).
- 언어: 설정 `Language`(auto·ko·en·es·ja·zh-Hans). Foldy를 켤 때 한 번 정한다(바꾸면 다시 켤 때). auto는 Windows 표시 언어(zh-CN·zh-SG → 간체, 모르는 언어 → 영어). 옮기는 동안은 `App.TranslationComplete=false`라 auto도 한국어.
- 없는 키는 영어, 그것도 없으면 키 그대로. `LocalizationTests`가 언어마다 키·{0} 자리가 같은지, 한국어가 남았는지 막는다. UI 시험·문서 캡처는 `FOLDY_LANGUAGE`로 고정(시험은 ko).

## 개발자 정리

- 지워도 되는 폴더 찾기(`Foldy.Core/Cleanup/RebuildableFolders`): 고른 폴더 안을 훑되 찾은 폴더 안으로는 들어가지 않는다. 종류마다 옆 표시 파일(package.json·.csproj·Cargo.toml…)·안 표시 파일(.venv의 pyvenv.cfg)을 확인해 오진을 막는다. 크기는 색인(부모 폴더의 FolderSizes)에서, 없으면 직접 더한다. .git은 CanRebuild=false: 기본 미선택, index·HEAD 시각으로 최근 사용을 보고 경고. 지우기는 휴지통(ShellViewModel.RecycleAsync).
- 파일을 쓰는 프로그램(`Foldy.Platform/SystemInfo/FileLockers`): Restart Manager(RmStartSession → RmRegisterResources → RmGetList). 작업이 실패하면 ShellViewModel.FindLockers로 이름을 받아 작업 줄·상태 줄에 붙인다. 강제 종료는 하지 않는다.
- 개발 캐시 정리(`Foldy.Core/Cleanup/DevCaches` + `DevCleanupFlows`): PATH에서 도구를 찾고(Store의 python 바로 가기 제외) 폴더 크기를 잰 뒤 공식 명령을 고정 인자로 실행(.cmd는 cmd.exe /d /c). 10분 넘으면 멈춘다.
- 넣지 않는 것: 레지스트리 청소·시스템 트윅·핸들 강제 해제 (DECISIONS 2026-11-21).

## AI 연결 (MCP 서버)

Foldy.exe --mcp가 공식 MCP 사양(stdio)으로 도는 서버. AI 앱(Claude Desktop, Claude Code, Codex, Gemini CLI, Cursor, VS Code, Antigravity)이 띄우고, 읽기 전용 도구 6개(파일 찾기, 폴더 보기, 폴더 크기, 폴더 설명, 파일 태그, 드라이브 상태)를 쓴다. 사용자는 "AI 연결" 화면의 [AI에 연결] 한 번으로 그 앱 설정에 Foldy를 넣는다. 자세한 것은 [mcp.md](mcp.md).

## 병렬 복사 엔진

robocopy를 호출하지 않고 robocopy /MT와 같은 병렬 복사를 Foldy 안에 직접 구현하며, 스레드 수는 자동으로 조절합니다. 이렇게 하면 한글 로그 깨짐, /MT 모드의 파일 내부 진행률 부재, 일시정지 불가, 충돌 UI 연동 문제가 모두 해결됩니다.

파이프라인: 스캐너(파일 목록, MFT 활용) → 계획기(충돌 확인, 순서) → 작업 큐(대형/소형 두 줄) → 워커 N개(CopyFileEx) → 검증기(크기·시간, 선택: 해시) → 마무리(이동이면 원본 삭제, 폴더 시간 복원).

- 폴더 생성은 먼저 순차로, 파일은 병렬로, 폴더 수정 시간은 맨 마지막에 복원.
- 대형 줄(예: 256MB 이상): 1~2개 스레드, COPY_FILE_NO_BUFFERING. 소형 줄: 여러 스레드. 큰 파일이 스레드를 모두 붙잡지 않게 한다.
- CopyFileEx 진행률 콜백으로 파일 내부 진행률, 콜백 안 대기로 일시정지, PROGRESS_CANCEL로 취소.
- 같은 서버 안의 SMB 복사는 API가 서버 측 복사를 자동 사용. CopyFile2의 SMB 압축 전송 옵션은 지원 범위 확인 필요.
- 긴 경로: `\\?\` 접두사 + longPathAware 매니페스트.

스레드 자동 조절:

| 경로 | 시작값 | 상한 |
| --- | --- | --- |
| SSD → SSD | 4 | 16 |
| HDD 포함 | 1 | 2 |
| 네트워크 | 8 | 32 |

1. SSD/HDD는 IOCTL_STORAGE_QUERY_PROPERTY의 IncursSeekPenalty로 판별
2. 2초마다 처리량(MB/s, 초당 파일 수)을 측정해 5% 넘게 좋아지면 +2, 나빠지면 되돌림
3. 드라이브 조합별 최적값을 저장해 다음 작업의 시작값으로 사용
4. 같은 드라이브를 쓰는 작업끼리 스레드 예산 공유. HDD 대상 작업은 순서대로

설정: "복사 스레드: 자동(권장) / 수동 [4]".

안정성:

- 재시도는 파일당 1초 후 1회, 실패하면 실패 목록에 넣고 계속. 무한 대기 없음.
- 작업 저널에 완료 목록을 기록해 크래시·재부팅 후 이어서 복사. 같은 파일(크기+시간)은 건너뜀.
- 이동은 대상 기록과 검증이 끝난 파일만 원본 삭제. 같은 드라이브 이동은 경로 변경만(즉시).
- CopyFileEx는 블로킹 호출이라 전용 워커 스레드에서 실행. 상한만큼 미리 만들고 ConcurrencyGate로 동시 실행 수만 조절.

```csharp
public sealed class ParallelCopyEngine {
  readonly Channel<CopyItem> _small = Channel.CreateUnbounded<CopyItem>();
  readonly Channel<CopyItem> _large = Channel.CreateUnbounded<CopyItem>();
  readonly ConcurrencyGate _gate;   // 실행 중 Limit 변경 가능
  readonly AutoTuner _tuner;        // 2초마다 _gate.Limit 조정

  void WorkerLoop(ChannelReader<CopyItem> q, CancellationToken ct) {
    foreach (var item in q.ReadAllBlocking(ct)) {
      using var permit = _gate.Acquire(ct);
      CopyWithRetry(item, ct);       // CopyFileEx + 진행률 콜백 + 일시정지
      _journal.MarkDone(item);
    }
  }
}
```

벤치마크: 같은 조건의 robocopy /MT 결과를 기준선으로 두고, 자체 엔진이 그보다 느리지 않은지 매 빌드 비교한다.

## Git 통합

폴더에 들어가면 상위로 올라가며 .git을 찾고, 저장소 안에서만 Git 요소가 나타났다가 밖으로 나가면 사라집니다. 읽기는 라이브러리로, 쓰기는 git.exe CLI로 나눕니다.

감지:

- .git이 파일인 경우(워크트리, 서브모듈)도 처리. 가장 가까운 저장소가 기준.
- 저장소 루트는 경로별 캐시. 로컬 NTFS는 MFT 색인에서 .git을 즉시 조회해 상위 목록의 저장소 폴더에 Git 배지를 미리 표시.
- 비동기 감지: 목록이 먼저 뜨고 Git 요소는 0.1~0.3초 뒤에 나타남.

| 위치 | 표시 |
| --- | --- |
| 기능 아이콘 바(오른쪽 끝 고정 자리) | Commit, Pull, Push, History |
| 경로바 | 저장소 루트에 Git 아이콘, 브랜치 칩 main ↑2 ↓1 |
| 탭 제목 | 작은 브랜치 아이콘 |
| 파일 목록 | M 수정 / A 추가 / ? 미추적 / ! 충돌, ignore는 흐리게 |
| 상태바 | 변경 개수, 마지막 fetch 시간 |
| 우클릭 메뉴 | 파일 히스토리, Blame, Stage, 변경 취소 |

Fork식 Git 뷰(History 버튼으로 탭 전환):

- 툴바: Fetch, Pull(merge/rebase), Push, Stash, Branch
- 사이드바: Local Changes, Branches, Remotes, Tags, Stashes
- 가운데: 브랜치 레인 그래프 + 커밋 목록(검색: 메시지·작성자·해시), 아래: 변경 파일과 Diff
- Local Changes: Unstaged/Staged, 파일·줄 단위 Stage, 메시지 입력 후 Commit / Commit & Push
- Pull 충돌: 충돌 파일 목록 + 외부 머지 도구(1차). Push force는 --force-with-lease만, 경고창 경유
- 그래프 레인: 토폴로지 순서로 받아 "열린 부모 해시 → 열 번호" 배열로 계산, 수천 개 단위 페이징 + 가상화
- 진행 표시: CLI --progress 출력을 파싱해 단계명과 퍼센트 표시

```csharp
interface IGitService {
  Task<RepoStatus> GetStatusAsync(string repo);
  IAsyncEnumerable<CommitNode> GetLogAsync(string repo, LogFilter f);
  Task StageAsync(...); Task CommitAsync(string msg, bool amend);
  Task<GitResult> PullAsync(PullMode mode, IProgress<string> p);
  Task<GitResult> PushAsync(bool setUpstream, bool forceWithLease, IProgress<string> p);
}
```

상태 계산: `git status --porcelain=v2 -z --branch` 한 번으로 파일 상태와 ahead/behind. 파일 변경 시 0.5초 디바운스 후 재계산, 창 포커스 시 갱신. 자동 fetch는 기본 끔(옵션 10분/30분), 자동 fetch에서는 인증 프롬프트를 띄우지 않는다.

예외 상황:

- 네트워크 드라이브 저장소: 감지만 하고 상태 계산은 수동
- dubious ownership: 안내 + [이 저장소 신뢰하기](safe.directory 추가)
- git.exe 없음: LibGit2Sharp로 상태·히스토리만, 쓰기 버튼에 Git for Windows 설치 안내
- SFTP 패널의 원격 저장소: SSH로 git 원격 실행(2차)

## SFTP/SSH와 터미널 아이콘

탭 종류에 따라 기능 아이콘 바의 같은 자리에 맞는 터미널 아이콘이 나타나고, 모든 터미널은 현재 패널 경로에서 시작합니다.

| 탭 종류 | 아이콘 |
| --- | --- |
| 로컬 폴더 | CMD, PowerShell, 관리자 ▾(관리자 CMD / 관리자 PowerShell, 방패 표시) |
| SFTP | SSH 터미널, sudo 셸 |
| Git 저장소 | 로컬 아이콘 + Git Bash(설치 시) |

단축키: ``Ctrl+` `` 기본 셸, ``Ctrl+Shift+` `` 관리자 셸, 아이콘 Ctrl+클릭도 관리자.

로컬 터미널:

- Windows Terminal이 있으면 `wt -d "경로" -p "프로필"`, 없으면 기본 콘솔. PowerShell 7(pwsh) 우선.
- 관리자 실행은 작업 폴더가 무시되므로 경로를 명령으로 전달: `cmd /k cd /d "C:\경로"`, PowerShell은 `-NoExit -Command "Set-Location -LiteralPath '...'"`.
- 매핑된 네트워크 드라이브는 관리자 세션에서 보이지 않으므로 UNC 경로로 변환.
- CMD는 UNC로 cd를 못 하므로 `pushd "\\서버\공유\경로"` 사용.
- 관리자 실행은 항상 UAC를 거치고, 관리자 자격 증명은 저장하지 않는다.

SSH 터미널(같은 서버, 같은 계정, 같은 원격 경로):

1. 1단계: 외부 터미널 + OpenSSH — `wt ssh -t -p 22 user@host "cd '/경로' && exec $SHELL -l"`. 키 인증은 -i로 같은 키를 넘겨 추가 입력 없음, 비밀번호 인증은 한 번 더 입력.
2. 2단계: 내장 터미널 패널 — 기존 SSH.NET 연결에서 ShellStream을 열어 재인증 없이 즉시. 셸 cd와 파일 패널 경로 동기화 옵션. WPF 터미널 에뮬레이터 컨트롤은 조사 필요.

- sudo 셸은 `sudo -i`로 시작, 비밀번호는 터미널에서 직접 입력받고 저장하지 않는다.
- SSH.NET은 known_hosts를 자동으로 읽지 않으므로 Foldy가 `~/.ssh/known_hosts`를 직접 읽고 써서 SFTP와 외부 ssh의 호스트 키 판단을 일치시킨다.
- SFTP 연결 프로필은 DPAPI로 암호화 저장.

설정: 기본 터미널(Windows Terminal / 기본 콘솔), 기본 셸(PowerShell 7 / Windows PowerShell / CMD), WT 프로필 이름(관리자용 별도), SSH 방식(외부 / 내장).

## 체감 속도, 진행 표시, 애니메이션

목표는 사용자가 기다린다고 느끼지 않게 하는 것이고, 기다려야 할 때는 "살아 있다"는 신호가 항상 움직이게 하는 것입니다.

| 동작 | 목표 |
| --- | --- |
| 키 입력, 선택, 스크롤 | 16ms 이내 |
| 로컬 폴더 열기, 탭 전환 | 100ms 이내 |
| 앱 실행 후 조작 가능 | 300ms 이내 |
| 느린 드라이브 | 100ms 안에 무언가 표시 |

체감 속도 기법:

- 트레이 상주 + 단축키로 창만 표시(콜드 스타트 제거). 상주하지 않을 때는 레이아웃 스냅샷부터 그림.
- 로컬 폴더 목록은 색인 메모리에서 바로(디스크 I/O 0).
- Prefetch: 트리에서 커서가 머문 폴더와 선택 폴더의 하위 목록.
- ListView 가상화(Recycling), 고정 행 높이, 보이는 행만 아이콘 로드, 정렬·필터는 백그라운드.
- 낙관적 UI: 이름 변경·삭제·새 폴더는 화면에 먼저 반영, 실패 시 되돌림.
- 모든 동작의 소요 시간을 기록하고 예산 초과를 로그. 테스트 폴더(10만 개 파일, 깊은 트리, 끊긴 SMB)로 매 빌드 측정.

경과 시간별 표현:

| 경과 | 표현 |
| --- | --- |
| 0~300ms | 표시 없음(깜빡임 방지) |
| 300ms~2초 | 경로바 얇은 진행선, 탭 아이콘 회전 점 |
| 2~10초 | "불러오는 중… 3,412개" 개수 실시간 증가, 스켈레톤 행 |
| 10초 이상 | 경과 시간 + [취소] + [백그라운드로 계속] |

진행 표시 위치: 경로바 2px 막대, 탭 아이콘 원형 링, 드라이브 바 링, 상태바 미니 막대, 작업 패널 상세 막대(%, 개수, MB/s, 남은 시간, 현재 파일, 일시정지/취소), 작업표시줄 진행률(TaskbarItemInfo).

진행률 규칙:

- 총량을 모를 때는 "계산 중…" 후 퍼센트로 전환. 로컬 NTFS는 색인으로 총량을 즉시 알 수 있다.
- 바이트 기준 계산, 막대는 절대 뒤로 가지 않음.
- 남은 시간은 지수이동평균, 처음 2~3초는 "계산 중". 숫자는 초당 4~10회만 갱신.
- 색: 진행 파랑, 일시정지 노랑, 오류·확인 필요 빨강, 완료 초록(아이콘·문구 병행).
- 충돌은 모달로 끊지 않고 작업 패널과 탭에 "확인 필요". 부분 실패는 "1,935개 성공, 5개 실패" + [실패 항목 보기] [다시 시도].

Foldy 캐릭터 애니메이션(사무실 세계관, 5초 이상 작업에만):

| 작업 | 장면 | 데이터 연결 |
| --- | --- | --- |
| 복사 | 복사본을 서류 카트에 싣고 운반, 원본 더미 그대로 | 걷는 속도 = MB/s, 캐릭터 수 = 현재 스레드 수, 큰 파일 = 큰 상자 |
| 이동(다른 드라이브) | 포장 → 운반 → 도착 확인 → 빗자루로 원본 정리 | 확인 체크 후에만 원본 삭제 |
| 이동(같은 드라이브) | 폴더 이름표만 옮겨 달기 | 0.5초 이하 |
| 삭제·정리 | 구긴 종이를 휴지통에 던지기, 두루마리 휴지 진행 막대(테마 옵션) | 골인 = 성공, 빗나감 = 실패. 영구 삭제는 분쇄기 |
| 네트워크 대기 | 졸거나 시계 보기 | 경과 초 표시, 15초 넘으면 [취소] 제안 |
| 디스크 분석 | 트리맵 블록이 채워짐 | 스캔 용량·파일 수 |
| Git push / pull | 로켓 발사 / 낙하산 착지 | CLI 단계별 장면 전환 |

카트 상태: 일시정지 = 앉아서 쉬기, 이름 충돌 = 머리 위 "?", 오류 = 서류 한 장 떨어짐, 네트워크 끊김 = 길 중간에서 두리번, 완료 = 꾸벅 인사.

구현 원칙: Lottie(SkiaSharp Skottie)로 재생하고 진행률로 프레임 지정. 창이 최소화되면 정지, 프레임 수 제한, 설정에서 끄기 가능, Windows "애니메이션 효과 끄기" 준수. 업무용 기본 테마와 재미 테마를 분리한다. 대기 중에는 단축키 팁을 한 줄씩 보여준다.

## 개발 로드맵과 확인 필요 사항

MVP는 듀얼 패널·탭·기본 파일 작업과 로컬 터미널 아이콘이고, 색인 서비스와 병렬 복사 엔진이 그 다음입니다.

1. MVP: 듀얼 패널, 탭, 트리와 목록, IFileOperation 복사·이동·삭제, 탭 상태 저장, 드라이브별 작업 큐와 느린 드라이브 처리, CMD/PowerShell/관리자 아이콘, 기본 진행 표시
2. 병렬 복사 엔진: 대형/소형 두 줄, 자동 스레드 조절, 작업 저널, robocopy 기준선 벤치마크
3. 색인 서비스: MFT 파싱, USN 감시, 즉시 검색, 폴더 크기, 디스크 분석과 정리 제안
4. Git: 자동 감지와 상태 표시 → Git 뷰(그래프, Diff) → Stage/Commit → Fetch/Pull/Push → 브랜치·Stash·충돌 UI
5. SFTP: SftpProvider와 연결 관리자, 외부 SSH 터미널 → 내장 터미널
6. 애니메이션: Foldy 마스코트와 소품 세트(카트, 상자, 빗자루, 휴지통), Lottie 연동

결정·확인이 필요한 것:

- [ ] 색인 서비스 언어: C++(자료·Win32 예제 풍부) vs Rust(안정성)
- [ ] 사용 환경: 개인용 vs 업무용. 업무용이면 색인 ACL 필터링, 접속 정보 저장, 감사 로그 요구 정리
- [ ] /MT 모드 robocopy 로그 형식(벤치마크 비교용)과 옵션 기본값을 실제 환경에서 확인
- [ ] CopyFile2의 SMB 압축 전송 옵션 지원 범위 확인
- [x] Google Drive 등 클라우드 드라이브 판별 기준 확인 (2026-11-19: 볼륨 라벨에 "Google Drive" 또는 파일 시스템 이름 GoogleDriveFS/DriveFS → `CloudVolumes`. 사이드바 구름 아이콘, 썸네일·EXIF·파일별 아이콘·미리보기 자동 읽기 차단. 실제 G:의 라벨·FS 이름은 노트북에서 확인 필요)
- [ ] WPF용 터미널 에뮬레이터 컨트롤 조사(내장 SSH 터미널용)
- [ ] Fork Windows판의 구현 방식(C#/WPF, git CLI 사용)과 LibGit2Sharp SSH 지원 범위는 기억에 근거한 내용이므로 확인
- [ ] Foldy 마스코트 표정 세트(기본, 작업 중, 대기, 완료, 오류) 디자인
