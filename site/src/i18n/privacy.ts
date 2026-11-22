// 개인정보 처리방침 페이지(/privacy/)의 글. Microsoft Store 제출에 쓰는 주소다.
// 사실만 쓴다: 바뀌면 코드(src/Foldy.Reporting, ShellWindow 업데이트, Markdown 원격 그림, SFTP)와 PRIVACY.md도 같이 본다.
// 날짜(updated)를 고친다. 언어마다 모양이 같아야 한다 (ko가 기준).

export type PrivacySection = { h: string; p?: string[]; items?: { t: string; d: string }[] };
export type PrivacyCopy = {
  title: string;
  description: string;
  updatedLabel: string;
  back: string;
  intro: string;
  sections: PrivacySection[];
};

export const privacyUpdated = '2026-11-22';

const ko: PrivacyCopy = {
  title: 'Foldy 개인정보 처리방침',
  description: 'Foldy는 개인정보를 모으지 않아요. 사용자가 켜거나 직접 누를 때만 인터넷을 써요.',
  updatedLabel: '마지막 수정',
  back: 'Foldy 첫 화면으로',
  intro:
    'Foldy(Microsoft Store 이름: Foldy File Manager)는 개인정보를 모으지 않아요. 계정도, 광고도, 사용 통계도, 추적도 없어요. 아래에 적은 경우에만, 사용자가 켜거나 직접 누를 때만 인터넷을 써요.',
  sections: [
    {
      h: '밖으로 보내지 않는 것',
      p: ['파일 이름과 내용, 검색어, 폴더 구조, 사용 기록은 이 컴퓨터 밖으로 나가지 않아요. 빠른 검색에 친 글자도 인터넷 검색에 쓰지 않아요.'],
    },
    {
      h: '인터넷을 쓰는 경우',
      items: [
        {
          t: '오류 보고 (기본으로 꺼짐)',
          d: 'Foldy가 갑자기 멈추면, 다음에 켤 때 보낼지 먼저 물어봐요. 허락할 때만 앱 버전, Windows 버전, 오류 종류, 오류가 난 코드 위치를 보내요. 보내기 전에 파일 경로, 사용자 이름, 컴퓨터 이름, 메일 주소, 서버 주소를 지워요. 보고는 Foldy 오류 중계 서버(Cloudflare)를 거쳐 GitHub의 공개 이슈로 올라가요. 중계 서버는 너무 많은 요청을 막는 데에만 IP 주소를 쓰고, 이슈에는 남기지 않아요. 설정 → 오류 보고에서 끌 수 있어요.',
        },
        {
          t: '버그 신고 버튼',
          d: '브라우저로 GitHub의 이슈 쓰기 페이지를 열어요. 앱 버전, Windows 버전, 최근 기록(경로와 이름을 지운 것)이 미리 채워져 있고, 사용자가 읽어 보고 직접 제출할 때만 올라가요. 이때는 GitHub의 개인정보 정책을 따라요.',
        },
        {
          t: '업데이트 확인 (홈페이지 설치본만)',
          d: 'GitHub에 새 버전이 있는지 묻고 설치 파일을 받아요. 사용자 정보는 보내지 않고, 설정에서 끌 수 있어요. Microsoft Store 판은 Foldy가 직접 확인하지 않고 Microsoft Store가 업데이트해요.',
        },
        {
          t: '사용자가 연결한 서버',
          d: 'SFTP·SSH 서버와 Git 원격 저장소에는 사용자가 연결할 때만, 요청한 파일과 명령만 주고받아요. 저장한 비밀번호는 Windows 암호화로 이 컴퓨터에만 둬요.',
        },
        {
          t: '메모(.md) 속 인터넷 그림',
          d: '기본으로 막혀 있어요. 사용자가 그림 보기를 누를 때만 그 그림이 있는 사이트에서 받아요.',
        },
      ],
    },
    {
      h: '이 컴퓨터에만 두는 것',
      p: [
        '설정, 기록(로그), 파일 목록 색인, 아직 보내지 않은 오류 보고는 이 컴퓨터의 사용자 폴더(%LOCALAPPDATA%\\Foldy)에만 둬요. Microsoft Store 판은 Windows가 Foldy 전용 폴더로 따로 관리해요.',
      ],
    },
    {
      h: 'AI 연결',
      p: [
        'AI 연결은 기본으로 꺼져 있어요. 켜면 사용자가 고른 AI 앱(예: Claude Desktop)이 이 컴퓨터 안에서 Foldy에 파일 이름, 폴더 내용, 폴더 설명을 물을 수 있어요. 읽기 전용이고, 인터넷에 열지 않아요. AI 앱이 받은 내용을 어떻게 다루는지는 그 앱의 개인정보 정책을 따라요.',
      ],
    },
    {
      h: '어린이',
      p: ['Foldy는 나이와 상관없이 누구의 개인정보도 모으지 않아요.'],
    },
    {
      h: '지우기',
      p: [
        'Microsoft Store 판은 제거하면 Windows가 Foldy의 데이터도 함께 지워요. 홈페이지 설치본은 제거한 뒤 %LOCALAPPDATA%\\Foldy 폴더를 지우면 돼요.',
        '이미 올라간 오류 보고를 지우고 싶으면 아래 문의처로 알려 주세요.',
      ],
    },
    {
      h: '바뀔 때',
      p: ['이 내용이 바뀌면 이 페이지를 고치고 위의 날짜를 바꿔요.'],
    },
    {
      h: '문의',
      p: ['GitHub 이슈로 알려 주세요: https://github.com/chadolkr/Foldy/issues'],
    },
  ],
};

const en: PrivacyCopy = {
  title: 'Foldy privacy policy',
  description: 'Foldy does not collect personal data. It only uses the internet when you turn something on or click it yourself.',
  updatedLabel: 'Last updated',
  back: 'Back to Foldy',
  intro:
    'Foldy (Microsoft Store name: Foldy File Manager) does not collect personal data. There are no accounts, ads, analytics or tracking. Foldy only uses the internet in the cases below, and only when you turn them on or click them yourself.',
  sections: [
    {
      h: 'What never leaves your PC',
      p: ['File names and contents, search terms, folder structure and usage history stay on this computer. What you type in quick search is never sent to a web search.'],
    },
    {
      h: 'When Foldy uses the internet',
      items: [
        {
          t: 'Crash reports (off by default)',
          d: 'If Foldy crashes, it asks the next time it starts whether to send a report. Only if you agree, it sends the app version, Windows version, error type and the code location of the error. Before sending, it removes file paths, user names, computer names, e-mail addresses and server addresses. Reports go through the Foldy crash relay (Cloudflare) to a public GitHub issue. The relay uses your IP address only to block too many requests and does not put it in the issue. Turn it off in Settings → Crash reports.',
        },
        {
          t: 'Report a bug button',
          d: 'Opens a new GitHub issue page in your browser, pre-filled with the app version, Windows version and recent log lines (with paths and names removed). Nothing is posted unless you read it and submit it yourself. GitHub’s privacy policy applies there.',
        },
        {
          t: 'Update check (website installer only)',
          d: 'Asks GitHub whether a new version exists and downloads the installer. No user data is sent, and you can turn it off in Settings. The Microsoft Store edition does not check by itself; Microsoft Store updates it.',
        },
        {
          t: 'Servers you connect to',
          d: 'Foldy connects to SFTP and SSH servers and Git remotes only when you connect, and only sends the files and commands you ask for. Saved passwords are encrypted with Windows data protection and stay on this computer.',
        },
        {
          t: 'Web images in notes (.md)',
          d: 'Blocked by default. Foldy downloads an image from its website only when you click to show images.',
        },
      ],
    },
    {
      h: 'What stays on this computer',
      p: [
        'Settings, logs, the file index and crash reports not yet sent are kept only in your user folder on this computer (%LOCALAPPDATA%\\Foldy). In the Microsoft Store edition, Windows keeps them in a folder that belongs to Foldy.',
      ],
    },
    {
      h: 'Connecting AI',
      p: [
        'AI connection is off by default. When you turn it on, an AI app you choose (for example Claude Desktop) can ask Foldy, inside this computer, for file names, folder contents and folder notes. It is read-only and is not opened to the internet. How the AI app handles what it receives is covered by that app’s privacy policy.',
      ],
    },
    {
      h: 'Children',
      p: ['Foldy does not collect personal data from anyone, of any age.'],
    },
    {
      h: 'Deleting your data',
      p: [
        'When you uninstall the Microsoft Store edition, Windows removes Foldy’s data too. For the website installer, uninstall it and then delete the %LOCALAPPDATA%\\Foldy folder.',
        'If you want a crash report that was already posted removed, contact us below.',
      ],
    },
    {
      h: 'Changes',
      p: ['If this policy changes, this page is updated and the date above changes.'],
    },
    {
      h: 'Contact',
      p: ['Open an issue on GitHub: https://github.com/chadolkr/Foldy/issues'],
    },
  ],
};

const es: PrivacyCopy = {
  title: 'Política de privacidad de Foldy',
  description: 'Foldy no recopila datos personales. Solo usa internet cuando tú activas algo o haces clic.',
  updatedLabel: 'Última actualización',
  back: 'Volver a Foldy',
  intro:
    'Foldy (nombre en Microsoft Store: Foldy File Manager) no recopila datos personales. No hay cuentas, anuncios, estadísticas de uso ni rastreo. Foldy solo usa internet en los casos de abajo, y solo cuando tú los activas o haces clic.',
  sections: [
    {
      h: 'Lo que nunca sale de tu PC',
      p: ['Los nombres y el contenido de los archivos, lo que buscas, la estructura de carpetas y el historial de uso se quedan en este equipo. Lo que escribes en la búsqueda rápida nunca se envía a un buscador web.'],
    },
    {
      h: 'Cuándo usa internet Foldy',
      items: [
        {
          t: 'Informes de errores (desactivados por defecto)',
          d: 'Si Foldy se cierra por un error, la próxima vez que lo abras te pregunta si quieres enviar un informe. Solo si aceptas, envía la versión de la app, la versión de Windows, el tipo de error y el lugar del código donde ocurrió. Antes de enviarlo borra rutas de archivos, nombres de usuario, nombres de equipo, correos y direcciones de servidores. El informe pasa por el relé de errores de Foldy (Cloudflare) y se publica como una incidencia pública en GitHub. El relé usa tu dirección IP solo para frenar demasiadas peticiones y no la incluye en la incidencia. Se desactiva en Configuración → Informes de errores.',
        },
        {
          t: 'Botón Informar de un error',
          d: 'Abre en tu navegador una página nueva de incidencia en GitHub, ya rellenada con la versión de la app, la versión de Windows y las últimas líneas del registro (sin rutas ni nombres). No se publica nada hasta que tú lo revisas y lo envías. Ahí se aplica la política de privacidad de GitHub.',
        },
        {
          t: 'Buscar actualizaciones (solo el instalador de la web)',
          d: 'Pregunta a GitHub si hay una versión nueva y descarga el instalador. No envía datos tuyos y se puede desactivar en Configuración. La edición de Microsoft Store no busca actualizaciones por su cuenta; la actualiza Microsoft Store.',
        },
        {
          t: 'Servidores a los que te conectas',
          d: 'Foldy se conecta a servidores SFTP y SSH y a remotos de Git solo cuando tú te conectas, y solo envía los archivos y órdenes que pides. Las contraseñas guardadas se cifran con la protección de datos de Windows y se quedan en este equipo.',
        },
        {
          t: 'Imágenes web en notas (.md)',
          d: 'Bloqueadas por defecto. Foldy descarga una imagen de su sitio web solo cuando haces clic para mostrar imágenes.',
        },
      ],
    },
    {
      h: 'Lo que se queda en este equipo',
      p: [
        'La configuración, los registros, el índice de archivos y los informes de errores aún no enviados se guardan solo en tu carpeta de usuario de este equipo (%LOCALAPPDATA%\\Foldy). En la edición de Microsoft Store, Windows los guarda en una carpeta propia de Foldy.',
      ],
    },
    {
      h: 'Conectar una IA',
      p: [
        'La conexión con IA viene desactivada. Si la activas, la app de IA que elijas (por ejemplo Claude Desktop) puede pedir a Foldy, dentro de este equipo, nombres de archivos, el contenido de carpetas y sus notas. Es de solo lectura y no se abre a internet. Lo que la app de IA haga con lo que recibe se rige por la política de privacidad de esa app.',
      ],
    },
    {
      h: 'Menores',
      p: ['Foldy no recopila datos personales de nadie, tenga la edad que tenga.'],
    },
    {
      h: 'Borrar tus datos',
      p: [
        'Al desinstalar la edición de Microsoft Store, Windows borra también los datos de Foldy. Con el instalador de la web, desinstálalo y borra la carpeta %LOCALAPPDATA%\\Foldy.',
        'Si quieres que se retire un informe de errores ya publicado, escríbenos abajo.',
      ],
    },
    {
      h: 'Cambios',
      p: ['Si esta política cambia, se actualiza esta página y la fecha de arriba.'],
    },
    {
      h: 'Contacto',
      p: ['Abre una incidencia en GitHub: https://github.com/chadolkr/Foldy/issues'],
    },
  ],
};

const ja: PrivacyCopy = {
  title: 'Foldy プライバシー ポリシー',
  description: 'Foldy は個人情報を集めません。インターネットを使うのは、あなたがオンにしたときや自分で押したときだけです。',
  updatedLabel: '最終更新',
  back: 'Foldy のトップへ',
  intro:
    'Foldy (Microsoft Store での名前: Foldy File Manager) は個人情報を集めません。アカウント、広告、利用統計、追跡はありません。インターネットを使うのは下に書いた場合だけで、それもあなたがオンにしたときや自分で押したときだけです。',
  sections: [
    {
      h: 'PC の外に出ないもの',
      p: ['ファイル名と中身、検索語、フォルダー構成、利用履歴はこのコンピューターの外に出ません。クイック検索に入力した文字を Web 検索に使うこともありません。'],
    },
    {
      h: 'インターネットを使う場合',
      items: [
        {
          t: 'エラー報告 (初期状態はオフ)',
          d: 'Foldy が突然止まった場合、次に起動したときに送るかどうかを先に確認します。許可したときだけ、アプリのバージョン、Windows のバージョン、エラーの種類、エラーが起きたコードの場所を送ります。送る前に、ファイルのパス、ユーザー名、コンピューター名、メール アドレス、サーバー アドレスを消します。報告は Foldy のエラー中継サーバー (Cloudflare) を通って GitHub の公開 Issue になります。中継サーバーは多すぎるリクエストを防ぐためだけに IP アドレスを使い、Issue には残しません。設定 → エラー報告 でオフにできます。',
        },
        {
          t: 'バグ報告ボタン',
          d: 'ブラウザーで GitHub の Issue 作成ページを開きます。アプリのバージョン、Windows のバージョン、最近のログ (パスや名前を消したもの) があらかじめ入っていて、あなたが読んで自分で送信したときだけ投稿されます。その場合は GitHub のプライバシー ポリシーに従います。',
        },
        {
          t: '更新の確認 (Web サイトのインストーラー版のみ)',
          d: 'GitHub に新しいバージョンがあるかを確認し、インストーラーをダウンロードします。ユーザー情報は送らず、設定でオフにできます。Microsoft Store 版は Foldy 自身では確認せず、Microsoft Store が更新します。',
        },
        {
          t: 'あなたが接続したサーバー',
          d: 'SFTP・SSH サーバーと Git のリモートには、あなたが接続したときだけ、頼んだファイルとコマンドだけをやり取りします。保存したパスワードは Windows のデータ保護で暗号化し、このコンピューターだけに置きます。',
        },
        {
          t: 'メモ (.md) の中の Web 画像',
          d: '初期状態ではブロックされています。画像を表示するボタンを押したときだけ、その画像があるサイトから受け取ります。',
        },
      ],
    },
    {
      h: 'このコンピューターだけに置くもの',
      p: [
        '設定、ログ、ファイルのインデックス、まだ送っていないエラー報告は、このコンピューターのユーザー フォルダー (%LOCALAPPDATA%\\Foldy) だけに置きます。Microsoft Store 版では、Windows が Foldy 専用のフォルダーで管理します。',
      ],
    },
    {
      h: 'AI との連携',
      p: [
        'AI との連携は初期状態でオフです。オンにすると、あなたが選んだ AI アプリ (例: Claude Desktop) が、このコンピューターの中で Foldy にファイル名、フォルダーの中身、フォルダーの説明を尋ねられます。読み取り専用で、インターネットには公開しません。AI アプリが受け取った内容をどう扱うかは、そのアプリのプライバシー ポリシーに従います。',
      ],
    },
    {
      h: 'お子さま',
      p: ['Foldy は年齢に関係なく、誰の個人情報も集めません。'],
    },
    {
      h: 'データの削除',
      p: [
        'Microsoft Store 版はアンインストールすると、Windows が Foldy のデータも一緒に削除します。Web サイトのインストーラー版は、アンインストールしたあと %LOCALAPPDATA%\\Foldy フォルダーを削除してください。',
        'すでに投稿されたエラー報告を消したい場合は、下の連絡先にお知らせください。',
      ],
    },
    {
      h: '変更について',
      p: ['この内容が変わるときは、このページを更新し、上の日付を変えます。'],
    },
    {
      h: 'お問い合わせ',
      p: ['GitHub の Issue でお知らせください: https://github.com/chadolkr/Foldy/issues'],
    },
  ],
};

const zhCN: PrivacyCopy = {
  title: 'Foldy 隐私政策',
  description: 'Foldy 不收集个人信息。只有在你开启某项功能或亲自点击时才会联网。',
  updatedLabel: '最后更新',
  back: '返回 Foldy 首页',
  intro:
    'Foldy（Microsoft Store 名称：Foldy File Manager）不收集个人信息。没有账号、没有广告、没有使用统计，也没有跟踪。Foldy 只在下面列出的情况下联网，而且只在你开启或亲自点击时才会联网。',
  sections: [
    {
      h: '不会离开电脑的内容',
      p: ['文件名和文件内容、搜索词、文件夹结构和使用记录都只留在这台电脑上。你在快速搜索中输入的文字也不会用于网络搜索。'],
    },
    {
      h: '会联网的情况',
      items: [
        {
          t: '错误报告（默认关闭）',
          d: '如果 Foldy 意外退出，下次启动时会先问你是否发送报告。只有你同意时，才会发送应用版本、Windows 版本、错误类型和出错的代码位置。发送前会删除文件路径、用户名、计算机名、邮箱地址和服务器地址。报告经由 Foldy 错误中转服务器（Cloudflare）发布为 GitHub 上的公开 Issue。中转服务器只用 IP 地址来阻止过多的请求，不会把它写进 Issue。可以在 设置 → 错误报告 中关闭。',
        },
        {
          t: '报告问题按钮',
          d: '在浏览器中打开 GitHub 的新建 Issue 页面，并预先填好应用版本、Windows 版本和最近的日志（已删除路径和名称）。只有你看过并亲自提交后才会发布。此时适用 GitHub 的隐私政策。',
        },
        {
          t: '检查更新（仅网站安装版）',
          d: '向 GitHub 询问是否有新版本并下载安装程序。不发送任何用户信息，可以在设置中关闭。Microsoft Store 版不会自己检查，由 Microsoft Store 负责更新。',
        },
        {
          t: '你连接的服务器',
          d: '只有在你连接时，Foldy 才会连接 SFTP、SSH 服务器和 Git 远程仓库，并且只传输你要求的文件和命令。保存的密码使用 Windows 数据保护加密，只保存在这台电脑上。',
        },
        {
          t: '笔记（.md）中的网络图片',
          d: '默认被阻止。只有在你点击显示图片时，才会从图片所在的网站下载。',
        },
      ],
    },
    {
      h: '只保存在这台电脑上的内容',
      p: [
        '设置、日志、文件索引和尚未发送的错误报告只保存在这台电脑的用户文件夹（%LOCALAPPDATA%\\Foldy）中。Microsoft Store 版由 Windows 在 Foldy 专用的文件夹中管理。',
      ],
    },
    {
      h: '连接 AI',
      p: [
        '连接 AI 默认关闭。开启后，你选择的 AI 应用（例如 Claude Desktop）可以在这台电脑内向 Foldy 查询文件名、文件夹内容和文件夹说明。它是只读的，不会对互联网开放。AI 应用如何处理收到的内容，以该应用的隐私政策为准。',
      ],
    },
    {
      h: '儿童',
      p: ['无论年龄大小，Foldy 都不收集任何人的个人信息。'],
    },
    {
      h: '删除数据',
      p: [
        '卸载 Microsoft Store 版时，Windows 会一并删除 Foldy 的数据。网站安装版请在卸载后删除 %LOCALAPPDATA%\\Foldy 文件夹。',
        '如果想删除已经发布的错误报告，请通过下面的方式联系我们。',
      ],
    },
    {
      h: '变更',
      p: ['如果本政策有变化，会更新此页面并修改上面的日期。'],
    },
    {
      h: '联系方式',
      p: ['请在 GitHub 上提交 Issue：https://github.com/chadolkr/Foldy/issues'],
    },
  ],
};

export const privacy: Record<string, PrivacyCopy> = { ko, en, es, ja, 'zh-cn': zhCN };
