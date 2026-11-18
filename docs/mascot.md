# 마스코트 "연주 & 우진" 사용법

작업 패널의 작업 한 줄 아래(또는 안)에 높이 48px 띠로 농구하는 두 아이가 나온다.
연주(주황 유니폼, 포니테일)와 우진(파랑 유니폼, 짧은 머리)은 모두 새로 그린 오리지널 캐릭터이고,
발 밑에 "연주", "우진" 이름표가 항상 보이며(밝은/어두운/레트로 테마 모두), 마우스를 올리면 장면 설명 툴팁이 뜬다.
장면 확인용 PNG는 `docs/mascot/`에 있다 (밝음 / 어둠 / 레트로 × 시간 순 5프레임).

## 구성

| 파일 | 역할 |
| --- | --- |
| `src/Foldy.Presentation/Mascot/MascotViewModel.cs` | 순수 규칙: 작업 종류·상태·측정값 → 장면, 속도, 말풍선. 5초 이상만 표시, 완료 후 2초 뒤 숨김 |
| `src/Foldy.UI/Mascot/MascotView.cs` | 컨트롤. 속성만 채우면 판정·시간·재생을 스스로 처리 |
| `src/Foldy.UI/Mascot/MascotPainter*.cs` | DrawingContext로 장면을 벡터로 그림 (모던·레트로 공용) |
| `assets/mascot/scenes.json` | 장면 길이·반복 정의 (Foldy.UI에 임베드) |

## 장면 매핑

| 작업 | 장면 |
| --- | --- |
| 복사 / 이동 / 같은 드라이브 이동 | 진행률 0~15% 연주→우진 패스, 15~92% 우진 드리블(진행률만큼 코트를 가로지르고 속도는 MB/s), 92~100% 점프슛. `Phase`를 주면 그것이 우선 |
| 완료 (복사·이동) | 우진의 3점슛 성공 → 연주와 하이파이브, 2초 뒤 숨김 |
| 완료 (그 외) | 둘이 하이파이브 |
| 삭제 | 공을 휴지통에 던짐. `ItemFailed`면 림에 맞고 튕김 |
| 영구 삭제 | 공이 코트 밖으로 날아감 |
| 검색 / 네트워크 대기 / 네트워크 끊김 | 둘이 두리번거리며 공을 찾음 (끊김, 15초 이상 대기는 "?" 말풍선, 대기는 `ShowCancelHint`) |
| 색인 / 디스크 분석 | 우진 드리블 연습, 연주는 기록 |
| Git push / pull | 점프슛 / 패스 |
| 일시정지 / 이름 충돌 | 우진이 공을 안고 대기 (충돌이면 "?") |
| 실패 | 슛이 림에 맞고 튕김 + "!", 4초 뒤 숨김 |

## 호스트에서 붙이기

작업 패널 항목 템플릿에 한 줄 추가한다. 숨겨진 동안(5초 미만, 꺼짐, OS 애니메이션 끔)은 크기 0이라 자리를 차지하지 않는다.

```xml
<Border ... >
  <StackPanel>
    <!-- 기존 제목·진행 막대 -->
    <mascot:MascotView Kind="{Binding MascotKind}"
                       State="{Binding State}"
                       Progress="{Binding Progress}"
                       BytesPerSecond="{Binding BytesPerSecond}"
                       Phase="{Binding MascotPhase}"
                       HasConflict="{Binding HasConflict}"
                       NetworkLost="{Binding NetworkLost}"
                       ItemFailed="{Binding LastItemFailed}"
                       IsMascotEnabled="{Binding Source={x:Static local:Settings.Current}, Path=ShowMascot}"
                       Margin="0,6,0,0"/>
  </StackPanel>
</Border>
```

`xmlns:mascot="clr-namespace:Foldy.UI.Mascot;assembly=Foldy.UI"`

`JobViewModel`에서 채울 값 (이 변경은 App/Presentation 쪽 작업이라 이 모듈에는 포함하지 않았다):

- `State`: 기존 `JobState` 그대로. 뷰가 상태 전이 시각을 스스로 재서 경과·완료 후 시간을 계산한다 (`Elapsed`를 넘길 필요 없음).
- `Progress`: 0~1, 모르면 -1.
- `MascotKind`: 작업을 만드는 쪽에서 `JobKind`(Foldy.Presentation.Mascot)로 정해 JobViewModel에 한 번 설정.
- `BytesPerSecond`: 복사 엔진의 현재 처리량 (드리블 속도).
- `Phase`, `HasConflict`, `NetworkLost`, `LastItemFailed`: 있으면 연결, 없으면 기본값.
- `IsMascotEnabled`: 설정의 "마스코트 표시" (기본 켜짐). 꺼지면 아무것도 그리지 않고 틱도 끊는다.
- `Retro`: 기본 null이면 `ThemeManager.Mode == Retro`를 따른다. 레트로는 1/3 크기로 안티앨리어싱 없이 그려 `NearestNeighbor`로 확대한 도트 느낌이고 이름표만 확대 후 또렷하게 얹는다.

## 성능·접근성

- `CompositionTarget.Rendering`을 모던 30fps / 레트로 10fps로 제한. 숨은 동안은 4Hz로 판정만 하고, 끝나서 숨으면 구독 해제.
- 컨트롤이 안 보이거나 창이 최소화·비활성(`PauseWhenInactive`, 기본 true)이면 렌더링을 멈춘다.
- Windows "애니메이션 효과 표시" 끄기(`SystemParameters.ClientAreaAnimation`)를 따라 숨는다.
- `FrameChanged` 이벤트와 `CurrentFrame.ShowCancelHint`로 호스트가 "[취소]" 제안을 띄울 수 있다.
- 색은 `Brush.Accent`(연주 유니폼, 레트로는 고정 주황), `Brush.Text`(이름표), `Brush.SurfaceAlt`, `Brush.BorderStrong`, `Brush.Error` 토큰에서 읽고 테마가 바뀌면 다시 읽는다.

## PNG 다시 만들기

```powershell
$env:FOLDY_MASCOT_PNG_DIR = "$PWD\docs\mascot"
dotnet test tests/Foldy.Mascot.Tests -p:Platform=x64
```
