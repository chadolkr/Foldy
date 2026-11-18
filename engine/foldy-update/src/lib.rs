//! Foldy 자동 업데이트 (설계서 10.5).
//!
//! 흐름: GitHub Releases 확인(ETag/304) → 버전 비교·건너뛴 버전 확인 → `update.json` + `.sig`를
//! 받아 내장 Ed25519 공개키로 검증 → 설치 파일을 Range 이어받기로 내려받기 → SHA-256 비교 →
//! (인증서를 받은 뒤) Authenticode 확인 → "설치 준비됨". 설치는 `msiexec /i <파일> /qn /norestart`.
//!
//! 모든 단계는 foldy-index 서비스가 호출한다. UI는 상태만 본다 (C# `UpdateViewModel`).

pub mod download;
pub mod error;
pub mod github;
pub mod hex;
pub mod http;
pub mod install;
pub mod manifest;
pub mod sign;
pub mod state;
pub mod updater;
pub mod version;
#[cfg(windows)]
pub mod winhttp;

/// 서비스·CLI가 쓰는 User-Agent (GitHub API는 User-Agent가 필수).
pub fn user_agent() -> String {
    format!("Foldy-Updater/{}", env!("CARGO_PKG_VERSION"))
}

/// 제품용 HTTP 클라이언트: Windows 시스템 프록시(PAC/WPAD)와 기본 자격 증명을 쓰는 WinHTTP.
#[cfg(windows)]
pub fn system_http_client() -> Result<winhttp::WinHttpClient> {
    winhttp::WinHttpClient::new(&user_agent())
}

pub use error::{Result, UpdateError};
pub use manifest::{Channel, ManifestAsset, UpdateManifest};
pub use version::Version;

/// GitHub 저장소 (설계서 10.4: 저장소 한 곳이 소스·이슈·업데이트의 기준).
pub const DEFAULT_REPO: &str = "chadolkr/Foldy";

/// GitHub REST API 기준 주소. 시험에서는 로컬 서버 주소로 바꾼다.
pub const DEFAULT_API_BASE: &str = "https://api.github.com";

/// 릴리스 자산 중 서명 대상 메타데이터 파일 이름.
pub const MANIFEST_ASSET: &str = "update.json";

/// 메타데이터 서명 파일 이름.
pub const SIGNATURE_ASSET: &str = "update.json.sig";
