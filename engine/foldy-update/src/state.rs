//! 업데이트 상태 파일 (`%ProgramData%\Foldy\Updates\state.json`): ETag, 마지막 확인 시간,
//! 건너뛴 버전, 설치 기록과 되돌리기 판단 (설계서 10.5 실패 대비).
//!
//! 되돌리기 규칙: 직전 버전 MSI를 하나 보관한다. 새 버전이 설치 후 **두 번 연속** 정상 시작하지
//! 못하면 직전 버전으로 되돌리고, 실패한 버전을 건너뛸 버전으로 기록한다. 판단은 순수 함수라 시험이 쉽다.

use std::collections::BTreeSet;
use std::fs;
use std::path::{Path, PathBuf};

use serde::{Deserialize, Serialize};

use crate::error::Result;
use crate::version::Version;

/// 몇 번 연속 시작에 실패하면 되돌릴지.
pub const MAX_FAILED_STARTS: u32 = 2;

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct InstallRecord {
    pub installed_version: String,
    pub previous_version: Option<String>,
    /// 업데이트 폴더에 보관한 직전 버전 MSI.
    pub previous_msi: Option<PathBuf>,
    /// 설치 후 연속 시작 실패 횟수.
    pub failed_starts: u32,
    /// 한 번이라도 정상 시작했으면 참 (이후로는 되돌리지 않는다).
    pub confirmed: bool,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum StartOutcome {
    /// 정상 시작: 서비스 Hello 응답 + Foldy.exe가 60초 안에 죽지 않음.
    Healthy,
    /// Hello 응답 없음 또는 60초 안에 크래시.
    Failed,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum RollbackDecision {
    /// 할 일 없음.
    None,
    /// 실패했지만 아직 기준 미만. 다음 시작을 지켜본다.
    Watch { failed_starts: u32 },
    /// 직전 MSI로 되돌린다. `skip`은 건너뛸 버전으로 이미 기록됨.
    Rollback {
        msi: PathBuf,
        to_version: String,
        skip: String,
    },
    /// 기준을 넘었지만 되돌릴 MSI가 없다 (첫 설치 등). 경고만 남긴다.
    NoRollbackAvailable { failed_starts: u32 },
}

#[derive(Debug, Clone, Default, PartialEq, Eq, Serialize, Deserialize)]
pub struct UpdateState {
    #[serde(default)]
    pub etag: Option<String>,
    /// ETag를 받은 채널 (채널이 바뀌면 ETag를 버린다).
    #[serde(default)]
    pub etag_channel: Option<String>,
    /// 마지막으로 확인에 성공한 시각 (Unix 초). 장애는 사용자에게 이 값으로만 보인다.
    #[serde(default)]
    pub last_check_unix: Option<u64>,
    #[serde(default)]
    pub skipped_versions: BTreeSet<String>,
    #[serde(default)]
    pub install: Option<InstallRecord>,
}

impl UpdateState {
    pub fn load(path: &Path) -> UpdateState {
        // 깨졌거나 없으면 새로 시작 (건너뛴 버전만 잃는다)
        fs::read(path)
            .ok()
            .and_then(|b| serde_json::from_slice(&b).ok())
            .unwrap_or_default()
    }

    /// 임시 파일에 쓰고 이름을 바꿔 원자적으로 저장한다.
    pub fn save(&self, path: &Path) -> Result<()> {
        if let Some(dir) = path.parent() {
            fs::create_dir_all(dir)?;
        }
        let tmp = path.with_extension("json.tmp");
        fs::write(&tmp, serde_json::to_vec_pretty(self)?)?;
        fs::rename(&tmp, path)?;
        Ok(())
    }

    pub fn is_skipped(&self, v: &Version) -> bool {
        self.skipped_versions
            .iter()
            .any(|s| Version::parse(s).is_ok_and(|x| &x == v))
    }

    pub fn skip(&mut self, v: &Version) {
        if !self.is_skipped(v) {
            self.skipped_versions.insert(v.to_string());
        }
    }

    pub fn unskip(&mut self, v: &Version) {
        self.skipped_versions
            .retain(|s| !Version::parse(s).is_ok_and(|x| &x == v));
    }

    /// MSI 설치를 시작하기 직전에 부른다.
    pub fn record_install(
        &mut self,
        new_version: &Version,
        previous_version: Option<&Version>,
        previous_msi: Option<PathBuf>,
    ) {
        self.install = Some(InstallRecord {
            installed_version: new_version.to_string(),
            previous_version: previous_version.map(ToString::to_string),
            previous_msi,
            failed_starts: 0,
            confirmed: false,
        });
    }

    /// 시작 결과를 기록하고 되돌릴지 정한다.
    pub fn record_start(&mut self, outcome: StartOutcome) -> RollbackDecision {
        let Some(rec) = self.install.as_mut() else {
            return RollbackDecision::None;
        };
        if rec.confirmed {
            return RollbackDecision::None;
        }
        match outcome {
            StartOutcome::Healthy => {
                rec.confirmed = true;
                rec.failed_starts = 0;
                RollbackDecision::None
            }
            StartOutcome::Failed => {
                rec.failed_starts += 1;
                if rec.failed_starts < MAX_FAILED_STARTS {
                    return RollbackDecision::Watch {
                        failed_starts: rec.failed_starts,
                    };
                }
                match (rec.previous_msi.clone(), rec.previous_version.clone()) {
                    (Some(msi), Some(to_version)) => {
                        let skip = rec.installed_version.clone();
                        if let Ok(v) = Version::parse(&skip) {
                            self.skip(&v);
                        }
                        // 되돌린 뒤에는 직전 버전이 "설치됨"이고 이미 검증된 버전이다
                        self.install = Some(InstallRecord {
                            installed_version: to_version.clone(),
                            previous_version: None,
                            previous_msi: None,
                            failed_starts: 0,
                            confirmed: true,
                        });
                        RollbackDecision::Rollback {
                            msi,
                            to_version,
                            skip,
                        }
                    }
                    _ => RollbackDecision::NoRollbackAvailable {
                        failed_starts: rec.failed_starts,
                    },
                }
            }
        }
    }
}

/// 업데이트 폴더 정리: `keep`에 없는 MSI·zip·`.part`를 지울 목록으로 돌려준다 (직전 버전 하나 + 새 버전만 남김).
pub fn files_to_prune(entries: &[PathBuf], keep: &[&Path]) -> Vec<PathBuf> {
    entries
        .iter()
        .filter(|p| {
            let name = p
                .file_name()
                .and_then(|n| n.to_str())
                .unwrap_or("")
                .to_ascii_lowercase();
            (name.ends_with(".msi") || name.ends_with(".zip") || name.ends_with(".part"))
                && !keep.iter().any(|k| k == p)
        })
        .cloned()
        .collect()
}

#[cfg(test)]
mod tests {
    use super::*;

    fn v(s: &str) -> Version {
        Version::parse(s).unwrap()
    }

    fn installed() -> UpdateState {
        let mut s = UpdateState::default();
        s.record_install(
            &v("1.3.0"),
            Some(&v("1.2.0")),
            Some(PathBuf::from(
                r"C:\ProgramData\Foldy\Updates\Foldy-1.2.0-x64.msi",
            )),
        );
        s
    }

    #[test]
    fn two_consecutive_failures_roll_back_and_skip() {
        let mut s = installed();
        assert_eq!(
            s.record_start(StartOutcome::Failed),
            RollbackDecision::Watch { failed_starts: 1 }
        );
        let d = s.record_start(StartOutcome::Failed);
        assert_eq!(
            d,
            RollbackDecision::Rollback {
                msi: PathBuf::from(r"C:\ProgramData\Foldy\Updates\Foldy-1.2.0-x64.msi"),
                to_version: "1.2.0".into(),
                skip: "1.3.0".into()
            }
        );
        assert!(s.is_skipped(&v("1.3.0")));
        assert_eq!(s.install.as_ref().unwrap().installed_version, "1.2.0");
        // 되돌린 버전은 확인된 버전 → 더 실패해도 다시 되돌리지 않는다
        assert_eq!(s.record_start(StartOutcome::Failed), RollbackDecision::None);
    }

    #[test]
    fn healthy_start_resets_and_confirms() {
        let mut s = installed();
        s.record_start(StartOutcome::Failed);
        assert_eq!(
            s.record_start(StartOutcome::Healthy),
            RollbackDecision::None
        );
        // 한 번 정상 시작한 뒤의 실패는 업데이트 탓으로 보지 않는다
        assert_eq!(s.record_start(StartOutcome::Failed), RollbackDecision::None);
        assert_eq!(s.record_start(StartOutcome::Failed), RollbackDecision::None);
        assert!(!s.is_skipped(&v("1.3.0")));
    }

    #[test]
    fn non_consecutive_failures_do_not_roll_back() {
        let mut s = installed();
        s.record_start(StartOutcome::Failed);
        s.record_start(StartOutcome::Healthy);
        assert_eq!(s.install.as_ref().unwrap().failed_starts, 0);
    }

    #[test]
    fn no_previous_msi_means_no_rollback() {
        let mut s = UpdateState::default();
        s.record_install(&v("1.0.0"), None, None);
        s.record_start(StartOutcome::Failed);
        assert_eq!(
            s.record_start(StartOutcome::Failed),
            RollbackDecision::NoRollbackAvailable { failed_starts: 2 }
        );
    }

    #[test]
    fn no_install_record_is_noop() {
        assert_eq!(
            UpdateState::default().record_start(StartOutcome::Failed),
            RollbackDecision::None
        );
    }

    #[test]
    fn skip_list_uses_semver_equality() {
        let mut s = UpdateState::default();
        s.skip(&v("v1.2.3"));
        s.skip(&v("1.2.3+build"));
        assert_eq!(s.skipped_versions.len(), 1);
        assert!(s.is_skipped(&v("1.2.3")));
        assert!(!s.is_skipped(&v("1.2.4")));
        s.unskip(&v("1.2.3"));
        assert!(s.skipped_versions.is_empty());
    }

    #[test]
    fn save_load_roundtrip() {
        let dir = std::env::temp_dir().join(format!("foldy-update-state-{}", std::process::id()));
        let path = dir.join("state.json");
        let mut s = installed();
        s.etag = Some("\"abc\"".into());
        s.skip(&v("2.0.0"));
        s.save(&path).unwrap();
        assert_eq!(UpdateState::load(&path), s);
        fs::write(&path, b"{broken").unwrap();
        assert_eq!(UpdateState::load(&path), UpdateState::default());
        fs::remove_dir_all(&dir).ok();
    }

    #[test]
    fn prune_keeps_current_and_previous() {
        let d = PathBuf::from(r"C:\u");
        let entries = vec![
            d.join("Foldy-1.1.0-x64.msi"),
            d.join("Foldy-1.2.0-x64.msi"),
            d.join("Foldy-1.3.0-x64.msi"),
            d.join("x.msi.part"),
            d.join("state.json"),
        ];
        let keep_a = d.join("Foldy-1.2.0-x64.msi");
        let keep_b = d.join("Foldy-1.3.0-x64.msi");
        let prune = files_to_prune(&entries, &[&keep_a, &keep_b]);
        assert_eq!(
            prune,
            vec![d.join("Foldy-1.1.0-x64.msi"), d.join("x.msi.part")]
        );
    }
}
