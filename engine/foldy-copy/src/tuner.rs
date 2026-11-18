//! 스레드 자동 조절 (설계서 "병렬 복사 엔진 → 스레드 자동 조절").
//!
//! 1. SSD/HDD는 IncursSeekPenalty로 판별 (win::drive_kind)
//! 2. 2초마다 처리량을 재서 5% 넘게 좋아지면 +2, 아니면 되돌림
//! 3. 드라이브 조합별 최적값을 저장해 다음 작업의 시작값으로 사용
//!
//! 워커 스레드는 상한만큼 미리 만들고 [`Gate`]로 동시 실행 수만 바꾼다.

use std::collections::BTreeMap;
use std::path::Path;
use std::sync::atomic::{AtomicU32, Ordering::*};
use std::sync::{Condvar, Mutex};
use std::time::Duration;

use serde::{Deserialize, Serialize};

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum DriveKind {
    Ssd,
    Hdd,
    Network,
    Unknown,
}

impl DriveKind {
    pub fn as_str(self) -> &'static str {
        match self {
            DriveKind::Ssd => "ssd",
            DriveKind::Hdd => "hdd",
            DriveKind::Network => "net",
            DriveKind::Unknown => "unknown",
        }
    }
}

/// 한 번에 늘리는 스레드 수
pub const STEP: u32 = 2;
/// 이만큼(5%) 넘게 좋아져야 늘린 값을 유지한다
pub const IMPROVE: f64 = 1.05;
/// 되돌린 뒤 다시 탐색하기까지 기다리는 측정 창 수
pub const COOLDOWN: u32 = 5;
/// 처리량 점수에서 파일 하나를 이만큼의 바이트로 친다 (작은 파일은 개수가 처리량)
pub const FILE_WEIGHT_BYTES: f64 = 64.0 * 1024.0;

/// 드라이브 조합의 (시작값, 상한). HDD가 끼면 1..2, 네트워크는 8..32, 나머지(SSD)는 4..16.
pub fn bounds(src: DriveKind, dst: DriveKind) -> (u32, u32) {
    if src == DriveKind::Hdd || dst == DriveKind::Hdd {
        (1, 2)
    } else if src == DriveKind::Network || dst == DriveKind::Network {
        (8, 32)
    } else {
        (4, 16)
    }
}

/// 측정 창의 처리량 점수 (바이트/초 + 파일/초 × 64KB).
pub fn score(bytes: u64, files: u64, secs: f64) -> f64 {
    if secs <= 0.0 {
        return 0.0;
    }
    (bytes as f64 + files as f64 * FILE_WEIGHT_BYTES) / secs
}

#[derive(Debug, Clone)]
pub struct AutoTuner {
    current: u32,
    min: u32,
    max: u32,
    /// 지금 값에서 잰 점수
    baseline: Option<f64>,
    /// 탐색(+STEP) 중이면 되돌아갈 값
    probing_from: Option<u32>,
    best: u32,
    cooldown: u32,
}

impl AutoTuner {
    pub fn new(start: u32, min: u32, max: u32) -> Self {
        let min = min.max(1);
        let max = max.max(min);
        let current = start.clamp(min, max);
        AutoTuner { current, min, max, baseline: None, probing_from: None, best: current, cooldown: 0 }
    }

    pub fn current(&self) -> u32 {
        self.current
    }

    /// 가장 좋았던 값 (작업 끝에 드라이브 조합별로 저장).
    pub fn best(&self) -> u32 {
        self.best
    }

    fn probe(&mut self) {
        if self.current < self.max {
            self.probing_from = Some(self.current);
            self.current = (self.current + STEP).min(self.max);
        }
    }

    /// 측정 창 하나의 점수를 넣고 다음 창의 스레드 수를 돌려준다.
    /// 점수 0(일시정지, 진행 없음)은 판단에 쓰지 않는다.
    pub fn observe(&mut self, score: f64) -> u32 {
        if score <= 0.0 || !score.is_finite() {
            return self.current;
        }
        match (self.probing_from.take(), self.baseline) {
            (Some(prev), Some(base)) => {
                if score > base * IMPROVE {
                    self.best = self.current;
                    self.baseline = Some(score);
                    self.probe();
                } else {
                    self.current = prev;
                    self.cooldown = COOLDOWN;
                }
            }
            _ => {
                self.baseline = Some(score);
                if self.cooldown > 0 {
                    self.cooldown -= 1;
                } else {
                    self.probe();
                }
            }
        }
        self.current = self.current.clamp(self.min, self.max);
        self.current
    }
}

/// 실행 중 상한을 바꿀 수 있는 동시 실행 관문 (설계서의 ConcurrencyGate).
pub struct Gate {
    limit: AtomicU32,
    active: Mutex<u32>,
    cv: Condvar,
}

pub struct Permit<'a>(&'a Gate);

impl Drop for Permit<'_> {
    fn drop(&mut self) {
        let mut a = self.0.active.lock().unwrap_or_else(|p| p.into_inner());
        *a = a.saturating_sub(1);
        drop(a);
        self.0.cv.notify_one();
    }
}

impl Gate {
    pub fn new(limit: u32) -> Self {
        Gate { limit: AtomicU32::new(limit.max(1)), active: Mutex::new(0), cv: Condvar::new() }
    }

    pub fn set_limit(&self, n: u32) {
        self.limit.store(n.max(1), Release);
        self.cv.notify_all();
    }

    pub fn limit(&self) -> u32 {
        self.limit.load(Acquire)
    }

    pub fn active(&self) -> u32 {
        *self.active.lock().unwrap_or_else(|p| p.into_inner())
    }

    /// 자리가 날 때까지 기다린다. `stop()`이 true가 되면 None.
    pub fn acquire(&self, stop: &dyn Fn() -> bool) -> Option<Permit<'_>> {
        let mut a = self.active.lock().unwrap_or_else(|p| p.into_inner());
        loop {
            if stop() {
                return None;
            }
            if *a < self.limit.load(Acquire) {
                *a += 1;
                return Some(Permit(self));
            }
            a = self.cv.wait_timeout(a, Duration::from_millis(50)).unwrap_or_else(|p| p.into_inner()).0;
        }
    }
}

/// 드라이브 조합 키: "ssd:C:\|hdd:D:\"
pub fn pair_key(src_root: &str, src: DriveKind, dst_root: &str, dst: DriveKind) -> String {
    format!("{}:{}|{}:{}", src.as_str(), src_root.to_uppercase(), dst.as_str(), dst_root.to_uppercase())
}

/// 드라이브 조합별 최적 스레드 수 (JSON 파일 하나). C#이 foldy.db copy_tuning으로 옮길 수 있다.
pub fn load_best(path: &Path, key: &str) -> Option<u32> {
    let text = std::fs::read_to_string(path).ok()?;
    let map: BTreeMap<String, u32> = serde_json::from_str(&text).ok()?;
    map.get(key).copied()
}

pub fn save_best(path: &Path, key: &str, value: u32) -> std::io::Result<()> {
    let mut map: BTreeMap<String, u32> =
        std::fs::read_to_string(path).ok().and_then(|t| serde_json::from_str(&t).ok()).unwrap_or_default();
    map.insert(key.to_owned(), value);
    if let Some(d) = path.parent() {
        std::fs::create_dir_all(d)?;
    }
    // 원자적 저장: 임시 파일에 쓰고 바꿔치기
    let tmp = path.with_extension("tmp");
    std::fs::write(&tmp, serde_json::to_vec_pretty(&map).unwrap_or_default())?;
    std::fs::rename(tmp, path)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn bounds_follow_design_table() {
        use DriveKind::*;
        assert_eq!(bounds(Ssd, Ssd), (4, 16));
        assert_eq!(bounds(Ssd, Hdd), (1, 2));
        assert_eq!(bounds(Hdd, Network), (1, 2));
        assert_eq!(bounds(Network, Ssd), (8, 32));
        assert_eq!(bounds(Unknown, Ssd), (4, 16));
    }

    #[test]
    fn score_weighs_files() {
        assert_eq!(score(0, 0, 0.0), 0.0);
        assert_eq!(score(1000, 0, 2.0), 500.0);
        assert_eq!(score(0, 2, 1.0), 2.0 * FILE_WEIGHT_BYTES);
    }

    #[test]
    fn climbs_while_improving_and_reverts_otherwise() {
        let mut t = AutoTuner::new(4, 1, 16);
        assert_eq!(t.observe(100.0), 6, "첫 창: 기준을 재고 +2 탐색");
        assert_eq!(t.observe(110.0), 8, "10% 향상 → 6 유지, 8 탐색");
        assert_eq!(t.best(), 6);
        assert_eq!(t.observe(112.0), 6, "1.8%만 향상 → 되돌림");
        assert_eq!(t.best(), 6);
        // 쿨다운 동안은 그대로
        for _ in 0..COOLDOWN {
            assert_eq!(t.observe(111.0), 6);
        }
        assert_eq!(t.observe(111.0), 8, "쿨다운 뒤 다시 탐색");
        assert_eq!(t.observe(200.0), 10);
        assert_eq!(t.best(), 8);
    }

    #[test]
    fn exactly_five_percent_is_not_enough() {
        let mut t = AutoTuner::new(4, 1, 16);
        t.observe(100.0);
        assert_eq!(t.observe(105.0), 4);
    }

    #[test]
    fn respects_max_and_ignores_idle_windows() {
        let mut t = AutoTuner::new(1, 1, 2);
        assert_eq!(t.observe(0.0), 1, "진행 없는 창은 무시");
        assert_eq!(t.observe(10.0), 2);
        assert_eq!(t.observe(100.0), 2, "상한에서 멈춤");
        assert_eq!(t.best(), 2);
        assert_eq!(t.observe(100.0), 2);
        let t = AutoTuner::new(99, 4, 16);
        assert_eq!(t.current(), 16, "시작값은 범위로 자른다");
    }

    #[test]
    fn gate_limits_concurrency() {
        let g = Gate::new(2);
        let a = g.acquire(&|| false).unwrap();
        let _b = g.acquire(&|| false).unwrap();
        assert_eq!(g.active(), 2);
        // 자리가 없으면 stop으로 빠져나온다
        let start = std::time::Instant::now();
        let stop = || start.elapsed() > Duration::from_millis(80);
        assert!(g.acquire(&stop).is_none());
        drop(a);
        assert!(g.acquire(&|| false).is_some());
        g.set_limit(0);
        assert_eq!(g.limit(), 1, "상한은 최소 1");
    }

    #[test]
    fn best_value_store_roundtrip() {
        let p = std::env::temp_dir().join(format!("foldy-tuning-{}.json", std::process::id()));
        let k = pair_key("c:\\", DriveKind::Ssd, "d:\\", DriveKind::Hdd);
        assert_eq!(k, "ssd:C:\\|hdd:D:\\");
        assert_eq!(load_best(&p, &k), None);
        save_best(&p, &k, 6).unwrap();
        save_best(&p, "other", 2).unwrap();
        assert_eq!(load_best(&p, &k), Some(6));
        let _ = std::fs::remove_file(p);
    }
}
