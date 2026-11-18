//! 이름 풀 병렬 부분 문자열 검색 (대소문자 무시).
//!
//! 소문자로 접은 이름 풀(`fold`)은 이름마다 NUL로 끝나는 연속 버퍼다. 쿼리에는 NUL이 없으므로
//! memmem 일치는 이름 경계를 넘지 않는다. 풀을 조각으로 나눠 rayon으로 동시에 훑고,
//! 몇 조각씩 묶은 "물결" 단위로 결과를 내보내 첫 결과가 빨리 나가게 한다.

use crate::index::VolumeIndex;
use memchr::memmem;
use rayon::prelude::*;
use std::sync::atomic::{AtomicBool, Ordering};

/// 조각 크기. 너무 작으면 rayon 작업 분배 비용이, 너무 크면 첫 결과 지연이 커진다.
const CHUNK: usize = 256 * 1024;

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct Hit {
    /// 노드 id (`VolumeIndex::node`, `path_of`에 쓴다)
    pub id: u32,
    /// 접힌 이름의 풀 위치 (커서 계산용)
    pub fold_off: u32,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Outcome {
    /// 끝까지 훑었다
    Exhausted,
    /// limit에 닿았다. `next`부터 다시 시작하면 이어진다.
    Limit { next: u32 },
    Cancelled,
}

/// `needle`은 `fold_name`으로 접은 쿼리 바이트. `start`는 풀 위치(0 = 처음).
/// `on_batch`가 false를 돌려주면 취소로 본다.
pub fn search(
    idx: &VolumeIndex,
    needle: &[u8],
    start: u32,
    limit: usize,
    cancel: &AtomicBool,
    mut on_batch: impl FnMut(&[Hit]) -> bool,
) -> Outcome {
    let pool = idx.fold_pool();
    if needle.is_empty() || needle.contains(&0) || limit == 0 {
        return Outcome::Exhausted;
    }
    let finder = memmem::Finder::new(needle);
    let wave = CHUNK * (rayon::current_num_threads().max(1) * 2);
    let mut pos = (start as usize).max(1);
    let mut found = 0usize;
    let mut last_off = u32::MAX;
    while pos < pool.len() {
        if cancel.load(Ordering::Relaxed) {
            return Outcome::Cancelled;
        }
        let wave_end = (pos + wave).min(pool.len());
        let starts: Vec<usize> = (pos..wave_end).step_by(CHUNK).collect();
        let parts: Vec<Vec<Hit>> = starts
            .par_iter()
            .map(|&a| {
                if cancel.load(Ordering::Relaxed) {
                    return Vec::new();
                }
                scan(idx, &finder, a, (a + CHUNK).min(wave_end))
            })
            .collect();
        if cancel.load(Ordering::Relaxed) {
            return Outcome::Cancelled;
        }
        let mut batch = Vec::new();
        for h in parts.into_iter().flatten() {
            // 조각 경계에 걸친 이름은 양쪽에서 잡힐 수 있다.
            if h.fold_off == last_off {
                continue;
            }
            last_off = h.fold_off;
            batch.push(h);
            found += 1;
            if found >= limit {
                let next = name_end(pool, h.fold_off as usize) + 1;
                if !on_batch(&batch) {
                    return Outcome::Cancelled;
                }
                return if next >= pool.len() { Outcome::Exhausted } else { Outcome::Limit { next: next as u32 } };
            }
        }
        if !batch.is_empty() && !on_batch(&batch) {
            return Outcome::Cancelled;
        }
        pos = wave_end;
    }
    Outcome::Exhausted
}

fn name_end(pool: &[u8], from: usize) -> usize {
    memchr::memchr(0, &pool[from.min(pool.len())..]).map(|x| from + x).unwrap_or(pool.len())
}

/// [a, b)에서 시작하는 일치를 찾는다. 이름당 한 번만 센다.
fn scan(idx: &VolumeIndex, finder: &memmem::Finder<'_>, a: usize, b: usize) -> Vec<Hit> {
    let pool = idx.fold_pool();
    let entries = idx.fold_entries();
    let hay_end = (b + finder.needle().len() - 1).min(pool.len());
    let mut out = Vec::new();
    let mut p = a;
    while p < b {
        let Some(i) = finder.find(&pool[p..hay_end]) else { break };
        let m = p + i;
        if m >= b {
            break;
        }
        let k = entries.partition_point(|e| (e.0 as usize) <= m);
        if k > 0 {
            let (off, id) = entries[k - 1];
            if idx.entry_visible(off, id) {
                out.push(Hit { id, fold_off: off });
            }
        }
        p = name_end(pool, m) + 1;
    }
    out
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::index::tests::{entry, sample};
    use crate::index::{VolumeIndex, fold_name};

    fn all(idx: &VolumeIndex, q: &str) -> Vec<String> {
        let c = AtomicBool::new(false);
        let mut v = Vec::new();
        let out = search(idx, fold_name(q).as_bytes(), 0, usize::MAX, &c, |b| {
            v.extend(b.iter().map(|h| idx.path_of(h.id)));
            true
        });
        assert_eq!(out, Outcome::Exhausted);
        v
    }

    #[test]
    fn case_insensitive_substring() {
        let v = sample();
        assert_eq!(all(&v, "TXT"), [r"C:\Users\me\a.txt"]);
        assert_eq!(all(&v, "e"), [r"C:\Users", r"C:\Users\me"], "메타 파일($Extend)과 루트는 빠진다");
        assert!(all(&v, "usnjrnl").is_empty());
        assert!(all(&v, "zzz").is_empty());
        assert!(all(&v, "").is_empty());
    }

    #[test]
    fn unicode_names_fold() {
        let mut v = VolumeIndex::new_ntfs("C:", 8);
        v.insert_build(5, &entry(5, ".", true, 0));
        v.insert_build(20, &entry(5, "ÄÖÜ Straße 보고서.DOCX", false, 1));
        v.finalize();
        assert_eq!(all(&v, "äöü"), [r"C:\ÄÖÜ Straße 보고서.DOCX"]);
        assert_eq!(all(&v, "보고서.docx").len(), 1);
    }

    fn big() -> VolumeIndex {
        let mut v = VolumeIndex::new_ntfs("C:", 300_000);
        v.insert_build(5, &entry(5, ".", true, 0));
        v.insert_build(16, &entry(5, "d", true, 0));
        for i in 0..200_000u32 {
            let name = if i % 1000 == 7 { format!("report-{i:06}.PDF") } else { format!("file-{i:06}.bin") };
            v.insert_build(100 + i, &entry(16, &name, false, 1));
        }
        v.finalize();
        v
    }

    #[test]
    fn limit_and_cursor_page_through_all_hits_once() {
        let v = big();
        let c = AtomicBool::new(false);
        let needle = fold_name("report");
        let mut seen = Vec::new();
        let mut cursor = 0u32;
        loop {
            let mut page = Vec::new();
            let out = search(&v, needle.as_bytes(), cursor, 33, &c, |b| {
                page.extend(b.iter().map(|h| h.id));
                true
            });
            assert!(page.len() <= 33);
            seen.extend(page);
            match out {
                Outcome::Limit { next } => cursor = next,
                Outcome::Exhausted => break,
                Outcome::Cancelled => panic!("cancelled"),
            }
        }
        assert_eq!(seen.len(), 200);
        let mut d = seen.clone();
        d.sort();
        d.dedup();
        assert_eq!(d.len(), 200, "중복 없음");
    }

    #[test]
    fn repeated_matches_in_one_name_count_once() {
        let mut v = VolumeIndex::new_ntfs("C:", 8);
        v.insert_build(5, &entry(5, ".", true, 0));
        v.insert_build(20, &entry(5, "aaaaaaaa", false, 1));
        v.insert_build(21, &entry(5, "baab", false, 1));
        v.finalize();
        assert_eq!(all(&v, "aa").len(), 2);
    }

    #[test]
    fn cancel_stops_search() {
        let v = big();
        let c = AtomicBool::new(true);
        assert_eq!(search(&v, b"file", 0, usize::MAX, &c, |_| true), Outcome::Cancelled);
        let c = AtomicBool::new(false);
        assert_eq!(search(&v, b"file", 0, usize::MAX, &c, |_| false), Outcome::Cancelled);
    }

    #[test]
    fn renamed_and_deleted_entries_disappear() {
        let mut v = sample();
        v.replace_record(40, &entry(31, "renamed.md", false, 100));
        assert!(all(&v, "a.txt").is_empty());
        assert_eq!(all(&v, "renamed"), [r"C:\Users\me\renamed.md"]);
        v.remove_record(41);
        assert!(all(&v, "b.bin").is_empty());
    }
}
