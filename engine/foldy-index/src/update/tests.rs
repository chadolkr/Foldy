//! 업데이트 서비스 시험: 로컬 가짜 릴리스 서버 + 가짜 호스트 (실제 msiexec는 실행하지 않는다).

use super::host::FakeHost;
use super::*;
use ed25519_dalek::SigningKey;
use foldy_update::manifest::{ManifestAsset, UpdateManifest};
use foldy_update::state::InstallRecord;
use std::collections::HashMap;
use std::io::{BufRead, BufReader};
use std::net::TcpListener;

const SEED: [u8; 32] = [7u8; 32];

fn payload(n: usize, salt: u8) -> Vec<u8> {
    (0..n).map(|i| ((i * 31 + 7) % 251) as u8 ^ salt).collect()
}

fn temp_dir(tag: &str) -> PathBuf {
    let d = std::env::temp_dir().join(format!("foldy-index-upd-{tag}-{}", std::process::id()));
    let _ = std::fs::remove_dir_all(&d);
    std::fs::create_dir_all(&d).unwrap();
    d
}

/// 경로 → 본문만 아는 작은 HTTP 서버.
struct Server {
    base: String,
    routes: Arc<Mutex<HashMap<String, Vec<u8>>>>,
}

impl Server {
    fn start() -> Server {
        let l = TcpListener::bind("127.0.0.1:0").unwrap();
        let base = format!("http://{}", l.local_addr().unwrap());
        let routes: Arc<Mutex<HashMap<String, Vec<u8>>>> = Arc::default();
        let r = routes.clone();
        std::thread::spawn(move || {
            for s in l.incoming().flatten() {
                let r = r.clone();
                std::thread::spawn(move || {
                    let mut rd = BufReader::new(s.try_clone().unwrap());
                    let mut line = String::new();
                    let _ = rd.read_line(&mut line);
                    loop {
                        let mut h = String::new();
                        if rd.read_line(&mut h).unwrap_or(0) == 0 || h.trim().is_empty() {
                            break;
                        }
                    }
                    let path = line.split_whitespace().nth(1).unwrap_or("/").to_string();
                    let body = r.lock().unwrap().get(&path).cloned();
                    let mut s = s;
                    match body {
                        Some(b) => {
                            let _ = write!(s, "HTTP/1.1 200 OK\r\nContent-Length: {}\r\nConnection: close\r\n\r\n", b.len());
                            let _ = s.write_all(&b);
                        }
                        None => {
                            let _ = s.write_all(b"HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                        }
                    }
                });
            }
        });
        Server { base, routes }
    }

    fn route(&self, path: &str, body: Vec<u8>) {
        self.routes.lock().unwrap().insert(path.into(), body);
    }

    /// 서명된 v{version} 릴리스를 올린다. 반환: MSI 자산.
    fn release(&self, version: &str, msi: &[u8], work: &Path) -> ManifestAsset {
        let name = format!("Foldy-{version}-x64.msi");
        let f = work.join(&name);
        std::fs::write(&f, msi).unwrap();
        let m = UpdateManifest::build(version, Channel::Stable, None, false, &[&f]).unwrap();
        let json = m.to_json_pretty().into_bytes();
        let sig = foldy_update::sign::sign(&SigningKey::from_bytes(&SEED), &json);
        self.route("/dl/update.json", json);
        self.route("/dl/update.json.sig", sig.into_bytes());
        self.route(&format!("/dl/{name}"), msi.to_vec());
        let rel = format!(
            r#"{{"tag_name":"v{version}","prerelease":false,"draft":false,"body":"변경 내역","html_url":"","assets":[
            {{"name":"update.json","size":1,"browser_download_url":"{b}/dl/update.json"}},
            {{"name":"update.json.sig","size":1,"browser_download_url":"{b}/dl/update.json.sig"}},
            {{"name":"{name}","size":{n},"browser_download_url":"{b}/dl/{name}"}}]}}"#,
            b = self.base,
            n = msi.len()
        );
        self.route("/repos/chadolkr/Foldy/releases/latest", rel.into_bytes());
        m.assets[0].clone()
    }
}

fn config(srv: &Server, dir: &Path, current: &str) -> UpdateConfig {
    UpdateConfig {
        api_base: srv.base.clone(),
        repo: "chadolkr/Foldy".into(),
        updates_dir: dir.join("Updates"),
        verifying_key: SigningKey::from_bytes(&SEED).verifying_key(),
        current_version: Version::parse(current).unwrap(),
        install_dir: dir.join("app"),
        first_delay: Duration::from_secs(3600),
        period: Duration::from_secs(3600),
        idle_timeout: Duration::from_millis(300),
        start_window: Duration::from_secs(60),
        relaunch_delay: Duration::ZERO,
        poll: Duration::from_millis(20),
        secure_dir: false,
    }
}

fn frame_status(f: &[u8]) -> UpdateStatusReply {
    let (_, ty) = decode_header(f).unwrap();
    assert_eq!(ty, MsgType::UpdateStatusReply, "{:?}", decode_body::<ErrorReply>(&f[HEADER_LEN..]));
    decode_body(&f[HEADER_LEN..]).unwrap()
}

fn frame_error(f: &[u8]) -> ErrorReply {
    let (_, ty) = decode_header(f).unwrap();
    assert_eq!(ty, MsgType::Error);
    decode_body(&f[HEADER_LEN..]).unwrap()
}

/// 프레임 본문만 (handle()은 헤더를 뺀 본문을 받는다).
macro_rules! body {
    ($ty:expr, $m:expr) => {
        encode($ty, $m)[HEADER_LEN..].to_vec()
    };
}

fn install_req(relaunch: bool, session: u32) -> Vec<u8> {
    body!(MsgType::InstallWhenIdle, &InstallWhenIdle { relaunch, session_id: session })
}

fn rt() -> tokio::runtime::Runtime {
    tokio::runtime::Builder::new_multi_thread().worker_threads(2).enable_all().build().unwrap()
}

fn wait_for(svc: &UpdateService, what: impl Fn(&UpdateStatusReply) -> bool) -> UpdateStatusReply {
    let t = Instant::now();
    loop {
        let s = svc.status();
        if what(&s) || t.elapsed() > Duration::from_secs(15) {
            return s;
        }
        std::thread::sleep(Duration::from_millis(20));
    }
}

#[test]
fn check_download_install_and_relaunch() {
    let dir = temp_dir("flow");
    let srv = Server::start();
    let msi = payload(200_000, 0);
    srv.release("1.2.3", &msi, &dir);
    let host = Arc::new(FakeHost::new(None));
    let svc = UpdateService::new(config(&srv, &dir, "1.0.0"), host.clone());
    let rt = rt();
    rt.spawn(svc.clone().run());

    let s = frame_status(&svc.handle(MsgType::UpdateStatus, &[], None));
    assert_eq!(s.current_version, "1.0.0");
    // 준비 전 설치 요청은 거부
    assert_eq!(frame_error(&svc.handle(MsgType::InstallWhenIdle, &install_req(true, 1), None)).code, update_error::NOT_READY);

    frame_status(&svc.handle(MsgType::CheckNow, &[], None));
    let s = wait_for(&svc, |s| s.state == update_state::READY_TO_INSTALL);
    assert_eq!(s.state, update_state::READY_TO_INSTALL, "{}", s.last_error);
    assert_eq!(s.available_version, "1.2.3");
    assert_eq!(s.notes, "변경 내역");
    assert_eq!((s.downloaded, s.total, s.progress_permille), (200_000, 200_000, 1000));
    assert!(s.last_check_unix > 0);
    let path = dir.join("Updates").join("Foldy-1.2.3-x64.msi");
    assert_eq!(std::fs::read(&path).unwrap(), msi);

    // 파이프 클라이언트 세션 5가 요청 → 요청 값(9)보다 실제 세션이 우선
    let s = frame_status(&svc.handle(MsgType::InstallWhenIdle, &install_req(true, 9), Some(5)));
    assert!(s.install_pending || s.state == update_state::INSTALLING || s.state == update_state::IDLE);
    let s = wait_for(&svc, |s| s.state == update_state::IDLE);
    assert_eq!(s.state, update_state::IDLE, "{}", s.last_error);
    let t = Instant::now();
    while host.lines().len() < 2 && t.elapsed() < Duration::from_secs(10) {
        std::thread::sleep(Duration::from_millis(20));
    }

    let lines = host.lines();
    assert_eq!(lines.len(), 2, "{lines:?}");
    let canon = foldy_update::install::strip_verbatim(&path.canonicalize().unwrap());
    assert!(lines[0].starts_with("install "), "{}", lines[0]);
    assert!(lines[0].to_ascii_lowercase().contains(r"system32\msiexec.exe /i "));
    assert!(lines[0].contains(&format!("{} /qn /norestart /l*v ", canon.display())), "{}", lines[0]);
    assert!(!lines[0].contains("FOLDY_ROLLBACK"));
    assert!(lines[1].starts_with("relaunch ") && lines[1].ends_with(r"app\Foldy.exe session=5"), "{}", lines[1]);

    let st = UpdateState::load(&state_path(&dir.join("Updates")));
    assert!(st.pending.is_none());
    assert_eq!(st.installed_msi.as_deref(), Some(path.as_path()));
    assert_eq!(st.install.as_ref().unwrap().installed_version, "1.2.3");
    assert_eq!(st.install.as_ref().unwrap().previous_version.as_deref(), Some("1.0.0"));
    drop(rt);
    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn install_waits_for_foldy_to_exit_then_defers() {
    let dir = temp_dir("defer");
    let srv = Server::start();
    srv.release("1.2.3", &payload(5000, 1), &dir);
    let host = Arc::new(FakeHost::new(None));
    *host.processes.lock().unwrap() = vec![77];
    let svc = UpdateService::new(config(&srv, &dir, "1.0.0"), host.clone());
    let rt = rt();
    rt.spawn(svc.clone().run());
    svc.handle(MsgType::CheckNow, &[], None);
    wait_for(&svc, |s| s.state == update_state::READY_TO_INSTALL);
    frame_status(&svc.handle(MsgType::InstallWhenIdle, &install_req(false, 1), None));
    let s = wait_for(&svc, |s| !s.last_error.is_empty());
    assert_eq!(s.state, update_state::READY_TO_INSTALL);
    assert!(s.last_error.contains("실행 중"), "{}", s.last_error);
    assert!(host.lines().is_empty(), "Foldy가 떠 있는 동안에는 설치하지 않는다");

    // Foldy가 끝나면 다음 요청에서 설치
    host.processes.lock().unwrap().clear();
    svc.handle(MsgType::InstallWhenIdle, &install_req(false, 1), None);
    wait_for(&svc, |s| s.state == update_state::IDLE);
    assert_eq!(host.lines().len(), 1, "다시 띄우기 요청이 없으면 설치만");
    drop(rt);
    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn relaunch_asked_while_waiting_for_foldy_is_honored() {
    // 2026-11-21 실제 사례: Foldy를 닫아 "다시 띄우지 않는 설치"가 Foldy 종료를 기다리는 동안
    // 다시 연 Foldy에서 [재시작하고 업데이트]를 누르면 그 요청이 무시되어 설치 후 Foldy가 뜨지 않았다
    let dir = temp_dir("late");
    let srv = Server::start();
    srv.release("1.2.3", &payload(5000, 2), &dir);
    let host = Arc::new(FakeHost::new(None));
    let mut cfg = config(&srv, &dir, "1.0.0");
    cfg.idle_timeout = Duration::from_secs(10);
    *host.processes.lock().unwrap() = vec![77];
    let svc = UpdateService::new(cfg, host.clone());
    let rt = rt();
    rt.spawn(svc.clone().run());
    svc.handle(MsgType::CheckNow, &[], None);
    wait_for(&svc, |s| s.state == update_state::READY_TO_INSTALL);
    frame_status(&svc.handle(MsgType::InstallWhenIdle, &install_req(false, 1), None)); // 닫으면서 설치
    wait_for(&svc, |s| s.state == update_state::INSTALLING);
    frame_status(&svc.handle(MsgType::InstallWhenIdle, &install_req(true, 6), Some(6))); // 다시 연 Foldy에서 버튼
    host.processes.lock().unwrap().clear();
    let t = Instant::now();
    while host.lines().len() < 2 && t.elapsed() < Duration::from_secs(10) {
        std::thread::sleep(Duration::from_millis(20));
    }
    let lines = host.lines();
    assert_eq!(lines.len(), 2, "{lines:?}");
    assert!(lines[1].starts_with("relaunch ") && lines[1].ends_with("session=6"), "{}", lines[1]);
    drop(rt);
    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn newer_release_replaces_the_one_waiting_to_install() {
    // 받아 둔 업데이트(1.2.3)가 설치를 기다리는 동안 1.3.0이 나오면, 확인할 때 1.3.0으로 바뀌고 옛 파일은 지운다
    let dir = temp_dir("newer");
    let srv = Server::start();
    srv.release("1.2.3", &payload(5000, 3), &dir);
    let host = Arc::new(FakeHost::new(None));
    let svc = UpdateService::new(config(&srv, &dir, "1.0.0"), host.clone());
    let rt = rt();
    rt.spawn(svc.clone().run());
    svc.handle(MsgType::CheckNow, &[], None);
    let s = wait_for(&svc, |s| s.state == update_state::READY_TO_INSTALL);
    assert_eq!(s.available_version, "1.2.3");
    srv.release("1.3.0", &payload(6000, 4), &dir);
    svc.handle(MsgType::CheckNow, &[], None);
    let s = wait_for(&svc, |s| s.state == update_state::READY_TO_INSTALL && s.available_version == "1.3.0");
    assert_eq!(s.available_version, "1.3.0", "{}", s.last_error);
    assert!(!dir.join("Updates").join("Foldy-1.2.3-x64.msi").exists(), "옛 파일은 지운다");
    drop(rt);
    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn client_cannot_choose_the_installed_file() {
    let dir = temp_dir("paths");
    let srv = Server::start();
    let msi = payload(4000, 2);
    let asset = srv.release("1.2.3", &msi, &dir);
    let host = Arc::new(FakeHost::new(None));
    let svc = UpdateService::new(config(&srv, &dir, "1.0.0"), host.clone());
    svc.startup();
    svc.check_blocking();
    assert_eq!(svc.status().state, update_state::READY_TO_INSTALL);

    // 1) 메시지에 경로를 덧붙여도 무시된다 (InstallWhenIdle에는 경로 필드가 없다)
    let mut b = install_req(false, 1);
    b.extend_from_slice(&body!(MsgType::SkipVersion, &SkipVersion { version: r"C:\evil\x.msi".into() }));
    frame_status(&svc.handle(MsgType::InstallWhenIdle, &b, None));
    let (ready, req) = svc.lock().m.take_install().unwrap();
    assert_eq!(ready.path, dir.join("Updates").join("Foldy-1.2.3-x64.msi"));

    // 2) 준비된 경로가 (어떻게든) 업데이트 폴더 밖을 가리키면, 내용이 같아도 설치하지 않는다
    let outside = dir.join("Foldy-1.2.3-x64.msi");
    assert_eq!(std::fs::read(&outside).unwrap(), msi);
    svc.install_blocking(Ready { path: outside, ..ready.clone() }, req);
    assert!(host.lines().is_empty());
    assert_eq!(svc.status().state, update_state::FAILED);
    assert!(svc.status().last_error.contains("허용되지 않은 설치 경로"), "{}", svc.status().last_error);

    // 3) 업데이트 폴더 안의 파일이 바뀌었으면 해시 검사에서 막고 파일을 지운다
    std::fs::write(&ready.path, payload(4000, 3)).unwrap();
    svc.install_blocking(Ready { asset: asset.clone(), ..ready.clone() }, req);
    assert!(host.lines().is_empty());
    assert!(!ready.path.exists());
    assert!(svc.status().last_error.contains("SHA-256"), "{}", svc.status().last_error);
    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn locked_file_cannot_be_replaced_during_install() {
    let dir = temp_dir("lock");
    let f = dir.join("a.msi");
    let data = payload(1000, 4);
    std::fs::write(&f, &data).unwrap();
    let (size, sha256) = foldy_update::manifest::sha256_file(&f).unwrap();
    let asset = ManifestAsset { name: "a.msi".into(), size, sha256 };
    let held = verify_locked(&f, &asset).unwrap();
    assert!(std::fs::OpenOptions::new().write(true).open(&f).is_err(), "검증 핸들을 쥔 동안 쓰기 금지");
    assert!(std::fs::read(&f).is_ok(), "읽기는 허용 (msiexec)");
    drop(held);
    assert!(std::fs::OpenOptions::new().write(true).open(&f).is_ok());
    std::fs::remove_dir_all(&dir).ok();
}

fn pending(version: &str, msi: PathBuf, relaunch: bool) -> PendingInstall {
    PendingInstall {
        version: version.into(),
        msi,
        relaunch,
        session_id: 3,
        rollback: false,
        started_unix: now_unix(),
        previous_record: Some(InstallRecord {
            installed_version: "1.0.0".into(),
            previous_version: None,
            previous_msi: None,
            failed_starts: 0,
            confirmed: true,
        }),
        asset: None,
    }
}

#[test]
fn restart_after_msi_confirms_success_and_relaunches() {
    let dir = temp_dir("restart-ok");
    let srv = Server::start();
    let upd = dir.join("Updates");
    std::fs::create_dir_all(&upd).unwrap();
    let mut st = UpdateState::default();
    st.record_install(&Version::new(1, 2, 3), Some(&Version::new(1, 0, 0)), None);
    st.pending = Some(pending("1.2.3", upd.join("Foldy-1.2.3-x64.msi"), true));
    st.save(&state_path(&upd)).unwrap();

    // MSI가 서비스를 바꾼 뒤의 새 서비스 = 1.2.3
    let host = Arc::new(FakeHost::new(None));
    let svc = UpdateService::new(config(&srv, &dir, "1.2.3"), host.clone());
    svc.startup();
    assert_eq!(host.lines(), vec![format!("relaunch {} session=3", dir.join("app").join("Foldy.exe").display())]);
    let st = UpdateState::load(&state_path(&upd));
    assert!(st.pending.is_none());
    assert_eq!(st.installed_msi, Some(upd.join("Foldy-1.2.3-x64.msi")));
    // 시작 감시가 켜지고, AppStarted가 오면 확정
    assert!(svc.lock().watch.is_some());
    frame_status(&svc.handle(MsgType::AppStarted, &body!(MsgType::AppStarted, &AppStarted { version: "1.2.3".into() }), None));
    assert!(svc.lock().watch.is_none());
    assert!(UpdateState::load(&state_path(&upd)).install.unwrap().confirmed);
    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn restart_with_old_version_means_install_failed() {
    let dir = temp_dir("restart-fail");
    let srv = Server::start();
    let upd = dir.join("Updates");
    std::fs::create_dir_all(&upd).unwrap();
    for round in 1..=2 {
        let mut st = UpdateState::load(&state_path(&upd));
        st.record_install(&Version::new(1, 2, 3), Some(&Version::new(1, 0, 0)), None);
        st.pending = Some(pending("1.2.3", upd.join("Foldy-1.2.3-x64.msi"), false));
        st.save(&state_path(&upd)).unwrap();
        let host = Arc::new(FakeHost::new(None));
        let svc = UpdateService::new(config(&srv, &dir, "1.0.0"), host.clone());
        svc.startup();
        assert!(host.lines().is_empty(), "다시 띄우기 요청이 없었다");
        let s = svc.status();
        assert_eq!(s.state, update_state::FAILED);
        let st = UpdateState::load(&state_path(&upd));
        assert_eq!(st.install.as_ref().unwrap().installed_version, "1.0.0", "설치 기록을 되돌린다");
        assert_eq!(st.is_skipped(&Version::new(1, 2, 3)), round == 2, "두 번 연속 실패하면 건너뛴다");
    }
    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn two_failed_starts_roll_back_to_previous_msi() {
    let dir = temp_dir("rollback");
    let srv = Server::start();
    let upd = dir.join("Updates");
    std::fs::create_dir_all(&upd).unwrap();
    let prev = upd.join("Foldy-1.2.2-x64.msi");
    std::fs::write(&prev, payload(3000, 5)).unwrap();
    let (size, sha256) = foldy_update::manifest::sha256_file(&prev).unwrap();
    let mut st = UpdateState::default();
    st.record_install(&Version::new(1, 2, 3), Some(&Version::new(1, 2, 2)), Some(prev.clone()));
    st.rollback_asset = Some(ManifestAsset { name: "Foldy-1.2.2-x64.msi".into(), size, sha256 });
    st.save(&state_path(&upd)).unwrap();

    let host = Arc::new(FakeHost::new(None));
    let svc = UpdateService::new(config(&srv, &dir, "1.2.3"), host.clone());
    svc.startup();
    assert!(svc.lock().watch.is_some());
    for _ in 0..2 {
        // Foldy.exe가 떴다가 60초 안에 사라진다
        *host.processes.lock().unwrap() = vec![100];
        svc.watch_tick();
        host.processes.lock().unwrap().clear();
        svc.watch_tick();
    }
    let lines = host.lines();
    assert_eq!(lines.len(), 2, "{lines:?}");
    assert!(lines[0].contains(&format!("{} /qn /norestart", foldy_update::install::strip_verbatim(&prev.canonicalize().unwrap()).display())), "{}", lines[0]);
    assert!(lines[0].ends_with(foldy_update::install::ROLLBACK_PROPERTY), "{}", lines[0]);
    assert!(lines[1].starts_with("relaunch "));
    let st = UpdateState::load(&state_path(&upd));
    assert!(st.is_skipped(&Version::new(1, 2, 3)));
    assert_eq!(st.install.as_ref().unwrap().installed_version, "1.2.2");
    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn rollback_refuses_unverified_previous_msi() {
    let dir = temp_dir("rollback-tampered");
    let srv = Server::start();
    let upd = dir.join("Updates");
    std::fs::create_dir_all(&upd).unwrap();
    let prev = upd.join("Foldy-1.2.2-x64.msi");
    std::fs::write(&prev, b"planted").unwrap();
    let mut st = UpdateState::default();
    st.record_install(&Version::new(1, 2, 3), Some(&Version::new(1, 2, 2)), Some(prev));
    st.rollback_asset = Some(ManifestAsset { name: "Foldy-1.2.2-x64.msi".into(), size: 7, sha256: "0".repeat(64) });
    st.save(&state_path(&upd)).unwrap();
    let host = Arc::new(FakeHost::new(None));
    let svc = UpdateService::new(config(&srv, &dir, "1.2.3"), host.clone());
    svc.startup();
    for _ in 0..2 {
        *host.processes.lock().unwrap() = vec![100];
        svc.watch_tick();
        host.processes.lock().unwrap().clear();
        svc.watch_tick();
    }
    assert!(host.lines().is_empty(), "검증되지 않는 MSI로는 되돌리지 않는다");
    assert!(svc.status().last_error.contains("검증 실패"), "{}", svc.status().last_error);
    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn skip_and_channel_messages() {
    let dir = temp_dir("skip");
    let srv = Server::start();
    srv.release("1.2.3", &payload(1000, 6), &dir);
    let host = Arc::new(FakeHost::new(None));
    let svc = UpdateService::new(config(&srv, &dir, "1.0.0"), host);
    svc.startup();
    svc.check_blocking();
    assert_eq!(svc.status().available_version, "1.2.3");
    let s = frame_status(&svc.handle(MsgType::SkipVersion, &body!(MsgType::SkipVersion, &SkipVersion { version: "v1.2.3".into() }), None));
    assert_eq!((s.state, s.available_version.as_str()), (update_state::IDLE, ""));
    // 건너뛴 버전은 다시 확인해도 준비되지 않는다
    svc.check_blocking();
    assert_eq!(svc.status().state, update_state::IDLE);
    assert_eq!(
        frame_error(&svc.handle(MsgType::SkipVersion, &body!(MsgType::SkipVersion, &SkipVersion { version: "abc".into() }), None)).code,
        update_error::BAD_ARGUMENT
    );
    let s = frame_status(&svc.handle(MsgType::SetChannel, &body!(MsgType::SetChannel, &SetChannel { channel: update_channel::BETA }), None));
    assert_eq!(s.channel, update_channel::BETA);
    assert_eq!(UpdateState::load(&state_path(&dir.join("Updates"))).channel, Some(Channel::Beta));
    assert_eq!(
        frame_error(&svc.handle(MsgType::SetChannel, &body!(MsgType::SetChannel, &SetChannel { channel: 7 }), None)).code,
        update_error::BAD_ARGUMENT
    );
    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn bad_signature_is_never_ready() {
    let dir = temp_dir("badsig");
    let srv = Server::start();
    srv.release("1.2.3", &payload(1000, 7), &dir);
    let other = SigningKey::from_bytes(&[9u8; 32]);
    let json = srv.routes.lock().unwrap()["/dl/update.json"].clone();
    srv.route("/dl/update.json.sig", foldy_update::sign::sign(&other, &json).into_bytes());
    let svc = UpdateService::new(config(&srv, &dir, "1.0.0"), Arc::new(FakeHost::new(None)));
    svc.startup();
    svc.check_blocking();
    let s = svc.status();
    assert_eq!(s.state, update_state::FAILED);
    assert!(s.last_error.contains("서명"), "{}", s.last_error);
    assert!(!dir.join("Updates").join("Foldy-1.2.3-x64.msi").exists(), "서명이 틀리면 내려받지도 않는다");
    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn disabled_updater_reports_error_code() {
    // 이 시험 프로세스에서는 전역 서비스를 등록하지 않는다
    let f = handle_pipe(MsgType::UpdateStatus, &[], None);
    assert_eq!(frame_error(&f).code, update_error::DISABLED);
}

#[test]
fn console_args_enable_updater_only_with_api() {
    assert!(UpdateConfig::from_console_args(&[]).unwrap().is_none());
    let dir = temp_dir("args");
    let key = dir.join("k.pub");
    std::fs::write(&key, foldy_update::sign::verifying_key_to_text(&SigningKey::from_bytes(&SEED).verifying_key())).unwrap();
    let a = |k: &str, v: &str| (k.to_string(), v.to_string());
    let (cfg, fake) = UpdateConfig::from_console_args(&[
        a("--update-api", "http://127.0.0.1:1/"),
        a("--update-pubkey", key.to_str().unwrap()),
        a("--update-current", "1.0.0"),
        a("--update-dir", dir.to_str().unwrap()),
        a("--update-fake-install", "x.log"),
    ])
    .unwrap()
    .unwrap();
    assert_eq!(cfg.api_base, "http://127.0.0.1:1");
    assert_eq!(cfg.current_version, Version::new(1, 0, 0));
    assert!(!cfg.secure_dir, "콘솔 모드는 폴더 ACL을 건드리지 않는다");
    assert_eq!(fake, Some(PathBuf::from("x.log")));
    std::fs::remove_dir_all(&dir).ok();
}
