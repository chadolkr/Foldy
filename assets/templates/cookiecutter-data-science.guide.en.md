# How to use Cookiecutter Data Science

> The data science project layout put together by DrivenData. Data goes in stages (raw → interim → processed), and notebooks, models and reports each get their own box. It is the most widely used layout in data science.

## What goes in each folder

| Folder | What goes in |
|---|---|
| **data/raw** | The original data you received. Don't edit it |
| **data/interim** | Data transformed partway |
| **data/processed** | Final data sets for modeling |
| **data/external** | Data from third-party sources |
| **docs** | Project documentation |
| **models** | Trained models and predictions |
| **notebooks** | Jupyter notebooks |
| **references** | Reference material such as data dictionaries and manuals |
| **reports** | Analysis reports (HTML, PDF) |
| **reports/figures** | Figures used in the reports |

Foldy creates only the folders above and a README.md. To also create the code folder and configuration files (pyproject.toml and so on), use the official `ccds` tool. Code is managed with Git.

## Example tree

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

## Naming rules

| What | Rule | Example |
|---|---|---|
| Notebooks | number-initials-description | `1.0-jqp-initial-data-exploration.ipynb` |

Folder names stay in English exactly as in the layout, because code uses these paths directly.

## Keeping it tidy

- Don't edit data/raw. Do every transformation in code so it can be rebuilt.
- data and models are large, so keep them out of Git.
- Notebooks are for exploring. Move code you use often out of notebooks into the code folder.

## When another style fits better

- A replication package for a statistics paper → **TIER Protocol 4.0 (reproducible research)**
- Brain imaging data → **BIDS (brain imaging data standard)**

Source: DrivenData, "Cookiecutter Data Science", https://cookiecutter-data-science.drivendata.org/
