//! GitHub Releases 확인 (설계서 10.5 앱에서 확인하기).
//!
//! stable: `GET /repos/{repo}/releases/latest` (GitHub가 pre-release·draft를 이미 뺀다)
//! beta:   `GET /repos/{repo}/releases` 목록에서 draft를 빼고 pre-release 포함 가장 높은 SemVer
//!
//! 이전 응답의 ETag를 `If-None-Match`로 보내 바뀐 게 없으면 304로 끝낸다
//! (인증 없는 호출의 IP당 시간 제한을 아끼려는 것. GitHub는 304를 제한 횟수에 세지 않는다).

use serde::Deserialize;

use crate::error::{Result, UpdateError};
use crate::http::HttpClient;
use crate::manifest::Channel;
use crate::version::Version;

/// 릴리스 목록 JSON은 크지 않다. 이보다 크면 비정상으로 본다.
const MAX_API_BODY: usize = 4 * 1024 * 1024;

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ReleaseAsset {
    pub name: String,
    pub size: u64,
    pub download_url: String,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ReleaseInfo {
    pub tag: String,
    pub version: Version,
    pub prerelease: bool,
    /// Release 본문(변경 내역). 앱의 "새 버전" 창에 그대로 보여준다.
    pub notes: String,
    pub html_url: String,
    pub assets: Vec<ReleaseAsset>,
}

impl ReleaseInfo {
    pub fn asset(&self, name: &str) -> Option<&ReleaseAsset> {
        self.assets.iter().find(|a| a.name == name)
    }
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum CheckResult {
    /// 304: 지난번과 같음.
    NotModified,
    /// 새 응답. `release`가 `None`이면 해당 채널에 릴리스가 없다.
    Fetched {
        etag: Option<String>,
        release: Option<ReleaseInfo>,
    },
}

#[derive(Deserialize)]
struct GhRelease {
    tag_name: String,
    #[serde(default)]
    prerelease: bool,
    #[serde(default)]
    draft: bool,
    #[serde(default)]
    body: Option<String>,
    #[serde(default)]
    html_url: Option<String>,
    #[serde(default)]
    assets: Vec<GhAsset>,
}

#[derive(Deserialize)]
struct GhAsset {
    name: String,
    size: u64,
    browser_download_url: String,
}

impl GhRelease {
    fn into_info(self) -> Result<ReleaseInfo> {
        Ok(ReleaseInfo {
            version: Version::parse(&self.tag_name)?,
            tag: self.tag_name,
            prerelease: self.prerelease,
            notes: self.body.unwrap_or_default(),
            html_url: self.html_url.unwrap_or_default(),
            assets: self
                .assets
                .into_iter()
                .map(|a| ReleaseAsset {
                    name: a.name,
                    size: a.size,
                    download_url: a.browser_download_url,
                })
                .collect(),
        })
    }
}

/// `owner/name` 형식만 받는다 (URL 경로 주입 방지).
pub fn validate_repo(repo: &str) -> Result<()> {
    let ok_part = |p: &str| {
        !p.is_empty()
            && p != "."
            && p != ".."
            && p.bytes()
                .all(|c| c.is_ascii_alphanumeric() || b"-_.".contains(&c))
    };
    match repo.split_once('/') {
        Some((o, n)) if ok_part(o) && ok_part(n) => Ok(()),
        _ => Err(UpdateError::Network(format!(
            "저장소는 owner/name 형식이어야 함: {repo:?}"
        ))),
    }
}

pub fn releases_url(api_base: &str, repo: &str, channel: Channel) -> String {
    let base = api_base.trim_end_matches('/');
    match channel {
        Channel::Stable => format!("{base}/repos/{repo}/releases/latest"),
        Channel::Beta => format!("{base}/repos/{repo}/releases?per_page=30"),
    }
}

/// 한 번 확인한다. `etag`는 지난번 응답의 ETag.
pub fn check<C: HttpClient + ?Sized>(
    client: &C,
    api_base: &str,
    repo: &str,
    channel: Channel,
    etag: Option<&str>,
) -> Result<CheckResult> {
    validate_repo(repo)?;
    let url = releases_url(api_base, repo, channel);
    let mut headers = vec![
        ("Accept", "application/vnd.github+json"),
        ("X-GitHub-Api-Version", "2022-11-28"),
    ];
    if let Some(e) = etag {
        headers.push(("If-None-Match", e));
    }
    let resp = client.get(&url, &headers)?;
    match resp.status {
        304 => Ok(CheckResult::NotModified),
        // releases/latest는 정식 릴리스가 하나도 없으면 404
        404 if channel == Channel::Stable => Ok(CheckResult::Fetched {
            etag: None,
            release: None,
        }),
        200 => {
            let etag = resp.header("etag").map(str::to_owned);
            let body = resp.read_body_limited(MAX_API_BODY)?;
            let release = match channel {
                Channel::Stable => parse_latest(&body)?,
                Channel::Beta => parse_list_best(&body)?,
            };
            Ok(CheckResult::Fetched { etag, release })
        }
        s => Err(UpdateError::HttpStatus(s)),
    }
}

/// `releases/latest` 응답. 혹시 draft·pre-release가 오면 무시한다.
pub fn parse_latest(body: &[u8]) -> Result<Option<ReleaseInfo>> {
    let r: GhRelease = serde_json::from_slice(body)?;
    if r.draft || r.prerelease {
        return Ok(None);
    }
    r.into_info().map(Some)
}

/// `releases` 목록에서 draft가 아닌 것 중 SemVer가 가장 높은 릴리스. 태그가 SemVer가 아니면 건너뛴다.
pub fn parse_list_best(body: &[u8]) -> Result<Option<ReleaseInfo>> {
    let list: Vec<GhRelease> = serde_json::from_slice(body)?;
    Ok(list
        .into_iter()
        .filter(|r| !r.draft)
        .filter_map(|r| r.into_info().ok())
        .max_by(|a, b| a.version.cmp(&b.version)))
}

#[cfg(test)]
mod tests {
    use super::*;

    const LIST: &str = r#"[
        {"tag_name":"v1.3.0-beta.1","prerelease":true,"draft":false,"body":"베타","html_url":"h","assets":[]},
        {"tag_name":"v1.4.0","prerelease":false,"draft":true,"assets":[]},
        {"tag_name":"nightly","prerelease":true,"assets":[]},
        {"tag_name":"v1.2.3","prerelease":false,"assets":[{"name":"update.json","size":10,"browser_download_url":"http://x/u"}]}
    ]"#;

    #[test]
    fn beta_picks_highest_non_draft() {
        let r = parse_list_best(LIST.as_bytes()).unwrap().unwrap();
        assert_eq!(r.tag, "v1.3.0-beta.1");
        assert!(r.prerelease);
        assert_eq!(r.notes, "베타");
    }

    #[test]
    fn latest_parses_assets() {
        let body = r#"{"tag_name":"v1.2.3","prerelease":false,"body":"변경","assets":[{"name":"update.json","size":10,"browser_download_url":"http://x/u"}]}"#;
        let r = parse_latest(body.as_bytes()).unwrap().unwrap();
        assert_eq!(r.version, Version::new(1, 2, 3));
        assert_eq!(r.asset("update.json").unwrap().download_url, "http://x/u");
    }

    #[test]
    fn repo_validation() {
        validate_repo("chadolkr/Foldy").unwrap();
        for bad in ["", "a", "a/b/c", "../x", "a/..", "a b/c", "a/b?x=1"] {
            assert!(validate_repo(bad).is_err(), "{bad}");
        }
    }

    #[test]
    fn urls() {
        assert_eq!(
            releases_url("https://api.github.com/", "a/b", Channel::Stable),
            "https://api.github.com/repos/a/b/releases/latest"
        );
        assert!(
            releases_url("http://127.0.0.1:1", "a/b", Channel::Beta)
                .ends_with("/repos/a/b/releases?per_page=30")
        );
    }
}
