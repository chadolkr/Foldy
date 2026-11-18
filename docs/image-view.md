# 이미지만 보기 — 호스트 연결 안내 (설계서 5.12)

이미지 보기 모듈은 화면 조각만 제공한다. 탭 상태(이미지만 토글, 썸네일 크기 기억), 파일 작업(휴지통·이름 변경·복사),
툴바·경로바 버튼은 호스트(Foldy.App / MainWindow)가 붙인다.

| 위치 | 형식 | 역할 |
|---|---|---|
| `Foldy.Platform/Imaging` | `ThumbnailService` | 셸 썸네일 캐시 → JPEG EXIF 썸네일 → WIC 축소 디코딩. 전용 STA 스레드 묶음, LIFO 대기열, 취소. PBGRA 픽셀(`PixelImage`)을 돌려준다 (WPF 모름). |
| | `ExifReader` / `ExifInfo` | 촬영일·카메라·방향·해상도·노출·내장 썸네일. |
| | `ImageFormats` | 이미지 확장자 판정(HEIC·AVIF·RAW는 WIC 코덱이 있을 때만), "대부분 이미지(70%)" 판정. |
| | `CloudFileGuard` | 클라우드 전용 파일(OFFLINE / RECALL_ON_OPEN / RECALL_ON_DATA_ACCESS) 판정. |
| `Foldy.UI/Imaging` | `ImageGridView` | 직접 그리는 가상화 썸네일 격자 (IScrollInfo). |
| | `ImagePreviewPane` | 오른쪽 미리보기 창. |
| | `QuickLookWindow` | 스페이스 빠른 보기 창. |
| | `ThumbnailLoader` | 메모리 캐시(200MB LRU) → 디스크 캐시 → `ThumbnailService`. |
| | `DiskThumbnailCache` | `%LOCALAPPDATA%\Foldy\thumbs.cache` (파일 하나, 500MB 넘으면 오래된 순 정리). |

## 1. 이미지만 보기 켜기

```csharp
// 현재 탭 목록(ItemList)에서 이미지만 골라 새 ItemList로 넘긴다. 다른 파일은 숨길 뿐 움직이지 않는다.
var images = new ItemList(tab.Items.Where(i => !i.IsFolder && ImageFormats.IsImage(i.Name)));

var grid = new ImageGridView
{
    ItemsSource = images,                 // IList. ItemList의 CollectionChanged(Reset, 뒤에 붙이기)를 따라간다
    ThumbnailSize = tab.ImageThumbSize,   // 96 / 160 / 256 (Ctrl+휠로 바뀜 → ThumbnailSizeChanged)
    AllowContentDecode = !isNetworkDrive, // 네트워크 드라이브: 셸 캐시만 (설계서 "자동 생성 끔")
};
var scroller = new ScrollViewer { Content = grid, CanContentScroll = true }; // IScrollInfo는 CanContentScroll=true 필요
```

- `ItemInfoSelector`는 기본값으로 충분하다. 기본 변환기가 `FileItem.FullPath`, `Name`, `Entry.Modified`, `Entry.Size`,
  `Entry.Attributes`를 이름으로 찾아 읽는다 (Foldy.UI는 Presentation을 참조하지 않기 때문).
  성능이 더 필요하면 직접 넣는다:
  `grid.ItemInfoSelector = o => { var f = (FileItem)o; return new ImageItemInfo(f.FullPath, f.Name, f.Entry.Modified, f.Entry.Size, f.Entry.Attributes); };`
- `Loader`는 비워 두면 `ThumbnailLoader.Shared`(공용 서비스 + 200MB 메모리 + 디스크 캐시). 격자·미리보기·빠른 보기에 같은 로더를 쓰면
  미리보기가 격자 썸네일을 즉시 먼저 보여 준다.
- 테마: 색은 생성자에서 `SetResourceReference`로 `Brush.Window`, `Brush.Text`, `Brush.TextSecondary`, `Brush.Selection`,
  `Brush.SelectionText`, `Brush.Hover`, `Brush.Focus`, `Brush.SurfaceAlt`(자리 표시), `Font.UI`, `Font.Size`에 묶인다.
  로컬 값이므로 Style Setter로는 덮어쓸 수 없다 — 바꾸려면 `SetResourceReference`/`SetValue`를 직접 부른다.

### 제안 버튼 ("대부분 이미지")

```csharp
var files = tab.Items.Where(i => !i.IsFolder).Select(i => i.Name);
pathBar.ShowImageSuggestion = !tab.ImagesOnly && ImageFormats.IsMostlyImages(files); // 70% 이상, 파일 3개 이상. 자동 전환은 하지 않는다
```

## 2. 격자 이벤트

| 이벤트 | 언제 | 호스트가 할 일 |
|---|---|---|
| `SelectionChanged` | 선택 변경 | 상태 줄, `SelectedIndices()`로 파일 작업 대상 |
| `FocusedIndexChanged(int)` | 포커스 이동 | 미리보기 창이 열려 있으면 `preview.Show(grid.InfoAt(i))` |
| `ItemActivated(int)` | 더블클릭·Enter | 기본 앱으로 열기 |
| `QuickLookRequested(int)` | 스페이스 | `QuickLookWindow` 열기 (아래) |
| `PreviewRequested(int)` | F3 | 미리보기 창 토글 |
| `ThumbnailSizeChanged(double)` | Ctrl+휠 | 탭 설정에 저장 |

Delete·F2·Ctrl+C 등은 격자가 처리하지 않고 그대로 올려 보내므로 기존 파일 목록과 같은 키 처리기를 쓰면 된다.
파일이 바뀌면 `grid.InvalidateThumbnail(path)`, 드라이브 설정이 바뀌면 `grid.ResetThumbnails()`.

## 3. 미리보기 창

```csharp
var preview = new ImagePreviewPane { Loader = grid.Loader, AllowContentDecode = grid.AllowContentDecode };
grid.FocusedIndexChanged += (_, i) => preview.Show(i >= 0 ? grid.InfoAt(i) : null);
```

맞춤 표시, 더블클릭·Ctrl+0 = 맞춤 ↔ 100%(필요하면 원본 해상도로 다시 디코딩), R = 화면 회전(파일 그대로),
아래 줄 = 해상도 · 파일 크기 · 촬영일 · 카메라 · 노출.

## 4. 빠른 보기

```csharp
grid.QuickLookRequested += (_, index) =>
{
    var ql = new QuickLookWindow(images, index) { Owner = window, Loader = grid.Loader, AllowContentDecode = grid.AllowContentDecode };
    ql.CurrentIndexChanged += (_, i) => grid.SelectIndex(i);
    ql.CommandRequested += async (_, e) =>
    {
        switch (e.Command)
        {
            case QuickLookCommand.Delete:            await fileOps.RecycleAsync(((FileItem)e.Item).FullPath); break;
            case QuickLookCommand.DeletePermanently: /* 확인 대화상자 후 */ break;
            case QuickLookCommand.Rename:            /* 이름 변경 대화상자 */ break;
            case QuickLookCommand.Copy or QuickLookCommand.Cut: clipboard.SetFiles(...); break;
        }
        // 목록이 바뀌었으면 새 목록과 위치를 넘긴다 (비면 창이 닫힌다)
        ql.UpdateItems(newImages, Math.Min(e.Index, newImages.Count - 1));
    };
    ql.Show();
};
```

키: ← → ↑ ↓ PageUp/Down 이전·다음, Home/End, 휠·+/- 확대, 더블클릭·0 = 맞춤 ↔ 100%, R 회전, Esc·스페이스 닫기.

## 5. 썸네일을 만드는 순서와 규칙

1. 메모리 캐시 (키 = 경로 + 수정 시각 + 크기 + 픽셀 크기) → 2. 디스크 캐시 → 3. Windows 셸 썸네일 캐시(`SIIGBF_INCACHEONLY`)
   → 4. JPEG EXIF 내장 썸네일(요청 크기의 75% 이상이면 최종, 작으면 먼저 보여 주고 교체) → 5. WIC 축소 디코딩 (EXIF 방향 적용).
- 셸 캐시 조회는 없을 때도 ~20ms가 든다(실측). 폴더마다 처음 8개가 모두 빗나가면 그 폴더는 셸 조회를 건너뛴다.
- **클라우드 전용 파일은 내용을 절대 열지 않는다**: 셸 캐시에 있는 것만 쓰고, 없으면 자리 표시 아이콘. EXIF도 읽지 않는다.
- 격자는 보이는 칸 + 다음 한 화면분만 요청하고, 범위를 벗어난 요청은 취소한다. 움직이는 GIF·WebP는 첫 프레임만.
- 디코딩 스레드 = 코어 수의 절반(2~8), 우선순위 BelowNormal. 미리보기 요청은 썸네일보다 먼저 처리된다.

## 6. 벤치

```powershell
dotnet run --project tests/Foldy.ImageBench -c Release -- gen-images D:\bench-images 1000        # JPEG(EXIF·내장 썸네일) 70% + PNG 30%, 1920×1280
dotnet run --project tests/Foldy.ImageBench -c Release -- bench-thumbs D:\bench-images 160 40    # 첫 화면 40장 / 전체 시간 (JSON)
dotnet run --project tests/Foldy.ImageBench -c Release -- bench-thumbs D:\bench-images 160 40 --no-shell
```

## 남은 일

- 탭별 "이미지만" 토글(Ctrl+Shift+I)·제안 버튼·썸네일 크기 저장은 호스트(Presentation 탭 상태 + MainWindow)에서.
- 움직이는 GIF·WebP 재생(마우스 오버·미리보기), HEIC 등 비JPEG의 방향 메타데이터, SVG 정적 렌더는 아직 없다.
- 디스크 캐시는 JPEG/PNG(WPF 인코더) 저장이며 WebP 압축·SQLite 색인(`foldy.db`의 `thumb_index`)은 쓰지 않는다.
- 정렬(촬영일)은 `ExifReader.TryReadFile`로 호스트가 백그라운드에서 읽어 정렬 키로 쓰면 된다.
