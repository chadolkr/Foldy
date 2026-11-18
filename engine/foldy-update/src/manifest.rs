//! `update.json` 모델 (설계서 10.5 릴리스 만들기 3).
//!
//! ```json
//! {
//!   "version": "1.2.3",
//!   "channel": "stable",
//!   "min_supported_version": "1.0.0",
//!   "mandatory": false,
//!   "assets": [
//!     { "name": "Foldy-1.2.3-x64-portable.zip", "size": 1234, "sha256": "…64자 16진수…" }
//!   ]
//! }
//! ```
//!
//! 서명은 이 파일의 **바이트 그대로**에 대해 검사한다 (다시 직렬화하지 않음).

use std::fs::File;
use std::io::Read;
use std::path::Path;

use serde::{Deserialize, Serialize};
use sha2::{Digest, Sha256};

use crate::error::{Result, UpdateError};
use crate::hex;
use crate::version::Version;

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize, Default)]
#[serde(rename_all = "lowercase")]
pub enum Channel {
    #[default]
    Stable,
    Beta,
}

impl Channel {
    pub fn parse(s: &str) -> Option<Channel> {
        match s.to_ascii_lowercase().as_str() {
            "stable" => Some(Channel::Stable),
            "beta" => Some(Channel::Beta),
            _ => None,
        }
    }
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct ManifestAsset {
    /// 릴리스 자산 파일 이름. 업데이트 폴더 안의 파일 이름으로도 쓰므로 경로 구분자는 금지.
    pub name: String,
    pub size: u64,
    /// 소문자 16진수 64자.
    pub sha256: String,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct UpdateManifest {
    pub version: String,
    pub channel: Channel,
    /// 이 버전보다 낮은 설치본은 이 업데이트를 건너뛸 수 없다 (필수 업데이트로 취급).
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub min_supported_version: Option<String>,
    /// 참이면 건너뛴 버전 목록을 무시하고 설치한다 (보안 수정 등).
    #[serde(default)]
    pub mandatory: bool,
    pub assets: Vec<ManifestAsset>,
}

/// 설치 방식에 따라 고르는 자산 (설계서 10.3: MSI 설치본 / 포터블 zip).
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum PackageKind {
    Msi,
    Portable,
}

impl PackageKind {
    fn matches(self, name: &str) -> bool {
        let lower = name.to_ascii_lowercase();
        match self {
            PackageKind::Msi => lower.ends_with(".msi"),
            PackageKind::Portable => lower.ends_with("-portable.zip"),
        }
    }
}

impl UpdateManifest {
    /// 바이트를 읽고 규칙을 검사한다. 서명 검사는 이 함수 **전에** 끝나 있어야 한다.
    pub fn parse(bytes: &[u8]) -> Result<Self> {
        let m: UpdateManifest = serde_json::from_slice(bytes)?;
        m.validate()?;
        Ok(m)
    }

    pub fn validate(&self) -> Result<()> {
        let bad = |m: String| Err(UpdateError::InvalidManifest(m));
        Version::parse(&self.version)?;
        if let Some(min) = &self.min_supported_version {
            Version::parse(min)?;
        }
        if self.assets.is_empty() {
            return bad("assets가 비어 있음".into());
        }
        for (i, a) in self.assets.iter().enumerate() {
            if !is_safe_file_name(&a.name) {
                return bad(format!("허용되지 않는 자산 이름: {:?}", a.name));
            }
            if a.sha256.len() != 64
                || hex::decode(&a.sha256).is_none()
                || a.sha256.bytes().any(|c| c.is_ascii_uppercase())
            {
                return bad(format!("{}: sha256은 소문자 16진수 64자여야 함", a.name));
            }
            if self.assets[..i]
                .iter()
                .any(|b| b.name.eq_ignore_ascii_case(&a.name))
            {
                return bad(format!("자산 이름 중복: {}", a.name));
            }
        }
        Ok(())
    }

    pub fn parsed_version(&self) -> Result<Version> {
        Version::parse(&self.version)
    }

    /// 건너뛰기를 허용하지 않는 업데이트인지: `mandatory`이거나 설치 버전이 최소 지원 버전보다 낮을 때.
    pub fn is_mandatory_for(&self, installed: &Version) -> bool {
        self.mandatory
            || self
                .min_supported_version
                .as_deref()
                .and_then(|m| Version::parse(m).ok())
                .is_some_and(|min| installed < &min)
    }

    pub fn asset(&self, kind: PackageKind) -> Option<&ManifestAsset> {
        self.assets.iter().find(|a| kind.matches(&a.name))
    }

    /// 릴리스 워크플로용: 파일들의 크기와 SHA-256을 계산해 매니페스트를 만든다.
    pub fn build(
        version: &str,
        channel: Channel,
        min_supported_version: Option<&str>,
        mandatory: bool,
        files: &[&Path],
    ) -> Result<Self> {
        let mut assets = Vec::with_capacity(files.len());
        for f in files {
            let name = f
                .file_name()
                .and_then(|n| n.to_str())
                .ok_or_else(|| {
                    UpdateError::InvalidManifest(format!(
                        "파일 이름을 읽을 수 없음: {}",
                        f.display()
                    ))
                })?
                .to_owned();
            let (size, sha256) = sha256_file(f)?;
            assets.push(ManifestAsset { name, size, sha256 });
        }
        let m = UpdateManifest {
            version: version.trim_start_matches(['v', 'V']).to_owned(),
            channel,
            min_supported_version: min_supported_version.map(str::to_owned),
            mandatory,
            assets,
        };
        m.validate()?;
        Ok(m)
    }

    pub fn to_json_pretty(&self) -> String {
        // 구조체 직렬화는 실패하지 않는다 (맵 키가 모두 문자열)
        let mut s = serde_json::to_string_pretty(self).unwrap_or_default();
        s.push('\n');
        s
    }
}

/// 경로 구분자·상위 폴더·드라이브 문자·제어 문자·Windows 예약 이름을 막는다.
pub fn is_safe_file_name(name: &str) -> bool {
    if name.is_empty() || name.len() > 200 || name.starts_with('.') || name.ends_with(['.', ' ']) {
        return false;
    }
    if name.chars().any(|c| {
        c.is_control() || matches!(c, '/' | '\\' | ':' | '*' | '?' | '"' | '<' | '>' | '|')
    }) {
        return false;
    }
    let stem = name.split('.').next().unwrap_or("").to_ascii_uppercase();
    let reserved = ["CON", "PRN", "AUX", "NUL"];
    !(reserved.contains(&stem.as_str())
        || ((stem.starts_with("COM") || stem.starts_with("LPT")) && stem.len() == 4))
}

/// 파일 크기와 SHA-256(소문자 16진수).
pub fn sha256_file(path: &Path) -> Result<(u64, String)> {
    let mut f = File::open(path)?;
    let mut h = Sha256::new();
    let mut buf = vec![0u8; 256 * 1024];
    let mut total = 0u64;
    loop {
        let n = f.read(&mut buf)?;
        if n == 0 {
            break;
        }
        h.update(&buf[..n]);
        total += n as u64;
    }
    Ok((total, hex::encode(&h.finalize())))
}

/// 내려받은 파일이 매니페스트와 같은지 (크기 → 해시 순서로 비교).
pub fn verify_file(path: &Path, asset: &ManifestAsset) -> Result<()> {
    let (size, sha) = sha256_file(path)?;
    if size != asset.size {
        return Err(UpdateError::SizeMismatch {
            expected: asset.size,
            actual: size,
        });
    }
    if sha != asset.sha256 {
        return Err(UpdateError::HashMismatch {
            expected: asset.sha256.clone(),
            actual: sha,
        });
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;

    const SHA: &str = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

    fn manifest(name: &str) -> UpdateManifest {
        UpdateManifest {
            version: "1.2.3".into(),
            channel: Channel::Stable,
            min_supported_version: Some("1.0.0".into()),
            mandatory: false,
            assets: vec![ManifestAsset {
                name: name.into(),
                size: 0,
                sha256: SHA.into(),
            }],
        }
    }

    #[test]
    fn roundtrip_json() {
        let m = manifest("Foldy-1.2.3-x64-portable.zip");
        let back = UpdateManifest::parse(m.to_json_pretty().as_bytes()).unwrap();
        assert_eq!(back, m);
        assert!(m.to_json_pretty().contains("\"channel\": \"stable\""));
    }

    #[test]
    fn rejects_path_traversal_names() {
        for n in [
            "../evil.msi",
            "a\\b.msi",
            "C:x.msi",
            ".hidden",
            "NUL.msi",
            "com1.zip",
            "x.msi.",
        ] {
            assert!(manifest(n).validate().is_err(), "{n:?}");
        }
        assert!(manifest("Foldy-1.2.3-x64.msi").validate().is_ok());
    }

    #[test]
    fn rejects_bad_hash_and_version() {
        let mut m = manifest("a.msi");
        m.assets[0].sha256 = SHA.to_uppercase();
        assert!(m.validate().is_err());
        let mut m = manifest("a.msi");
        m.version = "1.2".into();
        assert!(m.validate().is_err());
    }

    #[test]
    fn mandatory_rules() {
        let mut m = manifest("a.msi");
        assert!(!m.is_mandatory_for(&Version::new(1, 1, 0)));
        assert!(
            m.is_mandatory_for(&Version::new(0, 9, 9)),
            "최소 지원 버전보다 낮으면 필수"
        );
        m.mandatory = true;
        assert!(m.is_mandatory_for(&Version::new(1, 1, 0)));
    }

    #[test]
    fn picks_asset_by_kind() {
        let mut m = manifest("Foldy-1.2.3-x64.msi");
        m.assets.push(ManifestAsset {
            name: "Foldy-1.2.3-x64-portable.zip".into(),
            size: 0,
            sha256: SHA.into(),
        });
        assert_eq!(
            m.asset(PackageKind::Msi).unwrap().name,
            "Foldy-1.2.3-x64.msi"
        );
        assert_eq!(
            m.asset(PackageKind::Portable).unwrap().name,
            "Foldy-1.2.3-x64-portable.zip"
        );
    }

    #[test]
    fn channel_parse() {
        assert_eq!(Channel::parse("Beta"), Some(Channel::Beta));
        assert_eq!(Channel::parse("nightly"), None);
    }
}
