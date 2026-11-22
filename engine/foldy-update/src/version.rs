//! SemVer 2.0.0 비교 (설계서 10.5: 태그 버전이 설치 버전보다 높을 때만 업데이트).
//!
//! 태그는 `v1.2.3` 형식이므로 앞의 `v`는 떼고 읽는다. 빌드 메타데이터(`+...`)는 순서에 영향이 없다.

use std::cmp::Ordering;
use std::fmt;
use std::str::FromStr;

use crate::error::UpdateError;

#[derive(Debug, Clone, PartialEq, Eq, Hash)]
enum PreId {
    Num(u64),
    Alpha(String),
}

impl Ord for PreId {
    fn cmp(&self, other: &Self) -> Ordering {
        match (self, other) {
            (PreId::Num(a), PreId::Num(b)) => a.cmp(b),
            // 숫자 식별자는 영문 식별자보다 항상 낮다 (SemVer 11.4.3)
            (PreId::Num(_), PreId::Alpha(_)) => Ordering::Less,
            (PreId::Alpha(_), PreId::Num(_)) => Ordering::Greater,
            (PreId::Alpha(a), PreId::Alpha(b)) => a.cmp(b),
        }
    }
}

impl PartialOrd for PreId {
    fn partial_cmp(&self, other: &Self) -> Option<Ordering> {
        Some(self.cmp(other))
    }
}

#[derive(Debug, Clone)]
pub struct Version {
    pub major: u64,
    pub minor: u64,
    pub patch: u64,
    pre: Vec<PreId>,
    build: String,
}

impl Version {
    pub const fn new(major: u64, minor: u64, patch: u64) -> Self {
        Version {
            major,
            minor,
            patch,
            pre: Vec::new(),
            build: String::new(),
        }
    }

    /// `1.2.3`, `v1.2.3`, `1.2.3-beta.1+sha.abc` 를 받는다.
    pub fn parse(s: &str) -> Result<Self, UpdateError> {
        let bad = || UpdateError::InvalidVersion(s.to_owned());
        let t = s.trim();
        let t = t.strip_prefix(['v', 'V']).unwrap_or(t);
        let (rest, build) = match t.split_once('+') {
            Some((r, b)) => {
                if b.is_empty() || !b.split('.').all(valid_ident) {
                    return Err(bad());
                }
                (r, b.to_owned())
            }
            None => (t, String::new()),
        };
        let (core, pre) = match rest.split_once('-') {
            Some((c, p)) => (c, Some(p)),
            None => (rest, None),
        };
        let mut parts = core.split('.');
        let mut num = || -> Result<u64, UpdateError> {
            let p = parts.next().ok_or_else(bad)?;
            parse_numeric(p).ok_or_else(bad)
        };
        let (major, minor, patch) = (num()?, num()?, num()?);
        if parts.next().is_some() {
            return Err(bad());
        }
        let pre = match pre {
            None => Vec::new(),
            Some(p) => {
                if p.is_empty() {
                    return Err(bad());
                }
                p.split('.')
                    .map(|id| {
                        if !valid_ident(id) {
                            None
                        } else if id.bytes().all(|c| c.is_ascii_digit()) {
                            parse_numeric(id).map(PreId::Num)
                        } else {
                            Some(PreId::Alpha(id.to_owned()))
                        }
                    })
                    .collect::<Option<Vec<_>>>()
                    .ok_or_else(bad)?
            }
        };
        Ok(Version {
            major,
            minor,
            patch,
            pre,
            build,
        })
    }

    /// 프리릴리스(`-beta.1` 등)인지. 베타 채널에서만 받는다.
    pub fn is_prerelease(&self) -> bool {
        !self.pre.is_empty()
    }
}

/// 숫자 식별자: 0이 아닌 수는 앞자리 0 금지 (SemVer 2).
fn parse_numeric(s: &str) -> Option<u64> {
    if s.is_empty() || !s.bytes().all(|c| c.is_ascii_digit()) || (s.len() > 1 && s.starts_with('0'))
    {
        return None;
    }
    s.parse().ok()
}

fn valid_ident(s: &str) -> bool {
    !s.is_empty() && s.bytes().all(|c| c.is_ascii_alphanumeric() || c == b'-')
}

impl Ord for Version {
    fn cmp(&self, other: &Self) -> Ordering {
        (self.major, self.minor, self.patch)
            .cmp(&(other.major, other.minor, other.patch))
            .then_with(|| match (self.pre.is_empty(), other.pre.is_empty()) {
                (true, true) => Ordering::Equal,
                // 프리릴리스는 같은 정식 버전보다 낮다
                (true, false) => Ordering::Greater,
                (false, true) => Ordering::Less,
                (false, false) => self.pre.cmp(&other.pre),
            })
    }
}

impl PartialOrd for Version {
    fn partial_cmp(&self, other: &Self) -> Option<Ordering> {
        Some(self.cmp(other))
    }
}

/// 같음도 순서와 맞춘다: 빌드 메타데이터는 무시.
impl PartialEq for Version {
    fn eq(&self, other: &Self) -> bool {
        self.cmp(other) == Ordering::Equal
    }
}

impl Eq for Version {}

impl fmt::Display for Version {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(f, "{}.{}.{}", self.major, self.minor, self.patch)?;
        for (i, id) in self.pre.iter().enumerate() {
            f.write_str(if i == 0 { "-" } else { "." })?;
            match id {
                PreId::Num(n) => write!(f, "{n}")?,
                PreId::Alpha(a) => f.write_str(a)?,
            }
        }
        if !self.build.is_empty() {
            write!(f, "+{}", self.build)?;
        }
        Ok(())
    }
}

impl FromStr for Version {
    type Err = UpdateError;

    fn from_str(s: &str) -> Result<Self, Self::Err> {
        Version::parse(s)
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn v(s: &str) -> Version {
        Version::parse(s).unwrap()
    }

    #[test]
    fn parses_tags_with_v_prefix() {
        assert_eq!(v("v1.2.3"), Version::new(1, 2, 3));
        assert_eq!(
            v("1.2.3-beta.1+build.5").to_string(),
            "1.2.3-beta.1+build.5"
        );
        assert!(v("1.0.0-rc.1").is_prerelease());
        assert!(!v("1.0.0").is_prerelease());
    }

    #[test]
    fn rejects_invalid() {
        for s in [
            "",
            "1",
            "1.2",
            "1.2.3.4",
            "01.2.3",
            "1.2.3-",
            "1.2.3-01",
            "1.2.x",
            "1.2.3+",
            "1.2.3-a..b",
            "-1.2.3",
        ] {
            assert!(Version::parse(s).is_err(), "{s:?}는 거부해야 함");
        }
    }

    #[test]
    fn semver_spec_precedence_chain() {
        // SemVer 2.0.0 11장의 예시 순서
        let chain = [
            "1.0.0-alpha",
            "1.0.0-alpha.1",
            "1.0.0-alpha.beta",
            "1.0.0-beta",
            "1.0.0-beta.2",
            "1.0.0-beta.11",
            "1.0.0-rc.1",
            "1.0.0",
            "1.0.1",
            "1.1.0",
            "2.0.0",
            "10.0.0",
        ];
        for w in chain.windows(2) {
            assert!(v(w[0]) < v(w[1]), "{} < {}", w[0], w[1]);
        }
    }

    #[test]
    fn build_metadata_is_ignored() {
        assert_eq!(v("1.2.3+a"), v("1.2.3+b"));
        assert_eq!(v("1.2.3+a").cmp(&v("1.2.3")), Ordering::Equal);
    }

    #[test]
    fn numeric_compare_not_lexical() {
        assert!(v("1.10.0") > v("1.9.0"));
        assert!(v("0.10.0") > v("0.9.44")); // 부(MINOR)가 두 자리가 되어도 숫자로 (docs/versioning.md)
        assert!(v("0.10.0-beta.1") > v("0.9.44"));
        assert!(v("0.10.0-beta.2") < v("0.10.0"));
        assert!(v("1.0.0-beta.10") > v("1.0.0-beta.9"));
    }
}
