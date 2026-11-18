//! WinHTTP 기반 [`HttpClient`] (제품용).
//!
//! - 프록시: Windows 8.1+에서는 `WINHTTP_ACCESS_TYPE_AUTOMATIC_PROXY`로 시스템 설정(PAC/WPAD,
//!   수동 프록시, 우회 목록)을 WinHTTP가 알아서 적용한다. 세션을 이렇게 열 수 없는 OS에서는
//!   `WinHttpGetIEProxyConfigForCurrentUser` + `WinHttpGetProxyForUrl`로 요청마다 프록시를 정한다.
//! - 인증: `WINHTTP_AUTOLOGON_SECURITY_LEVEL_LOW`로 로그온 사용자의 기본 자격 증명을 쓰고,
//!   그래도 407이 오면 Negotiate/NTLM을 골라 `WinHttpSetCredentials(NULL, NULL)`(= 기본 자격 증명)로 다시 보낸다.
//! - TLS는 SChannel(Windows 인증서 저장소)을 쓴다.
//!
//! 서비스(SYSTEM)로 돌 때 "현재 사용자" 프록시 설정은 SYSTEM 계정의 것이다. 회사 환경에서
//! SYSTEM 프록시가 필요하면 `netsh winhttp set proxy`나 그룹 정책으로 설정한다 (docs/release-signing.md).

use std::ffi::c_void;
use std::io::{self, Read};

use windows::Win32::Foundation::{ERROR_INSUFFICIENT_BUFFER, GetLastError, GlobalFree, HGLOBAL};
use windows::Win32::Networking::WinHttp::*;
use windows::core::{BOOL, PCWSTR, PWSTR};

use crate::error::{Result, UpdateError};
use crate::http::{HttpClient, HttpResponse, Url, check_header};

/// WinHTTP 핸들. 드롭할 때 닫는다.
struct Handle(*mut c_void);

impl Handle {
    fn new(raw: *mut c_void, what: &str) -> Result<Handle> {
        if raw.is_null() {
            Err(last_error(what))
        } else {
            Ok(Handle(raw))
        }
    }
}

impl Drop for Handle {
    fn drop(&mut self) {
        // SAFETY: self.0은 WinHTTP가 돌려준 null 아닌 핸들이고, 여기서 한 번만 닫는다.
        unsafe {
            let _ = WinHttpCloseHandle(self.0);
        }
    }
}

// SAFETY: WinHTTP 세션 핸들은 여러 스레드에서 동시에 써도 된다 (MSDN: 동기 모드 세션은 스레드 안전).
// 요청·연결 핸들은 HttpResponse 본문 하나가 소유하고 한 번에 한 스레드만 읽는다.
unsafe impl Send for Handle {}
// SAFETY: 위와 같음. 공유되는 것은 세션 핸들뿐이고 &self로는 닫지 않는다.
unsafe impl Sync for Handle {}

fn last_error(what: &str) -> UpdateError {
    // SAFETY: 인자 없는 스레드 지역 오류 코드 조회.
    let code = unsafe { GetLastError() };
    UpdateError::Network(format!("{what} 실패 (WinHTTP 오류 {})", code.0))
}

fn wide(s: &str) -> Vec<u16> {
    s.encode_utf16().chain(std::iter::once(0)).collect()
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum ProxyMode {
    /// WinHTTP가 시스템 설정을 자동 적용 (Windows 8.1+).
    Automatic,
    /// 요청마다 IE/시스템 설정 + PAC/WPAD를 직접 조회.
    IeFallback,
    /// 프록시 없음 (시험에서 루프백 서버와 통신할 때).
    Direct,
}

pub struct WinHttpClient {
    session: Handle,
    mode: ProxyMode,
    max_proxy_auth_retries: u32,
}

impl WinHttpClient {
    /// 시스템 프록시를 쓰는 클라이언트 (제품 기본값).
    pub fn new(user_agent: &str) -> Result<WinHttpClient> {
        let ua = wide(user_agent);
        // SAFETY: ua는 호출 동안 살아 있는 NUL 종료 UTF-16. 프록시 문자열은 null(자동 모드).
        let raw = unsafe {
            WinHttpOpen(
                PCWSTR(ua.as_ptr()),
                WINHTTP_ACCESS_TYPE_AUTOMATIC_PROXY,
                PCWSTR::null(),
                PCWSTR::null(),
                0,
            )
        };
        let (session, mode) = if raw.is_null() {
            // Windows 8.1 미만: 자동 모드가 없으므로 프록시 없이 열고 요청마다 직접 정한다.
            // SAFETY: 위와 같음.
            let raw = unsafe {
                WinHttpOpen(
                    PCWSTR(ua.as_ptr()),
                    WINHTTP_ACCESS_TYPE_NO_PROXY,
                    PCWSTR::null(),
                    PCWSTR::null(),
                    0,
                )
            };
            (Handle::new(raw, "WinHttpOpen")?, ProxyMode::IeFallback)
        } else {
            (Handle(raw), ProxyMode::Automatic)
        };
        Self::finish(session, mode)
    }

    /// 프록시를 쓰지 않는 클라이언트 (루프백 시험 서버용).
    pub fn direct(user_agent: &str) -> Result<WinHttpClient> {
        let ua = wide(user_agent);
        // SAFETY: ua는 호출 동안 살아 있는 NUL 종료 UTF-16.
        let raw = unsafe {
            WinHttpOpen(
                PCWSTR(ua.as_ptr()),
                WINHTTP_ACCESS_TYPE_NO_PROXY,
                PCWSTR::null(),
                PCWSTR::null(),
                0,
            )
        };
        Self::finish(Handle::new(raw, "WinHttpOpen")?, ProxyMode::Direct)
    }

    fn finish(session: Handle, mode: ProxyMode) -> Result<WinHttpClient> {
        // 이름 해석 30초, 연결 30초, 송신 30초, 수신 60초
        // SAFETY: session은 열린 세션 핸들.
        unsafe { WinHttpSetTimeouts(session.0, 30_000, 30_000, 30_000, 60_000) }
            .map_err(|e| UpdateError::Network(format!("WinHttpSetTimeouts: {e}")))?;
        Ok(WinHttpClient {
            session,
            mode,
            max_proxy_auth_retries: 2,
        })
    }

    /// Windows 8.1 미만 경로: 시스템(IE) 설정과 PAC/WPAD로 이 URL의 프록시를 정해 요청에 건다.
    fn apply_fallback_proxy(&self, request: &Handle, url: &str) -> Result<()> {
        let Some((proxy, bypass)) = self.resolve_fallback_proxy(url) else {
            return Ok(()); // 직접 연결
        };
        let proxy_w = wide(&proxy);
        let mut bypass_w = bypass.as_deref().map(wide);
        let info = WINHTTP_PROXY_INFO {
            dwAccessType: WINHTTP_ACCESS_TYPE_NAMED_PROXY,
            lpszProxy: PWSTR(proxy_w.as_ptr().cast_mut()),
            lpszProxyBypass: bypass_w
                .as_mut()
                .map_or(PWSTR::null(), |b| PWSTR(b.as_mut_ptr())),
        };
        // SAFETY: info는 이 호출 동안 살아 있는 repr(C) 구조체이고, 가리키는 문자열도 살아 있다.
        // WinHTTP는 WINHTTP_OPTION_PROXY 값을 복사해 둔다.
        let bytes = unsafe {
            std::slice::from_raw_parts(
                (&raw const info).cast::<u8>(),
                size_of::<WINHTTP_PROXY_INFO>(),
            )
        };
        // SAFETY: request는 열린 요청 핸들, bytes는 위 구조체.
        unsafe { WinHttpSetOption(Some(request.0), WINHTTP_OPTION_PROXY, Some(bytes)) }
            .map_err(|e| UpdateError::Network(format!("프록시 설정: {e}")))
    }

    fn resolve_fallback_proxy(&self, url: &str) -> Option<(String, Option<String>)> {
        let mut ie = WINHTTP_CURRENT_USER_IE_PROXY_CONFIG::default();
        // SAFETY: ie는 쓰기 가능한 출력 구조체. 성공 시 안의 문자열은 GlobalFree로 해제해야 한다.
        let ok = unsafe { WinHttpGetIEProxyConfigForCurrentUser(&mut ie) }.is_ok();
        if !ok {
            return None;
        }
        let auto_url = take_pwstr(ie.lpszAutoConfigUrl);
        let static_proxy = take_pwstr(ie.lpszProxy);
        let static_bypass = take_pwstr(ie.lpszProxyBypass);

        if ie.fAutoDetect.as_bool() || auto_url.is_some() {
            let auto_url_w = auto_url.as_deref().map(wide);
            let mut opts = WINHTTP_AUTOPROXY_OPTIONS {
                dwFlags: 0,
                dwAutoDetectFlags: 0,
                lpszAutoConfigUrl: auto_url_w
                    .as_ref()
                    .map_or(PCWSTR::null(), |w| PCWSTR(w.as_ptr())),
                lpvReserved: std::ptr::null_mut(),
                dwReserved: 0,
                fAutoLogonIfChallenged: BOOL(1),
            };
            if ie.fAutoDetect.as_bool() {
                opts.dwFlags |= WINHTTP_AUTOPROXY_AUTO_DETECT;
                opts.dwAutoDetectFlags =
                    WINHTTP_AUTO_DETECT_TYPE_DHCP | WINHTTP_AUTO_DETECT_TYPE_DNS_A;
            }
            if auto_url_w.is_some() {
                opts.dwFlags |= WINHTTP_AUTOPROXY_CONFIG_URL;
            }
            let url_w = wide(url);
            let mut info = WINHTTP_PROXY_INFO::default();
            // SAFETY: 세션은 열려 있고, url_w·opts·info는 호출 동안 살아 있다. 성공 시 info 문자열은 GlobalFree.
            let ok = unsafe {
                WinHttpGetProxyForUrl(self.session.0, PCWSTR(url_w.as_ptr()), &mut opts, &mut info)
            }
            .is_ok();
            if ok {
                let proxy = take_pwstr(info.lpszProxy);
                let bypass = take_pwstr(info.lpszProxyBypass);
                return if info.dwAccessType == WINHTTP_ACCESS_TYPE_NAMED_PROXY {
                    proxy.map(|p| (p, bypass))
                } else {
                    None
                };
            }
            // PAC/WPAD 실패 → 수동 프록시 설정으로 넘어간다
        }
        static_proxy.map(|p| (p, static_bypass))
    }
}

/// WinHTTP가 할당한 문자열을 String으로 옮기고 해제한다.
fn take_pwstr(p: PWSTR) -> Option<String> {
    if p.is_null() {
        return None;
    }
    // SAFETY: p는 WinHTTP가 돌려준 NUL 종료 UTF-16 문자열.
    let s = unsafe { p.to_string() }.ok();
    // SAFETY: WinHTTP 문서대로 GlobalFree로 해제한다. 이후 p를 쓰지 않는다.
    unsafe {
        let _ = GlobalFree(Some(HGLOBAL(p.0.cast())));
    }
    s.filter(|s| !s.is_empty())
}

impl HttpClient for WinHttpClient {
    fn get(&self, url_text: &str, headers: &[(&str, &str)]) -> Result<HttpResponse> {
        let url = Url::parse(url_text)?;
        let host_w = wide(&url.host);
        // SAFETY: 세션은 열려 있고 host_w는 NUL 종료 문자열.
        let connect = Handle::new(
            unsafe { WinHttpConnect(self.session.0, PCWSTR(host_w.as_ptr()), url.port, 0) },
            "WinHttpConnect",
        )?;
        let verb = wide("GET");
        let path_w = wide(&url.path);
        let flags = if url.secure {
            WINHTTP_FLAG_SECURE
        } else {
            WINHTTP_OPEN_REQUEST_FLAGS(0)
        };
        // SAFETY: connect는 열린 연결 핸들, 문자열은 NUL 종료, 버전·참조·Accept는 null(기본값).
        let request = Handle::new(
            unsafe {
                WinHttpOpenRequest(
                    connect.0,
                    PCWSTR(verb.as_ptr()),
                    PCWSTR(path_w.as_ptr()),
                    PCWSTR::null(),
                    PCWSTR::null(),
                    std::ptr::null(),
                    flags,
                )
            },
            "WinHttpOpenRequest",
        )?;

        // 로그온 사용자 자격 증명을 프록시·인트라넷 인증에 자동으로 쓴다 (NTLM/Negotiate)
        let policy = WINHTTP_AUTOLOGON_SECURITY_LEVEL_LOW.to_ne_bytes();
        // SAFETY: request는 열린 요청 핸들, 값은 DWORD 4바이트.
        unsafe {
            WinHttpSetOption(
                Some(request.0),
                WINHTTP_OPTION_AUTOLOGON_POLICY,
                Some(&policy),
            )
        }
        .map_err(|e| UpdateError::Network(format!("자동 로그온 정책: {e}")))?;

        if self.mode == ProxyMode::IeFallback {
            self.apply_fallback_proxy(&request, url_text)?;
        }

        let mut header_text = String::new();
        for (k, v) in headers {
            check_header(k, v)?;
            header_text.push_str(&format!("{k}: {v}\r\n"));
        }
        let header_w: Vec<u16> = header_text.encode_utf16().collect();

        let mut auth_tries = 0;
        loop {
            let h = (!header_w.is_empty()).then_some(header_w.as_slice());
            // SAFETY: request는 열린 요청 핸들. 헤더 슬라이스는 호출 동안 살아 있고, 본문은 없다.
            unsafe { WinHttpSendRequest(request.0, h, None, 0, 0, 0) }
                .map_err(|e| UpdateError::Network(format!("요청 전송: {e}")))?;
            // SAFETY: 같은 요청 핸들, 예약 인자는 null.
            unsafe { WinHttpReceiveResponse(request.0, std::ptr::null_mut()) }
                .map_err(|e| UpdateError::Network(format!("응답 수신: {e}")))?;
            let status = query_status(&request)?;
            if status == 407 && auth_tries < self.max_proxy_auth_retries {
                auth_tries += 1;
                set_default_proxy_credentials(&request)?;
                drain(&request);
                continue;
            }
            let headers = query_headers(&request)?;
            return Ok(HttpResponse {
                status,
                headers,
                body: Box::new(WinHttpBody {
                    request,
                    _connect: connect,
                }),
            });
        }
    }
}

fn query_status(request: &Handle) -> Result<u16> {
    let mut status: u32 = 0;
    let mut len = size_of::<u32>() as u32;
    // SAFETY: 숫자 플래그로 DWORD 하나를 status에 쓴다. len은 버퍼 크기.
    unsafe {
        WinHttpQueryHeaders(
            request.0,
            WINHTTP_QUERY_STATUS_CODE | WINHTTP_QUERY_FLAG_NUMBER,
            PCWSTR::null(),
            Some((&raw mut status).cast()),
            &mut len,
            std::ptr::null_mut(),
        )
    }
    .map_err(|e| UpdateError::Network(format!("상태 코드 조회: {e}")))?;
    u16::try_from(status).map_err(|_| UpdateError::Network(format!("잘못된 상태 코드 {status}")))
}

fn query_headers(request: &Handle) -> Result<Vec<(String, String)>> {
    let mut len: u32 = 0;
    // SAFETY: 버퍼 없이 필요한 크기(바이트)만 묻는다. ERROR_INSUFFICIENT_BUFFER가 정상.
    let first = unsafe {
        WinHttpQueryHeaders(
            request.0,
            WINHTTP_QUERY_RAW_HEADERS_CRLF,
            PCWSTR::null(),
            None,
            &mut len,
            std::ptr::null_mut(),
        )
    };
    if let Err(e) = first
        && e.code() != ERROR_INSUFFICIENT_BUFFER.to_hresult()
    {
        return Err(UpdateError::Network(format!("헤더 조회: {e}")));
    }
    let mut buf = vec![0u16; (len as usize).div_ceil(2) + 1];
    let mut len_bytes = (buf.len() * 2) as u32;
    // SAFETY: buf는 len_bytes 바이트 크기의 쓰기 가능한 버퍼.
    unsafe {
        WinHttpQueryHeaders(
            request.0,
            WINHTTP_QUERY_RAW_HEADERS_CRLF,
            PCWSTR::null(),
            Some(buf.as_mut_ptr().cast()),
            &mut len_bytes,
            std::ptr::null_mut(),
        )
    }
    .map_err(|e| UpdateError::Network(format!("헤더 조회: {e}")))?;
    let text = String::from_utf16_lossy(&buf[..(len_bytes as usize / 2).min(buf.len())]);
    Ok(parse_raw_headers(&text))
}

/// `HTTP/1.1 200 OK\r\nA: b\r\n...` → [(A, b), ...]. 상태 줄은 건너뛴다.
pub(crate) fn parse_raw_headers(text: &str) -> Vec<(String, String)> {
    text.split("\r\n")
        .skip(1)
        .filter_map(|l| l.split_once(':'))
        .map(|(k, v)| (k.trim().to_owned(), v.trim().to_owned()))
        .filter(|(k, _)| !k.is_empty())
        .collect()
}

/// 407에 대해 Negotiate → NTLM 순으로 고르고, 사용자 이름·암호를 null로 줘서 기본 자격 증명을 쓰게 한다.
fn set_default_proxy_credentials(request: &Handle) -> Result<()> {
    let (mut supported, mut first, mut target) = (0u32, 0u32, 0u32);
    // SAFETY: 요청 핸들은 407 응답을 받은 상태, 출력 인자는 지역 변수.
    unsafe { WinHttpQueryAuthSchemes(request.0, &mut supported, &mut first, &mut target) }
        .map_err(|e| UpdateError::Network(format!("프록시 인증 방식 조회: {e}")))?;
    let scheme = [WINHTTP_AUTH_SCHEME_NEGOTIATE.0, WINHTTP_AUTH_SCHEME_NTLM.0]
        .into_iter()
        .find(|s| supported & s != 0)
        .ok_or_else(|| {
            UpdateError::Network(
                "프록시가 Negotiate/NTLM을 지원하지 않음 (기본 자격 증명으로 인증 불가)".into(),
            )
        })?;
    // SAFETY: 사용자 이름·암호 null = 로그온 사용자의 기본 자격 증명 (MSDN WinHttpSetCredentials).
    unsafe {
        WinHttpSetCredentials(
            request.0,
            WINHTTP_AUTH_TARGET_PROXY,
            scheme,
            PCWSTR::null(),
            PCWSTR::null(),
            std::ptr::null_mut(),
        )
    }
    .map_err(|e| UpdateError::Network(format!("프록시 자격 증명: {e}")))
}

/// 재전송 전에 407 응답 본문을 버린다.
fn drain(request: &Handle) {
    let mut buf = [0u8; 4096];
    loop {
        let mut read = 0u32;
        // SAFETY: buf는 4096바이트 쓰기 가능한 버퍼.
        let ok = unsafe {
            WinHttpReadData(
                request.0,
                buf.as_mut_ptr().cast(),
                buf.len() as u32,
                &mut read,
            )
        }
        .is_ok();
        if !ok || read == 0 {
            break;
        }
    }
}

/// 응답 본문 스트림. 요청 핸들이 먼저 닫히도록 필드 순서를 지킨다.
struct WinHttpBody {
    request: Handle,
    _connect: Handle,
}

impl Read for WinHttpBody {
    fn read(&mut self, buf: &mut [u8]) -> io::Result<usize> {
        if buf.is_empty() {
            return Ok(0);
        }
        let want = u32::try_from(buf.len()).unwrap_or(u32::MAX);
        let mut read = 0u32;
        // SAFETY: buf는 want 바이트 이상 쓰기 가능하고, 요청 핸들은 응답을 받은 상태.
        unsafe { WinHttpReadData(self.request.0, buf.as_mut_ptr().cast(), want, &mut read) }
            .map_err(|e| io::Error::new(io::ErrorKind::ConnectionAborted, e.to_string()))?;
        Ok(read as usize)
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn raw_headers_parse() {
        let h = parse_raw_headers(
            "HTTP/1.1 206 Partial Content\r\nETag: \"abc\"\r\nContent-Range: bytes 1-2/3\r\n\r\n",
        );
        assert_eq!(
            h,
            vec![
                ("ETag".into(), "\"abc\"".into()),
                ("Content-Range".into(), "bytes 1-2/3".into())
            ]
        );
    }

    #[test]
    fn system_proxy_session_opens() {
        // 네트워크 없이도 세션은 열려야 한다 (자동 프록시 모드 또는 폴백 모드)
        let c = WinHttpClient::new("Foldy-Updater-test").unwrap();
        assert!(matches!(
            c.mode,
            ProxyMode::Automatic | ProxyMode::IeFallback
        ));
    }
}
