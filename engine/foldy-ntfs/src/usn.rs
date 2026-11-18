//! USN 변경 저널 레코드 해석과 색인 반영 (FSCTL_READ_USN_JOURNAL 출력).
//!
//! USN 레코드에는 크기가 없으므로, 바뀐 레코드는 가능하면 MFT 레코드를 다시 읽어(`fetch`) 반영하고,
//! 읽지 못하면 USN 레코드의 이름·부모만으로 갱신한다.

use crate::index::{Entry, VolumeIndex};
use crate::record::RECORD_MASK;
use std::collections::HashMap;

pub mod reason {
    pub const DATA_OVERWRITE: u32 = 0x0000_0001;
    pub const DATA_EXTEND: u32 = 0x0000_0002;
    pub const DATA_TRUNCATION: u32 = 0x0000_0004;
    pub const FILE_CREATE: u32 = 0x0000_0100;
    pub const FILE_DELETE: u32 = 0x0000_0200;
    pub const RENAME_OLD_NAME: u32 = 0x0000_1000;
    pub const RENAME_NEW_NAME: u32 = 0x0000_2000;
    pub const BASIC_INFO_CHANGE: u32 = 0x0000_8000;
    pub const HARD_LINK_CHANGE: u32 = 0x0001_0000;
    pub const CLOSE: u32 = 0x8000_0000;

    /// 색인에 영향을 주는 이유들
    pub const RELEVANT: u32 = DATA_OVERWRITE
        | DATA_EXTEND
        | DATA_TRUNCATION
        | FILE_CREATE
        | FILE_DELETE
        | RENAME_OLD_NAME
        | RENAME_NEW_NAME
        | BASIC_INFO_CHANGE
        | HARD_LINK_CHANGE;
}

const FILE_ATTRIBUTE_DIRECTORY: u32 = 0x10;

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct UsnRecord {
    pub record: u64,
    pub parent: u64,
    pub usn: i64,
    pub reason: u32,
    pub attributes: u32,
    pub name: String,
}

fn u16_at(b: &[u8], o: usize) -> Option<u16> {
    b.get(o..o + 2).map(|s| u16::from_le_bytes([s[0], s[1]]))
}
fn u32_at(b: &[u8], o: usize) -> Option<u32> {
    b.get(o..o + 4).map(|s| u32::from_le_bytes([s[0], s[1], s[2], s[3]]))
}
fn u64_at(b: &[u8], o: usize) -> Option<u64> {
    let s = b.get(o..o + 8)?;
    let mut a = [0u8; 8];
    a.copy_from_slice(s);
    Some(u64::from_le_bytes(a))
}

/// USN_RECORD_V2 / V3 하나. V3의 128비트 참조 번호는 NTFS에서 하위 64비트만 쓴다.
pub fn parse_usn_record(r: &[u8]) -> Option<UsnRecord> {
    let major = u16_at(r, 4)?;
    let (frn, parent, usn, rsn, attrs, nlen, noff) = match major {
        2 => (u64_at(r, 8)?, u64_at(r, 16)?, u64_at(r, 24)?, u32_at(r, 40)?, u32_at(r, 52)?, u16_at(r, 56)?, u16_at(r, 58)?),
        3 => (u64_at(r, 8)?, u64_at(r, 24)?, u64_at(r, 40)?, u32_at(r, 56)?, u32_at(r, 68)?, u16_at(r, 72)?, u16_at(r, 74)?),
        _ => return None,
    };
    let nb = r.get(noff as usize..noff as usize + nlen as usize)?;
    let units: Vec<u16> = nb.as_chunks::<2>().0.iter().map(|c| u16::from_le_bytes(*c)).collect();
    Some(UsnRecord {
        record: frn & RECORD_MASK,
        parent: parent & RECORD_MASK,
        usn: usn as i64,
        reason: rsn,
        attributes: attrs,
        name: String::from_utf16_lossy(&units),
    })
}

/// FSCTL_READ_USN_JOURNAL 출력: 처음 8바이트는 다음 시작 USN, 뒤로 레코드가 이어진다.
pub fn parse_read_output(buf: &[u8]) -> (i64, Vec<UsnRecord>) {
    let Some(next) = u64_at(buf, 0) else { return (0, Vec::new()) };
    let mut out = Vec::new();
    let mut pos = 8usize;
    while let Some(len) = u32_at(buf, pos) {
        let len = len as usize;
        if len < 60 || pos + len > buf.len() {
            break;
        }
        if let Some(r) = parse_usn_record(&buf[pos..pos + len]) {
            out.push(r);
        }
        pos += (len + 7) & !7;
    }
    (next as i64, out)
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum Change {
    Delete(u32),
    /// 다시 읽어 반영. MFT를 못 읽을 때를 위해 USN의 이름·부모를 함께 둔다.
    Refresh { record: u32, parent: u32, name: String, is_dir: bool },
}

impl Change {
    pub fn record(&self) -> u32 {
        match self {
            Self::Delete(r) => *r,
            Self::Refresh { record, .. } => *record,
        }
    }
}

/// 레코드들을 레코드 번호별 최종 상태로 줄인다. 순서는 처음 나온 순서
/// (폴더 생성이 그 안의 파일 생성보다 먼저 반영되도록).
pub fn plan(records: &[UsnRecord]) -> Vec<Change> {
    let mut slot: HashMap<u32, usize> = HashMap::new();
    let mut out: Vec<Change> = Vec::new();
    for r in records {
        if r.reason & reason::RELEVANT == 0 {
            continue;
        }
        let (Ok(rec), Ok(parent)) = (u32::try_from(r.record), u32::try_from(r.parent)) else { continue };
        let change = if r.reason & reason::FILE_DELETE != 0 {
            Change::Delete(rec)
        } else if r.reason & reason::RENAME_OLD_NAME != 0 && r.reason & reason::RENAME_NEW_NAME == 0 {
            // 옛 이름 레코드는 새 이름 레코드가 곧 따라온다. 이미 계획이 있으면 그대로 둔다.
            if slot.contains_key(&rec) {
                continue;
            }
            Change::Refresh { record: rec, parent, name: r.name.clone(), is_dir: r.attributes & FILE_ATTRIBUTE_DIRECTORY != 0 }
        } else {
            Change::Refresh { record: rec, parent, name: r.name.clone(), is_dir: r.attributes & FILE_ATTRIBUTE_DIRECTORY != 0 }
        };
        match slot.get(&rec) {
            Some(&i) => out[i] = change,
            None => {
                slot.insert(rec, out.len());
                out.push(change);
            }
        }
    }
    out
}

/// 계획을 색인에 반영한다. `fetch`는 레코드 번호로 MFT 레코드를 다시 읽어 Entry를 만든다.
pub fn apply(idx: &mut VolumeIndex, changes: &[Change], mut fetch: impl FnMut(u32) -> Option<Entry>) {
    for c in changes {
        match c {
            Change::Delete(r) => idx.remove_record(*r),
            Change::Refresh { record, parent, name, is_dir } => {
                let e = match fetch(*record) {
                    Some(e) if !e.names.is_empty() => e,
                    // 다시 읽지 못했으면(이미 지워졌거나 권한 문제) USN 정보로 이름·위치만 갱신하고 크기는 유지
                    _ => {
                        let old = idx.node(*record).copied().filter(|n| n.in_use());
                        Entry {
                            names: vec![(*parent, name.clone())],
                            is_dir: *is_dir,
                            size: old.map(|n| n.size).unwrap_or(0),
                            alloc: old.map(|n| n.alloc).unwrap_or(0),
                            has_data: false,
                            mtime: old.map(|n| n.mtime).unwrap_or(0),
                            attributes: if *is_dir { FILE_ATTRIBUTE_DIRECTORY } else { 0 },
                        }
                    }
                };
                idx.replace_record(*record, &e);
            }
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::index::tests::{entry, sample};

    /// USN_RECORD_V2 바이트 생성 (시험용)
    fn v2(rec: u64, parent: u64, usn: i64, rsn: u32, attrs: u32, name: &str) -> Vec<u8> {
        let n16: Vec<u8> = name.encode_utf16().flat_map(|u| u.to_le_bytes()).collect();
        let len = (60 + n16.len() + 7) & !7;
        let mut b = vec![0u8; len];
        b[0..4].copy_from_slice(&(len as u32).to_le_bytes());
        b[4..6].copy_from_slice(&2u16.to_le_bytes());
        b[8..16].copy_from_slice(&(rec | (3 << 48)).to_le_bytes());
        b[16..24].copy_from_slice(&(parent | (1 << 48)).to_le_bytes());
        b[24..32].copy_from_slice(&usn.to_le_bytes());
        b[40..44].copy_from_slice(&rsn.to_le_bytes());
        b[52..56].copy_from_slice(&attrs.to_le_bytes());
        b[56..58].copy_from_slice(&(n16.len() as u16).to_le_bytes());
        b[58..60].copy_from_slice(&60u16.to_le_bytes());
        b[60..60 + n16.len()].copy_from_slice(&n16);
        b
    }

    fn v3(rec: u64, parent: u64, usn: i64, rsn: u32, name: &str) -> Vec<u8> {
        let n16: Vec<u8> = name.encode_utf16().flat_map(|u| u.to_le_bytes()).collect();
        let len = (76 + n16.len() + 7) & !7;
        let mut b = vec![0u8; len];
        b[0..4].copy_from_slice(&(len as u32).to_le_bytes());
        b[4..6].copy_from_slice(&3u16.to_le_bytes());
        b[8..16].copy_from_slice(&rec.to_le_bytes());
        b[24..32].copy_from_slice(&parent.to_le_bytes());
        b[40..48].copy_from_slice(&usn.to_le_bytes());
        b[56..60].copy_from_slice(&rsn.to_le_bytes());
        b[72..74].copy_from_slice(&(n16.len() as u16).to_le_bytes());
        b[74..76].copy_from_slice(&76u16.to_le_bytes());
        b[76..76 + n16.len()].copy_from_slice(&n16);
        b
    }

    fn output(next: i64, recs: &[Vec<u8>]) -> Vec<u8> {
        let mut b = next.to_le_bytes().to_vec();
        for r in recs {
            b.extend_from_slice(r);
        }
        b
    }

    #[test]
    fn parses_v2_and_v3_records() {
        let buf = output(
            9999,
            &[v2(40, 31, 100, reason::FILE_CREATE, 0x20, "새 파일.txt"), v3(41, 31, 101, reason::FILE_DELETE | reason::CLOSE, "b.bin")],
        );
        let (next, recs) = parse_read_output(&buf);
        assert_eq!(next, 9999);
        assert_eq!(recs.len(), 2);
        assert_eq!(recs[0], UsnRecord { record: 40, parent: 31, usn: 100, reason: reason::FILE_CREATE, attributes: 0x20, name: "새 파일.txt".into() });
        assert_eq!(recs[1].record, 41);
        assert_eq!(recs[1].reason, reason::FILE_DELETE | reason::CLOSE);
        // 깨진 입력
        let _ = parse_read_output(&buf[..20]);
        let _ = parse_read_output(&[]);
        let mut bad = buf.clone();
        bad[8] = 0xFF;
        bad[9] = 0xFF;
        let _ = parse_read_output(&bad);
    }

    fn rec(r: u64, p: u64, rsn: u32, name: &str) -> UsnRecord {
        UsnRecord { record: r, parent: p, usn: 0, reason: rsn, attributes: 0, name: name.into() }
    }

    #[test]
    fn plan_coalesces_per_record() {
        let p = plan(&[
            rec(50, 5, reason::FILE_CREATE, "tmp"),
            rec(51, 5, 0x0000_0800, "sec"), // 보안 변경: 무시
            rec(50, 5, reason::DATA_EXTEND, "tmp"),
            rec(52, 5, reason::FILE_CREATE, "gone"),
            rec(52, 5, reason::FILE_DELETE | reason::CLOSE, "gone"),
            rec(40, 31, reason::RENAME_OLD_NAME, "a.txt"),
            rec(40, 30, reason::RENAME_NEW_NAME, "moved.txt"),
        ]);
        assert_eq!(
            p,
            [
                Change::Refresh { record: 50, parent: 5, name: "tmp".into(), is_dir: false },
                Change::Delete(52),
                Change::Refresh { record: 40, parent: 30, name: "moved.txt".into(), is_dir: false },
            ]
        );
    }

    #[test]
    fn apply_with_and_without_fetch() {
        let mut v = sample();
        let changes = plan(&[
            rec(60, 31, reason::FILE_CREATE, "new.dat"),
            rec(40, 31, reason::RENAME_OLD_NAME, "a.txt"),
            rec(40, 30, reason::RENAME_NEW_NAME, "moved.txt"),
            rec(41, 31, reason::FILE_DELETE | reason::CLOSE, "b.bin"),
        ]);
        apply(&mut v, &changes, |r| (r == 60).then(|| entry(31, "new.dat", false, 777)));
        assert_eq!(v.path_of(60), r"C:\Users\me\new.dat");
        assert_eq!(v.path_of(40), r"C:\Users\moved.txt");
        assert_eq!(v.node(40).unwrap().size, 100, "fetch 실패 시 크기 유지");
        assert!(!v.node(41).unwrap().in_use());
        assert_eq!(v.node(31).unwrap().size, 777);
        assert_eq!(v.node(30).unwrap().size, 877);
    }
}
