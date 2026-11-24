# TIER Protocol 4.0 사용법

> Project TIER(Teaching Integrity in Empirical Research)가 정한 연구 폴더 사양입니다. 원본 데이터는 그대로 두고, 고치는 일은 모두 스크립트로 해서 **누구나 스크립트만 돌리면 같은 결과**가 나오게 합니다. 통계·실증 연구, 논문 심사의 재현 자료에 맞습니다.

## 폴더별로 무엇을 넣나

| 폴더 | 넣는 것 |
|---|---|
| **Data/InputData** | 받은 원본 데이터. 절대 고치지 않습니다 |
| **Data/InputData/Metadata** | 데이터 출처 안내(어디서 언제 받았나)와 코드북 |
| **Data/IntermediateData** | 처리 중간에 만든 데이터 |
| **Data/AnalysisData** | 분석에 쓰는 최종 데이터와 데이터 부록 |
| **Scripts/ProcessingScripts** | 원본을 분석용 데이터로 바꾸는 스크립트 |
| **Scripts/DataAppendixScripts** | 데이터 부록(변수 요약)을 만드는 스크립트 |
| **Scripts/AnalysisScripts** | 표·그림·결과를 만드는 스크립트 |
| **Output/DataAppendixOutput** | 데이터 부록 결과 |
| **Output/Results** | 표·그림 같은 연구 결과 |

맨 위에는 **Readme**(이 자료를 어떻게 보고 돌리는지)와 **보고서**(논문)를 둡니다. 모든 스크립트를 차례로 실행하는 **마스터 스크립트**는 Scripts에 둡니다.

## 예시 트리

```
WageStudy/
├─ Readme.txt
├─ Report.pdf
├─ Data/
│  ├─ InputData/
│  │  ├─ survey2025.csv
│  │  └─ Metadata/
│  ├─ IntermediateData/
│  └─ AnalysisData/
├─ Scripts/
│  ├─ MasterScript.R
│  ├─ ProcessingScripts/
│  ├─ DataAppendixScripts/
│  └─ AnalysisScripts/
└─ Output/
   ├─ DataAppendixOutput/
   └─ Results/
```

## 이름 규칙

| 대상 | 규칙 | 예 |
|---|---|---|
| 폴더·파일 | 빈칸 없이, 단어마다 첫 글자 대문자 | `ProcessingSurveyData.R` |

폴더 이름은 사양 그대로 영어로 둡니다. 심사자·공동 연구자가 바로 알아봅니다.

## 유지 요령

- InputData의 파일은 열어서 고치지 않습니다. 고칠 일이 있으면 스크립트로 합니다.
- Output은 언제든 지우고 마스터 스크립트로 다시 만들 수 있어야 합니다.
- 논문을 낼 때 이 폴더 전체를 압축해 재현 자료로 냅니다.

## 이럴 땐 다른 스타일을

- 문헌을 읽고 원고를 쓰는 연구라면 → **연구·학습 프로젝트**
- 머신러닝 모델을 만든다면 → **Cookiecutter Data Science**

출처: Project TIER, "TIER Protocol 4.0", https://www.projecttier.org/tier-protocol/protocol-4-0/root/
