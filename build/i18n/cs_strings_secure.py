# -*- coding: utf-8 -*-
"""잠긴 메모·파일 잠그기·금고를 쉽게 (사용자 2026-11-22: 주변 사람들이 어떻게 써야 할지 모르겠다고 함). (키, ko, en, es, ja, zh-Hans)"""

SECURE = [
    ("guide.passwordNew", "암호 ({0}글자 이상)", "Password (at least {0} characters)", "Contraseña (al menos {0} caracteres)", "パスワード ({0} 文字以上)", "密码（至少 {0} 个字符）"),
    ("sec.memoWhere", "\"{1}\" 폴더에 \"{0}\"로 저장했어요. 다시 열 때는 이 파일을 두 번 누르세요. 5분 동안 손대지 않으면 저장하고 잠가요.", "Saved as \"{0}\" in the \"{1}\" folder. To open it again, double-click that file. It saves and locks after 5 minutes without use.", "Se guardó como \"{0}\" en la carpeta \"{1}\". Para volver a abrirla, haz doble clic en ese archivo. Se guarda y bloquea tras 5 minutos sin uso.", "「{1}」フォルダーに「{0}」として保存しました。もう一度開くときはこのファイルをダブルクリックしてください。5 分間触らないと保存してロックします。", "已保存到“{1}”文件夹，文件名为“{0}”。再次打开时请双击这个文件。5 分钟不操作就会保存并锁定。"),
    ("sec.lockedWhatNow", "원래 프로그램으로 보려면 잠금을 풀어야 해요. 풀면 옆에 암호 없는 파일이 하나 생기고, 잠긴 파일은 그대로 남아요.", "To view it in its usual program, it has to be unlocked. That puts a copy without a password next to it; the locked file stays as is.", "Para verlo con su programa habitual hay que desbloquearlo. Eso deja al lado una copia sin contraseña; el archivo bloqueado se queda igual.", "元のプログラムで見るにはロックを解く必要があります。解くと隣にパスワードのないファイルが 1 つでき、ロックしたファイルはそのまま残ります。", "要用原来的程序查看，需要先解锁。解锁后旁边会多一个没有密码的文件，加密文件仍保留。"),
]
