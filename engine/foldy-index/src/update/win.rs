//! 실제 Windows 호스트 ([`super::host::Host`] 구현).
//!
//! - msiexec는 `DETACHED_PROCESS | CREATE_NEW_PROCESS_GROUP | CREATE_BREAKAWAY_FROM_JOB`으로 띄운다.
//!   MSI가 이 서비스를 멈추고 파일을 바꿔도 msiexec는 계속 돈다 (서비스의 작업 개체에 묶이지 않음).
//! - Foldy 다시 띄우기: 그 세션의 explorer.exe 토큰을 복제해(없으면 `WTSQueryUserToken`) →
//!   `CreateEnvironmentBlock` → `CreateProcessAsUserW`(winsta0\default).
//!   탐색기와 같은 로그온 세션으로 떠야 사용자의 매핑 드라이브(K: 등)가 보인다. 매핑 드라이브는 로그온 세션마다 따로이고,
//!   `WTSQueryUserToken`은 관리자 계정에서 탐색기와 다른 로그온 세션 토큰을 줄 수 있다 (실제 PC에서 확인, 0.2.11).
//!   어느 쪽이든 사용자의 일반(제한) 토큰이라 Foldy는 관리자 권한이 아니다.
//! - 업데이트 폴더: 소유자 SYSTEM, 상속 끊은 DACL (SYSTEM·Administrators 모두, Users 읽기·실행).

use std::ffi::c_void;
use std::io;
use std::os::windows::ffi::OsStrExt;
use std::os::windows::fs::MetadataExt;
use std::os::windows::process::CommandExt;
use std::path::Path;
use std::process::{Command, Stdio};

use windows_sys::Win32::Foundation::{CloseHandle, HANDLE, HLOCAL, LocalFree};
use windows_sys::Win32::Security::Authorization::{
    ConvertSidToStringSidW, ConvertStringSecurityDescriptorToSecurityDescriptorW, GetNamedSecurityInfoW, SDDL_REVISION_1,
    SE_FILE_OBJECT, SetNamedSecurityInfoW,
};
use windows_sys::Win32::Security::{
    DuplicateTokenEx, SecurityImpersonation, TOKEN_ASSIGN_PRIMARY, TOKEN_DUPLICATE, TOKEN_QUERY, TokenPrimary,
    ACL, DACL_SECURITY_INFORMATION, GetSecurityDescriptorDacl, GetSecurityDescriptorOwner, OWNER_SECURITY_INFORMATION,
    PROTECTED_DACL_SECURITY_INFORMATION, PSECURITY_DESCRIPTOR, PSID,
};
use windows_sys::Win32::Storage::FileSystem::{FILE_ATTRIBUTE_REPARSE_POINT, GetFileVersionInfoSizeW, GetFileVersionInfoW, VerQueryValueW};
use windows_sys::Win32::System::Environment::{CreateEnvironmentBlock, DestroyEnvironmentBlock};
use windows_sys::Win32::System::Pipes::GetNamedPipeClientSessionId;
use windows_sys::Win32::System::ProcessStatus::K32EnumProcesses;
use windows_sys::Win32::System::RemoteDesktop::{ProcessIdToSessionId, WTSGetActiveConsoleSessionId, WTSQueryUserToken};
use windows_sys::Win32::System::Threading::{
    CREATE_BREAKAWAY_FROM_JOB, CREATE_NEW_PROCESS_GROUP, CREATE_UNICODE_ENVIRONMENT, CreateProcessAsUserW, DETACHED_PROCESS,
    GetCurrentThread, OpenProcess, OpenProcessToken, PROCESS_INFORMATION, PROCESS_QUERY_LIMITED_INFORMATION, PROCESS_TERMINATE, QueryFullProcessImageNameW,
    STARTUPINFOW, SetThreadPriority, TerminateProcess, THREAD_MODE_BACKGROUND_BEGIN, THREAD_MODE_BACKGROUND_END,
};

use super::host::Host;

/// 업데이트 폴더 보안 설명자: 소유자 SYSTEM, 보호된 DACL. 0x1200a9 = FILE_GENERIC_READ | FILE_GENERIC_EXECUTE.
pub const UPDATES_SDDL: &str = "O:SYD:PAI(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;0x1200a9;;;BU)";

const SID_SYSTEM: &str = "S-1-5-18";
const SID_ADMINISTRATORS: &str = "S-1-5-32-544";

pub struct WinHost;

/// 세션 `session_id`의 explorer.exe 토큰을 기본 토큰으로 복제한다. 없거나 실패하면 None.
fn explorer_token(session_id: u32) -> Option<HANDLE> {
    let mut pids = vec![0u32; 8192];
    let mut needed = 0u32;
    // SAFETY: pids는 바이트 크기를 정확히 넘긴 쓰기 가능한 버퍼다.
    if unsafe { K32EnumProcesses(pids.as_mut_ptr(), (pids.len() * 4) as u32, &mut needed) } == 0 {
        return None;
    }
    pids.truncate((needed / 4) as usize);
    for pid in pids.into_iter().filter(|&p| p != 0) {
        let mut sid = u32::MAX;
        // SAFETY: 출력 정수 하나.
        if unsafe { ProcessIdToSessionId(pid, &mut sid) } == 0 || sid != session_id {
            continue;
        }
        let Some(path) = image_path(pid) else { continue };
        if !path.to_lowercase().ends_with("\\explorer.exe") {
            continue;
        }
        // SAFETY: 핸들은 아래에서 모두 닫는다.
        unsafe {
            let proc = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, 0, pid);
            if proc.is_null() {
                continue;
            }
            let mut tok: HANDLE = std::ptr::null_mut();
            let opened = OpenProcessToken(proc, TOKEN_DUPLICATE | TOKEN_QUERY | TOKEN_ASSIGN_PRIMARY, &mut tok) != 0;
            CloseHandle(proc);
            if !opened {
                continue;
            }
            let mut primary: HANDLE = std::ptr::null_mut();
            let dup = DuplicateTokenEx(
                tok,
                TOKEN_DUPLICATE | TOKEN_QUERY | TOKEN_ASSIGN_PRIMARY | 0x0080 /* TOKEN_ADJUST_DEFAULT */ | 0x0100, /* TOKEN_ADJUST_SESSIONID */
                std::ptr::null(),
                SecurityImpersonation,
                TokenPrimary,
                &mut primary,
            ) != 0;
            CloseHandle(tok);
            if dup {
                return Some(primary);
            }
        }
    }
    None
}

fn wide(s: &std::ffi::OsStr) -> Vec<u16> {
    s.encode_wide().chain([0]).collect()
}

/// `exe`로 실행 중인 모든 프로세스 ID.
fn processes_of(exe: &Path) -> Vec<u32> {
    let want = exe.as_os_str().to_string_lossy().to_lowercase();
    let mut pids = vec![0u32; 8192];
    let mut needed = 0u32;
    // SAFETY: pids는 바이트 크기를 정확히 넘긴 쓰기 가능한 버퍼다.
    if unsafe { K32EnumProcesses(pids.as_mut_ptr(), (pids.len() * 4) as u32, &mut needed) } == 0 {
        return Vec::new();
    }
    pids.truncate((needed / 4) as usize);
    pids.into_iter()
        .filter(|&pid| pid != 0 && image_path(pid).is_some_and(|p| p.to_lowercase() == want))
        .collect()
}

/// 이 프로세스가 AI 연결용 Foldy(`Foldy.exe --mcp`)인가. 명령줄을 못 읽으면 아니라고 본다 (창 Foldy로 세어 기다린다).
fn is_mcp_process(pid: u32) -> bool {
    command_line(pid).is_some_and(|c| is_mcp_command_line(&c))
}

/// 명령줄에 `--mcp` 인자가 있나 (실행 파일 경로 뒤의 낱말 중에서).
pub fn is_mcp_command_line(cmd: &str) -> bool {
    let rest = if let Some(r) = cmd.trim_start().strip_prefix('"') {
        r.split_once('"').map_or("", |(_, after)| after)
    } else {
        cmd.trim_start().split_once(char::is_whitespace).map_or("", |(_, after)| after)
    };
    rest.split_whitespace().any(|a| a.trim_matches('"').eq_ignore_ascii_case("--mcp"))
}

#[link(name = "ntdll")]
unsafe extern "system" {
    fn NtQueryInformationProcess(process: HANDLE, class: u32, info: *mut c_void, len: u32, ret: *mut u32) -> i32;
}

/// 다른 프로세스의 명령줄 (ProcessCommandLineInformation, 조회 전용 권한으로 된다).
fn command_line(pid: u32) -> Option<String> {
    const PROCESS_COMMAND_LINE_INFORMATION: u32 = 60;
    // SAFETY: 조회 전용 권한으로 연다. 실패하면 null.
    let h = unsafe { OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, 0, pid) };
    if h.is_null() {
        return None;
    }
    // UNICODE_STRING { Length: u16, MaximumLength: u16, Buffer: *u16 } 뒤에 글자가 온다
    let mut buf = vec![0u64; 8192 / 8];
    let mut ret = 0u32;
    // SAFETY: buf 바이트 크기를 정확히 넘긴다. 이어서 핸들을 닫는다.
    let status = unsafe { NtQueryInformationProcess(h, PROCESS_COMMAND_LINE_INFORMATION, buf.as_mut_ptr().cast(), (buf.len() * 8) as u32, &mut ret) };
    // SAFETY: 위에서 연 핸들을 한 번 닫는다.
    unsafe { CloseHandle(h) };
    if status < 0 {
        return None;
    }
    let base = buf.as_ptr().cast::<u8>();
    // SAFETY: 성공하면 buf 앞에 UNICODE_STRING이 있고, Buffer는 buf 안을 가리킨다. 길이를 buf 크기로 다시 확인한다.
    unsafe {
        let len = usize::from(*base.cast::<u16>()) / 2;
        let ptr = *base.add(8).cast::<*const u16>();
        let start = (ptr as usize).checked_sub(base as usize)?;
        if ptr.is_null() || start + len * 2 > buf.len() * 8 {
            return None;
        }
        Some(String::from_utf16_lossy(std::slice::from_raw_parts(ptr, len)))
    }
}

impl Host for WinHost {
    fn foldy_processes(&self, exe: &Path) -> Vec<u32> {
        processes_of(exe).into_iter().filter(|&p| !is_mcp_process(p)).collect()
    }

    fn mcp_processes(&self, exe: &Path) -> Vec<u32> {
        processes_of(exe).into_iter().filter(|&p| is_mcp_process(p)).collect()
    }

    fn end_process(&self, pid: u32) -> bool {
        // 끝내기 직전에 다시 확인한다 (그 사이 번호가 다른 프로세스에 다시 쓰였을 수 있다)
        if !is_mcp_process(pid) {
            return false;
        }
        // SAFETY: 끝내기 권한으로 열고, 끝낸 뒤 닫는다.
        unsafe {
            let h = OpenProcess(PROCESS_TERMINATE, 0, pid);
            if h.is_null() {
                return false;
            }
            let ok = TerminateProcess(h, 1) != 0;
            CloseHandle(h);
            ok
        }
    }

    fn run_installer(&self, mut cmd: Command) -> io::Result<i32> {
        cmd.stdin(Stdio::null()).stdout(Stdio::null()).stderr(Stdio::null());
        cmd.creation_flags(DETACHED_PROCESS | CREATE_NEW_PROCESS_GROUP | CREATE_BREAKAWAY_FROM_JOB);
        let mut child = match cmd.spawn() {
            Ok(c) => c,
            // 작업 개체가 분리를 허용하지 않으면 (서비스는 보통 작업 개체 밖이다) 분리 없이 다시
            Err(e) if e.raw_os_error() == Some(5) => {
                cmd.creation_flags(DETACHED_PROCESS | CREATE_NEW_PROCESS_GROUP);
                cmd.spawn()?
            }
            Err(e) => return Err(e),
        };
        Ok(child.wait()?.code().unwrap_or(-1))
    }

    fn launch_foldy(&self, exe: &Path, session_id: u32) -> io::Result<u32> {
        if !exe.is_file() {
            return Err(io::Error::new(io::ErrorKind::NotFound, format!("{} 없음", exe.display())));
        }
        let mut token: HANDLE = match explorer_token(session_id) {
            Some(t) => t,
            None => std::ptr::null_mut(),
        };
        // SAFETY: token은 출력 핸들. 성공하면 아래에서 닫는다. SYSTEM(SE_TCB_NAME)만 성공한다.
        if token.is_null() && unsafe { WTSQueryUserToken(session_id, &mut token) } == 0 {
            return Err(io::Error::last_os_error());
        }
        let mut env: *mut c_void = std::ptr::null_mut();
        // SAFETY: token은 유효한 사용자 토큰, env는 출력 포인터. 성공하면 DestroyEnvironmentBlock.
        let env_ok = unsafe { CreateEnvironmentBlock(&mut env, token, 0) } != 0;
        let app = wide(exe.as_os_str());
        let mut cmdline: Vec<u16> = format!("\"{}\"", exe.display()).encode_utf16().chain([0]).collect();
        let cwd = exe.parent().map(|p| wide(p.as_os_str()));
        let mut desktop: Vec<u16> = "winsta0\\default".encode_utf16().chain([0]).collect();
        let si = STARTUPINFOW { cb: size_of::<STARTUPINFOW>() as u32, lpDesktop: desktop.as_mut_ptr(), ..Default::default() };
        let mut pi = PROCESS_INFORMATION {
            hProcess: std::ptr::null_mut(),
            hThread: std::ptr::null_mut(),
            dwProcessId: 0,
            dwThreadId: 0,
        };
        // SAFETY: 모든 문자열은 NUL 종료 UTF-16이고 호출 동안 살아 있다. cmdline은 쓰기 가능한 버퍼.
        // env는 CreateEnvironmentBlock이 만든 유니코드 블록이거나 null(상속).
        let ok = unsafe {
            CreateProcessAsUserW(
                token,
                app.as_ptr(),
                cmdline.as_mut_ptr(),
                std::ptr::null(),
                std::ptr::null(),
                0,
                CREATE_UNICODE_ENVIRONMENT,
                if env_ok { env } else { std::ptr::null() },
                cwd.as_ref().map_or(std::ptr::null(), |c| c.as_ptr()),
                &si,
                &mut pi,
            )
        };
        let err = io::Error::last_os_error();
        // SAFETY: 위에서 얻은 핸들·블록을 한 번씩 해제한다.
        unsafe {
            if env_ok {
                DestroyEnvironmentBlock(env);
            }
            CloseHandle(token);
            if ok != 0 {
                CloseHandle(pi.hThread);
                CloseHandle(pi.hProcess);
            }
        }
        if ok == 0 { Err(err) } else { Ok(pi.dwProcessId) }
    }

    fn active_console_session(&self) -> u32 {
        // SAFETY: 인자 없는 조회 함수.
        unsafe { WTSGetActiveConsoleSessionId() }
    }

    fn secure_updates_dir(&self, dir: &Path) -> io::Result<bool> {
        // 부모(%ProgramData%\Foldy): 링크면 지우고, 일반 사용자가 만든 것이면 소유자·DACL을 바로잡는다.
        // (부모의 소유자가 사용자면 Updates를 다른 이름으로 바꿔 끼울 수 있으므로)
        if let Some(parent) = dir.parent() {
            remove_if_reparse(parent)?;
            std::fs::create_dir_all(parent)?;
            if !owner_is_trusted(parent)? {
                apply_sddl(parent, UPDATES_SDDL)?;
            }
        }
        remove_if_reparse(dir)?;
        let mut wiped = false;
        if dir.exists() && !owner_is_trusted(dir)? {
            // 다른 사용자가 미리 만든 폴더: 안의 파일은 믿을 수 없다
            std::fs::remove_dir_all(dir)?;
            wiped = true;
        }
        std::fs::create_dir_all(dir)?;
        apply_sddl(dir, UPDATES_SDDL)?;
        Ok(wiped)
    }
}

/// 링크(정션·심볼릭 링크)면 링크 자체만 지운다.
fn remove_if_reparse(p: &Path) -> io::Result<()> {
    match std::fs::symlink_metadata(p) {
        Ok(m) if m.file_attributes() & FILE_ATTRIBUTE_REPARSE_POINT != 0 => std::fs::remove_dir(p).or_else(|_| std::fs::remove_file(p)),
        _ => Ok(()),
    }
}

fn image_path(pid: u32) -> Option<String> {
    // SAFETY: 조회 전용 권한으로 연다. 실패하면 null.
    let h = unsafe { OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, 0, pid) };
    if h.is_null() {
        return None;
    }
    let mut buf = vec![0u16; 1024];
    let mut len = buf.len() as u32;
    // SAFETY: buf 길이를 len으로 정확히 넘긴다. 이어서 핸들을 닫는다.
    let ok = unsafe { QueryFullProcessImageNameW(h, 0, buf.as_mut_ptr(), &mut len) };
    // SAFETY: 위에서 연 핸들을 한 번 닫는다.
    unsafe { CloseHandle(h) };
    (ok != 0).then(|| String::from_utf16_lossy(&buf[..len as usize]))
}

fn owner_is_trusted(p: &Path) -> io::Result<bool> {
    let w = wide(p.as_os_str());
    let mut owner: PSID = std::ptr::null_mut();
    let mut sd: PSECURITY_DESCRIPTOR = std::ptr::null_mut();
    // SAFETY: w는 NUL 종료 경로, 출력 포인터들은 지역 변수. sd는 LocalFree로 해제한다.
    let rc = unsafe {
        GetNamedSecurityInfoW(
            w.as_ptr(),
            SE_FILE_OBJECT,
            OWNER_SECURITY_INFORMATION,
            &mut owner,
            std::ptr::null_mut(),
            std::ptr::null_mut(),
            std::ptr::null_mut(),
            &mut sd,
        )
    };
    if rc != 0 {
        return Err(io::Error::from_raw_os_error(rc as i32));
    }
    let sid = sid_string(owner);
    // SAFETY: GetNamedSecurityInfoW가 할당한 설명자를 한 번 해제한다 (owner는 그 안을 가리킨다).
    unsafe { LocalFree(sd as HLOCAL) };
    Ok(sid.is_some_and(|s| s == SID_SYSTEM || s == SID_ADMINISTRATORS))
}

fn sid_string(sid: PSID) -> Option<String> {
    if sid.is_null() {
        return None;
    }
    let mut s: *mut u16 = std::ptr::null_mut();
    // SAFETY: sid는 유효한 SID. 출력 문자열은 LocalFree로 해제한다.
    if unsafe { ConvertSidToStringSidW(sid, &mut s) } == 0 || s.is_null() {
        return None;
    }
    // SAFETY: s는 NUL 종료 UTF-16 문자열이다.
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

/// SDDL의 소유자와 DACL(상속 끊음)을 폴더에 적용한다. 하위 항목에는 상속 ACE가 전파된다.
pub fn apply_sddl(p: &Path, sddl: &str) -> io::Result<()> {
    let ws: Vec<u16> = sddl.encode_utf16().chain([0]).collect();
    let mut sd: PSECURITY_DESCRIPTOR = std::ptr::null_mut();
    // SAFETY: ws는 NUL 종료 문자열, sd는 출력 포인터. 성공하면 LocalFree.
    if unsafe { ConvertStringSecurityDescriptorToSecurityDescriptorW(ws.as_ptr(), SDDL_REVISION_1, &mut sd, std::ptr::null_mut()) } == 0 {
        return Err(io::Error::last_os_error());
    }
    let mut present = 0;
    let mut defaulted = 0;
    let mut dacl: *mut ACL = std::ptr::null_mut();
    let mut owner: PSID = std::ptr::null_mut();
    // SAFETY: sd는 방금 만든 자기 상대 설명자. 출력은 sd 내부를 가리킨다.
    let ok = unsafe {
        GetSecurityDescriptorDacl(sd, &mut present, &mut dacl, &mut defaulted) != 0
            && GetSecurityDescriptorOwner(sd, &mut owner, &mut defaulted) != 0
    };
    let rc = if ok {
        let w = wide(p.as_os_str());
        // SAFETY: w는 NUL 종료 경로, owner·dacl은 sd가 살아 있는 동안 유효하다.
        unsafe {
            SetNamedSecurityInfoW(
                w.as_ptr(),
                SE_FILE_OBJECT,
                OWNER_SECURITY_INFORMATION | DACL_SECURITY_INFORMATION | PROTECTED_DACL_SECURITY_INFORMATION,
                owner,
                std::ptr::null_mut(),
                dacl,
                std::ptr::null(),
            )
        }
    } else {
        io::Error::last_os_error().raw_os_error().unwrap_or(1) as u32
    };
    // SAFETY: ConvertStringSecurityDescriptorToSecurityDescriptorW가 할당한 메모리를 한 번 해제한다.
    unsafe { LocalFree(sd as HLOCAL) };
    if rc == 0 { Ok(()) } else { Err(io::Error::from_raw_os_error(rc as i32)) }
}

/// SDDL 문자열이 올바른지만 확인 (시험용: 실제 폴더에 적용하지 않는다).
#[cfg(test)]
pub fn sddl_is_valid(sddl: &str) -> bool {
    let ws: Vec<u16> = sddl.encode_utf16().chain([0]).collect();
    let mut sd: PSECURITY_DESCRIPTOR = std::ptr::null_mut();
    // SAFETY: 위 apply_sddl과 같다. 성공하면 바로 해제한다.
    let ok = unsafe { ConvertStringSecurityDescriptorToSecurityDescriptorW(ws.as_ptr(), SDDL_REVISION_1, &mut sd, std::ptr::null_mut()) } != 0;
    if ok {
        // SAFETY: 방금 할당된 설명자를 한 번 해제한다.
        unsafe { LocalFree(sd as HLOCAL) };
    }
    ok
}

/// 파일 버전 리소스의 ProductVersion 문자열 (.NET은 InformationalVersion, 예: "1.2.3+abc").
pub fn product_version(exe: &Path) -> Option<String> {
    let w = wide(exe.as_os_str());
    let mut handle = 0u32;
    // SAFETY: w는 NUL 종료 경로.
    let n = unsafe { GetFileVersionInfoSizeW(w.as_ptr(), &mut handle) };
    if n == 0 {
        return None;
    }
    let mut buf = vec![0u8; n as usize];
    // SAFETY: buf는 n바이트.
    if unsafe { GetFileVersionInfoW(w.as_ptr(), 0, n, buf.as_mut_ptr().cast()) } == 0 {
        return None;
    }
    let mut keys = Vec::new();
    if let Some(t) = query(&buf, "\\VarFileInfo\\Translation")
        && t.len() >= 4
    {
        let lang = u16::from_le_bytes([t[0], t[1]]);
        let cp = u16::from_le_bytes([t[2], t[3]]);
        keys.push(format!("\\StringFileInfo\\{lang:04x}{cp:04x}\\ProductVersion"));
    }
    keys.push("\\StringFileInfo\\000004b0\\ProductVersion".into());
    keys.push("\\StringFileInfo\\040904b0\\ProductVersion".into());
    keys.iter().find_map(|k| {
        let v = query(&buf, k)?;
        let units: Vec<u16> = v.as_chunks::<2>().0.iter().map(|c| u16::from_le_bytes(*c)).take_while(|&u| u != 0).collect();
        let s = String::from_utf16_lossy(&units).trim().to_string();
        (!s.is_empty()).then_some(s)
    })
}

fn query<'a>(block: &'a [u8], key: &str) -> Option<&'a [u8]> {
    let k: Vec<u16> = key.encode_utf16().chain([0]).collect();
    let mut p: *mut c_void = std::ptr::null_mut();
    let mut len = 0u32;
    // SAFETY: block은 GetFileVersionInfoW가 채운 버퍼, 결과 포인터는 block 안을 가리킨다.
    if unsafe { VerQueryValueW(block.as_ptr().cast(), k.as_ptr(), &mut p, &mut len) } == 0 || p.is_null() {
        return None;
    }
    let start = (p as usize).checked_sub(block.as_ptr() as usize)?;
    // 문자열 값의 len은 UTF-16 글자 수, Translation은 바이트 수 → 넉넉히 잡고 버퍼 끝에서 자른다
    let bytes = (len as usize) * 2;
    let end = (start + bytes).min(block.len());
    block.get(start..end)
}

/// 파이프 클라이언트의 세션 ID.
pub fn pipe_client_session(pipe: HANDLE) -> Option<u32> {
    let mut id = 0u32;
    // SAFETY: pipe는 연결된 서버 쪽 파이프 핸들이다.
    (unsafe { GetNamedPipeClientSessionId(pipe, &mut id) } != 0).then_some(id)
}

/// 이 스레드를 배경 우선순위(입출력·네트워크 낮춤)로 둔다. drop하면 되돌린다.
pub struct BackgroundPriority(bool);

impl BackgroundPriority {
    pub fn enter() -> Self {
        // SAFETY: 현재 스레드 의사 핸들에 대한 우선순위 변경.
        BackgroundPriority(unsafe { SetThreadPriority(GetCurrentThread(), THREAD_MODE_BACKGROUND_BEGIN) } != 0)
    }
}

impl Drop for BackgroundPriority {
    fn drop(&mut self) {
        if self.0 {
            // SAFETY: enter에서 시작한 배경 모드를 같은 스레드에서 끝낸다.
            unsafe { SetThreadPriority(GetCurrentThread(), THREAD_MODE_BACKGROUND_END) };
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn updates_sddl_parses() {
        assert!(sddl_is_valid(UPDATES_SDDL));
        assert!(!sddl_is_valid("D:(nonsense"));
    }

    #[test]
    fn current_process_is_listed_by_image_path() {
        let me = std::env::current_exe().unwrap();
        let pids = WinHost.foldy_processes(&me);
        assert!(pids.contains(&std::process::id()));
        assert!(WinHost.foldy_processes(Path::new(r"C:\없는\Foldy.exe")).is_empty());
        // 시험 프로세스는 --mcp 없이 떴다: 창 Foldy로 센다
        assert!(WinHost.mcp_processes(&me).is_empty());
        assert!(command_line(std::process::id()).is_some_and(|c| !c.is_empty()));
    }

    #[test]
    fn mcp_command_line_is_recognized() {
        assert!(is_mcp_command_line(r#""C:\Program Files\Foldy\Foldy.exe" --mcp"#));
        assert!(is_mcp_command_line(r"C:\Foldy\Foldy.exe --MCP"));
        assert!(!is_mcp_command_line(r#""C:\Program Files\Foldy\Foldy.exe""#));
        assert!(!is_mcp_command_line(r#""C:\Program Files\Foldy\Foldy.exe" "D:\--mcp 폴더""#));
        assert!(!is_mcp_command_line(r#""C:\x --mcp\Foldy.exe" D:\"#)); // 경로 안의 글자는 인자가 아니다
        assert!(!is_mcp_command_line(""));
    }

    #[test]
    fn missing_exe_is_not_launched() {
        assert!(WinHost.launch_foldy(Path::new(r"C:\없는\Foldy.exe"), 1).is_err());
    }

    #[test]
    fn reparse_check_ignores_plain_dirs() {
        let d = std::env::temp_dir().join(format!("foldy-index-reparse-{}", std::process::id()));
        std::fs::create_dir_all(&d).unwrap();
        remove_if_reparse(&d).unwrap();
        assert!(d.is_dir(), "일반 폴더는 지우지 않는다");
        std::fs::remove_dir_all(&d).ok();
    }
}
