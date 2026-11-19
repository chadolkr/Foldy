# Foldy 1.0 준비 점검 결과

점검일 2026-11-19, 브랜치 `worktree-agent-adaaf499442372425`, 기준 버전 0.2.10. 항목별로 "점검 → 결과 → 고친 곳"을 적는다.
코드 수정은 한 커밋(접근성)과 한 커밋(성능 도구·설치)으로 나뉜다. 커밋 해시는 이 문서가 들어간 브랜치의 `git log`로 확인한다.

## 1. 접근성 (WCAG 2.1 AA, 데스크톱 앱)

| 점검 | 결과 | 고친 곳 |
| --- | --- | --- |
| 글자 대비 4.5:1 (모던 라이트·다크·레트로) | 실패 9건: 라이트 TextSecondary(Pressed 위 4.24), Done(4.37), 강조색 위 흰 글자 2.46; 레트로 Error·Done(Surface 위 3.56 / 2.82) | `Modern.Light.xaml`, `Retro.xaml` 토큰 조정. 시험 `ThemeContrastTests`가 테마 XAML 색에서 직접 계산(본문·보조·상태색·선택·강조 위 글자·탭 색, 비활성 글자는 WCAG 면제) |
| 포커스 링 대비 3:1 (1.4.11) | 라이트에서 주황 포커스 2.28~2.46 실패 | 라이트 `Brush.Focus` #A65300. Windows 강조색을 쓸 때도 `AccentColor.EnsureContrast`로 3:1 보정, 강조색 위 글자는 WCAG 대비로 선택(`TextOn`) |
| 아이콘만 있는 버튼의 한국어 이름 | 툴팁만 있고 UIA 이름은 아이콘 글리프(약 25곳) | `Foldy.ToolButton` 스타일이 ToolTip을 AutomationProperties.Name으로 묶음(개별 지정이 우선). 사이드바 항목 전부, 핀·최근 지우기·작업 취소, Git 바 버튼(가져오기·받기·올리기·임시 보관·브랜치·옵션), 디스크 분석 위로 버튼에 명시 이름 |
| 키보드로 사이드바·버튼 도달 | 실패: `Tab`이 창 전체에서 "다른 패널로 이동"에 묶여 버튼으로 들어갈 수도 지나갈 수도 없었음. 버튼에 포커스가 있을 때 Del/F2도 파일 작업을 실행 | `ShellWindow.OnWindowKeyDown`: 버튼 포커스 중에는 Tab/Del/F2를 가로채지 않음, `Esc`는 목록으로 복귀. 새 명령 `Ctrl+Shift+E`(사이드바로 포커스 이동) |
| 보이는 포커스 링 | 입력칸(경로 등)에 없음, 레트로는 테두리 1px 두께 차이뿐, 탭의 닫기 버튼은 키보드 포커스에서도 투명 | `Foldy.InputBorder` 포커스 트리거, 레트로 점선 포커스 사각형, 탭 닫기 버튼 포커스 시 표시 |
| Esc로 대화상자 닫기 | 설정·정보·SFTP·Google Drive·템플릿·위저드·정리·충돌 창은 이미 IsCancel. 오류 보고 동의 창만 없었음 | 오류 보고 동의 창: Esc = "보내지 않기" |
| FastFileList 라이브 영역 | 정상 (행 단위 피어 없이 목록 이름 + `LiveRegionChanged`, Polite) | 시험 `FastFileListAccessibilityTests` 추가(위치 "3/5", 선택됨, List 피어, 포커스 가능) |
| 고대비 모드 | 없었음(고정 색이라 고대비에서 시스템 색을 무시) | `HighContrastTheme`: `SystemParameters.HighContrast`가 켜지면 모든 `Brush.*`를 SystemColors로 덮어씀(레트로·강조색보다 우선), 설정 변경 시 즉시 다시 적용. 시험 `HighContrastThemeTests` |

한계(남은 것): 고대비에서는 호버 구분이 약하다(배경이 Window/Control 같은 색). 마스코트·치료맵·Git 그래프 등 색에 의미가 있는 그림은 고대비 색으로 다시 칠하지 않았다. 실제 Narrator·NVDA 낭독은 이 PC에서 확인하지 못했다(UIA 속성·피어는 시험으로만 확인).

## 2. 성능 회귀 관문

도구: `build/perf-check.ps1` (Release 빌드 뒤 실행). 진짜 셸 창(사이드바·폴더 트리·Git 바 포함)에서 `Foldy.exe --bench-shell --bench-folder <10만 개 폴더>`를 3번 돌려 중앙값을 쓰고, 비교용으로 사이드바 없는 `--bench-list`(BenchWindow)도 잰다. `FOLDY_DATA_DIR`로 임시 설정 폴더를 써서 사용자 세션을 건드리지 않는다. 종료 코드: 0 통과, 2 미달, 3 환경 탓으로 판정 보류.

| 관문 | 기준 | 측정 (2026-11-19, UHD 620, 60Hz, 렌더 등급 2) | 결과 |
| --- | --- | --- | --- |
| 목록 스크롤 p95 (120행/프레임, 10만 행) | <= 16.7ms (프레임 타임스탬프 지터 0.5ms 허용) | 셸: 평균 16.7 / p95 16.7 / p99 16.7ms, 늦은 프레임 0.1~0.2%. BenchWindow: p95 16.7ms, 평균 16.9ms | PASS |
| 10만 개 폴더 첫 묶음 | < 100ms | 28.9ms (전체 열기 361ms) | PASS |
| 시작 → 첫 그리기 | 회귀 방지선 2500ms (설계 목표 "조작 가능 300ms") | 첫 그리기 1742ms, 조작 가능 1759ms (비 ReadyToRun 개발 빌드) | PASS(방지선) / 목표 300ms는 **미달** |
| 10만 개 연 뒤 메모리 | 작업 집합 400MB 이하 (회귀 방지선) | 작업 집합 230MB, private 144MB, 관리 힙 44MB (시작 직후 222MB) | PASS |

- 새 사이드바·트리·Git 바는 스크롤과 폴더 열기를 느리게 하지 않았다(셸과 BenchWindow가 같은 16.7ms). 고칠 회귀는 없었다. `FolderTabViewModel.FirstBatchTime`을 추가해 첫 묶음 시간을 측정한다.
- 시작 시간은 300ms 설계 목표에 한참 못 미친다. 개발 빌드 기준이라 게시본(ReadyToRun, 자체 포함)은 더 빠를 것이다. 게시본으로 다시 재고 그래도 길면 시작 작업(세션 복원, 서비스 연결, 드라이브 목록)을 첫 그리기 뒤로 미루는 작업이 필요하다. 1.0 전 확인 항목.
- 측정 주의: 화면이 잠겨 있으면 WPF 프레임이 약 4Hz(220~290ms)로 떨어진다. 점검 중 한 번 겪었고, 그때는 코드 변경이 없는 main 빌드의 BenchWindow도 평균 293ms였다. 잠금이 풀린 뒤에는 위처럼 통과했다. 스크립트는 잠금 화면(LogonUI), 유휴 프레임 40ms 초과, 스크롤 측정 시간 초과일 때 스크롤 관문을 INCONCLUSIVE로 표시한다. UIA 이벤트를 듣는 도구가 있으면 참고로 알린다(이 PC에도 있음).
- 유휴 프레임 p95는 33.3ms다. 마스코트 애니메이션의 30fps 상한 때문이며 정상이다.

## 3. 설치·제거 (정적 검토, 실제 설치·제거는 하지 않음)

검토 대상: `build/setup/Foldy-Setup.ps1`(스크립트 설치본), `build/installer/Foldy.wxs`(MSI). MSI는 `C:\ProgramData\Foldy\Updates\Foldy-0.2.10-x64.msi`를 임시 폴더로 복사해 `msiexec /a`(관리자 추출, 설치 아님)로 풀고 테이블을 읽었다. 443개 파일이 `PFiles64\Foldy`로 풀렸고 foldy-index.exe, foldy-update.exe, LICENSE, PRIVACY.md 포함. 설치된 Foldy와 FoldyIndexer 서비스는 건드리지 않았다.

| 항목 | MSI | 스크립트 설치본 |
| --- | --- | --- |
| 프로그램 추가/제거 | ARP 정보·아이콘·링크, 수정 버튼 없음(ARPNOMODIFY). 정상 | `Uninstall\Foldy` 키에 이름·버전·게시자·아이콘·제거 명령. 정상 |
| 시작 메뉴 바로가기 | `Shortcut`(Advertise 아님), 제거 시 함께 삭제 | `CommonPrograms\Foldy.lnk`, 제거 시 삭제. 바탕 화면 바로가기 없음 |
| 색인 서비스 | `ServiceInstall` + `ServiceControl`(Stop·Remove=both), 지연 자동 시작, 실패 시 재시작. 제거 때 서비스 삭제됨 | 정지 후 `sc delete`. 정상 |
| 스크립트 → MSI 전환 | 스크립트가 남긴 `Uninstall\Foldy` 키와 `Foldy-Setup.ps1` 삭제, 같은 이름 서비스는 교체 | — |
| 남는 파일 | `%ProgramData%\Foldy`(분석 DB, 업데이트 상태·기록·MSI, 폴더 설명 캐시)는 MSI 제거 때 **남는다** | 제거 시 `%ProgramData%\Foldy`까지 지운다 |
| 사용자 데이터 | `%LOCALAPPDATA%\Foldy`(설정, 세션, 사이드바, 단축키, 연결 정보, 로그, 썸네일 캐시)는 **기본으로 유지** | 같음(유지) |

고친 것:
- 스크립트 설치·제거가 MSI 설치본 위에서 돌면 파일만 덮어쓰거나 지워서 Windows Installer 등록이 고장 난 채 남는다(자동 업데이트가 스크립트 설치본을 MSI로 바꾸므로 실제로 생길 수 있음). 이제 HKLM 제거 목록에서 `WindowsInstaller=1`인 Foldy를 찾으면 안내만 하고 끝낸다.

판단을 남기는 것(바꾸지 않음):
- MSI 제거가 `%ProgramData%\Foldy`를 남기는 것: 내용은 다시 만들 수 있는 캐시·기록이다. 정리하려면 `RemoveFolderEx`가 필요한데, 주요 업그레이드가 옛 제품을 제거할 때도 실행돼 자동 업데이트가 쓰는 `Updates\*.msi`를 지울 수 있다. 가상 머신에서 설치·업그레이드·제거를 실제로 돌려 확인하기 전에는 넣지 않는다.
- 실행 중인 Foldy.exe가 있는 채 MSI를 제거하면 파일 잠금 때문에 재시작 요청이 뜰 수 있다(`CloseApplication` 미사용). 자동 업데이트 경로는 서비스가 처리하므로 수동 제거에서만 해당.
- 사용자 데이터 정책: 제거는 아무것도 묻지 않고 `%LOCALAPPDATA%\Foldy`를 유지한다. 완전 삭제를 원하면 그 폴더를 직접 지운다(도움말·README에 안내 문구가 있으면 좋다).

## 4. 검사 방법과 도구 목록

- 시험: 전체 `dotnet test`(접근성 시험 포함) 통과.
- 도구: `build/perf-check.ps1`, `Foldy.exe --bench-shell`(`ShellWindow.Bench.cs`), `FOLDY_DATA_DIR`(설정 폴더 바꾸기).

## 5. 사용자가 해야 하거나 정해야 하는 것

- [ ] **SignPath Foundation 승인**과 GitHub 변수·시크릿 연결(`docs/code-signing.md`). 승인 전에는 설치 파일이 서명되지 않아 SmartScreen 경고가 뜬다.
- [ ] 잠금을 푼 상태에서 `build/perf-check.ps1`을 게시본(ReadyToRun) 기준으로 한 번 더 실행하고 시작 시간을 다시 잰다(설계 목표 300ms).
- [ ] 실제 화면 읽기 프로그램(Narrator 또는 NVDA)으로 사이드바 → 목록 → 설정 → Git 바를 한 번 훑어 낭독을 확인한다.
- [ ] Windows 고대비 테마(예: 대비 테마 → 야간)를 켜고 화면이 쓸 만한지 눈으로 확인한다.
- [ ] 가상 머신에서 MSI 설치 → 업그레이드 → 제거를 한 번 돌려 위 `%ProgramData%\Foldy` 정리 여부를 정한다.
- [ ] Google Drive 로그인(OAuth 클라이언트 ID)과 실제 SFTP 서버 복사·이동은 아직 실사용 확인 전(PROGRESS.md 참고).