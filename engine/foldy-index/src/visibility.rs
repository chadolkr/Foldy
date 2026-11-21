//! 검색 결과를 요청한 사용자 기준으로 거른다 (보안 리뷰 M2).
//!
//! 서비스는 SYSTEM으로 전체 드라이브를 색인하므로, 그대로 두면 로그온한 누구나 다른 사용자 프로필
//! (`C:\Users\다른사람\…`)의 파일 이름을 검색할 수 있다. 연결마다 파이프 상대 프로세스의 사용자를 확인하고,
//! 그 사용자의 프로필이 아닌 다른 프로필·다른 사용자의 휴지통·`System Volume Information`은 결과에서 뺀다.
//! 사용자를 확인하지 못하면 모든 프로필을 숨긴다 (닫힌 쪽으로 실패).

use foldy_ntfs::fold_name;

pub struct Visibility {
    /// 숨길 폴더들 (fold_name, 끝 `\` 없음).
    hidden: Vec<String>,
    /// 요청한 사용자 SID (대문자). 다른 사용자의 휴지통 폴더를 가리는 데 쓴다.
    own_sid: Option<String>,
}

impl Visibility {
    /// `profiles`: 이 PC의 (SID, 프로필 경로) 목록. `own_sid`: 요청한 사용자 (모르면 None → 모든 프로필 숨김).
    /// 요청한 사용자의 SID (대문자). 모르면 None.
    pub fn user_sid(&self) -> Option<&str> {
        self.own_sid.as_deref()
    }

    pub fn new(own_sid: Option<&str>, profiles: &[(String, String)]) -> Self {
        let own = own_sid.map(|s| s.to_ascii_uppercase());
        let hidden = profiles
            .iter()
            .filter(|(sid, _)| own.as_deref() != Some(sid.to_ascii_uppercase().as_str()))
            .map(|(_, path)| fold_name(path.replace('/', "\\").trim_end_matches('\\')))
            .filter(|p| p.len() > 3) // 드라이브 루트 자체를 프로필로 적은 이상한 항목은 무시
            .collect();
        Visibility { hidden, own_sid: own }
    }

    pub fn allows(&self, path: &str) -> bool {
        let p = fold_name(&path.replace('/', "\\"));
        if self.hidden.iter().any(|h| p.starts_with(h.as_str()) && (p.len() == h.len() || p.as_bytes()[h.len()] == b'\\')) {
            return false;
        }
        // X:\System Volume Information, X:\$Recycle.Bin\<다른 SID>
        let mut parts = p.split('\\').skip(1);
        match parts.next() {
            Some(top) if top == fold_name("System Volume Information") => false,
            Some(top) if top == fold_name("$Recycle.Bin") => match parts.next() {
                Some(sid) if sid.starts_with(&fold_name("S-")) => {
                    self.own_sid.as_deref().is_some_and(|own| fold_name(own) == sid)
                }
                _ => true,
            },
            _ => true,
        }
    }
}

/// 파이프 상대 프로세스의 사용자 기준 [`Visibility`].
pub fn for_pipe_client(pipe: windows_sys::Win32::Foundation::HANDLE) -> Visibility {
    Visibility::new(win::pipe_client_sid(pipe).as_deref(), &win::profiles())
}

mod win {
    use windows_sys::Win32::Foundation::{CloseHandle, ERROR_SUCCESS, HANDLE};
    use windows_sys::Win32::System::Pipes::GetNamedPipeClientProcessId;
    use windows_sys::Win32::System::Registry::{
        HKEY, HKEY_LOCAL_MACHINE, KEY_READ, RRF_RT_REG_SZ, RegCloseKey, RegEnumKeyExW, RegGetValueW, RegOpenKeyExW,
    };
    use windows_sys::Win32::System::Threading::{OpenProcess, PROCESS_QUERY_LIMITED_INFORMATION};

    fn wide(s: &str) -> Vec<u16> {
        s.encode_utf16().chain([0]).collect()
    }

    pub fn pipe_client_sid(pipe: HANDLE) -> Option<String> {
        let mut pid = 0u32;
        // SAFETY: pipe는 연결된 서버 쪽 파이프 핸들이다.
        if unsafe { GetNamedPipeClientProcessId(pipe, &mut pid) } == 0 || pid == 0 {
            return None;
        }
        // SAFETY: 조회 전용 권한으로 연다. 성공하면 아래에서 CloseHandle.
        let process = unsafe { OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, 0, pid) };
        if process.is_null() {
            return None;
        }
        let sid = crate::acl::process_user_sid(process);
        // SAFETY: 위에서 연 프로세스 핸들을 한 번 닫는다.
        unsafe { CloseHandle(process) };
        sid
    }

    /// HKLM\…\ProfileList의 (SID, ProfileImagePath) 목록. 환경 변수는 펼쳐서 돌려준다.
    pub fn profiles() -> Vec<(String, String)> {
        const LIST: &str = r"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList";
        let mut key: HKEY = std::ptr::null_mut();
        // SAFETY: NUL로 끝나는 키 이름, 출력 핸들. 성공하면 아래에서 RegCloseKey.
        if unsafe { RegOpenKeyExW(HKEY_LOCAL_MACHINE, wide(LIST).as_ptr(), 0, KEY_READ, &mut key) } != ERROR_SUCCESS {
            return Vec::new();
        }
        let mut out = Vec::new();
        for i in 0.. {
            let mut name = [0u16; 256];
            let mut len = name.len() as u32;
            // SAFETY: name 버퍼와 길이(문자 수)를 정확히 넘긴다. 나머지 출력은 받지 않는다.
            let r = unsafe {
                RegEnumKeyExW(key, i, name.as_mut_ptr(), &mut len, std::ptr::null(), std::ptr::null_mut(), std::ptr::null_mut(), std::ptr::null_mut())
            };
            if r != ERROR_SUCCESS {
                break;
            }
            let sid = String::from_utf16_lossy(&name[..len as usize]);
            let mut buf = vec![0u16; 1024];
            let mut bytes = (buf.len() * 2) as u32;
            // SAFETY: 하위 키·값 이름은 NUL로 끝나고, buf 크기를 바이트로 넘긴다. REG_EXPAND_SZ는 펼쳐서 받는다.
            let r = unsafe {
                RegGetValueW(
                    key,
                    wide(&sid).as_ptr(),
                    wide("ProfileImagePath").as_ptr(),
                    RRF_RT_REG_SZ,
                    std::ptr::null_mut(),
                    buf.as_mut_ptr().cast(),
                    &mut bytes,
                )
            };
            if r == ERROR_SUCCESS {
                let n = (bytes as usize / 2).saturating_sub(1);
                let path = String::from_utf16_lossy(&buf[..n]).trim_end_matches('\0').to_string();
                if !path.is_empty() {
                    out.push((sid, path));
                }
            }
        }
        // SAFETY: 위에서 연 키를 한 번 닫는다.
        unsafe { RegCloseKey(key) };
        out
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn profiles() -> Vec<(String, String)> {
        vec![
            ("S-1-5-21-1-1001".into(), r"C:\Users\Dana".into()),
            ("S-1-5-21-1-1002".into(), r"C:\Users\Lee".into()),
            ("S-1-5-18".into(), r"%systemroot%\system32\config\systemprofile".into()),
        ]
    }

    #[test]
    fn own_profile_is_visible_and_others_are_not() {
        let v = Visibility::new(Some("s-1-5-21-1-1001"), &profiles());
        assert!(v.allows(r"C:\Users\Dana\Documents\a.txt"));
        assert!(v.allows(r"C:\Users\Public\b.txt"));
        assert!(!v.allows(r"C:\Users\Lee"));
        assert!(!v.allows(r"c:\users\LEE\Desktop\secret.docx"));
        assert!(v.allows(r"C:\Users\Leeway\x.txt"), "이름이 겹친다고 숨기지 않는다");
        assert!(v.allows(r"D:\Work\plan.md"));
    }

    #[test]
    fn unknown_user_sees_no_profiles() {
        let v = Visibility::new(None, &profiles());
        assert!(!v.allows(r"C:\Users\Dana\a.txt"));
        assert!(!v.allows(r"C:\Users\Lee\a.txt"));
        assert!(v.allows(r"C:\Program Files\Foldy\Foldy.exe"));
    }

    #[test]
    fn other_users_recycle_bins_and_system_volume_information_are_hidden() {
        let v = Visibility::new(Some("S-1-5-21-1-1001"), &profiles());
        assert!(v.allows(r"D:\$Recycle.Bin\S-1-5-21-1-1001\$RABC.txt"));
        assert!(!v.allows(r"D:\$Recycle.Bin\S-1-5-21-1-1002\$RABC.txt"));
        assert!(!v.allows(r"C:\System Volume Information\tracking.log"));
        assert!(Visibility::new(None, &[]).allows(r"D:\$Recycle.Bin"));
    }
}
