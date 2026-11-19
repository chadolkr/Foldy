# Python 프로젝트 사용법

> 패키지를 src 안에 두는 "src 레이아웃"입니다. 설치하지 않은 코드를 실수로 불러 쓰는 일을 막아 줍니다.

## 폴더별로 무엇을 넣나

| 폴더 | 넣는 것 | 넣지 않는 것 |
|---|---|---|
| **src/패키지** | 배포되는 파이썬 코드 | 테스트, 실험 노트북 |
| **tests** | pytest 테스트 (src와 같은 구조) | 손으로 돌리는 임시 스크립트 |
| **docs** | 사람이 읽는 문서 | 자동 생성 결과 |

맨 위에는 **pyproject.toml**(이름·의존성), **README.md**, **.gitignore**를 둡니다.

## 예시 트리

```
my-tool/
├─ src/
│  └─ my_tool/
│     └─ __init__.py
├─ tests/
│  └─ test_smoke.py
├─ docs/
├─ pyproject.toml
├─ README.md
└─ .gitignore
```

## 이름 규칙

| 대상 | 규칙 | 예 |
|---|---|---|
| 모듈·패키지 | snake_case | `file_scanner.py` |
| 클래스 | PascalCase | `FileScanner` |
| 테스트 파일 | test_로 시작 | `test_file_scanner.py` |

## 유지 요령

- 개발 중에는 `pip install -e .` 로 설치해 두고 테스트합니다.
- .venv, __pycache__, dist는 커밋하지 않습니다.
- 패키지 이름은 하이픈 대신 밑줄을 씁니다.


## 이럴 땐 다른 스타일을

- 언어를 정하지 않은 일반 저장소라면 → **소프트웨어 프로젝트**
- 문서·설계가 중심이라면 → **문서 작성 단계**
