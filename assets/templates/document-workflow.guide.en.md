# How to use Document stages

> Folders follow the stages a single document goes through. You can tell "how far along is it?" just by looking at the folders.

## What goes in each folder

| Folder | What goes in | What doesn't |
|---|---|---|
| **01_Draft** | The file you're writing and its versions | Files to send to others |
| **02_Review** | Files under review, feedback you received | Drafts still being written |
| **03_Final** | One confirmed final version | Several versions |
| **04_Distributed** | Files you actually sent (PDF etc.) and the date sent | Files to edit |

## Example tree

```
Business plan/
├─ 01_Draft/
│  ├─ BusinessPlan_v0.1_Draft.docx
│  └─ BusinessPlan_v0.2_Draft.docx
├─ 02_Review/
├─ 03_Final/
│  └─ BusinessPlan_v1.0_Final_2026-11-18.docx
├─ 04_Distributed/
│  └─ 2026-11-18_BusinessPlan_Investor.pdf
└─ README.md
```

## Naming rules

| What | Rule | Example |
|---|---|---|
| Working file | DocName_vVersion_Status | `BusinessPlan_v0.2_Draft.docx` |
| Final version | DocName_vVersion_Final_Date | `BusinessPlan_v1.0_Final_2026-11-18.docx` |
| Distributed copy | Date_DocName_Recipient | `2026-11-18_BusinessPlan_Investor.pdf` |

## Keeping it tidy

- **Copy** files when moving to the next stage. If you delete the earlier stage, you can't go back.
- Raise the version number instead of writing "final_really_final".
- Keep exactly one file in 03_Final.

## When another style fits better

- Many projects split by client → **Client project work**
- Writing papers or reports along with material and data → **Research & study project**
