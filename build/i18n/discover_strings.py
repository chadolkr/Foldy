# -*- coding: utf-8 -*-
"""
기능 찾기 (2026-11-22, 사용자 "좋은 기능이 있어도 처음 사용하는 사람은 기능이 어디 있는지 모른다").
한 줄 제안, 빈 폴더 안내, 새 메뉴 항목, 그리고 무엇이든 찾기(빠른 검색)가 말하듯 친 낱말로 기능을 찾게 하는 언어별 낱말(qi.<명령 id>).
strings.py가 함께 읽는다. 순서: (키, ko, en, es, ja, zh-Hans).
"""

DISCOVER = [
    # 메뉴 항목
    ("shell.protect.versions", "덮어쓴 파일 되살리기 (이전 버전, 고른 파일)", "Restore an overwritten file (earlier versions, selected file)", "Recuperar un archivo sobrescrito (versiones anteriores, archivo elegido)", "上書きしたファイルを戻す (以前のバージョン、選んだファイル)", "找回被覆盖的文件（以前的版本，选中的文件）"),
    ("shell.connect.ai", "AI에 연결 (Claude 등)…", "Connect to AI (Claude and others)…", "Conectar con IA (Claude y otras)…", "AI につなぐ (Claude など)…", "连接 AI（Claude 等）…"),
    ("sw.c.versions", "덮어쓴 파일 되살리기 (이전 버전, 고른 파일)", "Restore an overwritten file (earlier versions, selected file)", "Recuperar un archivo sobrescrito (versiones anteriores, archivo elegido)", "上書きしたファイルを戻す (以前のバージョン、選んだファイル)", "找回被覆盖的文件（以前的版本，选中的文件）"),
    ("sw.c.versionsNeed", "되살릴 파일 하나를 고른 뒤 다시 눌러 주세요. Foldy로 덮어쓴 파일은 예전 내용이 남아 있어요.", "Select the file to restore, then try again. Files overwritten by Foldy keep their earlier content.", "Elige el archivo que quieres recuperar y vuelve a intentarlo. Los archivos que Foldy sobrescribe guardan su contenido anterior.", "戻したいファイルを 1 つ選んでから、もう一度押してください。Foldy で上書きしたファイルは前の内容が残っています。", "请先选中要找回的文件，再点一次。被 Foldy 覆盖的文件会保留以前的内容。"),

    # 빈 폴더
    ("pane.empty.tip", "다른 창에서 파일을 끌어다 놓으면 복사돼요. 새 폴더는 F7이에요.", "Drag files here from the other pane to copy them. Press F7 for a new folder.", "Arrastra archivos aquí desde el otro panel para copiarlos. Pulsa F7 para crear una carpeta.", "もう一方の画面からファイルをドラッグするとコピーされます。新しいフォルダーは F7 です。", "从另一个窗格把文件拖到这里就会复制。按 F7 新建文件夹。"),

    # 한 줄 제안: 알맞은 순간에 한 번만 (그냥 두면 사라짐)
    ("hint.compare", "양쪽 창에 이름이 같은 폴더 '{0}'이(가) 열려 있어요. 두 폴더에서 다른 파일만 골라 볼까요?", "Both panes show a folder named '{0}'. Shall I show only the files that differ?", "Los dos paneles muestran una carpeta llamada '{0}'. ¿Te muestro solo los archivos que son distintos?", "両方の画面に同じ名前のフォルダー「{0}」が開いています。違うファイルだけ見てみましょうか?", "两边窗格打开了同名文件夹“{0}”。要只看两边不同的文件吗？"),
    ("hint.compare.btn", "두 창 비교", "Compare panes", "Comparar paneles", "2 つの画面を比べる", "比较两个窗格"),
    ("hint.rebuildable", "이 폴더에는 다시 만들 수 있는 폴더(node_modules·bin·obj 등)가 있어요. 지우면 공간이 얼마나 생기는지 볼까요?", "This folder has folders that can be rebuilt (node_modules, bin, obj…). Shall I show how much space removing them would free?", "Esta carpeta tiene carpetas que se pueden volver a generar (node_modules, bin, obj…). ¿Te muestro cuánto espacio liberarías?", "このフォルダーには作り直せるフォルダー (node_modules・bin・obj など) があります。消すとどれだけ空くか見てみましょうか?", "这个文件夹里有可以重新生成的文件夹（node_modules、bin、obj 等）。要看看删掉能腾出多少空间吗？"),
    ("hint.rebuildable.btn", "지워도 되는 폴더 찾기", "Find folders safe to delete", "Buscar carpetas que se pueden borrar", "消してもよいフォルダーを探す", "查找可以删除的文件夹"),
    ("hint.images", "사진이 {0:N0}장 있어요. 사진만 크게 모아 볼까요? (끄는 단추는 같은 자리에 있어요)", "There are {0:N0} photos here. Shall I show just the photos, large? (The button to turn it off stays in the same place.)", "Aquí hay {0:N0} fotos. ¿Te muestro solo las fotos, en grande? (El botón para quitarlo queda en el mismo sitio.)", "写真が {0:N0} 枚あります。写真だけ大きく並べてみましょうか? (戻すボタンは同じ場所にあります)", "这里有 {0:N0} 张照片。要只把照片放大显示吗？（关闭按钮就在同一位置）"),
    ("hint.images.btn", "이미지만 보기", "Show images only", "Ver solo imágenes", "画像だけ表示", "只看图片"),

    # 무엇이든 찾기: 기능마다 사람들이 부를 법한 말 (언어마다). 띄어 쓴 낱말 어느 것에 맞아도 된다.
    ("qi.memo.new", "메모 쓰기 메모 적기 메모장 기록 노트 글쓰기", "note write a note notepad jot memo", "nota escribir una nota bloc apunte memo", "メモ メモを書く メモ帳 記録 ノート", "笔记 写笔记 记事本 记录 备忘"),
    ("qi.secure.newMemo", "비밀 메모 비밀번호 적기 잠긴 메모 쓰기 암호 메모", "secret note password locked note encrypted note", "nota secreta contraseña nota bloqueada nota cifrada", "秘密のメモ パスワード ロック付きメモ 暗号メモ", "秘密笔记 密码 加密笔记 锁定笔记"),
    ("qi.secure.lock", "암호 걸기 잠그기 비밀번호 걸기 파일 잠그기", "password protect lock file encrypt", "proteger con contraseña bloquear archivo cifrar", "パスワードをかける ロック 暗号化 ファイルを守る", "加密 设置密码 锁定文件"),
    ("qi.secure.vault", "금고 폴더 잠그기 숨기기 비밀 폴더", "vault lock folder hide secret folder", "caja fuerte bloquear carpeta ocultar carpeta secreta", "金庫 フォルダーをロック 隠す 秘密のフォルダー", "保险箱 锁定文件夹 隐藏 秘密文件夹"),
    ("qi.cleanup.downloads", "다운로드 정리 다운로드 치우기 받은 파일 정리", "clean up downloads tidy downloads downloaded files", "limpiar descargas ordenar descargas archivos descargados", "ダウンロード整理 ダウンロードを片付ける", "整理下载 清理下载 下载的文件"),
    ("qi.cleanup.desktop", "바탕화면 정리 바탕화면 치우기 바탕 화면 청소", "clean up desktop tidy desktop messy desktop", "limpiar escritorio ordenar escritorio", "デスクトップ整理 デスクトップを片付ける", "整理桌面 清理桌面 桌面太乱"),
    ("qi.cleanup.logs", "로그 정리 로그 지우기 용량 확보", "clean up logs log files free space", "limpiar registros archivos de registro liberar espacio", "ログ整理 ログを消す 容量を空ける", "整理日志 清理日志 释放空间"),
    ("qi.analysis.open", "용량 보기 공간 확보 디스크 정리 무엇이 큰지 폴더 크기 공간 늘리기 디스크 꽉 참", "disk space free up space what is big folder size disk full storage", "espacio en disco liberar espacio qué ocupa tamaño de carpeta disco lleno", "容量 空き容量 ディスク整理 何が大きい フォルダーサイズ ディスクがいっぱい", "磁盘空间 释放空间 什么最大 文件夹大小 磁盘满了"),
    ("qi.app.emptyRecycleBin", "휴지통 비우기", "empty recycle bin trash", "vaciar papelera", "ごみ箱を空にする", "清空回收站"),
    ("qi.compare.panes", "폴더 비교 두 폴더 맞추기 동기화 같게 만들기 최신 폴더 다른 파일 차이 백업 확인", "compare folders sync folders make the same differences backup check", "comparar carpetas sincronizar igualar diferencias comprobar copia", "フォルダー比較 同期 同じにする 違い バックアップ確認", "比较文件夹 同步 让两边一样 差异 检查备份"),
    ("qi.folder.organize", "폴더 정리 이 폴더 치우기 청소", "organize folder tidy this folder", "organizar carpeta ordenar esta carpeta", "フォルダー整理 このフォルダーを片付ける", "整理文件夹 整理这个文件夹"),
    ("qi.folder.fromTemplate", "새 프로젝트 폴더 만들기 템플릿 폴더 틀 PARA", "new project folders template folder structure PARA", "carpetas de proyecto nuevo plantilla estructura PARA", "新しいプロジェクト フォルダーを作る テンプレート 構成 PARA", "新建项目文件夹 模板 文件夹结构 PARA"),
    ("qi.folder.manage", "관리 방식 PARA 정리 방법", "management method PARA how to organize", "método de organización PARA cómo organizar", "管理方法 PARA 整理のしかた", "管理方式 PARA 整理方法"),
    ("qi.file.newFolder", "새 폴더 만들기 폴더 만들기", "new folder make a folder create folder", "carpeta nueva crear carpeta", "新しいフォルダー フォルダーを作る", "新建文件夹 创建文件夹"),
    ("qi.view.zoomIn", "글씨 크게 글자 크게 화면 크게 잘 안 보여", "bigger text larger font zoom in hard to read", "texto más grande letra grande ampliar no se ve bien", "文字を大きく 画面を大きく 見えにくい", "字大一点 放大 看不清"),
    ("qi.view.zoomOut", "글씨 작게 글자 작게", "smaller text zoom out", "texto más pequeño reducir", "文字を小さく 縮小", "字小一点 缩小"),
    ("qi.view.themeDark", "어둡게 다크 모드 눈부셔", "dark mode dark theme too bright", "modo oscuro tema oscuro demasiado brillante", "ダークモード 暗く まぶしい", "深色模式 暗色 太亮"),
    ("qi.view.themeLight", "밝게 라이트 모드", "light mode light theme", "modo claro tema claro", "ライトモード 明るく", "浅色模式 亮色"),
    ("qi.view.hidden", "숨김 파일 보기 숨긴 파일", "show hidden files hidden", "mostrar archivos ocultos ocultos", "隠しファイルを表示 隠しファイル", "显示隐藏文件 隐藏文件"),
    ("qi.view.images", "사진만 보기 이미지만 사진 보기 사진 크게 썸네일 갤러리", "photos only images only view photos thumbnails gallery", "solo fotos solo imágenes ver fotos miniaturas galería", "写真だけ 画像だけ 写真を見る サムネイル ギャラリー", "只看照片 只看图片 看照片 缩略图 相册"),
    ("qi.view.preview", "미리보기 열지 않고 보기 내용 보기", "preview look inside without opening", "vista previa ver sin abrir", "プレビュー 開かずに見る 中身を見る", "预览 不打开就看 看内容"),
    ("qi.sftp.connect", "서버 연결 sftp ssh 원격", "connect to server sftp ssh remote", "conectar a servidor sftp ssh remoto", "サーバー接続 sftp ssh リモート", "连接服务器 sftp ssh 远程"),
    ("qi.ai.connect", "AI 연결 클로드 Claude 챗GPT Codex 인공지능 MCP", "connect AI Claude ChatGPT Codex artificial intelligence MCP", "conectar IA Claude ChatGPT Codex inteligencia artificial MCP", "AI 接続 Claude ChatGPT Codex 人工知能 MCP", "连接 AI Claude ChatGPT Codex 人工智能 MCP"),
    ("qi.file.versions", "되살리기 복구 이전 버전 예전 내용 덮어썼어 실수로 덮어씀 원래대로", "restore recover earlier version previous version overwritten by mistake undo overwrite", "recuperar restaurar versión anterior sobrescrito por error deshacer", "元に戻す 復元 以前のバージョン 前の内容 上書きしてしまった", "恢复 找回 以前的版本 旧内容 不小心覆盖了"),
    ("qi.cleanup.rebuildable", "지워도 되는 폴더 node_modules bin obj 개발 찌꺼기 빌드 폴더", "folders safe to delete node_modules bin obj build leftovers", "carpetas que se pueden borrar node_modules bin obj restos de compilación", "消してもよいフォルダー node_modules bin obj ビルドの残り", "可以删除的文件夹 node_modules bin obj 构建残留"),
    ("qi.cleanup.devcache", "개발 캐시 npm NuGet pip Docker 캐시 정리", "developer cache npm NuGet pip Docker clear cache", "caché de desarrollo npm NuGet pip Docker limpiar caché", "開発キャッシュ npm NuGet pip Docker キャッシュ整理", "开发缓存 npm NuGet pip Docker 清理缓存"),
    ("qi.cleanup.wizard", "정리 도우미 정리하기 깔끔하게 치우기", "tidy helper clean up neat", "asistente de orden ordenar limpiar", "整理アシスタント 片付け きれいに", "整理助手 收拾 整理干净"),
    ("qi.sidebar.pin", "고정 즐겨찾기 자주 쓰는 폴더 핀", "pin favorite pinned folders bookmark", "fijar favoritos carpetas fijadas marcador", "固定 お気に入り よく使うフォルダー ピン", "固定 收藏 常用文件夹 图钉"),
    ("qi.tab.new", "새 탭 탭 열기", "new tab open tab", "pestaña nueva abrir pestaña", "新しいタブ タブを開く", "新标签页 打开标签"),
    ("qi.git.open", "깃 커밋 기록 받기 올리기 버전 관리", "commit history pull push version control", "commit historial pull push control de versiones", "コミット 履歴 プル プッシュ バージョン管理", "提交 历史 拉取 推送 版本控制"),
    ("qi.folder.description", "폴더 설명 메모 붙이기 이 폴더가 뭔지", "folder description what this folder is for", "descripción de carpeta para qué es esta carpeta", "フォルダーの説明 このフォルダーは何か", "文件夹说明 这个文件夹是做什么的"),
    ("qi.app.settings", "설정 옵션 환경 설정", "settings options preferences", "configuración opciones preferencias", "設定 オプション 環境設定", "设置 选项 偏好"),
    ("qi.app.checkUpdate", "업데이트 새 버전", "update new version", "actualizar nueva versión", "アップデート 新しいバージョン", "更新 新版本"),
    ("qi.app.help", "도움말 사용법 설명서 모르겠어", "help how to use manual guide", "ayuda cómo se usa manual guía", "ヘルプ 使い方 説明書 わからない", "帮助 怎么用 说明书 不会用"),
    ("qi.app.tour", "둘러보기 처음 안내 사용법 알려 줘 소개 영상", "tour intro getting started show me how", "recorrido introducción primeros pasos enséñame", "紹介 はじめての案内 使い方を教えて", "导览 新手介绍 教我怎么用"),
    ("qi.app.reportBug", "버그 신고 오류 알리기 고장", "report bug error broken", "informar de un error fallo no funciona", "バグ報告 エラー 不具合", "报告问题 错误 坏了"),
    ("qi.tag.done", "작업 완료 표시 다 했어 체크", "mark done finished check off", "marcar como hecho terminado", "作業完了 終わった チェック", "标记完成 做完了 打勾"),
    ("qi.terminal.open", "터미널 명령창 cmd 파워셸", "terminal command prompt cmd powershell", "terminal símbolo del sistema cmd powershell", "ターミナル コマンドプロンプト cmd PowerShell", "终端 命令提示符 cmd PowerShell"),
]
