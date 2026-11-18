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
/// v2: Search / SearchBatch / CancelSearch / FolderSizes, StatusReply에 진행률과 볼륨별 상태 추가.
pub const PROTOCOL_VERSION: u32 = 2;
/// 프레임 본문 최대 크기 (입력 길이 검사, 설계서 12장).
pub const MAX_BODY: usize = 16 * 1024 * 1024;
/// 프레임 헤더 크기.
pub const HEADER_LEN: usize = 6;
/// 검색 한 번에 돌려줄 수 있는 최대 결과 수 (limit 상한).
pub const MAX_SEARCH_LIMIT: u32 = 100_000;
/// SearchBatch 한 프레임의 최대 결과 수.
pub const SEARCH_BATCH_MAX: usize = 1000;

/// 메시지 타입 코드. 요청은 0x00xx, 응답은 0x80xx.
///
/// 응답 순서: Search 이외 요청의 응답(…Reply, Error)은 요청 순서대로 온다. SearchBatch는 그 사이에
/// 끼어 올 수 있으므로 클라이언트는 타입과 `search_id`로 나눠 받는다.
#[repr(u16)]
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum MsgType {
    Hello = 0x0001,
    Status = 0x0002,
    Search = 0x0003,
    FolderSizes = 0x0004,
    /// 응답 없음. 진행 중인 검색을 멈추고 마지막 SearchBatch(status=Cancelled)를 보내게 한다.
    CancelSearch = 0x0005,
    /// 자동 업데이트 상태 조회 (설계서 10.5). 응답 UpdateStatusReply.
    UpdateStatus = 0x00A1,
    /// 지금 확인. 응답 UpdateStatusReply (확인은 서비스가 뒤에서 진행).
    CheckNow = 0x00A2,
    /// 작업이 없을 때 설치. 경로는 받지 않는다. 응답 UpdateStatusReply 또는 Error.
    InstallWhenIdle = 0x00A3,
    SkipVersion = 0x00A4,
    SetChannel = 0x00A5,
    /// 앱이 정상 시작했음을 알린다 (되돌리기 판단). 응답 UpdateStatusReply.
    AppStarted = 0x00A6,
    HelloReply = 0x8001,
    StatusReply = 0x8002,
    SearchBatch = 0x8003,
    FolderSizesReply = 0x8004,
    UpdateStatusReply = 0x80A1,
    Error = 0x80FF,
}

impl MsgType {
    pub fn from_u16(v: u16) -> Option<Self> {
        Some(match v {
            0x0001 => Self::Hello,
            0x0002 => Self::Status,
            0x0003 => Self::Search,
            0x0004 => Self::FolderSizes,
            0x0005 => Self::CancelSearch,
            0x00A1 => Self::UpdateStatus,
            0x00A2 => Self::CheckNow,
            0x00A3 => Self::InstallWhenIdle,
            0x00A4 => Self::SkipVersion,
            0x00A5 => Self::SetChannel,
            0x00A6 => Self::AppStarted,
            0x8001 => Self::HelloReply,
            0x8002 => Self::StatusReply,
            0x8003 => Self::SearchBatch,
            0x8004 => Self::FolderSizesReply,
            0x80A1 => Self::UpdateStatusReply,
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

/// 색인 상태 코드
pub mod state {
    pub const NOT_READY: u8 = 0;
    pub const INDEXING: u8 = 1;
    pub const READY: u8 = 2;
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct VolumeStatus {
    /// "C:" 또는 (디렉터리 스캔 모드) 폴더 경로
    pub volume: String,
    pub state: u8,
    pub entries: u64,
    pub last_usn: i64,
    /// 0~1000 (색인 중일 때 진행률)
    pub progress_permille: u16,
    /// 준비 안 됨 이유 등 (예: "관리자 권한 필요")
    pub message: String,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct StatusReply {
    /// 전체 상태: 하나라도 색인 중이면 1, 아니면 하나라도 준비됐으면 2, 아니면 0
    pub state: u8,
    pub indexed_entries: u64,
    pub last_usn: i64,
    /// 색인 중인 볼륨들의 평균 진행률 (0~1000)
    pub progress_permille: u16,
    pub volumes: Vec<VolumeStatus>,
}

/// 이름 부분 문자열 검색(대소문자 무시). 같은 연결에서 새 Search가 오면 이전 검색은 자동 취소된다.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct Search {
    /// 클라이언트가 정하는 검색 번호. SearchBatch에 그대로 돌아온다.
    pub search_id: u32,
    pub query: String,
    /// 최대 결과 수 (0이면 기본 1000, 상한 MAX_SEARCH_LIMIT)
    pub limit: u32,
    /// 0 = 처음부터, 아니면 이전 마지막 배치의 next_cursor
    pub cursor: u64,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct SearchHit {
    pub path: String,
    /// 파일은 논리 크기, 폴더는 하위 전체 크기
    pub size: u64,
    pub alloc_size: u64,
    /// 수정 시각 (FILETIME, UTC 100ns)
    pub mtime: i64,
    pub is_dir: bool,
}

/// 검색 상태 코드
pub mod search_status {
    pub const OK: u8 = 0;
    pub const CANCELLED: u8 = 1;
    /// 색인이 준비되지 않음 → 클라이언트는 폴백
    pub const NOT_READY: u8 = 2;
    pub const BAD_QUERY: u8 = 3;
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct SearchBatch {
    pub search_id: u32,
    pub hits: Vec<SearchHit>,
    /// 이 검색의 마지막 배치인지
    pub done: bool,
    pub status: u8,
    /// done이고 limit에 닿았을 때 이어서 검색할 커서. 0이면 더 없음.
    pub next_cursor: u64,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct CancelSearch {
    pub search_id: u32,
}

/// 폴더 바로 아래 항목들의 크기 (폴더는 하위 전체 크기). 크기 컬럼용.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct FolderSizes {
    pub path: String,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct FolderEntry {
    pub name: String,
    pub is_dir: bool,
    pub size: u64,
    pub alloc_size: u64,
}

/// FolderSizesReply 상태 코드
pub mod folder_status {
    pub const OK: u8 = 0;
    pub const NOT_FOUND: u8 = 1;
    /// 이 경로의 볼륨은 색인되지 않았거나 아직 준비 중
    pub const NOT_INDEXED: u8 = 2;
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct FolderSizesReply {
    pub status: u8,
    /// 폴더 자신의 전체 크기
    pub total_size: u64,
    pub total_alloc: u64,
    pub entries: Vec<FolderEntry>,
}

// ───────────── 자동 업데이트 (설계서 10.5, 타입 0x00Ax / 0x80Ax) ─────────────
//
// 서비스가 확인·다운로드·검증·설치를 모두 맡고, 앱은 상태를 보고 시점만 알린다.
// 어떤 메시지도 파일 경로를 받지 않는다: 설치 파일은 서비스가 자기 업데이트 폴더에서 검증한 MSI뿐이다.
// 업데이트를 지원하지 않는 (이전) 서비스는 이 타입을 모르므로 Error를 보내고 연결을 끊는다 →
// 클라이언트는 별도 연결로 확인하고, 실패하면 기존 방식(UAC 설치)으로 폴백한다.

/// UpdateStatusReply.state
pub mod update_state {
    pub const IDLE: u8 = 0;
    pub const CHECKING: u8 = 1;
    pub const DOWNLOADING: u8 = 2;
    pub const READY_TO_INSTALL: u8 = 3;
    pub const INSTALLING: u8 = 4;
    pub const FAILED: u8 = 5;
}

/// 업데이트 채널 코드 (SetChannel.channel, UpdateStatusReply.channel)
pub mod update_channel {
    pub const STABLE: u8 = 0;
    pub const BETA: u8 = 1;
}

/// 업데이트 요청의 Error 코드
pub mod update_error {
    /// 이 서비스 실행에서는 자동 업데이트가 꺼져 있음 (콘솔 실행 등) → 클라이언트는 폴백
    pub const DISABLED: u32 = 0xA0;
    /// 설치할 준비된 업데이트가 없음
    pub const NOT_READY: u32 = 0xA1;
    /// 잘못된 인자 (버전 형식, 채널 코드)
    pub const BAD_ARGUMENT: u32 = 0xA2;
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct UpdateStatus;

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct CheckNow;

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct InstallWhenIdle {
    /// 설치가 끝나면 서비스가 Foldy를 다시 띄울지 ([재시작하고 업데이트]). 종료 시 설치면 false.
    pub relaunch: bool,
    /// 다시 띄울 세션. 서비스는 파이프 클라이언트의 실제 세션을 우선하고, 이 값은 그와 같을 때만 쓴다.
    pub session_id: u32,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct SkipVersion {
    pub version: String,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct SetChannel {
    pub channel: u8,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct AppStarted {
    /// 앱 자신의 버전 (예: "1.2.3")
    pub version: String,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct UpdateStatusReply {
    pub state: u8,
    /// 서비스가 보는 설치 버전
    pub current_version: String,
    /// 받았거나 받는 중인 새 버전 (없으면 빈 문자열)
    pub available_version: String,
    pub channel: u8,
    /// 다운로드 진행률 0~1000
    pub progress_permille: u16,
    pub downloaded: u64,
    pub total: u64,
    /// 마지막으로 확인에 성공한 시각 (Unix 초, 0 = 없음)
    pub last_check_unix: u64,
    /// 마지막 오류 (없으면 빈 문자열)
    pub last_error: String,
    /// 건너뛸 수 없는 업데이트
    pub mandatory: bool,
    /// 설치 요청을 받아 Foldy 종료를 기다리는 중
    pub install_pending: bool,
    /// 변경 내역 (Release 본문)
    pub notes: String,
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
            let _ = decode_body::<Search>(&junk);
            let _ = decode_body::<SearchBatch>(&junk);
            let _ = decode_body::<FolderSizesReply>(&junk);
            let _ = decode_body::<UpdateStatusReply>(&junk);
            let _ = decode_body::<InstallWhenIdle>(&junk);
        }
    }
}
