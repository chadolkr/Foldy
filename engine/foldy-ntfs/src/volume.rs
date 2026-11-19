//! Win32 경계: NTFS 볼륨을 읽기 전용으로 열어 $MFT를 순차로 읽고, USN 저널과 개별 MFT 레코드를
//! FSCTL로 조회한다. 볼륨 핸들을 열려면 관리자 권한(또는 SYSTEM)이 필요하다.

use crate::index::{Entry, VolumeIndex};
use crate::record::{self, ATTR_DATA, ATTR_FILE_NAME, BootSector, ParsedRecord};
use crate::usn::{self, UsnRecord};
use rayon::prelude::*;
use std::fs::{File, OpenOptions};
use std::io;
use std::os::windows::fs::{FileExt, OpenOptionsExt};
use std::os::windows::io::AsRawHandle;
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::mpsc;
use windows_sys::Win32::Foundation::HANDLE;
use windows_sys::Win32::System::IO::DeviceIoControl;
use windows_sys::Win32::System::Ioctl::{
    FSCTL_GET_NTFS_FILE_RECORD, FSCTL_QUERY_USN_JOURNAL, FSCTL_READ_USN_JOURNAL, READ_USN_JOURNAL_DATA_V0,
    USN_JOURNAL_DATA_V0,
};

/// 순차 읽기 단위 (설계서: 4~16MB).
const READ_CHUNK: usize = 8 * 1024 * 1024;
const FILE_SHARE_ALL: u32 = 0x1 | 0x2 | 0x4;

/// 저널 오류 코드: 저장한 USN이 저널 범위를 벗어났거나 저널이 바뀜 → 전체 재스캔.
pub const ERROR_JOURNAL_DELETE_IN_PROGRESS: i32 = 1178;
pub const ERROR_JOURNAL_NOT_ACTIVE: i32 = 1179;
pub const ERROR_JOURNAL_ENTRY_DELETED: i32 = 1181;

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct JournalInfo {
    pub id: u64,
    pub first_usn: i64,
    pub next_usn: i64,
}

pub struct Volume {
    file: File,
    /// "C:"
    pub name: String,
    pub boot: BootSector,
}

fn invalid(e: impl std::fmt::Display) -> io::Error {
    io::Error::new(io::ErrorKind::InvalidData, e.to_string())
}

impl Volume {
    /// `\\.\C:`를 읽기 전용으로 연다. 관리자 권한이 없으면 PermissionDenied.
    pub fn open(name: &str) -> io::Result<Self> {
        let name = name.trim_end_matches('\\').to_ascii_uppercase();
        let file = OpenOptions::new().read(true).share_mode(FILE_SHARE_ALL).open(format!(r"\\.\{name}"))?;
        let mut boot = vec![0u8; 4096];
        read_exact_at(&file, 0, &mut boot)?;
        let boot = record::parse_boot_sector(&boot).map_err(invalid)?;
        Ok(Self { file, name, boot })
    }

    fn handle(&self) -> HANDLE {
        self.file.as_raw_handle() as HANDLE
    }

    /// $MFT 레코드 0번을 읽어 (전체 레코드 수, 런리스트)를 얻는다.
    fn mft_layout(&self) -> io::Result<(u64, Vec<record::Run>)> {
        let rs = self.boot.record_size as usize;
        let mut buf = vec![0u8; rs.max(self.boot.bytes_per_sector as usize)];
        read_exact_at(&self.file, self.boot.mft_offset(), &mut buf)?;
        let rec = &mut buf[..rs];
        record::apply_fixup(rec).map_err(invalid)?;
        let (size, runs) = record::mft_data_runs(rec).map_err(invalid)?;
        Ok((size / rs as u64, runs))
    }

    /// $MFT 전체를 순차로 읽어 색인을 만든다. 읽기 스레드와 해석(rayon)을 겹쳐 돌린다.
    /// `progress(처리한 레코드, 전체)`는 조각마다 불린다.
    pub fn build_index(&self, cancel: &AtomicBool, mut progress: impl FnMut(u64, u64)) -> io::Result<VolumeIndex> {
        let (total, runs) = self.mft_layout()?;
        let rs = self.boot.record_size as usize;
        let cluster = self.boot.cluster_size as u64;
        let mut idx = VolumeIndex::new_ntfs(&self.name, total as usize);
        let mut exts: Vec<ParsedRecord> = Vec::new();

        let (tx, rx) = mpsc::sync_channel::<io::Result<(u64, Vec<u8>, usize)>>(2);
        let (back_tx, back_rx) = mpsc::channel::<Vec<u8>>();
        std::thread::scope(|s| -> io::Result<()> {
            s.spawn(move || {
                let mut vcn_bytes: u64 = 0;
                for run in &runs {
                    let run_bytes = run.clusters * cluster;
                    if let Some(lcn) = run.lcn {
                        let mut done = 0u64;
                        while done < run_bytes {
                            let first_rec = (vcn_bytes + done) / rs as u64;
                            if first_rec >= total || cancel.load(Ordering::Relaxed) {
                                return;
                            }
                            let len = (run_bytes - done).min(READ_CHUNK as u64) as usize;
                            let mut buf = back_rx.try_recv().unwrap_or_else(|_| vec![0u8; READ_CHUNK]);
                            let r = read_exact_at(&self.file, lcn * cluster + done, &mut buf[..len]);
                            let failed = r.is_err();
                            if tx.send(r.map(|_| (first_rec, buf, len))).is_err() || failed {
                                return;
                            }
                            done += len as u64;
                        }
                    }
                    vcn_bytes += run_bytes;
                }
            });
            let mut processed = 0u64;
            for msg in rx {
                let (first, mut buf, len) = msg?;
                let parsed: Vec<Option<ParsedRecord>> = buf[..len - len % rs]
                    .par_chunks_mut(rs)
                    .enumerate()
                    .map(|(i, r)| {
                        let no = first + i as u64;
                        if no >= total {
                            return None;
                        }
                        record::apply_fixup(r).ok()?;
                        record::parse_record(r, no).ok().filter(|p| p.in_use)
                    })
                    .collect();
                for p in parsed.into_iter().flatten() {
                    let Ok(no) = u32::try_from(p.record_no) else { continue };
                    if p.base_record != 0 {
                        exts.push(p);
                    } else {
                        idx.insert_build(no, &Entry::from_parsed(&p));
                    }
                }
                processed = (processed + (len / rs) as u64).min(total);
                progress(processed, total);
                let _ = back_tx.send(buf);
            }
            Ok(())
        })?;
        if cancel.load(Ordering::Relaxed) {
            return Err(io::Error::new(io::ErrorKind::Interrupted, "index build cancelled"));
        }
        for p in &exts {
            if let Ok(base) = u32::try_from(p.base_record) {
                idx.merge_extension_build(base, p);
            }
        }
        idx.finalize();
        Ok(idx)
    }

    /// FSCTL_QUERY_USN_JOURNAL
    pub fn query_journal(&self) -> io::Result<JournalInfo> {
        // SAFETY: zero로 초기화한 POD 구조체. 모든 필드가 정수다.
        let mut out: USN_JOURNAL_DATA_V0 = unsafe { std::mem::zeroed() };
        let mut ret = 0u32;
        // SAFETY: 핸들은 self.file이 소유하는 유효한 볼륨 핸들이고, 출력 버퍼는 크기를 정확히 넘긴
        // 지역 구조체다. 동기 호출이므로 OVERLAPPED는 null.
        let ok = unsafe {
            DeviceIoControl(
                self.handle(),
                FSCTL_QUERY_USN_JOURNAL,
                std::ptr::null(),
                0,
                (&raw mut out).cast(),
                size_of::<USN_JOURNAL_DATA_V0>() as u32,
                &mut ret,
                std::ptr::null_mut(),
            )
        };
        if ok == 0 {
            return Err(io::Error::last_os_error());
        }
        Ok(JournalInfo { id: out.UsnJournalID, first_usn: out.FirstUsn, next_usn: out.NextUsn })
    }

    /// FSCTL_READ_USN_JOURNAL: `start`부터 바로 읽을 수 있는 레코드를 읽는다 (기다리지 않음).
    pub fn read_journal(&self, start: i64, journal_id: u64, buf: &mut [u8]) -> io::Result<(i64, Vec<UsnRecord>)> {
        let input = READ_USN_JOURNAL_DATA_V0 {
            StartUsn: start,
            ReasonMask: u32::MAX,
            ReturnOnlyOnClose: 0,
            Timeout: 0,
            BytesToWaitFor: 0,
            UsnJournalID: journal_id,
        };
        let mut ret = 0u32;
        // SAFETY: 입력은 지역 구조체, 출력은 호출자가 넘긴 슬라이스(길이를 그대로 전달). 동기 호출.
        let ok = unsafe {
            DeviceIoControl(
                self.handle(),
                FSCTL_READ_USN_JOURNAL,
                (&raw const input).cast(),
                size_of::<READ_USN_JOURNAL_DATA_V0>() as u32,
                buf.as_mut_ptr().cast(),
                buf.len() as u32,
                &mut ret,
                std::ptr::null_mut(),
            )
        };
        if ok == 0 {
            return Err(io::Error::last_os_error());
        }
        Ok(usn::parse_read_output(&buf[..(ret as usize).min(buf.len())]))
    }

    /// FSCTL_GET_NTFS_FILE_RECORD로 레코드 하나를 읽는다. 지정한 번호가 사용 중이 아니면
    /// NTFS는 더 낮은 사용 중 레코드를 돌려주므로 번호가 다르면 None.
    pub fn read_record(&self, rec: u32) -> io::Result<Option<Vec<u8>>> {
        let rs = self.boot.record_size as usize;
        let input: i64 = rec as i64;
        let mut out = vec![0u64; (12 + rs).div_ceil(8)];
        let mut ret = 0u32;
        // SAFETY: 입력은 NTFS_FILE_RECORD_INPUT_BUFFER와 같은 배치의 i64 하나, 출력은 8바이트 정렬된
        // Vec<u64> 버퍼(바이트 길이를 정확히 전달). 동기 호출.
        let ok = unsafe {
            DeviceIoControl(
                self.handle(),
                FSCTL_GET_NTFS_FILE_RECORD,
                (&raw const input).cast(),
                8,
                out.as_mut_ptr().cast(),
                (out.len() * 8) as u32,
                &mut ret,
                std::ptr::null_mut(),
            )
        };
        if ok == 0 {
            return Err(io::Error::last_os_error());
        }
        let bytes: Vec<u8> = out.iter().flat_map(|w| w.to_le_bytes()).collect();
        let frn = u64::from_le_bytes(bytes[0..8].try_into().unwrap_or_default()) & record::RECORD_MASK;
        let len = u32::from_le_bytes(bytes[8..12].try_into().unwrap_or_default()) as usize;
        if frn != rec as u64 || len == 0 || 12 + len > bytes.len() {
            return Ok(None);
        }
        Ok(Some(bytes[12..12 + len].to_vec()))
    }

    fn parse_fetched(&self, rec: u32) -> Option<ParsedRecord> {
        let mut raw = self.read_record(rec).ok()??;
        // 이 FSCTL이 주는 레코드는 보통 fixup이 이미 적용돼 있다. 적용 전 형식이면 적용한다.
        let mut fixed = raw.clone();
        if record::apply_fixup(&mut fixed).is_ok() {
            raw = fixed;
        }
        record::parse_record(&raw, rec as u64).ok()
    }

    /// 레코드를 다시 읽어 Entry로 만든다. $ATTRIBUTE_LIST가 가리키는 확장 레코드도 합친다.
    pub fn fetch_entry(&self, rec: u32) -> Option<Entry> {
        let p = self.parse_fetched(rec)?;
        if !p.in_use || p.base_record != 0 {
            return None;
        }
        let mut e = Entry::from_parsed(&p);
        let mut seen = vec![rec];
        for a in &p.attr_list {
            let Ok(r) = u32::try_from(a.record) else { continue };
            if (a.type_code == ATTR_FILE_NAME || a.type_code == ATTR_DATA) && !seen.contains(&r) {
                seen.push(r);
                if let Some(x) = self.parse_fetched(r) {
                    e.merge_extension(&x);
                }
            }
        }
        Some(e)
    }
}

fn read_exact_at(f: &File, mut off: u64, mut buf: &mut [u8]) -> io::Result<()> {
    while !buf.is_empty() {
        let n = f.seek_read(buf, off)?;
        if n == 0 {
            return Err(io::Error::new(io::ErrorKind::UnexpectedEof, "volume read past end"));
        }
        off += n as u64;
        buf = &mut buf[n..];
    }
    Ok(())
}

/// 로컬 고정 디스크 중 NTFS 볼륨 ("C:" 형식).
pub fn ntfs_fixed_volumes() -> Vec<String> {
    use windows_sys::Win32::Storage::FileSystem::{GetDriveTypeW, GetLogicalDrives, GetVolumeInformationW};
    const DRIVE_FIXED: u32 = 3;
    // SAFETY: 인자 없이 비트마스크만 반환하는 Win32 함수.
    let mask = unsafe { GetLogicalDrives() };
    let mut out = Vec::new();
    for i in 0..26u8 {
        if mask & (1 << i) == 0 {
            continue;
        }
        let letter = (b'A' + i) as char;
        let root: Vec<u16> = format!("{letter}:\\").encode_utf16().chain([0]).collect();
        // SAFETY: root는 NUL로 끝나는 UTF-16 문자열이고 호출 동안 살아 있다.
        if unsafe { GetDriveTypeW(root.as_ptr()) } != DRIVE_FIXED {
            continue;
        }
        let mut fs = [0u16; 32];
        // SAFETY: 필요 없는 출력은 null, fs 버퍼는 길이를 정확히 넘긴다.
        let ok = unsafe {
            GetVolumeInformationW(
                root.as_ptr(),
                std::ptr::null_mut(),
                0,
                std::ptr::null_mut(),
                std::ptr::null_mut(),
                std::ptr::null_mut(),
                fs.as_mut_ptr(),
                fs.len() as u32,
            )
        };
        if ok != 0 {
            let end = fs.iter().position(|&c| c == 0).unwrap_or(fs.len());
            if String::from_utf16_lossy(&fs[..end]) == "NTFS" {
                out.push(format!("{letter}:"));
            }
        }
    }
    out
}

/// 볼륨의 GUID 이름 `{xxxxxxxx-...}` (스냅샷 파일 이름용). 얻지 못하면 None.
pub fn volume_guid(name: &str) -> Option<String> {
    use windows_sys::Win32::Storage::FileSystem::GetVolumeNameForVolumeMountPointW;
    let root: Vec<u16> = format!("{}\\", name.trim_end_matches('\\')).encode_utf16().chain([0]).collect();
    let mut out = [0u16; 64];
    // SAFETY: root는 NUL로 끝나는 UTF-16, out은 길이를 정확히 넘긴 지역 버퍼.
    if unsafe { GetVolumeNameForVolumeMountPointW(root.as_ptr(), out.as_mut_ptr(), out.len() as u32) } == 0 {
        return None;
    }
    let end = out.iter().position(|&c| c == 0).unwrap_or(out.len());
    let s = String::from_utf16_lossy(&out[..end]);
    let (a, b) = (s.find('{')?, s.find('}')?);
    (a < b).then(|| s[a..=b].to_string())
}
