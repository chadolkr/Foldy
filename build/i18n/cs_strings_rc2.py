# -*- coding: utf-8 -*-
"""1.0 최종 점검(2026-11-22, 2차)에서 찾은 남은 한국어: 금고·암호 안내, 창 제목, 상태 글, 화면 읽기 이름, AI 연결 도구 설명. (키, ko, en, es, ja, zh-Hans)"""

RC2 = [
    # 암호 걸기·금고·잠긴 메모 (SecureFlows)
    ("sec.policyNote", "회사 PC라면 파일 암호화가 보안 정책에 맞는지 먼저 확인해 주세요.", "On a work PC, first check that your security policy allows encrypting files.", "Si es un PC del trabajo, comprueba primero que tu política de seguridad permite cifrar archivos.", "会社の PC なら、ファイルの暗号化がセキュリティ ポリシーで認められているか先に確認してください。", "如果是公司电脑，请先确认加密文件是否符合安全策略。"),
    ("sec.forgetWarning", "암호를 잊으면 Foldy도 열 수 없어요. 꼭 기억할 수 있는 암호로 정해 주세요.", "If you forget the password, not even Foldy can open it. Choose one you'll remember.", "Si olvidas la contraseña, ni siquiera Foldy podrá abrirlo. Elige una que vayas a recordar.", "パスワードを忘れると Foldy でも開けません。必ず覚えていられるパスワードにしてください。", "忘记密码后连 Foldy 也打不开。请设一个一定能记住的密码。"),
    ("sec.extractFailedN", "{0:N0}개는 꺼내지 못했어요 (금고에는 그대로 있어요):\n{1}", "Couldn't extract {0:N0} (they're still in the vault):\n{1}", "No se pudieron extraer {0:N0} (siguen en la caja fuerte):\n{1}", "{0:N0} 個は取り出せませんでした (金庫にはそのまま残っています):\n{1}", "有 {0:N0} 个未能取出（仍在保险箱中）：\n{1}"),
    # 창 제목
    ("del.title", "많은 파일 지우기", "Delete many files", "Borrar muchos archivos", "多くのファイルを削除", "删除大量文件"),
    ("about.version", "버전 {0}", "Version {0}", "Versión {0}", "バージョン {0}", "版本 {0}"),
    ("ql.title", "빠른 보기", "Quick look", "Vista rápida", "クイック表示", "快速查看"),
    ("tour.windowTitle", "Foldy 짧은 소개", "Foldy quick tour", "Recorrido rápido por Foldy", "Foldy のかんたん紹介", "Foldy 快速介绍"),
    ("desc.editTitle", "설명 편집 - {0}", "Edit description - {0}", "Editar descripción - {0}", "説明の編集 - {0}", "编辑说明 - {0}"),
    # 상태 글
    ("img.needsCodec", "코덱 필요", "Codec needed", "Falta el códec", "コーデックが必要", "需要解码器"),
    ("preview.headOnly", "앞부분만 표시 ({0} 중)", "showing the beginning only (of {0})", "solo se muestra el principio (de {0})", "先頭のみ表示 ({0} 中)", "仅显示开头（共 {0}）"),
    ("set.pressKey", "키를 누르세요…", "Press a key…", "Pulsa una tecla…", "キーを押してください…", "请按键…"),
    ("tpl.savedAs", "내 템플릿으로 저장함: {0}", "Saved to My templates: {0}", "Guardado en Mis plantillas: {0}", "マイ テンプレートに保存しました: {0}", "已保存到我的模板：{0}"),
    ("tpl.capturedDesc", "{0} 폴더 구조에서 만든 템플릿", "Template made from the {0} folder structure", "Plantilla creada a partir de la estructura de la carpeta {0}", "{0} フォルダーの構成から作ったテンプレート", "根据 {0} 文件夹结构创建的模板"),
    ("tpl.examples", "예: ", "e.g. ", "p. ej.: ", "例: ", "例如："),
    ("git.untrusted", "Git: 신뢰하지 않는 저장소", "Git: untrusted repository", "Git: repositorio no confiable", "Git: 信頼されていないリポジトリ", "Git：不受信任的仓库"),
    ("an.progressPct", "분석하는 중… {0}%", "Analyzing… {0}%", "Analizando… {0} %", "分析中… {0}%", "正在分析… {0}%"),
    ("copy.failedN", "파일 {0:N0}개를 처리하지 못했어요", "Couldn't process {0:N0} files", "No se pudieron procesar {0:N0} archivos", "{0:N0} 個のファイルを処理できませんでした", "有 {0:N0} 个文件未能处理"),
    ("copy.engineError", "복사 엔진 오류 (코드 {0})", "Copy engine error (code {0})", "Error del motor de copia (código {0})", "コピー エンジンのエラー (コード {0})", "复制引擎错误（代码 {0}）"),
    ("sync.warn", "동기화 폴더예요 ({0}). 옮기면 다른 PC에도 똑같이 반영되고, 파일을 내려받을 수 있어요.", "This is a sync folder ({0}). Moving files also changes them on your other PCs, and files may be downloaded.", "Es una carpeta sincronizada ({0}). Si mueves archivos, el cambio también se aplica en tus otros PC y puede que se descarguen archivos.", "同期フォルダーです ({0})。移動するとほかの PC にも同じように反映され、ファイルがダウンロードされることがあります。", "这是同步文件夹（{0}）。移动后其他电脑上也会同样变化，还可能下载文件。"),
    ("os.project", "Git 저장소·개발·AI 프로젝트 폴더(.git, .sln, package.json, .claude, CLAUDE.md 등이 있음)는 정리하지 않습니다. 파일을 옮기면 빌드나 Git 기록이 깨질 수 있어요. 프로젝트 밖의 일반 폴더에서 정리해 주세요.", "Git repositories and development or AI project folders (with .git, .sln, package.json, .claude, CLAUDE.md, etc.) aren't tidied. Moving files could break builds or Git history. Please tidy a regular folder outside the project.", "No se ordenan los repositorios Git ni las carpetas de proyectos de desarrollo o IA (con .git, .sln, package.json, .claude, CLAUDE.md, etc.). Mover archivos podría romper compilaciones o el historial de Git. Ordena una carpeta normal fuera del proyecto.", "Git リポジトリや開発・AI プロジェクトのフォルダー (.git、.sln、package.json、.claude、CLAUDE.md などがある) は整理しません。ファイルを動かすとビルドや Git の履歴が壊れることがあります。プロジェクトの外の普通のフォルダーで整理してください。", "不整理 Git 仓库和开发、AI 项目文件夹（含 .git、.sln、package.json、.claude、CLAUDE.md 等）。移动文件可能会破坏构建或 Git 历史。请在项目以外的普通文件夹中整理。"),
    # 화면 읽기 프로그램이 읽는 이름
    ("list.a11ySelected", ", 선택됨", ", selected", ", seleccionado", "、選択済み", "，已选中"),
    ("git.branchA11y", "브랜치 {0}", "Branch {0}", "Rama {0}", "ブランチ {0}", "分支 {0}"),
    ("dog.name", "냄새 잘 맡는 개", "Sniffing dog", "Perro rastreador", "鼻のきく犬", "嗅觉灵敏的小狗"),
    # AI 연결 화면의 도구 설명 (AI에게 보내는 설명은 그대로 두고, 화면에 보이는 것만 앱 언어로)
    ("mcp.tool.find_files", "파일 찾기", "Find files", "Buscar archivos", "ファイルを探す", "查找文件"),
    ("mcp.tool.find_files.desc", "이름에 낱말이 들어간 파일과 폴더를 찾아요. 이 PC의 드라이브는 색인으로 바로, 색인이 없는 드라이브(회사 드라이브·Google Drive)는 Foldy가 모아 둔 목록에서 찾아요.", "Finds files and folders whose names contain the words. Drives on this PC are searched instantly through the index; drives without an index (work drives, Google Drive) are searched in the list Foldy has collected.", "Busca archivos y carpetas cuyo nombre contiene las palabras. Las unidades de este PC se buscan al instante con el índice; las que no tienen índice (unidades del trabajo, Google Drive), en la lista que Foldy ha reunido.", "名前に語句が入ったファイルとフォルダーを探します。この PC のドライブはインデックスですぐに、インデックスのないドライブ (会社のドライブ・Google Drive) は Foldy が集めておいた一覧から探します。", "查找名称中包含这些词的文件和文件夹。本机驱动器通过索引即时查找；没有索引的驱动器（公司驱动器、Google Drive）在 Foldy 收集的列表中查找。"),
    ("mcp.tool.list_folder", "폴더 안 보기", "List a folder", "Ver una carpeta", "フォルダーの中を見る", "查看文件夹内容"),
    ("mcp.tool.list_folder.desc", "폴더 안의 파일과 하위 폴더를 보여 줘요 (이름·크기·수정한 때).", "Shows the files and subfolders in a folder (name, size, date modified).", "Muestra los archivos y subcarpetas de una carpeta (nombre, tamaño, fecha de modificación).", "フォルダー内のファイルとサブフォルダーを表示します (名前・サイズ・更新日時)。", "显示文件夹中的文件和子文件夹（名称、大小、修改时间）。"),
    ("mcp.tool.folder_sizes", "폴더 크기", "Folder sizes", "Tamaño de carpetas", "フォルダーのサイズ", "文件夹大小"),
    ("mcp.tool.folder_sizes.desc", "폴더 안의 하위 폴더와 파일을 크기가 큰 순서로 보여 줘요. 디스크가 왜 꽉 찼는지 볼 때 좋아요.", "Shows the subfolders and files in a folder, largest first. Handy for seeing why a disk is full.", "Muestra las subcarpetas y archivos de una carpeta, de mayor a menor. Útil para ver por qué el disco está lleno.", "フォルダー内のサブフォルダーとファイルを大きい順に表示します。ディスクがいっぱいな理由を見るのに便利です。", "按从大到小显示文件夹中的子文件夹和文件。适合查看磁盘为什么满了。"),
    ("mcp.tool.read_folder_note", "폴더 설명 읽기", "Read folder note", "Leer la nota de la carpeta", "フォルダーの説明を読む", "读取文件夹说明"),
    ("mcp.tool.read_folder_note.desc", "Foldy에서 폴더에 적어 둔 설명(무엇인지, 규칙, 메모)을 읽어요.", "Reads the note written for a folder in Foldy (what it is, rules, memos).", "Lee la nota escrita para una carpeta en Foldy (qué es, reglas, apuntes).", "Foldy でフォルダーに書いておいた説明 (何のフォルダーか・ルール・メモ) を読みます。", "读取在 Foldy 中为文件夹写的说明（用途、规则、备忘）。"),
    ("mcp.tool.get_file_tags", "파일 상태 태그", "File status tags", "Etiquetas de estado", "ファイルの状態タグ", "文件状态标签"),
    ("mcp.tool.get_file_tags.desc", "Foldy에서 파일에 붙인 상태 태그(진행 중, 검토 중, 완료 등)를 알려 줘요. 여러 경로를 한 번에 물어볼 수 있어요.", "Tells the status tags you gave files in Foldy (in progress, in review, done, etc.). You can ask about several paths at once.", "Indica las etiquetas de estado que pusiste a los archivos en Foldy (en curso, en revisión, terminado, etc.). Se pueden consultar varias rutas a la vez.", "Foldy でファイルに付けた状態タグ (進行中・レビュー中・完了など) を伝えます。複数のパスをまとめて聞けます。", "告知在 Foldy 中给文件加的状态标签（进行中、审阅中、完成等）。可以一次询问多个路径。"),
    ("mcp.tool.drive_status", "드라이브와 색인 상태", "Drives and index status", "Unidades y estado del índice", "ドライブとインデックスの状態", "驱动器和索引状态"),
    ("mcp.tool.drive_status.desc", "이 PC의 드라이브 목록과 남은 공간, 빠른 찾기 색인 상태(완료 · 색인 중 · 없음)를 알려 줘요.", "Lists the drives on this PC with free space and quick-search index status (ready, indexing, none).", "Muestra las unidades de este PC con el espacio libre y el estado del índice de búsqueda rápida (listo, indexando, sin índice).", "この PC のドライブ一覧と空き容量、クイック検索のインデックス状態 (完了・作成中・なし) を伝えます。", "列出本机驱动器、剩余空间和快速搜索索引状态（已完成、索引中、无）。"),
    ("mcp.param.query", "찾을 낱말. 파일 이름의 일부면 돼요 (예: \"제안서\", \".pptx\").", "Words to find. Part of a file name is enough (e.g. \"proposal\", \".pptx\").", "Palabras que buscar. Basta con parte del nombre (p. ej. \"propuesta\", \".pptx\").", "探す語句。ファイル名の一部で大丈夫です (例: 「提案書」「.pptx」)。", "要查找的词。文件名的一部分即可（例如“方案”“.pptx”）。"),
    ("mcp.param.folder", "이 폴더 아래에서만 찾기. 비우면 PC 전체.", "Search only under this folder. Leave empty for the whole PC.", "Buscar solo dentro de esta carpeta. Vacío para todo el PC.", "このフォルダーの下だけを探します。空なら PC 全体。", "只在此文件夹下查找。留空则查找整台电脑。"),
    ("mcp.param.limit", "최대 개수.", "Maximum number of results.", "Número máximo de resultados.", "最大件数。", "最多返回的数量。"),
    ("mcp.param.path", "폴더 경로.", "Folder path.", "Ruta de la carpeta.", "フォルダーのパス。", "文件夹路径。"),
    ("mcp.param.paths", "파일 또는 폴더 경로 목록.", "List of file or folder paths.", "Lista de rutas de archivos o carpetas.", "ファイルまたはフォルダーのパスの一覧。", "文件或文件夹路径列表。"),
]
