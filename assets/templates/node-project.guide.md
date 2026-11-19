# Node·TypeScript 프로젝트 사용법

> 소스는 src, 테스트는 test, 그대로 내보낼 파일은 public, 빌드 결과는 dist. dist와 node_modules는 git에 올리지 않습니다.

## 폴더별로 무엇을 넣나

| 폴더 | 넣는 것 | 넣지 않는 것 |
|---|---|---|
| **src** | TypeScript 소스 | 빌드 결과(.js) |
| **test** | 자동 테스트 | 임시 실험 코드 |
| **public** | 이미지·아이콘처럼 가공 없이 제공하는 파일 (웹일 때) | 소스 코드 |
| **dist** | 빌드 결과. 만들어지는 폴더라 미리 만들지 않습니다 | 직접 고친 파일 |
| **docs** | 사람이 읽는 문서 | — |

맨 위에는 **package.json**, **tsconfig.json**, **README.md**, **.gitignore**를 둡니다.

## 예시 트리

```
my-web/
├─ src/
│  └─ index.ts
├─ test/
│  └─ index.test.ts
├─ public/
├─ package.json
├─ tsconfig.json
├─ README.md
└─ .gitignore      (node_modules/, dist/ 포함)
```

## 이름 규칙

| 대상 | 규칙 | 예 |
|---|---|---|
| 일반 파일 | kebab-case | `file-list.ts` |
| 컴포넌트·클래스 | PascalCase | `FileList.tsx` |
| 테스트 | 이름.test.ts | `file-list.test.ts` |

## 유지 요령

- node_modules, dist, coverage, .env는 커밋하지 않습니다. 비밀 값은 .env에만 둡니다.
- 의존성을 바꾸면 lock 파일(package-lock.json)도 같이 커밋합니다.


## 이럴 땐 다른 스타일을

- 언어를 정하지 않은 일반 저장소라면 → **소프트웨어 프로젝트**
- 문서·설계가 중심이라면 → **문서 작성 단계**
