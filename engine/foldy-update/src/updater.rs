//! 한 주기의 업데이트 흐름 (foldy-index 서비스가 실행 1분 후 + 6시간마다 부른다).
//!
//! 확인 → 버전 비교 → update.json 서명 검증 → (건너뛴 버전이면 필수 업데이트가 아닌 한 멈춤)
//! → 설치 파일 이어받기 → 크기·SHA-256 → Authenticode(스텁) → [`Outcome::Ready`].
//! ETag는 주기가 끝까지 성공했을 때만 저장한다: 다운로드가 실패했는데 다음 확인이 304로 끝나면 안 되므로.

use std::path::{Path, PathBuf};
use std::time::{SystemTime, UNIX_EPOCH};

use ed25519_dalek::VerifyingKey;

use crate::download::download_with_retries;
use crate::error::{Result, UpdateError};
use crate::github::{self, CheckResult, ReleaseInfo};
use crate::http::HttpClient;
use crate::install::{AuthenticodePolicy, verify_authenticode};
use crate::manifest::{Channel, PackageKind, UpdateManifest, is_safe_file_name, verify_file};
use crate::sign;
use crate::state::UpdateState;
use crate::version::Version;
use crate::{MANIFEST_ASSET, SIGNATURE_ASSET};

const MAX_MANIFEST: usize = 256 * 1024;
const MAX_SIGNATURE: usize = 4 * 1024;

pub struct UpdaterConfig {
    pub api_base: String,
    pub repo: String,
    pub channel: Channel,
    pub current_version: Version,
    /// `%ProgramData%\Foldy\Updates` (Administrators·SYSTEM만 쓰기 가능).
    pub updates_dir: PathBuf,
    pub package: PackageKind,
    pub verifying_key: VerifyingKey,
    pub authenticode: AuthenticodePolicy,
    pub download_attempts: u32,
}

/// 확인만 했을 때의 판단 (순수 함수).
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum Decision {
    UpToDate,
    /// 더 높은 버전이지만 사용자가 건너뛴 버전 (필수 여부는 매니페스트를 봐야 안다).
    Skipped(Version),
    Available(Version),
}

pub fn decide(
    current: &Version,
    channel: Channel,
    release: &ReleaseInfo,
    state: &UpdateState,
) -> Decision {
    let v = &release.version;
    // stable 채널은 프리릴리스 버전을 받지 않는다
    if v <= current || (channel == Channel::Stable && (release.prerelease || v.is_prerelease())) {
        Decision::UpToDate
    } else if state.is_skipped(v) {
        Decision::Skipped(v.clone())
    } else {
        Decision::Available(v.clone())
    }
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct PreparedUpdate {
    pub version: Version,
    pub path: PathBuf,
    pub mandatory: bool,
    pub notes: String,
    pub manifest: UpdateManifest,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum Outcome {
    NotModified,
    UpToDate,
    Skipped(Version),
    Ready(PreparedUpdate),
}

pub struct Updater<C: HttpClient> {
    pub config: UpdaterConfig,
    pub client: C,
}

impl<C: HttpClient> Updater<C> {
    pub fn new(config: UpdaterConfig, client: C) -> Self {
        Updater { config, client }
    }

    /// 한 주기. `state`는 호출자가 저장한다.
    pub fn run_once(
        &self,
        state: &mut UpdateState,
        progress: &mut dyn FnMut(u64, u64),
    ) -> Result<Outcome> {
        let cfg = &self.config;
        let channel_name = format!("{:?}", cfg.channel);
        if state.etag_channel.as_deref() != Some(channel_name.as_str()) {
            state.etag = None;
        }
        let checked = github::check(
            &self.client,
            &cfg.api_base,
            &cfg.repo,
            cfg.channel,
            state.etag.as_deref(),
        )?;
        let (etag, release) = match checked {
            CheckResult::NotModified => {
                state.last_check_unix = Some(now_unix());
                return Ok(Outcome::NotModified);
            }
            CheckResult::Fetched { etag, release } => (etag, release),
        };
        let outcome = match release {
            None => Outcome::UpToDate,
            Some(release) => match decide(&cfg.current_version, cfg.channel, &release, state) {
                Decision::UpToDate => Outcome::UpToDate,
                Decision::Skipped(v) => {
                    // 건너뛴 버전이라도 필수 업데이트면 설치한다 → 작은 매니페스트만 확인
                    let manifest = self.fetch_manifest(&release)?;
                    if manifest.is_mandatory_for(&cfg.current_version) {
                        Outcome::Ready(self.download_package(&release, manifest, progress)?)
                    } else {
                        Outcome::Skipped(v)
                    }
                }
                Decision::Available(_) => {
                    let manifest = self.fetch_manifest(&release)?;
                    Outcome::Ready(self.download_package(&release, manifest, progress)?)
                }
            },
        };
        state.etag = etag;
        state.etag_channel = Some(channel_name);
        state.last_check_unix = Some(now_unix());
        Ok(outcome)
    }

    /// update.json + .sig를 받아 내장 공개키로 검사하고, 릴리스와 맞는지 본다.
    pub fn fetch_manifest(&self, release: &ReleaseInfo) -> Result<UpdateManifest> {
        let m_asset = release
            .asset(MANIFEST_ASSET)
            .ok_or_else(|| UpdateError::MissingAsset(MANIFEST_ASSET.into()))?;
        let s_asset = release
            .asset(SIGNATURE_ASSET)
            .ok_or_else(|| UpdateError::MissingAsset(SIGNATURE_ASSET.into()))?;
        let bytes = self.fetch_small(&m_asset.download_url, MAX_MANIFEST)?;
        let sig = self.fetch_small(&s_asset.download_url, MAX_SIGNATURE)?;
        let sig = String::from_utf8(sig).map_err(|_| UpdateError::BadSignature)?;
        // 서명 검사가 먼저. 통과하기 전에는 내용을 해석하지 않는다.
        sign::verify(&self.config.verifying_key, &bytes, &sig)?;
        let manifest = UpdateManifest::parse(&bytes)?;
        if manifest.parsed_version()? != release.version {
            return Err(UpdateError::InvalidManifest(format!(
                "update.json 버전 {}이 태그 {}와 다름",
                manifest.version, release.tag
            )));
        }
        if manifest.channel == Channel::Beta && self.config.channel == Channel::Stable {
            return Err(UpdateError::InvalidManifest(
                "stable 채널에 beta 매니페스트".into(),
            ));
        }
        Ok(manifest)
    }

    fn fetch_small(&self, url: &str, limit: usize) -> Result<Vec<u8>> {
        let resp = self
            .client
            .get(url, &[("Accept", "application/octet-stream")])?;
        if resp.status != 200 {
            return Err(UpdateError::HttpStatus(resp.status));
        }
        resp.read_body_limited(limit)
    }

    /// 설치 파일을 업데이트 폴더에 받아 크기·해시·Authenticode를 확인한다.
    pub fn download_package(
        &self,
        release: &ReleaseInfo,
        manifest: UpdateManifest,
        progress: &mut dyn FnMut(u64, u64),
    ) -> Result<PreparedUpdate> {
        let cfg = &self.config;
        let asset = manifest
            .asset(cfg.package)
            .ok_or_else(|| UpdateError::MissingAsset(format!("{:?} 패키지", cfg.package)))?
            .clone();
        // validate()에서 이미 검사했지만 파일 이름으로 쓰기 직전에 한 번 더
        if !is_safe_file_name(&asset.name) {
            return Err(UpdateError::PathNotAllowed(asset.name));
        }
        let gh = release
            .asset(&asset.name)
            .ok_or_else(|| UpdateError::MissingAsset(asset.name.clone()))?;
        if gh.size != asset.size {
            return Err(UpdateError::SizeMismatch {
                expected: asset.size,
                actual: gh.size,
            });
        }
        std::fs::create_dir_all(&cfg.updates_dir)?;
        let dest = cfg.updates_dir.join(&asset.name);
        // 이미 받아 둔 파일이 맞으면 다시 받지 않는다
        if !(dest.is_file() && verify_file(&dest, &asset).is_ok()) {
            download_with_retries(
                &self.client,
                &gh.download_url,
                &dest,
                asset.size,
                cfg.download_attempts,
                progress,
            )?;
            if let Err(e) = verify_file(&dest, &asset) {
                std::fs::remove_file(&dest).ok();
                return Err(e);
            }
        }
        verify_authenticode(&dest, &cfg.authenticode)?;
        Ok(PreparedUpdate {
            version: release.version.clone(),
            mandatory: manifest.is_mandatory_for(&cfg.current_version),
            notes: release.notes.clone(),
            path: dest,
            manifest,
        })
    }
}

fn now_unix() -> u64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map(|d| d.as_secs())
        .unwrap_or(0)
}

/// 기본 업데이트 폴더: `%ProgramData%\Foldy\Updates`.
pub fn default_updates_dir() -> PathBuf {
    let base = std::env::var_os("ProgramData")
        .map(PathBuf::from)
        .unwrap_or_else(|| PathBuf::from(r"C:\ProgramData"));
    base.join("Foldy").join("Updates")
}

/// 상태 파일 경로.
pub fn state_path(updates_dir: &Path) -> PathBuf {
    updates_dir.join("state.json")
}

#[cfg(test)]
mod tests {
    use super::*;

    fn release(tag: &str, pre: bool) -> ReleaseInfo {
        ReleaseInfo {
            tag: tag.into(),
            version: Version::parse(tag).unwrap(),
            prerelease: pre,
            notes: String::new(),
            html_url: String::new(),
            assets: Vec::new(),
        }
    }

    #[test]
    fn decide_rules() {
        let cur = Version::new(1, 2, 0);
        let mut st = UpdateState::default();
        assert_eq!(
            decide(&cur, Channel::Stable, &release("v1.2.0", false), &st),
            Decision::UpToDate
        );
        assert_eq!(
            decide(&cur, Channel::Stable, &release("v1.1.9", false), &st),
            Decision::UpToDate
        );
        assert_eq!(
            decide(&cur, Channel::Stable, &release("v1.3.0", false), &st),
            Decision::Available(Version::new(1, 3, 0))
        );
        assert_eq!(
            decide(&cur, Channel::Stable, &release("v1.3.0-beta.1", true), &st),
            Decision::UpToDate
        );
        assert!(matches!(
            decide(&cur, Channel::Beta, &release("v1.3.0-beta.1", true), &st),
            Decision::Available(_)
        ));
        st.skip(&Version::new(1, 3, 0));
        assert_eq!(
            decide(&cur, Channel::Stable, &release("v1.3.0", false), &st),
            Decision::Skipped(Version::new(1, 3, 0))
        );
    }
}
