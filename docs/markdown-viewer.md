# 내장 Markdown 뷰어 붙이기 (설계서 5.9)

코드는 `src/Foldy.UI/Markdown/`에 있다. 패널 탭 호스트에서 쓰는 것은 `MarkdownView` 하나다.

## 호스트가 할 일

```xml
<DataTemplate DataType="{x:Type tabs:MarkdownTabViewModel}">
  <md:MarkdownView FolderLinkCommand="{Binding DataContext.OpenFolderInNewTabCommand, RelativeSource={RelativeSource AncestorType=local:PaneView}}" />
</DataTemplate>
```

- **DataContext**: `MarkdownTabViewModel`. 뷰가 `FilePath`, `ShowSource`의 바뀜을 보고 다시 읽는다.
  뒤로/앞으로와 상대 `.md` 링크는 `GoBack`/`GoForward`/`OpenLinked`를 부른다(같은 탭).
- **폴더 링크**: `FolderLinkRequested` 이벤트(`MarkdownFolderLinkEventArgs.Path`, 처리했으면 `Handled = true`)가 먼저
  발생하고, 처리되지 않으면 `FolderLinkCommand`(매개변수: 폴더 전체 경로 `string`)를 부른다. 둘 중 하나만 연결하면 된다.
- **테마**: 앱 리소스(ThemeManager가 넣는 `Brush.*`, `Font.*` 토큰)만 있으면 된다. 코드 강조색 등 전용 색(`Md.*`)은
  문서 자체 리소스에 들어 있고, 뷰가 `ThemeManager.Changed`를 받아 바꾼다.
- **수명**: 뷰가 화면에서 빠지면(Unloaded) 파일 감시를 멈추고, 다시 붙으면(Loaded) 다시 읽는다. 따로 Dispose할 것은 없다.
- **Mermaid (선택)**: 앱 시작 때 `MarkdownView.DefaultMermaidRenderer = new WebView2MermaidRenderer(...)` 처럼
  `IMermaidRenderer` 구현체를 등록하면, mermaid 블록이 화면에 들어올 때(또는 "다이어그램 보기"를 누를 때) 그린다.
  등록하지 않으면 코드 블록으로만 보인다. 구현체는 아직 없다(v1 초반 예정, WebView2 + 번들 mermaid.min.js).

## 뷰가 혼자 하는 일

- 툴바: 뒤로/앞으로(Alt+←/→), 원문 보기, 목차, 편집(셸 "edit" 동사 → 없으면 메모장), 찾기(Ctrl+F, F3, Shift+F3).
- 인코딩: BOM(UTF-8/UTF-16) → 엄격한 UTF-8 → CP949 (`MarkdownText`).
- 파일이 바뀌면 400ms 모아서 다시 읽고 스크롤 위치를 지킨다.
- 1MB 넘는 문서는 블록 200개씩 나눠 붙여 앞부분부터 보인다. 파싱은 백그라운드.
- 링크: `#앵커` → 문서 안 이동, `.md` → 같은 탭, 폴더 → 호스트, 그 밖의 파일 → 셸 열기(실행 파일은 한 번 묻는다),
  http(s)/mailto → 기본 앱, 그 밖의 스킴(javascript: 등) → 무시.
- 이미지: 로컬 상대 경로만 바로 표시. 인터넷 이미지는 차단하고 "이미지 불러오기" 띠를 보인다. SVG는 자리 표시만.
- 원문 HTML은 실행하지 않고 글자로 보인다(`<br>`만 줄바꿈).

## 미리보기 창 등 다른 곳에서 렌더러만 쓰기

```csharp
var ast = await Task.Run(() => MarkdownParsing.Parse(text));          // 백그라운드
var headings = MarkdownOutline.Extract(ast);
var renderer = new MarkdownFlowRenderer(new MarkdownRenderOptions { BaseFolder = folder, Dark = ThemeManager.IsDark, LinkClicked = url => ... }, headings);
viewer.Document = renderer.Render(ast);                                // UI 스레드
```
