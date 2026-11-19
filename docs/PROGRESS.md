# Foldy 진행 현황 (2026-11-19 08:25 기준)

다음에 이어서 작업할 때 이 문서부터 읽는다. 설계: `docs/DESIGN.md`, 상세 설계서(Claude Doc) https://claude.ai/artifact/BQjDCujQvfm5T3hsVHNZFe, 결정 기록: `docs/adr/`.

## 현재 상태

- **최신 버전 0.2.7** (클라우드에서 릴리스, 아래 '클라우드 세션에서 한 일'). 이전 0.2.6: GitHub Releases https://github.com/chadolkr/Foldy/releases 에 올라가 있고, 사용자 PC에도 설치돼 있다(`C:\Program Files\Foldy`).
- **자동 업데이트(관리자 창 없음)는 실제 PC에서 끝까지 확인했다.**
  - 0.2.3 → 0.2.5 (08:08~08:09)
  - 0.2.5 → 0.2.6 (08:21~08:22, 약 1분 반): 색인 서비스(SYSTEM)가 받고, 서명·해시를 확인하고, MSI를 조용히 설치한 뒤 Foldy를 다시 띄웠다. 기록에 "새 버전이 정상 시작했습니다"가 남았고, 되돌리기용으로 0.2.5 MSI를 보관하고 있다.
  - 상태와 기록: `C:\ProgramData\Foldy\Updates\` (`updater.log`, `state.json`, `install-<v>.log`)
- 테스트 624개 통과 (`./build.ps1`, 또는 `dotnet test --solution Foldy.slnx --max-parallel-test-modules 3`).
- 브랜치 `main` = origin/main. 릴리스 방법: `Directory.Build.props`의 Version을 올리고 커밋한 뒤 `git tag vX.Y.Z` 하고 main과 태그를 푸시한다. `.github/workflows/release.yml`이 설치 zip, MSI, 서명된 update.json을 만든다.

## 클라우드 세션에서 한 일 (2026-11-19 09:00 KST, 노트북 꺼짐)

노트북 세션은 pull 뒤 아래 일을 다시 하지 않는다. Linux 클라우드에서 `dotnet build -p:EnableWindowsTargeting=true`로 컴파일만 확인했고, WPF 실행·전체 시험은 Windows(CI 또는 노트북)에서 돌려야 한다.

- **드라이브 칩**(패널 오른쪽 위): 본문 글자색 13px 굵게 + 테두리 칩(`Foldy.DriveChip`, 레트로는 입체 버튼). 툴팁에 이름·남은 공간.
- **상태바**: 메시지를 가운데 칩(`Foldy.StatusPill`/`Foldy.StatusMessage`, 14px 굵게)에 표시, 새 메시지면 강조색이 1.7초 비쳤다 사라짐(애니메이션 끄면 없음). 상태바 높이 34.
- **공통 사이드바**(창 맨 왼쪽, 216px, 접으면 48px 아이콘 띠, Ctrl+Shift+B): 자주 쓰는 폴더(핀 고정·순서·해제), 최근 폴더 8개(`Foldy.Presentation/Sidebar/QuickAccess.cs`, `%LOCALAPPDATA%\Foldy\quickaccess.json`), 드라이브 용량 막대(90% 이상 빨강), 도구(검색·디스크 분석·SFTP·템플릿·위저드·정리·이미지·터미널·휴지통), 맨 아래 모든 명령·업데이트·버그 신고·설정·도움말·더 보기(기존 ⋯ 메뉴).
- **아래 버튼 줄**: 앱 기능 버튼을 사이드바로 옮기고 파일 작업 6개만 남김 (사용자 요청 2026-11-19, 예전 "오른쪽 끝" 사양을 바꿈).
- **패널별 폴더 트리**: 두 패널 모두 트리 | 목록. 트리 너비·숨김을 session.json(`LeftTreeWidth`/`RightTreeWidth`, `SidebarExpanded`)에 저장.
- **단일 인스턴스**(알려진 이슈 1): `Foldy.Platform/System/SingleInstance.cs` (Local 이름 이벤트 + 현재 사용자 전용 파이프). 두 번째 실행은 인자를 넘기고 끝나며, 첫 창이 앞으로 오고 경로는 새 탭. 첫 Foldy가 3초 안에 답하지 않으면 그냥 새로 뜬다. 벤치·스크린샷 실행은 예외.
- 시험 추가: `QuickAccessTests`, `DriveSpaceTests`(Presentation), `SingleInstanceTests`(Platform). 도움말 `assets/help/index.md`에 "공통 사이드바" 절 추가.
- 화면 시안: 프로젝트 파일 `mockups/foldy-sidebar-{dark,light,retro}.png` (HTML 시안, 실제 화면 아님).
- CI 통과(639개) 후 0.2.7 릴리스. **아직 안 한 것**: 실제 PC 화면 확인(사이드바 너비, 좁은 창에서 목록 폭, 레트로 테마).
- CI 메모: `Foldy.Interop.Tests`의 시험 1개가 가끔 실패한다(c9f3711, d613a4d 1차). 다시 돌리면 통과. 로그에서 Rust 의도적 패닉 출력에 가려 이름을 못 봤다 → 노트북에서 찾아 고칠 것.

## 릴리스 이력

| 버전 | 내용 |
| --- | --- |
| 0.1.0 | 첫 릴리스. 듀얼 패널, 탭, FastFileList, 복사 엔진(Rust), 색인 서비스, Git, SFTP, Markdown 뷰어, 템플릿·위저드·정리, 이미지 보기 |
| 0.1.1 | 자동 업데이트 시험용 |
| 0.2.0 | 도움말(F1), 설정 창(Ctrl+,), 단축키 재지정, 강조색, Pretendard, 미리보기(F3), 마스코트 연주·우진, 디스크 분석, 서비스 기반 무UAC 업데이트 |
| 0.2.1 | 수동 [업데이트] 버튼, 시작 5초 뒤 확인, 실패 이유 표시 |
| 0.2.2 | 오류 자동 신고(동의, 같은 오류 묶음), 버그 신고 버튼, Foldy 정보 창 수정(Brush.Face 누락), 서비스 연결 재시도, 빨라진 애니메이션 |
| 0.2.3 | 터미널 수정(드라이브 루트 따옴표, 관리자 실행은 wt를 거치지 않음, 실패 시 대체 셸) |
| 0.2.4 | CI 시험 시간 초과로 릴리스 실패 (태그만 있음) |
| 0.2.5 | CI 시험 대기 시간 늘림. 업데이트 시험용 |
| 0.2.7 | (클라우드) 공통 사이드바(자주 쓰는 폴더·최근·드라이브 용량·도구·설정), 두 패널 모두 폴더 트리, 드라이브 칩·상태바 메시지 가독성, 단일 인스턴스(기존 창 앞으로) |
| 0.2.6 | 새 농구 애니메이션(진행률 = 위치, 11가지 동작, 작업별 마무리), 작업 중에는 업데이트를 예약했다가 끝나면 설치, 앱이 1시간마다 서비스에 확인 요청 |

## 마일스톤

| 단계 | 상태 | 남은 일 |
| --- | --- | --- |
| M0 기반·기술검증 | 완료 | — |
| M1 MVP | 완료 | — |
| M2 Rust 색인·분석 DB·즉시 검색 | 완료 | 색인 영속화·Subscribe, SQLite 목록 캐시, 중복 파일 찾기(file_hash), 예약 분석 |
| M3 Rust 복사 | 완료 | — (CopyThreads 연결함) |
| M4 Git | 핵심 완료 | diff/blame 화면 |
| M5 SFTP | 완료 | ssh-agent/Pageant (SSH.NET 미지원) |
| M6 애니메이션·출시 | 진행 중 | 아래 "다음 할 일" |

## 사용자 요구 사항 (정해진 사양)

- **디자인**
  - 모던(심플하고 세련됨)이 기본이고, 레트로는 선택.
  - 라이트·다크는 Windows 설정을 따른다.
  - 탭은 OneNote 스타일 색 탭.
  - 버전은 창 제목 "Foldy x.y.z"에 표시.
- **아래쪽 버튼 줄**
  - 파일 작업(F2 이름 바꾸기, F3 미리보기, F5 복사, F6 이동, F7 새 폴더, Del 삭제)은 두 패널 너비에 고르게 펼친다.
  - 명령·업데이트·버그 신고·도움말·⋯은 오른쪽 끝에 둔다.
- **마스코트 "연주"(여, 주황 유니폼) & "우진"(남, 파랑 유니폼)**
  - 오리지널 캐릭터이고, 이름은 발밑에 항상 보인다.
  - 진행률 = 위치: 0%에 맨 왼쪽에서 출발해 진행률만큼 오른쪽으로 나아간다.
  - 가는 동안 11가지 동작을 겹치지 않게 섞는다: 크로스오버, 비하인드 백, 레그스루, 스핀, 기브앤고, 바운스 패스, 앨리웁 셋업, 스크린앤롤, 컷인, 슛 페이크, 방향 전환.
  - 마무리는 작업이 끝났을 때만 한다.
    - 복사: 코트에 그린 3점 라인 밖, 골대에서 멀리서 던지는 3점슛 후 하이파이브.
    - 이동: 앨리웁 덩크.
    - 삭제: 코트 옆 휴지통에 던져 넣기.
    - 영구 삭제: 경기장 밖으로 멀리 던지기.
    - 실패: 림을 맞고 튕겨 나감.
  - 설정의 "애니메이션 사용"으로 끈다. CPU를 적게 쓴다(30fps 상한, 안 보이면 멈춤).
- **업데이트**
  - 서비스가 관리자 창 없이 설치한다.
  - 작업 중에 누르면 예약해 두었다가 작업이 끝나면 자동으로 설치하고, 다시 누르면 취소한다.
  - 모든 네트워크는 Windows 시스템 프록시(PAC/WPAD, 기본 자격 증명)를 쓴다.
- **오류 신고**
  - 비정상 종료와 평소 오류를 모으고, 같은 오류는 묶는다.
  - 처음에 "항상 자동으로 보내기 / 이번만 / 보내지 않기"를 묻는다.
  - 경로와 사용자 이름은 지운다.
  - Cloudflare Worker 릴레이가 GitHub 이슈로 올린다.
- **개발자 표기**: "David CHA", https://github.com/chadolkr/Foldy, GPL-3.0-or-later. 도움말 첫머리와 Foldy 정보 창에 넣는다.
- **도움말** `assets/help/index.md`: 기능을 추가할 때마다 같이 고친다.
- **x64만** 빌드한다.
- 사용자는 모바일에서 조작할 때가 많다. UAC를 누를 수 없을 수 있다.

## 알려진 이슈

1. ~~0.2.6 업데이트 뒤 Foldy 창이 두 개 떴다~~ → 단일 인스턴스 추가 (클라우드, Windows 확인 전).
2. 마스코트 띠가 48px라 3점슛 포물선이 낮다. 스크린앤롤 때 이름표가 겹칠 수 있다. 덩크·휴지통·영구 삭제 장면은 눈으로 확인하지 않았다.
3. 터미널 수정(0.2.3)은 사용자 확인 전이다. 오류가 계속 나면 메시지를 받아야 한다.
4. 레트로 테마에서 말풍선의 "?", "!"가 잘 안 보인다.
5. 업데이트 채널 일부는 저장만 되고 반영되지 않는다. (복사 스레드 수는 클라우드에서 연결함)
6. 미리보기 셸 처리기는 UI 스레드에서 돈다(전용 STA 스레드는 후속 과제).

## 다음 할 일 (순서대로)

1. ~~단일 인스턴스~~ (클라우드에서 구현, 위 참고). Windows에서 시험·실제 확인 후 0.2.7 릴리스.
2. 터미널·관리자 모드를 사용자에게 확인받고, 필요하면 고친다.
3. 마스코트를 다듬는다: 띠 높이를 키우거나 포물선을 높이고, 이름표 겹침을 없애고, 나머지 장면을 눈으로 확인한다.
4. ~~CopyThreads 설정을 복사 엔진에 연결~~ (클라우드, 0.2.8 예정): 작업을 시작할 때마다 설정값을 읽는다(`NativeCopyFileOperationOptions.ThreadsProvider`).
5. M6 출시 준비: 1.0 체크리스트(접근성, 성능 회귀, 설치·제거, SignPath 코드 서명), Git diff/blame, 색인 영속화.
6. 0.3.0 릴리스.

## 재개 방법 (세션 정보)

- **프로젝트**: Claude 프로젝트 "Foldy". 작업 스레드는 "다기능 파일매니저 (윈도우용)"이고, 이 스레드에서 이어서 말하면 된다.
- **Claude Code 세션**: 로컬 transcript는 `C:\Users\cuser\.claude\projects\C--WorkSpace-Foldy\06fb3c7c-e696-4feb-a9ca-dbc84dc367d8.jsonl`. 프로젝트 메모리는 `C:\Users\cuser\.claude\projects\C--WorkSpace-Foldy\memory\foldy-project.md`.
- **작업 폴더**: `C:\WorkSpace\Foldy` (git `main`, origin = https://github.com/chadolkr/Foldy). 남은 작업 트리는 `.claude\worktrees\` 아래에 있고, 모두 main에 병합했다.
- **시작할 때**: "docs/PROGRESS.md 읽고 이어서 진행해줘"라고 말하면 된다.
- **빌드와 시험**
  - `./build.ps1`: Rust, .NET, 시험 전체
  - `./build.ps1 -SkipRust`: .NET만
- **로컬 설치본**: `./build/setup/make-setup.ps1 -SkipRust`를 실행하면 `dist\Foldy-Setup\Foldy 설치.cmd`가 생긴다.
- **릴리스**
  1. `Directory.Build.props`의 `<Version>`을 올린다.
  2. 커밋하고 `git tag vX.Y.Z`를 만든다.
  3. `git push origin main`, `git push origin vX.Y.Z`
  4. Actions "Release"가 끝나면 `gh release view vX.Y.Z`로 확인한다.
- **업데이트 상태를 확인할 때**: `C:\ProgramData\Foldy\Updates\updater.log`, `state.json`. 서비스에 바로 확인을 요청하는 도구는 scratchpad의 `upd` 프로그램이다(IndexerClient `CheckForUpdateAsync`). 세션이 바뀌면 사라질 수 있다.
- **키와 비밀값의 위치** (값은 여기 적지 않는다)
  - 업데이트 서명 비밀키: `%LOCALAPPDATA%\Foldy\signing\foldy-update.signing.key`. 공개키는 저장소에 있다. GitHub secret은 `FOLDY_UPDATE_SIGNING_KEY`.
  - 오류 신고 릴레이: Cloudflare Worker `foldy-crash-relay` (`tools/crash-relay`, `npx wrangler deploy`). Worker secret은 `GITHUB_TOKEN`(Issues 쓰기 PAT). KV는 `CRASH_KV`.
- **권한 메모**: 관리자 권한 설치는 사용자가 직접 한다. 서비스에 설치를 시키는 명령도 사용자가 버튼으로 한다.

## 작업 환경 메모

- .NET 10 SDK: `%LOCALAPPDATA%\Microsoft\dotnet` (build.ps1이 PATH를 잡는다). Rust: rustup.
- 시험 exe는 `bin/x64/Release`에 생긴다. 반드시 `dotnet build Foldy.slnx -c Release`로 빌드한다. csproj 하나만 빌드하면 `bin/Release`로 가서 시험이 옛 파일로 돈다.
- Foldy.slnx가 충돌하면 `build/regen-slnx.ps1`을 실행한다.
- 서명 비밀키는 `%LOCALAPPDATA%\Foldy\signing`에 있다. 커밋하지 않는다. GitHub secret은 `FOLDY_UPDATE_SIGNING_KEY`.
- 로컬 설치본은 `build/setup/make-setup.ps1`로 만들고, 결과는 `dist\Foldy-Setup\Foldy 설치.cmd`.
