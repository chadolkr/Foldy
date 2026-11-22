//! Win32 호출 모음: CopyFile2(진행률 콜백·일시정지·취소), MoveFileExW, 볼륨·드라이브 종류 판별, 폴더 시간 복원.
//! 경로는 모두 `\\?\` 접두사를 붙여 260자 제한을 피한다.

use std::ffi::c_void;
use std::fs::{FileTimes, OpenOptions};
use std::os::windows::ffi::OsStrExt;
use std::os::windows::fs::{FileTimesExt, OpenOptionsExt};
use std::os::windows::io::AsRawHandle;
use std::panic::{AssertUnwindSafe, catch_unwind};
use std::path::Path;
use std::sync::atomic::{AtomicBool, AtomicU64, Ordering::*};
use std::time::Duration;

use windows_sys::Win32::Foundation::{GetLastError, HANDLE};
use windows_sys::Win32::Storage::FileSystem::{
    COPYFILE2_CALLBACK_CHUNK_FINISHED, COPYFILE2_EXTENDED_PARAMETERS, COPYFILE2_MESSAGE, COPYFILE2_MESSAGE_ACTION,
    COPYFILE2_PROGRESS_CANCEL, COPYFILE2_PROGRESS_CONTINUE, CopyFile2, FILE_ATTRIBUTE_DIRECTORY, FILE_FLAG_BACKUP_SEMANTICS, FILE_SHARE_READ, FILE_SHARE_WRITE,
    FILE_WRITE_ATTRIBUTES, GetDiskFreeSpaceExW, GetDriveTypeW, GetFileAttributesExW, GetFileExInfoStandard, GetVolumeInformationW,
    GetVolumePathNameW,
    MOVEFILE_REPLACE_EXISTING, MoveFileExW, WIN32_FILE_ATTRIBUTE_DATA,
};
use windows_sys::Win32::System::IO::DeviceIoControl;
use windows_sys::Win32::System::Ioctl::{
    DEVICE_SEEK_PENALTY_DESCRIPTOR, IOCTL_STORAGE_QUERY_PROPERTY, PropertyStandardQuery, STORAGE_PROPERTY_QUERY,
    StorageDeviceSeekPenaltyProperty,
};

pub use windows_sys::Win32::Foundation::ERROR_REQUEST_ABORTED;
pub use windows_sys::Win32::Storage::FileSystem::{COPY_FILE_FAIL_IF_EXISTS, COPY_FILE_NO_BUFFERING};

use crate::tuner::DriveKind;

const DRIVE_REMOTE: u32 = 4;
const BS: u16 = b'\\' as u16;

/// 절대 경로 + `\\?\` (UNC는 `\\?\UNC\`) 접두사 + NUL. 슬래시는 역슬래시로 바꾼다.
pub fn long_path_wide(p: &Path) -> Vec<u16> {
    let abs = std::path::absolute(p).unwrap_or_else(|_| p.to_path_buf());
    let s: Vec<u16> = abs.as_os_str().encode_wide().map(|c| if c == b'/' as u16 { BS } else { c }).collect();
    let mut out = Vec::with_capacity(s.len() + 9);
    if s.starts_with(&[BS, BS, b'?' as u16, BS]) || s.starts_with(&[BS, BS, b'.' as u16, BS]) {
        out.extend_from_slice(&s);
    } else if s.starts_with(&[BS, BS]) {
        out.extend(r"\\?\UNC\".encode_utf16());
        out.extend_from_slice(&s[2..]);
    } else {
        out.extend(r"\\?\".encode_utf16());
        out.extend_from_slice(&s);
    }
    out.push(0);
    out
}

/// CopyFile2 진행률 콜백에 넘기는 문맥.
pub struct CopyCtx<'a> {
    pub stop: &'a (dyn Fn() -> bool + Sync),
    pub paused: &'a AtomicBool,
    /// 작업 전체의 완료 바이트 (콜백에서 바로 더함)
    pub bytes_done: &'a AtomicU64,
    /// 이 파일에서 지금까지 옮긴 바이트 (실패하면 bytes_done에서 되돌림)
    pub transferred: AtomicU64,
}

unsafe extern "system" fn progress_cb(msg: *const COPYFILE2_MESSAGE, data: *const c_void) -> COPYFILE2_MESSAGE_ACTION {
    // extern 함수에서 panic이 새면 프로세스가 중단되므로 여기서 막고 복사를 취소한다.
    catch_unwind(AssertUnwindSafe(|| {
        // SAFETY: data는 copy_file이 넘긴 &CopyCtx이며, CopyFile2가 반환할 때까지(콜백이 불리는 동안) 살아 있다.
        let ctx = unsafe { &*(data as *const CopyCtx) };
        // SAFETY: msg는 CopyFile2가 콜백 동안 유효하게 넘기는 메시지다. Type이 CHUNK_FINISHED일 때만 그 공용체 칸을 읽는다.
        let m = unsafe { &*msg };
        if m.Type == COPYFILE2_CALLBACK_CHUNK_FINISHED {
            // SAFETY: 위에서 Type을 확인했다.
            let t = unsafe { m.Info.ChunkFinished.uliTotalBytesTransferred };
            let prev = ctx.transferred.swap(t, AcqRel);
            if t > prev {
                ctx.bytes_done.fetch_add(t - prev, AcqRel);
            }
        }
        // 일시정지: 콜백 안에서 기다린다 (설계서). 다음 덩어리를 읽지 않는다.
        while ctx.paused.load(Acquire) && !(ctx.stop)() {
            std::thread::sleep(Duration::from_millis(20));
        }
        if (ctx.stop)() { COPYFILE2_PROGRESS_CANCEL } else { COPYFILE2_PROGRESS_CONTINUE }
    }))
    .unwrap_or(COPYFILE2_PROGRESS_CANCEL)
}

/// CopyFile2 (CopyFileEx와 같은 복사 엔진, 덩어리마다 진행률 콜백). 실패하면 Win32 오류 코드,
/// 취소되면 ERROR_REQUEST_ABORTED (미완성 대상은 API가 지운다).
///
/// CopyFileExW가 아니라 CopyFile2를 쓰는 이유: 이 개발 환경(Windows 11 빌드 29671)에서 longPathAware
/// 프로세스(.NET 앱 호스트 = Foldy.exe)가 260자를 넘는 경로로 CopyFileExW를 부르면 프로세스가
/// 0xC0000417로 바로 종료된다. CopyFile2는 같은 경로에서 정상 동작한다 (docs/copy-engine.md).
pub fn copy_file(src: &Path, dst: &Path, flags: u32, ctx: &CopyCtx) -> Result<(), u32> {
    let s = long_path_wide(src);
    let d = long_path_wide(dst);
    let params = COPYFILE2_EXTENDED_PARAMETERS {
        dwSize: size_of::<COPYFILE2_EXTENDED_PARAMETERS>() as u32,
        dwCopyFlags: flags,
        pfCancel: std::ptr::null_mut(),
        pProgressRoutine: Some(progress_cb),
        pvCallbackContext: ctx as *const CopyCtx as *mut c_void,
    };
    // SAFETY: s, d는 NUL로 끝나는 UTF-16 버퍼이고 호출 동안 살아 있다. params와 ctx는 호출 동안 유효하며
    // 콜백에서만 &CopyCtx로 읽는다.
    let hr = unsafe { CopyFile2(s.as_ptr(), d.as_ptr(), &params) };
    if hr >= 0 { Ok(()) } else { Err(hresult_to_win32(hr)) }
}

/// HRESULT_FROM_WIN32의 역. 그 형식이 아니면 HRESULT 값 그대로.
pub fn hresult_to_win32(hr: i32) -> u32 {
    let u = hr as u32;
    if u & 0xFFFF_0000 == 0x8007_0000 { u & 0xFFFF } else { u }
}

/// 같은 볼륨 안의 이동 (경로만 바꿈).
pub fn move_file(src: &Path, dst: &Path, replace: bool) -> Result<(), u32> {
    let s = long_path_wide(src);
    let d = long_path_wide(dst);
    let flags = if replace { MOVEFILE_REPLACE_EXISTING } else { 0 };
    // SAFETY: s, d는 NUL로 끝나는 UTF-16 버퍼이고 호출 동안 살아 있다.
    let ok = unsafe { MoveFileExW(s.as_ptr(), d.as_ptr(), flags) };
    if ok != 0 { Ok(()) } else { Err(last_error()) }
}

fn last_error() -> u32 {
    // SAFETY: 인자 없는 스레드 지역 값 조회.
    unsafe { GetLastError() }
}

/// 경로가 속한 볼륨 루트 ("C:\", "\\server\share\", 마운트 폴더). 실패하면 None.
pub fn volume_root(p: &Path) -> Option<String> {
    let w = long_path_wide(p);
    let mut buf = vec![0u16; 1024];
    // SAFETY: w는 NUL로 끝나고, buf는 buf.len()개 u16을 쓸 수 있다.
    let ok = unsafe { GetVolumePathNameW(w.as_ptr(), buf.as_mut_ptr(), buf.len() as u32) };
    if ok == 0 {
        return None;
    }
    let n = buf.iter().position(|&c| c == 0).unwrap_or(buf.len());
    let s = String::from_utf16_lossy(&buf[..n]);
    Some(if let Some(rest) = s.strip_prefix(r"\\?\UNC\") {
        format!(r"\\{rest}")
    } else if let Some(rest) = s.strip_prefix(r"\\?\") {
        // 드라이브 문자면 접두사를 떼고, Volume{GUID}는 그대로 둔다
        if rest.len() >= 2 && rest.as_bytes()[1] == b':' { rest.to_owned() } else { s.clone() }
    } else {
        s
    })
}

/// 그 경로가 있는 볼륨에서 이 사용자가 쓸 수 있는 빈 공간과 파일 시스템 이름 ("NTFS", "FAT32", "exFAT" …).
/// 알 수 없으면 None (경로가 아직 없어도 드라이브가 있으면 안다).
pub fn volume_room(p: &Path) -> (Option<u64>, Option<String>) {
    let Some(root) = volume_root(p) else { return (None, None) };
    let mut w: Vec<u16> = root.encode_utf16().collect();
    if w.last() != Some(&BS) {
        w.push(BS);
    }
    w.push(0);
    let mut free = 0u64;
    // SAFETY: w는 NUL로 끝나는 UTF-16, free는 쓸 수 있는 u64, 나머지 출력은 null(받지 않음)이다.
    let ok = unsafe { GetDiskFreeSpaceExW(w.as_ptr(), &mut free, std::ptr::null_mut(), std::ptr::null_mut()) };
    let mut fs = [0u16; 64];
    // SAFETY: w는 NUL로 끝나고, fs는 fs.len()개 u16을 쓸 수 있으며, 받지 않는 출력은 null이다.
    let ok_fs = unsafe {
        GetVolumeInformationW(
            w.as_ptr(),
            std::ptr::null_mut(),
            0,
            std::ptr::null_mut(),
            std::ptr::null_mut(),
            std::ptr::null_mut(),
            fs.as_mut_ptr(),
            fs.len() as u32,
        )
    };
    let name = (ok_fs != 0).then(|| {
        let n = fs.iter().position(|&c| c == 0).unwrap_or(fs.len());
        String::from_utf16_lossy(&fs[..n])
    });
    ((ok != 0).then_some(free), name)
}

pub fn same_volume(a: &Path, b: &Path) -> bool {
    match (volume_root(a), volume_root(b)) {
        (Some(x), Some(y)) => x.eq_ignore_ascii_case(&y),
        _ => false,
    }
}

/// SSD/HDD/네트워크 판별 (IOCTL_STORAGE_QUERY_PROPERTY의 IncursSeekPenalty).
pub fn drive_kind(p: &Path) -> (String, DriveKind) {
    let Some(root) = volume_root(p) else { return (String::new(), DriveKind::Unknown) };
    if root.starts_with(r"\\") && !root.starts_with(r"\\?\") {
        return (root, DriveKind::Network);
    }
    let w: Vec<u16> = root.encode_utf16().chain([0]).collect();
    // SAFETY: w는 NUL로 끝나는 UTF-16.
    if unsafe { GetDriveTypeW(w.as_ptr()) } == DRIVE_REMOTE {
        return (root, DriveKind::Network);
    }
    let letter = root.trim_end_matches('\\');
    if letter.len() != 2 {
        return (root, DriveKind::Unknown);
    }
    let kind = seek_penalty(letter).map_or(DriveKind::Unknown, |hdd| if hdd { DriveKind::Hdd } else { DriveKind::Ssd });
    (root, kind)
}

fn seek_penalty(letter: &str) -> Option<bool> {
    // 접근 권한 0으로 열면 관리자 권한 없이도 장치 속성을 물을 수 있다.
    let dev = OpenOptions::new()
        .access_mode(0)
        .share_mode(FILE_SHARE_READ | FILE_SHARE_WRITE)
        .open(format!(r"\\.\{letter}"))
        .ok()?;
    let query = STORAGE_PROPERTY_QUERY {
        PropertyId: StorageDeviceSeekPenaltyProperty,
        QueryType: PropertyStandardQuery,
        AdditionalParameters: [0],
    };
    let mut out = DEVICE_SEEK_PENALTY_DESCRIPTOR::default();
    let mut ret = 0u32;
    // SAFETY: dev는 열린 장치 핸들이고, 입력·출력 버퍼는 지역 변수로 지정한 크기만큼 유효하다. 동기 호출이라 overlapped는 null.
    let ok = unsafe {
        DeviceIoControl(
            dev.as_raw_handle() as HANDLE,
            IOCTL_STORAGE_QUERY_PROPERTY,
            &query as *const _ as *const c_void,
            size_of::<STORAGE_PROPERTY_QUERY>() as u32,
            &mut out as *mut _ as *mut c_void,
            size_of::<DEVICE_SEEK_PENALTY_DESCRIPTOR>() as u32,
            &mut ret,
            std::ptr::null_mut(),
        )
    };
    (ok != 0 && ret as usize >= size_of::<DEVICE_SEEK_PENALTY_DESCRIPTOR>()).then_some(out.IncursSeekPenalty)
}

/// 원본 폴더의 만든·수정·접근 시간을 대상 폴더에 복사한다 (작업 마지막 단계).
pub fn copy_dir_times(src: &Path, dst: &Path) -> std::io::Result<()> {
    let m = std::fs::metadata(src)?;
    let f = OpenOptions::new().access_mode(FILE_WRITE_ATTRIBUTES).custom_flags(FILE_FLAG_BACKUP_SEMANTICS).open(dst)?;
    let mut t = FileTimes::new().set_modified(m.modified()?).set_accessed(m.accessed()?);
    if let Ok(c) = m.created() {
        t = t.set_created(c);
    }
    f.set_times(t)
}

/// 읽기 전용 속성을 지운다 (덮어쓰기·원본 삭제 전).
pub fn clear_readonly(p: &Path) {
    if let Ok(m) = std::fs::symlink_metadata(p) {
        let mut perm = m.permissions();
        if perm.readonly() {
            #[allow(clippy::permissions_set_readonly_false)]
            perm.set_readonly(false);
            let _ = std::fs::set_permissions(p, perm);
        }
    }
}

/// 읽기 전용이어도 지운다.
pub fn remove_file_force(p: &Path) -> std::io::Result<()> {
    match std::fs::remove_file(p) {
        Ok(()) => Ok(()),
        Err(e) if e.kind() == std::io::ErrorKind::PermissionDenied => {
            clear_readonly(p);
            std::fs::remove_file(p)
        }
        Err(e) => Err(e),
    }
}

/// 파일 크기·수정 시간 (FILETIME, 100ns 단위)·폴더 여부. 핸들을 열지 않는 GetFileAttributesExW라
/// 작은 파일마다 하는 검증이 싸다 (std::fs::metadata는 파일을 연다).
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct Attr {
    pub size: u64,
    pub mtime: u64,
    pub is_dir: bool,
}

pub fn file_attr(p: &Path) -> Option<Attr> {
    let w = long_path_wide(p);
    let mut d = WIN32_FILE_ATTRIBUTE_DATA::default();
    // SAFETY: w는 NUL로 끝나고, d는 GetFileExInfoStandard가 쓰는 WIN32_FILE_ATTRIBUTE_DATA 크기의 지역 변수다.
    let ok = unsafe { GetFileAttributesExW(w.as_ptr(), GetFileExInfoStandard, &mut d as *mut _ as *mut c_void) };
    (ok != 0).then(|| Attr {
        size: (u64::from(d.nFileSizeHigh) << 32) | u64::from(d.nFileSizeLow),
        mtime: (u64::from(d.ftLastWriteTime.dwHighDateTime) << 32) | u64::from(d.ftLastWriteTime.dwLowDateTime),
        is_dir: d.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY != 0,
    })
}

/// 1601-01-01부터의 100ns 단위 (FILETIME)로 바꾼다.
pub fn filetime_of(t: std::time::SystemTime) -> u64 {
    const UNIX_TO_1601: u64 = 116_444_736_000_000_000;
    match t.duration_since(std::time::UNIX_EPOCH) {
        Ok(d) => UNIX_TO_1601 + (d.as_nanos() / 100) as u64,
        Err(e) => UNIX_TO_1601.saturating_sub((e.duration().as_nanos() / 100) as u64),
    }
}

/// Win32 오류 코드를 사람이 읽을 문장으로 (시스템 언어).
pub fn error_text(code: u32) -> String {
    format!("{} (Win32 {code})", std::io::Error::from_raw_os_error(code as i32))
}

#[cfg(test)]
mod tests {
    use super::*;

    fn s(w: &[u16]) -> String {
        String::from_utf16_lossy(&w[..w.len() - 1])
    }

    #[test]
    fn long_path_prefixes() {
        assert_eq!(s(&long_path_wide(Path::new("C:\\a/b"))), r"\\?\C:\a\b");
        assert_eq!(s(&long_path_wide(Path::new(r"\\srv\share\x"))), r"\\?\UNC\srv\share\x");
        assert_eq!(s(&long_path_wide(Path::new(r"\\?\C:\x"))), r"\\?\C:\x");
        assert_eq!(*long_path_wide(Path::new("C:\\")).last().unwrap(), 0);
    }

    #[test]
    fn hresult_mapping() {
        assert_eq!(hresult_to_win32(0x8007_04D3_u32 as i32), ERROR_REQUEST_ABORTED);
        assert_eq!(hresult_to_win32(0x8000_4005_u32 as i32), 0x8000_4005);
    }

    #[test]
    fn temp_volume_is_known() {
        let t = std::env::temp_dir();
        let root = volume_root(&t).unwrap();
        assert!(root.ends_with('\\'), "{root}");
        assert!(same_volume(&t, &t.join("없는 폴더").join("x")));
        let (_, kind) = drive_kind(&t);
        assert_ne!(kind, DriveKind::Network);
    }

    #[test]
    fn attr_matches_std_metadata() {
        let p = std::env::temp_dir().join(format!("foldy-attr-{}.bin", std::process::id()));
        std::fs::write(&p, [1u8; 1234]).unwrap();
        let a = file_attr(&p).unwrap();
        let m = std::fs::metadata(&p).unwrap();
        assert_eq!(a.size, 1234);
        assert!(!a.is_dir);
        assert_eq!(a.mtime, filetime_of(m.modified().unwrap()));
        assert!(file_attr(&std::env::temp_dir()).unwrap().is_dir);
        assert!(file_attr(&p.with_extension("없음")).is_none());
        assert_eq!(filetime_of(std::time::UNIX_EPOCH), 116_444_736_000_000_000);
        let _ = std::fs::remove_file(p);
    }
}
