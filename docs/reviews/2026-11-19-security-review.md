# 보안·코드 리뷰 (0.9 출시 전, 2026-11-19)

기준: main `f87a9fd` (0.3.18). 0.3.19 이후 수정은 아래 "상태"에 적는다.
범위: 업데이트(update.json·서명·MSI 설치), SYSTEM 색인 서비스 IPC, 금고·잠긴 메모, 파일 작업(zip 풀기·링크·삭제·SFTP), 버그 신고 개인정보, desktop.ini·.foldy.md, 셸 연동.

## 상태 요약

| # | 등급 | 내용 | 상태 |
|---|---|---|---|
| C1 | 심각 | 금고에서 골라 꺼내기가 폴더 밖에 쓸 수 있음 (zip slip) | 고침: 0.3.19 `SafeTarget` + PR #6에서 `:`·`\`로 시작·폴더 자신 거부 추가 |
| H1 | 높음 | 압축본에서 색인 서비스를 설치하면 사용자 폴더 exe가 SYSTEM으로 돎 | 고침: PR #6 (Program Files·Windows 아래 exe만 허용) |
| H2 | 높음 | 금고 암호 최소 4자 + 표준 zip 키 만들기(PBKDF2-SHA1 1,000회)가 약함 | 고침: PR #6 (형식 2: PBKDF2-SHA256 60만 회, 새 암호 8자 이상) |
| H3 | 높음 | 금고 안 파일 이름·폴더 구조가 암호 없이 보임 | 고침: PR #6 (형식 2: 이름은 암호화한 목록 안에만) |
| H4 | 높음 | OneDrive "필요할 때 받기" 파일이 금고에서 빠진 채 원본을 휴지통으로 보낼 수 있음 | 고침: PR #6 (모두 넣고, 빠진 게 있으면 원본을 지우자고 하지 않음) |
| H5 | 높음 | 공개 버그 신고에 프록시 주소·IP·서버 이름·문서 이름이 남을 수 있음 | 고침: PR #6 (앱·릴레이 지우개 규칙 추가, 프록시 주소 기록 안 함). 릴레이는 `wrangler deploy` 해야 반영 |
| M1 | 중간 | 서비스 없이 업데이트할 때 사용자 폴더의 MSI를 관리자 권한으로 설치 (바꿔치기 가능, PowerShell 따옴표 문제) | 고침 (2차 PR): 실행 직전 해시 재확인, 관리자만 쓸 수 있는 폴더로 복사해 다시 확인한 뒤 설치, 경로 따옴표 처리 |
| M2 | 중간 | 색인 검색이 다른 사용자·관리자 폴더의 파일 이름도 보여 줌 | 남음 (요청한 사용자 확인이 필요해 Windows에서 시험하며 따로 진행) |
| M3 | 중간 | 파이프 보안 설정 실패 시 기본 보안으로 열림 | 고침 (2차 PR): 실패하면 파이프를 열지 않음 |
| M4 | 중간 | 업데이트 폴더 보안 설정이 실패해도 업데이트를 계속함 | 고침 (2차 PR): 이번 실행은 자동 업데이트를 끄고 이유를 보여 줌 |
| M5 | 중간 | Markdown 링크: 위험 확장자 목록 방식, UNC 링크 클릭 시 로그인 정보(NTLM) 전송 | 고침 (2차 PR): 안전한 확장자만 바로 열기, 네트워크 링크는 묻고 건드리지 않음, 네트워크 그림은 불러오지 않음 |
| M6 | 중간 | 로그 정리의 월별 zip을 제자리 수정 (중간에 꺼지면 이전 로그까지 손상) | 고침 (2차 PR): 새 파일에 쓰고 확인한 뒤 바꿔 넣음 |
| M7 | 중간 | 잠근 뒤 원본을 휴지통으로만 보냄 (비우기 전까지 평문이 남음) | 고침 (2차 PR): "원본 바로 지우기" 선택, 휴지통을 비워야 한다는 안내 |
| L1 | 낮음 | 바뀐 호스트 키 확인 창의 기본 버튼 | 고침 (2차 PR) |
| L5 | 낮음 | 금고 크기 계산 | 고침 (H4와 함께) |
| L2~L4, L6, L7 | 낮음 | 아래 참고 | 남음 (0.9 뒤 검토) |

공개 이슈 #1·#2·#5를 확인했다 (2026-11-19). 프록시 주소·IP·서버 이름은 없었다. #5 로그에는 드라이브 문자·세션 번호 같은 PC 정보만 있다.

## 잘 되어 있는 것

- update.json: 서명 검사가 해석보다 먼저, `verify_strict`, 태그와 매니페스트 버전 일치, stable에서 beta 거부, 낮은 버전 거부(되돌리기는 서비스 안에서만), 자산 이름 검사, 크기 상한.
- 설치: 파이프 메시지에 경로가 없음, `System32\msiexec.exe` 고정, 업데이트 폴더의 링크 제거·소유자 확인·ACL, 설치 직전 쓰기 금지로 열고 해시 재확인.
- 파이프: 네트워크 거부, 첫 인스턴스 고정, 원격 클라이언트 거부, 세션은 파이프에서 확인.
- SFTP: known_hosts 확인, 처음·바뀐 키는 지문을 보여 주고 물음, 비밀번호는 DPAPI, 명령줄에 비밀번호 없음, 원격 경로 따옴표 처리.
- 일반 삭제는 IFileOperation이라 휴지통이 안 되는 곳은 Windows가 직접 확인을 묻는다.

## 높음 (자세히)

### H1. 사용자 폴더의 exe를 SYSTEM 서비스로 등록
- 위치: `src/Foldy.App/ShellWindow.xaml.cs` `RunIndexerSetup`, `src/Foldy.Platform/SystemInfo/IndexerService.cs`
- 쉬운 설명: 다운로드 폴더에 푼 Foldy에서 서비스를 설치하면, 그 폴더의 `foldy-index.exe`를 아무 프로그램이나 바꿔 놓을 수 있고 다음 시작 때 최고 권한으로 실행된다.
- 고친 것: `IndexerService.IsProtectedLocation`으로 Program Files·Windows 아래만 허용. 테스트 `IndexerServiceTests`.

### H2·H3. 금고 암호 강도와 파일 이름
- 위치: `src/Foldy.Core/Vault/SecureZip.cs`, `src/Foldy.App/GuideWindow.xaml.cs`
- 쉬운 설명: 표준 zip 암호는 규격상 1,000번만 다져서 그래픽카드로 1초에 수백만 개를 시험할 수 있다. 목록은 암호화되지 않아 이름이 보인다.
- 고친 것 (사용자 결정 2026-11-19: 7-Zip 호환보다 보안): 형식 2.
  - `foldy-secure.json` (암호화 안 함): `{"format":2,"kdf":"pbkdf2-sha256","iterations":600000,"salt":"…"}`
  - `index` (AES-256): 원래 이름·크기·시각 목록
  - `d/000001` … (AES-256): 내용
  - zip 암호는 사용자 암호가 아니라 PBKDF2-HMAC-SHA256(암호, 소금, 60만 회)의 16진수. 반복 횟수가 10만~1,000만 밖이면 열지 않는다(약하게 속이거나 앱을 멈추게 하는 파일 방지).
  - 0.3.18까지의 표준 zip 파일도 그대로 열린다. 새 암호는 8자 이상.
- 남은 점: 크기는 여전히 보인다. 7-Zip으로는 새 파일을 열 수 없다.

### H4. OneDrive 파일이 빠진 채 원본 삭제
- 위치: `VaultMaker`(`SecureZip.cs`), `SecureFlows.PutInVaultAsync`
- 쉬운 설명: "필요할 때 받기" 파일은 재분석 지점 표시가 있어 예전 코드가 조용히 건너뛰었다. 원본을 휴지통으로 보내면 OneDrive가 클라우드에서도 지운다.
- 고친 것: 폴더 링크만 건너뛰고 파일은 모두 넣는다. 건너뛴 것이 있거나 금고 항목 수가 다르면 원본을 지우자고 하지 않는다. 테스트 `Vault_reports_folder_links_so_the_original_is_not_deleted`.

### H5. 버그 신고의 회사·서버 정보
- 위치: `src/Foldy.Reporting/ReportScrubber.cs`, `tools/crash-relay/src/scrub.ts`, `ShellWindow.xaml.cs` `DescribeUserProxy`
- 쉬운 설명: 신고에는 최근 로그가 붙는데, 로그에 프록시 주소가 찍혔고 지우개는 URL·IP·서버 이름을 지우지 않았다.
- 고친 것: `<url>`, `<ip>`(IPv4·IPv6), `<account>`(user@서버), `<host>`(.local·.corp·.lan·.internal 등), 서버 경로(`/home/…`)를 지운다. 문서 이름은 확장자만 남긴다(`<file>.docx`, Foldy 소스·실행 파일 이름만 유지). 프록시는 "시스템 프록시 사용"만 기록. 앱·릴레이 테스트에 같은 사례를 넣었다.

## 중간

- **M1** `src/Foldy.App/Services/AppUpdater.cs`: 확인과 설치 사이에 사용자 폴더의 MSI가 바뀌면 관리자 권한으로 실행된다. UAC 창에는 "Windows PowerShell"이 보인다. 경로에 `'`가 있으면 명령이 깨진다. → PowerShell 없이 `msiexec.exe`를 `runas` + 인자 목록으로 실행, 실행 직전 쓰기 금지로 열고 SHA-256 재확인.
- **M2** `engine/foldy-index/src/query.rs`: 서비스는 SYSTEM으로 전체 드라이브를 색인하고, 로그온한 누구에게나 결과를 준다. → 요청한 사용자 기준으로 거르기(가장 후 권한 확인) 또는 다른 사용자 프로필 제외, 도움말에 명시.
- **M3** `engine/foldy-index/src/server.rs`: ACL 실패 시 기본 보안으로 파이프를 연다. → 실패하면 열지 않기.
- **M4** `engine/foldy-index/src/update/mod.rs` `startup`: `secure_updates_dir` 실패 후에도 업데이트를 계속한다. → 이번 실행은 자동 업데이트 끄기.
- **M5** `src/Foldy.UI/Markdown/MarkdownLinks.cs`: `.iso` `.img` `.chm` `.msc` `.settingcontent-ms` `.library-ms` `.docm` `.xlsm` 등은 묻지 않고 연다. `\\서버\…` 링크는 클릭만으로 NTLM 해시를 보낸다. → 안전한 확장자만 바로 열기, UNC는 묻거나 막기.
- **M6** `src/Foldy.Core/Logs/LogCleanup.cs`: `ZipArchiveMode.Update`는 닫을 때 전체를 다시 쓴다. → 새 임시 zip에 쓰고 확인 후 바꿔 넣기 (`File.Move` 덮어쓰기).
- **M7** `src/Foldy.App/Services/SecureFlows.cs`: 원본은 휴지통으로만 간다. → "바로 지우기" 선택지와 "휴지통을 비워야 완전히 사라져요" 안내.

## 낮음

- **L1** `SftpIntegration.cs`: 호스트 키가 바뀌었을 때 기본 버튼이 OK. → 기본값 Cancel.
- **L2** `update/mod.rs`: 일반 사용자가 PC 전체의 건너뛰기·채널을 바꿀 수 있다.
- **L3** `IndexerClient.cs`: 서버가 SYSTEM 서비스인지 확인하지 않는다 (`GetNamedPipeServerProcessId`).
- **L4** `ExplorerFolderMark.cs`: desktop.ini에 exe 전체 경로(사용자 이름 포함 가능)를 쓴다. 끌 때 원래 있던 읽기 전용 속성도 지운다.
- **L5** (H4에서 함께 고침) 금고 크기 계산 기준이 실제 넣는 것과 달랐다.
- **L6** `tools/crash-relay`: 인증이 없어 누구나 하루 20개까지 공개 이슈를 만들 수 있다. → `exceptionType`·버전 형식 제한, 비공개 저장소 검토.
- **L7** 암호는 `string`이라 메모리에서 지울 수 없다 (데스크톱 앱의 흔한 한계). 미리보기 바이트는 지우고 있다.

## 더하면 좋은 테스트

1. 금고: 악성 이름(추가됨), 형식 1·2 열기(추가됨), 링크·OneDrive 폴더(추가됨).
2. 지우개: 앱·릴레이가 같은 입력표를 쓰도록 한 파일로 공유.
3. AppUpdater(C#): 서명 실패·해시 불일치·낮은 버전 거부, 확인 뒤 MSI가 바뀌면 설치하지 않기.
4. 서비스 설치 경로 (추가됨).
5. 로그 정리 zip: 쓰는 중 예외가 나도 기존 zip 유지 (M6).
6. 파이프 프로토콜 퍼징: 깨진 헤더·거대한 길이·잘린 본문 (`foldy-proto`, proptest).
7. Markdown 링크 분류: UNC, `file://서버`, 위험 확장자.
8. 느린 드라이브 취소: 탭을 닫으면 폴더 크기·검색이 실제로 멈추는지.

참고: Linux(.NET 10, `EnableWindowsTargeting`)에서 `Foldy.Core.Tests`의 7개는 원래부터 Windows 경로를 가정해 실패한다 (Cleanup·LocalProvider). Windows CI에서 확인할 것.
