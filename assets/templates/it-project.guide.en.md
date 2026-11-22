# How to use IT system development documents (ISO/IEC/IEEE 12207)

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
