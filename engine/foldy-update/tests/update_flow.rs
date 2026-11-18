//! 업데이트 흐름 통합 시험: 서명된/서명 없는/변조된 update.json, ETag/304, Range 이어받기.
//! GitHub API는 부르지 않는다 — 로컬 시험 서버가 GitHub 응답 모양을 흉내 낸다.

mod common;

use common::{Route, TestServer, fixture, payload, temp_dir};
use ed25519_dalek::SigningKey;
use foldy_update::download::{download_resumable, download_with_retries, part_path};
use foldy_update::github::{self, CheckResult};
use foldy_update::http::{HttpClient, PlainHttpClient};
use foldy_update::install::AuthenticodePolicy;
use foldy_update::manifest::{Channel, PackageKind};
use foldy_update::sign;
use foldy_update::state::UpdateState;
use foldy_update::updater::{Outcome, Updater, UpdaterConfig};
use foldy_update::{UpdateError, Version};

/// 시험 전용 서명 키 시드 (fixtures/test-signing.pub와 짝).
const TEST_SEED: [u8; 32] = [7u8; 32];

fn test_key() -> ed25519_dalek::VerifyingKey {
    let vk = SigningKey::from_bytes(&TEST_SEED).verifying_key();
    assert_eq!(
        vk,
        sign::parse_verifying_key(&String::from_utf8(fixture("test-signing.pub")).unwrap())
            .unwrap()
    );
    vk
}

fn release_json(srv: &TestServer, tag: &str, prerelease: bool, with_sig: bool) -> String {
    let mut assets = vec![
        format!(
            r#"{{"name":"update.json","size":1,"browser_download_url":"{}"}}"#,
            srv.url("/dl/update.json")
        ),
        format!(
            r#"{{"name":"Foldy-1.2.3-x64.msi","size":300000,"browser_download_url":"{}"}}"#,
            srv.url("/dl/Foldy-1.2.3-x64.msi")
        ),
        format!(
            r#"{{"name":"Foldy-1.2.3-x64-portable.zip","size":1000,"browser_download_url":"{}"}}"#,
            srv.url("/dl/portable.zip")
        ),
    ];
    if with_sig {
        assets.push(format!(
            r#"{{"name":"update.json.sig","size":1,"browser_download_url":"{}"}}"#,
            srv.url("/dl/update.json.sig")
        ));
    }
    format!(
        r#"{{"tag_name":"{tag}","prerelease":{prerelease},"draft":false,"body":"변경 내역","html_url":"https://example.invalid/r","assets":[{}]}}"#,
        assets.join(",")
    )
}

/// update.json으로 쓸 픽스처 이름을 받아 서버를 꾸민다.
fn server(manifest_fixture: &str, with_sig: bool) -> TestServer {
    let srv = TestServer::start();
    srv.route(
        "/repos/chadolkr/Foldy/releases/latest",
        Route {
            body: release_json(&srv, "v1.2.3", false, with_sig).into_bytes(),
            etag: Some("\"rel-1\"".into()),
            ..Route::default()
        },
    );
    srv.route(
        "/dl/update.json",
        Route {
            body: fixture(manifest_fixture),
            ..Route::default()
        },
    );
    srv.route(
        "/dl/update.json.sig",
        Route {
            body: fixture("update.json.sig"),
            ..Route::default()
        },
    );
    srv.route(
        "/dl/Foldy-1.2.3-x64.msi",
        Route {
            body: payload(),
            ..Route::default()
        },
    );
    srv.route(
        "/dl/portable.zip",
        Route {
            body: payload()[..1000].to_vec(),
            ..Route::default()
        },
    );
    srv
}

fn updater<C: HttpClient>(
    srv: &TestServer,
    client: C,
    dir: &std::path::Path,
    current: &str,
) -> Updater<C> {
    Updater::new(
        UpdaterConfig {
            api_base: srv.base.clone(),
            repo: "chadolkr/Foldy".into(),
            channel: Channel::Stable,
            current_version: Version::parse(current).unwrap(),
            updates_dir: dir.to_owned(),
            package: PackageKind::Msi,
            verifying_key: test_key(),
            authenticode: AuthenticodePolicy::Disabled,
            download_attempts: 3,
        },
        client,
    )
}

#[test]
fn signed_manifest_downloads_and_verifies() {
    let srv = server("update.json", true);
    let dir = temp_dir("signed");
    let up = updater(&srv, PlainHttpClient::default(), &dir, "1.0.0");
    let mut st = UpdateState::default();
    let mut last = (0, 0);
    let out = up.run_once(&mut st, &mut |d, t| last = (d, t)).unwrap();
    let Outcome::Ready(p) = out else {
        panic!("준비되어야 함: {out:?}")
    };
    assert_eq!(p.version, Version::new(1, 2, 3));
    assert_eq!(p.path, dir.join("Foldy-1.2.3-x64.msi"));
    assert_eq!(std::fs::read(&p.path).unwrap(), payload());
    assert!(!p.mandatory);
    assert_eq!(p.notes, "변경 내역");
    assert_eq!(last, (300_000, 300_000));
    assert_eq!(st.etag.as_deref(), Some("\"rel-1\""));
    assert!(st.last_check_unix.is_some());

    // 두 번째 주기: ETag가 같으면 304로 끝나고 아무것도 받지 않는다
    let before = srv.requests_to("/dl/Foldy-1.2.3-x64.msi").len();
    assert_eq!(
        up.run_once(&mut st, &mut |_, _| {}).unwrap(),
        Outcome::NotModified
    );
    let checks = srv.requests_to("/repos/chadolkr/Foldy/releases/latest");
    assert_eq!(
        checks.last().unwrap().header("if-none-match"),
        Some("\"rel-1\"")
    );
    assert_eq!(srv.requests_to("/dl/Foldy-1.2.3-x64.msi").len(), before);
    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn tampered_manifest_is_rejected_before_download() {
    let srv = server("update.tampered.json", true);
    let dir = temp_dir("tampered");
    let up = updater(&srv, PlainHttpClient::default(), &dir, "1.0.0");
    let mut st = UpdateState::default();
    let err = up.run_once(&mut st, &mut |_, _| {}).unwrap_err();
    assert!(matches!(err, UpdateError::BadSignature), "{err}");
    assert!(err.is_security());
    assert!(
        srv.requests_to("/dl/Foldy-1.2.3-x64.msi").is_empty(),
        "서명이 틀리면 설치 파일을 받지 않는다"
    );
    assert!(st.etag.is_none(), "실패한 주기는 ETag를 저장하지 않는다");
    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn unsigned_release_is_rejected() {
    let srv = server("update.json", false);
    let dir = temp_dir("unsigned");
    let up = updater(&srv, PlainHttpClient::default(), &dir, "1.0.0");
    let err = up
        .run_once(&mut UpdateState::default(), &mut |_, _| {})
        .unwrap_err();
    assert!(
        matches!(err, UpdateError::MissingAsset(ref n) if n == "update.json.sig"),
        "{err}"
    );
    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn wrong_key_is_rejected() {
    let other = SigningKey::from_bytes(&[9u8; 32]).verifying_key();
    let data = fixture("update.json");
    let sig = String::from_utf8(fixture("update.json.sig")).unwrap();
    sign::verify(&test_key(), &data, &sig).unwrap();
    assert!(matches!(
        sign::verify(&other, &data, &sig),
        Err(UpdateError::BadSignature)
    ));
    assert!(matches!(
        sign::verify(&test_key(), &fixture("update.tampered.json"), &sig),
        Err(UpdateError::BadSignature)
    ));
}

#[test]
fn hash_mismatch_deletes_file() {
    let srv = server("update.json", true);
    let mut bad = payload();
    bad[12345] ^= 0xff;
    srv.route(
        "/dl/Foldy-1.2.3-x64.msi",
        Route {
            body: bad,
            ..Route::default()
        },
    );
    let dir = temp_dir("hash");
    let up = updater(&srv, PlainHttpClient::default(), &dir, "1.0.0");
    let err = up
        .run_once(&mut UpdateState::default(), &mut |_, _| {})
        .unwrap_err();
    assert!(matches!(err, UpdateError::HashMismatch { .. }), "{err}");
    assert!(!dir.join("Foldy-1.2.3-x64.msi").exists());
    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn up_to_date_and_skipped() {
    let srv = server("update.json", true);
    let dir = temp_dir("skip");
    let up = updater(&srv, PlainHttpClient::default(), &dir, "1.2.3");
    assert_eq!(
        up.run_once(&mut UpdateState::default(), &mut |_, _| {})
            .unwrap(),
        Outcome::UpToDate
    );

    let up = updater(&srv, PlainHttpClient::default(), &dir, "1.1.0");
    let mut st = UpdateState::default();
    st.skip(&Version::new(1, 2, 3));
    assert_eq!(
        up.run_once(&mut st, &mut |_, _| {}).unwrap(),
        Outcome::Skipped(Version::new(1, 2, 3))
    );
    assert!(srv.requests_to("/dl/Foldy-1.2.3-x64.msi").is_empty());

    // 최소 지원 버전(1.0.0)보다 낮으면 건너뛴 버전이어도 필수로 설치
    let up = updater(&srv, PlainHttpClient::default(), &dir, "0.9.0");
    let mut st = UpdateState::default();
    st.skip(&Version::new(1, 2, 3));
    let out = up.run_once(&mut st, &mut |_, _| {}).unwrap();
    assert!(
        matches!(out, Outcome::Ready(ref p) if p.mandatory),
        "{out:?}"
    );
    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn resume_after_connection_drop_uses_range() {
    let srv = TestServer::start();
    srv.route(
        "/f",
        Route {
            body: payload(),
            cut_times: 1,
            cut_after: 100_000,
            ..Route::default()
        },
    );
    let dir = temp_dir("resume");
    let dest = dir.join("f.msi");
    let client = PlainHttpClient::default();

    // 첫 시도는 중간에 끊기고 .part가 남는다
    let err =
        download_resumable(&client, &srv.url("/f"), &dest, 300_000, &mut |_, _| {}).unwrap_err();
    assert!(err.is_transient(), "{err}");
    let have = std::fs::metadata(part_path(&dest)).unwrap().len();
    assert!(have > 0 && have < 300_000, "{have}");

    // 두 번째 시도는 Range로 이어받는다
    download_resumable(&client, &srv.url("/f"), &dest, 300_000, &mut |_, _| {}).unwrap();
    assert_eq!(std::fs::read(&dest).unwrap(), payload());
    assert!(!part_path(&dest).exists());
    let reqs = srv.requests_to("/f");
    assert_eq!(reqs.len(), 2);
    assert_eq!(reqs[0].header("range"), None);
    assert_eq!(
        reqs[1].header("range"),
        Some(format!("bytes={have}-").as_str())
    );
    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn retries_resume_automatically_and_handle_servers_ignoring_range() {
    let srv = TestServer::start();
    srv.route(
        "/r",
        Route {
            body: payload(),
            cut_times: 2,
            cut_after: 50_000,
            ..Route::default()
        },
    );
    srv.route(
        "/ignore",
        Route {
            body: payload(),
            ignore_range: true,
            ..Route::default()
        },
    );
    let dir = temp_dir("retry");
    let client = PlainHttpClient::default();
    download_with_retries(
        &client,
        &srv.url("/r"),
        &dir.join("r.msi"),
        300_000,
        5,
        &mut |_, _| {},
    )
    .unwrap();
    assert_eq!(std::fs::read(dir.join("r.msi")).unwrap(), payload());

    // .part가 있어도 서버가 200을 주면 처음부터 다시 쓴다
    let dest = dir.join("i.msi");
    std::fs::write(part_path(&dest), vec![0u8; 1234]).unwrap();
    download_resumable(&client, &srv.url("/ignore"), &dest, 300_000, &mut |_, _| {}).unwrap();
    assert_eq!(std::fs::read(&dest).unwrap(), payload());
    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn oversized_download_is_aborted() {
    let srv = TestServer::start();
    srv.route(
        "/big",
        Route {
            body: payload(),
            ..Route::default()
        },
    );
    let dir = temp_dir("big");
    let dest = dir.join("big.msi");
    let err = download_resumable(
        &PlainHttpClient::default(),
        &srv.url("/big"),
        &dest,
        1000,
        &mut |_, _| {},
    )
    .unwrap_err();
    assert!(matches!(err, UpdateError::SizeMismatch { .. }), "{err}");
    assert!(!part_path(&dest).exists());
    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn beta_channel_reads_release_list() {
    let srv = TestServer::start();
    let list = format!(
        "[{},{}]",
        release_json(&srv, "v1.2.3", false, true),
        release_json(&srv, "v1.3.0-beta.2", true, true)
    );
    srv.route(
        "/repos/chadolkr/Foldy/releases?per_page=30",
        Route {
            body: list.into_bytes(),
            etag: Some("W/\"l\"".into()),
            ..Route::default()
        },
    );
    let r = github::check(
        &PlainHttpClient::default(),
        &srv.base,
        "chadolkr/Foldy",
        Channel::Beta,
        None,
    )
    .unwrap();
    let CheckResult::Fetched {
        etag,
        release: Some(rel),
    } = r
    else {
        panic!("{r:?}")
    };
    assert_eq!(rel.tag, "v1.3.0-beta.2");
    assert_eq!(etag.as_deref(), Some("W/\"l\""));
    let again = github::check(
        &PlainHttpClient::default(),
        &srv.base,
        "chadolkr/Foldy",
        Channel::Beta,
        Some("W/\"l\""),
    )
    .unwrap();
    assert_eq!(again, CheckResult::NotModified);
}

#[test]
fn stable_without_releases_is_up_to_date() {
    let srv = TestServer::start(); // 경로 없음 → 404
    let r = github::check(
        &PlainHttpClient::default(),
        &srv.base,
        "chadolkr/Foldy",
        Channel::Stable,
        None,
    )
    .unwrap();
    assert_eq!(
        r,
        CheckResult::Fetched {
            etag: None,
            release: None
        }
    );
}

/// 제품용 WinHTTP 클라이언트도 같은 시험 서버로 확인한다 (루프백이라 프록시 없이).
#[cfg(windows)]
#[test]
fn winhttp_client_handles_etag_and_range() {
    use foldy_update::winhttp::WinHttpClient;
    let srv = server("update.json", true);
    let dir = temp_dir("winhttp");
    let client = WinHttpClient::direct("Foldy-Updater-test").unwrap();

    let up = updater(&srv, &client, &dir, "1.0.0");
    let mut st = UpdateState::default();
    assert!(matches!(
        up.run_once(&mut st, &mut |_, _| {}).unwrap(),
        Outcome::Ready(_)
    ));
    assert_eq!(
        up.run_once(&mut st, &mut |_, _| {}).unwrap(),
        Outcome::NotModified
    );
    let ua = srv.requests_to("/repos/chadolkr/Foldy/releases/latest")[0]
        .header("user-agent")
        .map(str::to_owned);
    assert_eq!(ua.as_deref(), Some("Foldy-Updater-test"));

    srv.route(
        "/cut",
        Route {
            body: payload(),
            cut_times: 1,
            cut_after: 70_000,
            ..Route::default()
        },
    );
    let dest = dir.join("cut.msi");
    download_with_retries(&client, &srv.url("/cut"), &dest, 300_000, 3, &mut |_, _| {}).unwrap();
    assert_eq!(std::fs::read(&dest).unwrap(), payload());
    let reqs = srv.requests_to("/cut");
    assert!(reqs.len() >= 2);
    assert!(
        reqs.last()
            .unwrap()
            .header("range")
            .is_some_and(|r| r.starts_with("bytes=") && r != "bytes=0-")
    );
    std::fs::remove_dir_all(&dir).ok();
}
