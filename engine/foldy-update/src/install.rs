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
    Ok(file)
}

/// 실행할 명령을 만든다 (실행은 호출자). Windows에서는 서비스와 분리된 프로세스로 띄운다.
pub fn install_command(updates_dir: &Path, msi: &Path) -> Result<Command> {
    let file = validate_msi_path(updates_dir, msi)?;
    let mut cmd = Command::new(msiexec_path());
    cmd.args(msiexec_args(&file));
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
