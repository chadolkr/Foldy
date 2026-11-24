# BIDS 사용법

> BIDS(Brain Imaging Data Structure)는 뇌 영상 연구 데이터를 두는 국제 표준입니다. 참가자 → 세션 → 데이터 종류 순서로 폴더를 나누고, 파일 이름도 정해진 규칙을 따릅니다. 이렇게 두면 fMRIPrep 같은 분석 도구와 공개 저장소(OpenNeuro)가 바로 읽습니다.

## 폴더별로 무엇을 넣나

| 폴더·파일 | 넣는 것 |
|---|---|
| **dataset_description.json** | 데이터셋 이름과 BIDS 버전 (꼭 있어야 함) |
| **participants.tsv** | 참가자 목록 (participant_id 한 줄에 한 명) |
| **README.md** | 데이터셋 설명 |
| **sub-01** | 참가자 한 명. 참가자마다 sub-번호 폴더 |
| **sub-01/ses-01** | 측정 한 번. 세션이 하나뿐이면 없어도 됩니다 |
| **anat, func …** | 데이터 종류. anat(구조 MRI), func(기능 MRI), dwi(확산), fmap(필드맵), eeg, meg, pet 등 정해진 이름만 씁니다 |
| **sourcedata** | 변환하기 전 원본 (DICOM 등) |
| **derivatives** | 처리·분석 결과 |
| **code** | 변환·분석 스크립트 |

## 예시 트리

```
MyStudy/
├─ dataset_description.json
├─ participants.tsv
├─ README.md
├─ sub-01/
│  └─ ses-01/
│     ├─ anat/
│     │  └─ sub-01_ses-01_T1w.nii.gz
│     └─ func/
│        └─ sub-01_ses-01_task-rest_bold.nii.gz
├─ sourcedata/
├─ derivatives/
└─ code/
```

## 이름 규칙

| 대상 | 규칙 | 예 |
|---|---|---|
| 데이터 파일 | sub-참가자_ses-세션_종류 | `sub-01_ses-01_T1w.nii.gz` |
| 과제가 있는 파일 | sub-참가자_ses-세션_task-과제_종류 | `sub-01_ses-01_task-rest_bold.nii.gz` |

폴더와 파일 이름은 표준 그대로 영어로 둡니다. 다른 이름을 쓰면 도구가 읽지 못합니다.

## 유지 요령

- 참가자가 늘면 sub-01 폴더를 복사해 sub-02로 이름을 바꾸고 participants.tsv에 한 줄 더합니다.
- 손으로 이름을 바꾸기보다 dcm2bids·HeuDiConv 같은 변환 도구를 쓰면 실수가 적습니다.
- 마치면 BIDS Validator(https://bids-standard.github.io/bids-validator/)로 검사합니다.

## 이럴 땐 다른 스타일을

- 뇌 영상이 아닌 통계 데이터라면 → **TIER Protocol 4.0 (재현 가능한 연구)**
- 문헌·원고 중심 연구라면 → **연구·학습 프로젝트**

출처: BIDS Specification, https://bids-specification.readthedocs.io/ , https://bids.neuroimaging.io/
