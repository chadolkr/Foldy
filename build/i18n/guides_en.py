# -*- coding: utf-8 -*-
"""템플릿 사용법 (assets/templates/<id>.guide.md) 영어판. python build/i18n/guides.py 로 <id>.guide.en.md 를 만든다."""

LANG = 'en'

G = {
'para': r'''# How to use PARA

> Everything goes into one of four boxes, based on "when does it end?". With only four boxes, you spend little time deciding where things go.

## What goes in each folder

| Folder | What goes in | What doesn't |
|---|---|---|
| **1_Projects** | Work with a deadline or end point. E.g. moving house, the Q3 report, renewing a passport | Work that never ends (that's Areas) |
| **2_Areas** | Ongoing responsibilities that never end. E.g. health, finances, car, running a team | One-off tasks |
| **3_Resources** | Reference material by topic. E.g. recipes, design references, manuals | Documents for things you're responsible for |
| **4_Archives** | Things that are finished or no longer used. Folders moved from the other three | Material you use now |

Not sure where something goes? Ask in this order:

1. **When does it end?** If it has a date → 1_Projects
2. **Do I keep looking after it?** If it needs managing forever → 2_Areas
3. **Is it just for reference?** → 3_Resources
4. None of these and no longer used → 4_Archives

## Example tree

```
Documents/
├─ 1_Projects/
│  ├─ 2026-11_Moving/
│  └─ 2026-12_Year-end tax/
├─ 2_Areas/
│  ├─ Health/
│  └─ Finances/
├─ 3_Resources/
│  └─ Cooking/
└─ 4_Archives/
   └─ 2026-03_Passport renewal/
```

## Naming rules

- Pattern: `Date_Topic_Content[_vVersion].ext`
- E.g. `2026-11-18_Moving_QuoteComparison_v02.xlsx`, `2026-03-02_Checkup_Results.pdf`
- Name project folders `StartYearMonth_Name` (2026-11_Moving) so they sort by start date.
- Use two-digit versions like `_v01`, `_v02`. Avoid "final" and "really final".

## Keeping it tidy

- When a project ends, move its folder **as a whole** to 4_Archives. Don't delete it.
- Once a month, skim 1_Projects and move finished work to the archive.
- 2_Areas rarely changes. If you have more than 10 areas, check whether you've split them too finely.
- Stopping subfolders at 2–3 levels makes things easier to find.

## When another style fits better

- Lots of material that you want to reach directly by number → **Johnny.Decimal**
- Freelance or sales work split by client → **Client project work**
- Mostly photos and videos → **Photo & video archive**
''',
'johnny-decimal': r'''# How to use Johnny.Decimal

> Every place gets a number. Say "22.04" and its location is settled. Great when you have lots of material and often look up the same things again.

## Three levels

| Level | Looks like | Meaning | Limit |
|---|---|---|---|
| **Area** | `10-19`, `20-29` | A big topic. One range of ten is one area | up to 10 |
| **Category** | `11`, `12`, `21` | A box inside an area, numbered within the area's range | up to 10 per area |
| **ID** | `11.01`, `22.04` | The actual place where material goes | 01–99 per category |

## What goes in / what doesn't

- **Area folders** contain only category folders. Don't put files there directly.
- **Category folders** contain ID folders (`11.01 Name`) or files with an ID number.
- **00-09 System** holds only the number list (the index document) and notes about the system itself.
- **Never go deeper than three levels.** If you want to split further, take a new ID number.

## Keeping the number list

The number list (`00.00 Number list.md`) is the heart of this method. Write one line every time you create a new number, and you'll know what's where without opening folders, and you'll never use the same number twice.

## Example tree

```
00-09 System/
└─ 00 Index/
   └─ 00.00 Number list.md
10-19 Life/
├─ 11 Home/
│  └─ 11.01 Lease agreement/
└─ 12 Car/
20-29 Money/
├─ 22 Taxes/
│  └─ 22.04 2026 Year-end tax/
└─ 23 Insurance/
```

## Naming rules

- Pattern: `Number Content.ext` — the number is the location.
- E.g. `11.01 Car insurance policy.pdf`, `22.04 2026 Year-end tax.xlsx`
- One space between number and name. Numbers always have two digits (`11.01`, `11.10`).

## Keeping it tidy

- Don't fill every box at the start; begin with 3–5 areas and a few categories.
- If a category looks like it'll exceed 10, it's a sign to split the area.
- Never reuse numbers. Move unused IDs to an archive category (e.g. `39 Archive`).

## When another style fits better

- Managing numbers feels like a chore and you just want to split by "is it finished?" → **PARA**
- Photos and videos where date order matters → **Photo & video archive**
''',
'number-prefix': r'''# How to use Numbers and dates

> The easiest way to organize. Numbers in front of folders keep their order fixed, and file names that start with the date make name order the same as date order.

| Folder | What goes in |
|---|---|
| **00_Inbox** | Put things here first: received files, things you haven't decided where to put (it's at the top, so you always see it) |
| **01_In progress** | What you're working on now |
| **02_Materials** | Reference material you look at often |
| **03_Done** | Finished but looked at now and then |
| **99_Archive** | Old and rarely looked at (99, so always at the bottom) |

## Using the inbox

1. Put new files in **00_Inbox** without thinking about it.
2. Once a week, empty 00_Inbox by sorting it into 01–03.
3. Once it's empty, you're done! The key is not letting it pile up.

## File names

Name files **Date_Content**, like `2026-11-19_Report.docx`, and they line up in date order just by sorting by name.

## When another style fits better

- You want to separate ongoing work from ongoing responsibilities → **PARA**
- So many folders that you want to jump straight to them by number → **Johnny.Decimal**
- Material where the date is everything, like photos and scans → **Archive by date**
''',
'date-archive': r'''# How to use Archive by date

> No deciding where things go. Just drop each file into the year-month folder of the date it came into being.

## What goes in each folder

| Folder | What goes in | What doesn't |
|---|---|---|
| **Year (2026)** | That year's year-month folders | No files directly |
| **Year-month (2026-11)** | Files created or received that month | Working files you keep editing for a long time |

It suits records you rarely look at again once they've passed (receipts, bills, screenshots, received files) better than material you look up by topic.

## Example tree

```
Archive/
└─ 2026/
   ├─ 2026-10/
   └─ 2026-11/
      ├─ 2026-11-03_Meeting_notes.md
      └─ 2026-11-18_Electricity_bill.pdf
```

## Naming rules

Name things Date_Content. Sorting by name then sorts by time.

| What | Rule | Example |
|---|---|---|
| File | YYYY-MM-DD_Content | `2026-11-18_Electricity_bill.pdf` |
| Folder | YYYY-MM | `2026-11` |

## Keeping it tidy

- When the month changes, create a new year-month folder. Using this template again on the same archive creates only the missing folders.
- Always use the date the thing "came into being". Mixing received dates and written dates gets confusing.
- You can also move files that have already piled up into this structure with the "By date" style of folder tidying.

## When another style fits better

- You need to find things by project or topic → **PARA** or **Johnny.Decimal**
- Photos and videos → **Photo & video archive** (Date_Event plus a separate RAW folder)
''',
'client-project': r'''# How to use Client project work

> Projects sit under each client folder in start-date order, and inside them, numbered folders follow the order of the work (contract → materials → work → deliverables).

## What goes in each folder

| Folder | What goes in | What doesn't |
|---|---|---|
| **Client name/** | That client's project folders | Material that mixes several clients |
| **Date_Project/** | Everything for one project. The date is the start date | — |
| **01_Contract** | Contracts, quotes, purchase orders, NDAs, invoices | Drafts in progress |
| **02_Materials** | Original material from the client (never edited) | Files you've edited |
| **03_Work** | Drafts, working files, meeting notes | Final versions you sent |
| **04_Deliverables** | Only the final versions sent to the client | Drafts and interim versions |
| **99_Archive** | What to keep after the end, things no longer used | — |

## Example tree

```
ACME/
└─ 2026-11-18_Website redesign/
   ├─ 01_Contract/
   │  └─ 2026-11-18_ACME_Contract_Signed.pdf
   ├─ 02_Materials/
   ├─ 03_Work/
   ├─ 04_Deliverables/
   │  └─ 2026-12-20_ACME_DesignDraft_v03.pdf
   └─ 99_Archive/
```

## Naming rules

- Pattern: `Date_Client_DocType_vVersion.ext`
- E.g. `2026-11-18_ACME_Proposal_v03.pptx`, `2026-12-01_ACME_Contract_Signed.pdf`
- Instead of adding `_final` to names, use the **date**. The most recent date is the latest version.
- Versions start at `_v01`, with two digits.

## Keeping it tidy

- Keep final versions **only in 04_Deliverables**. Don't keep copies of the same file elsewhere.
- Don't edit originals in 02_Materials; copy them to 03_Work and edit there.
- When a project ends, leave it in the client folder, or move just what needs tidying to 99_Archive.
- Spell each client name one way only (don't mix "ACME" and "Acme Inc.").

## When another style fits better

- Personal and work matters mixed, with no client split → **PARA**
- A project that builds software → **IT system development documents**
''',
'document-workflow': r'''# How to use Document stages

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
''',
'household-life': r'''# How to use Household papers

> Papers you look for a few times a year, split into six boxes. The goal is to find anything within three clicks when you're in a hurry.

## What goes in each folder

| Folder | What goes in | What doesn't |
|---|---|---|
| **Finances** | Tax returns, bank and card statements, year-end tax | Every everyday receipt |
| **Health** | Visit and prescription records, checkup results | — |
| **Home** | Contracts and property records, utilities and fees, appliance manuals | — |
| **Vehicle** | Registration, maintenance records, proof of insurance | — |
| **Passport & ID** | Copies of passports, ID cards and certificates | The physical originals |
| **Insurance** | Policies, terms, claim records | — |

## Example tree

```
Household papers/
├─ Finances/
│  ├─ Taxes/
│  └─ Bank & cards/
├─ Health/
│  ├─ Visits & prescriptions/
│  └─ Checkups/
├─ Home/
│  ├─ Contracts & deeds/
│  ├─ Utilities & fees/
│  └─ Repairs & manuals/
├─ Vehicle/
├─ Passport & ID/
└─ Insurance/
```

## Naming rules

Date_Organization_Content. Use the date written on the document.

| What | Rule | Example |
|---|---|---|
| Document | Date_Organization_Content | `2026-05-31_TaxOffice_IncomeTaxReturn.pdf` |

## Keeping it tidy

- Keep sensitive papers such as Passport & ID and Insurance in an encrypted folder or a password-protected backup. Take extra care when uploading to the cloud.
- For papers with an expiry date (passport, insurance), write the expiry date in the file name or your calendar.
- Tidy away papers you no longer need after five years only after checking them yourself. Foldy never deletes anything.

## When another style fits better

- More work or study material than papers → **PARA**
- You just want to pile things up by date → **Archive by date**
''',
'photo-archive': r'''# How to use Photo & video archive

> With year → Date_Event, sorting by name is sorting by time. Originals live in one place only and are never edited.

## What goes in each folder

| Folder | What goes in | What doesn't |
|---|---|---|
| **Year/** | That year's event folders | Photos from mixed years |
| **Date_Event/** | One event. The date is the first day | — |
| **RAW** | Originals exactly as they came from the camera or phone | Retouched files |
| **Edited** | Retouched, cropped or composited files | Originals |
| **Exports** | Files resized or converted for sharing or printing | Files in progress |

## Example tree

```
Photos/
└─ 2026/
   ├─ 2026-08-02_Summer holiday/
   └─ 2026-11-18_JejuTrip/
      ├─ RAW/
      │  └─ 2026-11-18_143205_JejuTrip_001.jpg
      ├─ Edited/
      │  └─ 2026-11-18_JejuTrip_Edited_001.jpg
      └─ Exports/
```

## Naming rules

- Pattern: `Date_Time_Event_Number.ext` (by shooting time)
- E.g. `2026-11-18_143205_JejuTrip_001.jpg`, `2026-11-18_JejuTrip_Edited_001.jpg`
- Names given by the camera (IMG_1234) can be changed all at once with "Name by shooting time" in Foldy's batch rename.

## Keeping it tidy

- **Never edit RAW.** Always retouch a copy, in Edited.
- With just RAW backed up you can recreate everything else, so back up RAW first.
- Keep event names short. Long full paths cause problems when moving to other devices.
- On the day you import, pick out blurry or duplicate shots right away to save effort later (delete only after checking yourself).

## When another style fits better

- Photos are part of documents (site photos, evidence) → 02_Materials in **Client project work**
- More documents than photos → **PARA**
''',
'design-assets': r'''# How to use Design & creative assets

> Four boxes that keep apart what mustn't be touched (originals), what you're making (work), the results (exports) and inspiration (reference).

## What goes in each folder

| Folder | What goes in | What doesn't |
|---|---|---|
| **Originals** | Received images, photos, assets, fonts | Edited files |
| **Work** | Working files (psd, ai, fig, project files) and their versions | Finished results |
| **Exports** | Results exported for delivery or publishing (png, jpg, pdf, mp4) | Files in progress |
| **Reference** | References, mood boards, guidelines | Your own results |

## Example tree

```
Brand logo/
├─ Originals/
├─ Work/
│  ├─ Logo_Main_v2.ai
│  └─ Logo_Main_v3.ai
├─ Exports/
│  ├─ Logo_Web_512.png
│  └─ Logo_Print.pdf
└─ Reference/
```

## Naming rules

| What | Rule | Example |
|---|---|---|
| Working file | Job_Content_vVersion | `Logo_Main_v3.ai` |
| Exported file | Job_Use_Size | `Logo_Web_512.png` |

## Keeping it tidy

- Never edit originals. If you need to change one, copy it to Work and edit there.
- Don't overwrite working files; save with a higher v number so it's easy to go back.
- For large jobs, back up Originals and Work first.

## When another style fits better

- You just pile up photos and videos by date → **Photo & video archive**
- Work you deliver to clients → **Client project work**
''',
'research-project': r'''# How to use Research & study project

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
''',
'pm-project': r'''# How to use Project management (PMBOK 5 phases)

> A way to organize project documents in any industry. Folders follow the five process groups of the PMBOK Guide from the Project Management Institute (PMI): initiating, planning, executing, monitoring and controlling, and closing.

## What goes in each folder

| Folder | What goes in |
|---|---|
| **01_Initiating** | Project charter, stakeholder register, approval to start |
| **02_Planning** | Scope, schedule (WBS), budget, quality, risk and communication plans |
| **03_Executing** | Deliverables, meeting notes, working material |
| **04_Monitoring and controlling** | Weekly and monthly progress reports, change requests, issue and risk logs |
| **05_Closing** | Final report, acceptance sign-off, lessons learned (retrospective), contract closure |
| **99_Archive** | Old versions, things no longer used |

## Keeping it tidy

- Monitoring and controlling runs alongside executing. Put reports and change requests in 04 as they come.
- Leaving lessons learned in 05_Closing at the end makes the next project easier.

## When another style fits better

- A project that builds software → **IT system development documents**
- Construction and design, where several teams exchange drawings and models → **Construction & design information**
- Splitting just contracts and deliverables by client is enough → **Client project work**
''',
'it-project': r'''# How to use IT system development documents (ISO/IEC/IEEE 12207)

> **Documents** are split in the order of the technical processes of ISO/IEC/IEEE 12207, the international standard for the software life cycle (requirements → analysis → design → implementation → testing → transition → operation and maintenance). Source code stays in a Git repository; this folder only notes where the repository is.

## What goes in each folder

| Folder | What goes in | Process in the standard |
|---|---|---|
| **01_Requirements** | Incoming requests, stakeholder requirements, requirements specification | Stakeholder requirements, system/software requirements definition |
| **02_Analysis** | Current state and business analysis, system analysis, feasibility study | Business or mission analysis, system analysis |
| **03_Design** | Architecture, detailed design, screen and data design | Architecture definition, design definition |
| **04_Implementation** | Implementation notes, build and deployment setup, repository location | Implementation, integration |
| **05_Testing** | Test plans, scenarios and results, defect list | Verification, validation |
| **06_Transition** | Cutover and deployment plans, data migration, user training | Transition |
| **07_Operation and maintenance** | Operations manual, incident log, change and improvement requests | Operation, maintenance |
| **99_Archive** | Old versions, things no longer used | — |

## Keeping it tidy

- When requirements change, put the new version in 01 (date and v number) and move the old one to 99_Archive.
- Noting which requirements each test result checks makes tracing easy.

## When another style fits better

- Schedule and reporting focused, for any industry → **Project management (PMBOK 5 phases)**
- Outsourced work per client company → **Client project work**
''',
'cde-project': r'''# How to use Construction & design information (ISO 19650 CDE)

> Drawings, models and documents are split by the four states of the Common Data Environment (CDE) in ISO 19650, the international standard for construction information management (work in progress → shared → published → archived). The folder tells you at a glance what state a file is in.

## What goes in each folder

| Folder | What goes in |
|---|---|
| **01_Work in progress** (WIP) | What your team is still editing. Not seen outside the team |
| **02_Shared** (Shared) | What's been released to other disciplines and teams for coordination and review |
| **03_Published** (Published) | Approved final versions the site or client may use |
| **04_Archived** (Archived) | Superseded by a new revision or finished. Kept as a record |

## Naming rules

- Pattern: `Project_Discipline_DocType_Number_Revision.ext` (e.g. `NewServiceCenter_Arch_FloorPlan_A101_P02.pdf`)
- Follow your company's or client's revision codes (e.g. P01 for review, C01 for construction).

## Keeping it tidy

- Moving between states goes through the agreed review and approval. Nothing jumps from 01 straight to 03.
- Never edit published versions. To change something, make a new revision in 01_Work in progress.

## When another style fits better

- An ordinary work project without drawings → **Project management (PMBOK 5 phases)**
''',
}
