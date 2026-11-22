//! C ABI. 이 파일의 시그니처에서 C# 바인딩(NativeMethods.g.cs)이 생성된다.
//!
//! ABI v2 (M3): FcProgress에 속도·스레드·실패·건너뜀·확인 대기·단계를 더했고,
//! 현재 파일 이름·문제 목록·충돌 해결·저널 읽기 함수를 추가했다. v1의 함수 시그니처는 그대로다.

use std::panic::{AssertUnwindSafe, catch_unwind};

use crate::job;

pub const FC_OK: i32 = 0;
pub const FC_ERR_INVALID_ARG: i32 = -1;
pub const FC_ERR_INVALID_HANDLE: i32 = -2;
pub const FC_ERR_PANIC: i32 = -3;
pub const FC_ERR_INVALID_STATE: i32 = -4;
pub const FC_ERR_IO: i32 = -5;
/// 호출자 버퍼가 작다. `out_len`에 필요한 u16 개수가 들어 있다.
pub const FC_ERR_BUFFER_TOO_SMALL: i32 = -6;
/// 해결할 충돌 항목이 없다 (이미 해결했거나 id가 틀림).
pub const FC_ERR_NOT_FOUND: i32 = -7;
/// 시험용 크래시 흉내로 멈춤 (_test_abort_after_files).
pub const FC_ERR_ABORTED: i32 = -8;
/// 대상 드라이브의 빈 공간이 모자라 시작하지 않음 (아무것도 쓰지 않았다). `bytes_total`에 필요한 바이트가 들어 있다.
pub const FC_ERR_NO_SPACE: i32 = -9;
/// 대상이 FAT32 드라이브라 4GB가 넘는 파일을 담을 수 없어 시작하지 않음. `bytes_total`에 그 파일 크기가 들어 있다.
pub const FC_ERR_FILE_TOO_BIG: i32 = -10;

/// 진행 상황 스냅샷. C#의 바인딩 구조체와 배치가 같아야 한다 (80바이트).
#[repr(C)]
#[derive(Debug, Default, Clone, Copy)]
pub struct FcProgress {
    /// JobState 값 (0 Created, 1 Running, 2 Paused, 3 Done, 4 Failed, 5 Canceled, 6 NeedsAttention)
    pub state: u32,
    /// 마지막 오류 코드 (FC_OK 또는 FC_ERR_*). Done인데 FC_ERR_IO면 일부 파일이 실패했다.
    pub last_error: i32,
    /// 끝난 파일 (건너뛴 파일 포함, 실패 제외)
    pub files_done: u64,
    pub files_total: u64,
    pub bytes_done: u64,
    pub bytes_total: u64,
    /// 최근 처리 속도 (바이트/초, 0.5초 단위 지수 평균)
    pub bytes_per_sec: u64,
    pub files_failed: u64,
    pub files_skipped: u64,
    /// 사용자 확인을 기다리는 충돌 수 (0보다 크면 state가 NeedsAttention)
    pub issues_pending: u32,
    /// 지금 복사 중인 워커 수
    pub threads_active: u32,
    /// 현재 동시 실행 상한 (자동 조절 값)
    pub thread_limit: u32,
    /// 0 대기, 1 스캔, 2 계획, 3 복사, 4 마무리, 5 끝
    pub phase: u32,
}

fn guard(f: impl FnOnce() -> i32) -> i32 {
    catch_unwind(AssertUnwindSafe(f)).unwrap_or(FC_ERR_PANIC)
}

/// 문자열을 호출자 버퍼에 UTF-16으로 쓴다 (NUL 없음). 길이는 항상 `out_len`에 쓴다.
///
/// # Safety
/// `buf`는 null이거나 `cap`개의 u16을 쓸 수 있어야 하고, `out_len`은 쓰기 가능해야 한다.
unsafe fn write_utf16(s: &str, buf: *mut u16, cap: usize, out_len: *mut usize) -> i32 {
    if out_len.is_null() {
        return FC_ERR_INVALID_ARG;
    }
    let w: Vec<u16> = s.encode_utf16().collect();
    // SAFETY: null 검사를 했고, 호출자가 쓰기 가능한 포인터를 넘긴다 (함수 계약).
    unsafe { *out_len = w.len() };
    if w.is_empty() {
        return FC_OK;
    }
    if buf.is_null() || cap < w.len() {
        return FC_ERR_BUFFER_TOO_SMALL;
    }
    // SAFETY: buf는 cap ≥ w.len()개의 u16을 쓸 수 있고 w와 겹치지 않는다.
    unsafe { std::ptr::copy_nonoverlapping(w.as_ptr(), buf, w.len()) };
    FC_OK
}

/// 엔진 ABI 버전. C#은 로드 직후 이 값을 확인하고 다르면 IFileOperation으로 폴백한다.
#[unsafe(no_mangle)]
pub extern "C" fn fc_abi_version() -> u32 {
    2
}

/// 작업을 만든다. `spec_json`은 UTF-16 JSON. 필드는 docs/copy-engine.md 참고:
/// {"sources":["C:\\a"],"dest_dir":"D:\\out","mode":"copy|move","conflict":"skip|overwrite|rename|ask",
///  "verify":"none|size_time|hash","threads":0,"journal_path":"...","resume":false,"tuning_path":"..."}
///
/// # Safety
/// `spec_json`은 `spec_len`개의 u16을 읽을 수 있어야 하고, `out_handle`은 쓰기 가능해야 한다.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn fc_job_create(spec_json: *const u16, spec_len: usize, out_handle: *mut u64) -> i32 {
    guard(|| {
        if spec_json.is_null() || out_handle.is_null() || spec_len > 64 * 1024 * 1024 {
            return FC_ERR_INVALID_ARG;
        }
        // SAFETY: 호출자가 spec_len개의 u16이 유효함을 보장한다 (함수 계약).
        let units = unsafe { std::slice::from_raw_parts(spec_json, spec_len) };
        let Ok(text) = String::from_utf16(units) else { return FC_ERR_INVALID_ARG };
        match job::create(&text) {
            Ok(h) => {
                // SAFETY: null 검사를 했고, 호출자가 쓰기 가능한 포인터를 넘긴다.
                unsafe { *out_handle = h };
                FC_OK
            }
            Err(e) => e,
        }
    })
}

#[unsafe(no_mangle)]
pub extern "C" fn fc_job_start(handle: u64) -> i32 {
    guard(|| job::with(handle, job::start))
}

#[unsafe(no_mangle)]
pub extern "C" fn fc_job_pause(handle: u64) -> i32 {
    guard(|| job::with(handle, |j| j.set_paused(true)))
}

#[unsafe(no_mangle)]
pub extern "C" fn fc_job_resume(handle: u64) -> i32 {
    guard(|| job::with(handle, |j| j.set_paused(false)))
}

#[unsafe(no_mangle)]
pub extern "C" fn fc_job_cancel(handle: u64) -> i32 {
    guard(|| job::with(handle, |j| j.cancel()))
}

/// # Safety
/// `out`은 쓰기 가능한 `FcProgress`를 가리켜야 한다.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn fc_job_poll(handle: u64, out: *mut FcProgress) -> i32 {
    guard(|| {
        if out.is_null() {
            return FC_ERR_INVALID_ARG;
        }
        job::with(handle, |j| {
            // SAFETY: null 검사를 했고, 호출자가 쓰기 가능한 포인터를 넘긴다.
            unsafe { *out = j.snapshot() };
            FC_OK
        })
    })
}

/// 마지막으로 시작한 파일의 원본 전체 경로를 UTF-16으로 `buf`에 쓴다 (NUL 없음, 길이는 `out_len`).
/// 버퍼가 작으면 FC_ERR_BUFFER_TOO_SMALL과 함께 필요한 길이를 돌려준다.
///
/// # Safety
/// `buf`는 null이거나 `cap`개의 u16을 쓸 수 있어야 하고, `out_len`은 쓰기 가능해야 한다.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn fc_job_current_name(handle: u64, buf: *mut u16, cap: usize, out_len: *mut usize) -> i32 {
    guard(|| {
        job::with(handle, |j| {
            let name = j.current_name();
            // SAFETY: 포인터 계약은 이 함수의 계약과 같다.
            unsafe { write_utf16(&name, buf, cap, out_len) }
        })
    })
}

/// 문제 목록(충돌·실패)을 UTF-16 JSON 배열로 쓴다:
/// [{"id":1,"kind":"conflict|failed","source":"..","dest":"..","message":"..","resolved":false}]
///
/// # Safety
/// `buf`는 null이거나 `cap`개의 u16을 쓸 수 있어야 하고, `out_len`은 쓰기 가능해야 한다.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn fc_job_issues(handle: u64, buf: *mut u16, cap: usize, out_len: *mut usize) -> i32 {
    guard(|| {
        job::with(handle, |j| {
            let json = j.issues_json();
            // SAFETY: 포인터 계약은 이 함수의 계약과 같다.
            unsafe { write_utf16(&json, buf, cap, out_len) }
        })
    })
}

/// 충돌 해결. `action`: 0 건너뛰기, 1 덮어쓰기, 2 이름 바꾸기("이름 (2)"). `apply_all`이 0이 아니면
/// 남은 충돌 전체에 적용한다 (`issue_id`는 무시).
#[unsafe(no_mangle)]
pub extern "C" fn fc_job_resolve(handle: u64, issue_id: u64, action: u32, apply_all: u32) -> i32 {
    guard(|| job::with(handle, |j| j.resolve(issue_id, action, apply_all != 0)))
}

/// 저널 파일의 작업 명세(JSON)를 읽는다. 크래시 뒤 C#이 "resume":true를 더해 fc_job_create로 이어 간다.
///
/// # Safety
/// `path`는 `path_len`개의 u16을 읽을 수 있어야 하고, `buf`/`out_len`은 fc_job_current_name과 같다.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn fc_journal_read_spec(
    path: *const u16,
    path_len: usize,
    buf: *mut u16,
    cap: usize,
    out_len: *mut usize,
) -> i32 {
    guard(|| {
        if path.is_null() || path_len == 0 || path_len > 32 * 1024 {
            return FC_ERR_INVALID_ARG;
        }
        // SAFETY: 호출자가 path_len개의 u16이 유효함을 보장한다.
        let units = unsafe { std::slice::from_raw_parts(path, path_len) };
        let Ok(p) = String::from_utf16(units) else { return FC_ERR_INVALID_ARG };
        let Ok(replay) = crate::journal::read(std::path::Path::new(&p)) else { return FC_ERR_IO };
        let st = crate::journal::ResumeState::from_records(&replay.records);
        let Some(spec) = st.spec else { return FC_ERR_NOT_FOUND };
        // SAFETY: 포인터 계약은 이 함수의 계약과 같다.
        unsafe { write_utf16(&spec, buf, cap, out_len) }
    })
}

/// 작업을 해제한다. 실행 중이면 취소하고 기다리지 않는다 (워커는 Arc로 살아 있다가 스스로 끝난다).
#[unsafe(no_mangle)]
pub extern "C" fn fc_job_free(handle: u64) -> i32 {
    guard(|| job::free(handle))
}

/// 자체 시험용: 내부에서 panic을 일으켜 FC_ERR_PANIC이 반환되는지 확인한다.
#[unsafe(no_mangle)]
pub extern "C" fn fc_selftest_panic() -> i32 {
    guard(|| panic!("fc_selftest_panic: intentional panic"))
}
