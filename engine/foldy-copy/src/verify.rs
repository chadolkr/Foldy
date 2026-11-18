//! 검증기: 크기 + 수정 시간, 선택적으로 내용 CRC32. 이동은 여기를 통과한 파일만 원본을 지운다.

use std::fs::File;
use std::io::Read;
use std::path::Path;
use std::time::{Duration, SystemTime};

use crate::journal::Crc32;
use crate::spec::VerifyMode;
use crate::win;

/// FAT/exFAT 대상의 2초 단위 시간을 허용한다.
const MTIME_TOLERANCE: Duration = Duration::from_secs(2);

pub fn times_close(a: SystemTime, b: SystemTime) -> bool {
    let d = a.duration_since(b).or_else(|_| b.duration_since(a)).unwrap_or_default();
    d <= MTIME_TOLERANCE
}

/// FILETIME(100ns) 두 값이 허용 오차 안인지.
fn filetimes_close(a: u64, b: u64) -> bool {
    a.abs_diff(b) <= MTIME_TOLERANCE.as_nanos() as u64 / 100
}

/// 대상이 (크기, 수정 시간) 기준으로 같은 파일인지 (이어하기에서 건너뛰기 판단).
pub fn matches(size: u64, mtime: SystemTime, dst: &Path) -> bool {
    win::file_attr(dst).is_some_and(|a| !a.is_dir && a.size == size && filetimes_close(a.mtime, win::filetime_of(mtime)))
}

pub fn file_crc(p: &Path) -> std::io::Result<u32> {
    let mut f = File::open(p)?;
    let mut buf = vec![0u8; 1 << 20];
    let mut c = Crc32::default();
    loop {
        let n = f.read(&mut buf)?;
        if n == 0 {
            return Ok(c.finish());
        }
        c.update(&buf[..n]);
    }
}

pub fn verify(src: &Path, dst: &Path, mode: VerifyMode) -> Result<(), String> {
    verify_expected(src, dst, mode, None)
}

/// `expected`(스캔 때 본 원본 크기·FILETIME)와 대상이 같으면 원본을 다시 조회하지 않는다.
/// 다르면 원본이 그 사이 바뀌었을 수 있으니 원본을 다시 보고 비교한다.
pub fn verify_expected(src: &Path, dst: &Path, mode: VerifyMode, expected: Option<(u64, u64)>) -> Result<(), String> {
    if mode == VerifyMode::None {
        return Ok(());
    }
    // 핸들을 열지 않는 속성 조회 (작은 파일 10만 개에서도 싸게)
    let b = win::file_attr(dst).ok_or("대상을 확인하지 못했습니다")?;
    let a = match expected {
        Some((size, mtime)) if size == b.size && filetimes_close(mtime, b.mtime) => {
            win::Attr { size, mtime, is_dir: false }
        }
        _ => win::file_attr(src).ok_or("원본을 확인하지 못했습니다")?,
    };
    if a.size != b.size {
        return Err(format!("검증 실패: 크기가 다릅니다 ({} ≠ {})", a.size, b.size));
    }
    if !filetimes_close(a.mtime, b.mtime) {
        return Err("검증 실패: 수정 시간이 다릅니다".into());
    }
    if mode == VerifyMode::Hash {
        let x = file_crc(src).map_err(|e| format!("원본 읽기 실패: {e}"))?;
        let y = file_crc(dst).map_err(|e| format!("대상 읽기 실패: {e}"))?;
        if x != y {
            return Err(format!("검증 실패: 내용 해시가 다릅니다 ({x:08x} ≠ {y:08x})"));
        }
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn detects_size_time_and_content_differences() {
        let d = std::env::temp_dir().join(format!("foldy-verify-{}", std::process::id()));
        std::fs::create_dir_all(&d).unwrap();
        let a = d.join("a");
        let b = d.join("b");
        std::fs::write(&a, b"hello").unwrap();
        std::fs::write(&b, b"hellp").unwrap();
        let t = std::fs::metadata(&a).unwrap().modified().unwrap();
        File::options().write(true).open(&b).unwrap().set_modified(t).unwrap();
        assert!(verify(&a, &b, VerifyMode::SizeTime).is_ok(), "크기·시간만 보면 같다");
        assert!(verify(&a, &b, VerifyMode::Hash).unwrap_err().contains("해시"));
        assert!(matches(5, t, &b));
        assert!(!matches(6, t, &b));

        std::fs::write(&b, b"hello!").unwrap();
        assert!(verify(&a, &b, VerifyMode::SizeTime).unwrap_err().contains("크기"));
        assert!(verify(&a, &b, VerifyMode::None).is_ok());
        std::fs::write(&b, b"hello").unwrap();
        File::options().write(true).open(&b).unwrap().set_modified(t - Duration::from_secs(60)).unwrap();
        assert!(verify(&a, &b, VerifyMode::SizeTime).unwrap_err().contains("시간"));
        let _ = std::fs::remove_dir_all(d);
    }
}
