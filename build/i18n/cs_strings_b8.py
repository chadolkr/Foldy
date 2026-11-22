# -*- coding: utf-8 -*-
"""SFTP 호스트 키 · 안내 창 암호 · 오류 보고 동의 · SFTP 연결 창 · 같은 이름 충돌 · 한 줄 힌트 · 업데이트 검증 · 다이어그램 · 로그 정리 · 다운로드 이유 · Git 설치 · 이미지 · 이전 버전 이름 · 정리 실행 · 템플릿 가져오기·변수·계획 · 메모 묶음. (키, ko, en, es, ja, zh-Hans)
옮기지 않는 것: McpRunner(AI에게 주는 답), DiagnosticMode(진단 기록), App.xaml.cs(로그·개발용), FileRow(성능 측정 창), FolderTemplate 묶음 이름(템플릿 값)."""

B8 = [
    # SftpIntegration
    ("sftp.trustYes", "확인했어요, 연결", "I've checked, connect", "Lo he comprobado, conectar", "確認しました、接続", "已确认，连接"),
    ("sftp.dontConnect", "연결하지 않기", "Don't connect", "No conectar", "接続しない", "不连接"),
    ("sftp.saveConnect", "저장하고 연결", "Save and connect", "Guardar y conectar", "保存して接続", "保存并连接"),
    ("sftp.onceConnect", "이번만 연결", "Connect just this once", "Conectar solo esta vez", "今回だけ接続", "仅本次连接"),
    ("sftp.hostKey", "호스트 키 확인", "Check host key", "Comprobar clave del host", "ホスト キーの確認", "检查主机密钥"),
    ("sftp.quotedOk", "\"확인\"", "\"OK\"", "\"Aceptar\"", "「OK」", "“确定”"),
    ("sftp.bracketTrust", "[확인했어요, 연결]", "[I've checked, connect]", "[Lo he comprobado, conectar]", "[確認しました、接続]", "[已确认，连接]"),
    ("sftp.noOnce", "(아니요: 이번만 연결)", "(No: connect just this once)", "(No: conectar solo esta vez)", "(いいえ: 今回だけ接続)", "（否：仅本次连接）"),
    ("sftp.keyChanged", "⚠ {0}:{1} 서버의 호스트 키가 저장된 것과 다릅니다.\n중간자 공격일 수 있습니다.\n\n{2}\n{3}\n\n서버 관리자에게 키가 바뀐 게 맞는지 확인한 뒤에만 \"확인\"을 누르세요.", "⚠ The host key of server {0}:{1} differs from the saved one.\nThis could be a man-in-the-middle attack.\n\n{2}\n{3}\n\nPress \"OK\" only after the server administrator confirms the key really changed.", "⚠ La clave del host del servidor {0}:{1} es distinta de la guardada.\nPodría ser un ataque de intermediario.\n\n{2}\n{3}\n\nPulsa \"Aceptar\" solo después de que el administrador confirme que la clave cambió.", "⚠ {0}:{1} サーバーのホスト キーが保存されたものと違います。\n中間者攻撃の可能性があります。\n\n{2}\n{3}\n\nサーバー管理者にキーが変わったことを確認してから「OK」を押してください。", "⚠ 服务器 {0}:{1} 的主机密钥与保存的不同。\n可能是中间人攻击。\n\n{2}\n{3}\n\n请先向服务器管理员确认密钥确实已更改，再按“确定”。"),
    ("sftp.firstConnect", "{0}:{1} 서버에 처음 연결합니다.\n\n{2}\n{3}\n\n이 키를 known_hosts에 저장하고 연결할까요?\n(아니요: 이번만 연결)", "Connecting to server {0}:{1} for the first time.\n\n{2}\n{3}\n\nSave this key to known_hosts and connect?\n(No: connect just this once)", "Primera conexión al servidor {0}:{1}.\n\n{2}\n{3}\n\n¿Guardar esta clave en known_hosts y conectar?\n(No: conectar solo esta vez)", "{0}:{1} サーバーに初めて接続します。\n\n{2}\n{3}\n\nこのキーを known_hosts に保存して接続しますか?\n(いいえ: 今回だけ接続)", "首次连接服务器 {0}:{1}。\n\n{2}\n{3}\n\n要把此密钥保存到 known_hosts 并连接吗？\n（否：仅本次连接）"),
    # GuideWindow
    ("guide.toggleAll", "모두 고르기 / 모두 풀기", "Select all / clear all", "Seleccionar todo / quitar todo", "すべて選択 / すべて解除", "全选 / 全部取消"),
    ("guide.password", "암호", "Password", "Contraseña", "パスワード", "密码"),
    ("guide.again", "같은 암호를 한 번 더", "Same password again", "La misma contraseña otra vez", "同じパスワードをもう一度", "再输一次同样的密码"),
    ("guide.enterPw", "암호를 입력해 주세요.", "Please enter a password.", "Escribe una contraseña.", "パスワードを入力してください。", "请输入密码。"),
    ("guide.mismatch", "두 암호가 달라요. 다시 입력해 주세요.", "The two passwords don't match. Please try again.", "Las dos contraseñas no coinciden. Vuelve a escribirlas.", "2 つのパスワードが違います。もう一度入力してください。", "两次输入的密码不一致，请重新输入。"),
    ("guide.pwMin", "암호는 {0}글자 이상으로 정해 주세요. 단어 몇 개를 띄어 쓴 문장이 기억하기 쉽고 안전해요.", "Use at least {0} characters. A few words with spaces in between are easy to remember and safe.", "Usa al menos {0} caracteres. Unas cuantas palabras separadas por espacios son fáciles de recordar y seguras.", "パスワードは {0} 文字以上にしてください。単語をいくつか空白でつないだ文が覚えやすく安全です。", "密码请至少 {0} 个字符。用空格隔开的几个词既好记又安全。"),
    ("guide.andMore", "\n… 외 {0:N0}개", "\n… and {0:N0} more", "\n… y {0:N0} más", "\n… ほか {0:N0} 個", "\n… 另外 {0:N0} 个"),
    # CrashConsentPrompt
    ("crash.lastTime", "지난번에 Foldy가 비정상 종료되었습니다.", "Foldy closed unexpectedly last time.", "Foldy se cerró inesperadamente la última vez.", "前回 Foldy が異常終了しました。", "Foldy 上次异常退出。"),
    ("crash.send", "오류 보고서 보내기", "Send error report", "Enviar informe de error", "エラー レポートを送る", "发送错误报告"),
    ("crash.help", "\n\n오류 보고서를 개발자에게 보내 주시면 고치는 데 큰 도움이 됩니다.\n", "\n\nSending an error report to the developer helps a lot with fixing it.\n", "\n\nEnviar un informe de error al desarrollador ayuda mucho a solucionarlo.\n", "\n\nエラー レポートを開発者に送っていただけると、直すのに大いに役立ちます。\n", "\n\n把错误报告发给开发者，对修复很有帮助。\n"),
    ("crash.contents", "보고서에는 앱·Windows 버전, 오류 내용, 최근 로그가 들어가며 파일 경로와 사용자 이름은 지웁니다. ", "The report includes the app and Windows versions, the error and recent logs; file paths and user names are removed. ", "El informe incluye las versiones de la app y de Windows, el error y registros recientes; se quitan rutas y nombres de usuario. ", "レポートにはアプリ・Windows のバージョン、エラー内容、最近のログが入り、ファイル パスとユーザー名は消します。", "报告包含应用和 Windows 版本、错误内容和最近的日志，文件路径和用户名会被删除。"),
    ("crash.grouped", "같은 오류는 하나의 GitHub 공개 이슈로 묶입니다. 설정 → 일반에서 언제든 바꿀 수 있습니다.", "The same error is grouped into one public GitHub issue. You can change this anytime in Settings → General.", "Los errores iguales se agrupan en una issue pública de GitHub. Puedes cambiarlo cuando quieras en Configuración → General.", "同じエラーは 1 つの GitHub 公開 Issue にまとめられます。設定 → 全般でいつでも変えられます。", "相同的错误会合并到一个公开的 GitHub Issue。可随时在 设置 → 常规 中更改。"),
    ("crash.always", "항상 자동으로 보내기", "Always send automatically", "Enviar siempre automáticamente", "常に自動で送る", "始终自动发送"),
    ("crash.once", "이번만 보내기", "Send just this once", "Enviar solo esta vez", "今回だけ送る", "仅本次发送"),
    ("crash.count", "Foldy에서 오류가 {0}건 기록되었습니다.", "Foldy recorded {0} errors.", "Foldy registró {0} errores.", "Foldy でエラーが {0} 件記録されました。", "Foldy 记录了 {0} 个错误。"),
    # SftpConnectWindow
    ("sftp.keyFile", "SSH 개인 키 파일", "SSH private key file", "Archivo de clave privada SSH", "SSH 秘密キー ファイル", "SSH 私钥文件"),
    ("sftp.allFiles", "모든 파일|*.*", "All files|*.*", "Todos los archivos|*.*", "すべてのファイル|*.*", "所有文件|*.*"),
    ("sftp.checkFields", "서버 주소, 포트, 사용자 이름을 확인하세요.", "Check the server address, port and user name.", "Revisa la dirección del servidor, el puerto y el usuario.", "サーバーのアドレス、ポート、ユーザー名を確認してください。", "请检查服务器地址、端口和用户名。"),
    ("sftp.savedPwUnreadable", "저장된 비밀번호를 읽을 수 없습니다. 다시 입력하세요.", "The saved password can't be read. Please enter it again.", "No se puede leer la contraseña guardada. Vuelve a escribirla.", "保存したパスワードを読めません。もう一度入力してください。", "无法读取已保存的密码，请重新输入。"),
    ("sftp.connecting", "서버에 연결하는 중…", "Connecting to the server…", "Conectando con el servidor…", "サーバーに接続しています…", "正在连接服务器…"),
    ("sftp.connectFailed", "연결하지 못했습니다: ", "Couldn't connect: ", "No se pudo conectar: ", "接続できませんでした: ", "无法连接："),
    ("sftp.connect", "연결", "Connect", "Conectar", "接続", "连接"),
    # ConflictWindow
    ("conf.applyAll", "남은 충돌에도 똑같이", "Do the same for remaining conflicts", "Hacer lo mismo con los conflictos restantes", "残りの競合にも同じように", "对其余冲突也这样处理"),
    ("conf.title", "Foldy · 같은 이름의 파일", "Foldy · file with the same name", "Foldy · archivo con el mismo nombre", "Foldy · 同じ名前のファイル", "Foldy · 同名文件"),
    ("conf.keepBoth", "둘 다 유지", "Keep both", "Conservar ambos", "両方残す", "保留两者"),
    ("conf.overwrite", "덮어쓰기", "Overwrite", "Sobrescribir", "上書き", "覆盖"),
    ("conf.skip", "건너뛰기", "Skip", "Omitir", "スキップ", "跳过"),
    ("conf.exists", "'{0}'이(가) 대상 폴더에 이미 있습니다.", "'{0}' already exists in the destination folder.", "'{0}' ya existe en la carpeta de destino.", "「{0}」は移動先フォルダーにすでにあります。", "目标文件夹中已存在“{0}”。"),
    ("conf.paths", "원본: {0}\n대상: {1}", "Source: {0}\nDestination: {1}", "Origen: {0}\nDestino: {1}", "元: {0}\n先: {1}", "源：{0}\n目标：{1}"),
    # ContextHints
    ("hint.logCleanup", "로그 정리", "Log cleanup", "Limpieza de registros", "ログの整理", "清理日志"),
    ("hint.downloads", "다운로드에 파일이 {0:N0}개 쌓였어요. 이미 설치한 설치 파일이나 중복 파일을 골라 드릴까요?", "{0:N0} files have piled up in Downloads. Shall I pick out installers you already used and duplicates?", "Se han acumulado {0:N0} archivos en Descargas. ¿Separo los instaladores ya usados y los duplicados?", "ダウンロードにファイルが {0:N0} 個たまっています。インストール済みのインストーラーや重複ファイルを選びましょうか?", "下载里积了 {0:N0} 个文件。要挑出已安装过的安装包和重复文件吗？"),
    ("hint.desktop", "바탕화면에 파일이 {0:N0}개 있어요. 버리지 않고 종류별로 보관해 드릴까요?", "There are {0:N0} files on your desktop. Shall I archive them by type, without throwing anything away?", "Hay {0:N0} archivos en tu escritorio. ¿Los archivo por tipo, sin tirar nada?", "デスクトップにファイルが {0:N0} 個あります。捨てずに種類別に保管しましょうか?", "桌面上有 {0:N0} 个文件。要不丢弃、按类型归档吗？"),
    ("hint.logs", "이 폴더에 로그 파일이 {0:N0}개 있어요. 오래된 로그를 달마다 zip으로 묶어 공간을 줄일까요?", "This folder has {0:N0} log files. Shall I pack old logs into a zip per month to save space?", "Esta carpeta tiene {0:N0} archivos de registro. ¿Empaqueto los antiguos en un zip por mes para ahorrar espacio?", "このフォルダーにログ ファイルが {0:N0} 個あります。古いログを月ごとに zip にまとめて容量を減らしましょうか?", "这个文件夹有 {0:N0} 个日志文件。要把旧日志按月打包成 zip 以节省空间吗？"),
    # AppUpdater
    ("au.noMsi", "update.json에 MSI가 없습니다", "update.json has no MSI", "update.json no incluye un MSI", "update.json に MSI がありません", "update.json 中没有 MSI"),
    ("au.mismatch", "내려받은 MSI가 update.json과 다릅니다 (크기 또는 SHA-256)", "The downloaded MSI doesn't match update.json (size or SHA-256)", "El MSI descargado no coincide con update.json (tamaño o SHA-256)", "ダウンロードした MSI が update.json と違います (サイズまたは SHA-256)", "下载的 MSI 与 update.json 不符（大小或 SHA-256）"),
    ("au.badSignature", "update.json 서명이 맞지 않습니다: ", "The update.json signature is invalid: ", "La firma de update.json no es válida: ", "update.json の署名が正しくありません: ", "update.json 签名无效："),
    # MermaidBlock
    ("mm.timeout", "시간 초과(2초)", "Timed out (2 s)", "Tiempo agotado (2 s)", "タイムアウト (2 秒)", "超时（2 秒）"),
    ("mm.viewCode", "코드 보기", "View code", "Ver código", "コードを見る", "查看代码"),
    ("mm.failed", "다이어그램을 그리지 못했습니다: ", "Couldn't draw the diagram: ", "No se pudo dibujar el diagrama: ", "図を描けませんでした: ", "无法绘制图表："),
    # LogCleanup
    ("lc.inUse", "다른 프로그램이 쓰는 중", "In use by another program", "En uso por otro programa", "ほかのプログラムが使用中", "被其他程序占用"),
    ("lc.noPermission", "권한 없음", "No permission", "Sin permiso", "権限なし", "没有权限"),
    ("lc.countMismatch", "새 zip의 항목 수가 맞지 않음", "The new zip's entry count doesn't match", "El número de elementos del nuevo zip no coincide", "新しい zip の項目数が合いません", "新 zip 的条目数不一致"),
    ("lc.verifyFailed", "압축 확인 실패 (원본은 그대로 둠)", "Couldn't verify the zip (originals left as is)", "No se pudo verificar el zip (los originales siguen igual)", "圧縮の確認に失敗 (元はそのまま)", "压缩验证失败（原件保持不变）"),
    # DownloadsCleanup
    ("dlc.installer", "이미 설치된 프로그램 ({0})의 설치 파일", "Installer for an already installed program ({0})", "Instalador de un programa ya instalado ({0})", "インストール済みのプログラム ({0}) のインストーラー", "已安装程序（{0}）的安装文件"),
    ("dlc.duplicate", "\"{0}\"와 내용이 같음", "Same content as \"{0}\"", "Mismo contenido que \"{0}\"", "「{0}」と内容が同じ", "与“{0}”内容相同"),
    ("dlc.unpacked", "이미 푼 압축 파일 (옆에 \"{0}\" 폴더가 있음)", "Already extracted (the \"{0}\" folder is next to it)", "Ya extraído (la carpeta \"{0}\" está al lado)", "展開済みの圧縮ファイル (隣に「{0}」フォルダーがある)", "已解压的压缩包（旁边有“{0}”文件夹）"),
    ("dlc.old", "{0:N0}일 동안 열지 않음", "Not opened for {0:N0} days", "Sin abrir desde hace {0:N0} días", "{0:N0} 日間開いていない", "{0:N0} 天未打开"),
    # GitView · ImagePresenter
    ("gv.continue", "계속하기", "Continue", "Continuar", "続ける", "继续"),
    ("gv.browserFailed", "브라우저를 열 수 없습니다. https://git-scm.com/download/win 을 직접 여세요.", "Can't open the browser. Open https://git-scm.com/download/win yourself.", "No se puede abrir el navegador. Abre tú https://git-scm.com/download/win.", "ブラウザーを開けません。https://git-scm.com/download/win を直接開いてください。", "无法打开浏览器。请自行打开 https://git-scm.com/download/win 。"),
    ("img.codec", "코덱 필요: Microsoft Store에서 확장을 설치하세요", "Codec needed: install the extension from the Microsoft Store", "Se necesita un códec: instala la extensión desde Microsoft Store", "コーデックが必要です: Microsoft Store から拡張機能をインストールしてください", "需要编解码器：请从 Microsoft Store 安装扩展"),
    ("img.cantPreview", "미리 볼 수 없습니다", "Can't preview", "No se puede previsualizar", "プレビューできません", "无法预览"),
    # VersionHistory (꺼낸 파일 이름)
    ("vh.name", "{0} (이전 버전 {1}){2}", "{0} (previous version {1}){2}", "{0} (versión anterior {1}){2}", "{0} (以前のバージョン {1}){2}", "{0}（以前的版本 {1}）{2}"),
    ("vh.nameN", "{0} (이전 버전 {1} {2}){3}", "{0} (previous version {1} {2}){3}", "{0} (versión anterior {1} {2}){3}", "{0} (以前のバージョン {1} {2}){3}", "{0}（以前的版本 {1} {2}）{3}"),
    # OrganizeExecutor · Template* · TextPack
    ("oe.noSource", "원본이 없습니다.", "The source doesn't exist.", "No existe el origen.", "元がありません。", "源不存在。"),
    ("oe.crossDrive", "다른 드라이브로는 폴더를 옮기지 않습니다 (복사 엔진 사용).", "Folders aren't moved to another drive (the copy engine is used).", "Las carpetas no se mueven a otra unidad (se usa el motor de copia).", "別のドライブへはフォルダーを移しません (コピー エンジンを使います)。", "不会把文件夹移到其他驱动器（使用复制引擎）。"),
    ("tl.noWizard", "wizard.json이 포함되지 않았습니다.", "wizard.json isn't included.", "wizard.json no está incluido.", "wizard.json が含まれていません。", "未包含 wizard.json。"),
    ("tl.cantImport", "가져올 수 없는 템플릿입니다: {0} ({1})", "This template can't be imported: {0} ({1})", "No se puede importar esta plantilla: {0} ({1})", "取り込めないテンプレートです: {0} ({1})", "无法导入此模板：{0}（{1}）"),
    ("tvar.badChar", "쓸 수 없는 문자: {0}", "Character not allowed: {0}", "Carácter no permitido: {0}", "使えない文字: {0}", "不能使用的字符：{0}"),
    ("tp.tooLong", "전체 경로가 너무 깁니다.", "The full path is too long.", "La ruta completa es demasiado larga.", "パス全体が長すぎます。", "完整路径太长。"),
    ("tp.folderExists", "같은 이름의 폴더가 있어 파일을 만들 수 없습니다.", "A folder with the same name exists, so the file can't be created.", "Hay una carpeta con el mismo nombre, así que no se puede crear el archivo.", "同じ名前のフォルダーがあるのでファイルを作れません。", "存在同名文件夹，无法创建文件。"),
    ("tp.fileExists", "같은 이름의 파일이 있어 폴더를 만들 수 없습니다.", "A file with the same name exists, so the folder can't be created.", "Hay un archivo con el mismo nombre, así que no se puede crear la carpeta.", "同じ名前のファイルがあるのでフォルダーを作れません。", "存在同名文件，无法创建文件夹。"),
    ("pack.tooMany", "항목이 너무 많은 파일이에요.", "This file has too many entries.", "Este archivo tiene demasiados elementos.", "項目が多すぎるファイルです。", "此文件的条目太多。"),
    ("pack.tooBig", "너무 큰 파일이에요.", "This file is too large.", "Este archivo es demasiado grande.", "大きすぎるファイルです。", "文件太大。"),
]
