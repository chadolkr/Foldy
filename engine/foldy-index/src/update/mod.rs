//! 서비스 자동 업데이트 (설계서 10.5): SYSTEM 서비스가 확인·다운로드·검증·설치를 모두 맡아 UAC 창이 없다.
//!
//! - 확인: 시작 1분 뒤 + 6시간마다, 또는 CheckNow. ETag(304)는 foldy-update가 처리한다.
//! - 다운로드: `%ProgramData%\Foldy\Updates\` (소유자 SYSTEM, SYSTEM·Administrators 모두, Users 읽기).
//!   update.json Ed25519 서명(내장 공개키) → MSI 크기·SHA-256 → "설치 준비됨".
//! - 설치: 앱이 InstallWhenIdle을 보내면 (종료할 때 / [재시작하고 업데이트]) Foldy.exe가 모두 끝나기를 기다렸다가,
//!   쓰기 잠금을 건 채 해시를 다시 확인하고 `msiexec /i <검증된 MSI> /qn /norestart`를 분리 실행한다.
//!   설치 파일 경로는 언제나 서비스가 정한 것이다 — 파이프 메시지에는 경로가 없다.
//! - MSI가 서비스를 멈추고 바꾼 뒤 새 서비스가 시작하면 `state.json`의 `pending`으로 결과를 판단하고,
//!   요청이 있었으면 사용자 세션에 Foldy를 다시 띄운다.
//! - 되돌리기: 새 버전 Foldy가 두 번 연속 60초 안에 끝나면(AppStarted 없이) 직전 MSI로 되돌리고 그 버전을 건너뛴다.

pub mod host;
pub mod machine;
pub mod win;

use std::io::Write;
use std::path::{Path, PathBuf};
use std::sync::{Arc, Mutex, MutexGuard, OnceLock};
use std::time::{Duration, Instant, SystemTime, UNIX_EPOCH};

use ed25519_dalek::VerifyingKey;
use foldy_proto::*;
use foldy_update::http::PlainHttpClient;
use foldy_update::install::{AUTHENTICODE_POLICY, InstallOptions, install_command_with};
use foldy_update::manifest::{PackageKind, verify_locked};
use foldy_update::state::{PendingInstall, RollbackDecision, StartOutcome, UpdateState, files_to_prune};
use foldy_update::updater::{Outcome, Updater, UpdaterConfig, default_updates_dir, state_path};
use foldy_update::{Channel, Version};
use tokio::sync::Notify;

use host::{Host, describe};
use machine::{Checked, InstallRequest, Machine, Ready, StartWatch, WatchEvent, channel_from_code, choose_session};

/// 설치 기록이 이보다 오래되면 결과를 알 수 없음으로 보고 Foldy를 다시 띄우지 않는다.
const PENDING_MAX_AGE_SECS: u64 = 2 * 60 * 60;

pub struct UpdateConfig {
    pub api_base: String,
    pub repo: String,
    pub updates_dir: PathBuf,
    pub verifying_key: VerifyingKey,
    pub current_version: Version,
    /// Foldy.exe가 있는 폴더 (서비스 exe와 같은 폴더). 다시 띄우기·실행 중 확인에 쓴다.
    pub install_dir: PathBuf,
    pub first_delay: Duration,
    pub period: Duration,
    /// InstallWhenIdle 뒤 Foldy가 끝나기를 기다리는 최대 시간
    pub idle_timeout: Duration,
    /// 설치 후 시작 감시: 이 시간 안에 끝나면 시작 실패
    pub start_window: Duration,
    /// 설치 뒤 Foldy를 다시 띄우기 전 대기 (msiexec가 마무리할 시간)
    pub relaunch_delay: Duration,
    pub poll: Duration,
    /// 업데이트 폴더 ACL을 직접 정할지 (서비스 모드). 콘솔 시험에서는 끈다.
    pub secure_dir: bool,
}

impl UpdateConfig {
    /// Windows 서비스 기본값: 내장 공개키, `%ProgramData%\Foldy\Updates`, GitHub 저장소.
    pub fn for_service() -> Result<Self, String> {
        let exe = std::env::current_exe().map_err(|e| e.to_string())?;
        let install_dir = exe.parent().map(Path::to_path_buf).ok_or("설치 폴더를 알 수 없음")?;
        let verifying_key = foldy_update::sign::embedded_verifying_key().map_err(|e| e.to_string())?;
        Ok(UpdateConfig {
            api_base: foldy_update::DEFAULT_API_BASE.into(),
            repo: foldy_update::DEFAULT_REPO.into(),
            updates_dir: default_updates_dir(),
            verifying_key,
            current_version: installed_version(&install_dir),
            install_dir,
            first_delay: Duration::from_secs(60),
            period: Duration::from_secs(6 * 60 * 60),
            idle_timeout: Duration::from_secs(120),
            start_window: Duration::from_secs(60),
            relaunch_delay: Duration::from_secs(5),
            poll: Duration::from_secs(2),
            secure_dir: true,
        })
    }

    /// 콘솔 실행(개발·시험)용: `--update-api`가 있을 때만 켠다. 반환: (설정, 가짜 설치 기록 파일).
    pub fn from_console_args(args: &[(String, String)]) -> Result<Option<(Self, Option<PathBuf>)>, String> {
        let get = |k: &str| args.iter().rev().find(|(n, _)| n == k).map(|(_, v)| v.as_str());
        let Some(api) = get("--update-api") else {
            return Ok(None);
        };
        let ms = |k: &str, d: u64| -> Result<Duration, String> {
            get(k).map_or(Ok(Duration::from_millis(d)), |v| v.parse().map(Duration::from_millis).map_err(|_| format!("{k}: 숫자가 아님")))
        };
        let verifying_key = match get("--update-pubkey") {
            Some(p) => foldy_update::sign::parse_verifying_key(&std::fs::read_to_string(p).map_err(|e| format!("{p}: {e}"))?),
            None => foldy_update::sign::embedded_verifying_key(),
        }
        .map_err(|e| e.to_string())?;
        let exe = std::env::current_exe().map_err(|e| e.to_string())?;
        let install_dir = get("--update-install-dir").map(PathBuf::from).or_else(|| exe.parent().map(Path::to_path_buf)).ok_or("설치 폴더")?;
        let current_version = match get("--update-current") {
            Some(v) => Version::parse(v).map_err(|e| e.to_string())?,
            None => installed_version(&install_dir),
        };
        let cfg = UpdateConfig {
            api_base: api.trim_end_matches('/').into(),
            repo: get("--update-repo").unwrap_or(foldy_update::DEFAULT_REPO).into(),
            updates_dir: get("--update-dir").map(PathBuf::from).unwrap_or_else(default_updates_dir),
            verifying_key,
            current_version,
            install_dir,
            first_delay: ms("--update-first-delay-ms", 60_000)?,
            period: Duration::from_secs(6 * 60 * 60),
            idle_timeout: ms("--update-idle-timeout-ms", 120_000)?,
            start_window: ms("--update-start-window-ms", 60_000)?,
            relaunch_delay: ms("--update-relaunch-delay-ms", 0)?,
            poll: ms("--update-poll-ms", 500)?,
            secure_dir: false,
        };
        Ok(Some((cfg, get("--update-fake-install").map(PathBuf::from))))
    }

    fn foldy_exe(&self) -> PathBuf {
        self.install_dir.join("Foldy.exe")
    }
}

/// 설치된 제품 버전: 설치 폴더 Foldy.exe의 ProductVersion (태그 버전과 같다). 없으면 이 서비스의 크레이트 버전.
pub fn installed_version(install_dir: &Path) -> Version {
    win::product_version(&install_dir.join("Foldy.exe"))
        .and_then(|s| Version::parse(&s).ok())
        .unwrap_or_else(|| Version::parse(env!("CARGO_PKG_VERSION")).unwrap_or(Version::new(0, 0, 0)))
}

struct Inner {
    m: Machine,
    st: UpdateState,
    check_requested: bool,
    watch: Option<StartWatch>,
}

pub struct UpdateService {
    cfg: UpdateConfig,
    host: Arc<dyn Host>,
    inner: Mutex<Inner>,
    kick: Notify,
}

static GLOBAL: OnceLock<Arc<UpdateService>> = OnceLock::new();

/// 파이프 서버가 쓸 업데이트 서비스를 등록한다 (프로세스에 하나).
pub fn set_global(svc: Arc<UpdateService>) {
    let _ = GLOBAL.set(svc);
}

pub fn is_update_msg(ty: MsgType) -> bool {
    matches!(
        ty,
        MsgType::UpdateStatus | MsgType::CheckNow | MsgType::InstallWhenIdle | MsgType::SkipVersion | MsgType::SetChannel | MsgType::AppStarted
    )
}

/// 파이프 요청 처리. 업데이트가 꺼져 있으면 Error(DISABLED) → 클라이언트는 기존 방식으로 폴백.
pub fn handle_pipe(ty: MsgType, body: &[u8], pipe_session: Option<u32>) -> Vec<u8> {
    match GLOBAL.get() {
        Some(s) => s.handle(ty, body, pipe_session),
        None => err(update_error::DISABLED, "이 서비스 실행에서는 자동 업데이트가 꺼져 있습니다"),
    }
}

fn err(code: u32, message: &str) -> Vec<u8> {
    encode(MsgType::Error, &ErrorReply { code, message: message.to_string() })
}

fn now_unix() -> u64 {
    SystemTime::now().duration_since(UNIX_EPOCH).map(|d| d.as_secs()).unwrap_or(0)
}

impl UpdateService {
    pub fn new(cfg: UpdateConfig, host: Arc<dyn Host>) -> Arc<Self> {
        Arc::new(UpdateService {
            cfg,
            host,
            inner: Mutex::new(Inner { m: Machine::default(), st: UpdateState::default(), check_requested: false, watch: None }),
            kick: Notify::new(),
        })
    }

    fn lock(&self) -> MutexGuard<'_, Inner> {
        self.inner.lock().unwrap_or_else(|e| e.into_inner())
    }

    fn channel(st: &UpdateState) -> Channel {
        st.channel.unwrap_or_default()
    }

    pub fn status(&self) -> UpdateStatusReply {
        let g = self.lock();
        g.m.status(&self.cfg.current_version, Self::channel(&g.st), g.st.last_check_unix)
    }

    fn status_frame(&self) -> Vec<u8> {
        encode(MsgType::UpdateStatusReply, &self.status())
    }

    /// 업데이트 기록 (서비스에는 콘솔이 없으므로 업데이트 폴더의 updater.log에도 남긴다).
    fn log(&self, msg: &str) {
        eprintln!("[update] {msg}");
        let path = self.cfg.updates_dir.join("updater.log");
        if std::fs::metadata(&path).is_ok_and(|m| m.len() > 512 * 1024) {
            let _ = std::fs::rename(&path, self.cfg.updates_dir.join("updater.log.old"));
        }
        if let Ok(mut f) = std::fs::OpenOptions::new().create(true).append(true).open(&path) {
            let _ = writeln!(f, "{} {msg}", now_unix());
        }
    }

    fn save(&self, g: &Inner) {
        if let Err(e) = g.st.save(&state_path(&self.cfg.updates_dir)) {
            self.log(&format!("state.json 저장 실패: {e}"));
        }
    }

    pub fn handle(&self, ty: MsgType, body: &[u8], pipe_session: Option<u32>) -> Vec<u8> {
        match ty {
            MsgType::UpdateStatus => self.status_frame(),
            MsgType::CheckNow => {
                self.lock().check_requested = true;
                self.kick.notify_one();
                self.status_frame()
            }
            MsgType::InstallWhenIdle => match decode_body::<InstallWhenIdle>(body) {
                Ok(req) => {
                    let session = choose_session(req.session_id, pipe_session, self.host.active_console_session());
                    let r = self.lock().m.request_install(InstallRequest { relaunch: req.relaunch, session_id: session });
                    match r {
                        Ok(()) => {
                            self.kick.notify_one();
                            self.status_frame()
                        }
                        Err(_) => err(update_error::NOT_READY, "설치할 준비된 업데이트가 없습니다"),
                    }
                }
                Err(e) => err(1, &e.to_string()),
            },
            MsgType::SkipVersion => match decode_body::<SkipVersion>(body).map(|s| Version::parse(&s.version)) {
                Ok(Ok(v)) => {
                    let mut g = self.lock();
                    g.st.skip(&v);
                    g.m.skip(&v);
                    self.save(&g);
                    drop(g);
                    self.status_frame()
                }
                Ok(Err(e)) => err(update_error::BAD_ARGUMENT, &e.to_string()),
                Err(e) => err(1, &e.to_string()),
            },
            MsgType::SetChannel => match decode_body::<SetChannel>(body).map(|c| channel_from_code(c.channel)) {
                Ok(Some(c)) => {
                    let mut g = self.lock();
                    if g.st.channel.unwrap_or_default() != c {
                        g.st.channel = Some(c);
                        g.st.etag = None;
                        g.m.channel_changed(c);
                        g.check_requested = true;
                        self.save(&g);
                        self.kick.notify_one();
                    }
                    drop(g);
                    self.status_frame()
                }
                Ok(None) => err(update_error::BAD_ARGUMENT, "채널은 0(stable) 또는 1(beta)"),
                Err(e) => err(1, &e.to_string()),
            },
            MsgType::AppStarted => match decode_body::<AppStarted>(body) {
                Ok(a) => {
                    if Version::parse(&a.version).is_ok_and(|v| v == self.cfg.current_version) {
                        let mut g = self.lock();
                        if g.watch.is_some() {
                            self.mark_healthy(&mut g);
                        }
                    }
                    self.status_frame()
                }
                Err(e) => err(1, &e.to_string()),
            },
            other => err(3, &format!("unsupported request: {other:?}")),
        }
    }

    /// 업데이트 작업. 런타임이 끝날 때까지 돈다.
    pub async fn run(self: Arc<Self>) {
        let s = self.clone();
        let _ = tokio::task::spawn_blocking(move || s.startup()).await;
        let mut next = tokio::time::Instant::now() + self.cfg.first_delay;
        let mut tick = tokio::time::interval(self.cfg.poll);
        tick.set_missed_tick_behavior(tokio::time::MissedTickBehavior::Skip);
        loop {
            tokio::select! {
                _ = tokio::time::sleep_until(next) => {
                    self.lock().check_requested = true;
                    next = tokio::time::Instant::now() + self.cfg.period;
                }
                _ = self.kick.notified() => {}
                _ = tick.tick() => {
                    if self.lock().watch.is_some() {
                        let s = self.clone();
                        let _ = tokio::task::spawn_blocking(move || s.watch_tick()).await;
                    }
                }
            }
            if std::mem::take(&mut self.lock().check_requested) {
                let s = self.clone();
                let _ = tokio::task::spawn_blocking(move || s.check_blocking()).await;
            }
            if self.lock().m.install.is_some() {
                self.clone().install().await;
            }
        }
    }

    /// 서비스 시작: 폴더 준비, 상태 읽기, 지난 설치의 결과 판단.
    pub fn startup(&self) {
        let dir = &self.cfg.updates_dir;
        let secured = if self.cfg.secure_dir { self.host.secure_updates_dir(dir) } else { std::fs::create_dir_all(dir).map(|_| false) };
        match secured {
            Ok(true) => self.log("다른 사용자가 미리 만든 업데이트 폴더를 비우고 ACL을 다시 정했습니다"),
            Ok(false) => {}
            Err(e) => self.log(&format!("업데이트 폴더 준비 실패: {e}")),
        }
        let mut st = UpdateState::load(&state_path(dir));
        // 준비된 업데이트는 메모리에만 있으므로 시작 후 첫 확인은 304 없이 다시 받는다 (파일은 검증되면 재사용)
        st.etag = None;
        let current = self.cfg.current_version.clone();
        let mut relaunch = None;
        {
            let mut g = self.lock();
            g.st = st;
            if let Some(p) = g.st.pending.take() {
                let fresh = now_unix().saturating_sub(p.started_unix) < PENDING_MAX_AGE_SECS;
                if Version::parse(&p.version).is_ok_and(|v| v == current) {
                    self.finish_success(&mut g, &p);
                } else {
                    self.finish_failure(&mut g, &p, format!("설치 후에도 버전이 {current} 그대로입니다 (설치 실패 또는 취소, install-{}.log 참고)", p.version));
                }
                if p.relaunch && fresh {
                    relaunch = Some(p.session_id);
                }
            } else if let Some(rec) = g.st.install.clone()
                && !rec.confirmed
                && Version::parse(&rec.installed_version).is_ok_and(|v| v != current)
            {
                // 설치 기록은 새 버전인데 실제로는 그 버전이 아니다 (MSI가 끝에서 되돌아감 등)
                self.log(&format!("설치 기록 {}과 실제 버전 {current}이 달라 기록을 지웁니다", rec.installed_version));
                if let Ok(v) = Version::parse(&rec.installed_version) {
                    g.st.record_install_failure(&v);
                }
                g.st.install = None;
            }
            if self.watch_active(&g.st) {
                g.watch = Some(StartWatch::new(self.cfg.start_window));
            }
            self.save(&g);
        }
        if let Some(s) = relaunch {
            std::thread::sleep(self.cfg.relaunch_delay);
            self.relaunch(s);
        }
    }

    fn watch_active(&self, st: &UpdateState) -> bool {
        st.install
            .as_ref()
            .is_some_and(|r| !r.confirmed && Version::parse(&r.installed_version).is_ok_and(|v| v == self.cfg.current_version))
    }

    fn finish_success(&self, g: &mut Inner, p: &PendingInstall) {
        g.st.pending = None;
        g.st.installed_msi = Some(p.msi.clone());
        g.st.installed_asset = p.asset.clone();
        g.st.install_failure = None;
        g.m.install_succeeded();
        self.log(&format!("{} 설치 완료{}", p.version, if p.rollback { " (되돌리기)" } else { "" }));
    }

    fn finish_failure(&self, g: &mut Inner, p: &PendingInstall, why: String) {
        g.st.pending = None;
        if p.rollback {
            self.log(&format!("되돌리기 실패: {why}"));
            g.m.install_failed(why, true);
            return;
        }
        g.st.install = p.previous_record.clone();
        g.st.rollback_asset = None;
        let skipped = Version::parse(&p.version).is_ok_and(|v| g.st.record_install_failure(&v));
        let why = if skipped { format!("{why} — 계속 실패해 {} 버전은 건너뜁니다", p.version) } else { why };
        self.log(&format!("설치 실패: {why}"));
        g.m.install_failed(why, skipped);
    }

    fn relaunch(&self, session: u32) {
        let exe = self.cfg.foldy_exe();
        match self.host.launch_foldy(&exe, session) {
            Ok(pid) => self.log(&format!("세션 {session}에 Foldy 다시 띄움 (pid {pid})")),
            Err(e) => self.log(&format!("Foldy를 다시 띄우지 못함 (세션 {session}): {e}")),
        }
    }

    fn mark_healthy(&self, g: &mut Inner) {
        g.st.record_start(StartOutcome::Healthy);
        g.watch = None;
        self.save(g);
        self.log("새 버전이 정상 시작했습니다");
    }

    /// 한 번 확인 (블로킹: 다운로드 포함).
    pub fn check_blocking(&self) {
        let (mut st, channel) = {
            let mut g = self.lock();
            if !g.m.begin_check() {
                return;
            }
            (g.st.clone(), Self::channel(&g.st))
        };
        let result = {
            let _bg = win::BackgroundPriority::enter();
            self.run_check(&mut st, channel)
        };
        let mut g = self.lock();
        g.st.etag = st.etag;
        g.st.etag_channel = st.etag_channel;
        g.st.last_check_unix = st.last_check_unix;
        let mut checked = match result {
            Ok(Outcome::Ready(p)) => match p.manifest.asset(PackageKind::Msi) {
                Some(a) => Ok(Checked::Ready(Ready {
                    version: p.version.clone(),
                    path: p.path.clone(),
                    asset: a.clone(),
                    mandatory: p.mandatory,
                    notes: p.notes.clone(),
                })),
                None => Err("update.json에 MSI가 없습니다".to_string()),
            },
            Ok(Outcome::NotModified) => Ok(Checked::NotModified),
            Ok(Outcome::UpToDate | Outcome::Skipped(_)) => Ok(Checked::Nothing),
            Err(e) => {
                if e.is_security() {
                    self.log(&format!("경고: 업데이트 검증 실패 — {e}"));
                }
                Err(e.to_string())
            }
        };
        // 확인하는 사이 사용자가 건너뛴 버전
        if let Ok(Checked::Ready(r)) = &checked
            && !r.mandatory
            && g.st.is_skipped(&r.version)
        {
            checked = Ok(Checked::Nothing);
        }
        if let Ok(Checked::Ready(r)) = &checked {
            self.log(&format!("{} 설치 준비됨: {}", r.version, r.path.display()));
            self.prune(&g.st, &r.path);
        }
        g.m.finish_check(checked);
        self.save(&g);
    }

    fn run_check(&self, st: &mut UpdateState, channel: Channel) -> foldy_update::Result<Outcome> {
        let config = UpdaterConfig {
            api_base: self.cfg.api_base.clone(),
            repo: self.cfg.repo.clone(),
            channel,
            current_version: self.cfg.current_version.clone(),
            updates_dir: self.cfg.updates_dir.clone(),
            package: PackageKind::Msi,
            verifying_key: self.cfg.verifying_key,
            authenticode: AUTHENTICODE_POLICY,
            download_attempts: 3,
        };
        let mut progress = |d: u64, t: u64| self.lock().m.on_progress(d, t);
        if self.cfg.api_base.starts_with("http://") {
            // 로컬 시험 서버 (프록시·TLS 없음)
            Updater::new(config, PlainHttpClient::default()).run_once(st, &mut progress)
        } else {
            // 제품: WinHTTP + 시스템 프록시
            Updater::new(config, foldy_update::system_http_client()?).run_once(st, &mut progress)
        }
    }

    /// 새 MSI와 설치된(되돌리기용) MSI만 남긴다.
    fn prune(&self, st: &UpdateState, keep_new: &Path) {
        let Ok(rd) = std::fs::read_dir(&self.cfg.updates_dir) else { return };
        let entries: Vec<PathBuf> = rd.filter_map(|e| e.ok().map(|e| e.path())).collect();
        let mut keep: Vec<&Path> = vec![keep_new];
        if let Some(p) = &st.installed_msi {
            keep.push(p);
        }
        if let Some(p) = st.install.as_ref().and_then(|r| r.previous_msi.as_deref()) {
            keep.push(p);
        }
        for f in files_to_prune(&entries, &keep) {
            let _ = std::fs::remove_file(f);
        }
    }

    /// 설치 요청 처리: Foldy 종료를 기다린 뒤 설치한다.
    async fn install(self: Arc<Self>) {
        let Some((ready, req)) = self.lock().m.take_install() else { return };
        let exe = self.cfg.foldy_exe();
        let deadline = tokio::time::Instant::now() + self.cfg.idle_timeout;
        loop {
            let (h, e) = (self.host.clone(), exe.clone());
            let running = tokio::task::spawn_blocking(move || !h.foldy_processes(&e).is_empty()).await.unwrap_or(true);
            if !running {
                break;
            }
            if tokio::time::Instant::now() >= deadline {
                self.lock().m.install_deferred("Foldy가 아직 실행 중이라 설치를 미뤘습니다".into());
                self.log("Foldy가 끝나지 않아 설치를 미룸");
                return;
            }
            tokio::time::sleep(self.cfg.poll).await;
        }
        let s = self.clone();
        let _ = tokio::task::spawn_blocking(move || s.install_blocking(ready, req)).await;
    }

    fn install_blocking(&self, ready: Ready, req: InstallRequest) {
        let dir = &self.cfg.updates_dir;
        // 쓰기를 막은 채 다시 검증하고, msiexec가 끝날 때까지 이 핸들을 쥐고 있다 (바꿔치기 방지)
        let locked = match verify_locked(&ready.path, &ready.asset) {
            Ok(f) => f,
            Err(e) => {
                if e.is_security() {
                    let _ = std::fs::remove_file(&ready.path);
                }
                let why = format!("설치 직전 검증 실패: {e}");
                self.log(&why);
                self.lock().m.install_failed(why, true);
                return;
            }
        };
        let opts = InstallOptions { rollback: false, log: Some(dir.join(format!("install-{}.log", ready.version))) };
        let cmd = match install_command_with(dir, &ready.path, &opts) {
            Ok(c) => c,
            Err(e) => {
                let why = e.to_string();
                self.log(&why);
                self.lock().m.install_failed(why, true);
                return;
            }
        };
        let pending = {
            let mut g = self.lock();
            let prev_msi = g.st.installed_msi.clone().filter(|p| p.is_file() && p != &ready.path);
            let prev_asset = prev_msi.as_ref().and(g.st.installed_asset.clone());
            let previous_record = g.st.install.clone();
            g.st.record_install(&ready.version, Some(&self.cfg.current_version), prev_msi);
            g.st.rollback_asset = prev_asset;
            let p = PendingInstall {
                version: ready.version.to_string(),
                msi: ready.path.clone(),
                relaunch: req.relaunch,
                session_id: req.session_id,
                rollback: false,
                started_unix: now_unix(),
                previous_record,
                asset: Some(ready.asset.clone()),
            };
            g.st.pending = Some(p.clone());
            self.save(&g);
            p
        };
        self.log(&format!("설치 시작: {}", describe(&cmd)));
        let r = self.host.run_installer(cmd);
        drop(locked);
        self.after_installer(&pending, r);
    }

    /// msiexec가 끝났는데 이 서비스가 아직 살아 있을 때 (실패했거나, MSI가 서비스를 건드리지 않았을 때).
    fn after_installer(&self, p: &PendingInstall, r: std::io::Result<i32>) {
        {
            let mut g = self.lock();
            match r {
                // 0 성공, 3010/1641 다시 시작 필요 (설치 자체는 성공)
                Ok(0 | 3010 | 1641) => self.finish_success(&mut g, p),
                Ok(code) => self.finish_failure(&mut g, p, format!("msiexec 종료 코드 {code} (install-{}.log 참고)", p.version)),
                Err(e) => self.finish_failure(&mut g, p, format!("msiexec를 실행하지 못함: {e}")),
            }
            if self.watch_active(&g.st) {
                g.watch = Some(StartWatch::new(self.cfg.start_window));
            }
            self.save(&g);
        }
        if p.relaunch {
            std::thread::sleep(self.cfg.relaunch_delay);
            self.relaunch(p.session_id);
        }
    }

    /// 시작 감시 한 번 (블로킹).
    fn watch_tick(&self) {
        let pids = self.host.foldy_processes(&self.cfg.foldy_exe());
        let mut g = self.lock();
        let Some(ev) = g.watch.as_mut().and_then(|w| w.observe(Instant::now(), &pids)) else { return };
        match ev {
            WatchEvent::Healthy => self.mark_healthy(&mut g),
            WatchEvent::Failed => {
                let decision = g.st.record_start(StartOutcome::Failed);
                self.save(&g);
                match decision {
                    RollbackDecision::Watch { failed_starts } => {
                        self.log(&format!("새 버전 시작 실패 {failed_starts}회"));
                    }
                    RollbackDecision::Rollback { msi, to_version, skip } => {
                        g.watch = None;
                        self.log(&format!("새 버전 {skip}이 연속으로 시작하지 못해 {to_version}으로 되돌립니다"));
                        let session = self.host.active_console_session();
                        let job = self.prepare_rollback(&mut g, &msi, &to_version, session);
                        drop(g);
                        if let Some((cmd, pending, locked)) = job {
                            let r = self.host.run_installer(cmd);
                            drop(locked);
                            self.after_installer(&pending, r);
                        }
                    }
                    RollbackDecision::NoRollbackAvailable { failed_starts } => {
                        g.watch = None;
                        g.m.last_error = Some(format!("새 버전이 {failed_starts}번 연속 시작하지 못했지만 되돌릴 이전 MSI가 없습니다"));
                        self.log("되돌릴 이전 MSI 없음");
                    }
                    RollbackDecision::None => g.watch = None,
                }
            }
        }
    }

    fn prepare_rollback(
        &self,
        g: &mut Inner,
        msi: &Path,
        to_version: &str,
        session: u32,
    ) -> Option<(std::process::Command, PendingInstall, std::fs::File)> {
        let dir = &self.cfg.updates_dir;
        let fail = |g: &mut Inner, why: String| {
            self.log(&why);
            g.m.install_failed(why, false);
            self.save(g);
            None
        };
        let Some(asset) = g.st.rollback_asset.clone() else {
            return fail(g, "되돌릴 MSI의 검증 정보가 없어 되돌리지 않습니다".into());
        };
        let locked = match verify_locked(msi, &asset) {
            Ok(f) => f,
            Err(e) => return fail(g, format!("되돌릴 MSI 검증 실패: {e}")),
        };
        let opts = InstallOptions { rollback: true, log: Some(dir.join(format!("rollback-{to_version}.log"))) };
        let cmd = match install_command_with(dir, msi, &opts) {
            Ok(c) => c,
            Err(e) => return fail(g, e.to_string()),
        };
        let pending = PendingInstall {
            version: to_version.to_string(),
            msi: msi.to_path_buf(),
            relaunch: true,
            session_id: session,
            rollback: true,
            started_unix: now_unix(),
            previous_record: None,
            asset: Some(asset),
        };
        g.st.pending = Some(pending.clone());
        g.m.phase = machine::Phase::Installing;
        g.m.last_error = Some(format!("{to_version}으로 되돌리는 중"));
        self.save(g);
        self.log(&format!("되돌리기 시작: {}", describe(&cmd)));
        Some((cmd, pending, locked))
    }
}

#[cfg(test)]
mod tests;
