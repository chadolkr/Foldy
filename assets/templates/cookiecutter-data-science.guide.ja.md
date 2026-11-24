# Cookiecutter Data Science の使い方

> DrivenData がまとめたデータサイエンス・プロジェクトの構成です。データは段階ごと (raw → interim → processed) に、ノートブック・モデル・レポートはそれぞれの区分に置きます。データサイエンスでいちばん広く使われている形です。

## フォルダーごとに何を入れるか

| フォルダー | 入れるもの |
|---|---|
| **data/raw** | 受け取った元データ。手を加えません |
| **data/interim** | 途中で変換したデータ |
| **data/processed** | モデルに使う最終データ |
| **data/external** | 外部から受け取ったデータ |
| **docs** | プロジェクトの文書 |
| **models** | 学習したモデル、予測結果 |
| **notebooks** | Jupyter ノートブック |
| **references** | データ辞書・説明書などの参考資料 |
| **reports** | 分析レポート (HTML、PDF) |
| **reports/figures** | レポートに使う図 |

Foldy が作るのは上のフォルダーと README.md だけです。コードのフォルダーや設定ファイル (pyproject.toml など) まで作るには公式ツール `ccds` を使ってください。コードは Git で管理します。

## ツリーの例

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

## 名前の付け方

| 対象 | ルール | 例 |
|---|---|---|
| ノートブック | 番号-イニシャル-説明 | `1.0-jqp-initial-data-exploration.ipynb` |

フォルダー名は構成どおり英語のままにします。コードがこのパスをそのまま使います。

## 整理を続けるコツ

- data/raw は直しません。変換はすべてコードで行い、作り直せるようにします。
- data と models は大きいので Git に入れません。
- ノートブックは探索用です。よく使うコードはノートブックから出してコードのフォルダーへ移します。

## こんなときは別のスタイルを

- 統計分析論文の再現用資料なら → **TIER Protocol 4.0 (再現可能な研究)**
- 脳画像データなら → **BIDS (脳画像データ標準)**

出典: DrivenData, "Cookiecutter Data Science", https://cookiecutter-data-science.drivendata.org/
