# Cookiecutter Data Science 使用方法

> DrivenData 整理的数据科学项目结构。数据按阶段（raw → interim → processed）存放，笔记本、模型和报告各有自己的格子。这是数据科学中使用最广的结构。

## 每个文件夹放什么

| 文件夹 | 放什么 |
|---|---|
| **data/raw** | 收到的原始数据。不要修改 |
| **data/interim** | 中间转换过的数据 |
| **data/processed** | 建模用的最终数据 |
| **data/external** | 来自第三方的数据 |
| **docs** | 项目文档 |
| **models** | 训练好的模型和预测结果 |
| **notebooks** | Jupyter 笔记本 |
| **references** | 数据字典、说明书等参考资料 |
| **reports** | 分析报告（HTML、PDF） |
| **reports/figures** | 报告中用的图 |

Foldy 只创建上面的文件夹和 README.md。如果还要创建代码文件夹和配置文件（pyproject.toml 等），请使用官方工具 `ccds`。代码用 Git 管理。

## 示例树

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

## 命名规则

| 对象 | 规则 | 示例 |
|---|---|---|
| 笔记本 | 编号-缩写-说明 | `1.0-jqp-initial-data-exploration.ipynb` |

文件夹名称按结构保留英文，因为代码直接使用这些路径。

## 保持整洁的诀窍

- 不修改 data/raw。所有转换都用代码完成，以便重新生成。
- data 和 models 体积大，不放进 Git。
- 笔记本用于探索。常用的代码从笔记本移到代码文件夹。

## 这些情况适合其他风格

- 统计论文的可重复资料包 → **TIER Protocol 4.0 (可重复研究)**
- 脑影像数据 → **BIDS (脑影像数据标准)**

出处：DrivenData, "Cookiecutter Data Science", https://cookiecutter-data-science.drivendata.org/
