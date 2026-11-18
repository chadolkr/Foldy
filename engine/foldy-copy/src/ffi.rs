//! C ABI. 이 파일의 시그니처에서 C# 바인딩(NativeMethods.g.cs)이 생성된다.

use std::panic::{AssertUnwindSafe, catch_unwind};

use crate::job;

pub const FC_OK: i32 = 0;
pub const FC_ERR_INVALID_ARG: i32 = -1;
pub const FC_ERR_INVALID_HANDLE: i32 = -2;
pub const FC_ERR_PANIC: i32 = -3;
pub const FC_ERR_INVALID_STATE: i32 = -4;
pub const FC_ERR_IO: i32 = -5;

/// 진행 상황 스냅샷. C#의 바인딩 구조체와 배치가 같아야 한다.
#[repr(C)]
#[derive(Debug, Default, Clone, Copy)]
pub struct FcProgress {
    /// JobState 값 (0 Created, 1 Running, 2 Paused, 3 Done, 4 Failed, 5 Canceled)
    pub state: u32,
    /// 마지막 오류 코드 (FC_OK 또는 FC_ERR_*)
    pub last_error: i32,
    pub files_done: u64,
    pub files_total: u64,
    pub bytes_done: u64,
    pub bytes_total: u64,
}

fn guard(f: impl FnOnce() -> i32) -> i32 {
    catch_unwind(AssertUnwindSafe(f)).unwrap_or(FC_ERR_PANIC)
}

/// 엔진 ABI 버전. C#은 로드 직후 이 값을 확인하고 다르면 IFileOperation으로 폴백한다.
#[unsafe(no_mangle)]
pub extern "C" fn fc_abi_version() -> u32 {
    1
}

/// 작업을 만든다. `spec_json`은 UTF-16 JSON: {"sources":["C:\\a.txt"],"dest_dir":"D:\\out"}
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
