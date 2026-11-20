//! 업데이트 상태 기계 (순수 로직, 시험 대상).
//!
//! ```text
//! Idle ──확인──▶ Checking ──받는 중──▶ Downloading ──검증 끝──▶ ReadyToInstall ──설치 요청 + Foldy 종료──▶ Installing
//!   ▲              │ 새 것 없음                │ 오류                     │ 건너뛰기                     │ 실패
//!   └──────────────┴───────────────────────────┴────────▶ Failed ◀─────────┴──────────────────────────────┘
//! ```
//!
//! 준비된 업데이트(`ready`)는 다음 확인이 실패해도 지워지지 않는다 (네트워크 장애로 설치 기회를 잃지 않게).
//! 설치 경로는 오직 `ready.path`에서 오며, 이 값은 서비스가 자기 업데이트 폴더에 받아 검증한 파일이다.

use std::collections::HashMap;
use std::path::PathBuf;
use std::time::{Duration, Instant};

use foldy_proto::{UpdateStatusReply, update_channel, update_state};
use foldy_update::{Channel, ManifestAsset, Version};

#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum Phase {
    #[default]
    Idle,
    Checking,
    Downloading,
    ReadyToInstall,
    Installing,
    Failed,
}

impl Phase {
    pub fn code(self) -> u8 {
        match self {
            Phase::Idle => update_state::IDLE,
            Phase::Checking => update_state::CHECKING,
            Phase::Downloading => update_state::DOWNLOADING,
            Phase::ReadyToInstall => update_state::READY_TO_INSTALL,
            Phase::Installing => update_state::INSTALLING,
            Phase::Failed => update_state::FAILED,
        }
    }

    fn busy(self) -> bool {
        matches!(self, Phase::Checking | Phase::Downloading | Phase::Installing)
    }
}

/// 검증을 마친 설치 파일.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Ready {
    pub version: Version,
    /// 업데이트 폴더 안의 MSI (서비스가 정한 경로).
    pub path: PathBuf,
    /// update.json의 크기·SHA-256 (설치 직전에 다시 확인).
    pub asset: ManifestAsset,
    pub mandatory: bool,
    pub notes: String,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct InstallRequest {
    pub relaunch: bool,
    pub session_id: u32,
}

/// 확인 한 번의 결과.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum Checked {
    Ready(Ready),
    /// 304: 바뀐 것 없음 → 준비된 업데이트는 그대로.
    NotModified,
    /// 최신이거나 건너뛴 버전 → 준비된 업데이트도 버린다.
    Nothing,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum RequestError {
    NotReady,
}

#[derive(Debug, Default)]
pub struct Machine {
    pub phase: Phase,
    pub ready: Option<Ready>,
    pub progress: (u64, u64),
    pub last_error: Option<String>,
    /// 설치 요청을 받았고 Foldy가 끝나기를 기다리는 중
    pub install: Option<InstallRequest>,
}

impl Machine {
    /// 확인을 시작해도 되면 참 (이미 확인·설치 중이거나 설치를 기다리는 중이면 거짓).
    pub fn begin_check(&mut self) -> bool {
        if self.phase.busy() || self.install.is_some() {
            return false;
        }
        self.phase = Phase::Checking;
        self.progress = (0, 0);
        true
    }

    /// 설치 직전에 한 번 더 확인 (사용자 요청 2026-11-21: 버전 차이가 크면 중간 버전을 거치지 말고 최신으로 한 번에).
    /// 설치를 기다리는 중에도 허락한다. 결과가 "새것 없음"이어도 이미 준비된 업데이트는 버리지 않는다(<see cref="finish_refresh"/>).
    pub fn begin_refresh(&mut self) -> bool {
        if self.phase.busy() {
            return false;
        }
        self.phase = Phase::Checking;
        self.progress = (0, 0);
        true
    }

    /// 설치 직전 확인의 결과: 더 새 버전이 준비됐으면 그것으로 바꾸고, 그 밖에는 이미 준비된 것을 그대로 쓴다.
    pub fn finish_refresh(&mut self, r: Result<Checked, String>) {
        if let Ok(Checked::Ready(newer)) = r
            && self.ready.as_ref().is_none_or(|old| newer.version > old.version)
        {
            self.ready = Some(newer);
        }
        self.last_error = None;
        self.phase = if self.ready.is_some() { Phase::ReadyToInstall } else { Phase::Idle };
    }

    pub fn on_progress(&mut self, done: u64, total: u64) {
        if matches!(self.phase, Phase::Checking | Phase::Downloading) {
            self.phase = Phase::Downloading;
            self.progress = (done, total);
        }
    }

    pub fn finish_check(&mut self, r: Result<Checked, String>) {
        match r {
            Ok(Checked::Ready(ready)) => {
                self.ready = Some(ready);
                self.last_error = None;
            }
            Ok(Checked::NotModified) => self.last_error = None,
            Ok(Checked::Nothing) => {
                self.ready = None;
                self.last_error = None;
            }
            Err(e) => self.last_error = Some(e),
        }
        self.phase = match (&self.ready, &self.last_error) {
            (Some(_), _) => Phase::ReadyToInstall,
            (None, Some(_)) => Phase::Failed,
            (None, None) => Phase::Idle,
        };
    }

    /// 설치 요청. 준비된 업데이트가 있어야 한다. 요청은 경로를 담지 않는다.
    pub fn request_install(&mut self, req: InstallRequest) -> Result<(), RequestError> {
        if self.phase == Phase::Installing {
            return Ok(());
        }
        if self.ready.is_none() {
            return Err(RequestError::NotReady);
        }
        self.install = Some(match self.install {
            // 두 번 오면 "다시 띄우기"는 한 번이라도 원했으면 유지
            Some(prev) => InstallRequest { relaunch: prev.relaunch || req.relaunch, session_id: req.session_id },
            None => req,
        });
        Ok(())
    }

    /// 설치를 시작할 수 있으면 Installing으로 넘기고 (준비된 파일, 요청)을 돌려준다.
    pub fn take_install(&mut self) -> Option<(Ready, InstallRequest)> {
        if self.phase.busy() {
            return None;
        }
        let req = self.install?;
        let ready = self.ready.clone()?;
        self.install = None;
        self.phase = Phase::Installing;
        self.last_error = None;
        Some((ready, req))
    }

    /// Foldy가 끝나지 않아 설치를 미룬다 (다음 종료나 버튼을 기다린다).
    pub fn install_deferred(&mut self, why: String) {
        self.phase = if self.ready.is_some() { Phase::ReadyToInstall } else { Phase::Idle };
        self.last_error = Some(why);
    }

    /// 설치 실패. `drop_ready`면 (검증 실패, 건너뛸 버전) 준비된 파일도 버린다.
    pub fn install_failed(&mut self, why: String, drop_ready: bool) {
        if drop_ready {
            self.ready = None;
        }
        self.phase = Phase::Failed;
        self.last_error = Some(why);
    }

    pub fn install_succeeded(&mut self) {
        self.ready = None;
        self.install = None;
        self.phase = Phase::Idle;
        self.last_error = None;
        self.progress = (0, 0);
    }

    /// 사용자가 이 버전을 건너뛴다. 준비된 업데이트가 그 버전이고 필수가 아니면 버린다.
    pub fn skip(&mut self, v: &Version) -> bool {
        let drop = self.phase != Phase::Installing && self.ready.as_ref().is_some_and(|r| &r.version == v && !r.mandatory);
        if drop {
            self.ready = None;
            self.install = None;
            if self.phase == Phase::ReadyToInstall || self.phase == Phase::Failed {
                self.phase = Phase::Idle;
            }
        }
        drop
    }

    /// 채널을 바꾸면, stable로 돌아갈 때 준비된 프리릴리스는 버린다.
    pub fn channel_changed(&mut self, channel: Channel) {
        if channel == Channel::Stable
            && self.phase != Phase::Installing
            && self.ready.as_ref().is_some_and(|r| r.version.is_prerelease())
        {
            self.ready = None;
            self.install = None;
            if self.phase == Phase::ReadyToInstall {
                self.phase = Phase::Idle;
            }
        }
    }

    pub fn status(&self, current: &Version, channel: Channel, last_check_unix: Option<u64>) -> UpdateStatusReply {
        let (done, total) = self.progress;
        let permille = if total == 0 { 0 } else { ((done.min(total) as u128 * 1000) / total as u128) as u16 };
        UpdateStatusReply {
            state: self.phase.code(),
            current_version: current.to_string(),
            available_version: self.ready.as_ref().map(|r| r.version.to_string()).unwrap_or_default(),
            channel: channel_code(channel),
            progress_permille: permille,
            downloaded: done,
            total,
            last_check_unix: last_check_unix.unwrap_or(0),
            last_error: self.last_error.clone().unwrap_or_default(),
            mandatory: self.ready.as_ref().is_some_and(|r| r.mandatory),
            install_pending: self.install.is_some(),
            notes: self.ready.as_ref().map(|r| r.notes.clone()).unwrap_or_default(),
        }
    }
}

pub fn channel_code(c: Channel) -> u8 {
    match c {
        Channel::Stable => update_channel::STABLE,
        Channel::Beta => update_channel::BETA,
    }
}

pub fn channel_from_code(c: u8) -> Option<Channel> {
    match c {
        update_channel::STABLE => Some(Channel::Stable),
        update_channel::BETA => Some(Channel::Beta),
        _ => None,
    }
}

/// 다시 띄울 세션 고르기. 파이프 클라이언트의 실제 세션이 우선이고, 요청 값은 활성 콘솔 세션과 같을 때만 믿는다.
/// 세션 0(서비스 세션)에는 창을 띄울 수 없으므로 콘솔 세션으로 바꾼다.
pub fn choose_session(requested: u32, pipe_client: Option<u32>, console: u32) -> u32 {
    match pipe_client {
        Some(s) if s != 0 => s,
        _ if requested != 0 && requested == console => requested,
        _ => console,
    }
}

/// 설치 후 시작 감시 (설계서 10.5 실패 대비: Foldy.exe가 시작 후 60초 안에 끝나면 실패).
/// 앱이 AppStarted를 보내면 그 자리에서 정상으로 본다.
#[derive(Debug)]
pub struct StartWatch {
    window: Duration,
    seen: HashMap<u32, Instant>,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum WatchEvent {
    Healthy,
    Failed,
}

impl StartWatch {
    pub fn new(window: Duration) -> Self {
        StartWatch { window, seen: HashMap::new() }
    }

    /// 지금 떠 있는 Foldy.exe 목록을 보고 판단한다.
    pub fn observe(&mut self, now: Instant, pids: &[u32]) -> Option<WatchEvent> {
        for &p in pids {
            self.seen.entry(p).or_insert(now);
        }
        // 창보다 오래 살아 있으면 정상
        if self.seen.iter().any(|(p, t)| pids.contains(p) && now.duration_since(*t) >= self.window) {
            self.seen.clear();
            return Some(WatchEvent::Healthy);
        }
        // 창 안에 사라진 프로세스 → 실패 (한 번만 센다)
        let gone: Vec<u32> = self.seen.keys().filter(|p| !pids.contains(p)).copied().collect();
        let mut failed = false;
        for p in gone {
            if let Some(t) = self.seen.remove(&p) {
                failed |= now.duration_since(t) < self.window;
            }
        }
        failed.then_some(WatchEvent::Failed)
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn ready(v: &str) -> Ready {
        Ready {
            version: Version::parse(v).unwrap(),
            path: PathBuf::from(format!(r"C:\ProgramData\Foldy\Updates\Foldy-{v}-x64.msi")),
            asset: ManifestAsset { name: format!("Foldy-{v}-x64.msi"), size: 3, sha256: "0".repeat(64) },
            mandatory: false,
            notes: "변경".into(),
        }
    }

    const REQ: InstallRequest = InstallRequest { relaunch: true, session_id: 1 };

    #[test]
    fn check_download_ready_install_success() {
        let mut m = Machine::default();
        assert!(m.begin_check());
        assert!(!m.begin_check(), "확인 중에는 다시 시작하지 않는다");
        assert_eq!(m.phase, Phase::Checking);
        m.on_progress(50, 200);
        assert_eq!(m.phase, Phase::Downloading);
        let s = m.status(&Version::new(1, 0, 0), Channel::Stable, Some(5));
        assert_eq!((s.state, s.progress_permille, s.last_check_unix), (update_state::DOWNLOADING, 250, 5));
        m.finish_check(Ok(Checked::Ready(ready("1.2.3"))));
        assert_eq!(m.phase, Phase::ReadyToInstall);
        let s = m.status(&Version::new(1, 0, 0), Channel::Beta, None);
        assert_eq!(s.available_version, "1.2.3");
        assert_eq!(s.channel, update_channel::BETA);

        m.request_install(REQ).unwrap();
        assert!(m.status(&Version::new(1, 0, 0), Channel::Stable, None).install_pending);
        assert!(!m.begin_check(), "설치를 기다리는 동안 확인하지 않는다");
        let (r, req) = m.take_install().unwrap();
        assert_eq!(r, ready("1.2.3"));
        assert_eq!(req, REQ);
        assert_eq!(m.phase, Phase::Installing);
        assert!(m.take_install().is_none());
        m.install_succeeded();
        assert_eq!(m.phase, Phase::Idle);
        assert!(m.ready.is_none());
    }

    #[test]
    fn refresh_before_install_switches_to_the_latest_version() {
        let mut m = Machine::default();
        m.begin_check();
        m.finish_check(Ok(Checked::Ready(ready("1.2.3"))));
        m.request_install(REQ).unwrap();
        // 설치를 기다리는 동안에도 한 번 더 확인한다: 그사이 1.2.5가 나왔으면 그것으로 한 번에
        assert!(m.begin_refresh());
        m.finish_refresh(Ok(Checked::Ready(ready("1.2.5"))));
        let (r, _) = m.take_install().unwrap();
        assert_eq!(r.version, Version::new(1, 2, 5));
    }

    #[test]
    fn refresh_keeps_the_ready_update_when_nothing_newer_or_offline() {
        for result in [Ok(Checked::Nothing), Ok(Checked::NotModified), Err("인터넷 없음".to_string()), Ok(Checked::Ready(ready("1.2.2")))] {
            let mut m = Machine::default();
            m.begin_check();
            m.finish_check(Ok(Checked::Ready(ready("1.2.3"))));
            m.request_install(REQ).unwrap();
            assert!(m.begin_refresh());
            m.finish_refresh(result);
            let (r, _) = m.take_install().unwrap();
            assert_eq!(r.version, Version::new(1, 2, 3), "새것이 없거나 확인 못 해도 준비된 것으로 설치한다");
        }
    }

    #[test]
    fn install_without_ready_is_refused() {
        let mut m = Machine::default();
        assert_eq!(m.request_install(REQ), Err(RequestError::NotReady));
        assert!(m.take_install().is_none());
        // 실패 상태에서도 준비된 파일이 없으면 거부
        m.begin_check();
        m.finish_check(Err("네트워크".into()));
        assert_eq!(m.phase, Phase::Failed);
        assert_eq!(m.request_install(REQ), Err(RequestError::NotReady));
    }

    #[test]
    fn errors_keep_prepared_update() {
        let mut m = Machine::default();
        m.begin_check();
        m.finish_check(Ok(Checked::Ready(ready("1.2.3"))));
        m.begin_check();
        m.finish_check(Err("GitHub 장애".into()));
        assert_eq!(m.phase, Phase::ReadyToInstall, "확인 실패로 준비된 업데이트를 잃지 않는다");
        assert_eq!(m.last_error.as_deref(), Some("GitHub 장애"));
        m.begin_check();
        m.finish_check(Ok(Checked::NotModified));
        assert_eq!(m.phase, Phase::ReadyToInstall);
        assert!(m.last_error.is_none());
        m.begin_check();
        m.finish_check(Ok(Checked::Nothing));
        assert_eq!(m.phase, Phase::Idle);
        assert!(m.ready.is_none());
    }

    #[test]
    fn deferred_and_failed_install_can_retry() {
        let mut m = Machine::default();
        m.begin_check();
        m.finish_check(Ok(Checked::Ready(ready("1.2.3"))));
        m.request_install(REQ).unwrap();
        m.take_install().unwrap();
        m.install_deferred("Foldy 실행 중".into());
        assert_eq!(m.phase, Phase::ReadyToInstall);
        m.request_install(InstallRequest { relaunch: false, session_id: 2 }).unwrap();
        m.take_install().unwrap();
        m.install_failed("msiexec 1603".into(), false);
        assert_eq!(m.phase, Phase::Failed);
        m.request_install(REQ).unwrap();
        assert!(m.take_install().is_some(), "실패 뒤 다시 시도할 수 있다");
        m.install_failed("SHA-256 불일치".into(), true);
        assert_eq!(m.request_install(REQ), Err(RequestError::NotReady));
    }

    #[test]
    fn repeated_requests_merge_relaunch() {
        let mut m = Machine::default();
        m.begin_check();
        m.finish_check(Ok(Checked::Ready(ready("1.2.3"))));
        m.request_install(REQ).unwrap();
        m.request_install(InstallRequest { relaunch: false, session_id: 3 }).unwrap();
        assert_eq!(m.install, Some(InstallRequest { relaunch: true, session_id: 3 }));
    }

    #[test]
    fn skip_drops_matching_non_mandatory() {
        let mut m = Machine::default();
        m.begin_check();
        m.finish_check(Ok(Checked::Ready(ready("1.2.3"))));
        assert!(!m.skip(&Version::new(1, 2, 4)));
        assert!(m.skip(&Version::new(1, 2, 3)));
        assert_eq!(m.phase, Phase::Idle);
        let mut r = ready("1.2.3");
        r.mandatory = true;
        m.begin_check();
        m.finish_check(Ok(Checked::Ready(r)));
        assert!(!m.skip(&Version::new(1, 2, 3)), "필수 업데이트는 건너뛸 수 없다");
    }

    #[test]
    fn switching_to_stable_drops_prerelease() {
        let mut m = Machine::default();
        m.begin_check();
        m.finish_check(Ok(Checked::Ready(ready("1.3.0-beta.1"))));
        m.channel_changed(Channel::Beta);
        assert!(m.ready.is_some());
        m.channel_changed(Channel::Stable);
        assert!(m.ready.is_none());
        assert_eq!(m.phase, Phase::Idle);
    }

    #[test]
    fn session_choice_prefers_pipe_client() {
        assert_eq!(choose_session(7, Some(2), 1), 2);
        assert_eq!(choose_session(1, None, 1), 1);
        assert_eq!(choose_session(7, None, 1), 1, "콘솔이 아닌 세션 값은 믿지 않는다");
        assert_eq!(choose_session(0, Some(0), 3), 3, "세션 0에는 띄우지 않는다");
    }

    #[test]
    fn start_watch_rules() {
        let t0 = Instant::now();
        let s = |n| t0 + Duration::from_secs(n);
        let mut w = StartWatch::new(Duration::from_secs(60));
        assert_eq!(w.observe(s(0), &[]), None);
        assert_eq!(w.observe(s(1), &[10]), None);
        assert_eq!(w.observe(s(20), &[10]), None);
        // 60초 안에 사라짐 → 실패
        assert_eq!(w.observe(s(30), &[]), Some(WatchEvent::Failed));
        assert_eq!(w.observe(s(31), &[]), None, "한 번만 센다");
        // 60초 넘게 살아 있으면 정상
        assert_eq!(w.observe(s(40), &[11]), None);
        assert_eq!(w.observe(s(101), &[11]), Some(WatchEvent::Healthy));
    }

    #[test]
    fn channel_codes_roundtrip() {
        for c in [Channel::Stable, Channel::Beta] {
            assert_eq!(channel_from_code(channel_code(c)), Some(c));
        }
        assert_eq!(channel_from_code(9), None);
    }
}
