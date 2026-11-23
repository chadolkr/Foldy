# -*- coding: utf-8 -*-
"""큰 폴더 정리 (사용자 2026-11-23: 파일 80만 개·폴더 11만 개·93GB에서 계획 만들기가 끝나지 않음). (키, ko, en, es, ja, zh-Hans)"""

BIGORG = [
    ("org.counts", "파일 {0:N0}개 · 폴더 {1:N0}개 · {2}", "{0:N0} files · {1:N0} folders · {2}", "{0:N0} archivos · {1:N0} carpetas · {2}", "ファイル {0:N0} 個 · フォルダー {1:N0} 個 · {2}", "文件 {0:N0} 个 · 文件夹 {1:N0} 个 · {2}"),
    ("org.countsTotal", " / 전체 {0}", " / {0} in total", " / {0} en total", " / 全体 {0}", " / 共 {0}"),
    ("org.stage.dups", "같은 파일을 찾는 중… ({0:N0} / {1:N0})", "Looking for identical files… ({0:N0} / {1:N0})", "Buscando archivos idénticos… ({0:N0} / {1:N0})", "同じファイルを探しています… ({0:N0} / {1:N0})", "正在查找相同的文件…（{0:N0} / {1:N0}）"),
    ("org.stage.plan", "어디로 옮길지 정하는 중… ({0:N0} / {1:N0})", "Deciding where each file goes… ({0:N0} / {1:N0})", "Decidiendo adónde va cada archivo… ({0:N0} / {1:N0})", "移し先を決めています… ({0:N0} / {1:N0})", "正在决定每个文件放到哪里…（{0:N0} / {1:N0}）"),
    ("org.moreFiles", "… 외 {0:N0}개", "… and {0:N0} more", "… y {0:N0} más", "… ほか {0:N0} 個", "… 另外 {0:N0} 个"),
    ("org.tooBig", "폴더가 너무 커서 한 번에 계획을 만들 수 없어요. 안쪽 폴더를 하나씩 골라 정리해 주세요.", "This folder is too big to plan in one go. Please tidy the folders inside it one at a time.", "La carpeta es demasiado grande para planificarla de una vez. Ordena sus carpetas una por una.", "フォルダーが大きすぎて一度に計画を作れません。中のフォルダーを 1 つずつ選んで整理してください。", "文件夹太大，无法一次生成计划。请逐个选择里面的文件夹来整理。"),
    ("org.bg.offer", "큰 폴더라 계획을 만드는 데 몇 분 걸릴 수 있어요.", "This is a big folder, so making the plan may take a few minutes.", "Es una carpeta grande; crear el plan puede tardar unos minutos.", "大きなフォルダーなので、計画づくりに数分かかることがあります。", "这个文件夹很大，生成计划可能需要几分钟。"),
    ("org.bg.offerTip", "[뒤에서 하기]를 누르면 이 창을 닫고 계속 만들어요. 그동안 다른 일을 하셔도 되고, 다 되면 알려 드려요.", "Press [Do it in the background] to close this window and keep going. You can do other things meanwhile, and I'll let you know when it's ready.", "Pulsa [Hacerlo en segundo plano] para cerrar esta ventana y seguir. Mientras tanto puedes hacer otras cosas; te aviso cuando esté listo.", "[裏で続ける] を押すとこの画面を閉じて作り続けます。その間はほかの作業をしてもかまいません。できたらお知らせします。", "按 [在后台进行] 会关闭此窗口并继续生成。期间可以做别的事，完成后会通知你。"),
    ("org.bg.run", "뒤에서 하기", "Do it in the background", "Hacerlo en segundo plano", "裏で続ける", "在后台进行"),
    ("org.bg.wait", "여기서 기다리기", "Wait here", "Esperar aquí", "ここで待つ", "在这里等"),
    ("org.bg.job", "정리 계획 만들기: {0}", "Making a tidy plan: {0}", "Creando el plan de orden: {0}", "整理の計画づくり: {0}", "生成整理计划：{0}"),
    ("org.bg.open", "계획 보기", "See the plan", "Ver el plan", "計画を見る", "查看计划"),
    ("org.bg.ready", "'{0}' 정리 계획이 준비됐어요", "The tidy plan for '{0}' is ready", "El plan de orden de '{0}' está listo", "「{0}」の整理の計画ができました", "“{0}”的整理计划已准备好"),
    ("org.bg.failed", "'{0}' 정리 계획을 만들지 못했어요", "Couldn't make the tidy plan for '{0}'", "No se pudo crear el plan de orden de '{0}'", "「{0}」の整理の計画を作れませんでした", "未能生成“{0}”的整理计划"),
]
