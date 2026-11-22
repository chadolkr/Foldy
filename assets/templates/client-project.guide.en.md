# How to use Client project work

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
