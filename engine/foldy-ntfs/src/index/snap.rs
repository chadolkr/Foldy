//! 색인 스냅샷 직렬화 (docs/index-persistence.md).
//!
//! 형식 (모두 little endian): 매직 `FLDYIDX1`, 버전 u32, 메타(볼륨 일련번호, 저널 ID, 다음 USN),
//! 루트 정보, 노드·링크·이름 풀·하드링크 표, 마지막 8바이트는 앞 전체의 해시.
//! 검색용 접힌 이름(fold)은 저장하지 않고 읽을 때 다시 만든다. 이름 풀은 쓰면서 쓰레기를 뺀다.
//! 해시가 맞지 않거나 형식이 조금이라도 이상하면 오류를 돌려주고, 호출자는 전체 재구축으로 간다.

use super::{Node, VolumeIndex, fold_name};
use std::collections::HashMap;

const MAGIC: &[u8; 8] = b"FLDYIDX1";
const VERSION: u32 = 1;

/// 스냅샷이 어느 볼륨의 어느 저널 위치까지 반영했는지.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct SnapshotMeta {
    pub volume_serial: u64,
    pub journal_id: u64,
    /// 이 위치부터 저널을 다시 읽으면 스냅샷 이후의 변경을 따라잡는다.
    pub next_usn: i64,
}

#[derive(Debug, PartialEq, Eq)]
pub enum SnapshotError {
    Corrupt(&'static str),
    UnsupportedVersion(u32),
}

impl std::fmt::Display for SnapshotError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            Self::Corrupt(w) => write!(f, "snapshot corrupt: {w}"),
            Self::UnsupportedVersion(v) => write!(f, "snapshot version {v} not supported"),
        }
    }
}

impl std::error::Error for SnapshotError {}

/// 빠른 64비트 해시 (무결성 검사용, 보안용 아님: 폴더 ACL이 변조를 막는다).
fn checksum(data: &[u8]) -> u64 {
    const K: u64 = 0x9E37_79B9_7F4A_7C15;
    let mut h: u64 = 0xCBF2_9CE4_8422_2325 ^ data.len() as u64;
    let (chunks, rest) = data.as_chunks::<8>();
    for c in chunks {
        h = (h ^ u64::from_le_bytes(*c)).wrapping_mul(K);
        h ^= h >> 29;
    }
    for &b in rest {
        h = (h ^ b as u64).wrapping_mul(K);
    }
    h ^ (h >> 32)
}

struct Reader<'a> {
    b: &'a [u8],
    pos: usize,
}

impl<'a> Reader<'a> {
    fn take(&mut self, n: usize) -> Result<&'a [u8], SnapshotError> {
        let end = self.pos.checked_add(n).ok_or(SnapshotError::Corrupt("length overflow"))?;
        let s = self.b.get(self.pos..end).ok_or(SnapshotError::Corrupt("truncated"))?;
        self.pos = end;
        Ok(s)
    }
    fn u8(&mut self) -> Result<u8, SnapshotError> {
        Ok(self.take(1)?[0])
    }
    fn u16(&mut self) -> Result<u16, SnapshotError> {
        Ok(u16::from_le_bytes(self.take(2)?.try_into().unwrap_or_default()))
    }
    fn u32(&mut self) -> Result<u32, SnapshotError> {
        Ok(u32::from_le_bytes(self.take(4)?.try_into().unwrap_or_default()))
    }
    fn u64(&mut self) -> Result<u64, SnapshotError> {
        Ok(u64::from_le_bytes(self.take(8)?.try_into().unwrap_or_default()))
    }
    /// 남은 바이트로 불가능한 개수는 미리 거절한다 (거대한 할당 방지).
    fn count(&mut self, min_item: usize) -> Result<usize, SnapshotError> {
        let n = self.u32()? as usize;
        if n.saturating_mul(min_item) > self.b.len() - self.pos {
            return Err(SnapshotError::Corrupt("count exceeds data"));
        }
        Ok(n)
    }
}

const NODE_BYTES: usize = 4 + 4 + 2 + 2 + 8 + 8 + 8;

fn put_node(out: &mut Vec<u8>, n: &Node, name_off: u32) {
    out.extend_from_slice(&n.parent.to_le_bytes());
    out.extend_from_slice(&name_off.to_le_bytes());
    out.extend_from_slice(&n.name_len.to_le_bytes());
    out.extend_from_slice(&n.flags.to_le_bytes());
    out.extend_from_slice(&n.size.to_le_bytes());
    out.extend_from_slice(&n.alloc.to_le_bytes());
    out.extend_from_slice(&n.mtime.to_le_bytes());
}

fn get_node(r: &mut Reader) -> Result<Node, SnapshotError> {
    Ok(Node {
        parent: r.u32()?,
        name_off: r.u32()?,
        fold_off: 0,
        name_len: r.u16()?,
        flags: r.u16()?,
        size: r.u64()?,
        alloc: r.u64()?,
        mtime: r.u64()? as i64,
    })
}

impl VolumeIndex {
    /// 색인을 스냅샷 바이트로 만든다. 호출자가 읽기 잠금을 잡고 부른다.
    pub fn to_snapshot(&self, meta: &SnapshotMeta) -> Vec<u8> {
        let mut out = Vec::with_capacity((self.nodes.len() + self.links.len()) * NODE_BYTES + self.names.len() + 256);
        out.extend_from_slice(MAGIC);
        out.extend_from_slice(&VERSION.to_le_bytes());
        out.extend_from_slice(&meta.volume_serial.to_le_bytes());
        out.extend_from_slice(&meta.journal_id.to_le_bytes());
        out.extend_from_slice(&meta.next_usn.to_le_bytes());
        out.push(self.ntfs_meta as u8);
        out.extend_from_slice(&self.root.to_le_bytes());
        out.extend_from_slice(&(self.root_path.len() as u32).to_le_bytes());
        out.extend_from_slice(self.root_path.as_bytes());

        // 이름 풀은 살아 있는 노드의 이름만 순서대로 다시 모은다.
        let mut names: Vec<u8> = Vec::with_capacity(self.names.len().saturating_sub(self.garbage));
        let mut nodes_buf = Vec::with_capacity(self.nodes.len() * NODE_BYTES);
        let mut links_buf = Vec::with_capacity(self.links.len() * NODE_BYTES);
        for (list, buf) in [(&self.nodes, &mut nodes_buf), (&self.links, &mut links_buf)] {
            for n in list.iter() {
                let mut off = 0;
                if n.name_len > 0 {
                    off = names.len() as u32;
                    let s = n.name_off as usize;
                    names.extend_from_slice(self.names.get(s..s + n.name_len as usize).unwrap_or(&[]));
                }
                // 이름을 못 읽는 노드는 길이 0으로 쓴다 (검색에서 이름이 빈 항목).
                let mut n2 = *n;
                if n.name_len > 0 && names.len() as u32 - off != n.name_len as u32 {
                    n2.name_len = 0;
                    names.truncate(off as usize);
                }
                put_node(buf, &n2, off);
            }
        }
        out.extend_from_slice(&(self.nodes.len() as u32).to_le_bytes());
        out.extend_from_slice(&(self.links.len() as u32).to_le_bytes());
        out.extend_from_slice(&(names.len() as u32).to_le_bytes());
        out.extend_from_slice(&nodes_buf);
        out.extend_from_slice(&links_buf);
        out.extend_from_slice(&names);
        out.extend_from_slice(&(self.extra.len() as u32).to_le_bytes());
        let mut keys: Vec<&u32> = self.extra.keys().collect();
        keys.sort();
        for k in keys {
            let ids = &self.extra[k];
            out.extend_from_slice(&k.to_le_bytes());
            out.extend_from_slice(&(ids.len() as u32).to_le_bytes());
            for id in ids {
                out.extend_from_slice(&id.to_le_bytes());
            }
        }
        out.extend_from_slice(&(self.free_links.len() as u32).to_le_bytes());
        for id in &self.free_links {
            out.extend_from_slice(&id.to_le_bytes());
        }
        let sum = checksum(&out);
        out.extend_from_slice(&sum.to_le_bytes());
        out
    }

    /// 스냅샷 바이트에서 색인을 복원한다. 해시·형식이 어긋나면 오류.
    pub fn from_snapshot(bytes: &[u8]) -> Result<(Self, SnapshotMeta), SnapshotError> {
        if bytes.len() < MAGIC.len() + 8 {
            return Err(SnapshotError::Corrupt("too short"));
        }
        let (body, tail) = bytes.split_at(bytes.len() - 8);
        if &body[..8] != MAGIC {
            return Err(SnapshotError::Corrupt("bad magic"));
        }
        if checksum(body) != u64::from_le_bytes(tail.try_into().unwrap_or_default()) {
            return Err(SnapshotError::Corrupt("checksum mismatch"));
        }
        let mut r = Reader { b: body, pos: 8 };
        let version = r.u32()?;
        if version != VERSION {
            return Err(SnapshotError::UnsupportedVersion(version));
        }
        let meta = SnapshotMeta { volume_serial: r.u64()?, journal_id: r.u64()?, next_usn: r.u64()? as i64 };
        let ntfs_meta = r.u8()? != 0;
        let root = r.u32()?;
        let rp_len = r.count(1)?;
        let root_path = String::from_utf8(r.take(rp_len)?.to_vec()).map_err(|_| SnapshotError::Corrupt("root path"))?;
        let n_nodes = r.u32()? as usize;
        let n_links = r.u32()? as usize;
        let names_len = r.u32()? as usize;
        if (n_nodes.saturating_add(n_links)).saturating_mul(NODE_BYTES).saturating_add(names_len) > body.len() - r.pos {
            return Err(SnapshotError::Corrupt("section sizes"));
        }
        let mut nodes = Vec::with_capacity(n_nodes);
        for _ in 0..n_nodes {
            nodes.push(get_node(&mut r)?);
        }
        let mut links = Vec::with_capacity(n_links);
        for _ in 0..n_links {
            links.push(get_node(&mut r)?);
        }
        let names = r.take(names_len)?.to_vec();
        for n in nodes.iter().chain(links.iter()) {
            if n.name_len > 0 && (n.name_off as usize).saturating_add(n.name_len as usize) > names.len() {
                return Err(SnapshotError::Corrupt("name range"));
            }
        }
        if (root as usize) >= nodes.len() {
            return Err(SnapshotError::Corrupt("root out of range"));
        }
        let n_extra = r.count(8)?;
        let mut extra = HashMap::with_capacity(n_extra);
        for _ in 0..n_extra {
            let rec = r.u32()?;
            let k = r.count(4)?;
            let mut ids = Vec::with_capacity(k);
            for _ in 0..k {
                ids.push(r.u32()?);
            }
            extra.insert(rec, ids);
        }
        let n_free = r.count(4)?;
        let mut free_links = Vec::with_capacity(n_free);
        for _ in 0..n_free {
            free_links.push(r.u32()?);
        }
        if r.pos != body.len() {
            return Err(SnapshotError::Corrupt("trailing bytes"));
        }

        // 접힌 이름 풀과 색인을 다시 만든다.
        let mut idx = Self {
            root_path,
            root,
            ntfs_meta,
            nodes,
            links,
            names,
            fold: vec![0],
            fold_index: Vec::new(),
            extra,
            free_links,
            garbage: 0,
            ready: true,
        };
        idx.rebuild_fold();
        Ok((idx, meta))
    }

    fn rebuild_fold(&mut self) {
        let mut fold = Vec::with_capacity(self.names.len() + self.nodes.len() + 1);
        fold.push(0u8);
        let mut fold_index = Vec::with_capacity(self.nodes.len());
        let ids: Vec<u32> = (0..self.nodes.len() as u32).chain((0..self.links.len() as u32).map(|i| i | super::LINK_BIT)).collect();
        for id in ids {
            let Some(n) = self.node(id).copied() else { continue };
            if n.name_len == 0 {
                continue;
            }
            let fo = fold.len() as u32;
            fold.extend_from_slice(fold_name(self.name(id)).as_bytes());
            fold.push(0);
            fold_index.push((fo, id));
            if let Some(m) = self.node_mut(id) {
                m.fold_off = fo;
            }
        }
        self.fold = fold;
        self.fold_index = fold_index;
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::index::tests::{entry, sample};

    fn meta() -> SnapshotMeta {
        SnapshotMeta { volume_serial: 0xAABB_CCDD, journal_id: 77, next_usn: 12345 }
    }

    #[test]
    fn round_trip_preserves_paths_sizes_and_search() {
        let mut v = sample();
        // 이름 변경으로 쓰레기와 하드링크도 만든다.
        v.replace_record(40, &entry(31, "renamed.txt", false, 300));
        let mut e = entry(30, "x.dll", false, 1000);
        e.names.push((31, "x-link.dll".into()));
        v.replace_record(50, &e);
        let bytes = v.to_snapshot(&meta());
        let (back, m) = VolumeIndex::from_snapshot(&bytes).unwrap();
        assert_eq!(m, meta());
        assert_eq!(back.root_path(), v.root_path());
        assert_eq!(back.live_count(), v.live_count());
        for id in [5, 30, 31, 40, 41, 42, 50] {
            assert_eq!(back.path_of(id), v.path_of(id));
            assert_eq!(back.node(id).map(|n| (n.size, n.alloc, n.flags, n.mtime)), v.node(id).map(|n| (n.size, n.alloc, n.flags, n.mtime)));
        }
        assert_eq!(back.resolve(r"C:\Users\me\renamed.txt"), Some(40));
        assert_eq!(back.resolve(r"c:\users\ME\X-LINK.dll").map(|i| back.path_of(i)), v.resolve(r"c:\users\ME\X-LINK.dll").map(|i| v.path_of(i)));
        // 읽은 색인에도 증분 갱신이 그대로 된다.
        let mut back = back;
        back.remove_record(41);
        assert_eq!(back.node(31).unwrap().size, 300, "추가 하드링크 이름은 크기에 들어가지 않는다");
        // 접힌 이름 색인이 검색에 쓰인다.
        let hits = crate::search::search(&back, b"renamed", 0, 10, &std::sync::atomic::AtomicBool::new(false), |_| true);
        assert_eq!(hits, crate::search::Outcome::Exhausted);
    }

    #[test]
    fn snapshot_drops_name_garbage() {
        let mut v = sample();
        for i in 0..50 {
            v.replace_record(40, &entry(31, &format!("longer-name-number-{i}.txt"), false, 1));
        }
        let (back, _) = VolumeIndex::from_snapshot(&v.to_snapshot(&meta())).unwrap();
        assert!(back.names.len() < v.names.len());
        assert_eq!(back.path_of(40), v.path_of(40));
    }

    #[test]
    fn corruption_is_detected() {
        let bytes = sample().to_snapshot(&meta());
        // 어느 한 바이트를 바꿔도, 어디서 잘라도 오류여야 한다 (패닉 없이).
        for i in 0..bytes.len() {
            let mut b = bytes.clone();
            b[i] ^= 0x5A;
            assert!(VolumeIndex::from_snapshot(&b).is_err(), "byte {i}");
        }
        for n in 0..bytes.len() {
            assert!(VolumeIndex::from_snapshot(&bytes[..n]).is_err(), "len {n}");
        }
        assert!(VolumeIndex::from_snapshot(&[]).is_err());
    }

    #[test]
    fn rejects_other_version_even_with_valid_checksum() {
        let mut bytes = sample().to_snapshot(&meta());
        bytes[8] = 9;
        let n = bytes.len() - 8;
        let sum = checksum(&bytes[..n]);
        bytes[n..].copy_from_slice(&sum.to_le_bytes());
        assert_eq!(VolumeIndex::from_snapshot(&bytes).err(), Some(SnapshotError::UnsupportedVersion(9)));
    }
}
