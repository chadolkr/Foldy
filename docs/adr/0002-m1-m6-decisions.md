# ADR 0002: M1~M6 진행 중 내린 결정

- 날짜: 2026-11-19
- 상태: 채택 (사용자 요청에 따라 묻지 않고 진행하며 기록)

| 주제 | 결정 | 이유 |
| --- | --- | --- |
| 기본 디자인 | 모던(심플·플랫) 기본, 라이트/다크는 Windows 설정을 실시간으로 따름, 레트로는 선택 | 사용자 요청 (2026-11-18) |
| 탭 | OneNote 섹션처럼 탭마다 다른 색 (`Brush.Tab0~7`) | 사용자 요청 |
| 파일 목록 | 직접 그리는 FastFileList, 행 단위 자동화 피어 없음 → 포커스 행을 라이브 영역으로 알림 | M0 측정 (ADR 0001) |
| 파일 작업 | 삭제·이름 바꾸기·새 폴더는 IFileOperation(탐색기와 같은 확인·UAC·휴지통), 복사·이동은 Rust 병렬 엔진(충돌은 Foldy 창에서 확인), DLL이 없으면 IFileOperation | 설계서 5.4, 5.8 |
| 셸 메뉴 | UI 스레드에서 IContextMenu2/3, 소유 창 메시지 전달. 타사 확장 예외는 잡아서 무시 | 하위 메뉴(보내기·연결 프로그램) 표시에 소유 창 스레드가 필요 |
| 검색 | 색인 서비스가 준비되면 전체 드라이브 즉시 검색, 아니면 현재 폴더 아래를 훑기(2만 개 제한) | 서비스 없는 PC에서도 동작 |
| 폴더 크기 | 색인 서비스 FolderSizes로만 (디스크 I/O 없음). 색인 밖이면 비워 둔다 | DESIGN.md |
| 감시 | 로컬 고정·이동식 = FileSystemWatcher, 네트워크 45초·SFTP 60초 폴링(활성 탭만) | DESIGN.md 느린 드라이브 |
| 설치 | 0.1: 스크립트 설치(Program Files + 서비스) + WiX MSI 병행. 스크립트는 UTF-8 BOM (Windows PowerShell 5.1), 서비스 등록은 New-Service/CIM | PS 5.1 인코딩·따옴표 문제 |
| 자동 업데이트 | GitHub Releases + Ed25519 서명 + SHA-256. 앱이 확인·검증하고, 서비스가 있으면 SYSTEM 서비스가 UAC 없이 MSI 설치 (사용자가 PC 앞에 없어도 적용) | 설계서 10.5, 사용자는 모바일로만 조작 가능 |
| 네트워크 | 모든 HTTP는 Windows 시스템 프록시(PAC/WPAD)와 로그온 자격 증명 사용 (Rust WinHTTP, C# 시스템 프록시 + DefaultCredentials) | 사용자 요청 (사내 프록시) |
| 플랫폼 | x64만 빌드·배포 | 사용자 요청 |
| SFTP 인증 | 비밀번호·키 파일 (DPAPI 저장), ~/.ssh/config Host 사용, ssh-agent/Pageant는 SSH.NET 미지원으로 제외 (외부 SSH 터미널은 그대로 사용) | 라이브러리 한계 |
| 테스트 실행 | `dotnet test --max-parallel-test-modules 3` | 모듈을 모두 동시에 돌리면 WPF 테스트 프로세스가 충돌 |
| 병렬 개발 | 독립 모듈은 작업 트리에서 병렬로 만들고, 테스트 통과 뒤 main에 병합. 단순 작업은 가벼운 모델 | 사용자 요청 (속도·토큰 절약) |
