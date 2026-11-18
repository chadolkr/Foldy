# 폴더 템플릿 · 위저드 · 폴더 정리 · 폴더 설명 (설계서 5.10, 5.11, 5.13)

호스트(Foldy.App)가 이 기능을 여는 방법과 구성 요소를 정리합니다.

## 구성

| 위치 | 내용 |
|---|---|
| `src/Foldy.Core/Templates/` | 템플릿 모델(`FolderTemplate`), JSON(`TemplateJson`), 변수 치환(`TemplateVariables`), 안전 검사(`TemplateValidator`), 계획(`TemplatePlanner`), 생성·되돌리기(`TemplateApplier`), 폴더 → 템플릿(`TemplateCapture`), 목록(`TemplateLibrary`), 위저드 규칙(`WizardDefinition`, `WizardScorer`) |
| `src/Foldy.Core/Organize/` | 정리 스캔(`OrganizeScanner`), 순수 계획(`OrganizePlanner`), 저널(`OrganizeJournalWriter/Reader`), 실행·되돌리기(`OrganizeExecutor` + `IMoveOperations`) |
| `src/Foldy.Core/Descriptions/` | `.folder_description.md` 모델·머리말 파서, `FolderDescriptionService`(원자적 쓰기, 폴백 저장소 `IFolderNoteStore`) |
| `src/Foldy.Presentation/Templates/` | `TemplateDialogViewModel`, `FolderWizardViewModel`, `OrganizeViewModel`, `DescriptionEditorViewModel`, `DescriptionBannerViewModel`, 공용 `TemplateCustomizer` |
| `src/Foldy.UI/Templates/` | 창 4개 + `TemplateDialogs` 진입점, 공용 스타일 `TemplateResources.xaml`, 간단한 Markdown 표시 `SimpleMarkdown` |
| `assets/templates/` | 내장 템플릿 5종(`*.foldy-template.json`), 사용법(`*.guide.md`), `wizard.json`. Foldy.Core에 `Foldy.Templates.<파일명>` 리소스로 포함 |

## 호스트에서 여는 법

Foldy.UI는 Foldy.Presentation을 참조하지 않으므로 창은 ViewModel을 `object`로 받아 바인딩만 합니다.
ViewModel의 `DialogResult`(bool?)가 값을 가지면 창이 닫힙니다. **Foldy.App에 `Foldy.Presentation` 프로젝트 참조를 추가해야 합니다.**

```csharp
using Foldy.Core.Templates;
using Foldy.Presentation.Templates;
using Foldy.UI.Templates;

var library = new TemplateLibrary().Reload();          // 내장 + %LOCALAPPDATA%\Foldy\Templates

// 1) 템플릿으로 만들기 (folder.fromTemplate, Ctrl+Shift+N)
var vm = new TemplateDialogViewModel(library, pane.CurrentPath, seenGuides: settings.SeenTemplateGuides);
if (TemplateDialogs.ShowTemplatePicker(mainWindow, vm) == true)
{
    undoStack.Push(() => TemplateApplier.Undo(vm.Result!, LocalTemplateFileSystem.Instance)); // Ctrl+Z 한 번에 전체 취소
    pane.NavigateAndSelect(vm.CreatedTopFolder!);
}

// 2) 폴더 위저드 (folder.wizard, 빈 폴더 안내)
var wiz = new FolderWizardViewModel(TemplateLibrary.LoadWizard(), library, pane.CurrentPath);
wiz.OrganizeRequested += (_, folder) => Dispatcher.BeginInvoke(() => OpenOrganize(folder));
TemplateDialogs.ShowFolderWizard(mainWindow, wiz);    // wiz.Result, wiz.EnableNamingHints, wiz.SavedTemplatePath

// 3) 폴더 정리 (folder.organize, 폴더 우클릭 "이 폴더 정리하기…")
void OpenOrganize(string folder)
{
    var org = new OrganizeViewModel(folder, uiDispatcher, templates: library.Templates,
        media: exifProvider /* IMediaDateProvider, Foldy.Platform에서 구현 */);
    TemplateDialogs.ShowOrganize(mainWindow, org);    // org.LastJournalPath → job_history에 30일 보관
}

// 나중에 되돌리기: new OrganizeViewModel(...).UseJournal(path) 후 UndoCommand, 또는 OrganizeExecutor.Undo(path, LocalMoveOperations.Instance)

// 4) 폴더 설명 편집 (폴더 우클릭 "설명 편집")
var descService = new FolderDescriptionService(new JsonFolderNoteStore(Path.Combine(appData, "folder_notes.json")));
var ed = new DescriptionEditorViewModel(descService, folder);
ed.Load();                                            // 드라이브 큐에서 호출해도 됨
TemplateDialogs.ShowDescriptionEditor(mainWindow, ed);
```

설명 띠는 `DescriptionBannerViewModel.Show(descService.Read(folder), id => library.Find(id)?.DisplayBadge)`로 채웁니다
(템플릿 배지는 머리말의 `template` 항목에서 옵니다).

## 동작 규칙 요약

- **만들기**: 없는 것만 만들고 절대 덮어쓰지 않음(`FileMode.CreateNew`). 같은 이름의 다른 종류(파일↔폴더)는 "만들 수 없음"으로 표시.
  `TemplateApplier.Undo`는 내용이 그대로인 파일과 빈 폴더만 지웁니다.
- **안전 검사**: 절대 경로, `..`, 드라이브 문자, 예약 이름(CON, NUL, COM1 …, `CON.txt` 포함), 금지 문자, 끝의 점·공백,
  실행 파일·스크립트·바로가기(.exe .ps1 .lnk …), 64KB 넘는/텍스트가 아닌 파일 내용, 500개 초과를 거부. 변수 값은 치환 뒤 다시 검사.
- **변수**: `{{key}}`, `{{date:yyyy-MM-dd}}`, 줄임 표기 `{{yyyy}}`. 선택형 변수(`choices`)와 노드 조건(`when: "language=rust|node"`)으로 언어별 변형.
  `optional: true` 노드는 기본 해제, `nameEn`은 위저드의 "폴더 이름 영문".
- **템플릿으로 저장**: `TemplateCapture.Capture(folder, name)` — 깊이 5, 최대 500, 숨김·시스템·링크 제외, 64KB 이하 텍스트만 내용 포함,
  날짜 조각은 `DateSuggestions`로 제안 → 고른 것만 `ApplyDateSuggestions`.
- **정리**: 계획은 순수 함수(`OrganizePlanner.Plan`), 실행 전 미리보기, 삭제 없음. 맞는 규칙이 없으면 `_분류 필요`, 이름·크기·해시가 같으면 `_중복 후보`.
  충돌은 `이름 (2).확장자`. 프로젝트 폴더(.git, .sln, package.json, Cargo.toml, .psd …)는 쪼개지 않고, 숨김·시스템·링크·정션·폴더 설명 파일은 건드리지 않음.
  Windows·Program Files·프로젝트 폴더 자체는 정리 거부. 사용 중인 파일은 건너뛰고 목록으로 알림.
- **저널**: `%LOCALAPPDATA%\Foldy\jobs\organize-*.journal`, 레코드 = `[길이 u32][CRC32 u32][UTF-8 JSON]` (header / mkdir / move / fail / done / undone).
  되돌리기는 그사이 수정된 파일(크기·수정 시각 다름)은 현재 위치에 두고 알리며, 정리로 만든 빈 폴더만 지웁니다.

## 아직 남은 것

- `IMediaDateProvider`(EXIF 촬영 시각)와 `FolderIdentity`(볼륨 일련번호 + 파일 ID) 구현은 Foldy.Platform 몫입니다.
- 폴백 저장소는 지금 JSON 파일(`JsonFolderNoteStore`)입니다. SQLite `folder_notes`·`folder_desc_fts`가 들어오면 같은 인터페이스로 교체합니다.
- 다른 드라이브로의 폴더 이동은 거부합니다(복사 엔진 연결 필요). 파일은 `File.Move`가 복사+삭제로 처리합니다.
- 템플릿 관리 창(이름 변경·편집·내보내기)은 `TemplateLibrary`의 API만 있고 화면은 없습니다. 미리보기 끌어 놓기(대상 폴더 바꾸기)는 목록의 "옮길 폴더" 입력칸으로 대신합니다.
- 사용법 표시는 `SimpleMarkdown`(제목·인용·목록·표·코드)이며, 내장 Markdown 뷰어(5.9)가 생기면 교체할 수 있습니다.
- 레트로 테마의 "설치 마법사 모양"은 따로 만들지 않았습니다(토큰만 따라감).
