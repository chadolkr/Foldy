# -*- coding: utf-8 -*-
"""업데이트 진행 창 (Foldy가 닫혀 있는 동안 PowerShell로 뜨는 작은 창). 서비스 기록에서 찾는 표시 말은 옮기지 않는다. (키, ko, en, es, ja, zh-Hans)"""

UP = [
    ("up.heading", "Foldy를 {0} 버전으로 업데이트하고 있어요", "Updating Foldy to {0}", "Actualizando Foldy a {0}", "Foldy を {0} に更新しています", "正在将 Foldy 更新到 {0}"),
    ("up.waitClose", "Foldy가 닫히기를 기다리는 중이에요… 끝나면 Foldy가 저절로 다시 열려요.", "Waiting for Foldy to close… Foldy reopens by itself when it's done.", "Esperando a que Foldy se cierre… Foldy se volverá a abrir solo al terminar.", "Foldy が閉じるのを待っています… 終わると Foldy が自動でまた開きます。", "正在等待 Foldy 关闭… 完成后 Foldy 会自动重新打开。"),
    ("up.failed", "업데이트를 설치하지 못했어요. 지금 쓰던 버전은 그대로예요. Foldy를 다시 열고 [업데이트]를 한 번 더 눌러 주세요.", "The update couldn't be installed. Your current version is unchanged. Reopen Foldy and press [Update] once more.", "No se pudo instalar la actualización. Tu versión actual no cambia. Vuelve a abrir Foldy y pulsa [Actualizar] otra vez.", "更新をインストールできませんでした。今のバージョンはそのままです。Foldy を開き直して [更新] をもう一度押してください。", "无法安装更新，当前版本保持不变。请重新打开 Foldy，再按一次 [更新]。"),
    ("up.waitOther", "열려 있는 다른 Foldy 창이 닫히기를 기다리고 있어요. 다른 Foldy 창을 모두 닫아 주세요.", "Waiting for other open Foldy windows to close. Please close all other Foldy windows.", "Esperando a que se cierren otras ventanas de Foldy. Cierra todas las demás ventanas de Foldy.", "開いているほかの Foldy の画面が閉じるのを待っています。ほかの Foldy の画面をすべて閉じてください。", "正在等待其他打开的 Foldy 窗口关闭。请关闭所有其他 Foldy 窗口。"),
    ("up.installing", "설치하고 있어요… 보통 1분 안에 끝나요.", "Installing… This usually takes less than a minute.", "Instalando… Suele tardar menos de un minuto.", "インストールしています… 普通は 1 分以内に終わります。", "正在安装… 通常一分钟内完成。"),
    ("up.installed", "설치가 끝났어요. Foldy를 다시 여는 중이에요…", "Installed. Reopening Foldy…", "Instalado. Volviendo a abrir Foldy…", "インストールが終わりました。Foldy をまた開いています…", "安装完成，正在重新打开 Foldy…"),
    ("up.done", "다 됐어요!", "All done!", "¡Listo!", "完了しました!", "完成了！"),
    ("up.doneOpen", "다 됐어요! 새 Foldy가 열려요.", "All done! The new Foldy is opening.", "¡Listo! Se abre el nuevo Foldy.", "完了しました! 新しい Foldy が開きます。", "完成了！新的 Foldy 正在打开。"),
    ("up.slow", "생각보다 오래 걸리고 있어요. 다른 Foldy 창이 열려 있으면 닫아 주세요. 끝나면 Foldy를 열어 드릴게요.", "This is taking longer than expected. If another Foldy window is open, please close it. I'll open Foldy when it's done.", "Está tardando más de lo previsto. Si hay otra ventana de Foldy abierta, ciérrala. Abriré Foldy al terminar.", "思ったより時間がかかっています。ほかの Foldy の画面が開いていたら閉じてください。終わったら Foldy を開きます。", "耗时比预期长。如果有其他 Foldy 窗口打开，请关闭。完成后会为你打开 Foldy。"),
]
