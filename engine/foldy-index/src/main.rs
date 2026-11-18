//! foldy-index: 색인 서비스.
//!
//! M0 범위: Named Pipe 서버로 Hello / Status 에 응답해 C# 클라이언트와의 경계를 검증한다.
//! Windows 서비스 등록, 파이프 ACL(Interactive 사용자만), MFT 색인은 M2에서 추가한다.
//!
//! 사용법: `foldy-index [--pipe <이름>] [--once]`
//!   --pipe  파이프 이름 (기본 \\.\pipe\Foldy.Indexer.v1, 테스트는 임의 이름 사용)
//!   --once  클라이언트 하나를 처리한 뒤 종료 (통합 테스트용)

use foldy_proto::*;
use std::io;
use tokio::io::{AsyncReadExt, AsyncWriteExt};
use tokio::net::windows::named_pipe::{NamedPipeServer, ServerOptions};

struct Args {
    pipe: String,
    once: bool,
}

fn parse_args() -> Args {
    let mut a = Args { pipe: PIPE_NAME.to_string(), once: false };
    let mut it = std::env::args().skip(1);
    while let Some(x) = it.next() {
        match x.as_str() {
            "--pipe" => {
                if let Some(v) = it.next() {
                    a.pipe = if v.starts_with(r"\\.\pipe\") { v } else { format!(r"\\.\pipe\{v}") };
                }
            }
            "--once" => a.once = true,
            _ => eprintln!("unknown argument: {x}"),
        }
    }
    a
}

#[tokio::main(flavor = "multi_thread", worker_threads = 2)]
async fn main() -> io::Result<()> {
    let args = parse_args();
    let mut server = ServerOptions::new().first_pipe_instance(true).create(&args.pipe)?;
    println!("READY {}", args.pipe);

    loop {
        server.connect().await?;
        let client = server;
        // 다음 클라이언트를 받을 인스턴스를 먼저 만든다.
        server = ServerOptions::new().create(&args.pipe)?;
        let handle = tokio::spawn(async move {
            if let Err(e) = serve(client).await {
                eprintln!("client error: {e}");
            }
        });
        if args.once {
            let _ = handle.await;
            return Ok(());
        }
    }
}

async fn serve(mut pipe: NamedPipeServer) -> io::Result<()> {
    let mut header = [0u8; HEADER_LEN];
    loop {
        match pipe.read_exact(&mut header).await {
            Ok(_) => {}
            Err(e) if e.kind() == io::ErrorKind::UnexpectedEof => return Ok(()),
            Err(e) => return Err(e),
        }
        let reply = match decode_header(&header) {
            Ok((len, ty)) => {
                let mut body = vec![0u8; len];
                pipe.read_exact(&mut body).await?;
                handle(ty, &body)
            }
            Err(e) => {
                // 헤더가 깨졌으면 스트림 동기화가 불가능하므로 오류를 보내고 연결을 끊는다.
                let f = encode(MsgType::Error, &ErrorReply { code: 1, message: e.to_string() });
                pipe.write_all(&f).await?;
                return Ok(());
            }
        };
        pipe.write_all(&reply).await?;
    }
}

fn handle(ty: MsgType, body: &[u8]) -> Vec<u8> {
    match ty {
        MsgType::Hello => match decode_body::<Hello>(body) {
            Ok(h) if h.protocol == PROTOCOL_VERSION => encode(
                MsgType::HelloReply,
                &HelloReply {
                    protocol: PROTOCOL_VERSION,
                    service_version: env!("CARGO_PKG_VERSION").to_string(),
                    volumes: volumes(),
                },
            ),
            Ok(h) => err(2, &format!("protocol mismatch: client {} != service {}", h.protocol, PROTOCOL_VERSION)),
            Err(e) => err(1, &e.to_string()),
        },
        MsgType::Status => encode(MsgType::StatusReply, &StatusReply { state: 0, indexed_entries: 0, last_usn: 0 }),
        other => err(3, &format!("unsupported request: {other:?}")),
    }
}

fn err(code: u32, message: &str) -> Vec<u8> {
    encode(MsgType::Error, &ErrorReply { code, message: message.to_string() })
}

/// 드라이브 문자 목록. GetLogicalDrives는 디스크에 접근하지 않으므로
/// 끊긴 네트워크 드라이브가 있어도 막히지 않는다. (색인 대상 판정은 M2)
fn volumes() -> Vec<String> {
    // SAFETY: 인자가 없고 비트마스크만 반환하는 Win32 함수.
    let mask = unsafe { windows_sys::Win32::Storage::FileSystem::GetLogicalDrives() };
    (0..26u8).filter(|i| mask & (1 << i) != 0).map(|i| format!("{}:", (b'A' + i) as char)).collect()
}
