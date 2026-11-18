//! 업데이트 오류. 네트워크·GitHub 장애는 조용히 다음 주기에 재시도하고 (설계서 10.5 실패 대비),
//! 서명·해시 오류는 즉시 중단하고 경고 로그를 남긴다 — 호출자가 [`UpdateError::is_security`]로 구분한다.

use std::fmt;

pub type Result<T> = std::result::Result<T, UpdateError>;

#[derive(Debug)]
pub enum UpdateError {
    /// 네트워크·TLS·연결 끊김 (다음 주기에 재시도).
    Network(String),
    /// 기대하지 않은 HTTP 상태 코드.
    HttpStatus(u16),
    /// 파일 입출력.
    Io(std::io::Error),
    /// JSON 형식 오류.
    Json(String),
    /// 버전 문자열이 SemVer가 아님.
    InvalidVersion(String),
    /// update.json 내용이 규칙에 맞지 않음.
    InvalidManifest(String),
    /// 키 파일·서명 파일 형식 오류.
    InvalidKey(String),
    /// Ed25519 서명이 맞지 않음 (보안).
    BadSignature,
    /// 앱에 공개키가 아직 내장되지 않음 (보안: 검증 불가 → 설치 안 함).
    NoEmbeddedKey,
    /// SHA-256 불일치 (보안).
    HashMismatch { expected: String, actual: String },
    /// 크기 불일치 (보안).
    SizeMismatch { expected: u64, actual: u64 },
    /// 릴리스에 필요한 자산이 없음.
    MissingAsset(String),
    /// Authenticode 서명을 확인할 수 없음 (보안).
    Authenticode(String),
    /// 업데이트 폴더 밖의 파일을 설치하라는 요청 (권한 상승 방지, 보안).
    PathNotAllowed(String),
    /// 다운로드가 끝나기 전에 연결이 끊김. `.part` 파일이 남아 있어 이어받을 수 있다.
    Incomplete { received: u64, expected: u64 },
}

impl UpdateError {
    /// 서명·해시·경로처럼 "공격일 수 있는" 오류인지. 참이면 재시도하지 말고 경고를 남긴다.
    pub fn is_security(&self) -> bool {
        matches!(
            self,
            UpdateError::BadSignature
                | UpdateError::NoEmbeddedKey
                | UpdateError::HashMismatch { .. }
                | UpdateError::SizeMismatch { .. }
                | UpdateError::Authenticode(_)
                | UpdateError::PathNotAllowed(_)
        )
    }

    /// 조용히 다음 주기에 다시 시도하면 되는 일시적 오류인지.
    pub fn is_transient(&self) -> bool {
        matches!(
            self,
            UpdateError::Network(_) | UpdateError::Incomplete { .. } | UpdateError::Io(_)
        ) || matches!(self, UpdateError::HttpStatus(s) if *s >= 500 || *s == 403 || *s == 429)
    }
}

impl fmt::Display for UpdateError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            UpdateError::Network(m) => write!(f, "네트워크 오류: {m}"),
            UpdateError::HttpStatus(s) => write!(f, "HTTP 상태 {s}"),
            UpdateError::Io(e) => write!(f, "입출력 오류: {e}"),
            UpdateError::Json(m) => write!(f, "JSON 오류: {m}"),
            UpdateError::InvalidVersion(v) => write!(f, "SemVer 형식이 아님: {v:?}"),
            UpdateError::InvalidManifest(m) => write!(f, "update.json 오류: {m}"),
            UpdateError::InvalidKey(m) => write!(f, "키/서명 형식 오류: {m}"),
            UpdateError::BadSignature => write!(f, "update.json 서명이 맞지 않음"),
            UpdateError::NoEmbeddedKey => write!(f, "내장 업데이트 공개키가 없음"),
            UpdateError::HashMismatch { expected, actual } => {
                write!(f, "SHA-256 불일치 (기대 {expected}, 실제 {actual})")
            }
            UpdateError::SizeMismatch { expected, actual } => {
                write!(f, "크기 불일치 (기대 {expected}, 실제 {actual})")
            }
            UpdateError::MissingAsset(n) => write!(f, "릴리스 자산 없음: {n}"),
            UpdateError::Authenticode(m) => write!(f, "Authenticode 확인 실패: {m}"),
            UpdateError::PathNotAllowed(p) => write!(f, "허용되지 않은 설치 경로: {p}"),
            UpdateError::Incomplete { received, expected } => {
                write!(f, "다운로드 미완료 ({received}/{expected} 바이트)")
            }
        }
    }
}

impl std::error::Error for UpdateError {
    fn source(&self) -> Option<&(dyn std::error::Error + 'static)> {
        match self {
            UpdateError::Io(e) => Some(e),
            _ => None,
        }
    }
}

impl From<std::io::Error> for UpdateError {
    fn from(e: std::io::Error) -> Self {
        UpdateError::Io(e)
    }
}

impl From<serde_json::Error> for UpdateError {
    fn from(e: serde_json::Error) -> Self {
        UpdateError::Json(e.to_string())
    }
}
