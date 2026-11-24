# Cookiecutter Data Science 사용법

> DrivenData가 정리한 데이터 과학 프로젝트 구조입니다. 데이터는 단계별(raw → interim → processed)로, 노트북·모델·보고서는 각자 칸에 둡니다. 데이터 과학에서 가장 널리 쓰이는 틀입니다.

## 폴더별로 무엇을 넣나

| 폴더 | 넣는 것 |
|---|---|
| **data/raw** | 받은 원본 데이터. 고치지 않습니다 |
| **data/interim** | 중간에 바꾼 데이터 |
| **data/processed** | 모델에 쓰는 최종 데이터 |
| **data/external** | 다른 곳에서 받은 데이터 |
| **docs** | 프로젝트 문서 |
| **models** | 학습한 모델, 예측 결과 |
| **notebooks** | Jupyter 노트북 |
| **references** | 데이터 사전, 설명서 같은 참고 자료 |
| **reports** | 분석 보고서 (HTML, PDF) |
| **reports/figures** | 보고서에 쓰는 그림 |

Foldy는 위 폴더와 README.md만 만듭니다. 코드 폴더와 설정 파일(pyproject.toml 등)까지 만들려면 공식 도구 `ccds`를 쓰세요. 코드는 Git으로 관리합니다.

## 예시 트리

```
churn-model/
├─ README.md
├─ data/
│  ├─ raw/
│  ├─ interim/
│  ├─ processed/
│  └─ external/
├─ models/
├─ notebooks/
│  └─ 1.0-jqp-initial-data-exploration.ipynb
├─ references/
└─ reports/
   └─ figures/
```

## 이름 규칙

| 대상 | 규칙 | 예 |
|---|---|---|
| 노트북 | 번호-이니셜-설명 | `1.0-jqp-initial-data-exploration.ipynb` |

폴더 이름은 구조 그대로 영어로 둡니다. 코드가 이 경로를 그대로 씁니다.

## 유지 요령

- data/raw는 고치지 않습니다. 모든 변환은 코드로 해서 다시 만들 수 있게 합니다.
- data와 models는 크기가 커서 Git에 넣지 않습니다.
- 노트북은 탐색용입니다. 자주 쓰는 코드는 노트북에서 꺼내 코드 폴더로 옮깁니다.

## 이럴 땐 다른 스타일을

- 통계 분석 논문의 재현 자료라면 → **TIER Protocol 4.0 (재현 가능한 연구)**
- 뇌 영상 데이터라면 → **BIDS (뇌 영상 데이터 표준)**

출처: DrivenData, "Cookiecutter Data Science", https://cookiecutter-data-science.drivendata.org/
