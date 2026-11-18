//! Search / FolderSizes 요청 처리 (여러 볼륨에 걸친 검색, 커서 인코딩).
//!
//! 커서 = (볼륨 순번 << 56) | 이름 풀 위치. 클라이언트에게는 불투명한 값이다.

use crate::state::Indexer;
use foldy_ntfs::search::{self, Outcome};
use foldy_ntfs::{VolumeIndex, fold_name};
use foldy_proto::*;
use std::sync::atomic::AtomicBool;

const POS_MASK: u64 = (1 << 56) - 1;
const DEFAULT_LIMIT: u32 = 1000;

fn batch(id: u32, hits: Vec<SearchHit>, done: bool, status: u8, next: u64) -> SearchBatch {
    SearchBatch { search_id: id, hits, done, status, next_cursor: next }
}

fn to_hit(idx: &VolumeIndex, id: u32) -> Option<SearchHit> {
    let n = idx.node(id)?;
    Some(SearchHit { path: idx.path_of(id), size: n.size, alloc_size: n.alloc, mtime: n.mtime, is_dir: n.is_dir() })
}

/// 검색을 실행하며 배치를 `send`로 보낸다. 마지막 배치는 항상 done=true다.
/// `send`가 false를 돌려주면(연결 끊김) 멈춘다.
pub fn search(ix: &Indexer, req: &Search, cancel: &AtomicBool, mut send: impl FnMut(SearchBatch) -> bool) {
    let id = req.search_id;
    let needle = fold_name(req.query.trim());
    if needle.is_empty() {
        send(batch(id, Vec::new(), true, search_status::BAD_QUERY, 0));
        return;
    }
    if !ix.volumes.iter().any(|v| v.state.load(std::sync::atomic::Ordering::Acquire) == state::READY) {
        send(batch(id, Vec::new(), true, search_status::NOT_READY, 0));
        return;
    }
    let limit = if req.limit == 0 { DEFAULT_LIMIT } else { req.limit.min(MAX_SEARCH_LIMIT) } as usize;
    let first_vol = (req.cursor >> 56) as usize;
    let mut found = 0usize;
    let mut alive = true;
    for (ord, slot) in ix.volumes.iter().enumerate().skip(first_vol) {
        let Ok(guard) = slot.index.read() else { continue };
        let Some(idx) = guard.as_ref() else { continue };
        let start = if ord == first_vol { (req.cursor & POS_MASK) as u32 } else { 0 };
        let outcome = search::search(idx, needle.as_bytes(), start, limit - found, cancel, |hits| {
            found += hits.len();
            for chunk in hits.chunks(SEARCH_BATCH_MAX) {
                let v: Vec<SearchHit> = chunk.iter().filter_map(|h| to_hit(idx, h.id)).collect();
                if !send(batch(id, v, false, search_status::OK, 0)) {
                    alive = false;
                    return false;
                }
            }
            true
        });
        if !alive {
            return;
        }
        match outcome {
            Outcome::Cancelled => {
                send(batch(id, Vec::new(), true, search_status::CANCELLED, 0));
                return;
            }
            Outcome::Limit { next } => {
                send(batch(id, Vec::new(), true, search_status::OK, ((ord as u64) << 56) | next as u64));
                return;
            }
            Outcome::Exhausted if found >= limit => {
                let next = if ord + 1 < ix.volumes.len() { ((ord as u64) + 1) << 56 } else { 0 };
                send(batch(id, Vec::new(), true, search_status::OK, next));
                return;
            }
            Outcome::Exhausted => {}
        }
    }
    send(batch(id, Vec::new(), true, search_status::OK, 0));
}

fn covers(idx: &VolumeIndex, path: &str) -> bool {
    let root = fold_name(idx.root_path().trim_end_matches('\\'));
    let p = fold_name(&path.replace('/', "\\"));
    p.starts_with(&root) && (p.len() == root.len() || p.as_bytes()[root.len()] == b'\\')
}

pub fn folder_sizes(ix: &Indexer, path: &str) -> FolderSizesReply {
    let empty = |status| FolderSizesReply { status, total_size: 0, total_alloc: 0, entries: Vec::new() };
    for slot in &ix.volumes {
        let Ok(guard) = slot.index.read() else { continue };
        let Some(idx) = guard.as_ref() else { continue };
        if !covers(idx, path) {
            continue;
        }
        let Some(dir) = idx.resolve(path) else { return empty(folder_status::NOT_FOUND) };
        let Some(d) = idx.node(dir) else { return empty(folder_status::NOT_FOUND) };
        let entries = if d.is_dir() {
            idx.children(dir)
                .into_iter()
                .filter_map(|c| {
                    let n = idx.node(c)?;
                    Some(FolderEntry { name: idx.name(c).to_string(), is_dir: n.is_dir(), size: n.size, alloc_size: n.alloc })
                })
                .collect()
        } else {
            Vec::new()
        };
        return FolderSizesReply { status: folder_status::OK, total_size: d.size, total_alloc: d.alloc, entries };
    }
    empty(folder_status::NOT_INDEXED)
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::state::{VolumeSlot, scan_dir};
    use std::sync::Arc;

    fn temp_tree(tag: &str) -> std::path::PathBuf {
        let root = std::env::temp_dir().join(format!("foldy-index-test-{tag}-{}", std::process::id()));
        let _ = std::fs::remove_dir_all(&root);
        std::fs::create_dir_all(root.join("하위").join("깊은")).unwrap();
        std::fs::write(root.join("Report-2026.PDF"), vec![0u8; 5000]).unwrap();
        std::fs::write(root.join("하위").join("report-old.pdf"), vec![0u8; 100]).unwrap();
        std::fs::write(root.join("하위").join("깊은").join("notes.txt"), vec![0u8; 7]).unwrap();
        for i in 0..50 {
            std::fs::write(root.join(format!("bulk-{i:03}.report")), b"x").unwrap();
        }
        root
    }

    fn indexer(roots: &[&std::path::Path]) -> Indexer {
        let mut ix = Indexer::default();
        for r in roots {
            let slot = Arc::new(VolumeSlot::new(r.to_string_lossy()));
            slot.publish(scan_dir(&slot.name).unwrap());
            ix.volumes.push(slot);
        }
        ix
    }

    fn run(ix: &Indexer, q: &str, limit: u32, cursor: u64) -> (Vec<SearchHit>, SearchBatch) {
        let mut hits = Vec::new();
        let mut last = None;
        let c = AtomicBool::new(false);
        search(ix, &Search { search_id: 9, query: q.into(), limit, cursor }, &c, |b| {
            assert_eq!(b.search_id, 9);
            assert!(last.is_none(), "done 뒤에 배치가 오면 안 됨");
            hits.extend(b.hits.iter().cloned());
            if b.done {
                last = Some(b);
            }
            true
        });
        (hits, last.unwrap())
    }

    #[test]
    fn search_finds_real_files_with_sizes() {
        let root = temp_tree("search");
        let ix = indexer(&[&root]);
        let (hits, last) = run(&ix, "REPORT-", 0, 0);
        assert_eq!(last.status, search_status::OK);
        assert_eq!(last.next_cursor, 0);
        let mut paths: Vec<String> = hits.iter().map(|h| h.path.clone()).collect();
        paths.sort();
        assert_eq!(
            paths,
            [root.join("Report-2026.PDF").to_string_lossy().into_owned(), root.join("하위").join("report-old.pdf").to_string_lossy().into_owned()]
        );
        let h = hits.iter().find(|h| h.path.ends_with("Report-2026.PDF")).unwrap();
        assert_eq!(h.size, 5000);
        assert!(!h.is_dir && h.mtime > 0);
        let (dirs, _) = run(&ix, "깊은", 0, 0);
        assert!(dirs.len() == 1 && dirs[0].is_dir && dirs[0].size == 7);
        let _ = std::fs::remove_dir_all(&root);
    }

    #[test]
    fn cursor_pages_across_volumes() {
        let a = temp_tree("page-a");
        let b = temp_tree("page-b");
        let ix = indexer(&[&a, &b]);
        let (all, _) = run(&ix, "report", 0, 0);
        assert_eq!(all.len(), 2 * 52);
        let mut seen = Vec::new();
        let mut cursor = 0;
        loop {
            let (h, last) = run(&ix, "report", 7, cursor);
            assert!(h.len() <= 7);
            seen.extend(h.into_iter().map(|x| x.path));
            if last.next_cursor == 0 {
                break;
            }
            cursor = last.next_cursor;
        }
        let mut s2 = seen.clone();
        s2.sort();
        s2.dedup();
        assert_eq!(seen.len(), 104);
        assert_eq!(s2.len(), 104);
        let _ = std::fs::remove_dir_all(&a);
        let _ = std::fs::remove_dir_all(&b);
    }

    #[test]
    fn bad_query_not_ready_and_cancel() {
        let ix = Indexer::default();
        assert_eq!(run(&ix, "x", 0, 0).1.status, search_status::NOT_READY);
        let root = temp_tree("bad");
        let ix = indexer(&[&root]);
        assert_eq!(run(&ix, "   ", 0, 0).1.status, search_status::BAD_QUERY);
        let c = AtomicBool::new(true);
        let mut st = None;
        search(&ix, &Search { search_id: 1, query: "report".into(), limit: 0, cursor: 0 }, &c, |b| {
            st = Some(b.status);
            true
        });
        assert_eq!(st, Some(search_status::CANCELLED));
        let _ = std::fs::remove_dir_all(&root);
    }

    #[test]
    fn folder_sizes_of_real_tree() {
        let root = temp_tree("sizes");
        let ix = indexer(&[&root]);
        let r = folder_sizes(&ix, &root.to_string_lossy());
        assert_eq!(r.status, folder_status::OK);
        assert_eq!(r.total_size, 5000 + 100 + 7 + 50);
        let sub = r.entries.iter().find(|e| e.name == "하위").unwrap();
        assert!(sub.is_dir);
        assert_eq!(sub.size, 107);
        let r2 = folder_sizes(&ix, &root.join("하위").to_string_lossy().to_uppercase());
        assert_eq!(r2.entries.len(), 2);
        assert_eq!(folder_sizes(&ix, &root.join("없음").to_string_lossy()).status, folder_status::NOT_FOUND);
        assert_eq!(folder_sizes(&ix, r"Q:\nowhere").status, folder_status::NOT_INDEXED);
        let _ = std::fs::remove_dir_all(&root);
    }
}
