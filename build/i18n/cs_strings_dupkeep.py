# -*- coding: utf-8 -*-
"""정리 도구: 같은 파일 묶음에서 어느 것과 같은지 경로를 보고 남길 파일 고르기 (사용자 2026-11-23). (키, ko, en, es, ja, zh-Hans)"""

DUPKEEP = [
    ("org.dupsTab", "같은 파일 ({0:N0}묶음)", "Identical files ({0:N0} sets)", "Archivos idénticos ({0:N0} grupos)", "同じファイル ({0:N0} 組)", "相同的文件（{0:N0} 组）"),
    ("org.dup.title", "같은 파일 {0:N0}개: {1} ({2})", "{0:N0} identical files: {1} ({2})", "{0:N0} archivos idénticos: {1} ({2})", "同じファイル {0:N0} 個: {1} ({2})", "{0:N0} 个相同的文件：{1}（{2}）"),
    ("org.dup.tip", "남길 파일 하나를 고르세요. 고른 파일은 정리 규칙대로 두고, 나머지는 '{0}' 폴더로 옮겨요. 아무것도 지우지 않아요.", "Pick the one file to keep. It is tidied as usual, and the others move to the '{0}' folder. Nothing is deleted.", "Elige el archivo que quieres conservar. Se ordena como siempre y los demás pasan a la carpeta '{0}'. No se borra nada.", "残すファイルを 1 つ選んでください。選んだファイルはいつもどおり整理し、ほかは「{0}」フォルダーへ移します。何も削除しません。", "请选择要保留的一个文件。它会照常整理，其余的移到“{0}”文件夹。不会删除任何文件。"),
    ("org.dup.keep", "남기기", "Keep", "Conservar", "残す", "保留"),
    ("org.dup.keepTo", "남겨요 → {0}", "Kept → {0}", "Se conserva → {0}", "残します → {0}", "保留 → {0}"),
    ("org.dup.keepHere", "남겨요 (그 자리에 그대로)", "Kept (stays where it is)", "Se conserva (se queda donde está)", "残します (その場所のまま)", "保留（留在原处）"),
    ("org.dup.toDups", "'{0}' 폴더로 옮겨요", "Moves to the '{0}' folder", "Pasa a la carpeta '{0}'", "「{0}」フォルダーへ移します", "移到“{0}”文件夹"),
    ("org.dup.stays", "옮기지 않아요 (그 자리에 그대로)", "Not moved (stays where it is)", "No se mueve (se queda donde está)", "移しません (その場所のまま)", "不移动（留在原处）"),
    ("org.dup.modified", "수정한 날 {0:yyyy-MM-dd HH:mm}", "Modified {0:yyyy-MM-dd HH:mm}", "Modificado {0:yyyy-MM-dd HH:mm}", "更新日時 {0:yyyy-MM-dd HH:mm}", "修改时间 {0:yyyy-MM-dd HH:mm}"),
    ("org.dup.col.name", "이름", "Name", "Nombre", "名前", "名称"),
    ("org.dup.col.count", "개수", "Copies", "Copias", "個数", "数量"),
    ("org.dup.col.size", "크기", "Size", "Tamaño", "サイズ", "大小"),
    ("org.dup.col.keeper", "남길 파일 (정리할 폴더 기준 위치)", "File to keep (location in this folder)", "Archivo que se conserva (ubicación en esta carpeta)", "残すファイル (このフォルダー内の場所)", "保留的文件（在此文件夹中的位置）"),
    ("org.dup.pick", "줄을 누르면 같은 파일들이 어디에 있는지 보고 남길 파일을 고를 수 있어요.", "Click a row to see where each identical file is and pick the one to keep.", "Haz clic en una fila para ver dónde está cada archivo idéntico y elegir cuál conservar.", "行を押すと、同じファイルがそれぞれどこにあるかを見て、残すファイルを選べます。", "点击一行即可查看每个相同文件的位置，并选择要保留的文件。"),
    # 이유별 묶음 막대·보이는 것만 체크 (사용자 2026-11-23: 중복만·기타만 골라 보고, 전체 체크·해제, 많은 것부터)
    ("org.cat.title", "어디로 가나요 (많은 순) · 누르면 그것만 보여요", "Where files go (most first) · click to show only those", "Adónde van (de más a menos) · haz clic para ver solo esos", "移し先 (多い順) · 押すとそれだけ表示", "去向（按数量）· 点击只看这一类"),
    ("org.cat.all", "모두", "All", "Todos", "すべて", "全部"),
    ("org.cat.detail", "{0:N0}개 · 체크 {1:N0} · {2}", "{0:N0} · {1:N0} checked · {2}", "{0:N0} · {1:N0} marcados · {2}", "{0:N0} 個 · チェック {1:N0} · {2}", "{0:N0} 个 · 已勾选 {1:N0} · {2}"),
    ("org.checkAll", "보이는 것 모두 체크", "Check all shown", "Marcar todo lo visible", "表示中をすべてチェック", "勾选显示的全部"),
    ("org.uncheckAll", "보이는 것 모두 해제", "Uncheck all shown", "Desmarcar todo lo visible", "表示中をすべて外す", "取消勾选显示的全部"),
    ("org.visible", "{0:N0}개 보는 중", "Showing {0:N0}", "Mostrando {0:N0}", "{0:N0} 個を表示中", "正在显示 {0:N0} 个"),
    # 위 트리 한쪽 숨기기·다시 보기 (사용자 2026-11-23: 한쪽을 줄이거나 닫았다가 다시 열기)
    ("org.tree.hide", "숨기기", "Hide", "Ocultar", "隠す", "隐藏"),
    ("org.tree.showAfter", "정리 후 다시 보기", "Show 'After' again", "Volver a mostrar 'Después'", "整理後をもう一度表示", "重新显示“整理后”"),
    ("org.tree.showBefore", "지금 다시 보기", "Show 'Now' again", "Volver a mostrar 'Ahora'", "今をもう一度表示", "重新显示“现在”"),
    # 주 창: 한쪽 창만 크게 / 두 창으로 (사용자 2026-11-23)
    ("pane.single.on", "이 창만 크게", "Show only this pane", "Mostrar solo este panel", "この画面だけ大きく", "只显示这个窗格"),
    ("pane.single.on.tip", "이 창만 크게: 다른 쪽 창을 접어요 (같은 단추로 다시 두 창)", "Show only this pane: hides the other one (press again for both)", "Mostrar solo este panel: oculta el otro (pulsa otra vez para ver los dos)", "この画面だけ大きく: もう片方を隠します (もう一度押すと 2 画面)", "只显示这个窗格：隐藏另一个（再按一次恢复两个）"),
    ("pane.single.off", "두 창으로 보기", "Show both panes", "Mostrar los dos paneles", "2 画面で表示", "显示两个窗格"),
    ("pane.single.off.tip", "두 창으로 보기: 접었던 창을 원래 넓이로 다시 열어요 (Tab을 누르면 다른 창을 크게)", "Show both panes: brings the hidden one back at its old width (Tab shows the other one full width)", "Mostrar los dos paneles: vuelve a abrir el oculto con su ancho anterior (Tab muestra el otro a pantalla completa)", "2 画面で表示: 隠した画面を元の幅で開きます (Tab でもう片方を大きく)", "显示两个窗格：按原来的宽度重新打开隐藏的窗格（按 Tab 可放大另一个）"),
    ("cmd.singlePane", "이 창만 크게 / 두 창으로 보기", "Show only this pane / both panes", "Mostrar solo este panel / los dos", "この画面だけ大きく / 2 画面", "只显示这个窗格 / 两个窗格"),
]
