# 테스트

## 자동 테스트

- `./build.ps1` 또는 `dotnet build Foldy.slnx -c Release` 후 `dotnet test --solution Foldy.slnx -c Release --no-build`. Rust는 `cargo test` (engine 폴더).
- **CI**: `.github/workflows/ci.yml`이 main 푸시와 모든 PR에서 **Windows**(windows-latest)로 Rust·.NET 빌드와 전체 테스트를 돌린다. 릴리스(`release.yml`)도 Windows에서 테스트를 통과해야 설치 파일을 만든다.
- 클라우드(Linux) 세션에서는 Windows 경로·셸을 전제로 한 테스트가 실패한다. 그 결과로 판단하지 말고 CI(Windows) 결과를 본다.
- 2026-11-19 기준 820개 이상 (Core: 정리 규칙·로그 zip·다운로드·바탕화면·관리 방식·금고/잠금 암호화·zip slip·이전 버전 보관, Interop: 색인·업데이트 파이프, Templates, UI 등).

- **언어별 시험 (2026-11-22, 사용자 "언어별로 테스트를 해줘")**:
  - `LanguageTests`(UI): en·es·ja·zh-Hans로 Foldy를 띄워 주 창과 설정 창에 보이는 글(화면 낭독기 이름·도움말)에 한국어가 남았는지 본다. 폴더 트리(Windows가 주는 폴더 이름)와 제목 표시줄(Windows가 그 PC 언어로 그림)은 뺀다. 찾은 글은 `TestResults/language-<언어>.txt`, 본 글 전부는 `language-<언어>-all.txt`(검수·글자 잘림 확인용), 설정 창 사진은 `language-<언어>-settings.png`. 주 창은 실제 드라이브가 보이므로 찍지 않는다(개인 폴더 이름 노출 방지). 주 창 사진은 `build/doc-screenshots.ps1 -Lang <언어>`(예시 폴더만 보임)로 본다.
  - `LanguageFolderTests`(Templates): 네 언어마다 내장 템플릿 19개를 계획해 폴더·파일 이름에 한국어가 없는지, 정리 규칙 대상이 트리 이름과 맞는지, 위저드 질문이 번역됐는지, 바탕화면 보관·종류 폴더가 그 언어 이름이고 이미 있는 한국어 폴더는 그대로 쓰는지 본다.
  - `Every_key_used_in_code_is_in_the_language_files`(Core): 코드·XAML이 쓰는 키가 언어 파일에 다 있는지.
- **불안정 시험 대응 (2026-11-22)**: 화면 시험의 `Retry.While*`는 모두 `ignoreException: true`(UI 자동화가 가끔 COMException E_UNEXPECTED를 낸다). 복사 취소 시험은 "반쪽 파일이 남지 않는다"만 본다(빠른 디스크에서는 취소가 닿기 전에 다 복사될 수 있다).
- 다국어: `LocalizationTests`(키·자리 표시자·남은 한국어, 언어 고르기). UI 시험은 FOLDY_LANGUAGE=ko로 돈다(CI는 영어 Windows). 영어 화면 확인은 `FOLDY_LANGUAGE=en build/doc-screenshots.ps1 -Out <dir>`.

## 수동 점검표 (릴리스 전에 Windows에서)

자동으로 하기 어려운 것만 남긴다. 일반 릴리스(0.x.y)는 자동 테스트만 통과하면 배포하고, 이 점검표는 큰 릴리스(0.9·1.0) 전이나 관련 코드가 바뀌었을 때만 돌린다. 결과는 아래 기록에 날짜와 함께 적는다. 자동으로 옮길 수 있게 되면 테스트로 옮기고 여기서 지운다.

| # | 점검 | 단계 | 기대 결과 |
| --- | --- | --- | --- |
| M1 | 서비스 없는 업데이트 (UAC) | 관리자 PowerShell에서 `Stop-Service FoldyIndexer` → 이전 버전(예: `-p:Version=0.3.0`으로 빌드, 옆에 `foldy-update.exe` 복사) 실행 → … → 업데이트 확인 → UAC 예 | 최신 MSI가 설치되고, **설치된 Foldy**가 다시 뜬다. 서비스는 설치 프로그램이 다시 켠다 |
| M2 | 서비스 업데이트 (UAC 없음) | 설치본 이전 버전 실행, 서비스 켜 둔 채 업데이트 | UAC 없이 설치, 작업 중이면 끝난 뒤 설치 |
| M3 | 파일 잠그기 → 원본 바로 지우기 | 시험용 txt 오른쪽 클릭 → 🔒 잠그기 (암호) → 암호 두 번 → "원본 바로 지우기" | `.foldylock`만 남고 원본은 휴지통에도 없음 |
| M4 | 잠긴 메모 | 지키기 → 새 잠긴 메모 → 글·그림(Ctrl+V) → Ctrl+S → 닫고 다시 열기 | 암호 물음, 글·그림 그대로. 5분 손대지 않으면 저장 후 닫힘 |
| M5 | 금고 | 폴더 오른쪽 클릭 → 금고에 넣기 → 열기 → 미리 보기·꺼내기 | 파일 이름이 암호 없이 안 보임, 꺼낸 내용 같음 |
| M6 | 탐색기 연결 | 탐색기에서 `.foldylock`·`.foldyvault` 두 번 누르기 | Foldy가 열리고 암호를 물음 |
| M7 | 화면 크기 (일부 자동: review-large 장면) | Windows 설정 → 접근성 → 텍스트 크기 150% → Foldy | 바로 커짐. Ctrl+= / Ctrl+- / Ctrl+0 동작 |
| M8 | 작은 화면 (**자동**: UI 테스트 `Small_laptop_screen_keeps_every_sidebar_tool_visible`, review-small 장면) | 1366×768에서 실행 | 사이드바 스크롤 없음, 좁은 창은 트리 자동 접힘, 경로 보임 |
| M9 | 회사 보안 드라이브·느린 드라이브 | 느린 네트워크 드라이브 열기 | 목록 먼저, 느린 드라이브 표시, 멈추지 않음 |
| M10 | 회사 프록시(PAC) 뒤 업데이트 | 사내망에서 업데이트 확인 | 사용자 프록시로 확인·설치 |
| M11 | 빠른 검색 전역 단축키 (일부 자동: UI 테스트 `Ctrl_space_opens_quick_search_and_calculates`는 Foldy 창 안에서만) | 메모장을 앞에 두고 Ctrl+Space → "디스" → Enter / "보고서" → Ctrl+Enter / 다른 프로그램이 Ctrl+Space를 잡은 상태로 처음 설정 | 어디서든 창이 뜸, 디스플레이 설정이 열림, Foldy에서 들어 있는 폴더가 열림. 겹치면 다른 키 후보와 [직접 눌러서 정하기]가 나옴 |
| M13 | 보통 권한에서 색인 연결 (일부 자동: `Installed_service_is_trusted_when_it_is_running`은 서비스가 있는 PC에서만) | 관리자가 아닌 Foldy를 켜고 상태 줄 확인, Ctrl+Space로 다른 드라이브에 있는 파일 이름 검색 | "검색: 색인 사용", 다른 드라이브 파일도 찾음 |
| M14 | 업데이트 진행 창 (기록 문구는 자동: `UpdateProgressTextTests`) | 설치본에서 [재시작하고 업데이트] / 다른 Foldy 창을 하나 더 띄운 채로 누르기 | 작은 창이 단계를 보이고 새 Foldy가 뜨면 닫힘 / "다른 Foldy 창을 모두 닫아 주세요" |
| M15 | 회사 드라이브 살살 모으기 (알고리즘은 자동: `SlowCrawlerTests`) | 회사 PC에서 Foldy를 켜 두고 K: 옆 "색인 중 · N개"가 늘어나는지, 다 모은 뒤 다른 사람이 만든 파일이 Ctrl+Space로 찾히는지(폴더 시각 갱신), 직접 만든 파일이 바로 찾히는지(변경 알림), 보안 프로그램 경고가 없는지, 복사 중엔 멈추는지 | 모두 그렇다. 안 되면 결정 기록에 적고 조정 |
| M16 | AI 연결 (프로토콜·설정 파일·Claude 설정 위치·앱별 안내 키는 자동: `McpServerTests`, `McpClientConfigTests`(Claude-Data·옮겨 간 폴더·여러 판·Antigravity·Claude Code와 구별), `McpSuggestTests`) | 더 보기 → AI 연결 → Claude Desktop 체크 → [AI에 연결] → 아래 "Claude Desktop에서 Foldy 쓰기" 안내 확인 → [Claude 다시 시작] → [끄고 다시 켜기] → 새 대화에서 "C 드라이브 사용량을 보여 줘" / "AI 연결 켜기" 끄고 다시 묻기 / 체크 풀고 [AI에 연결] | 안내가 화면 언어로 보임, Claude가 꺼졌다 다시 켜지고 Claude Code 명령줄은 그대로 / Foldy 도구로 답함 / "꺼져 있어요" / 설정 파일에서 foldy만 빠짐, .foldy-backup 있음 |
| M17 | 개발자 정리 (판별·명령은 자동: `RebuildableFoldersTests`, `DevCachesTests`, `FileLockersTests`) | 실제 프로젝트 폴더에서 정리하기 → 지워도 되는 폴더 찾기 / Excel로 연 파일을 Delete / 정리하기 → 개발 캐시 정리에서 npm만 정리 | node_modules·bin이 크기 순, .git은 체크 안 됨·빨간 글씨 / 작업 창에 "Microsoft Excel이(가) 쓰고 있어요" / npm 캐시가 줄고 다음 npm install이 정상 |
| M18 | 미리보기: DRM·암호로 보호된 파일 (실제 DRM 파일 확인 2026-11-22, 1.0.1) (판정은 자동: `FileProtectionTests`, `PreviewProtectionTests`) | 회사 문서 보안(DRM)이 걸린 Office·한글·PDF 파일을 고르고 F3 / 암호 건 docx를 고르고 F3 / 보통 docx·pdf | "DRM으로 보호된 파일이라 미리 볼 수 없어요" / "암호가 걸린 파일이라…" / 평소처럼 미리보기 |
| M12 | 빨리 지우기 (엔진은 자동: `FastDeleteTests`) | 파일 1만 개 시험 폴더 → Delete → [빨리 지우기] → 확인 / 중간에 [취소] | 기본 단추가 휴지통·그만두기, 빠르게 지워짐, 취소하면 멈추고 남은 것은 그대로 |

## 기록

| 날짜 | 버전·브랜치 | 점검 | 결과 |
| --- | --- | --- | --- |
| 2026-11-19 | PR 7 (0.3.0 빌드) | M1 | 통과: 0.3.23 설치, 서비스 다시 켜짐. 발견: 설치 뒤 시험판 exe가 다시 떠서 "최신"이라고 잘못 보임 → PR 7에서 설치된 Foldy를 띄우도록 고침 |
| 2026-11-19 | PR 7 (0.3.0 빌드) | M3 | 통과: 잠근 뒤 "원본 바로 지우기"로 원본이 바로 지워짐 (사용자 확인) |
| 2026-11-20 | 0.9.9 전 | 사용자 사용 | 발견: 왼쪽 패널 파일이 안 보임 → 이미지만 보기가 켜져 있었음(그림 없는 폴더). 안내 줄·[모든 파일 보기] 추가, UI 테스트 `Images_only_mode_says_so_and_turns_off_in_place` |
| 2026-11-20 | 0.9.6 전 | 자동 전체 | 발견: 다른 시험과 함께 돌 때 UI 시험 2개(버튼 크기, 작은 화면)가 가끔 실패 — 시작 직후 배치가 바뀌는 중에 잼 → 깨끗해질 때까지 다시 재도록 고침. 통과 924 |
| 2026-11-20 | 0.9.5 전 | 사용자 사용 | 발견: 설정 → 복사 "스레드", 터미널 "프로그램"이 자음만 보임 → 모든 화면 Ideal로, `HangulRenderingTests` 추가 |
| 2026-11-20 | 0.9.4 뒤 | 자동 (CI) | 발견: 업데이트 파이프 시험이 가끔 실패 — 서비스가 시험용 설치 기록을 쓰는 순간에 읽으면 파일이 잠겨 있었음 → 쓰기 공유를 허용해 읽고 덜 써진 줄은 다음 차례에 세도록 고침 |
| 2026-11-20 | 0.9.4 전 | M13 | 발견: 0.9.3에서 보통 권한 Foldy가 색인 서비스를 거절(빠른 검색이 지금 폴더만 찾음) → 서비스 관리자 확인으로 고침, 개발 PC에서 실제 서비스 연결·한글 낱말 검색 확인 |
| 2026-11-20 | 0.9.3 전 | 자동 전체 | 통과: .NET (UI 6 포함, 빠른 검색·계산·단위 변환·말하듯 찾기·빨리 지우기 새 테스트), 화면 자동 점검 (빠른 검색 4장·많은 파일 지우기 포함) |
| 2026-11-20 | 0.9.0 전 | 자동 전체 | 통과: .NET 858 (UI 4), Rust 185, 화면 자동 점검 33장 |
| 2026-11-19 | 0.3.21 | 둘러보기 | 발견: 작은 글씨에서 "드·그" 가로획이 덜 그려짐, [다음] 말풍선이 쌓임 → 고침 |
