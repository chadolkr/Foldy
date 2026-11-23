# -*- coding: utf-8 -*-
"""정리 도구: 큰 폴더의 정리 계획을 저장해 두고 이어서 하기, 정리 직전 다시 확인 (사용자 2026-11-23). (키, ko, en, es, ja, zh-Hans)"""

RESUME = [
    ("org.saved.offer", "{0}에 만든 정리 계획이 있어요. 옮길 파일 {1:N0}개 중 {2:N0}개를 체크해 두었어요.", "You have a tidy plan from {0}. {2:N0} of {1:N0} items are checked.", "Tienes un plan de orden de {0}. {2:N0} de {1:N0} elementos están marcados.", "{0} に作った整理計画があります。{1:N0} 個のうち {2:N0} 個にチェックが付いています。", "有一个 {0} 制定的整理计划。{1:N0} 个项目中已勾选 {2:N0} 个。"),
    ("org.saved.tip", "[이어서 하기]를 누르면 체크한 것까지 그대로 보여요. 정리할 때 그사이 바뀌거나 없어진 파일은 옮기지 않고 알려 드려요.", "Press [Continue] to see it again with your checks. When tidying, files that changed or disappeared since then are left alone and listed.", "Pulsa [Continuar] para verlo de nuevo con tus marcas. Al ordenar, los archivos que cambiaron o desaparecieron desde entonces no se mueven y te lo indico.", "[続きから] を押すと、チェックしたところまでそのまま表示します。整理するとき、その後に変わったファイルやなくなったファイルは移さずにお知らせします。", "按 [继续] 会按原样显示，包括你勾选的内容。整理时，之后有变动或已不存在的文件不会移动，并会告诉你。"),
    ("org.saved.resume", "이어서 하기", "Continue", "Continuar", "続きから", "继续"),
    ("org.saved.fresh", "새로 만들기", "Start over", "Empezar de nuevo", "新しく作る", "重新制定"),
    ("org.saved.today", "오늘 {0:t}", "today at {0:t}", "hoy a las {0:t}", "今日 {0:t}", "今天 {0:t}"),
    ("org.saved.yesterday", "어제 {0:t}", "yesterday at {0:t}", "ayer a las {0:t}", "昨日 {0:t}", "昨天 {0:t}"),
    ("org.saved.day", "{0:M} {0:t}", "{0:M} at {0:t}", "el {0:M} a las {0:t}", "{0:M} {0:t}", "{0:M} {0:t}"),
    ("org.saved.loading", "저장해 둔 계획을 불러오는 중이에요…", "Opening the saved plan…", "Abriendo el plan guardado…", "保存した計画を開いています…", "正在打开保存的计划…"),
    ("org.saved.gone", "저장해 둔 계획을 열 수 없어요. [계획 만들기]로 새로 만들어 주세요.", "The saved plan can't be opened. Press [Make a plan] to make a new one.", "No se puede abrir el plan guardado. Pulsa [Crear un plan] para hacer uno nuevo.", "保存した計画を開けません。[計画を作る] で新しく作ってください。", "无法打开保存的计划。请按 [生成计划] 重新制定。"),
    ("org.saved.resumed", "{0}에 만든 계획이에요. 체크한 것도 그대로예요. [정리하기]를 누르면 그사이 바뀐 파일은 옮기지 않아요.", "This is the plan from {0}, with your checks. When you press [Tidy up], files that changed since then are not moved.", "Es el plan de {0}, con tus marcas. Al pulsar [Ordenar], los archivos que cambiaron desde entonces no se mueven.", "{0} に作った計画です。チェックもそのままです。[整理する] を押しても、その後に変わったファイルは移しません。", "这是 {0} 制定的计划，勾选内容也保留着。按 [整理] 时，之后有变动的文件不会移动。"),
    ("org.changedSkipped", " 계획을 만든 뒤 바뀌었거나 없어진 {0:N0}개는 옮기지 않았어요.", " {0:N0} items that changed or disappeared after the plan was made were not moved.", " {0:N0} elementos que cambiaron o desaparecieron después de hacer el plan no se movieron.", " 計画を作った後に変わった、またはなくなった {0:N0} 個は移していません。", " 制定计划后有变动或已不存在的 {0:N0} 个项目未移动。"),
    ("oe.changedSince", "계획을 만든 뒤 바뀌었어요.", "Changed after the plan was made.", "Cambió después de hacer el plan.", "計画を作った後に変わりました。", "制定计划后已更改。"),
]
