//! Foldy 색인 서비스 파이프 프로토콜 (설계서 5.6).
//!
//! 프레임: `길이(u32 LE, 본문 바이트 수)` + `타입(u16 LE)` + `본문(postcard)`.
//! 파이프 이름: `\\.\pipe\Foldy.Indexer.v1`.
//!
//! C# 쪽(`Foldy.Indexer.Client`)은 이 정의를 따르며, `tests/golden/*.bin`
//! 골든 파일로 양쪽 인코딩이 어긋나지 않는지 검사합니다.

use serde::{Deserialize, Serialize};

/// 기본 파이프 이름.
pub const PIPE_NAME: &str = r"\\.\pipe\Foldy.Indexer.v1";
/// 프로토콜 버전. 다르면 클라이언트는 LocalProvider로 폴백한다.
pub const PROTOCOL_VERSION: u32 = 1;
/// 프레임 본문 최대 크기 (입력 길이 검사, 설계서 12장).
pub const MAX_BODY: usize = 16 * 1024 * 1024;
/// 프레임 헤더 크기.
pub const HEADER_LEN: usize = 6;

/// 메시지 타입 코드. 요청은 0x00xx, 응답은 0x80xx.
#[repr(u16)]
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum MsgType {
    Hello = 0x0001,
    Status = 0x0002,
    HelloReply = 0x8001,
    StatusReply = 0x8002,
    Error = 0x80FF,
}

impl MsgType {
    pub fn from_u16(v: u16) -> Option<Self> {
        Some(match v {
            0x0001 => Self::Hello,
            0x0002 => Self::Status,
            0x8001 => Self::HelloReply,
            0x8002 => Self::StatusReply,
            0x80FF => Self::Error,
            _ => return None,
        })
    }
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct Hello {
    pub protocol: u32,
    pub client: String,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct HelloReply {
    pub protocol: u32,
    pub service_version: String,
    pub volumes: Vec<String>,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct Status;

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct StatusReply {
    /// 0 = 준비 안 됨, 1 = 색인 중, 2 = 준비됨
    pub state: u8,
    pub indexed_entries: u64,
    pub last_usn: i64,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct ErrorReply {
    pub code: u32,
    pub message: String,
}

#[derive(Debug)]
pub enum FrameError {
    TooLarge(usize),
    UnknownType(u16),
    Decode(postcard::Error),
    Short,
}

impl std::fmt::Display for FrameError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            Self::TooLarge(n) => write!(f, "frame body too large: {n}"),
            Self::UnknownType(t) => write!(f, "unknown message type: {t:#06x}"),
            Self::Decode(e) => write!(f, "decode error: {e}"),
            Self::Short => write!(f, "frame too short"),
        }
    }
}

impl std::error::Error for FrameError {}

/// 메시지를 프레임 하나로 인코딩한다.
pub fn encode<T: Serialize>(ty: MsgType, msg: &T) -> Vec<u8> {
    let body = postcard::to_stdvec(msg).expect("postcard encoding into Vec cannot fail");
    let mut out = Vec::with_capacity(HEADER_LEN + body.len());
    out.extend_from_slice(&(body.len() as u32).to_le_bytes());
    out.extend_from_slice(&(ty as u16).to_le_bytes());
    out.extend_from_slice(&body);
    out
}

/// 헤더 6바이트를 해석한다. (본문 길이, 타입)
pub fn decode_header(h: &[u8]) -> Result<(usize, MsgType), FrameError> {
    if h.len() < HEADER_LEN {
        return Err(FrameError::Short);
    }
    let len = u32::from_le_bytes([h[0], h[1], h[2], h[3]]) as usize;
    if len > MAX_BODY {
        return Err(FrameError::TooLarge(len));
    }
    let raw = u16::from_le_bytes([h[4], h[5]]);
    let ty = MsgType::from_u16(raw).ok_or(FrameError::UnknownType(raw))?;
    Ok((len, ty))
}

pub fn decode_body<'a, T: Deserialize<'a>>(body: &'a [u8]) -> Result<T, FrameError> {
    postcard::from_bytes(body).map_err(FrameError::Decode)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn roundtrip_hello_reply() {
        let m = HelloReply { protocol: 1, service_version: "0.0.1".into(), volumes: vec!["C:".into()] };
        let f = encode(MsgType::HelloReply, &m);
        let (len, ty) = decode_header(&f).unwrap();
        assert_eq!(ty, MsgType::HelloReply);
        assert_eq!(len, f.len() - HEADER_LEN);
        let back: HelloReply = decode_body(&f[HEADER_LEN..]).unwrap();
        assert_eq!(back, m);
    }

    #[test]
    fn rejects_oversized_and_unknown() {
        let mut h = [0u8; 6];
        h[..4].copy_from_slice(&((MAX_BODY as u32) + 1).to_le_bytes());
        assert!(matches!(decode_header(&h), Err(FrameError::TooLarge(_))));
        let h = [0, 0, 0, 0, 0x34, 0x12];
        assert!(matches!(decode_header(&h), Err(FrameError::UnknownType(0x1234))));
    }

    #[test]
    fn garbage_body_does_not_panic() {
        for n in 0..64u8 {
            let junk: Vec<u8> = (0..n).map(|i| i.wrapping_mul(37).wrapping_add(n)).collect();
            let _ = decode_body::<HelloReply>(&junk);
            let _ = decode_body::<StatusReply>(&junk);
        }
    }
}
