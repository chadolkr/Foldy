# -*- coding: utf-8 -*-
"""문서 캡처의 예시 Markdown 문서 (프로젝트 안내). (키, ko, en, es, ja, zh-Hans)"""

KO = """# 할 일 앱 프로젝트 안내

![표지](images/cover.png)

간단한 **할 일 관리 앱**을 만드는 프로젝트입니다. 이 문서는 팀원이 처음 읽는 안내서입니다.

## 목표

- 할 일을 추가·완료·삭제할 수 있다
- 마감일과 검색을 지원한다
- 데이터는 로컬 JSON 파일에 저장한다

## 일정

| 단계 | 기간 | 담당 | 상태 |
| --- | --- | --- | --- |
| 설계 | 10/01 ~ 10/07 | 김민지 | 완료 |
| 구현 | 10/08 ~ 10/31 | 박준 | 진행 중 |
| 시험 | 11/01 ~ 11/10 | 김민지 | 예정 |

## 해야 할 일

- [x] 저장소 만들기
- [x] 기본 화면 설계
- [ ] 검색 기능 마무리
- [ ] 설치 안내 작성

## 실행 방법

```python
from src.app import TodoApp

app = TodoApp("todos.json")
app.add("보고서 쓰기")
print(app.list())
```

> 자세한 내용은 `docs/design.md` 를 보세요."""

EN = """# To-do app project guide

![Cover](images/cover.png)

A project to build a simple **to-do list app**. This is the first guide new team members read.

## Goals

- Add, complete and delete to-dos
- Support due dates and search
- Store data in a local JSON file

## Schedule

| Phase | Dates | Owner | Status |
| --- | --- | --- | --- |
| Design | 10/01 – 10/07 | Minji Kim | Done |
| Build | 10/08 – 10/31 | Joon Park | In progress |
| Test | 11/01 – 11/10 | Minji Kim | Planned |

## To do

- [x] Create the repository
- [x] Design the basic screen
- [ ] Finish search
- [ ] Write install instructions

## How to run

```python
from src.app import TodoApp

app = TodoApp("todos.json")
app.add("Write the report")
print(app.list())
```

> See `docs/design.md` for details."""

ES = """# Guía del proyecto de la app de tareas

![Portada](images/cover.png)

Un proyecto para crear una sencilla **app de lista de tareas**. Es la primera guía que leen quienes se unen al equipo.

## Objetivos

- Añadir, completar y borrar tareas
- Fechas límite y búsqueda
- Guardar los datos en un archivo JSON local

## Calendario

| Fase | Fechas | Responsable | Estado |
| --- | --- | --- | --- |
| Diseño | 01/10 – 07/10 | Minji Kim | Hecho |
| Desarrollo | 08/10 – 31/10 | Joon Park | En curso |
| Pruebas | 01/11 – 10/11 | Minji Kim | Previsto |

## Pendiente

- [x] Crear el repositorio
- [x] Diseñar la pantalla básica
- [ ] Terminar la búsqueda
- [ ] Escribir la guía de instalación

## Cómo ejecutarla

```python
from src.app import TodoApp

app = TodoApp("todos.json")
app.add("Escribir el informe")
print(app.list())
```

> Consulta `docs/design.md` para más detalles."""

JA = """# ToDo アプリ プロジェクト案内

![表紙](images/cover.png)

シンプルな **ToDo 管理アプリ** を作るプロジェクトです。この文書はチームに入った人が最初に読む案内です。

## 目標

- ToDo を追加・完了・削除できる
- 締め切りと検索に対応する
- データはローカルの JSON ファイルに保存する

## スケジュール

| 段階 | 期間 | 担当 | 状態 |
| --- | --- | --- | --- |
| 設計 | 10/01 ~ 10/07 | Minji Kim | 完了 |
| 実装 | 10/08 ~ 10/31 | Joon Park | 進行中 |
| 試験 | 11/01 ~ 11/10 | Minji Kim | 予定 |

## やること

- [x] リポジトリを作る
- [x] 基本画面の設計
- [ ] 検索機能の仕上げ
- [ ] インストール案内を書く

## 実行方法

```python
from src.app import TodoApp

app = TodoApp("todos.json")
app.add("報告書を書く")
print(app.list())
```

> 詳しくは `docs/design.md` を見てください。"""

ZH = """# 待办应用项目说明

![封面](images/cover.png)

这是一个制作简单 **待办事项应用** 的项目。本文档是新成员首先阅读的说明。

## 目标

- 可以添加、完成、删除待办
- 支持截止日期和搜索
- 数据保存在本地 JSON 文件中

## 进度安排

| 阶段 | 时间 | 负责人 | 状态 |
| --- | --- | --- | --- |
| 设计 | 10/01 ~ 10/07 | Minji Kim | 已完成 |
| 实现 | 10/08 ~ 10/31 | Joon Park | 进行中 |
| 测试 | 11/01 ~ 11/10 | Minji Kim | 计划中 |

## 待办

- [x] 创建仓库
- [x] 设计基本界面
- [ ] 完成搜索功能
- [ ] 编写安装说明

## 运行方法

```python
from src.app import TodoApp

app = TodoApp("todos.json")
app.add("写报告")
print(app.list())
```

> 详细内容请见 `docs/design.md`。"""

DEMO2 = [("demo.guideBody", KO, EN, ES, JA, ZH)]
