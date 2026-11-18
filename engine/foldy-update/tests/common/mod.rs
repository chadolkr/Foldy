//! 시험용 로컬 HTTP 서버 (스레드 하나, 연결마다 처리 후 닫음).
//! 지원: 고정 경로 → 본문, ETag/If-None-Match → 304, Range → 206, "처음 N번은 중간에 끊기".

#![allow(dead_code)]

use std::collections::HashMap;
use std::io::{BufRead, BufReader, Write};
use std::net::{TcpListener, TcpStream};
use std::sync::{Arc, Mutex};
use std::thread;

#[derive(Clone, Default)]
pub struct Route {
    pub body: Vec<u8>,
    pub etag: Option<String>,
    pub content_type: String,
    /// 처음 이 횟수만큼은 `cut_after` 바이트만 보내고 연결을 끊는다.
    pub cut_times: u32,
    pub cut_after: usize,
    /// Range를 무시하고 항상 200을 준다.
    pub ignore_range: bool,
}

#[derive(Debug, Clone)]
pub struct Seen {
    pub path: String,
    pub headers: Vec<(String, String)>,
}

impl Seen {
    pub fn header(&self, name: &str) -> Option<&str> {
        self.headers
            .iter()
            .find(|(k, _)| k.eq_ignore_ascii_case(name))
            .map(|(_, v)| v.as_str())
    }
}

pub struct TestServer {
    pub base: String,
    routes: Arc<Mutex<HashMap<String, Route>>>,
    pub seen: Arc<Mutex<Vec<Seen>>>,
}

impl TestServer {
    pub fn start() -> TestServer {
        let listener = TcpListener::bind("127.0.0.1:0").unwrap();
        let base = format!("http://{}", listener.local_addr().unwrap());
        let routes: Arc<Mutex<HashMap<String, Route>>> = Arc::default();
        let seen: Arc<Mutex<Vec<Seen>>> = Arc::default();
        let (r, s) = (routes.clone(), seen.clone());
        thread::spawn(move || {
            for stream in listener.incoming() {
                let Ok(stream) = stream else { continue };
                let (r, s) = (r.clone(), s.clone());
                thread::spawn(move || {
                    let _ = handle(stream, &r, &s);
                });
            }
        });
        TestServer { base, routes, seen }
    }

    pub fn route(&self, path: &str, route: Route) {
        self.routes.lock().unwrap().insert(path.to_owned(), route);
    }

    pub fn url(&self, path: &str) -> String {
        format!("{}{}", self.base, path)
    }

    pub fn requests_to(&self, path: &str) -> Vec<Seen> {
        self.seen
            .lock()
            .unwrap()
            .iter()
            .filter(|s| s.path == path)
            .cloned()
            .collect()
    }
}

fn handle(
    stream: TcpStream,
    routes: &Mutex<HashMap<String, Route>>,
    seen: &Mutex<Vec<Seen>>,
) -> std::io::Result<()> {
    let mut reader = BufReader::new(stream.try_clone()?);
    let mut line = String::new();
    reader.read_line(&mut line)?;
    let path = line.split_whitespace().nth(1).unwrap_or("/").to_owned();
    let mut headers = Vec::new();
    loop {
        let mut l = String::new();
        if reader.read_line(&mut l)? == 0 {
            break;
        }
        let l = l.trim_end();
        if l.is_empty() {
            break;
        }
        if let Some((k, v)) = l.split_once(':') {
            headers.push((k.trim().to_owned(), v.trim().to_owned()));
        }
    }
    let req = Seen {
        path: path.clone(),
        headers,
    };
    seen.lock().unwrap().push(req.clone());

    let route = {
        let mut map = routes.lock().unwrap();
        match map.get_mut(&path) {
            Some(r) => {
                let snapshot = r.clone();
                if r.cut_times > 0 {
                    r.cut_times -= 1;
                }
                Some(snapshot)
            }
            None => None,
        }
    };
    let mut out = stream;
    let Some(route) = route else {
        out.write_all(b"HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n")?;
        return Ok(());
    };
    if let (Some(tag), Some(inm)) = (&route.etag, req.header("if-none-match"))
        && tag == inm
    {
        out.write_all(
            format!("HTTP/1.1 304 Not Modified\r\nETag: {tag}\r\nConnection: close\r\n\r\n")
                .as_bytes(),
        )?;
        return Ok(());
    }
    let total = route.body.len();
    let start = if route.ignore_range {
        None
    } else {
        req.header("range")
            .and_then(|r| r.strip_prefix("bytes="))
            .and_then(|r| r.strip_suffix('-'))
            .and_then(|n| n.parse::<usize>().ok())
    };
    let mut head = String::new();
    let body: &[u8] = match start {
        Some(s) if s >= total => {
            out.write_all(format!("HTTP/1.1 416 Range Not Satisfiable\r\nContent-Range: bytes */{total}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n").as_bytes())?;
            return Ok(());
        }
        Some(s) => {
            head.push_str(&format!(
                "HTTP/1.1 206 Partial Content\r\nContent-Range: bytes {s}-{}/{total}\r\n",
                total - 1
            ));
            &route.body[s..]
        }
        None => {
            head.push_str("HTTP/1.1 200 OK\r\n");
            &route.body
        }
    };
    if let Some(tag) = &route.etag {
        head.push_str(&format!("ETag: {tag}\r\n"));
    }
    let ct = if route.content_type.is_empty() {
        "application/octet-stream"
    } else {
        &route.content_type
    };
    head.push_str(&format!("Content-Type: {ct}\r\nContent-Length: {}\r\nAccept-Ranges: bytes\r\nConnection: close\r\n\r\n", body.len()));
    out.write_all(head.as_bytes())?;
    if route.cut_times > 0 {
        out.write_all(&body[..route.cut_after.min(body.len())])?;
        out.flush()?;
        // 본문 도중 연결 끊기
        let _ = out.shutdown(std::net::Shutdown::Both);
        return Ok(());
    }
    out.write_all(body)?;
    out.flush()
}

/// 픽스처와 같은 설치 파일 내용 (300,000바이트 패턴).
pub fn payload() -> Vec<u8> {
    (0..300_000u32)
        .map(|i| ((i * 31 + 7) % 251) as u8)
        .collect()
}

pub fn fixture(name: &str) -> Vec<u8> {
    std::fs::read(
        std::path::Path::new(env!("CARGO_MANIFEST_DIR"))
            .join("tests")
            .join("fixtures")
            .join(name),
    )
    .unwrap()
}

pub fn temp_dir(tag: &str) -> std::path::PathBuf {
    let d = std::env::temp_dir().join(format!("foldy-update-it-{tag}-{}", std::process::id()));
    let _ = std::fs::remove_dir_all(&d);
    std::fs::create_dir_all(&d).unwrap();
    d
}
