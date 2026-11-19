# .NET 솔루션 사용법

> 솔루션 하나에 프로젝트 여러 개를 src와 tests로 나눠 둡니다. "코드는 src, 테스트는 tests"만 지켜도 처음 보는 사람이 길을 잃지 않습니다.

## 폴더별로 무엇을 넣나

| 폴더 | 넣는 것 | 넣지 않는 것 |
|---|---|---|
| **src** | 앱·라이브러리 프로젝트(각각 자기 폴더) | 테스트 프로젝트 |
| **tests** | 테스트 프로젝트 (대상 + .Tests) | 실서비스 코드 |
| **docs** | 사람이 읽는 문서, 설계 기록 | 자동 생성 결과 |

맨 위에는 솔루션 파일(.sln 또는 .slnx), **README.md**, **.gitignore**, **.editorconfig**를 둡니다.

## 예시 트리

```
MyApp/
├─ src/
│  ├─ MyApp.Core/
│  └─ MyApp.App/
├─ tests/
│  └─ MyApp.Core.Tests/
├─ docs/
├─ MyApp.slnx
├─ README.md
└─ .gitignore
```

## 이름 규칙

| 대상 | 규칙 | 예 |
|---|---|---|
| 프로젝트·클래스·파일 | PascalCase | `FileListViewModel.cs` |
| 테스트 프로젝트 | 대상.Tests | `MyApp.Core.Tests` |
| 인터페이스 | I로 시작 | `IFileSystem` |

## 유지 요령

- bin, obj, .vs는 커밋하지 않습니다.
- 공통 빌드 설정은 맨 위 Directory.Build.props 한 곳에 모읍니다.


## 이럴 땐 다른 스타일을

- 언어를 정하지 않은 일반 저장소라면 → **소프트웨어 프로젝트**
- 문서·설계가 중심이라면 → **문서 작성 단계**
