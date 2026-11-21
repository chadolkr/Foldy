//! BuildAnalysis / AnalysisInfo 요청 처리 (설계서 7.3). 분석은 볼륨마다 백그라운드 스레드 하나에서
//! 낮은 우선순위로 돌고, 진행률은 VolumeSlot.analysis → StatusReply.analysis로 보인다.

use crate::analysis::{self, BuildOptions, rules::RuleSet};
use crate::state::{Indexer, VolumeSlot};
use foldy_proto::*;
use std::sync::Arc;
use std::sync::atomic::Ordering;

pub fn start_build(ix: &Arc<Indexer>, volume: &str) -> BuildAnalysisReply {
    let reply = |status, message: &str| BuildAnalysisReply { status, message: message.to_string() };
    let Some(slot) = ix.find_volume(volume).cloned() else {
        return reply(analysis_status::UNKNOWN_VOLUME, "색인 서비스가 다루지 않는 볼륨");
    };
    if slot.state.load(Ordering::Acquire) != state::READY {
        return reply(analysis_status::NOT_READY, "색인이 아직 준비되지 않음");
    }
    if !slot.analysis.try_start() {
        return reply(analysis_status::ALREADY_RUNNING, "이미 분석 중");
    }
    slot.analysis.set(analysis_state::BUILDING, 0, "색인 읽는 중");
    let ix2 = ix.clone();
    std::thread::Builder::new()
        .name(format!("analysis {}", slot.name))
        .spawn(move || run(&ix2, &slot))
        .map(|_| reply(analysis_status::OK, "분석 시작"))
        .unwrap_or_else(|e| reply(analysis_status::NOT_READY, &format!("스레드를 만들 수 없음: {e}")))
}

fn run(ix: &Indexer, slot: &VolumeSlot) {
    analysis::lower_thread_priority();
    let job = &slot.analysis;
    let started_at = analysis::now_filetime();
    // 1. 읽기 잠금은 복사하는 동안만
    let ex = {
        let Ok(g) = slot.index.read() else {
            job.set(analysis_state::FAILED, 0, "색인 잠금 실패");
            return;
        };
        let Some(idx) = g.as_ref() else {
            job.set(analysis_state::FAILED, 0, "색인이 없음");
            return;
        };
        analysis::extract(idx)
    };
    job.set(analysis_state::BUILDING, 100, "규칙 적용");
    let dir = ix.analysis_dir();
    let rules = RuleSet::load_default_locations();
    let opts = BuildOptions {
        volume: slot.name.clone(),
        started_at,
        usn: slot.last_usn.load(Ordering::Relaxed),
        min_file_alloc: 0,
    };
    if ix.analysis_dir.is_none() {
        let _ = std::fs::create_dir_all(&dir);
        if let Err(e) = analysis::secure_dir(&dir) {
            eprintln!("[{}] 분석 폴더 ACL 설정 실패: {e}", slot.name);
        }
    }
    let key = analysis::volume_key(&slot.name);
    let progress = |p: u16, m: &str| job.set(analysis_state::BUILDING, p.min(999), m);
    match analysis::build_into_dir(&dir, &key, &ex, &rules, &opts, &progress, &ix.shutdown) {
        Ok((path, st)) => {
            let msg = format!(
                "분석 완료: 폴더 {}개, 파일 {}개, 제안 {}개, {:.1}MB ({})",
                st.dirs,
                st.files,
                st.candidates,
                st.db_bytes as f64 / 1048576.0,
                path.display()
            );
            eprintln!("[{}] {msg}", slot.name);
            job.set(analysis_state::READY, 1000, &msg);
        }
        Err(e) => {
            eprintln!("[{}] 분석 실패: {e}", slot.name);
            job.set(analysis_state::FAILED, 0, &e);
        }
    }
}

/// 분석을 요청한 사용자에게만 분석 DB 읽기를 허락한다 (처음 한 번만 ACL을 쓴다). 시험 폴더는 건드리지 않는다.
pub fn allow_reader(ix: &Indexer, sid: Option<&str>) {
    if ix.analysis_dir.is_some() {
        return;
    }
    if let Some(sid) = sid
        && let Err(e) = analysis::allow_reader(&ix.analysis_dir(), sid)
    {
        eprintln!("분석 폴더 읽기 허락 실패: {e}");
    }
}

pub fn info(ix: &Indexer, volume: &str) -> AnalysisInfoReply {
    let empty = |status, volume: String| AnalysisInfoReply { status, volume, db_path: String::new(), scan_time: 0, previous: Vec::new() };
    let Some(slot) = ix.find_volume(volume) else {
        return empty(analysis_status::UNKNOWN_VOLUME, volume.to_string());
    };
    let snaps = analysis::list_snapshots(&ix.analysis_dir(), &analysis::volume_key(&slot.name));
    let Some((latest, t)) = snaps.first() else {
        return empty(analysis_status::NOT_FOUND, slot.name.clone());
    };
    AnalysisInfoReply {
        status: analysis_status::OK,
        volume: slot.name.clone(),
        db_path: latest.to_string_lossy().into_owned(),
        scan_time: *t,
        previous: snaps
            .iter()
            .skip(1)
            .take(analysis::MAX_HISTORY)
            .map(|(p, t)| AnalysisSnapshot { db_path: p.to_string_lossy().into_owned(), scan_time: *t })
            .collect(),
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::time::{Duration, Instant};

    #[test]
    fn build_over_pipe_handlers_reports_progress_and_info() {
        let dir = std::env::temp_dir().join(format!("foldy-analysis-job-{}", std::process::id()));
        let _ = std::fs::remove_dir_all(&dir);
        let slot = Arc::new(VolumeSlot::new("D:"));
        let mut ix = Indexer { analysis_dir: Some(dir.clone()), ..Default::default() };
        ix.volumes.push(slot.clone());
        let ix = Arc::new(ix);
        // 색인 준비 전
        assert_eq!(start_build(&ix, "D:").status, analysis_status::NOT_READY);
        assert_eq!(start_build(&ix, "Q:").status, analysis_status::UNKNOWN_VOLUME);
        assert_eq!(info(&ix, "d:\\").status, analysis_status::NOT_FOUND);
        slot.publish(crate::analysis::tests::sample_index());
        assert_eq!(start_build(&ix, "d:").status, analysis_status::OK);
        let t = Instant::now();
        while slot.analysis.state.load(Ordering::Acquire) == analysis_state::BUILDING {
            assert!(t.elapsed() < Duration::from_secs(30));
            std::thread::sleep(Duration::from_millis(20));
        }
        let st = ix.status();
        assert_eq!(st.analysis.len(), 1);
        assert_eq!(st.analysis[0].state, analysis_state::READY, "{}", st.analysis[0].message);
        assert_eq!(st.analysis[0].progress_permille, 1000);
        let r = info(&ix, "D:");
        assert_eq!(r.status, analysis_status::OK);
        assert!(std::path::Path::new(&r.db_path).exists());
        assert!(r.scan_time > 0 && r.previous.is_empty());
        let _ = std::fs::remove_dir_all(&dir);
    }
}
