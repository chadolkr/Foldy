//! Named Pipe 서버. 연결마다 읽기 루프 하나와 쓰기 작업 하나를 두어,
//! 검색 배치를 보내는 동안에도 다음 요청(새 Search, CancelSearch, Status)을 받는다.

use crate::acl::PipeSecurity;
use crate::query;
use crate::state::Indexer;
use foldy_proto::*;
use std::io;
use std::sync::Arc;
use std::sync::atomic::{AtomicBool, Ordering};
use tokio::io::{AsyncReadExt, AsyncWriteExt};
use tokio::net::windows::named_pipe::{NamedPipeServer, ServerOptions};
use tokio::sync::mpsc;

fn create(name: &str, first: bool, sec: &mut PipeSecurity) -> io::Result<NamedPipeServer> {
    let mut opts = ServerOptions::new();
    opts.first_pipe_instance(first).reject_remote_clients(true);
    // SAFETY: sec가 가리키는 SECURITY_ATTRIBUTES는 서버 루프가 끝날 때까지 살아 있다.
    unsafe { opts.create_with_security_attributes_raw(name, sec.as_ptr()) }
}

/// 서버 루프. `once`면 첫 클라이언트를 처리한 뒤 끝낸다. `shutdown`이 끝나면 멈춘다.
pub async fn run(pipe: &str, once: bool, indexer: Arc<Indexer>, shutdown: impl std::future::Future<Output = ()>) -> io::Result<()> {
    // 보안 설명자를 못 만들면 파이프를 열지 않는다 (기본 보안으로 열면 SYSTEM 서비스에 누가 붙을지 알 수 없다)
    let mut sec = PipeSecurity::interactive_only().map_err(|e| {
        eprintln!("pipe ACL 설정 실패, 파이프를 열지 않습니다: {e}");
        e
    })?;
    let mut server = create(pipe, true, &mut sec)?;
    println!("READY {pipe}");
    tokio::pin!(shutdown);
    loop {
        tokio::select! {
            r = server.connect() => r?,
            _ = &mut shutdown => {
                indexer.shutdown.store(true, Ordering::Relaxed);
                return Ok(());
            }
        }
        let client = server;
        // 다음 클라이언트를 받을 인스턴스를 먼저 만든다.
        server = create(pipe, false, &mut sec)?;
        let ix = indexer.clone();
        let handle = tokio::spawn(async move {
            if let Err(e) = serve(client, ix).await {
                eprintln!("client error: {e}");
            }
        });
        if once {
            let _ = handle.await;
            return Ok(());
        }
    }
}

async fn serve(pipe: NamedPipeServer, ix: Arc<Indexer>) -> io::Result<()> {
    // 업데이트 후 Foldy를 다시 띄울 세션 (클라이언트가 보낸 값보다 이것을 믿는다)
    let session = {
        use std::os::windows::io::AsRawHandle;
        crate::update::win::pipe_client_session(pipe.as_raw_handle())
    };
    // 검색 결과는 연결한 사용자 기준으로 거른다 (다른 사용자 프로필은 보이지 않게)
    let vis = {
        use std::os::windows::io::AsRawHandle;
        Arc::new(crate::visibility::for_pipe_client(pipe.as_raw_handle()))
    };
    let (mut rd, mut wr) = tokio::io::split(pipe);
    let (tx, mut rx) = mpsc::channel::<Vec<u8>>(64);
    let writer = tokio::spawn(async move {
        while let Some(f) = rx.recv().await {
            if wr.write_all(&f).await.is_err() {
                break;
            }
        }
        let _ = wr.flush().await;
    });

    let mut current: Option<(u32, Arc<AtomicBool>)> = None;
    let mut header = [0u8; HEADER_LEN];
    let result = loop {
        match rd.read_exact(&mut header).await {
            Ok(_) => {}
            Err(e) if e.kind() == io::ErrorKind::UnexpectedEof => break Ok(()),
            Err(e) => break Err(e),
        }
        let (len, ty) = match decode_header(&header) {
            Ok(x) => x,
            Err(e) => {
                // 헤더가 깨졌으면 스트림 동기화가 불가능하므로 오류를 보내고 연결을 끊는다.
                let _ = tx.send(err(1, &e.to_string())).await;
                break Ok(());
            }
        };
        let mut body = vec![0u8; len];
        if let Err(e) = rd.read_exact(&mut body).await {
            break Err(e);
        }
        match ty {
            MsgType::Search => match decode_body::<Search>(&body) {
                Ok(req) => {
                    // 새 검색이 오면 이전 검색은 자동 취소
                    if let Some((_, flag)) = current.take() {
                        flag.store(true, Ordering::Relaxed);
                    }
                    let flag = Arc::new(AtomicBool::new(false));
                    current = Some((req.search_id, flag.clone()));
                    let (ix2, tx2, vis2) = (ix.clone(), tx.clone(), vis.clone());
                    tokio::task::spawn_blocking(move || {
                        query::search(&ix2, &req, &vis2, &flag, |b| tx2.blocking_send(encode(MsgType::SearchBatch, &b)).is_ok());
                    });
                }
                Err(e) => {
                    if tx.send(err(1, &e.to_string())).await.is_err() {
                        break Ok(());
                    }
                }
            },
            MsgType::CancelSearch => {
                if let Ok(c) = decode_body::<CancelSearch>(&body)
                    && let Some((id, flag)) = &current
                    && *id == c.search_id
                {
                    flag.store(true, Ordering::Relaxed);
                }
            }
            MsgType::FolderSizes => {
                let reply = match decode_body::<FolderSizes>(&body) {
                    Ok(req) => {
                        let (ix2, vis2) = (ix.clone(), vis.clone());
                        match tokio::task::spawn_blocking(move || query::folder_sizes(&ix2, &req.path, &vis2)).await {
                            Ok(r) => encode(MsgType::FolderSizesReply, &r),
                            Err(e) => err(4, &e.to_string()),
                        }
                    }
                    Err(e) => err(1, &e.to_string()),
                };
                if tx.send(reply).await.is_err() {
                    break Ok(());
                }
            }
            MsgType::BuildAnalysis => {
                // 분석은 백그라운드 스레드에서 돌고, 응답은 시작 여부만 바로 보낸다.
                let reply = match decode_body::<BuildAnalysis>(&body) {
                    Ok(req) => {
                        crate::analysis_job::allow_reader(&ix, vis.user_sid());
                        encode(MsgType::BuildAnalysisReply, &crate::analysis_job::start_build(&ix, &req.volume, &crate::analysis_job::Requester::from_visibility(&vis)))
                    }
                    Err(e) => err(1, &e.to_string()),
                };
                if tx.send(reply).await.is_err() {
                    break Ok(());
                }
            }
            MsgType::AnalysisInfo => {
                let reply = match decode_body::<AnalysisInfo>(&body) {
                    Ok(req) => {
                        let (ix2, who) = (ix.clone(), crate::analysis_job::Requester::from_visibility(&vis));
                        match tokio::task::spawn_blocking(move || {
                            crate::analysis_job::allow_reader(&ix2, who.sid.as_deref());
                            crate::analysis_job::info(&ix2, &req.volume, &who)
                        })
                        .await
                        {
                            Ok(r) => encode(MsgType::AnalysisInfoReply, &r),
                            Err(e) => err(4, &e.to_string()),
                        }
                    }
                    Err(e) => err(1, &e.to_string()),
                };
                if tx.send(reply).await.is_err() {
                    break Ok(());
                }
            }
            t if crate::update::is_update_msg(t) => {
                if tx.send(crate::update::handle_pipe(t, &body, session)).await.is_err() {
                    break Ok(());
                }
            }
            other => {
                if tx.send(handle_simple(other, &body, &ix)).await.is_err() {
                    break Ok(());
                }
            }
        }
    };
    if let Some((_, flag)) = current {
        flag.store(true, Ordering::Relaxed);
    }
    drop(tx);
    let _ = writer.await;
    result
}

fn handle_simple(ty: MsgType, body: &[u8], ix: &Indexer) -> Vec<u8> {
    match ty {
        MsgType::Hello => match decode_body::<Hello>(body) {
            Ok(h) if h.protocol == PROTOCOL_VERSION => encode(
                MsgType::HelloReply,
                &HelloReply { protocol: PROTOCOL_VERSION, service_version: env!("CARGO_PKG_VERSION").to_string(), volumes: crate::volumes() },
            ),
            Ok(h) => err(2, &format!("protocol mismatch: client {} != service {}", h.protocol, PROTOCOL_VERSION)),
            Err(e) => err(1, &e.to_string()),
        },
        MsgType::Status => encode(MsgType::StatusReply, &ix.status()),
        other => err(3, &format!("unsupported request: {other:?}")),
    }
}

fn err(code: u32, message: &str) -> Vec<u8> {
    encode(MsgType::Error, &ErrorReply { code, message: message.to_string() })
}
