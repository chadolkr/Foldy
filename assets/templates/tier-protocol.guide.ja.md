# TIER Protocol 4.0 の使い方

> Project TIER (Teaching Integrity in Empirical Research) が定めた研究フォルダーの仕様です。元データはそのままにし、手を加えるのはすべてスクリプトで行うので、**スクリプトを実行すれば誰でも同じ結果** が得られます。統計・実証研究や、論文の再現用資料に向いています。

## フォルダーごとに何を入れるか

| フォルダー | 入れるもの |
|---|---|
| **Data/InputData** | 受け取った元データ。決して手を加えません |
| **Data/InputData/Metadata** | データの出典ガイド (どこでいつ入手したか) とコードブック |
| **Data/IntermediateData** | 処理の途中で作ったデータ |
| **Data/AnalysisData** | 分析に使う最終データとデータ付録 |
| **Scripts/ProcessingScripts** | 元データを分析用データに変えるスクリプト |
| **Scripts/DataAppendixScripts** | データ付録 (変数の要約) を作るスクリプト |
| **Scripts/AnalysisScripts** | 表・図・結果を作るスクリプト |
| **Output/DataAppendixOutput** | データ付録の結果 |
| **Output/Results** | 表・図などの研究結果 |

いちばん上には **Readme** (資料の見方・実行のしかた) と **レポート** (論文) を置きます。すべてのスクリプトを順に実行する **マスタースクリプト** は Scripts に置きます。

## ツリーの例

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

## 名前の付け方

| 対象 | ルール | 例 |
|---|---|---|
| フォルダー・ファイル | 空白なし、単語ごとに頭文字を大文字に | `ProcessingSurveyData.R` |

フォルダー名は仕様どおり英語のままにします。査読者や共同研究者がすぐにわかります。

## 整理を続けるコツ

- InputData のファイルは開いて直しません。直すことがあればスクリプトで行います。
- Output はいつ消してもマスタースクリプトで作り直せるようにします。
- 論文を出すときは、このフォルダー全体を圧縮して再現用資料として出します。

## こんなときは別のスタイルを

- 文献を読んで原稿を書く研究なら → **研究・学習プロジェクト**
- 機械学習モデルを作るなら → **Cookiecutter Data Science**

出典: Project TIER, "TIER Protocol 4.0", https://www.projecttier.org/tier-protocol/protocol-4-0/root/
