# -*- coding: utf-8 -*-
"""색인 없는 드라이브: 지금 색인하기, 마지막으로 모은 때 (사용자 2026-11-23). (키, ko, en, es, ja, zh-Hans)"""

INDEX = [
    ("shell.indexNow", "지금 색인하기", "Index now", "Indexar ahora", "今すぐインデックス", "立即索引"),
    ("sw.c.indexNow", "지금 색인하기 (네트워크·USB·Google Drive 등)", "Index now (network, USB, Google Drive, etc.)", "Indexar ahora (red, USB, Google Drive, etc.)", "今すぐインデックス (ネットワーク・USB・Google ドライブなど)", "立即索引（网络、USB、Google 云端硬盘等）"),
    ("sw.drv.crawledAt", "색인 완료 · {0:N0}개 · {1}", "Indexed · {0:N0} · {1}", "Indexado · {0:N0} · {1}", "インデックス完了 · {0:N0} 件 · {1}", "索引完成 · {0:N0} 个 · {1}"),
    # 다 읽었는데 하나도 못 읽었거나 드라이브를 읽지 못함 (사용자 2026-11-23: K: 드라이브 "색인 완료 · 0개")
    ("sw.drv.unreadable", "색인 못 함 · 이 드라이브를 읽지 못했어요", "Not indexed · couldn't read this drive", "Sin indexar · no se pudo leer esta unidad", "インデックスできません · このドライブを読めませんでした", "无法索引 · 读取不了这个驱动器"),
    ("sw.idxnow.started", "{0} 색인을 시작했어요. 뒤에서 살살 모으니 다른 일을 하셔도 돼요.", "Started indexing {0}. It runs gently in the background, so you can keep working.", "Se empezó a indexar {0}. Va despacio en segundo plano, puedes seguir trabajando.", "{0} のインデックスを始めました。裏でゆっくり集めるので、ほかの作業を続けられます。", "已开始索引 {0}。会在后台慢慢进行，你可以继续做别的事。"),
    ("sw.idxnow.queued", "{0} 색인을 예약했어요. 지금 {1}을(를) 모으는 중이라 그다음에 시작해요.", "Indexing {0} is next. It starts after {1}, which is being gathered now.", "La indexación de {0} es la siguiente. Empieza después de {1}, que se está recopilando ahora.", "{0} のインデックスを予約しました。今 {1} を集めているので、その次に始めます。", "已预约索引 {0}。目前正在收集 {1}，完成后开始。"),
    ("sw.idxnow.already", "{0}은(는) 이미 색인하는 중이거나 차례를 기다리고 있어요.", "{0} is already being indexed or waiting for its turn.", "{0} ya se está indexando o está esperando su turno.", "{0} はすでにインデックス中か、順番待ちです。", "{0} 已在索引中或正在排队。"),
    ("sw.idxnow.nothing", "따로 색인할 드라이브가 없어요. C:처럼 색인 서비스가 맡은 드라이브는 늘 최신이에요.", "There are no drives to index separately. Drives handled by the index service, such as C:, are always up to date.", "No hay unidades que indexar aparte. Las del servicio de índice, como C:, siempre están al día.", "別にインデックスするドライブはありません。C: のようにインデックス サービスが受け持つドライブは常に最新です。", "没有需要单独索引的驱动器。像 C: 这样由索引服务负责的驱动器始终是最新的。"),
    ("sw.idxnow.off", "설정 → 일반 → 빠른 찾기에서 '색인 없는 드라이브도 천천히 모으기'를 켜면 색인할 수 있어요.", "Turn on 'Gently gather drives without an index' in Settings → General → Quick find to index drives.", "Activa 'Recopilar poco a poco las unidades sin índice' en Configuración → General → Búsqueda rápida para indexar.", "設定 → 全般 → クイック検索で「インデックスのないドライブもゆっくり集める」をオンにするとインデックスできます。", "在 设置 → 常规 → 快速查找 中打开“慢慢收集没有索引的驱动器”后即可索引。"),
    ("qi.index.now", "색인 시작 색인 다시 지금 색인 인덱스 새로 고침 목록 모으기 재색인", "index now reindex start indexing refresh index rebuild", "indexar ahora reindexar iniciar indexación actualizar índice", "インデックス 今すぐ 再インデックス 開始 更新 index", "立即索引 重新索引 开始索引 刷新索引 index"),
    # 색인 서비스가 다시 시작돼 연결이 잠깐 끊김 (1.9.1, 벤치마크 중 발견): 스스로 다시 연결하고, 안내는 저절로 사라진다
    ("sw.idx.reconnecting", "검색: 색인 서비스에 다시 연결하는 중…", "Search: reconnecting to the index service…", "Búsqueda: reconectando con el servicio de índice…", "検索: インデックス サービスに再接続しています…", "搜索：正在重新连接索引服务…"),
    ("sw.idx.reconnectingTip", "색인 서비스가 다시 시작되었어요. 곧 저절로 다시 연결돼요. 그동안은 모아 둔 목록과 지금 폴더에서 찾아요.", "The index service restarted. It will reconnect by itself shortly. Until then, search uses the gathered lists and the current folder.", "El servicio de índice se reinició. Se volverá a conectar solo en breve. Mientras tanto, la búsqueda usa las listas recopiladas y la carpeta actual.", "インデックス サービスが再起動しました。まもなく自動で再接続します。それまでは集めておいた一覧と今のフォルダーから探します。", "索引服务已重新启动，很快会自动重新连接。在此之前，搜索会使用已收集的列表和当前文件夹。"),
    ("sw.idx.reconnectingNotice", "색인 서비스가 다시 시작되어 잠깐 연결이 끊겼어요. 저절로 다시 연결할게요.", "The index service restarted, so the connection dropped for a moment. Reconnecting by itself.", "El servicio de índice se reinició y la conexión se cortó un momento. Se reconecta solo.", "インデックス サービスが再起動したため、接続が一時的に切れました。自動で再接続します。", "索引服务已重新启动，连接暂时中断。正在自动重新连接。"),
    ("sw.idx.reconnected", "색인 서비스에 다시 연결했어요. 이제 모든 드라이브에서 다시 찾아요.", "Reconnected to the index service. Search covers all drives again.", "Reconectado con el servicio de índice. La búsqueda vuelve a cubrir todas las unidades.", "インデックス サービスに再接続しました。またすべてのドライブから探せます。", "已重新连接索引服务。搜索又能覆盖所有驱动器了。"),
]
