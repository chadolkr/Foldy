# 설정 (settings.json, keymap.json, 설정 창)

설정 창은 `AppSettings`와 `KeyMap`의 *복사본*을 편집하고, 호스트(Foldy.App)가 결과를 저장하고 반영한다.

| 구성 | 위치 |
|---|---|
| 설정 값 | `Foldy.Presentation/Persistence/AppSettings.cs` (settings.json) |
| 편집 ViewModel | `Foldy.Presentation/Settings/SettingsViewModel.cs` |
| 단축키 표 | `Foldy.Presentation/Commands/KeyMap.cs` (keymap.json) |
| 설정 창 | `Foldy.UI/Settings/SettingsWindow.xaml` |
| 강조색 | `Foldy.UI/Theming/AccentColor.cs`, `ThemeManager.SetAccent` |

## 호스트가 설정 창을 여는 방법

```csharp
var keyMap = KeyMap.Load();                                  // %LOCALAPPDATA%\Foldy\keymap.json
var vm = new SettingsViewModel(currentSettings, keyMap, wtProfileNames);
vm.Applied += (_, e) =>                                      // [적용]/[확인]마다 발생
{
    ApplySettings(e.Settings);                               // 아래 "반영" 참고
    e.KeyMap.Save();                                         // 재지정만 저장 (없으면 파일 삭제)
    RebuildKeyBindings(e.KeyMap);
};
var window = new SettingsWindow(vm) { Owner = this };
window.SelectCategory(6);                                    // 선택: 0 일반 … 6 단축키
window.ShowDialog();
```

`wtProfileNames`는 Windows Terminal 프로필 이름 목록(없으면 생략). 명령 팔레트 ID `app.settings`(기본 `Ctrl+,`)에 연결하면 된다.

## 반영 (Applied 이벤트 안에서)

```csharp
void ApplySettings(AppSettings s)
{
    JsonFileStore.Save(settingsPath, s);                     // settings.json
    ThemeManager.SetMode(s.Theme switch
    {
        "light" => FoldyTheme.Light, "dark" => FoldyTheme.Dark,
        "retro" => FoldyTheme.Retro, _ => FoldyTheme.System,
    });
    ThemeManager.SetAccent(s.Accent);                        // "windows" | "foldy"
    // 줄 높이: compact 24 / normal 28 / comfortable 32 → List.RowHeight 리소스
    // s.ShowHidden, s.MarkdownOpen, s.Terminal, s.Animations: 각 서비스에 전달
    // s.CopyThreads: 0이면 자동, 1 이상이면 복사 엔진 스레드 수 고정
    // s.Update: IUpdateService.ApplySettingsAsync(s.Update, ct)
    // s.SendCrashReports: 오류 보고 동의 플래그
}
```

- 테마와 강조색은 `DynamicResource` 토큰이라 창을 다시 만들 필요 없이 바로 바뀐다. Windows 강조색은 `Brush.Accent / AccentText / Focus / Progress / Selection`을 덮어쓰며, Windows 설정에서 색이 바뀌면 자동으로 따라간다. 레트로 테마는 덮어쓰지 않는다.
- 이전 settings.json에 없는 새 필드(`accent`, `copyThreads`, `update`, `sendCrashReports`)는 기본값으로 읽히므로 마이그레이션이 필요 없다.

## 단축키 재지정

- keymap.json은 **기본값과 다른 것만** 담는 평평한 JSON 객체다: `{ "file.copy": "Ctrl+Shift+C", "view.tree": "" }` (빈 문자열 = 단축키 없음).
- 제스처 표기: `Ctrl+Shift+P`, `F5`, `Ctrl+\``. 수정 키 순서는 Ctrl, Alt, Shift, Win. 읽을 때 정규화하고, 모르는 명령이나 해석할 수 없는 값은 무시한다.
- 호스트는 시작할 때와 `Applied` 이후에 `KeyMap`으로 입력 바인딩을 다시 만든다: 눌린 키를 `KeyGesture`로 만들어 `keyMap.Resolve(gesture)`로 명령 ID를 찾거나, `keyMap.GetGesture(id)`로 팔레트 표시를 갱신한다. 새 명령은 `KeyMap.DefaultCommands`에 `CommandDefinition(id, 제목, 기본 제스처)`를 추가한다.
- 충돌은 막지 않고 경고만 한다 (`KeyMap.ConflictsFor`, `FindConflicts`). 충돌이면 `Resolve`는 정의 순서상 먼저 나오는 명령을 돌려준다.
- 설정 창의 단축키 페이지: 단축키 칸을 누르고 새 조합을 입력, `Esc` 취소, `Backspace` 지우기, `기본값`/`모두 기본값으로` 되돌리기.

## 글꼴

UI 글꼴은 Pretendard(SIL OFL 1.1, `assets/fonts/Pretendard-OFL-1.1.txt`)를 Foldy.UI에 리소스로 넣어 쓰고, 없으면 Segoe UI Variable / Segoe UI / Malgun Gothic으로 대체한다 (`Modern.Controls.xaml`의 `Font.UI`). Regular, Medium, SemiBold, Bold 네 굵기(v1.3.9, OTF)만 포함한다.
