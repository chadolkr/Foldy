# How to use TIER Protocol 4.0

> The research folder specification from Project TIER (Teaching Integrity in Empirical Research). The original data stays untouched and every change is made by scripts, so **anyone who runs the scripts gets the same results**. It suits statistical and empirical research and replication packages for journals.

## What goes in each folder

| Folder | What goes in |
|---|---|
| **Data/InputData** | The original data you received. Never edit it |
| **Data/InputData/Metadata** | Data sources guide (where and when you got it) and codebooks |
| **Data/IntermediateData** | Data made partway through processing |
| **Data/AnalysisData** | Final analysis data and the data appendix |
| **Scripts/ProcessingScripts** | Scripts that turn the originals into analysis data |
| **Scripts/DataAppendixScripts** | Scripts that build the data appendix (variable summaries) |
| **Scripts/AnalysisScripts** | Scripts that make the tables, figures and results |
| **Output/DataAppendixOutput** | Data appendix output |
| **Output/Results** | Study results such as tables and figures |

At the top go the **Readme** (how to read and run everything) and the **report** (your paper). The **master script** that runs every script in order goes in Scripts.

## Example tree

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

## Naming rules

| What | Rule | Example |
|---|---|---|
| Folders and files | No spaces; capitalize each word | `ProcessingSurveyData.R` |

Folder names stay in English exactly as the specification has them, so reviewers and co-authors recognize them at once.

## Keeping it tidy

- Never open and edit the files in InputData. Any change is made by a script.
- You should be able to delete Output at any time and rebuild it with the master script.
- When you publish, zip the whole folder as your replication package.

## When another style fits better

- Research based on reading and writing manuscripts → **Research & study project**
- You build machine learning models → **Cookiecutter Data Science**

Source: Project TIER, "TIER Protocol 4.0", https://www.projecttier.org/tier-protocol/protocol-4-0/root/
