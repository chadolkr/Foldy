# 소프트웨어 프로젝트 사용법

> 처음 보는 사람도 "코드는 어디, 테스트는 어디, 실행은 어떻게"를 1분 안에 알 수 있게 하는 저장소 기본 구조입니다.

## 폴더별로 무엇을 넣나

| 폴더 | 넣는 것 | 넣지 않는 것 |
|---|---|---|
| **src** | 배포되는 코드 | 실험용 코드 조각, 빌드 결과물 |
| **tests** | 자동 테스트와 테스트 데이터 | 손으로 돌려 보는 임시 파일 |
| **docs** | 사람이 읽는 문서, 설계 기록(adr) | 자동 생성된 API 문서 결과물 |
| **scripts** | 빌드·배포·자동화 스크립트 | 비밀번호·키가 든 파일 |
| **assets** | 앱에 들어가는 그림·아이콘·글꼴 | 원본 디자인 작업 파일(크면 따로 보관) |
| **build** | 생성물. 버전 관리에서 뺍니다 | 직접 고친 파일 |

맨 위에는 **README.md**(무엇이고 어떻게 실행하나), **LICENSE**, **.gitignore**(생성물 목록)를 둡니다.

## 예시 트리

```
my-app/
├─ src/
├─ tests/
├─ docs/
│  └─ adr/
│     └─ 0001-record-architecture-decisions.md
├─ scripts/
├─ README.md
├─ LICENSE
└─ .gitignore
```

## 이름 규칙

언어마다 관례를 따릅니다.

| 대상 | 규칙 | 예 |
|---|---|---|
| C# | PascalCase | `FileListViewModel.cs` |
| Rust·Python | snake_case | `mft_parser.rs` |
| 문서 | kebab-case | `getting-started.md` |
| 설계 기록 | 네 자리 번호-제목 | `0003-use-sqlite.md` |

## 유지 요령

- README의 실행 방법은 실제로 돌아가는 명령으로 유지합니다. 바뀌면 같은 커밋에서 고칩니다.
- 생성물(bin, obj, target, node_modules, build)은 .gitignore에 넣고 커밋하지 않습니다.
- 중요한 설계 결정은 docs/adr에 짧게 남깁니다. "왜"가 가장 빨리 잊힙니다.
- 이 폴더는 Foldy의 폴더 정리에서 **프로젝트 폴더로 통째로** 다뤄지며 안쪽이 쪼개지지 않습니다.

## 이럴 땐 다른 스타일을

- 코드가 아니라 고객 산출물(문서·디자인)이 중심이라면 → **고객·프로젝트 업무**
- 개인 학습 자료 모음이라면 → **PARA**의 3_Resources
