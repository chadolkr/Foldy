# BIDS 使用方法

> BIDS（Brain Imaging Data Structure）是存放脑影像研究数据的国际标准。按 参与者 → 会话 → 数据类型 划分文件夹，文件名也遵循固定规则。这样存放后，fMRIPrep 等分析工具和公开数据库（OpenNeuro）都能直接读取。

## 每个文件夹放什么

| 文件夹或文件 | 放什么 |
|---|---|
| **dataset_description.json** | 数据集名称和 BIDS 版本（必需） |
| **participants.tsv** | 参与者列表（每行一个 participant_id） |
| **README.md** | 数据集说明 |
| **sub-01** | 一位参与者。每位参与者一个 sub-编号 文件夹 |
| **sub-01/ses-01** | 一次采集。只有一次时可以省略 |
| **anat, func …** | 数据类型。只用规定的名称：anat（结构 MRI）、func（功能 MRI）、dwi（扩散）、fmap（场图）、eeg、meg、pet 等 |
| **sourcedata** | 转换前的原始数据（DICOM 等） |
| **derivatives** | 处理与分析结果 |
| **code** | 转换与分析脚本 |

## 示例树

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

## 命名规则

| 对象 | 规则 | 示例 |
|---|---|---|
| 数据文件 | sub-参与者_ses-会话_类型 | `sub-01_ses-01_T1w.nii.gz` |
| 有任务的文件 | sub-参与者_ses-会话_task-任务_类型 | `sub-01_ses-01_task-rest_bold.nii.gz` |

文件夹和文件名称按标准保留英文。用其他名称工具就读不了。

## 保持整洁的诀窍

- 新增参与者时，复制 sub-01 文件夹、改名为 sub-02，并在 participants.tsv 里加一行。
- 用 dcm2bids、HeuDiConv 等转换工具比手动改名更少出错。
- 完成后用 BIDS Validator（https://bids-standard.github.io/bids-validator/）检查。

## 这些情况适合其他风格

- 不是脑影像的统计数据 → **TIER Protocol 4.0 (可重复研究)**
- 以文献和稿件为主的研究 → **研究与学习项目**

出处：BIDS Specification, https://bids-specification.readthedocs.io/ , https://bids.neuroimaging.io/
