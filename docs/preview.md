# 미리보기 창 (설계서 9장, 5.9, 5.12)

패널 오른쪽에 파일 내용을 미리 보여 주는 창. **F3**로 패널마다 켜고 끄며, 패널에서 포커스된(선택된) 파일이 바뀔 때마다 그 파일을 보여 준다.
Markdown 뷰어(5.9)와 이미지 미리보기(5.12)가 같은 창을 공유한다.

## 구성

| 층 | 위치 | 내용 |
|---|---|---|
| Presentation (WPF 없음) | `src/Foldy.Presentation/Preview/` | `IPreviewProvider`, `PreviewProviderRegistry`, `PreviewPolicy`(보호 규칙), `PreviewViewModel`, `TextFileReader`(인코딩) |
| Platform (Win32/COM) | `src/Foldy.Platform/Preview/` | `IPreviewHandler`·`IInitializeWith*` COM 선언, `ShellPreviewHandlers`(처리기 조회·블랙리스트), `PreviewHandlerSession`(실패 격리), `PreviewHostWindow` |
| UI | `src/Foldy.UI/Preview/` | `PreviewPane`(.xaml/.cs), `TextPreviewView`, `ShellPreviewHost`(HwndHost) |

## 공급자 (확장자 → 뷰)

`Order`가 작은 것부터 `CanPreview`가 참인 첫 공급자를 쓴다.

| 공급자 | Order | 대상 | 크기 제한 | 표시 |
|---|---|---|---|---|
| `markdown` | 10 | .md .markdown .mdown .mkd | 8 MB | `MarkdownView` (DataContext = `MarkdownTabViewModel`) |
| `image` | 20 | JPG PNG GIF BMP TIFF WebP ICO (+ 호스트가 넘긴 코덱 판정) | 128 MB | `ImagePreviewPane` |
| `text` | 30 | 텍스트·코드 확장자, LICENSE 등 | 없음 (앞 1 MB만 읽음) | `TextPreviewView` (고정폭, CodeHighlighter 강조) |
| `shell` | 1000 | 위에 없고 Windows 처리기가 있는 형식 (PDF, Office …) | 256 MB | `ShellPreviewHost` (IPreviewHandler) |

텍스트는 BOM → UTF-8 검증 → BOM 없는 UTF-16 추정 → CP949 순으로 판별하고, NUL·제어 문자가 많으면 바이너리로 보고 "미리 볼 수 없음".
200,000자 이하의 지원 언어(cs js py rs json sh ps1 sql xml)만 강조하고, 더 크면 강조 없이 TextBox.

## 보호 규칙 (DESIGN.md 느린 드라이브)

`PreviewPolicy.Evaluate`가 디스크·네트워크에 접근하지 않고 순서대로 판정한다.

1. 폴더 → "폴더"
2. 클라우드 전용(OFFLINE / RECALL_ON_OPEN / RECALL_ON_DATA_ACCESS) → **"클라우드 파일 — 내려받지 않음"**. 내용은 절대 읽지 않는다. 이미지만 `AllowContentRead=false`로 셸 썸네일만 쓴다. 사용자가 눌러도 풀리지 않는다.
3. 네트워크 드라이브(UNC, 매핑 드라이브, SFTP) → **"네트워크 드라이브 — [미리보기]"** + 버튼. `ForceCommand`(= `PreviewRequest.UserForced`)를 누른 파일 한 번만 읽는다.
4. 공급자 없음 → "미리 볼 수 없음" / 크기 초과 → 크기 안내.

## 셸 미리보기 처리기 호스트

- 조회: `AssocQueryString(ASSOCSTR_SHELLEXTENSION, ".ext", "{8895b1c6-…}")`로 CLSID를 찾고, `HKLM/HKCU\...\PreviewHandlers` 승인 목록에 있는 것만 쓴다.
- 생성: `CLSCTX_LOCAL_SERVER`(prevhost.exe 서로게이트 → 처리기가 죽어도 Foldy는 산다)를 먼저, 등록이 없으면 `INPROC`.
- 초기화: `IInitializeWithStream`(`SHCreateStreamOnFileEx`, 공유 읽기) 우선, 없으면 `IInitializeWithFile` → `SetWindow` → `DoPreview`. 크기가 바뀌면 `SetRect`, 닫을 때 `Unload` 후 `FinalReleaseComObject`.
- 격리: 모든 호출을 try/catch로 감싸 실패하면 Unload·Release 후 `PreviewHandlerException`. 같은 CLSID가 연속 3번 실패하면 `PreviewHandlerBlacklist`에 올려 이후 `IsAvailable`이 false (성공하면 횟수 초기화). 호출이 5초를 넘으면(사후 판정) 실패로 치고 닫는다. 실패는 상태 줄 "미리 볼 수 없음 (처리기 오류)"로 보인다.

## 호스트(패널)가 할 일

앱(`Foldy.App`, `PaneView`, `ShellWindow`)은 이 작업에서 건드리지 않았다. 붙일 때:

1. 시작 시 한 번 공급자 모음을 만든다 (셸·이미지 판정은 Platform에서 주입).
   ```csharp
   var registry = PreviewProviderRegistry.CreateDefault(
       shellHandlerAvailable: ShellPreviewHandlers.IsAvailable,
       isImage: ImageFormats.IsImage);
   ```
2. 패널마다 `PreviewViewModel`(필드)과 `PreviewPane`(패널 오른쪽 열, 접힘 상태로 시작)을 하나씩 둔다. `pane.DataContext = vm`.
3. **F3**: 열 `Visibility`/너비를 토글한다. 켤 때 현재 포커스 항목을 한 번 보낸다. 끄면 `vm.ShowAsync((PreviewRequest?)null)`로 비워 처리기를 Unload하고 Markdown 감시를 끈다.
4. 포커스 항목이 바뀔 때(켜져 있을 때만): `_ = vm.ShowAsync(fileItem)` (`FileItem` → `PreviewRequest.From`, 네트워크 판정은 `FsEntry.Path.Provider`와 경로로). 빠르게 지나가는 선택은 `Debounce`(120 ms)로 합쳐지고 이전 읽기는 취소된다.
5. 파일 연결이 바뀌면 `ShellPreviewHandlers.Invalidate()` (`Associations.Invalidate()`와 같은 시점).
6. 설정으로 "항상 켜짐"을 두려면 네트워크 규칙은 그대로 둔다 (자동 미리보기가 느린 드라이브를 읽지 않게).

## 알려진 한계

- 처리기는 UI(STA) 스레드에서 만든다. 서로게이트(prevhost) 덕분에 크래시는 격리되지만, 응답 없는 처리기의 COM 호출은 UI 스레드에서 중간에 끊을 수 없어 시간 초과는 사후 판정이다. 전용 STA 스레드 + 자식 창 분리는 후속 과제.
- HwndHost 에어스페이스: 셸 처리기 영역 위에는 WPF 팝업·툴팁이 가려질 수 있다.
- 폴더 설명(5.13)을 미리보기에 보이는 것은 아직 없다.
