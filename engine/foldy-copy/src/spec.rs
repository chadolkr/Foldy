//! 작업 명세 (fc_job_create의 JSON). 모르는 필드는 무시하고, 빠진 필드는 기본값을 쓴다 (ABI v1 JSON도 그대로 받음).

use serde::{Deserialize, Serialize};

/// 대형 줄 기준 (설계서: 256MB 이상은 대형 줄, COPY_FILE_NO_BUFFERING).
pub const DEFAULT_LARGE_THRESHOLD: u64 = 256 * 1024 * 1024;

#[derive(Debug, Clone, Copy, PartialEq, Eq, Default, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum Mode {
    #[default]
    Copy,
    Move,
}

/// 대상에 같은 이름이 있을 때.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum ConflictPolicy {
    Skip,
    Overwrite,
    /// "이름 (2).ext"
    Rename,
    /// 문제 목록에 올리고 NeedsAttention 상태로 사용자의 fc_job_resolve를 기다린다.
    #[default]
    Ask,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Default, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum VerifyMode {
    None,
    /// 크기 + 수정 시간
    #[default]
    SizeTime,
    /// 크기 + 수정 시간 + 내용 CRC32
    Hash,
}

fn default_large() -> u64 {
    DEFAULT_LARGE_THRESHOLD
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct Spec {
    pub sources: Vec<String>,
    pub dest_dir: String,
    #[serde(default)]
    pub mode: Mode,
    #[serde(default)]
    pub conflict: ConflictPolicy,
    #[serde(default)]
    pub verify: VerifyMode,
    /// 0 = 자동 조절, 그 밖에는 고정 스레드 수 (설정 "수동 [4]").
    #[serde(default)]
    pub threads: u32,
    /// 작업 저널 경로 (jobs/<id>.journal). 없으면 저널을 쓰지 않는다.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub journal_path: Option<String>,
    /// true면 journal_path의 완료 목록을 읽어 이어서 복사한다.
    #[serde(default)]
    pub resume: bool,
    /// 드라이브 조합별 최적 스레드 수 저장 파일 (JSON). 없으면 학습값을 쓰지 않는다.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub tuning_path: Option<String>,
    #[serde(default = "default_large")]
    pub large_threshold: u64,
    /// 자동 조절 측정 창 (기본 2000ms).
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub tune_interval_ms: Option<u64>,
    /// 진단·시험용: 같은 볼륨 이동도 이름 변경 대신 복사 → 검증 → 원본 삭제로 처리한다.
    #[serde(default)]
    pub move_by_copy: bool,
    /// 시험용: 파일 n개를 끝낸 뒤 크래시처럼 멈춘다 (저널 마무리 없음, 상태 Failed, FC_ERR_ABORTED).
    #[serde(default, rename = "_test_abort_after_files", skip_serializing)]
    pub test_abort_after_files: Option<u64>,
}

impl Spec {
    pub fn parse(json: &str) -> Option<Spec> {
        let spec: Spec = serde_json::from_str(json).ok()?;
        if spec.dest_dir.is_empty() {
            return None;
        }
        Some(spec)
    }

    /// 저널 Plan 레코드에 남길 형태 (이어하기 플래그와 시험 옵션은 뺀다).
    pub fn to_journal_json(&self) -> String {
        let mut s = self.clone();
        s.resume = false;
        s.test_abort_after_files = None;
        serde_json::to_string(&s).unwrap_or_default()
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn v1_json_gets_defaults() {
        let s = Spec::parse(r#"{"sources":["C:\\a"],"dest_dir":"D:\\o"}"#).unwrap();
        assert_eq!(s.mode, Mode::Copy);
        assert_eq!(s.conflict, ConflictPolicy::Ask);
        assert_eq!(s.verify, VerifyMode::SizeTime);
        assert_eq!(s.large_threshold, DEFAULT_LARGE_THRESHOLD);
        assert!(Spec::parse(r#"{"sources":[],"dest_dir":""}"#).is_none());
    }

    #[test]
    fn journal_json_drops_resume_and_test_flags() {
        let s = Spec::parse(r#"{"sources":[],"dest_dir":"D:\\o","resume":true,"_test_abort_after_files":3,"mode":"move","conflict":"rename"}"#).unwrap();
        assert_eq!(s.test_abort_after_files, Some(3));
        let back = Spec::parse(&s.to_journal_json()).unwrap();
        assert!(!back.resume);
        assert_eq!(back.test_abort_after_files, None);
        assert_eq!(back.mode, Mode::Move);
        assert_eq!(back.conflict, ConflictPolicy::Rename);
    }
}
