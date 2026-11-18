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
/// v2 확장(하위 호환): BuildAnalysis / AnalysisInfo 요청, StatusReply 끝에 `analysis` 목록.
/// 옛 클라이언트는 새 요청을 보내지 않고 StatusReply 끝에 붙은 바이트를 읽지 않으므로 버전은 그대로 둔다.
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
    /// 디스크 분석 스냅샷 DB 만들기를 시작한다 (설계서 7.3). 바로 BuildAnalysisReply가 오고,
    /// 진행률은 StatusReply.analysis로 본다.
    BuildAnalysis = 0x0006,
    /// 볼륨의 최신 분석 DB 경로·분석 시각과 비교용 이전 스냅샷 목록.
    AnalysisInfo = 0x0007,
    HelloReply = 0x8001,
    StatusReply = 0x8002,
    SearchBatch = 0x8003,
    FolderSizesReply = 0x8004,
    BuildAnalysisReply = 0x8006,
    AnalysisInfoReply = 0x8007,
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
            0x0006 => Self::BuildAnalysis,
            0x0007 => Self::AnalysisInfo,
            0x8001 => Self::HelloReply,
            0x8002 => Self::StatusReply,
            0x8003 => Self::SearchBatch,
            0x8004 => Self::FolderSizesReply,
            0x8006 => Self::BuildAnalysisReply,
            0x8007 => Self::AnalysisInfoReply,
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
    /// 분석 DB를 만들었거나 만드는 중인 볼륨들 (v2 확장, 맨 끝에 붙인다)
    pub analysis: Vec<AnalysisProgress>,
}

/// 분석 작업 상태 코드 (AnalysisProgress.state)
pub mod analysis_state {
    pub const IDLE: u8 = 0;
    pub const BUILDING: u8 = 1;
    pub const READY: u8 = 2;
    pub const FAILED: u8 = 3;
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct AnalysisProgress {
    pub volume: String,
    pub state: u8,
    /// 0~1000
    pub progress_permille: u16,
    /// 단계 설명 또는 실패 이유
    pub message: String,
}

/// 분석 DB 만들기. `volume`은 Hello/Status의 볼륨 이름("C:") 또는 --scan-dir 폴더 경로.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct BuildAnalysis {
    pub volume: String,
}

/// BuildAnalysisReply / AnalysisInfoReply 상태 코드
pub mod analysis_status {
    /// BuildAnalysis: 시작함. AnalysisInfo: 스냅샷 있음.
    pub const OK: u8 = 0;
    /// 이미 만드는 중 (진행률은 Status로)
    pub const ALREADY_RUNNING: u8 = 1;
    /// 색인이 아직 준비되지 않음 → 클라이언트는 직접 열거(폴백)
    pub const NOT_READY: u8 = 2;
    /// 서비스가 모르는 볼륨 → 폴백
    pub const UNKNOWN_VOLUME: u8 = 3;
    /// AnalysisInfo: 아직 만든 스냅샷이 없음
    pub const NOT_FOUND: u8 = 4;
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct BuildAnalysisReply {
    pub status: u8,
    pub message: String,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct AnalysisInfo {
    pub volume: String,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct AnalysisSnapshot {
    /// SQLite 파일 전체 경로. 앱은 읽기 전용(mode=ro)으로 연다.
    pub db_path: String,
    /// 분석 시각 (FILETIME, UTC 100ns)
    pub scan_time: i64,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct AnalysisInfoReply {
    pub status: u8,
    pub volume: String,
    /// 최신 스냅샷 (status가 OK가 아니면 빈 경로, 0)
    pub db_path: String,
    pub scan_time: i64,
    /// 비교용 이전 스냅샷, 최신순 (최대 4개)
    pub previous: Vec<AnalysisSnapshot>,
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
            let _ = decode_body::<BuildAnalysis>(&junk);
            let _ = decode_body::<AnalysisInfoReply>(&junk);
        }
    }
}
