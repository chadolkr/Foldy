//! `foldy-hotkey.exe` — Foldy가 꺼져 있어도 단축키(기본 Ctrl+Space)로 무엇이든 찾기 창을 연다 (1.11.0, docs/DECISIONS.md).
//!
//! 로그인할 때 시작해(설치 파일의 Run 등록, 없으면 Foldy가 사용자 Run에 등록) 단축키 하나만 기다리는 아주 작은 프로그램이다.
//! 창도, 아이콘도, 다른 라이브러리도 없다(메모리 약 1MB). 단축키를 누르면
//! - Foldy가 켜져 있으면 파이프로 "찾기 창만 열어 줘"를 보내고 (주 창은 그대로)
//! - 꺼져 있으면 같은 폴더의 `Foldy.exe --quick-search`를 띄운다 (주 창 없이 찾기 창만).
//!
//! 키와 켜고 끄기는 Foldy가 `%LOCALAPPDATA%\Foldy\quick-search-key.txt`에 적는다. 키를 바꾸면 Foldy가 이 도우미를 끄고 다시 띄운다.
//! 키를 다른 프로그램이 이미 쓰면 조용히 끝난다: 그때는 Foldy가 켜질 때 알리고 다른 키를 고르게 한다(기존 흐름).
//!
//! 1.13.0: 느린 드라이브 목록 주기 모으기도 한다 (`gather-every.txt`). 1분마다 설정을 다시 읽고, 때가 되면
//! Foldy가 꺼져 있을 때만 `Foldy.exe --gather`를 띄운다 (Foldy가 켜져 있으면 그 Foldy가 모은다).
//! 단축키를 쓰지 않아도(꺼짐·겹침) 주기 모으기가 켜져 있으면 남아 있고, 둘 다 꺼지면 끝난다.
//! 작업 스케줄러는 쓰지 않는다 (사용자 2026-11-25 "작업 스케쥴러는 안하고 싶다", "관리가 쉬운 방법으로"):
//! 새로 등록할 것도, 지울 때 따로 지울 것도 없다.
#![cfg_attr(windows, windows_subsystem = "windows")]

#[cfg(not(windows))]
fn main() {}

#[cfg(windows)]
fn main() {
    std::process::exit(win::run());
}

#[cfg(windows)]
mod win {
    use std::io::{Read, Write};
    use std::path::PathBuf;
    use std::sync::atomic::{AtomicBool, Ordering};
    use std::time::{Duration, SystemTime, UNIX_EPOCH};

    use foldy_hotkey::{
        Config, GATHER_ARG, GATHER_FILE, KEY_WINDOW_NAME, MOD_CONTROL, QUICK_SEARCH_ARG, VK_SPACE,
        WINDOW_CLASS, activate_pipe_path, encode_args, gather_due, instance_event_name,
        layouts_conflict_with_ctrl_space, parse_config, parse_gather,
    };
    use windows_sys::Win32::Foundation::{
        CloseHandle, ERROR_ALREADY_EXISTS, GetLastError, HWND, LPARAM, LRESULT, WPARAM,
    };
    use windows_sys::Win32::System::LibraryLoader::GetModuleHandleW;
    use windows_sys::Win32::System::RemoteDesktop::ProcessIdToSessionId;
    use windows_sys::Win32::System::Threading::{
        CreateMutexW, GetCurrentProcessId, OpenEventW, SYNCHRONIZATION_SYNCHRONIZE,
    };
    use windows_sys::Win32::System::WindowsProgramming::GetUserNameW;
    use windows_sys::Win32::UI::Input::KeyboardAndMouse::{
        GetKeyboardLayoutList, MOD_NOREPEAT, RegisterHotKey, UnregisterHotKey,
    };
    use windows_sys::Win32::UI::WindowsAndMessaging::{
        AllowSetForegroundWindow, CreateWindowExW, DefWindowProcW, DestroyWindow, DispatchMessageW,
        GetMessageW, HWND_MESSAGE, KillTimer, MSG, PostQuitMessage, RegisterClassW, SetTimer,
        WM_CLOSE, WM_DESTROY, WM_HOTKEY, WM_TIMER, WNDCLASSW,
    };

    const HOTKEY_ID: i32 = 0xF01D;
    const GATHER_TIMER: usize = 0xF01E;
    /// 주기 모으기 설정을 다시 읽는 간격.
    const GATHER_CHECK_MS: u32 = 60_000;
    const ASFW_ANY: u32 = u32::MAX;

    /// 단축키를 짧게 여러 번 눌러도 요청은 하나씩만 (앞 요청이 끝나기 전 누른 것은 버린다).
    static BUSY: AtomicBool = AtomicBool::new(false);

    fn wide(s: &str) -> Vec<u16> {
        s.encode_utf16().chain(std::iter::once(0)).collect()
    }

    pub fn run() -> i32 {
        // 같은 로그온 세션에 하나만 (Local\ 이름은 세션마다 따로)
        let mutex_name = wide(r"Local\Foldy.QuickSearchKey");
        // SAFETY: 이름은 0으로 끝나는 UTF-16, 보안 속성은 기본(null). 핸들은 프로세스가 끝날 때까지 쥐고 있는다.
        let mutex = unsafe { CreateMutexW(std::ptr::null(), 0, mutex_name.as_ptr()) };
        // SAFETY: 바로 앞 호출의 오류 값을 읽는다.
        if mutex.is_null() || unsafe { GetLastError() } == ERROR_ALREADY_EXISTS {
            return 0;
        }

        let key = match parse_config(&read_file(foldy_hotkey::CONFIG_FILE)) {
            Config::Off => Err(0),
            Config::Key { mods, vk } => Ok((mods, vk)),
            Config::Default if layouts_conflict_with_ctrl_space(&keyboard_layouts()) => Err(3), // 입력기 전환 키를 빼앗지 않는다
            Config::Default => Ok((MOD_CONTROL, VK_SPACE)),
        };

        // 창 없이(스레드 메시지로) 먼저 등록한다: 실패하면 Foldy가 키를 받는 도우미 창(이름 "key")을 찾지 못해 스스로 등록·안내한다
        let holds_key = match key {
            Ok((mods, vk)) => {
                // SAFETY: hwnd null = 이 스레드의 메시지 큐로 WM_HOTKEY가 온다.
                if unsafe {
                    RegisterHotKey(std::ptr::null_mut(), HOTKEY_ID, mods | MOD_NOREPEAT, vk)
                } == 0
                {
                    Err(2)
                } else {
                    Ok(())
                }
            }
            Err(code) => Err(code),
        };
        if let Err(code) = holds_key
            && gather_hours().is_none()
        {
            return code; // 단축키도 주기 모으기도 쓰지 않는다
        }
        let holds_key = holds_key.is_ok();

        let hwnd = create_message_window(holds_key);
        // SAFETY: 이 스레드의 창에 1분 타이머 (WM_TIMER는 아래 메시지 루프에서 받는다).
        unsafe { SetTimer(hwnd, GATHER_TIMER, GATHER_CHECK_MS, None) };
        let started = now_secs();
        let mut last_gather: Option<u64> = None;
        // SAFETY: MSG는 0으로 채워도 되는 C 구조체.
        let mut msg: MSG = unsafe { std::mem::zeroed() };
        // SAFETY: 이 스레드의 메시지 루프. msg는 유효한 MSG.
        while unsafe { GetMessageW(&mut msg, std::ptr::null_mut(), 0, 0) } > 0 {
            if msg.message == WM_TIMER && msg.wParam == GATHER_TIMER {
                match gather_hours() {
                    None if !holds_key => {
                        // 주기 모으기를 껐고 단축키도 쓰지 않는다: 남아 있을 까닭이 없다
                        // SAFETY: 메시지 루프를 끝낸다.
                        unsafe { PostQuitMessage(0) };
                    }
                    None => {}
                    Some(hours) => {
                        let now = now_secs();
                        if gather_due(now, started, last_gather, hours) {
                            last_gather = Some(now);
                            if !foldy_running() {
                                std::thread::spawn(|| launch_foldy(GATHER_ARG));
                            }
                        }
                    }
                }
                continue;
            }

            if msg.message == WM_HOTKEY && msg.wParam == HOTKEY_ID as usize {
                if !BUSY.swap(true, Ordering::AcqRel) {
                    // 누른 순간 이 프로세스가 앞으로 나올 권한을 받는다: 띄울 Foldy·실행 중인 Foldy에 넘긴다
                    // SAFETY: 인자 없는 단순 호출.
                    unsafe { AllowSetForegroundWindow(ASFW_ANY) };
                    std::thread::spawn(|| {
                        open_quick_search();
                        BUSY.store(false, Ordering::Release);
                    });
                }
                continue;
            }

            // SAFETY: GetMessageW가 채운 메시지를 창 프로시저로 보낸다.
            unsafe { DispatchMessageW(&msg) };
        }

        // SAFETY: 위에서 등록한 키·타이머와 만든 창·뮤텍스를 정리한다.
        unsafe {
            if holds_key {
                UnregisterHotKey(std::ptr::null_mut(), HOTKEY_ID);
            }
            KillTimer(hwnd, GATHER_TIMER);
            if !hwnd.is_null() {
                DestroyWindow(hwnd);
            }
            CloseHandle(mutex);
        }
        0
    }

    fn read_file(name: &str) -> String {
        std::env::var_os("LOCALAPPDATA")
            .map(|d| PathBuf::from(d).join("Foldy").join(name))
            .and_then(|p| std::fs::read_to_string(p).ok())
            .unwrap_or_default()
    }

    fn gather_hours() -> Option<u32> {
        parse_gather(&read_file(GATHER_FILE))
    }

    fn now_secs() -> u64 {
        SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .map_or(0, |d| d.as_secs())
    }

    /// 이 사용자의 Foldy가 켜져 있나 (첫 Foldy가 만들어 둔 이벤트가 있나). 찾기 창만 떠 있는 Foldy도 켜진 것으로 본다.
    fn foldy_running() -> bool {
        let Some((session, user)) = session_and_user() else {
            return false;
        };
        let name = wide(&instance_event_name(session, &user));
        // SAFETY: 이름은 0으로 끝나는 UTF-16. 열리면 바로 닫는다.
        let h = unsafe { OpenEventW(SYNCHRONIZATION_SYNCHRONIZE, 0, name.as_ptr()) };
        if h.is_null() {
            return false;
        }
        // SAFETY: 위에서 연 핸들.
        unsafe { CloseHandle(h) };
        true
    }

    fn keyboard_layouts() -> Vec<usize> {
        // SAFETY: 개수만 묻고(버퍼 null), 그 크기의 버퍼로 다시 묻는다.
        let n = unsafe { GetKeyboardLayoutList(0, std::ptr::null_mut()) };
        if n <= 0 {
            return Vec::new();
        }
        let mut list = vec![std::ptr::null_mut(); n as usize];
        // SAFETY: list는 n개 HKL을 담을 수 있다.
        let got = unsafe { GetKeyboardLayoutList(n, list.as_mut_ptr()) };
        list.truncate(got.max(0) as usize);
        list.into_iter().map(|h| h as usize).collect()
    }

    /// Foldy가 FindWindowEx(HWND_MESSAGE, 클래스)로 찾아 WM_CLOSE를 보낼 수 있는 메시지 전용 창.
    fn create_message_window(holds_key: bool) -> HWND {
        let class = wide(WINDOW_CLASS);
        let name = wide(if holds_key { KEY_WINDOW_NAME } else { "" });
        // SAFETY: 클래스·창 만들기. 이름 버퍼는 이 함수 안에서 살아 있고, 창 프로시저는 'static 함수.
        unsafe {
            let instance = GetModuleHandleW(std::ptr::null());
            let mut wc: WNDCLASSW = std::mem::zeroed();
            wc.lpfnWndProc = Some(wnd_proc);
            wc.hInstance = instance;
            wc.lpszClassName = class.as_ptr();
            RegisterClassW(&wc);
            CreateWindowExW(
                0,
                class.as_ptr(),
                name.as_ptr(),
                0,
                0,
                0,
                0,
                0,
                HWND_MESSAGE,
                std::ptr::null_mut(),
                instance,
                std::ptr::null(),
            )
        }
    }

    unsafe extern "system" fn wnd_proc(
        hwnd: HWND,
        msg: u32,
        wparam: WPARAM,
        lparam: LPARAM,
    ) -> LRESULT {
        match msg {
            WM_CLOSE => {
                // SAFETY: 자기 창을 닫는다 (Foldy가 키를 바꾸거나 껐을 때 보냄).
                unsafe { DestroyWindow(hwnd) };
                0
            }
            WM_DESTROY => {
                // SAFETY: 메시지 루프를 끝낸다.
                unsafe { PostQuitMessage(0) };
                0
            }
            // SAFETY: 나머지는 기본 처리.
            _ => unsafe { DefWindowProcW(hwnd, msg, wparam, lparam) },
        }
    }

    fn open_quick_search() {
        if !send_to_running_foldy() {
            launch_foldy(QUICK_SEARCH_ARG);
        }
    }

    /// 실행 중인 Foldy의 파이프에 요청을 보낸다. Foldy가 없거나 응답이 없으면 false.
    fn send_to_running_foldy() -> bool {
        let Some(path) = pipe_path() else {
            return false;
        };
        let mut pipe = None;
        for _ in 0..10 {
            match std::fs::OpenOptions::new()
                .read(true)
                .write(true)
                .open(&path)
            {
                Ok(p) => {
                    pipe = Some(p);
                    break;
                }
                // ERROR_PIPE_BUSY: 앞 요청을 처리하는 중. 잠깐 뒤 다시
                Err(e) if e.raw_os_error() == Some(231) => {
                    std::thread::sleep(Duration::from_millis(50))
                }
                Err(_) => return false, // 파이프 없음 = Foldy 꺼짐
            }
        }
        let Some(mut pipe) = pipe else { return false };

        let mut pid = [0u8; 4];
        if pipe.read_exact(&mut pid).is_err() {
            return false;
        }
        let pid = u32::from_le_bytes(pid);
        if pid > 0 {
            // SAFETY: Foldy 프로세스가 찾기 창을 앞으로 가져올 수 있게 한다.
            unsafe { AllowSetForegroundWindow(pid) };
        }
        if pipe
            .write_all(&encode_args(&[QUICK_SEARCH_ARG]))
            .and_then(|_| pipe.flush())
            .is_err()
        {
            return false;
        }
        let mut ack = [0u8; 1];
        pipe.read_exact(&mut ack).is_ok() && ack[0] == 1
    }

    fn pipe_path() -> Option<String> {
        session_and_user().map(|(session, user)| activate_pipe_path(session, &user))
    }

    fn session_and_user() -> Option<(u32, String)> {
        let mut session = 0u32;
        // SAFETY: 이 프로세스의 세션 번호를 묻는다.
        if unsafe { ProcessIdToSessionId(GetCurrentProcessId(), &mut session) } == 0 {
            return None;
        }
        let mut buf = [0u16; 257];
        let mut len = buf.len() as u32;
        // SAFETY: buf는 len개 UTF-16 문자를 담는다. 성공하면 len은 끝의 0까지 센 길이.
        if unsafe { GetUserNameW(buf.as_mut_ptr(), &mut len) } == 0 || len == 0 {
            return None;
        }
        let user = String::from_utf16_lossy(&buf[..(len as usize - 1)]);
        Some((session, user))
    }

    fn launch_foldy(arg: &str) {
        let Some(exe) = std::env::current_exe()
            .ok()
            .and_then(|p| p.parent().map(|d| d.join("Foldy.exe")))
        else {
            return;
        };
        let _ = std::process::Command::new(exe).arg(arg).spawn();
    }
}
