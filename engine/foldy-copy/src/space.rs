//! 복사 전에 대상 드라이브 공간 확인 (사용자 원칙 2026-11-22 "제한은 미리 알려 시간을 아낀다").
//!
//! 한참 복사하다가 "공간이 부족해요"라고 하지 않도록, 계획이 끝나면 한 바이트도 쓰기 전에
//! (1) 대상 드라이브의 빈 공간과 (2) FAT32 드라이브의 한 파일 한도(4GB - 1바이트)를 본다.
//! 빈 공간을 알 수 없으면(일부 네트워크 위치) 막지 않는다.

/// FAT32 파일 하나의 최대 크기.
pub const FAT32_MAX_FILE: u64 = 0xFFFF_FFFF;
/// 파일이 실제로 차지하는 단위 (NTFS 기본 클러스터). 작은 파일이 많을 때 모자라게 어림하지 않도록 올림한다.
pub const CLUSTER: u64 = 4096;
/// 목록·폴더 기록 등 여유.
pub const SLACK: u64 = 1 << 20;

/// 디스크에서 차지하는 크기 (클러스터 단위 올림).
pub fn on_disk(size: u64) -> u64 {
    size.div_ceil(CLUSTER).saturating_mul(CLUSTER)
}

/// 파일 하나를 쓸 때 더 필요한 공간. 덮어쓰면(<paramref>replaces</paramref>) 지금 파일 자리를 다시 쓰므로 차이만.
pub fn extra(size: u64, replaces: Option<u64>) -> u64 {
    match replaces {
        Some(old) => on_disk(size).saturating_sub(on_disk(old)),
        None => on_disk(size),
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Verdict {
    Ok,
    /// 빈 공간이 모자람: 필요한 바이트(여유 포함), 남은 바이트.
    NoSpace { need: u64, free: u64 },
    /// FAT 계열 드라이브라 이 크기의 파일을 담을 수 없음.
    TooBigForFat { size: u64 },
}

/// FAT, FAT16, FAT32 (exFAT는 큰 파일을 담을 수 있으므로 아니다).
pub fn is_fat(fs: &str) -> bool {
    fs.len() >= 3 && fs[..3].eq_ignore_ascii_case("FAT")
}

/// 판정. <c>extra_total</c>은 [`extra`]의 합, <c>biggest</c>는 쓸 파일 중 가장 큰 것.
pub fn judge(extra_total: u64, biggest: u64, free: Option<u64>, fs: Option<&str>) -> Verdict {
    if fs.is_some_and(is_fat) && biggest > FAT32_MAX_FILE {
        return Verdict::TooBigForFat { size: biggest };
    }
    if extra_total == 0 {
        return Verdict::Ok;
    }
    let need = extra_total.saturating_add(SLACK);
    match free {
        Some(free) if need > free => Verdict::NoSpace { need, free },
        _ => Verdict::Ok,
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn rounds_up_to_clusters() {
        assert_eq!(on_disk(0), 0);
        assert_eq!(on_disk(1), 4096);
        assert_eq!(on_disk(4096), 4096);
        assert_eq!(on_disk(4097), 8192);
    }

    #[test]
    fn overwrite_needs_only_the_difference() {
        assert_eq!(extra(10_000, None), 12_288);
        assert_eq!(extra(10_000, Some(9_000)), 0);
        assert_eq!(extra(10_000, Some(100)), 8_192);
    }

    #[test]
    fn judges_space_and_fat32() {
        assert_eq!(judge(0, 0, Some(0), Some("NTFS")), Verdict::Ok);
        assert_eq!(judge(100, 100, Some(10), Some("NTFS")), Verdict::NoSpace { need: 100 + SLACK, free: 10 });
        assert_eq!(judge(100, 100, Some(100 + SLACK), Some("NTFS")), Verdict::Ok);
        // 모르면 막지 않는다
        assert_eq!(judge(u64::MAX / 2, 1, None, None), Verdict::Ok);
        let big = FAT32_MAX_FILE + 1;
        assert_eq!(judge(big, big, Some(u64::MAX / 2), Some("FAT32")), Verdict::TooBigForFat { size: big });
        assert_eq!(judge(big, big, Some(u64::MAX / 2), Some("exFAT")), Verdict::Ok);
        assert_eq!(judge(FAT32_MAX_FILE, FAT32_MAX_FILE, Some(u64::MAX / 2), Some("FAT32")), Verdict::Ok);
        assert!(is_fat("fat") && is_fat("FAT32") && !is_fat("exFAT") && !is_fat("NTFS") && !is_fat("FA"));
    }
}
