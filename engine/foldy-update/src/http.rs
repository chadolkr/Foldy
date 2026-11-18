//! HTTP 계층. 업데이트 로직은 [`HttpClient`] 트레이트만 보고, 실제 통신은
//! - [`crate::winhttp::WinHttpClient`]: 제품용. Windows 시스템 프록시(PAC/WPAD 포함)와
//!   로그온 사용자의 기본 자격 증명(NTLM/Negotiate)으로 회사 프록시 인증까지 처리한다.
//! - [`PlainHttpClient`]: 시험·개발용. `http://`만, 프록시·TLS 없음. 로컬 시험 서버와 통신한다.
//!
//! GET만 필요하다 (릴리스 확인, update.json, 설치 파일 Range 다운로드).

use std::io::{self, BufRead, BufReader, Read, Write};
use std::net::TcpStream;
use std::time::Duration;

use crate::error::{Result, UpdateError};

/// 응답. 본문은 스트림으로 읽는다 (설치 파일은 수십 MB).
pub struct HttpResponse {
    pub status: u16,
    pub headers: Vec<(String, String)>,
    pub body: Box<dyn Read>,
}

impl HttpResponse {
    /// 이름은 대소문자를 구분하지 않는다. 같은 이름이 여러 개면 첫 번째.
    pub fn header(&self, name: &str) -> Option<&str> {
        self.headers
            .iter()
            .find(|(k, _)| k.eq_ignore_ascii_case(name))
            .map(|(_, v)| v.as_str())
    }

    /// 본문을 `limit` 바이트까지만 읽는다. 넘으면 오류 (악의적인 서버가 메모리를 채우지 못하게).
    pub fn read_body_limited(mut self, limit: usize) -> Result<Vec<u8>> {
        let mut buf = Vec::new();
        let n = (&mut self.body)
            .take(limit as u64 + 1)
            .read_to_end(&mut buf)
            .map_err(net)?;
        if n > limit {
            return Err(UpdateError::Network(format!(
                "응답 본문이 {limit}바이트를 넘음"
            )));
        }
        Ok(buf)
    }
}

impl std::fmt::Debug for HttpResponse {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("HttpResponse")
            .field("status", &self.status)
            .field("headers", &self.headers)
            .finish()
    }
}

pub trait HttpClient {
    /// GET 요청. 4xx/5xx도 오류가 아니라 응답으로 돌려준다 (304·404·416을 호출자가 해석).
    /// 리디렉션(GitHub 자산 → CDN)은 구현이 따라간다.
    fn get(&self, url: &str, headers: &[(&str, &str)]) -> Result<HttpResponse>;
}

impl<T: HttpClient + ?Sized> HttpClient for &T {
    fn get(&self, url: &str, headers: &[(&str, &str)]) -> Result<HttpResponse> {
        (**self).get(url, headers)
    }
}

impl<T: HttpClient + ?Sized> HttpClient for Box<T> {
    fn get(&self, url: &str, headers: &[(&str, &str)]) -> Result<HttpResponse> {
        (**self).get(url, headers)
    }
}

/// 분해한 URL. `host`는 IPv6면 대괄호 없이 담는다.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Url {
    pub secure: bool,
    pub host: String,
    pub port: u16,
    /// 경로 + 쿼리 (`/a/b?c=d`). 비어 있으면 `/`.
    pub path: String,
}

impl Url {
    pub fn parse(url: &str) -> Result<Url> {
        let bad = || UpdateError::Network(format!("지원하지 않는 URL: {url}"));
        let (secure, rest) = if let Some(r) = strip_prefix_ci(url, "https://") {
            (true, r)
        } else if let Some(r) = strip_prefix_ci(url, "http://") {
            (false, r)
        } else {
            return Err(bad());
        };
        let (authority, path) = match rest.find(['/', '?', '#']) {
            Some(i) => (&rest[..i], &rest[i..]),
            None => (rest, ""),
        };
        let path = path.split('#').next().unwrap_or("");
        if authority.is_empty() || authority.contains('@') {
            // 사용자 정보가 든 URL은 받지 않는다 (자격 증명 유출 방지)
            return Err(bad());
        }
        let default_port = if secure { 443 } else { 80 };
        let (host, port) = if let Some(v6) = authority.strip_prefix('[') {
            let (h, after) = v6.split_once(']').ok_or_else(bad)?;
            let port = match after.strip_prefix(':') {
                Some(p) => p.parse().map_err(|_| bad())?,
                None if after.is_empty() => default_port,
                None => return Err(bad()),
            };
            (h.to_owned(), port)
        } else {
            match authority.rsplit_once(':') {
                Some((h, p)) => (h.to_owned(), p.parse().map_err(|_| bad())?),
                None => (authority.to_owned(), default_port),
            }
        };
        if host.is_empty() || host.chars().any(|c| c.is_whitespace() || c.is_control()) {
            return Err(bad());
        }
        let path = if path.is_empty() {
            "/".to_owned()
        } else if path.starts_with('?') {
            format!("/{path}")
        } else {
            path.to_owned()
        };
        Ok(Url {
            secure,
            host,
            port,
            path,
        })
    }

    /// 상대 Location 헤더를 이 URL 기준으로 푼다.
    pub fn join(&self, location: &str) -> Result<Url> {
        if strip_prefix_ci(location, "http://").is_some()
            || strip_prefix_ci(location, "https://").is_some()
        {
            return Url::parse(location);
        }
        let scheme = if self.secure { "https" } else { "http" };
        let host = if self.host.contains(':') {
            format!("[{}]", self.host)
        } else {
            self.host.clone()
        };
        let path = if location.starts_with('/') {
            location.to_owned()
        } else {
            let base = self.path.split('?').next().unwrap_or("/");
            let dir = &base[..base.rfind('/').map_or(0, |i| i + 1)];
            format!("{dir}{location}")
        };
        Url::parse(&format!("{scheme}://{host}:{}{path}", self.port))
    }
}

fn strip_prefix_ci<'a>(s: &'a str, prefix: &str) -> Option<&'a str> {
    (s.len() >= prefix.len()
        && s.is_char_boundary(prefix.len())
        && s[..prefix.len()].eq_ignore_ascii_case(prefix))
    .then(|| &s[prefix.len()..])
}

pub(crate) fn net(e: io::Error) -> UpdateError {
    UpdateError::Network(e.to_string())
}

/// 헤더 값에 줄바꿈이 섞이면 요청 위조가 되므로 막는다.
pub(crate) fn check_header(name: &str, value: &str) -> Result<()> {
    if name.is_empty() || name.contains([':', '\r', '\n']) || value.contains(['\r', '\n']) {
        return Err(UpdateError::Network(format!("잘못된 헤더: {name}")));
    }
    Ok(())
}

/// 시험·개발용 HTTP/1.1 클라이언트. `http://`만 지원하고 프록시를 쓰지 않는다.
/// 제품 코드는 [`crate::winhttp::WinHttpClient`]를 쓴다.
#[derive(Debug, Clone)]
pub struct PlainHttpClient {
    pub timeout: Duration,
    pub max_redirects: u32,
}

impl Default for PlainHttpClient {
    fn default() -> Self {
        PlainHttpClient {
            timeout: Duration::from_secs(10),
            max_redirects: 5,
        }
    }
}

impl HttpClient for PlainHttpClient {
    fn get(&self, url: &str, headers: &[(&str, &str)]) -> Result<HttpResponse> {
        let mut url = Url::parse(url)?;
        for _ in 0..=self.max_redirects {
            if url.secure {
                return Err(UpdateError::Network(
                    "PlainHttpClient는 https를 지원하지 않음".into(),
                ));
            }
            let resp = self.get_once(&url, headers)?;
            if matches!(resp.status, 301 | 302 | 303 | 307 | 308)
                && let Some(loc) = resp.header("location")
            {
                url = url.join(loc)?;
                continue;
            }
            return Ok(resp);
        }
        Err(UpdateError::Network("리디렉션이 너무 많음".into()))
    }
}

impl PlainHttpClient {
    fn get_once(&self, url: &Url, headers: &[(&str, &str)]) -> Result<HttpResponse> {
        let addr = if url.host.contains(':') {
            format!("[{}]:{}", url.host, url.port)
        } else {
            format!("{}:{}", url.host, url.port)
        };
        let stream = TcpStream::connect(&addr).map_err(net)?;
        stream.set_read_timeout(Some(self.timeout)).map_err(net)?;
        stream.set_write_timeout(Some(self.timeout)).map_err(net)?;
        let mut req = format!(
            "GET {} HTTP/1.1\r\nHost: {}\r\nConnection: close\r\n",
            url.path, addr
        );
        for (k, v) in headers {
            check_header(k, v)?;
            req.push_str(&format!("{k}: {v}\r\n"));
        }
        req.push_str("\r\n");
        (&stream).write_all(req.as_bytes()).map_err(net)?;

        let mut reader = BufReader::new(stream);
        let mut line = String::new();
        reader.read_line(&mut line).map_err(net)?;
        let status: u16 = line
            .split_whitespace()
            .nth(1)
            .and_then(|s| s.parse().ok())
            .ok_or_else(|| UpdateError::Network(format!("잘못된 상태 줄: {line:?}")))?;
        let mut hdrs = Vec::new();
        loop {
            line.clear();
            if reader.read_line(&mut line).map_err(net)? == 0 {
                return Err(UpdateError::Network("헤더 도중 연결 끊김".into()));
            }
            let l = line.trim_end_matches(['\r', '\n']);
            if l.is_empty() {
                break;
            }
            if let Some((k, v)) = l.split_once(':') {
                hdrs.push((k.trim().to_owned(), v.trim().to_owned()));
            }
        }
        let len = hdrs
            .iter()
            .find(|(k, _)| k.eq_ignore_ascii_case("content-length"))
            .and_then(|(_, v)| v.parse::<u64>().ok());
        let body: Box<dyn Read> = match len {
            Some(n) => Box::new(ExactReader {
                inner: reader,
                remaining: n,
            }),
            None => Box::new(reader),
        };
        Ok(HttpResponse {
            status,
            headers: hdrs,
            body,
        })
    }
}

/// Content-Length만큼 읽고, 그 전에 연결이 끊기면 `UnexpectedEof`로 알린다.
struct ExactReader<R> {
    inner: R,
    remaining: u64,
}

impl<R: Read> Read for ExactReader<R> {
    fn read(&mut self, buf: &mut [u8]) -> io::Result<usize> {
        if self.remaining == 0 || buf.is_empty() {
            return Ok(0);
        }
        let max = buf
            .len()
            .min(usize::try_from(self.remaining).unwrap_or(usize::MAX));
        let n = self.inner.read(&mut buf[..max])?;
        if n == 0 {
            return Err(io::Error::new(
                io::ErrorKind::UnexpectedEof,
                "본문 도중 연결 끊김",
            ));
        }
        self.remaining -= n as u64;
        Ok(n)
    }
}

/// `Content-Range: bytes 100-199/200` → (100, Some(200)).
pub fn parse_content_range(v: &str) -> Option<(u64, Option<u64>)> {
    let rest = v.trim().strip_prefix("bytes")?.trim_start();
    let (range, total) = rest.split_once('/')?;
    let (start, _end) = range.split_once('-')?;
    let total = if total.trim() == "*" {
        None
    } else {
        Some(total.trim().parse().ok()?)
    };
    Some((start.trim().parse().ok()?, total))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn parses_urls() {
        let u = Url::parse("https://api.github.com/repos/a/b/releases?per_page=30").unwrap();
        assert_eq!(
            u,
            Url {
                secure: true,
                host: "api.github.com".into(),
                port: 443,
                path: "/repos/a/b/releases?per_page=30".into()
            }
        );
        let u = Url::parse("http://127.0.0.1:8080").unwrap();
        assert_eq!((u.port, u.path.as_str()), (8080, "/"));
        let u = Url::parse("http://[::1]:9/x#frag").unwrap();
        assert_eq!((u.host.as_str(), u.port, u.path.as_str()), ("::1", 9, "/x"));
        for bad in [
            "ftp://x/",
            "http://",
            "http://user:pw@host/",
            "http://host:notaport/",
            "https://ho st/",
        ] {
            assert!(Url::parse(bad).is_err(), "{bad}");
        }
    }

    #[test]
    fn joins_locations() {
        let base = Url::parse("http://h:1/a/b?q").unwrap();
        assert_eq!(base.join("/c").unwrap().path, "/c");
        assert_eq!(base.join("c").unwrap().path, "/a/c");
        assert_eq!(base.join("https://cdn/x").unwrap().host, "cdn");
    }

    #[test]
    fn content_range() {
        assert_eq!(
            parse_content_range("bytes 100-199/200"),
            Some((100, Some(200)))
        );
        assert_eq!(parse_content_range("bytes 5-9/*"), Some((5, None)));
        assert_eq!(parse_content_range("items 1-2/3"), None);
    }

    #[test]
    fn header_injection_rejected() {
        assert!(check_header("X", "a\r\nEvil: 1").is_err());
        assert!(check_header("Range", "bytes=0-").is_ok());
    }
}
