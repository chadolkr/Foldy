# Foldy

<img src="assets/icons/png/foldy-64.png" width="64" align="right" alt="Foldy icon" />

빠르고 안정적인 Windows 듀얼 패널 파일 관리자입니다. "절대 멈춘 것처럼 보이지 않는 탐색기"를 목표로 합니다.

- **직관적**: 탐색기와 같은 동작, 모달 대신 제자리 진행 표시
- **빠름**: UI 스레드 I/O 금지, 드라이브별 작업 큐, 직접 그리는 대용량 목록, Rust 색인·복사 엔진
- **안정적**: 프로세스 격리, FFI panic 차단, 폴백 경로
- **다양한 기능**: 듀얼 패널·탭, 즉시 검색, 디스크 분석, Git, SFTP/SSH, 터미널, 내장 Markdown 뷰어

> 현재 상태: **M0 (기반과 기술 검증)**. 아직 일상 사용 단계가 아닙니다. 진행 상황은 [docs/adr/0001-m0-results.md](docs/adr/0001-m0-results.md)를 보세요.

## 구성

| 경로 | 내용 |
| --- | --- |
| `src/Foldy.App` | WPF 실행 파일 (조립 루트) |
| `src/Foldy.UI` | 테마(모던 라이트/다크, 레트로), FastFileList 등 컨트롤 |
| `src/Foldy.Core` | 모델, Provider 추상화, 드라이브별 작업 큐 |
| `src/Foldy.Platform` | Win32 호출 (테마·제목 표시줄, M1에서 셸 연동) |
| `src/Foldy.Providers.Local` | 로컬·SMB 폴더 스트리밍 열거 |
| `src/Foldy.Copy.Native` | Rust 복사 엔진 P/Invoke (csbindgen 생성) |
| `src/Foldy.Indexer.Client` | 색인 서비스 Named Pipe 클라이언트 |
| `engine/` | Rust 워크스페이스: `foldy-proto`, `foldy-index`, `foldy-copy`, `foldy-ntfs` |
| `tests/` | xUnit v3 테스트, 벤치마크 도구 |

## 빌드

필요한 도구: .NET 10 SDK, Rust stable (MSVC), Visual Studio C++ Build Tools.

```powershell
./build.ps1            # Rust → .NET 빌드 + 전체 테스트
./build.ps1 -Bench     # 10만 개 목록 스크롤 벤치 추가 실행
```

실행: `src/Foldy.App/bin/Release/net10.0-windows/Foldy.exe`

유용한 옵션: `--theme system|light|dark|retro`, `--bench-list [--step N]`, `--bench-folder <폴더>`, `--list-wpf`(비교용 기본 ListView).

## 라이선스

[GPL-3.0-or-later](LICENSE) · 개발자 David CHA · <https://github.com/chadolkr/Foldy>
