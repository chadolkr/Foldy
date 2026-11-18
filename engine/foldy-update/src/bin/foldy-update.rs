//! `foldy-update` — 릴리스 서명 도구 겸 확인용 CLI (docs/release-signing.md).
//!
//! ```text
//! foldy-update keygen --out-dir <폴더>
//! foldy-update manifest --version 1.2.3 [--channel stable|beta] [--min-supported 1.0.0] [--mandatory] --out update.json <파일...>
//! foldy-update sign <update.json> [--key <비밀키 파일>]      (없으면 환경 변수 FOLDY_UPDATE_SIGNING_KEY)
//! foldy-update verify <update.json> [--sig <파일>] [--pubkey <공개키 파일>]   (없으면 앱 내장 공개키)
//! foldy-update check [--repo owner/name] [--beta] [--current 0.0.1] [--api-base URL]
//! ```

use std::path::{Path, PathBuf};
use std::process::ExitCode;

use foldy_update::github::{self, CheckResult};
use foldy_update::manifest::{Channel, UpdateManifest};
use foldy_update::sign;
use foldy_update::version::Version;
use foldy_update::{DEFAULT_API_BASE, DEFAULT_REPO};

const KEY_ENV: &str = "FOLDY_UPDATE_SIGNING_KEY";

type CliResult = Result<(), String>;

fn main() -> ExitCode {
    let args: Vec<String> = std::env::args().skip(1).collect();
    let result = match args.first().map(String::as_str) {
        Some("keygen") => keygen(&args[1..]),
        Some("manifest") => manifest(&args[1..]),
        Some("sign") => sign_cmd(&args[1..]),
        Some("verify") => verify_cmd(&args[1..]),
        Some("check") => check(&args[1..]),
        _ => Err(usage()),
    };
    match result {
        Ok(()) => ExitCode::SUCCESS,
        Err(e) => {
            eprintln!("오류: {e}");
            ExitCode::FAILURE
        }
    }
}

fn usage() -> String {
    "사용법:\n  foldy-update keygen --out-dir <폴더>\n  foldy-update manifest --version <v> [--channel stable|beta] [--min-supported <v>] [--mandatory] --out <update.json> <파일...>\n  foldy-update sign <update.json> [--key <파일>]   (기본: 환경 변수 FOLDY_UPDATE_SIGNING_KEY)\n  foldy-update verify <update.json> [--sig <파일>] [--pubkey <파일>]\n  foldy-update check [--repo owner/name] [--beta] [--current <v>] [--api-base <URL>]".into()
}

/// 간단한 인자 분해: `--이름 값`, `--플래그`, 나머지는 위치 인자.
struct Args {
    flags: Vec<(String, Option<String>)>,
    positional: Vec<String>,
}

impl Args {
    fn parse(raw: &[String], switches: &[&str]) -> Args {
        let mut flags = Vec::new();
        let mut positional = Vec::new();
        let mut it = raw.iter();
        while let Some(a) = it.next() {
            if let Some(name) = a.strip_prefix("--") {
                if switches.contains(&name) {
                    flags.push((name.to_owned(), None));
                } else {
                    flags.push((name.to_owned(), it.next().cloned()));
                }
            } else {
                positional.push(a.clone());
            }
        }
        Args { flags, positional }
    }

    fn value(&self, name: &str) -> Option<&str> {
        self.flags
            .iter()
            .find(|(n, _)| n == name)
            .and_then(|(_, v)| v.as_deref())
    }

    fn has(&self, name: &str) -> bool {
        self.flags.iter().any(|(n, _)| n == name)
    }
}

fn keygen(raw: &[String]) -> CliResult {
    let a = Args::parse(raw, &[]);
    let dir = PathBuf::from(
        a.value("out-dir")
            .ok_or("--out-dir가 필요합니다 (저장소 밖의 폴더를 쓰세요)")?,
    );
    std::fs::create_dir_all(&dir).map_err(|e| e.to_string())?;
    let secret = dir.join("foldy-update.signing.key");
    let public = dir.join("foldy-update.signing.pub");
    if secret.exists() || public.exists() {
        return Err(format!(
            "{} 또는 .pub이 이미 있습니다. 덮어쓰지 않습니다.",
            secret.display()
        ));
    }
    if inside_git_repo(&dir) {
        eprintln!(
            "경고: {}는 git 저장소 안입니다. 비밀키를 절대 커밋하지 마세요 (*.signing.key는 .gitignore에 있음).",
            dir.display()
        );
    }
    let sk = sign::generate_signing_key().map_err(|e| e.to_string())?;
    std::fs::write(&secret, sign::signing_key_to_text(&sk)).map_err(|e| e.to_string())?;
    std::fs::write(&public, sign::verifying_key_to_text(&sk.verifying_key()))
        .map_err(|e| e.to_string())?;
    println!(
        "비밀키: {}  → GitHub Actions 시크릿 {KEY_ENV}에 내용을 넣은 뒤 안전한 오프라인 보관소로 옮기세요",
        secret.display()
    );
    println!(
        "공개키: {}  → 아래 줄을 engine/foldy-update/keys/update-signing.pub에 붙여 넣어 커밋",
        public.display()
    );
    println!(
        "{}",
        foldy_update::hex::encode(sk.verifying_key().as_bytes())
    );
    Ok(())
}

fn inside_git_repo(dir: &Path) -> bool {
    let Ok(abs) = dir.canonicalize() else {
        return false;
    };
    abs.ancestors().any(|p| p.join(".git").exists())
}

fn manifest(raw: &[String]) -> CliResult {
    let a = Args::parse(raw, &["mandatory"]);
    let version = a.value("version").ok_or("--version이 필요합니다")?;
    let channel = match a.value("channel") {
        None => {
            if Version::parse(version)
                .map_err(|e| e.to_string())?
                .is_prerelease()
            {
                Channel::Beta
            } else {
                Channel::Stable
            }
        }
        Some(c) => Channel::parse(c).ok_or("--channel은 stable 또는 beta")?,
    };
    let out = PathBuf::from(a.value("out").ok_or("--out이 필요합니다")?);
    if a.positional.is_empty() {
        return Err("자산 파일을 하나 이상 주세요".into());
    }
    let files: Vec<PathBuf> = a.positional.iter().map(PathBuf::from).collect();
    let refs: Vec<&Path> = files.iter().map(PathBuf::as_path).collect();
    let m = UpdateManifest::build(
        version,
        channel,
        a.value("min-supported"),
        a.has("mandatory"),
        &refs,
    )
    .map_err(|e| e.to_string())?;
    std::fs::write(&out, m.to_json_pretty()).map_err(|e| e.to_string())?;
    println!(
        "{} 작성: {} {:?}, 자산 {}개",
        out.display(),
        m.version,
        m.channel,
        m.assets.len()
    );
    Ok(())
}

fn sign_cmd(raw: &[String]) -> CliResult {
    let a = Args::parse(raw, &[]);
    let file = PathBuf::from(
        a.positional
            .first()
            .ok_or("서명할 update.json 경로가 필요합니다")?,
    );
    let key_text = match a.value("key") {
        Some(k) => std::fs::read_to_string(k).map_err(|e| format!("{k}: {e}"))?,
        None => std::env::var(KEY_ENV)
            .map_err(|_| format!("--key 또는 환경 변수 {KEY_ENV}가 필요합니다"))?,
    };
    let sk = sign::parse_signing_key(&key_text).map_err(|e| e.to_string())?;
    let data = std::fs::read(&file).map_err(|e| e.to_string())?;
    UpdateManifest::parse(&data).map_err(|e| format!("서명 전 검사 실패: {e}"))?;
    let sig = sign::sign(&sk, &data);
    sign::verify(&sk.verifying_key(), &data, &sig).map_err(|e| e.to_string())?;
    let out = sig_path(&file);
    std::fs::write(&out, sig).map_err(|e| e.to_string())?;
    println!(
        "{} 작성 (공개키 {})",
        out.display(),
        foldy_update::hex::encode(sk.verifying_key().as_bytes())
    );
    Ok(())
}

fn sig_path(file: &Path) -> PathBuf {
    let mut s = file.as_os_str().to_owned();
    s.push(".sig");
    PathBuf::from(s)
}

fn verify_cmd(raw: &[String]) -> CliResult {
    let a = Args::parse(raw, &[]);
    let file = PathBuf::from(
        a.positional
            .first()
            .ok_or("검사할 update.json 경로가 필요합니다")?,
    );
    let sig_file = a
        .value("sig")
        .map(PathBuf::from)
        .unwrap_or_else(|| sig_path(&file));
    let vk = match a.value("pubkey") {
        Some(p) => {
            sign::parse_verifying_key(&std::fs::read_to_string(p).map_err(|e| format!("{p}: {e}"))?)
        }
        None => sign::embedded_verifying_key(),
    }
    .map_err(|e| e.to_string())?;
    let data = std::fs::read(&file).map_err(|e| e.to_string())?;
    let sig =
        std::fs::read_to_string(&sig_file).map_err(|e| format!("{}: {e}", sig_file.display()))?;
    sign::verify(&vk, &data, &sig).map_err(|e| e.to_string())?;
    let m = UpdateManifest::parse(&data).map_err(|e| e.to_string())?;
    println!(
        "서명 OK: {} {:?}, 자산 {}개",
        m.version,
        m.channel,
        m.assets.len()
    );
    Ok(())
}

fn check(raw: &[String]) -> CliResult {
    let a = Args::parse(raw, &["beta"]);
    let repo = a.value("repo").unwrap_or(DEFAULT_REPO);
    let api = a.value("api-base").unwrap_or(DEFAULT_API_BASE);
    let channel = if a.has("beta") {
        Channel::Beta
    } else {
        Channel::Stable
    };
    let current = Version::parse(a.value("current").unwrap_or(env!("CARGO_PKG_VERSION")))
        .map_err(|e| e.to_string())?;
    let result = run_check(api, repo, channel).map_err(|e| e.to_string())?;
    match result {
        CheckResult::NotModified => println!("바뀐 것 없음 (304)"),
        CheckResult::Fetched { release: None, .. } => {
            println!("{repo}: {channel:?} 채널 릴리스 없음")
        }
        CheckResult::Fetched {
            release: Some(r),
            etag,
        } => {
            let newer = r.version > current;
            println!(
                "최신: {} (pre-release: {}), 현재: {current} → {}",
                r.tag,
                r.prerelease,
                if newer {
                    "업데이트 있음"
                } else {
                    "최신 상태"
                }
            );
            println!(
                "자산: {}",
                r.assets
                    .iter()
                    .map(|x| x.name.as_str())
                    .collect::<Vec<_>>()
                    .join(", ")
            );
            if let Some(e) = etag {
                println!("ETag: {e}");
            }
        }
    }
    Ok(())
}

#[cfg(windows)]
fn run_check(api: &str, repo: &str, channel: Channel) -> foldy_update::Result<CheckResult> {
    // http://(로컬 시험 서버)는 프록시 없이, 그 밖에는 시스템 프록시를 쓰는 WinHTTP
    if api.starts_with("http://") {
        github::check(
            &foldy_update::http::PlainHttpClient::default(),
            api,
            repo,
            channel,
            None,
        )
    } else {
        github::check(
            &foldy_update::system_http_client()?,
            api,
            repo,
            channel,
            None,
        )
    }
}

#[cfg(not(windows))]
fn run_check(api: &str, repo: &str, channel: Channel) -> foldy_update::Result<CheckResult> {
    github::check(
        &foldy_update::http::PlainHttpClient::default(),
        api,
        repo,
        channel,
        None,
    )
}
