# TIER Protocol 4.0 使用方法

> Project TIER（Teaching Integrity in Empirical Research）制定的研究文件夹规范。原始数据保持不动，所有修改都用脚本完成，所以**任何人运行脚本都能得到相同的结果**。适合统计与实证研究，以及期刊要求的可重复资料包。

## 每个文件夹放什么

| 文件夹 | 放什么 |
|---|---|
| **Data/InputData** | 收到的原始数据。绝不修改 |
| **Data/InputData/Metadata** | 数据来源说明（何时何处获得）和编码手册 |
| **Data/IntermediateData** | 处理过程中产生的数据 |
| **Data/AnalysisData** | 分析用的最终数据和数据附录 |
| **Scripts/ProcessingScripts** | 把原始数据转成分析数据的脚本 |
| **Scripts/DataAppendixScripts** | 生成数据附录（变量摘要）的脚本 |
| **Scripts/AnalysisScripts** | 生成表格、图和结果的脚本 |
| **Output/DataAppendixOutput** | 数据附录输出 |
| **Output/Results** | 表格、图等研究结果 |

最上层放 **Readme**（如何查看和运行全部内容）和 **报告**（论文）。按顺序运行所有脚本的**主脚本**放在 Scripts。

## 示例树

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

## 命名规则

| 对象 | 规则 | 示例 |
|---|---|---|
| 文件夹和文件 | 不含空格，每个单词首字母大写 | `ProcessingSurveyData.R` |

文件夹名称按规范保留英文，审稿人和合作者一看就懂。

## 保持整洁的诀窍

- 不要打开修改 InputData 里的文件。需要改动就用脚本。
- Output 随时可以删掉，再用主脚本重新生成。
- 发表时把整个文件夹压缩，作为可重复资料包提交。

## 这些情况适合其他风格

- 以阅读文献、撰写稿件为主的研究 → **研究与学习项目**
- 要构建机器学习模型 → **Cookiecutter Data Science**

出处：Project TIER, "TIER Protocol 4.0", https://www.projecttier.org/tier-protocol/protocol-4-0/root/
