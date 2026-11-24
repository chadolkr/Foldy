# BIDS の使い方

> BIDS (Brain Imaging Data Structure) は脳画像研究データを置く国際標準です。参加者 → セッション → データの種類の順にフォルダーを分け、ファイル名も決まったルールに従います。こうしておけば fMRIPrep などの解析ツールや公開リポジトリ (OpenNeuro) がそのまま読めます。

## フォルダーごとに何を入れるか

| フォルダー・ファイル | 入れるもの |
|---|---|
| **dataset_description.json** | データセット名と BIDS のバージョン (必須) |
| **participants.tsv** | 参加者の一覧 (一行に participant_id 一つ) |
| **README.md** | データセットの説明 |
| **sub-01** | 参加者一人。参加者ごとに sub-番号 のフォルダー |
| **sub-01/ses-01** | 測定一回。セッションが一つだけなら省略できます |
| **anat, func …** | データの種類。決まった名前だけを使います: anat (構造 MRI)、func (機能 MRI)、dwi (拡散)、fmap (フィールドマップ)、eeg、meg、pet など |
| **sourcedata** | 変換前の元データ (DICOM など) |
| **derivatives** | 処理・分析の結果 |
| **code** | 変換・分析スクリプト |

## ツリーの例

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

## 名前の付け方

| 対象 | ルール | 例 |
|---|---|---|
| データファイル | sub-参加者_ses-セッション_種類 | `sub-01_ses-01_T1w.nii.gz` |
| 課題のあるファイル | sub-参加者_ses-セッション_task-課題_種類 | `sub-01_ses-01_task-rest_bold.nii.gz` |

フォルダー名とファイル名は標準どおり英語のままにします。ほかの名前だとツールが読めません。

## 整理を続けるコツ

- 参加者が増えたら sub-01 フォルダーをコピーして sub-02 に名前を変え、participants.tsv に一行足します。
- 手で名前を変えるより dcm2bids・HeuDiConv などの変換ツールを使うほうが間違いが少なくなります。
- 終わったら BIDS Validator (https://bids-standard.github.io/bids-validator/) で確認します。

## こんなときは別のスタイルを

- 脳画像ではない統計データなら → **TIER Protocol 4.0 (再現可能な研究)**
- 文献・原稿が中心の研究なら → **研究・学習プロジェクト**

出典: BIDS Specification, https://bids-specification.readthedocs.io/ , https://bids.neuroimaging.io/
