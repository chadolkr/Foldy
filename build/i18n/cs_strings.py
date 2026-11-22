# -*- coding: utf-8 -*-
"""코드(C#) 안 글. (키, ko, en, es, ja, zh-Hans). ko는 코드의 문자열과 똑같아야 cs_tool.py apply가 바꾼다."""

CS = [
    # 명령 이름 (모든 명령·Ctrl+Space·단축키 설정)
    ("cmd.copyOther", "반대 패널로 복사", "Copy to other pane", "Copiar al otro panel", "反対側へコピー", "复制到另一窗格"),
    ("cmd.moveOther", "반대 패널로 이동", "Move to other pane", "Mover al otro panel", "反対側へ移動", "移动到另一窗格"),
    ("cmd.newFolder", "새 폴더", "New folder", "Nueva carpeta", "新しいフォルダー", "新建文件夹"),
    ("cmd.rename", "이름 바꾸기", "Rename", "Cambiar nombre", "名前の変更", "重命名"),
    ("cmd.recycle", "휴지통으로 삭제", "Delete to Recycle Bin", "Eliminar a la papelera", "ごみ箱へ削除", "删除到回收站"),
    ("cmd.deletePermanent", "영구 삭제", "Delete permanently", "Eliminar definitivamente", "完全に削除", "永久删除"),
    ("cmd.fromTemplate", "템플릿으로 새 폴더", "New folder from template", "Nueva carpeta desde plantilla", "テンプレートから新規フォルダー", "用模板新建文件夹"),
    ("cmd.wizard", "폴더 위저드", "Folder wizard", "Asistente de carpetas", "フォルダーウィザード", "文件夹向导"),
    ("cmd.organize", "이 폴더 정리하기", "Tidy this folder", "Ordenar esta carpeta", "このフォルダーを整理", "整理此文件夹"),
    ("cmd.editDescription", "폴더 설명 편집", "Edit folder description", "Editar la descripción de la carpeta", "フォルダーの説明を編集", "编辑文件夹说明"),
    ("cmd.switchPane", "다른 패널로 이동", "Switch to other pane", "Cambiar al otro panel", "もう一方の画面へ", "切换到另一窗格"),
    ("cmd.newTab", "새 탭", "New tab", "Nueva pestaña", "新しいタブ", "新建标签页"),
    ("cmd.closeTab", "탭 닫기", "Close tab", "Cerrar pestaña", "タブを閉じる", "关闭标签页"),
    ("cmd.nextTab", "다음 탭", "Next tab", "Pestaña siguiente", "次のタブ", "下一个标签页"),
    ("cmd.prevTab", "이전 탭", "Previous tab", "Pestaña anterior", "前のタブ", "上一个标签页"),
    ("cmd.search", "파일 이름 검색", "Search file names", "Buscar nombres de archivo", "ファイル名で検索", "搜索文件名"),
    ("cmd.path", "경로 입력", "Type a path", "Escribir una ruta", "パスを入力", "输入路径"),
    ("cmd.refresh", "새로 고침", "Refresh", "Actualizar", "最新の情報に更新", "刷新"),
    ("cmd.back", "뒤로", "Back", "Atrás", "戻る", "后退"),
    ("cmd.forward", "앞으로", "Forward", "Adelante", "進む", "前进"),
    ("cmd.up", "위로", "Up", "Subir", "上へ", "向上"),
    ("cmd.terminal", "터미널 열기 (기본 셸)", "Open terminal (default shell)", "Abrir terminal (shell predeterminado)", "ターミナルを開く (既定のシェル)", "打开终端（默认 Shell）"),
    ("cmd.terminalAdmin", "관리자 터미널 열기", "Open admin terminal", "Abrir terminal de administrador", "管理者ターミナルを開く", "打开管理员终端"),
    ("cmd.cmd", "명령 프롬프트 열기", "Open Command Prompt", "Abrir Símbolo del sistema", "コマンド プロンプトを開く", "打开命令提示符"),
    ("cmd.cmdAdmin", "관리자 명령 프롬프트 열기", "Open admin Command Prompt", "Abrir Símbolo del sistema como administrador", "管理者コマンド プロンプトを開く", "打开管理员命令提示符"),
    ("cmd.hidden", "숨김 파일 보기 켜기/끄기", "Show/hide hidden files", "Mostrar/ocultar archivos ocultos", "隠しファイルの表示/非表示", "显示/隐藏隐藏文件"),
    ("cmd.analysis", "폴더 분석", "Folder analysis", "Análisis de carpetas", "フォルダー分析", "文件夹分析"),
    ("cmd.ai", "AI 연결 (MCP)", "AI connection (MCP)", "Conexión con IA (MCP)", "AI 接続 (MCP)", "AI 连接（MCP）"),
    ("cmd.preview", "미리보기 창 보이기/숨기기", "Show/hide preview pane", "Mostrar/ocultar vista previa", "プレビュー画面の表示/非表示", "显示/隐藏预览窗格"),
    ("cmd.tree", "폴더 트리 보이기/숨기기", "Show/hide folder tree", "Mostrar/ocultar el árbol de carpetas", "フォルダーツリーの表示/非表示", "显示/隐藏文件夹树"),
    ("cmd.sidebar", "사이드바 접기/펼치기", "Collapse/expand sidebar", "Contraer/expandir barra lateral", "サイドバーの折りたたみ/展開", "折叠/展开侧边栏"),
    ("cmd.pin", "지금 폴더를 자주 쓰는 폴더에 고정/해제", "Pin/unpin the current folder", "Fijar/desfijar la carpeta actual", "今のフォルダーの固定/解除", "固定/取消固定当前文件夹"),
    ("cmd.images", "이미지만 보기 켜기/끄기", "Images only on/off", "Solo imágenes: activar/desactivar", "画像だけ表示のオン/オフ", "开启/关闭仅显示图片"),
    ("cmd.git", "Git 뷰 열기", "Open Git view", "Abrir la vista de Git", "Git 画面を開く", "打开 Git 视图"),
    ("cmd.blame", "Git: 이 파일 Blame", "Git: blame this file", "Git: blame de este archivo", "Git: このファイルの Blame", "Git：Blame 此文件"),
    ("cmd.sftp", "SFTP 서버에 연결", "Connect to SFTP server", "Conectar a un servidor SFTP", "SFTP サーバーに接続", "连接 SFTP 服务器"),
    ("cmd.palette", "모든 명령 보기", "Show all commands", "Ver todos los comandos", "すべてのコマンドを表示", "显示所有命令"),
    ("cmd.quickSearch", "빠른 검색 (기능·파일, 다른 프로그램에서도)", "Quick search (features and files, from any program)", "Búsqueda rápida (funciones y archivos, desde cualquier programa)", "クイック検索 (機能・ファイル、ほかのアプリからも)", "快速搜索（功能和文件，在任何程序中）"),
    ("cmd.settings", "설정", "Settings", "Configuración", "設定", "设置"),
    ("cmd.help", "도움말", "Help", "Ayuda", "ヘルプ", "帮助"),
    ("cmd.update", "업데이트 확인", "Check for updates", "Buscar actualizaciones", "更新を確認", "检查更新"),

    # 설정 선택지
    ("set.none", "없음", "None", "Ninguno", "なし", "无"),
    ("set.keys.same", "같은 단축키: ", "Same shortcut: ", "Mismo atajo: ", "同じショートカット: ", "相同快捷键："),
    ("set.theme.system", "시스템 설정 따르기", "Use system setting", "Usar la configuración del sistema", "システム設定に従う", "跟随系统设置"),
    ("set.theme.light", "라이트", "Light", "Claro", "ライト", "浅色"),
    ("set.theme.dark", "다크", "Dark", "Oscuro", "ダーク", "深色"),
    ("set.theme.retro", "레트로", "Retro", "Retro", "レトロ", "复古"),
    ("set.accent.foldy", "Foldy 오렌지", "Foldy orange", "Naranja Foldy", "Foldy オレンジ", "Foldy 橙"),
    ("set.accent.windows", "Windows 강조색", "Windows accent color", "Color de acento de Windows", "Windows のアクセントカラー", "Windows 强调色"),
    ("set.density.compact", "조밀하게 (행 24)", "Compact (24 px rows)", "Compacto (filas de 24 px)", "詰める (行 24)", "紧凑（行高 24）"),
    ("set.density.normal", "보통 (행 28)", "Normal (28 px rows)", "Normal (filas de 28 px)", "標準 (行 28)", "标准（行高 28）"),
    ("set.density.relaxed", "여유롭게 (행 32)", "Relaxed (32 px rows)", "Amplio (filas de 32 px)", "ゆったり (行 32)", "宽松（行高 32）"),
    ("set.md.foldy", "Foldy 뷰어로 보기 (기본)", "View in Foldy (default)", "Ver en Foldy (predeterminado)", "Foldy のビューアーで表示 (既定)", "用 Foldy 查看（默认）"),
    ("set.md.app", "등록된 앱으로 열기 (없으면 Foldy)", "Open with the registered app (or Foldy if none)", "Abrir con la app registrada (o Foldy si no hay)", "登録されたアプリで開く (なければ Foldy)", "用已关联的应用打开（没有则用 Foldy）"),
    ("set.term.auto", "자동 (Windows Terminal이 있으면 사용)", "Automatic (Windows Terminal if installed)", "Automático (Windows Terminal si está instalado)", "自動 (Windows Terminal があれば使う)", "自动（如有 Windows Terminal 则使用）"),
    ("set.term.console", "콘솔 창", "Console window", "Ventana de consola", "コンソール ウィンドウ", "控制台窗口"),
    ("set.shell.cmd", "명령 프롬프트 (cmd)", "Command Prompt (cmd)", "Símbolo del sistema (cmd)", "コマンド プロンプト (cmd)", "命令提示符（cmd）"),
    ("set.update.auto", "자동으로 설치", "Install automatically", "Instalar automáticamente", "自動でインストール", "自动安装"),
    ("set.update.notify", "내려받고 알림만", "Download and notify only", "Solo descargar y avisar", "ダウンロードして通知だけ", "仅下载并通知"),
    ("set.update.off", "사용 안 함", "Off", "Desactivado", "使わない", "关闭"),
    ("set.channel.stable", "안정", "Stable", "Estable", "安定版", "稳定版"),
    ("set.channel.beta", "베타", "Beta", "Beta", "ベータ", "测试版"),

    # 확인 창
    ("dlg.ok", "확인", "OK", "Aceptar", "OK", "确定"),
    ("dlg.cancel", "그만두기", "Cancel", "Cancelar", "やめる", "取消"),

    # 파일 작업 (상태 줄·작업 줄)
    ("ops.adminCancelled", "관리자 실행이 취소됐습니다", "Running as administrator was cancelled", "Se canceló la ejecución como administrador", "管理者としての実行がキャンセルされました", "已取消以管理员身份运行"),
    ("ops.selectFirst", "먼저 파일을 선택하세요", "Select files first", "Selecciona archivos primero", "先にファイルを選んでください", "请先选择文件"),
    ("ops.noOtherFolder", "반대 패널에 폴더 탭이 없습니다", "The other pane has no folder tab", "El otro panel no tiene una pestaña de carpeta", "反対側の画面にフォルダーのタブがありません", "另一窗格没有文件夹标签页"),
    ("ops.move", "이동", "move", "mover", "移動", "移动"),
    ("ops.copy", "복사", "copy", "copiar", "コピー", "复制"),
    ("ops.sftpDeleteWarn", "잠깐만요! 서버에는 휴지통이 없어요. 🙂\n지우면 되살릴 수 없으니 한 번만 더 확인해 주세요.\n\n", "Just a moment! Servers have no Recycle Bin. 🙂\nDeleted files can't be restored, so please check once more.\n\n", "¡Un momento! Los servidores no tienen papelera. 🙂\nLo que borres no se podrá recuperar, así que revísalo una vez más.\n\n", "ちょっと待って! サーバーにはごみ箱がありません。🙂\n消すと戻せないので、もう一度だけ確認してください。\n\n", "请稍等！服务器上没有回收站。🙂\n删除后无法恢复，请再确认一次。\n\n"),
    ("ops.deleteCancelled", "삭제를 취소했습니다", "Delete cancelled", "Eliminación cancelada", "削除をやめました", "已取消删除"),
    ("ops.toRecycle", "휴지통으로", "to Recycle Bin", "a la papelera", "ごみ箱へ", "移到回收站"),
    ("ops.badName", "이름에 쓸 수 없는 문자가 있습니다: \\ / : * ? \" < > |", "The name contains characters that aren't allowed: \\ / : * ? \" < > |", "El nombre contiene caracteres no permitidos: \\ / : * ? \" < > |", "名前に使えない文字があります: \\ / : * ? \" < > |", "名称中包含不允许的字符：\\ / : * ? \" < > |"),
    ("ops.cancelled", "취소됨", "Cancelled", "Cancelado", "キャンセルしました", "已取消"),
    ("ops.cantOpen", "열 수 없습니다: {0}", "Can't open: {0}", "No se puede abrir: {0}", "開けません: {0}", "无法打开：{0}"),
    ("ops.cantOpenTerminal", "터미널을 열 수 없습니다: {0}", "Can't open the terminal: {0}", "No se puede abrir la terminal: {0}", "ターミナルを開けません: {0}", "无法打开终端：{0}"),
    ("ops.job.recycle", "{0} 휴지통으로", "{0} to Recycle Bin", "{0} a la papelera", "{0} をごみ箱へ", "{0} 移到回收站"),
    ("ops.job.deletePermanent", "{0} 영구 삭제", "Delete {0} permanently", "Eliminar {0} definitivamente", "{0} を完全に削除", "永久删除 {0}"),
    ("ops.job.fastDelete", "{0} 빨리 지우기", "Fast delete {0}", "Borrado rápido de {0}", "{0} を高速削除", "快速删除 {0}"),
    ("ops.more", "\n  … 외 {0:N0}개", "\n  … and {0:N0} more", "\n  … y {0:N0} más", "\n  … ほか {0:N0} 個", "\n  … 另外 {0:N0} 个"),
    ("ops.deleteList", "지울 항목 {0:N0}개:\n{1}{2}\n\n정말 지울까요?", "{0:N0} items to delete:\n{1}{2}\n\nDelete them?", "{0:N0} elementos para eliminar:\n{1}{2}\n\n¿Eliminarlos?", "削除する項目 {0:N0} 個:\n{1}{2}\n\n本当に削除しますか?", "要删除的项目 {0:N0} 个：\n{1}{2}\n\n确定删除吗？"),
    ("ops.deletedN", "{0:N0}개 지웠어요", "{0:N0} deleted", "{0:N0} eliminados", "{0:N0} 個削除しました", "已删除 {0:N0} 个"),
    ("ops.deleteFailed", "{0:N0}개는 지우지 못했어요 (쓰는 중이거나 권한이 없어요): {1}", "{0:N0} couldn't be deleted (in use or no permission): {1}", "{0:N0} no se pudieron eliminar (en uso o sin permiso): {1}", "{0:N0} 個は削除できませんでした (使用中か権限がありません): {1}", "{0:N0} 个无法删除（正在使用或没有权限）：{1}"),
    ("ops.deletedAll", "{0:N0}개를 지웠어요", "Deleted {0:N0}", "Se eliminaron {0:N0}", "{0:N0} 個を削除しました", "已删除 {0:N0} 个"),
    ("ops.deleteStopped", "중간에 멈췄어요. 이미 지운 {0:N0}개는 되살릴 수 없어요", "Stopped partway. The {0:N0} already deleted can't be restored", "Se detuvo a mitad. Los {0:N0} ya eliminados no se pueden recuperar", "途中で止めました。すでに削除した {0:N0} 個は戻せません", "已中途停止。已删除的 {0:N0} 个无法恢复"),
    ("ops.cantRename", "이름을 바꿀 수 없습니다: {0}", "Can't rename: {0}", "No se puede cambiar el nombre: {0}", "名前を変更できません: {0}", "无法重命名：{0}"),
    ("ops.cantCreateFolder", "폴더를 만들 수 없습니다: {0}", "Can't create the folder: {0}", "No se puede crear la carpeta: {0}", "フォルダーを作成できません: {0}", "无法创建文件夹：{0}"),
]


from cs_strings_sw import SW  # noqa: E402
CS = CS + SW
from cs_strings_cf import CF  # noqa: E402
CS = CS + CF
from cs_strings_b3 import B3  # noqa: E402
CS = CS + B3
from cs_strings_b4 import B4  # noqa: E402
CS = CS + B4
from cs_strings_folders import FOLDERS  # noqa: E402
CS = CS + FOLDERS
from cs_strings_pres import PRES  # noqa: E402
CS = CS + PRES
