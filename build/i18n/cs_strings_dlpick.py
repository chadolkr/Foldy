# -*- coding: utf-8 -*-
"""다운로드 정리: 묶음마다 파일 목록(이름·크기·마지막으로 연 날)을 먼저 보여 주고, 열어 보거나 폴더에서 보고 고르기 (사용자 의견 2026-11-27). (키, ko, en, es, ja, zh-Hans)"""

DLPICK = [
    ("cf.dl.listHint", "어떤 파일인지 아래에서 보세요. 남길 파일은 체크를 풀면 돼요.", "Here are the files. Uncheck any you want to keep.", "Estos son los archivos. Desmarca los que quieras conservar.", "どのファイルか下で確かめてください。残したいファイルはチェックを外します。", "下面是这些文件。想保留的文件取消勾选即可。"),
    ("cf.dl.lastUsed", "마지막으로 연 날 {0:yyyy-MM-dd}", "Last opened {0:yyyy-MM-dd}", "Abierto por última vez {0:yyyy-MM-dd}", "最後に開いた日 {0:yyyy-MM-dd}", "上次打开 {0:yyyy-MM-dd}"),
    ("cf.dl.archivePicked", "고른 파일 보관하기", "Archive checked files", "Archivar los marcados", "選んだファイルを保管", "归档勾选的文件"),
    ("cf.dl.recyclePicked", "고른 파일 휴지통으로", "Checked files to Recycle Bin", "Marcados a la papelera", "選んだファイルをごみ箱へ", "勾选的文件放进回收站"),
    ("cf.keepAll", "모두 그대로 두기", "Keep them all", "Dejarlos todos", "すべてそのまま", "全部保留"),
    ("cf.dl.leftAlone", "체크를 푼 {0:N0}개는 그대로 뒀어요.", "Left the {0:N0} unchecked files where they are.", "Los {0:N0} archivos sin marcar se quedan donde están.", "チェックを外した {0:N0} 個はそのままにしました。", "未勾选的 {0:N0} 个文件保持原样。"),
    ("guide.openFile", "열어 보기", "Open", "Abrir", "開いてみる", "打开看看"),
    ("guide.showInFolder", "폴더에서 보기", "Show in folder", "Mostrar en la carpeta", "フォルダーで表示", "在文件夹中显示"),
    ("guide.pickedFiles", "고른 파일 {0:N0}개 / {1:N0}개 · {2}", "{0:N0} of {1:N0} files checked · {2}", "{0:N0} de {1:N0} archivos marcados · {2}", "選んだファイル {0:N0} / {1:N0} 個 · {2}", "已勾选 {0:N0} / {1:N0} 个文件 · {2}"),
]
