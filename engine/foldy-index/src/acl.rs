//! 파이프 보안 설명자 (설계서 5.6: 로그온 사용자(Interactive)만 연결).
//!
//! DACL: 네트워크 로그온 거부, SYSTEM·Administrators·Interactive 사용자 허용, 그리고 서비스를 띄운
//! 계정 자신(콘솔 모드에서 시험할 때 비대화형 세션이라도 연결할 수 있게). SYSTEM 서비스로 돌 때는
//! 마지막 항목이 SYSTEM과 같으므로 사실상 Interactive 사용자만 남는다.

use std::ffi::c_void;
use std::io;
use windows_sys::Win32::Foundation::{CloseHandle, HANDLE, HLOCAL, LocalFree};
use windows_sys::Win32::Security::Authorization::{
    ConvertSidToStringSidW, ConvertStringSecurityDescriptorToSecurityDescriptorW, SDDL_REVISION_1,
};
use windows_sys::Win32::Security::{GetTokenInformation, PSECURITY_DESCRIPTOR, SECURITY_ATTRIBUTES, TOKEN_QUERY, TOKEN_USER, TokenUser};
use windows_sys::Win32::System::Threading::{GetCurrentProcess, OpenProcessToken};

pub struct PipeSecurity {
    sa: SECURITY_ATTRIBUTES,
    sd: PSECURITY_DESCRIPTOR,
}

impl PipeSecurity {
    pub fn interactive_only() -> io::Result<Self> {
        let me = current_user_sid().map(|s| format!("(A;;GA;;;{s})")).unwrap_or_default();
        // FILE_GENERIC_READ | FILE_GENERIC_WRITE = 0x0012019F (FILE_CREATE_PIPE_INSTANCE 제외)
        let sddl = format!("D:P(D;;GA;;;NU)(A;;GA;;;SY)(A;;GA;;;BA)(A;;0x0012019f;;;IU){me}");
        let wide: Vec<u16> = sddl.encode_utf16().chain([0]).collect();
        let mut sd: PSECURITY_DESCRIPTOR = std::ptr::null_mut();
        // SAFETY: wide는 NUL로 끝나는 UTF-16 문자열, sd는 출력 포인터. 성공하면 sd는 LocalAlloc 메모리이고
        // Drop에서 LocalFree로 해제한다.
        let ok = unsafe { ConvertStringSecurityDescriptorToSecurityDescriptorW(wide.as_ptr(), SDDL_REVISION_1, &mut sd, std::ptr::null_mut()) };
        if ok == 0 {
            return Err(io::Error::last_os_error());
        }
        let sa = SECURITY_ATTRIBUTES { nLength: size_of::<SECURITY_ATTRIBUTES>() as u32, lpSecurityDescriptor: sd, bInheritHandle: 0 };
        Ok(Self { sa, sd })
    }

    /// CreateNamedPipeW에 넘길 SECURITY_ATTRIBUTES 포인터. self가 살아 있는 동안 유효하다.
    pub fn as_ptr(&mut self) -> *mut c_void {
        (&raw mut self.sa).cast()
    }
}

impl Drop for PipeSecurity {
    fn drop(&mut self) {
        // SAFETY: sd는 ConvertStringSecurityDescriptorToSecurityDescriptorW가 할당한 메모리이고 한 번만 해제한다.
        unsafe {
            LocalFree(self.sd as HLOCAL);
        }
    }
}

/// 현재 프로세스 토큰의 사용자 SID 문자열 (S-1-5-...).
fn current_user_sid() -> Option<String> {
    let mut token: HANDLE = std::ptr::null_mut();
    // SAFETY: 현재 프로세스 의사 핸들로 토큰을 연다. 성공하면 아래에서 CloseHandle.
    if unsafe { OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &mut token) } == 0 {
        return None;
    }
    let mut buf = vec![0u64; 64];
    let mut len = 0u32;
    // SAFETY: buf는 8바이트 정렬된 512바이트 버퍼이고 길이를 정확히 넘긴다.
    let ok = unsafe { GetTokenInformation(token, TokenUser, buf.as_mut_ptr().cast(), (buf.len() * 8) as u32, &mut len) };
    // SAFETY: 위에서 연 토큰 핸들을 한 번 닫는다.
    unsafe { CloseHandle(token) };
    if ok == 0 {
        return None;
    }
    // SAFETY: 성공한 TokenUser 조회는 버퍼 앞에 TOKEN_USER를 쓴다. buf는 TOKEN_USER 정렬을 만족한다.
    let sid = unsafe { (*(buf.as_ptr() as *const TOKEN_USER)).User.Sid };
    let mut s: *mut u16 = std::ptr::null_mut();
    // SAFETY: sid는 buf 안의 유효한 SID. 출력 문자열은 LocalFree로 해제한다.
    if unsafe { ConvertSidToStringSidW(sid, &mut s) } == 0 || s.is_null() {
        return None;
    }
    // SAFETY: s는 NUL로 끝나는 UTF-16 문자열이다.
    let out = unsafe {
        let mut n = 0;
        while *s.add(n) != 0 {
            n += 1;
        }
        String::from_utf16_lossy(std::slice::from_raw_parts(s, n))
    };
    // SAFETY: ConvertSidToStringSidW가 할당한 메모리를 한 번 해제한다.
    unsafe { LocalFree(s as HLOCAL) };
    Some(out)
}
