# How to use BIDS

> BIDS (Brain Imaging Data Structure) is the international standard for organizing brain imaging research data. Folders go participant → session → data type, and file names follow set rules. Laid out this way, analysis tools such as fMRIPrep and public repositories (OpenNeuro) can read the data right away.

## What goes in each folder

| Folder or file | What goes in |
|---|---|
| **dataset_description.json** | Dataset name and BIDS version (required) |
| **participants.tsv** | List of participants (one participant_id per line) |
| **README.md** | Description of the dataset |
| **sub-01** | One participant. Each participant gets a sub-<label> folder |
| **sub-01/ses-01** | One recording session. Can be left out if there's only one |
| **anat, func …** | Data type. Only the set names: anat (anatomical MRI), func (functional MRI), dwi (diffusion), fmap (field maps), eeg, meg, pet and so on |
| **sourcedata** | Originals before conversion (DICOM etc.) |
| **derivatives** | Processing and analysis results |
| **code** | Conversion and analysis scripts |

## Example tree

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

## Naming rules

| What | Rule | Example |
|---|---|---|
| Data files | sub-<participant>_ses-<session>_<type> | `sub-01_ses-01_T1w.nii.gz` |
| Files with a task | sub-<participant>_ses-<session>_task-<task>_<type> | `sub-01_ses-01_task-rest_bold.nii.gz` |

Folder and file names stay in English exactly as in the standard. Tools can't read other names.

## Keeping it tidy

- For each new participant, copy the sub-01 folder, rename it sub-02 and add a line to participants.tsv.
- A converter such as dcm2bids or HeuDiConv makes fewer mistakes than renaming by hand.
- When you're done, check everything with the BIDS Validator (https://bids-standard.github.io/bids-validator/).

## When another style fits better

- Statistical data rather than brain imaging → **TIER Protocol 4.0 (reproducible research)**
- Research based on literature and manuscripts → **Research & study project**

Source: BIDS Specification, https://bids-specification.readthedocs.io/ , https://bids.neuroimaging.io/
