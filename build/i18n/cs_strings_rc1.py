# -*- coding: utf-8 -*-
"""1.0 점검(2026-11-22)에서 찾은 남은 한국어: 미리보기 상태 글, 파일 잠금 안내. (키, ko, en, es, ja, zh-Hans)"""

RC1 = [
    # 미리보기 창 상태 줄 (PreviewMessages)
    ("preview.notPreviewable", "미리 볼 수 없음", "Can't preview", "No se puede previsualizar", "プレビューできません", "无法预览"),
    ("preview.cloudOnly", "클라우드 파일 — 내려받지 않음", "Cloud file — not downloaded", "Archivo en la nube — no se descarga", "クラウドのファイル — ダウンロードしません", "云端文件 — 不会下载"),
    ("preview.networkDrive", "네트워크·클라우드 드라이브 — [미리보기]", "Network or cloud drive — [Preview]", "Unidad de red o en la nube — [Vista previa]", "ネットワーク・クラウドのドライブ — [プレビュー]", "网络或云驱动器 — [预览]"),
    ("preview.loading", "불러오는 중…", "Loading…", "Cargando…", "読み込み中…", "正在加载…"),
    ("preview.failed", '미리 볼 수 없음', "Can't preview (handler error)", "No se puede previsualizar (error del controlador)", "プレビューできません (ハンドラーのエラー)", "无法预览（处理程序出错）"),
    ("preview.folder", "폴더", "Folder", "Carpeta", "フォルダー", "文件夹"),
    # 파일을 다른 프로그램이 쓰고 있을 때 (FileLockers)
    ("lock.inUse", "{0}이(가) 쓰고 있어요. 그 프로그램을 닫고 다시 해 주세요.", "{0} is using it. Close that program and try again.", "{0} lo está usando. Cierra ese programa e inténtalo de nuevo.", "{0} が使用中です。そのプログラムを閉じてもう一度試してください。", "{0} 正在使用它。请关闭该程序后再试。"),
    ("lock.more", "{0} 외 {1}개", "{0} and {1} more", "{0} y {1} más", "{0} ほか {1} 個", "{0} 等另外 {1} 个"),
    ("lock.program", "프로그램 {0}", "Program {0}", "Programa {0}", "プログラム {0}", "程序 {0}"),
]
