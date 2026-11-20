# Foldy 진행 현황 (2026-11-20 기준, 0.9.10)

다음에 이어서 작업할 때 이 문서부터 읽는다. 설계: `docs/DESIGN.md`, 디자인 원칙: `docs/DESIGN-PRINCIPLES.md`, 결정: `docs/DECISIONS.md`, 변경: `CHANGELOG.md`, 시험: `docs/TESTING.md`, 리뷰: `docs/reviews/`.

## 0.9.2 ~ 0.9.3 (2026-11-20, 사용자 요청 기능 동결 예외)

- 0.9.2: UX 3차 "할아버지 할머니도 편하게"(같은 자리 토글, 사이드바 가장자리 끌기, 큰 누르는 곳 + 버튼 크기 자동 검사, 처음 설정에서 글자 크기·밝기). 리뷰 `docs/reviews/2026-11-20-ux-3.md`.
- 0.9.3: 빠른 검색(Ctrl+Space, Spotlight 범위, 말하듯 찾기, 블러드하운드, 단축키 겹침 처리), 빨리 지우기(5,000개 이상), 접기 버튼·아이콘 통일, 디자인 원칙 1.3. 결정은 DECISIONS.md 2026-11-20 항목들.
- 0.9.4: 보통 권한에서 색인 서비스 연결이 거절되던 회귀(보안 리뷰 L3 검사) 고침. DECISIONS.md "색인 연결 회귀".
- 0.9.5: 한글 획이 빠져 보이던 문제(Display 그리기) 고침, 모든 화면 Ideal + 테스트. Git 표시를 쉬운 말(수정·추가·새 파일·충돌)과 설명 툴팁으로. 업데이트 파이프 시험 안정화.
- 0.9.6: 폴더 분석은 디스크·폴더를 먼저 고르기.
- 0.9.7: 탭 줄 SFTP 단추, 서버 트리 한 단계 펼치기.
- 0.9.8: 수다쟁이 Foldy(원칙 1.4), 업데이트 진행 창, 트리 "불러오는 중".
- 0.9.9: 이미지만 보기 안내 줄과 [모든 파일 보기].
- 0.9.10: 업데이트 진행 창에 연주·우진 (드리블·패스, 끝에 레이업 2점슛), 새 장면 Layup.
- 다음: 써 보며 빠른 검색 범위(태그·메모 내용·SFTP·Git) 정하기, Ctrl+Space를 "Foldy에게 말 걸기" 하나의 입구로 할지(첫 화면 버튼 줄이기는 사용자 동의 후), 다국어 리소스.

## 0.9.1 (2026-11-20, 0.9.0은 빌드 실패로 배포 안 됨)

- 기능 동결 뒤 첫 안정판. 0.3.13~0.3.24와 그 뒤 작업을 모았다: 정리 도구(대화 창), 폴더 관리 방식, 금고·잠긴 메모·파일 잠그기, 덮어쓰기 복구, 두 창 비교·맞추기, 작업 태그, 메모(.md/.textpack), AI·Claude 템플릿, 네 묶음 사이드바, 화면 크기(Windows 텍스트 크기 따르기), 맥락 제안.
- 보안 리뷰: 치명 1·높음 5 모두 고침, 중간·낮음 대부분 고침 (`docs/reviews/2026-11-19-security-review.md`).
- 시험: .NET 858개(UI 자동 테스트 4개 포함), Rust 185개, 모든 화면 자동 점검 33장, 수동 점검 M1·M3 통과.
- **문서**: README·도움말(F1)은 0.9.1 기준으로 맞춤 (네 묶음, 첫 화면, 화면 크기, 메모 .md·.textpack, 작업 태그, 두 창 비교, AI 템플릿, 금고 새 형식, 이전 버전, 한 줄 제안, 업데이트는 더 보기 안). 결정은 DECISIONS.md, 변경은 CHANGELOG.md(0.3.14~0.9.1), 리뷰는 docs/reviews/(보안 PR #6·#7·#9·#10, L2·L6·L7은 이유와 함께 받아들임), 시험은 TESTING.md.
- **0.9 뒤 계획: 영어·다국어** — 사용자가 읽는 글(화면·메시지·대화 창·둘러보기·도움말)을 리소스 파일(.resx)로 옮기고 영어부터 번역. 원칙은 DESIGN-PRINCIPLES.md "다국어 준비". 로그와 버그 신고는 영어로 통일. 새로 고치는 화면부터 지킨다.
- **다음에 할 일 (1.0 전)**: 사용자가 써 보며 고칠 것 모으기, 다국어 리소스로 옮기기, 미룬 것(직접 태그 만들기·태그로 찾기, 목록 색칠 비교, 사진 정리·백업·이름 정리) 중 필요한 것만.

## 0.3.9 ~ 0.3.11 (2026-11-19 저녁, 0.3.11은 아직 릴리스 전)

- 0.3.9: 사이드바 휴지통 용량·비우기, 사이드바 압축(아이콘 한 줄, 최근 5개), 느린 드라이브는 네트워크·리다이렉터만. 0.3.8 작업 표시줄 고정 유지 확인 완료.
- 0.3.10: 템플릿 10개(Java·Python·Node·.NET·Go, 문서 흐름, 연구, 가정, 디자인, 날짜 보관) + 분류, 위저드 언어 질문.
- main에 들어갔고 0.3.11로 낼 것: Git/프로젝트 폴더는 정리 안 함(빨간 안내만, 옵션 없음), 빈 계획 안내·현재 구조 표시·건너뜀 탭, 새 버전 첫 실행 시 아이콘 캐시 새로 고침, "디스크 분석"→"폴더 분석", 터미널 버튼 Windows 원래 아이콘(+UAC 방패), **색인 서비스 연결 재시도**(0.3초 1회 → 3초×3회 + 30초마다; "색인 서비스 없음" 오표시 수정).
- README 상세 설명서(기능별 화면 26장, `--doc-screenshots` 모드, `docs/images/`)와 도움말(F1) 현행화 완료.
- **다음에 할 일**: 문서 작업 병합 → 빌드·시험 → `Directory.Build.props` 0.3.11 → 태그 → 릴리스 → 회사 PC에서 색인 연결·아이콘 확인.

## 문서 화면 (README·도움말)

- `build/doc-screenshots.ps1`(= `Foldy.exe --doc-screenshots <폴더>`, `ShellWindow.DocShots.cs`)가 가짜 샘플 폴더(C:\FDemo, 임시 데이터 폴더 C:\FoldyDemoData)에서 장면별 PNG를 `docs/images`에 만든다. 화면이 바뀌면 다시 실행한다. 도움말용 작은 그림은 `assets/help/images`.

## 0.3.1 ~ 0.3.8 (2026-11-19, 회사 PC 대응)

- 회사 PC(사내 VDI, UAC 꺼짐, 회사 PAC 프록시, 인증 없음)에서 확인·해결:
  - 회사 보안 드라이브 빈 화면 → 루트 폴더가 System 속성이라 숨김 처리됐던 것. 0.3.5부터 탐색기처럼 Hidden만 숨김. 확인 완료.
  - 업데이트 시간 초과 → 서비스(SYSTEM)는 사용자 PAC를 못 씀 + GitHub API 익명 한도(회사 공용 출구 IP). 0.3.5부터 앱이 사용자 프록시로 `releases/latest/download/update.json`(서명 검사) → MSI 설치(관리자 확인 한 번). 0.3.6 업데이트 버튼으로 설치 성공 확인, 0.3.7 성공.
  - 0.3.7: 최신이면 "최신 버전입니다" 표시, F3 미리보기를 목록 위쪽에 (파일명 잘림 방지).
  - 0.3.8: MSI `MajorUpgrade Schedule="afterInstallExecute"` — 업데이트 뒤 작업 표시줄 고정 유지 (확인 대기).
- 0.3.6: 느린 드라이브 자동 감지(`DriveSpeedTracker`) — 목록 먼저, 크기는 뒤에서, Git·썸네일·아이콘 생략, 미리보기 클릭, "느린 드라이브" 칩. 마스코트는 1.5초부터, Windows 애니메이션 끔(VDI)은 무시. 확인 완료(애니메이션).
- 0.3.1~0.3.3: 관리자로 켜지면 탐색기 통해 일반 권한 재실행(UAC 꺼진 PC는 효과 없음), 버그 진단 로그 강화. 0.3.4의 셸 열거 폴백은 0.3.5에서 제거.
- 남은 것: Rust 서비스도 API 대신 update.json 사용(지금은 API, 회사망에서는 어차피 앱 경로로 대체), 0.3.8 고정 유지 확인.

## 0.3.0 (2026-11-19)

- 원칙 추가: **복잡한 기능은 하지 않는다** (단순·안정 우선). Google Drive 직접 연결은 제거(PR #4), Mermaid 렌더러는 중단.
- 색인 저장: 서비스 재시작 때 저장된 색인을 읽고 USN 저널만 따라잡는다(`docs/index-persistence.md`, 실제 MFT 시간은 관리자로 `foldy-index --bench C:` 측정 필요).
- 1.0 점검(`docs/release-1.0-checklist.md`): 대비 수정, 고대비 테마, 한국어 자동화 이름, Tab/Del/F2 키보드 수정, Ctrl+Shift+E 사이드바, 성능 기준 스크립트 `build/perf-check.ps1`(모두 통과), 스크립트 설치가 MSI 설치를 덮지 않게.
- 0.2.11: 업데이트 뒤 Foldy를 탐색기 토큰으로 다시 띄움(매핑 드라이브 K: 문제), 드라이브 열기 진단 로그, 버그 진단 모드, 로그 폴더 열기.
- 남은 것(사용자): SignPath 승인 대기(2026-11-19 접수 확인 메일 받음, 승인 후 GitHub 변수·시크릿 설정 — `docs/code-signing.md`), 스크린 리더 실사용 확인, K: 컴퓨터에서 0.2.11+ 확인.

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

- **Google Drive 직접 연결은 제거함** (2026-11-19, 사용자 결정: 기능이 복잡해서 Google Drive for desktop의 G: 드라이브를 쓰기로 함). PR #3(022ae61)을 되돌렸다. 다시 필요하면 이 되돌리기 커밋을 `git revert` 하거나 022ae61 기록에서 되살린다.

## 0.2.10 (2026-11-19 13:49 PC 무UAC 업데이트 확인, 창 1개로 정상 재시작)

0.2.8은 CI 시간 측정 시험이 실패해서, 0.2.9는 MSI RTF 한글 이스케이프 버그로 릴리스되지 않았다(태그만 있음). 아래 내용과 SFTP 복사·이동(덮어쓰기 안전, 심볼릭 링크 알림)이 0.2.10에 들어갔다.

## (이전) 0.2.8 준비 중 기록

main에 들어간 것 (642개 시험 통과, 아직 릴리스 전):
- **0.2.7 자동 업데이트도 PC에서 확인했다**: 관리자 창 없이 약 50초, "정상 시작" 기록.
- **파일 목록 열**: 유형 열은 없애고 이름 왼쪽 아이콘으로만 보인다. 남는 폭은 모두 파일명에 준다(파일명이 다 보이는 게 가장 중요하다는 사용자 요청). 그다음 크기(72px), 수정(68px, 0.85배 글자).
- **수정 날짜를 짧게**: 오늘은 `08:22`, 올해는 `11-19`, 지난해 이전은 `24-04-06` (`FileItem.ShortModifiedText`).
- **마스코트 다듬기**: 띠 높이 64px, 높고 긴 3점슛 포물선(`docs/mascot/ball-trajectories.png`), 이름표 겹침 방지, 레트로 말풍선 ?·! 또렷하게.
- **복사 스레드 수 설정 연결** (클라우드).
- **새 아이콘**: 모던 플랫 스타일 (클라우드, 5f677fb).
- **코드 서명 준비**: SignPath 아티팩트 구성 초안, `docs/code-signing.md`, README의 Code signing policy, `PRIVACY.md`, 설치 중 개인정보 정책 표시. release.yml에 SignPath 단계가 있지만, 변수·시크릿이 없으면 건너뛴다. SignPath Foundation 승인을 기다리는 중이다 (클라우드).
- **터미널**: 사용자가 모두 정상이라고 확인했다 (이슈 해결).

- **SFTP 복사·이동**: `SftpAwareFileOperationService`(Foldy.Providers.Sftp)가 sftp:// 경로가 낀 작업만 맡고 로컬끼리는 기존 서비스로 넘긴다. 업로드·다운로드(재귀), 같은 연결 이동=서버 이름 바꾸기, 서버 간 복사=임시 파일 경유, 바이트 진행률, 취소 시 부분 파일 삭제, 충돌 창 재사용, SFTP 삭제 전 확인(`ShellViewModel.ConfirmAsync`). 시험 `SftpTransferTests`(메모리 가짜 서버). 실제 서버 수동 확인은 아직 안 했다. 덮어쓰기는 `.이름.foldy-part` 임시 파일에 다 쓴 뒤 바꿔 넣는다(SFTP는 posix-rename, 미지원이면 삭제 후 이름 바꾸기; 로컬은 File.Move overwrite). 건너뛴 심볼릭 링크 수는 결과 문구로 알린다. 알려진 한계: 탐색기로 끌어 내기는 안 됨(FileDrop에 sftp:// 문자열).

진행 중 (작업 트리 `.claude/worktrees/agent-adc20aa7eab2f277d`, 아직 병합 전):
- **Git 개편**
  - 위쪽 한 줄: 브랜치, 변경 수, 가져오기·받기·올리기·임시 보관 버튼(한국어 이름과 툴팁).
  - 한 화면 커밋: 체크 → diff → 메시지 → [커밋] / [커밋 후 올리기].
  - "기록" 탭.
  - 안내 문구: 변경 없음, 저장소 아님(git init), Git 미설치, 인증 실패.
- **diff / blame 화면**: `Foldy.Git/GitDiff.cs`, `GitBlame.cs` 파서.
- **자동 Git 바**: 저장소 폴더에 들어가면 패널 위에 얇은 바가 나타난다. 설정 "Git 저장소 폴더에서 Git 화면 자동 열기"(기본 꺼짐), "Git 바 보이기"(기본 켜짐).
- 네트워크가 끊겨 중단됐다가 재개했다. 완료되면 병합하고 0.2.8을 릴리스한다.

## 1.0 준비 점검 (2026-11-19, 작업 트리 worktree-agent-adaaf499442372425, 병합 전)
(대비 토큰·고대비 대체·한국어 자동화 이름·키보드 Tab 흐름), 성능 관문(uild/perf-check.ps1, --bench-shell), 설치·제거 정적 검토 결과는 docs/release-1.0-checklist.md. 남은 것: SignPath 승인, 게시본 시작 시간 재측정(300ms 목표), 낭독기·고대비 눈 확인, MSI 가상 머신 시험.

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
| 0.2.7 | (클라우드, 노트북에서 태그·릴리스, PC 무UAC 업데이트 확인) 공통 사이드바(자주 쓰는 폴더·최근·드라이브 용량·도구·설정), 두 패널 모두 폴더 트리, 드라이브 칩·상태바 메시지 가독성, 단일 인스턴스(기존 창 앞으로) |
| 0.2.6 | 새 농구 애니메이션(진행률 = 위치, 11가지 동작, 작업별 마무리), 작업 중에는 업데이트를 예약했다가 끝나면 설치, 앱이 1시간마다 서비스에 확인 요청 |

## 마일스톤

| 단계 | 상태 | 남은 일 |
| --- | --- | --- |
| M0 기반·기술검증 | 완료 | — |
| M1 MVP | 완료 | — |
| M2 Rust 색인·분석 DB·즉시 검색 | 완료 | Subscribe(폴더 변경 알림), 색인 영속화는 구현함(docs/index-persistence.md, MFT 실측 남음), SQLite 목록 캐시, 중복 파일 찾기(file_hash), 예약 분석 |
| M3 Rust 복사 | 완료 | — (CopyThreads 연결함) |
| M4 Git | 핵심 완료 | diff/blame·사용 흐름 개편·자동 Git 바 (진행 중, 0.2.8) |
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
3. ~~터미널 수정(0.2.3)~~ 사용자 확인 완료.
4. 레트로 테마에서 말풍선의 "?", "!"가 잘 안 보인다.
5. 업데이트 채널 일부는 저장만 되고 반영되지 않는다. (복사 스레드 수는 클라우드에서 연결함)
6. 미리보기 셸 처리기는 UI 스레드에서 돈다(전용 STA 스레드는 후속 과제).

## 느린 드라이브 자동 적응

- `DriveSpeedTracker`(Presentation/Services): 드라이브 뿌리(`K:`, `\\서버\공유`)별로 첫 묶음 시간의 EMA(250ms 초과, 표본 2개 이상) 또는 한 번 2초 초과면 느림. 저장 안 함, 설정 없음.
- 느린 드라이브: Git 감지 건너뜀, 파일별 아이콘·썸네일 내용 읽기 중단(`CloudFileGuard.IsSlowDrivePath`), 미리보기는 네트워크처럼 [미리보기] 클릭 필요(`PreviewGuards.IsRemotePath`), 폴더 감시는 폴링. 복사 엔진은 그대로.
- 폴더 크기는 목록을 그린 뒤 `Task.Run`으로, 폴더를 떠나면 취소. 경로 표시줄 옆 "느린 드라이브" 칩, 처음 느려질 때 상태 줄 한 번.

## 다음 할 일 (순서대로)

1. ~~단일 인스턴스~~ (클라우드에서 구현, 위 참고). Windows에서 시험·실제 확인 후 0.2.7 릴리스.
2. ~~터미널·관리자 모드 확인~~ (2026-11-19 사용자 확인: 모두 정상)
3. ~~마스코트 다듬기~~ (main 반영). 남은 것: 아이를 0.8배보다 키우기(손·공 위치를 다시 맞춰야 함), 일시정지 장면 확인.
3-1. Git 개편 마무리와 병합 → 0.2.8 릴리스 → PC 업데이트 확인.
4. ~~CopyThreads 설정을 복사 엔진에 연결~~ (클라우드, 0.2.8 예정): 작업을 시작할 때마다 설정값을 읽는다(`NativeCopyFileOperationOptions.ThreadsProvider`).
5. M6 출시 준비: 1.0 체크리스트(접근성, 성능 회귀, 설치·제거, SignPath 코드 서명), Git diff/blame, 색인 영속화.
6. 0.3.0 릴리스.

## 재개 방법 (세션 정보)

- **프로젝트**: Claude 프로젝트 "Foldy". 작업 스레드는 "다기능 파일매니저 (윈도우용)"이고, 이 스레드에서 이어서 말하면 된다.
- **Claude Code 세션**: 로컬 transcript는 `%USERPROFILE%\.claude\projects\C--WorkSpace-Foldy\06fb3c7c-e696-4feb-a9ca-dbc84dc367d8.jsonl`. 프로젝트 메모리는 `%USERPROFILE%\.claude\projects\C--WorkSpace-Foldy\memory\foldy-project.md`.
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
