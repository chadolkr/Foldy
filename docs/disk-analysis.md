# 디스크 분석과 정리 제안

설계서 7.3 "디스크 분석 DB (SQLite)", 9장 cleanup-rules.json, DESIGN.md "MFT/USN 기반 디스크 색인과 디스크 정리"의 구현.
색인 서비스(foldy-index)가 메모리 색인을 그대로 훑어 볼륨별 SQLite 스냅샷을 만들고(디스크를 다시 읽지 않는다), 앱은 그 파일을
읽기 전용(`Mode=ReadOnly`)으로 열어 트리맵·Top N·정리 제안·지난 분석 대비를 보여 준다. 서비스가 없는 드라이브(USB, 네트워크,
FAT/exFAT)나 서비스가 꺼져 있으면 C#이 폴더를 직접 열거해 같은 스키마의 DB를 `%LOCALAPPDATA%\Foldy\Analysis\`에 만든다.

| 파일 | 역할 |
| --- | --- |
| `engine/foldy-index/src/analysis/mod.rs` | 색인 → 스냅샷 DB: 복사(`extract`, 읽기 잠금은 이 동안만) → 집계 → 규칙 → 임시 파일 대량 INSERT → 인덱스 → ANALYZE → 이름 변경, 보관 정책, 폴더 ACL |
| `engine/foldy-index/src/analysis/rules.rs` | 정리 규칙 엔진 (글롭, 조건, 보호 폴더) |
| `engine/foldy-index/src/analysis_job.rs` | `BuildAnalysis` / `AnalysisInfo` 처리, 볼륨별 백그라운드 스레드(낮은 우선순위) |
| `engine/foldy-proto/src/lib.rs` | 메시지 정의 (아래) |
| `assets/cleanup-rules.json` | 기본 규칙. Rust는 `include_str!`, C#은 EmbeddedResource로 넣는다 |
| `src/Foldy.Analysis/` | `AnalysisDb`(쿼리), `CleanupRules`, `SquarifiedLayout`(트리맵 배치), `SnapshotBuilder` + `FallbackScanner`(폴백), `AnalysisPaths`(이름·보관), `AnalysisCoordinator` + `IAnalysisSnapshotService` |
| `src/Foldy.Indexer.Client/IndexerAnalysisService.cs` | 파이프 메시지 → `IAnalysisSnapshotService` 어댑터 |
| `src/Foldy.Presentation/Analysis/AnalysisTabViewModel.cs` | `TabBase`(Kind = `TabKind.Analysis`, 제목 `분석: <루트>`) |
| `src/Foldy.UI/Analysis/AnalysisView.xaml(.cs)`, `TreemapControl.cs` | 화면, 직접 그리는 정사각형 트리맵 |
| `tests/Foldy.Analysis.Tests` | 쿼리, 규칙, 트리맵 배치, 폴백 스캐너, ViewModel, 렌더 스모크(라이트/다크/레트로) |
| `tests/Foldy.Interop.Tests/IndexerAnalysisTests.cs` | 골든 프레임, Rust가 만든 DB를 C#이 여는 끝까지 시험 |

## 호스트가 여는 법

```csharp
// 앱 전체에서 하나. 서비스 연결은 호출마다 새로 맺는다 (기존 IndexerClient를 넘기려면 connect 인수와 disposeClient: false).
var analysis = new AnalysisCoordinator(new IndexerAnalysisService());   // 서비스를 아예 안 쓰려면 null

var tab = new AnalysisTabViewModel(analysis, @"C:\");   // 드라이브 루트 또는 폴더 ("D:\작업"이면 트리맵이 그 폴더에서 시작)
tab.RecycleRequested += async (_, e) =>
{
    // 확인 창 → 휴지통으로 (영구 삭제 금지, 설계서 8.3). ViewModel은 아무것도 지우지 않는다.
    var ok = Confirm($"{e.Paths.Count}개, {AnalysisText.FormatSize(e.TotalBytes)}를 휴지통으로 옮길까요?")
             && await recycleBin.MoveAsync(e.Paths);           // 호스트의 휴지통 작업(IJob)
    foreach (var item in e.Items) tab.MarkRecycled(item, ok);   // 성공하면 목록에서 빠지고, 실패·취소면 버튼이 다시 켜진다
};
tab.OpenRequested += (_, e) => (e.IsFolder ? (Action<string>)pane.NewFolderTab : pane.RevealFile)(e.Path);
pane.AddTab(tab);
pane.ActiveTab = tab;   // OnActivated(): 최신 스냅샷을 열고, 없으면 바로 분석을 시작한다
```

탭 종류별 화면 선택 (Git 탭과 같은 자리):

```csharp
TabKind.Analysis => new Foldy.UI.Analysis.AnalysisView { DataContext = (AnalysisTabViewModel)tab },
```

- 분석 탭은 세션에 저장하지 않는다 (`ShellViewModel`이 Search/Git처럼 건너뛴다).
- 탭을 닫을 때 `Close()`가 진행 중인 분석을 취소하고 DB 연결을 닫는다.
- 키: F5 다시 분석, Esc 취소, 트리맵에서 Backspace·오른쪽 클릭·마우스 뒤로 = 한 단계 위로. 목록 두 번 클릭 = `OpenRequested`.
- 테마 토큰만 쓴다 (`Brush.*`, `Font.*`, `Radius`). 트리맵 칸 색: 폴더 `Brush.Tab0~7`, 파일 `Brush.SurfaceAlt`, 나머지 `Brush.Hover`.
- 규칙 파일 바꾸기: 서비스는 실행 파일 옆 → `%ProgramData%\Foldy\cleanup-rules.json`, 폴백은 `%LOCALAPPDATA%\Foldy\cleanup-rules.json`, 없거나 깨졌으면 기본값.

## 파이프 메시지 (프로토콜 v2 하위 호환 확장)

옛 클라이언트는 새 요청을 보내지 않고 StatusReply 끝에 붙은 바이트를 읽지 않으므로 `PROTOCOL_VERSION`은 2 그대로다.
C# 디코더는 `analysis` 목록이 없는 옛 서비스 응답도 읽는다. 옛 서비스가 새 요청을 받으면 Error("unsupported request")를 돌려주고,
어댑터는 null → 폴백 스캐너로 넘어간다.

| 타입 | 코드 | 본문 (postcard) |
| --- | --- | --- |
| `BuildAnalysis` | 0x0006 | `volume: String` ("C:" 또는 `--scan-dir` 폴더 경로) |
| `BuildAnalysisReply` | 0x8006 | `status: u8, message: String`. 바로 온다. 0 시작함, 1 이미 분석 중, 2 색인 준비 안 됨, 3 모르는 볼륨 |
| `AnalysisInfo` | 0x0007 | `volume: String` |
| `AnalysisInfoReply` | 0x8007 | `status: u8, volume: String, db_path: String, scan_time: i64(FILETIME), previous: Vec<{db_path, scan_time}>`. status 0 있음, 3 모르는 볼륨, 4 아직 없음 |
| `StatusReply` (끝에 추가) | 0x8002 | `analysis: Vec<{volume: String, state: u8(0 대기,1 분석 중,2 완료,3 실패), progress_permille: u16, message: String}>` |

진행률: 0~100 색인 복사, 150 집계·규칙, 200~300 폴더 쓰기, 300~750 파일 쓰기, 780 인덱스, 950 ANALYZE, 1000 완료.
`IndexerAnalysisService.BuildAsync`가 `PollInterval`(기본 300ms)마다 Status를 물어 `IProgress<AnalysisProgress>`로 넘긴다.
시험·개발용 서비스 인수 `--analysis-dir <폴더>` (지정하면 ACL을 건드리지 않는다).

## DB

스키마는 설계서 7.3 그대로 (`AnalysisSchema.Tables` = Rust `SCHEMA`). 다른 점:

- `dir_parent` 인덱스를 `(parent_id, name)`으로 만든다 (트리맵 자식 조회 + 지난 스냅샷과 이름으로 짝 맞추기). `file_dir(dir_id)`를 더했다 (트리맵의 파일 칸).
- `PRAGMA user_version = 1`, `application_id = 0x466F6C64("Fold")`. 시각 열은 모두 FILETIME(UTC 100ns). `atime`은 색인에 없어 NULL.
- 루트 `dir` 행의 `name`은 루트 전체 경로("C:\", "D:\작업"), `parent_id`는 NULL. 경로는 `parent_id` 사슬을 재귀 CTE로 올라가 만든다(`AnalysisDb.PathOf`, 캐시).
- 폴더 `size/alloc`은 서비스 DB에서는 색인의 누적값(NTFS 메타 파일 포함), 폴백 DB에서는 파일 합이다. 트리맵은 부모 크기와 자식 합의 차이를 "기타 (작은 파일·시스템)" 칸으로 그린다.
- `file` 행은 기본으로 모두 넣는다. `BuildOptions.min_file_alloc`으로 작은 파일은 dir 집계·ext_stat에만 남길 수 있다 (설계서 옵션, 앱에는 아직 노출 안 함).
- `ext`: 마지막 점 뒤, 소문자, 점이 맨 앞·맨 끝이거나 16자 초과면 "" (`AnalysisText.Extension` = Rust `extension`).
- 하드링크 추가 이름과 NTFS 메타 파일(`$MFT`, `$Extend` 아래)은 넣지 않는다. 루트까지 부모가 이어지지 않는 고아 항목도 뺀다.

### 파일 이름, 보관, 권한

- `<볼륨 키>.<yyyyMMddTHHmmssZ>.db`. 볼륨 키는 드라이브면 볼륨 GUID(`{…}`, 못 얻으면 드라이브 문자), 폴더면 `dir-<소문자 경로 FNV-1a 64>`.
  설계서의 `<볼륨GUID>.db` 하나를 덮어쓰는 방식은 앱이 이전 스냅샷을 열어 둔 채면 Windows에서 이름 변경이 공유 위반으로 실패하므로, 시각을 붙인 새 이름으로 바꾼다(항상 성공).
  최신 경로는 `AnalysisInfo`(서비스) 또는 `AnalysisPaths.ListSnapshots`(폴더 목록)로 찾는다.
- 보관: 최신 1개 + 바로 이전 1개 + 그보다 오래된 것은 앞서 남긴 것보다 6.5일 이상 오래된 것만, 이전 스냅샷 최대 4개 (`retention_plan` = `AnalysisPaths.RetentionPlan`). 열려 있어 못 지운 파일은 다음 분석 때 다시 지운다.
- 서비스 폴더 `%ProgramData%\Foldy\Analysis`의 ACL: 상속을 끊고 SYSTEM·Administrators 모두, **Interactive(로그온 사용자) 읽기**, 서비스 계정 자신.
  설계서는 "Administrators + 설치한 사용자"인데 서비스는 설치한 사용자를 알 수 없으므로 일단 Interactive 읽기로 했다 (파이프 ACL과 같은 기준). ProgramData 기본 ACL은 Users에게 쓰기까지 주므로, 일반 사용자가 가짜 DB를 넣지 못하게 막는 의미도 있다.
  업무용 모드에서 좁히려면 MSI가 설치 사용자 SID를 레지스트리에 남기고 `secure_dir`의 IU를 그 SID로 바꾼다.
- DB에는 권한 없는 폴더의 이름도 들어간다 (색인과 같음). 업무용 PC라면 ACL 필터링이 필요하다 (설계서 12장 결정 사항).

## 정리 규칙 (cleanup-rules.json)

```json
{ "id": "node-modules", "tag": "node_modules", "title": "Node.js 패키지 폴더",
  "kind": "dir",                       // "dir"(기본) | "file"
  "names": ["node_modules"],           // 이름 글롭 중 하나
  "paths": ["\\Users\\*\\AppData\\Local\\Temp"],   // 볼륨 루트 기준 경로 글롭 중 하나
  "parentHas": ["package.json"],       // 같은 부모에 이 이름(글롭)의 항목이 있어야 (폴더 규칙)
  "extensions": ["dmp"],               // 파일 규칙
  "olderThanDays": 30, "minSize": 10485760,
  "reason": "…", "suggest": true }     // suggest=false면 태그만 (.git)
```

- 글롭: `*`는 `\`를 넘지 않고 `**`는 넘는다, `?` 한 글자, 대소문자 무시. 조건이 있는 필드는 모두 만족해야 하고 목록 안에서는 하나만 맞으면 된다. 첫 번째로 맞는 규칙이 태그를 정한다.
- `olderThanDays`는 폴더면 하위에서 가장 최근 수정 시각(`newest_mtime`), 파일이면 그 파일 수정 시각 기준. 시각을 모르면 오래되지 않은 것으로 본다.
- 제안된 폴더 아래는 다시 제안하지 않는다 (용량을 두 번 세지 않게). 파일 규칙은 규칙마다 큰 것 5000개까지.
- **보호 폴더**: 규칙 내용과 상관없이 엔진이 제안을 막는다 (`is_protected` / `IsProtected`, 시험으로 고정).
  볼륨 루트, `Windows`, `Program Files(x86)`, `ProgramData`, `$Recycle.Bin`, `System Volume Information`, `Recovery`, `Boot`, `EFI`, `Windows.old` 등,
  `Users`·프로필 폴더 자체·`Default`/`Public`, 프로필 바로 아래 사용자 폴더(Desktop, Documents, Downloads…) 자체, `AppData`·`AppData\Local|Roaming|LocalLow` 자체,
  `AppData\*\Microsoft`(Windows, Credentials, Protect, Crypto, Vault…). 시스템·재분석 지점 특성이 있는 항목도 막는다.
  파일은 보호 폴더 바로 안의 것(볼륨 루트의 pagefile.sys, 프로필 루트의 NTUSER.DAT)도 막고, 다운로드·문서 같은 사용자 폴더 안의 파일만 예외다.

## 측정 (합성 100만 개 색인)

`cargo test --release -p foldy-index bench_1m -- --ignored --nocapture` (폴더 10,921개, 파일 1,000,040개). 다른 빌드가 함께 돌던 개발 PC 값:

| 옵션 | DB 크기 | 전체 시간 | 내역 |
| --- | --- | --- | --- |
| 파일 모두 | 110 MB | 15~20 s | 색인 복사 0.1 s, 집계+규칙 1.1 s, INSERT 3.3 s, **CREATE INDEX 10~15 s**, ANALYZE(analysis_limit=1000) 5 ms |
| 1 MB 이상만 file 행 | 83 MB (file 75만 행) | 9~13 s | INSERT 1.7~2.6 s, CREATE INDEX 6~9 s |

설계서 추정(100 MB 안팎, 수 초)과 비교하면 크기는 맞고 시간은 인덱스 생성이 대부분이다. 같은 PC에서 Python sqlite3로 100만 행 `file`에
같은 인덱스 4개를 만들어도 13 s가 걸렸으므로 SQLite 정렬 비용 자체다. 줄이려면 `file_ext`/`file_mtime`을 빼고(확장자는 ext_stat, 오래된 파일은
file_alloc 순으로 걸러도 Top N은 빠르다) `min_file_alloc`을 쓰는 것을 실측 후 결정한다 (설계서 12장 "분석 DB에 작은 파일까지 모두 넣을지").
분석 중에도 이전 스냅샷은 계속 읽을 수 있고, 색인 읽기 잠금은 복사하는 0.1 s 동안만 잡는다.

## 한계 / 이후 작업

- 중복 후보(`file_hash`) 테이블과 해시 계산은 아직 없다.
- 예약 분석(정해진 시각에 BuildAnalysis)과 "분석 DB에 작은 파일 넣을지" 설정 화면은 호스트 몫.
- Top N 목록은 볼륨 전체 기준이다 (폴더 하나를 열어도 트리맵만 그 폴더에서 시작).
- 폴백 스캐너는 할당 크기를 4 KB 클러스터로 어림한다 (NTFS 압축·스파스 파일은 실제와 다를 수 있음). 우선순위는 `ThreadPriority.Lowest`(I/O 우선순위는 그대로).
- 지난 분석 대비는 폴더 이름으로 짝을 맞춘다 (이름을 바꾼 폴더는 "새 폴더"로 보인다).
