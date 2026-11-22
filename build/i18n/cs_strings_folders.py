# -*- coding: utf-8 -*-
"""Foldy가 만드는 폴더 이름 (만드는 그 시점의 앱 언어로, 이미 있는 한국어 폴더는 그대로 쓴다). 파일 이름에 못 쓰는 글자(\\ / : * ? " < > |)는 쓰지 않는다."""

FOLDERS = [
    ("folder.desktopArchive", "바탕화면 보관", "Desktop archive", "Archivo del escritorio", "デスクトップ保管", "桌面归档"),
    ("folder.downloadsArchive", "보관", "Archive", "Archivo", "保管", "归档"),
    ("folder.unclassified", "_분류 필요", "_To sort", "_Por clasificar", "_要分類", "_待分类"),
    ("folder.duplicates", "_중복 후보", "_Possible duplicates", "_Posibles duplicados", "_重複候補", "_疑似重复"),
    ("ftg.documents", "문서", "Documents", "Documentos", "文書", "文档"),
    ("ftg.images", "이미지", "Images", "Imágenes", "画像", "图片"),
    ("ftg.videos", "영상", "Videos", "Vídeos", "動画", "视频"),
    ("ftg.music", "음악", "Music", "Música", "音楽", "音乐"),
    ("ftg.archives", "압축", "Archives", "Comprimidos", "圧縮ファイル", "压缩包"),
    ("ftg.installers", "설치파일", "Installers", "Instaladores", "インストーラー", "安装程序"),
    ("ftg.code", "코드", "Code", "Código", "コード", "代码"),
    ("ftg.other", "기타", "Other", "Otros", "その他", "其他"),
]

FOLDERS += [
    ("drive.space", "{0} 남음 / {1}", "{0} free of {1}", "{0} libres de {1}", "空き {0} / {1}", "可用 {0} / 共 {1}"),
    ("drive.offline", "연결 안 됨", "Not connected", "Sin conexión", "未接続", "未连接"),
]
