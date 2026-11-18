//! 설치 단계 (설계서 10.5 설치 시점과 방법).
//!
//! 서비스는 **자기 업데이트 폴더 안의 검증된 MSI만** `msiexec /i <파일> /qn /norestart`로 분리 실행한다.
//! UI가 임의 경로를 설치하라고 요청하는 길은 만들지 않는다 (권한 상승 방지) → [`install_command`]가 경로를 검사.
//!
//! Authenticode(WinVerifyTrust) 확인은 코드 서명 인증서(10.6장 SignPath Foundation)를 받기 전까지 꺼 둔다.
//! 그동안은 Ed25519 서명 + SHA-256만으로 검증한다.

use std::ffi::OsString;
use std::path::{Path, PathBuf};
use std::process::Command;

use crate::error::{Result, UpdateError};

/// Authenticode 확인 정책.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum AuthenticodePolicy {
    /// 인증서를 받기 전: 건너뛴다.
    Disabled,
    /// 서명이 유효하고 서명자가 이 게시자여야 한다 (예: "SignPath Foundation").
    RequirePublisher(String),
}

/// 현재 정책. 인증서를 받으면 `RequirePublisher("SignPath Foundation")`으로 바꾸고 WinVerifyTrust를 구현한다.
pub const AUTHENTICODE_POLICY: AuthenticodePolicy = AuthenticodePolicy::Disabled;

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum AuthenticodeOutcome {
    Skipped,
    Verified,
}

/// Authenticode 확인 (스텁). 정책이 켜져 있으면 아직 구현이 없으므로 **실패**로 닫는다 (fail closed).
pub fn verify_authenticode(
    path: &Path,
    policy: &AuthenticodePolicy,
) -> Result<AuthenticodeOutcome> {
    match policy {
        AuthenticodePolicy::Disabled => Ok(AuthenticodeOutcome::Skipped),
        AuthenticodePolicy::RequirePublisher(publisher) => Err(UpdateError::Authenticode(format!(
            "{}: WinVerifyTrust 확인이 아직 구현되지 않음 (기대 게시자 {publisher})",
            path.display()
        ))),
    }
}

/// `msiexec` 인자: `/i <파일> /qn /norestart`.
pub fn msiexec_args(msi: &Path) -> Vec<OsString> {
    vec![
        "/i".into(),
        msi.as_os_str().to_owned(),
        "/qn".into(),
        "/norestart".into(),
    ]
}

/// `%SystemRoot%\System32\msiexec.exe` (PATH를 믿지 않는다).
pub fn msiexec_path() -> PathBuf {
    let root = std::env::var_os("SystemRoot").unwrap_or_else(|| OsString::from(r"C:\Windows"));
    PathBuf::from(root).join("System32").join("msiexec.exe")
}

/// `msi`가 `updates_dir` 바로 안의 `.msi` 파일인지 확인한다. 정규화한 경로를 돌려준다.
pub fn validate_msi_path(updates_dir: &Path, msi: &Path) -> Result<PathBuf> {
    let deny = || UpdateError::PathNotAllowed(msi.display().to_string());
    let dir = updates_dir.canonicalize().map_err(|_| deny())?;
    let file = msi.canonicalize().map_err(|_| deny())?;
    let is_msi = file
        .extension()
        .and_then(|e| e.to_str())
        .is_some_and(|e| e.eq_ignore_ascii_case("msi"));
    if !is_msi || file.parent() != Some(dir.as_path()) || !file.is_file() {
        return Err(deny());
    }
    Ok(strip_verbatim(&file))
}

/// `canonicalize`가 붙이는 `\\?\` 접두어를 뗀다 (msiexec는 확장 경로를 잘 받지 못한다).
/// `\\?\C:\…` → `C:\…`, `\\?\UNC\서버\…` → `\\서버\…`. 그 밖의 형식은 그대로 둔다.
pub fn strip_verbatim(p: &Path) -> PathBuf {
    let s = p.as_os_str().to_string_lossy();
    if let Some(rest) = s.strip_prefix(r"\\?\UNC\") {
        return PathBuf::from(format!(r"\\{rest}"));
    }
    if let Some(rest) = s.strip_prefix(r"\\?\") {
        let b = rest.as_bytes();
        if b.len() >= 3 && b[0].is_ascii_alphabetic() && b[1] == b':' && b[2] == b'\\' {
            return PathBuf::from(rest);
        }
    }
    p.to_path_buf()
}

/// 되돌리기 설치에 붙이는 MSI 속성. Foldy.wxs는 이 속성이 있을 때만 낮은 버전 설치(다운그레이드)를 허용한다.
pub const ROLLBACK_PROPERTY: &str = "FOLDY_ROLLBACK=1";

/// 설치 옵션.
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct InstallOptions {
    /// 되돌리기 설치 ([`ROLLBACK_PROPERTY`]를 붙인다).
    pub rollback: bool,
    /// 자세한 설치 기록 파일 (`/l*v`). 업데이트 폴더 안이어야 한다.
    pub log: Option<PathBuf>,
}

/// `msiexec` 인자: `/i <파일> /qn /norestart [/l*v <기록>] [FOLDY_ROLLBACK=1]`.
pub fn msiexec_args_with(msi: &Path, opts: &InstallOptions) -> Vec<OsString> {
    let mut args = msiexec_args(msi);
    if let Some(log) = &opts.log {
        args.push("/l*v".into());
        args.push(log.as_os_str().to_owned());
    }
    if opts.rollback {
        args.push(ROLLBACK_PROPERTY.into());
    }
    args
}

/// 실행할 명령을 만든다 (실행은 호출자). Windows에서는 서비스와 분리된 프로세스로 띄운다.
pub fn install_command(updates_dir: &Path, msi: &Path) -> Result<Command> {
    install_command_with(updates_dir, msi, &InstallOptions::default())
}

/// [`install_command`]에 옵션을 더한 것. 기록 파일도 업데이트 폴더 바로 안으로 제한한다.
pub fn install_command_with(updates_dir: &Path, msi: &Path, opts: &InstallOptions) -> Result<Command> {
    let file = validate_msi_path(updates_dir, msi)?;
    if let Some(log) = &opts.log {
        let dir = updates_dir
            .canonicalize()
            .map_err(|_| UpdateError::PathNotAllowed(log.display().to_string()))?;
        let parent_ok = log
            .parent()
            .and_then(|p| p.canonicalize().ok())
            .is_some_and(|p| p == dir);
        if !parent_ok || log.file_name().is_none() {
            return Err(UpdateError::PathNotAllowed(log.display().to_string()));
        }
    }
    let mut cmd = Command::new(msiexec_path());
    cmd.args(msiexec_args_with(&file, opts));
    #[cfg(windows)]
    {
        use std::os::windows::process::CommandExt;
        const DETACHED_PROCESS: u32 = 0x0000_0008;
        const CREATE_NEW_PROCESS_GROUP: u32 = 0x0000_0200;
        // MSI가 서비스를 멈춰도 msiexec는 계속 돈다
        cmd.creation_flags(DETACHED_PROCESS | CREATE_NEW_PROCESS_GROUP);
    }
    Ok(cmd)
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::fs;

    fn tmp(name: &str) -> PathBuf {
        let d = std::env::temp_dir().join(format!(
            "foldy-update-install-{name}-{}",
            std::process::id()
        ));
        fs::create_dir_all(&d).unwrap();
        d
    }

    #[test]
    fn builds_quiet_msiexec_command() {
        let dir = tmp("ok");
        let msi = dir.join("Foldy-1.2.3-x64.msi");
        fs::write(&msi, b"msi").unwrap();
        let cmd = install_command(&dir, &msi).unwrap();
        assert!(
            cmd.get_program()
                .to_string_lossy()
                .to_ascii_lowercase()
                .ends_with(r"system32\msiexec.exe")
        );
        let args: Vec<_> = cmd
            .get_args()
            .map(|a| a.to_string_lossy().into_owned())
            .collect();
        assert_eq!(args[0], "/i");
        assert!(args[1].ends_with("Foldy-1.2.3-x64.msi"));
        assert_eq!(&args[2..], ["/qn", "/norestart"]);
        fs::remove_dir_all(&dir).ok();
    }

    #[test]
    fn refuses_paths_outside_updates_dir() {
        let dir = tmp("deny");
        let sub = dir.join("sub");
        fs::create_dir_all(&sub).unwrap();
        let outside = sub.join("x.msi");
        fs::write(&outside, b"msi").unwrap();
        let exe = dir.join("x.exe");
        fs::write(&exe, b"exe").unwrap();
        let escaped = dir.join("sub").join("..").join("..").join("x.msi");
        for p in [&outside, &exe, &escaped, &dir.join("missing.msi")] {
            assert!(
                matches!(
                    install_command(&dir, p),
                    Err(UpdateError::PathNotAllowed(_))
                ),
                "{}",
                p.display()
            );
        }
        // sub 안의 MSI는 sub 기준으로는 허용, dir 기준으로는 거부
        assert!(install_command(&sub, &outside).is_ok());
        assert!(matches!(
            install_command(&dir, &outside),
            Err(UpdateError::PathNotAllowed(_))
        ));
        fs::remove_dir_all(&dir).ok();
    }

    #[test]
    fn verbatim_prefix_is_removed_for_msiexec() {
        assert_eq!(strip_verbatim(Path::new(r"\\?\C:\ProgramData\Foldy\Updates\a.msi")), PathBuf::from(r"C:\ProgramData\Foldy\Updates\a.msi"));
        assert_eq!(strip_verbatim(Path::new(r"\\?\UNC\srv\share\a.msi")), PathBuf::from(r"\\srv\share\a.msi"));
        assert_eq!(strip_verbatim(Path::new(r"C:\a.msi")), PathBuf::from(r"C:\a.msi"));
        assert_eq!(strip_verbatim(Path::new(r"\\?\Volume{x}\a.msi")), PathBuf::from(r"\\?\Volume{x}\a.msi"));
        let dir = tmp("plain");
        let msi = dir.join("Foldy-1.0.0-x64.msi");
        fs::write(&msi, b"msi").unwrap();
        let p = validate_msi_path(&dir, &msi).unwrap();
        assert!(!p.to_string_lossy().starts_with(r"\\?\"), "{}", p.display());
        fs::remove_dir_all(&dir).ok();
    }

    #[test]
    fn rollback_and_log_options() {
        let dir = tmp("opts");
        let msi = dir.join("Foldy-1.2.2-x64.msi");
        fs::write(&msi, b"msi").unwrap();
        let opts = InstallOptions {
            rollback: true,
            log: Some(dir.join("install.log")),
        };
        let cmd = install_command_with(&dir, &msi, &opts).unwrap();
        let args: Vec<_> = cmd
            .get_args()
            .map(|a| a.to_string_lossy().into_owned())
            .collect();
        assert_eq!(&args[2..4], ["/qn", "/norestart"]);
        assert_eq!(args[4], "/l*v");
        assert!(args[5].ends_with("install.log"));
        assert_eq!(args[6], ROLLBACK_PROPERTY);
        // 기록 파일도 업데이트 폴더 밖은 거부
        let bad = InstallOptions {
            rollback: false,
            log: Some(std::env::temp_dir().join("evil.log")),
        };
        assert!(matches!(
            install_command_with(&dir, &msi, &bad),
            Err(UpdateError::PathNotAllowed(_))
        ));
        fs::remove_dir_all(&dir).ok();
    }

    #[test]
    fn authenticode_stub() {
        let p = Path::new("x.msi");
        assert_eq!(
            verify_authenticode(p, &AUTHENTICODE_POLICY).unwrap(),
            AuthenticodeOutcome::Skipped
        );
        assert!(
            verify_authenticode(
                p,
                &AuthenticodePolicy::RequirePublisher("SignPath Foundation".into())
            )
            .unwrap_err()
            .is_security()
        );
    }
}
