# ADR 0001: M0 기술 검증 결과

- 날짜: 2026-11-18
- 상태: 채택
- 측정 PC: Lenovo 노트북, Intel UHD Graphics 620, 60Hz, Windows 11 (렌더링 등급 2 = 하드웨어 가속)

## 관문 1: 10만 개 목록 16ms

측정 방법: `Foldy.exe --bench-list --step N` 이 매 프레임 N행씩 끝까지(최대 900프레임) 스크롤하며
`CompositionTarget.Rendering` 간격을 잰다. 통과 조건은 p95 ≤ 20ms(60Hz에서 프레임을 놓치지 않음).

| 목록 | 스크롤 | 평균 | p95 | 늦은 프레임 | 결과 |
| --- | --- | --- | --- | --- | --- |
| WPF ListView/GridView (기본) | 120행/프레임 | 82.4ms | 116.6ms | 100% | 미달 |
| WPF ListView + 경량 자동화 피어 | 120행/프레임 | 26.2ms | 49.9ms | 51% | 미달 |
| WPF ListView + 경량 자동화 피어 | 3행/프레임 | 16.7ms | 16.7ms | 0.2% | 통과 |
| **FastFileList (직접 그리기)** | 120행/프레임 | 17.3ms | 16.7ms | 3.4% | **통과** |
| **FastFileList** | 30행/프레임 | 16.8ms | 16.7ms | 0.4% | **통과** |
| **FastFileList** | 3행/프레임 | 16.7ms | 16.7ms | 0.3% | **통과** |

발견한 원인: 이 PC처럼 펜·터치 입력이나 접근성 도구가 UI 자동화(UIA)를 듣고 있으면, WPF는 스크롤할 때마다
행·셀 단위 AutomationPeer 트리를 다시 만든다(`AutomationPeer.UpdateSubtree`). 프로파일에서 레이아웃보다
3배 이상 시간을 썼다. 경량 피어로 72ms → 18ms가 됐지만 화면 전체가 바뀌는 빠른 끌기에서는 여전히 미달.

결정: 파일 목록은 **FastFileList**(DrawingContext로 보이는 행만 그리고, 행 텍스트를 캐시, IScrollInfo 직접 구현,
비트 배열 선택)로 간다. WPF ListView는 비교용으로만 남긴다 (`--list-wpf`).
화면 읽기 지원은 M1에서 포커스·선택 항목만 알려 주는 가벼운 자동화 피어로 보완한다.

## 실제 폴더 열기 (10만 개 파일, NTFS SSD)

| 측정 | 첫 표시 | 전체 |
| --- | --- | --- |
| LocalProvider 단독 (5회, 2회차 이후) | 25~30ms | 101~112ms |
| 앱에서 열기 (`--bench-folder`) | 52ms | 202ms |

목표(첫 화면 100ms, 전체 1초) 안.

## 관문 2: C#↔Rust 경계 안정성

`tests/Foldy.Interop.Tests` (13개) 모두 통과:

- FFI (foldy_copy.dll): 한글·이모지·긴 파일 이름 복사, Rust panic 100회 → 오류 코드만 반환하고 프로세스 생존,
  잘못된 핸들·null 포인터·깨진 JSON 거부, 8스레드 × 1,250회 생성/조회/해제 스트레스, 일시정지·재개·취소.
- Named Pipe (foldy-index.exe): Rust가 만든 골든 프레임을 C#이 같은 값으로 읽고 씀, Hello/Status 1,000회 왕복,
  깨진 프레임을 보낸 클라이언트가 있어도 서비스가 다음 클라이언트를 받음, 서비스가 없으면 200ms 안에 폴백.

결정: 설계서 5.8의 경계(정수 핸들 + catch_unwind + 폴링, 길이·타입 프레임 + postcard)를 그대로 쓴다.

## 그 밖의 결정

- 기본 테마는 **모던**(심플·플랫, Foldy 오렌지 강조색), Windows 앱 모드에 따라 라이트/다크 자동 전환. 레트로는 선택 테마.
- 제목 표시줄에 버전 표시 (`Foldy 0.0.1`), 다크 모드에서는 제목 표시줄도 어둡게 (DWM).
- 테스트는 xUnit v3 + Microsoft.Testing.Platform. FluentAssertions 8은 상용 라이선스라 Apache-2.0 포크 AwesomeAssertions 사용.
- 폴더 열거는 .NET `FileSystemEnumerable`(NtQueryDirectoryFile) 사용. 측정상 충분히 빨라 직접 FindFirstFileEx를 부를 필요 없음.

## M1로 넘기는 것

- FastFileList: 열 너비 조절, 정렬(자연 정렬, 백그라운드), Type-ahead, 아이콘, 가벼운 자동화 피어, 큰 목록용 스크롤바 최소 크기.
- DriveWorkQueue 워치독에 `CancelSynchronousIo` 연결 (Foldy.Platform).
- foldy-proto에서 C# 메시지 코드 생성, foldy-index Windows 서비스 등록과 파이프 ACL.
- 설정 저장(settings.json)에 테마 선택 저장.
