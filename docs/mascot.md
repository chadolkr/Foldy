# 마스코트 "연주 & 우진" 사용법

작업 패널의 작업 한 줄 아래(또는 안)에 높이 48px 띠로 농구하는 두 아이가 나온다.
연주(주황 유니폼, 포니테일)와 우진(파랑 유니폼, 짧은 머리)은 모두 새로 그린 오리지널 캐릭터이고,
발 밑에 "연주", "우진" 이름표가 항상 보이며(밝은/어두운/레트로 테마 모두), 마우스를 올리면 장면 설명 툴팁이 뜬다.
장면 확인용 PNG는 `docs/mascot/`에 있다 (밝음 / 어둠 / 레트로 × 시간 순 5프레임).

## 구성

| 파일 | 역할 |
| --- | --- |
| `src/Foldy.Presentation/Mascot/MascotViewModel.cs` | 순수 규칙: 작업 종류·상태·측정값 → 장면, 속도, 말풍선. 5초 이상만 표시, 완료 후 2초 뒤 숨김 |
| `src/Foldy.Presentation/Mascot/MascotPlays.cs` | 동작 라이브러리 11종과 시드 기반 동작 순서(`PlayPlan`), 코트 배치(`MascotCourt`) |
| `src/Foldy.UI/Mascot/MascotView.cs` | 컨트롤. 속성만 채우면 판정·시간·재생을 스스로 처리 |
| `src/Foldy.UI/Mascot/MascotPainter*.cs` | DrawingContext로 장면을 벡터로 그림 (모던·레트로 공용) |
| `assets/mascot/scenes.json` | 장면 길이·반복 정의 (Foldy.UI에 임베드) |

## 복사·이동·삭제: 진행 막대가 되는 농구

진행 중에는 슛하지 않는다. 시드로 정해진 동작을 이어 붙이고(크로스오버, 비하인드 백, 다리 사이 드리블, 스핀 무브,
기브 앤 고, 바운드 패스, 앨리웁 셋업, 스크린 앤 롤, 컷 인, 슛 페이크, 방향 전환 — 이웃한 동작은 항상 다르다),
**플레이 중심이 진행률을 따라 맨 왼쪽(0%)에서 오른쪽으로 전진**한다. 중심 주위로는 좌우로 오가며 패스·드리블을 한다.
진행률을 모르면(-1) 중간 근처에서 더 넓게 움직인다. 진행률 92% 이상이면 `Setup`: 마무리 위치 바깥에서 슛 없이 준비한다.
뷰는 진행률 변화를 부드럽게 따라가므로 껑충 뛰지 않는다.

완료(Done) 때만 작업마다 다른 마무리가 나오고, 성공하면 하이파이브 후 2초 뒤 숨는다.

| 작업 | 100% 위치 | 마무리 장면 |
| --- | --- | --- |
| 복사 | 바닥에 그린 3점 라인 바깥, 골대에서 띠 폭의 약 1/3 | `Score`: 라인 밖에서 길게 던진 3점슛이 림에 닿지 않고 들어감(스위시) |
| 이동 / 같은 드라이브 이동 | 같은 위치 | `Dunk`: 속공, 연주가 앨리웁 패스를 올리고 우진이 덩크 |
| 삭제(휴지통) | 사이드라인 앞 | `TossBin`: 사이드라인 너머 코트 옆 휴지통에 던짐. `ItemFailed`면 빗나가 튕김 |
| 영구 삭제 | 사이드라인 앞 | `OutOfBounds`: 경기장 밖으로 멀리 던져 버림 |
| 실패 | - | 복사·이동: `Miss`(3점 라인 밖에서 던진 슛이 림에 맞고 튕김, "!"), 삭제: 빗나가는 `TossBin` |

## 그 밖의 작업

| 작업 | 장면 |
| --- | --- |
| 검색 / 네트워크 대기 / 네트워크 끊김 | 둘이 코트 곳곳을 조깅하며 두리번거림 (끊김·15초 이상 대기는 "?" 말풍선, 대기는 `ShowCancelHint`) |
| 색인 / 디스크 분석 | 우진 콘 지그재그·풋워크 드리블, 연주 왕복 달리기 (한 바퀴마다 위치·종류 변화) |
| Git push / pull | 점프슛 반복(위치가 바뀜) / 패스 위주 연속 동작 |
| 일시정지 / 이름 충돌 | 우진이 공을 안고 대기 (충돌이면 "?") |
| 그 외 완료 | 달려와 하이파이브 |

## 호스트에서 붙이기

작업 패널 항목 템플릿에 한 줄 추가한다. 숨겨진 동안(5초 미만, 꺼짐, OS 애니메이션 끔)은 크기 0이라 자리를 차지하지 않는다.
띠는 패널 폭 전체를 쓰고 장면은 최대 640px까지 펼쳐진다 (`scenes.json`의 `maxWorldWidth`).

```xml
<mascot:MascotView Kind="{Binding MascotKind}" State="{Binding State}" Progress="{Binding Progress}"
                   BytesPerSecond="{Binding BytesPerSecond}" Phase="{Binding MascotPhase}"
                   HasConflict="{Binding HasConflict}" NetworkLost="{Binding NetworkLost}"
                   ItemFailed="{Binding LastItemFailed}" Seed="{Binding MascotSeed}"
                   IsMascotEnabled="{Binding ShowMascot}" Margin="0,6,0,0"/>
```

`xmlns:mascot="clr-namespace:Foldy.UI.Mascot;assembly=Foldy.UI"`

- `State`: 기존 `JobState` 그대로. 뷰가 상태 전이 시각을 스스로 재서 경과·완료 후 시간을 계산한다.
- `Progress`: 0~1, 모르면 -1. 이 값이 플레이 중심(왼쪽 → 3점 라인)을 정한다.
- `Kind`: `JobKind`(Foldy.Presentation.Mascot).
- `Seed`: 동작 순서를 정한다. 0(기본)이면 뷰마다 무작위, 값을 주면 항상 같은 순서(작업 ID 해시 등을 권장).
- `BytesPerSecond`: 복사 엔진의 현재 처리량 (재생 속도).
- `Phase`, `HasConflict`, `NetworkLost`, `ItemFailed`: 있으면 연결, 없으면 기본값.
- `IsMascotEnabled`: 설정의 "마스코트 표시" (기본 켜짐). 꺼지면 아무것도 그리지 않고 틱도 끊는다.
- `Retro`: 기본 null이면 `ThemeManager.Mode == Retro`를 따른다. 레트로는 1/3 크기로 안티앨리어싱 없이 그려 `NearestNeighbor`로 확대한 도트 느낌이고 이름표만 확대 후 또렷하게 얹는다.

## 성능·접근성

- `CompositionTarget.Rendering`을 모던 30fps / 레트로 12fps로 제한. 숨은 동안은 4Hz로 판정만 하고, 끝나서 숨으면 구독 해제.
- 컨트롤이 안 보이거나 창이 최소화·비활성(`PauseWhenInactive`, 기본 true)이면 렌더링을 멈춘다.
- Windows "애니메이션 효과 표시" 끄기(`SystemParameters.ClientAreaAnimation`)를 따라 숨는다.
- `FrameChanged` 이벤트와 `CurrentFrame.ShowCancelHint`로 호스트가 "[취소]" 제안을 띄울 수 있다.
- 색은 `Brush.Accent`(연주 유니폼, 3점 라인, 레트로는 고정 주황), `Brush.Text`(이름표), `Brush.SurfaceAlt`, `Brush.BorderStrong`, `Brush.Error` 토큰에서 읽고 테마가 바뀌면 다시 읽는다.

## PNG 다시 만들기

```powershell
$env:FOLDY_MASCOT_PNG_DIR = "$PWD\docs\mascot"
dotnet test tests/Foldy.Mascot.Tests -p:Platform=x64
```
