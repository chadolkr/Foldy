# -*- coding: utf-8 -*-
"""
Foldy 다국어 글 원본 (2026-11-21~). 이 파일을 고치고 `python build/i18n/strings.py`를 실행하면
assets/i18n/<언어>.json이 다시 만들어진다. 언어를 더하려면 LANGS에 코드를 더하고 모든 글에 그 언어를 채운다
(빠진 것이 있으면 이 스크립트가 멈춘다). 앱은 Foldy.Core.Localization.Loc으로 읽는다.
"""
import json, os, sys

LANGS = ["ko", "en", "es", "ja", "zh-Hans"]

S = {
    # 한 줄 소개
    "tagline": {"ko": "빠르고 똑똑한 파일 관리자, 오프라인에서도", "en": "A fast, smart file manager, even offline", "es": "Un gestor de archivos rápido e inteligente, incluso sin conexión", "ja": "速くて賢いファイルマネージャー、オフラインでも", "zh-Hans": "快速又聪明的文件管理器，离线也能用"},
    "tagline.sub": {"ko": "AI와 연결하면 더 편하게", "en": "Even easier when connected to AI", "es": "Aún más fácil conectado a la IA", "ja": "AIとつなげばもっと便利に", "zh-Hans": "连接 AI 更方便"},

    # 사이드바: 대표 기능 넷
    "side.find": {"ko": "무엇이든 찾기", "en": "Find anything", "es": "Buscar todo", "ja": "なんでも検索", "zh-Hans": "查找任何东西"},
    "side.find.tip": {"ko": "파일·앱·Windows 설정·Foldy 기능을 한 칸에서 (어디서든 Ctrl+Space)", "en": "Files, apps, Windows settings and Foldy features in one box (Ctrl+Space anywhere)", "es": "Archivos, apps, ajustes de Windows y funciones de Foldy en un solo cuadro (Ctrl+Espacio en cualquier lugar)", "ja": "ファイル・アプリ・Windows の設定・Foldy の機能をひとつの欄で (どこでも Ctrl+Space)", "zh-Hans": "在一个框里查找文件、应用、Windows 设置和 Foldy 功能（随处按 Ctrl+Space）"},
    "side.space": {"ko": "공간 늘리기", "en": "Free up space", "es": "Liberar espacio", "ja": "空き容量を増やす", "zh-Hans": "释放空间"},
    "side.space.tip": {"ko": "폴더 분석: 무엇이 공간을 차지하는지 보고 지워도 되는 것만 골라 비우기", "en": "Folder analysis: see what takes up space and remove only what is safe to delete", "es": "Análisis de carpetas: mira qué ocupa espacio y elimina solo lo que se puede borrar", "ja": "フォルダー分析: 何が容量を使っているかを見て、消しても大丈夫なものだけ片付けます", "zh-Hans": "文件夹分析：查看什么占用了空间，只清理可以安全删除的内容"},
    "side.tidy": {"ko": "바탕화면, 다운로드, 폴더 정리", "en": "Tidy up folders", "es": "Ordenar carpetas", "ja": "フォルダーを整理", "zh-Hans": "整理文件夹"},
    "side.tidy.tip": {"ko": "정리 도우미: 어디를 · 어떻게 고르면 정리 후 모습을 먼저 보여 줘요 (지우지 않고, 되돌릴 수 있어요)", "en": "Tidy helper: choose where and how, and see the result before anything moves (nothing is deleted, and you can undo)", "es": "Asistente de orden: elige dónde y cómo, y mira el resultado antes de mover nada (no se borra nada y puedes deshacer)", "ja": "整理アシスタント: 場所と方法を選ぶと、動かす前に整理後の姿を見せます (削除なし、元に戻せます)", "zh-Hans": "整理助手：选好位置和方式，先看整理后的样子（不会删除，可以撤销）"},
    "side.template": {"ko": "템플릿으로 폴더 만들기", "en": "Folders from a template", "es": "Carpetas con plantilla", "ja": "テンプレートで作成", "zh-Hans": "用模板创建文件夹"},
    "side.template.tip": {"ko": "PARA·업종별 프로젝트 단계 같은 폴더 틀을 미리 보고 한 번에 만들기 (Ctrl+Shift+N)", "en": "Preview and create a folder structure such as PARA or industry project phases in one go (Ctrl+Shift+N)", "es": "Previsualiza y crea de una vez una estructura como PARA o las fases de un proyecto por sector (Ctrl+Mayús+N)", "ja": "PARA や業種別のプロジェクト工程などのフォルダー構成を確認して一度に作成 (Ctrl+Shift+N)", "zh-Hans": "预览并一次创建 PARA 或各行业项目阶段等文件夹结构（Ctrl+Shift+N）"},

    # 사이드바: 제목과 묶음
    "side.pinned": {"ko": "자주 쓰는 폴더", "en": "Pinned folders", "es": "Carpetas fijadas", "ja": "よく使うフォルダー", "zh-Hans": "常用文件夹"},
    "side.recent": {"ko": "최근 폴더", "en": "Recent folders", "es": "Carpetas recientes", "ja": "最近のフォルダー", "zh-Hans": "最近的文件夹"},
    "side.drives": {"ko": "드라이브", "en": "Drives", "es": "Unidades", "ja": "ドライブ", "zh-Hans": "驱动器"},
    "side.tools": {"ko": "도구", "en": "Tools", "es": "Herramientas", "ja": "ツール", "zh-Hans": "工具"},
    "side.group.find": {"ko": "찾기", "en": "Find", "es": "Buscar", "ja": "探す", "zh-Hans": "查找"},
    "side.group.tidy": {"ko": "정리하기", "en": "Tidy", "es": "Ordenar", "ja": "整理する", "zh-Hans": "整理"},
    "side.group.protect": {"ko": "지키기", "en": "Protect", "es": "Proteger", "ja": "守る", "zh-Hans": "保护"},
    "side.group.connect": {"ko": "연결하기", "en": "Connect", "es": "Conectar", "ja": "つなぐ", "zh-Hans": "连接"},
    "side.recycle": {"ko": "휴지통", "en": "Recycle Bin", "es": "Papelera", "ja": "ごみ箱", "zh-Hans": "回收站"},

    # 한 줄 제안
    "suggest.desktop": {"ko": "바탕화면에 파일이 {0}개 있어요. 종류별로 정리해 드릴까요? 지우지 않고, 되돌릴 수 있어요.", "en": "There are {0} files on your desktop. Shall I sort them by type? Nothing is deleted, and you can undo.", "es": "Hay {0} archivos en tu escritorio. ¿Los ordeno por tipo? No se borra nada y puedes deshacerlo.", "ja": "デスクトップにファイルが {0} 個あります。種類別に整理しましょうか? 削除はせず、元に戻せます。", "zh-Hans": "桌面上有 {0} 个文件。要按类型整理吗？不会删除，可以撤销。"},
    "suggest.downloads": {"ko": "다운로드 폴더에 파일이 {0}개 있어요. 이미 설치한 설치 파일·중복을 골라 정리해 드릴까요?", "en": "There are {0} files in Downloads. Shall I pick out installers you already used and duplicates?", "es": "Hay {0} archivos en Descargas. ¿Separo los instaladores ya usados y los duplicados?", "ja": "ダウンロードにファイルが {0} 個あります。インストール済みのインストーラーや重複を選んで整理しましょうか?", "zh-Hans": "下载文件夹里有 {0} 个文件。要挑出已安装过的安装包和重复文件来整理吗？"},
    "suggest.ai": {"ko": "{0}에게 \"{1}\"라고 하면 Foldy가 내 PC에서 찾아 줘요. 읽기만 해요.", "en": "Ask {0} \"{1}\" and Foldy finds it on your PC. Read-only.", "es": "Pídele a {0} \"{1}\" y Foldy lo busca en tu PC. Solo lectura.", "ja": "{0} に「{1}」と頼むと、Foldy が PC の中から探します。読み取りのみです。", "zh-Hans": "对 {0} 说“{1}”，Foldy 就会在你的电脑上找到。只读。"},
    "suggest.tidy": {"ko": "정리하기", "en": "Tidy up", "es": "Ordenar", "ja": "整理する", "zh-Hans": "整理"},
    "suggest.connect": {"ko": "연결하기", "en": "Connect", "es": "Conectar", "ja": "接続する", "zh-Hans": "连接"},
    "suggest.later": {"ko": "나중에", "en": "Later", "es": "Más tarde", "ja": "あとで", "zh-Hans": "稍后"},
    "suggest.never": {"ko": "다시 묻지 않기", "en": "Don't ask again", "es": "No volver a preguntar", "ja": "今後表示しない", "zh-Hans": "不再询问"},
    "common.close": {"ko": "닫기", "en": "Close", "es": "Cerrar", "ja": "閉じる", "zh-Hans": "关闭"},

    # AI에게 물어보는 예시 (한 줄 제안·AI 연결 화면)
    "ai.ex.0": {"ko": "작년 견적서 찾아 줘", "en": "Find last year's quotes", "es": "Busca los presupuestos del año pasado", "ja": "去年の見積書を探して", "zh-Hans": "找一下去年的报价单"},
    "ai.ex.1": {"ko": "D 드라이브에서 공간을 많이 차지하는 폴더가 뭐야?", "en": "Which folders take up the most space on drive D?", "es": "¿Qué carpetas ocupan más espacio en la unidad D?", "ja": "D ドライブで容量を多く使っているフォルダーは?", "zh-Hans": "D 盘里哪些文件夹最占空间？"},
    "ai.ex.2": {"ko": "다운로드 폴더에 뭐가 있는지 정리해서 알려 줘", "en": "Summarize what's in my Downloads folder", "es": "Resume qué hay en mi carpeta de Descargas", "ja": "ダウンロードフォルダーに何があるかまとめて", "zh-Hans": "总结一下下载文件夹里有什么"},
    "ai.ex.3": {"ko": "이 프로젝트 폴더 설명 읽어 줘", "en": "Read me this project folder's description", "es": "Léeme la descripción de esta carpeta de proyecto", "ja": "このプロジェクトフォルダーの説明を読んで", "zh-Hans": "读一下这个项目文件夹的说明"},
    "ai.ex.4": {"ko": "보고서 폴더에서 아직 검토 중인 파일 알려 줘", "en": "Which files in the reports folder are still in review?", "es": "¿Qué archivos de la carpeta de informes siguen en revisión?", "ja": "報告書フォルダーでまだレビュー中のファイルは?", "zh-Hans": "报告文件夹里哪些文件还在审阅中？"},
    "ai.ex.5": {"ko": "드라이브 남은 공간과 색인 상태 알려 줘", "en": "Show free space and index status for my drives", "es": "Muestra el espacio libre y el estado del índice de mis unidades", "ja": "ドライブの空き容量とインデックスの状態を教えて", "zh-Hans": "告诉我各驱动器的剩余空间和索引状态"},

    # 짧은 소개
    "tour.qs.t": {"ko": "어디서든 Ctrl+Space, 치는 순간 찾아요", "en": "Ctrl+Space anywhere, found as you type", "es": "Ctrl+Espacio en cualquier lugar: lo encuentra mientras escribes", "ja": "どこでも Ctrl+Space、打った瞬間に見つかる", "zh-Hans": "随处按 Ctrl+Space，边输入边找到"},
    "tour.qs.s": {"ko": "Spotlight처럼 · 217만 개 파일 중에서도 0.003초 · Enter로 열기", "en": "0.003 s even across 2.17 million files · Enter to open", "es": "0,003 s incluso entre 2,17 millones de archivos · Intro para abrir", "ja": "217 万個のファイルからでも 0.003 秒 · Enter で開く", "zh-Hans": "217 万个文件中也只需 0.003 秒 · 按 Enter 打开"},
    "tour.qs2.t": {"ko": "Windows 설정·앱도 바로", "en": "Windows settings and apps too", "es": "También ajustes de Windows y apps", "ja": "Windows の設定やアプリもすぐ", "zh-Hans": "Windows 设置和应用也能直接打开"},
    "tour.qs2.s": {"ko": "'디스'만 쳐도 디스플레이 설정 · 앱은 이름으로 실행", "en": "Type 'disp' for Display settings · launch apps by name", "es": "Escribe 'pant' para la configuración de pantalla · abre apps por nombre", "ja": "「ディス」と打つだけでディスプレイ設定 · アプリは名前で起動", "zh-Hans": "输入“显示”即可打开显示设置 · 按名称启动应用"},
    "tour.qs3.t": {"ko": "계산·단위 변환도 바로", "en": "Math and unit conversion too", "es": "También cálculos y conversión de unidades", "ja": "計算や単位変換もすぐ", "zh-Hans": "计算和单位换算也能直接做"},
    "tour.qs3.s": {"ko": "32평 = 105.79㎡ · Enter로 결과 복사", "en": "10 ft = 3.048 m · Enter copies the result", "es": "10 ft = 3,048 m · Intro copia el resultado", "ja": "32 坪 = 105.79㎡ · Enter で結果をコピー", "zh-Hans": "10 ft = 3.048 m · 按 Enter 复制结果"},
    "tour.qs4.t": {"ko": "말하듯 써도 알아들어요", "en": "Type it the way you'd say it", "es": "Escríbelo como lo dirías", "ja": "話すように書いても伝わります", "zh-Hans": "像说话一样输入也能听懂"},
    "tour.qs4.s": {"ko": "'다운로드 정리해줘' → Foldy 기능이 바로 시작", "en": "'tidy my downloads' → the Foldy feature starts right away", "es": "'ordena mis descargas' → la función de Foldy empieza al instante", "ja": "「ダウンロードを整理して」→ Foldy の機能がすぐ始まる", "zh-Hans": "“整理我的下载” → Foldy 功能立即开始"},
    "tour.space.t": {"ko": "용량 범인을 한눈에", "en": "Spot what eats your disk at a glance", "es": "Descubre de un vistazo qué ocupa tu disco", "ja": "容量を食うものがひと目でわかる", "zh-Hans": "一眼看出谁占了磁盘"},
    "tour.space.s": {"ko": "큰 상자가 큰 폴더 · 지워도 되는 것만 골라 줘요", "en": "Big boxes are big folders · it picks only what's safe to delete", "es": "Los cuadros grandes son carpetas grandes · solo elige lo que se puede borrar", "ja": "大きな箱が大きなフォルダー · 消してもよいものだけ選びます", "zh-Hans": "大方块就是大文件夹 · 只挑出可以删除的"},
    "tour.junk.t": {"ko": "개발 찌꺼기 폴더, 크기 순으로 한 번에", "en": "Build leftovers, largest first, in one go", "es": "Restos de compilación, de mayor a menor, de una vez", "ja": "開発の残りフォルダーを大きい順にまとめて", "zh-Hans": "开发残留文件夹，按大小一次清理"},
    "tour.junk.s": {"ko": "node_modules·bin·__history 등 · .git은 경고 · 휴지통으로 되살릴 수 있어요", "en": "node_modules, bin, __history… · .git gets a warning · restore from the Recycle Bin", "es": "node_modules, bin, __history… · .git con aviso · se recupera de la Papelera", "ja": "node_modules・bin・__history など · .git は警告 · ごみ箱から戻せます", "zh-Hans": "node_modules、bin、__history 等 · .git 会提醒 · 可从回收站恢复"},
    "tour.desk.t": {"ko": "쌓인 바탕화면, 한 번에 정리", "en": "Clear a cluttered desktop in one go", "es": "Ordena un escritorio desordenado de una vez", "ja": "散らかったデスクトップを一度に整理", "zh-Hans": "一次整理杂乱的桌面"},
    "tour.desk.s": {"ko": "'바탕화면 보관' 폴더에 달별·종류별로 · 되돌리기 한 번이면 원래대로", "en": "Into an archive folder by month and type · one undo puts it all back", "es": "A una carpeta de archivo por mes y tipo · un deshacer lo devuelve todo", "ja": "保管フォルダーに月別・種類別に · 一回の元に戻すで元どおり", "zh-Hans": "按月份和类型放进归档文件夹 · 一次撤销即可还原"},
    "tour.para.t": {"ko": "뒤죽박죽 폴더도 PARA 네 칸으로", "en": "Even a messy folder into the four PARA boxes", "es": "Incluso una carpeta desordenada en las cuatro cajas PARA", "ja": "ごちゃごちゃのフォルダーも PARA の 4 つの箱へ", "zh-Hans": "杂乱的文件夹也能分进 PARA 四格"},
    "tour.para.s": {"ko": "정리 후 모습을 먼저 보여 줘요 · 끝난 프로젝트는 보관으로 · 되돌릴 수 있어요", "en": "See the result first · finished projects go to Archives · you can undo", "es": "Primero ves el resultado · los proyectos terminados van al Archivo · puedes deshacer", "ja": "先に整理後の姿を見せます · 終わったプロジェクトは保管へ · 元に戻せます", "zh-Hans": "先给你看整理后的样子 · 完成的项目放进归档 · 可以撤销"},
    "tour.tpl.t": {"ko": "폴더 틀을 클릭 한 번에", "en": "A folder structure in one click", "es": "Una estructura de carpetas en un clic", "ja": "フォルダー構成をワンクリックで", "zh-Hans": "一键创建文件夹结构"},
    "tour.tpl.s": {"ko": "PARA 네 칸 · 업종별 프로젝트 단계(PMBOK·IT 개발·건설 설계) · 칸마다 무엇을 넣는지까지", "en": "PARA · industry project phases (PMBOK, IT, construction) · with what goes in each folder", "es": "PARA · fases de proyecto por sector (PMBOK, TI, construcción) · con qué va en cada carpeta", "ja": "PARA · 業種別のプロジェクト工程 (PMBOK・IT 開発・建設設計) · 各フォルダーに何を入れるかまで", "zh-Hans": "PARA · 各行业项目阶段（PMBOK、IT 开发、建筑设计）· 还说明每个文件夹放什么"},
    "tour.copy.t": {"ko": "끌어다 놓으면 복사 끝", "en": "Drag and drop to copy", "es": "Arrastra y suelta para copiar", "ja": "ドラッグ＆ドロップでコピー完了", "zh-Hans": "拖放即可复制"},
    "tour.copy.s": {"ko": "두 창 사이로 끌거나 F5 · 이동은 F6", "en": "Drag between the two panes or press F5 · F6 to move", "es": "Arrastra entre los dos paneles o pulsa F5 · F6 para mover", "ja": "2 つの画面の間でドラッグか F5 · 移動は F6", "zh-Hans": "在两个窗格间拖动或按 F5 · 移动按 F6"},
    "tour.ai.t": {"ko": "AI에게 물어보면 Foldy가 찾아요", "en": "Ask your AI, Foldy finds it", "es": "Pregúntale a tu IA y Foldy lo encuentra", "ja": "AI に聞けば Foldy が探します", "zh-Hans": "问 AI，Foldy 帮你找"},
    "tour.ai.s": {"ko": "Claude·Codex 등에 [AI에 연결] 한 번 · \"작년 견적서 찾아 줘\" · 읽기만 해요", "en": "One click to connect Claude, Codex and more · \"find last year's quotes\" · read-only", "es": "Un clic para conectar Claude, Codex y más · \"busca los presupuestos del año pasado\" · solo lectura", "ja": "Claude・Codex などにワンクリックで接続 · 「去年の見積書を探して」· 読み取りのみ", "zh-Hans": "一键连接 Claude、Codex 等 · “找一下去年的报价单” · 只读"},
    "tour.memo.t": {"ko": "비밀번호는 잠긴 메모에", "en": "Keep passwords in a locked note", "es": "Guarda las contraseñas en una nota bloqueada", "ja": "パスワードはロック付きメモに", "zh-Hans": "密码放进加锁笔记"},
    "tour.memo.s": {"ko": "5분 동안 안 쓰면 저절로 잠겨요", "en": "Locks itself after 5 minutes idle", "es": "Se bloquea sola tras 5 minutos sin uso", "ja": "5 分使わないと自動でロック", "zh-Hans": "5 分钟不用就自动锁定"},
    "tour.git.t": {"ko": "프로젝트 폴더면 Git이 저절로", "en": "Git shows up in project folders", "es": "Git aparece solo en carpetas de proyecto", "ja": "プロジェクトフォルダーなら Git が自動で", "zh-Hans": "项目文件夹里自动显示 Git"},
    "tour.git.s": {"ko": "받기·올리기·커밋을 버튼으로", "en": "Pull, push and commit with buttons", "es": "Pull, push y commit con botones", "ja": "取得・送信・コミットをボタンで", "zh-Hans": "用按钮拉取、推送和提交"},
    "tour.end.s": {"ko": "모르는 게 있으면 F1 · 이 소개는 ? → 짧은 소개 보기에서 다시 볼 수 있어요", "en": "Press F1 for help · replay this from ? → Short intro", "es": "Pulsa F1 para ayuda · vuelve a verlo desde ? → Introducción breve", "ja": "わからないときは F1 · この紹介は ? → 短い紹介 からもう一度見られます", "zh-Hans": "有问题按 F1 · 可在 ? → 简短介绍 中重看"},
    "tour.skip": {"ko": "건너뛰기", "en": "Skip", "es": "Omitir", "ja": "スキップ", "zh-Hans": "跳过"},
    "tour.start": {"ko": "시작하기", "en": "Get started", "es": "Empezar", "ja": "はじめる", "zh-Hans": "开始使用"},

    # 설정: 언어
    "settings.language": {"ko": "언어", "en": "Language", "es": "Idioma", "ja": "言語", "zh-Hans": "语言"},
    "settings.language.auto": {"ko": "시스템 설정 따르기", "en": "Use system setting", "es": "Usar la configuración del sistema", "ja": "システム設定に従う", "zh-Hans": "跟随系统设置"},
    "settings.language.hint": {"ko": "바꾸면 Foldy를 다시 켤 때 적용돼요.", "en": "Takes effect the next time Foldy starts.", "es": "Se aplica la próxima vez que inicies Foldy.", "ja": "次に Foldy を起動したときに反映されます。", "zh-Hans": "下次启动 Foldy 时生效。"},
}


# 주 창 메뉴·단추 (shell_strings.py)
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from shell_strings import SHELL  # noqa: E402
from xaml_strings import XAML  # noqa: E402
from xaml_strings2 import XAML2  # noqa: E402
from cs_strings import CS  # noqa: E402
for _k, _ko, _en, _es, _ja, _zh in SHELL + XAML + XAML2 + CS:
    S[_k] = {"ko": _ko, "en": _en, "es": _es, "ja": _ja, "zh-Hans": _zh}


def main():
    root = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
    out = os.path.join(root, "assets", "i18n")
    os.makedirs(out, exist_ok=True)
    missing = [(k, l) for k, v in S.items() for l in LANGS if not v.get(l)]
    if missing:
        sys.exit("빠진 번역: " + ", ".join(f"{k}[{l}]" for k, l in missing))
    for l in LANGS:
        table = {k: v[l] for k, v in S.items()}
        with open(os.path.join(out, l + ".json"), "w", encoding="utf-8", newline="\n") as f:
            json.dump(table, f, ensure_ascii=False, indent=2, sort_keys=True)
            f.write("\n")
    print(f"{len(S)} keys x {len(LANGS)} languages")


if __name__ == "__main__":
    main()
