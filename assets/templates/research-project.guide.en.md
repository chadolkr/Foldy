# How to use Research & study project

> Boxes follow the flow of reading (literature) → collecting (data) → analyzing → writing (manuscript) → presenting. Good for papers, graduation projects and study notes.

## What goes in each folder

| Folder | What goes in | What doesn't |
|---|---|---|
| **Literature** | Paper PDFs, reference lists (.bib), reading notes | Your own manuscript |
| **Data/Originals** | Data exactly as received or collected | Modified data |
| **Data/Cleaned** | Cleaned and processed data | Originals |
| **Analysis** | Analysis code and notebooks, result tables and figures | Original data |
| **Manuscript** | Paper and report manuscripts and their versions | Presentation slides |
| **Presentations** | Slides, posters | Manuscripts |

## Example tree

```
Sensor research/
├─ Literature/
├─ Data/
│  ├─ Originals/
│  └─ Cleaned/
├─ Analysis/
├─ Manuscript/
├─ Presentations/
└─ README.md
```

## Naming rules

| What | Rule | Example |
|---|---|---|
| Paper | Author_Year_Title | `Lee_2023_Sensor_Fusion.pdf` |
| Data | Content_Date | `Survey_2026-11-18.csv` |
| Manuscript, analysis | Name_vVersion | `Manuscript_v0.3.docx` |

## Keeping it tidy

- Never modify original data. Process it in the Cleaned folder, and keep how you processed it as code in Analysis.
- Write a one-line summary of each paper you read in README or your reading notes; it makes citing easier later.

## When another style fits better

- You only manage the draft, review and final of one document → **Document stages**
- The focus is building a system rather than research → **IT system development documents**
