//! 색인 스냅샷 저장·복원과 USN 저널 따라잡기 (docs/index-persistence.md).
//!
//! 원칙: 조금이라도 의심스러우면 복원하지 않고 전체 재구축으로 간다. 복원 규칙은 `restore`에 모여 있다.

use crate::state::VolumeSlot;
use foldy_ntfs::usn::{self, UsnRecord};
use foldy_ntfs::volume::{JournalInfo, Volume};
use foldy_ntfs::{Entry, SnapshotMeta, VolumeIndex};
use foldy_proto::state;
use std::collections::HashMap;
use std::io;
use std::path::{Path, PathBuf};
use std::sync::RwLock;
use std::sync::atomic::{AtomicBool, Ordering};

/// 저널을 읽는 쪽 추상화. 실제로는 `Volume`, 시험에서는 가짜.
pub trait JournalSource {
    fn serial(&self) -> u64;
    fn journal(&self) -> io::Result<JournalInfo>;
    fn read(&self, start: i64, journal_id: u64, buf: &mut [u8]) -> io::Result<(i64, Vec<UsnRecord>)>;
    fn fetch(&self, rec: u32) -> Option<Entry>;
}

impl JournalSource for Volume {
    fn serial(&self) -> u64 {
        self.boot.serial
    }
    fn journal(&self) -> io::Result<JournalInfo> {
        self.query_journal()
    }
    fn read(&self, start: i64, journal_id: u64, buf: &mut [u8]) -> io::Result<(i64, Vec<UsnRecord>)> {
        self.read_journal(start, journal_id, buf)
    }
    fn fetch(&self, rec: u32) -> Option<Entry> {
        self.fetch_entry(rec)
    }
}

/// 저장할 때 필요한 볼륨 식별 정보. 저널을 따라가기 시작한 뒤에만 있다.
#[derive(Debug, Clone)]
pub struct Context {
    /// 파일 이름 (볼륨 GUID)
    pub stem: String,
    pub serial: u64,
    pub journal_id: u64,
}

/// 기본 폴더 %ProgramData%\Foldy\Index
pub fn default_dir() -> PathBuf {
    let pd = std::env::var_os("ProgramData").map(PathBuf::from).unwrap_or_else(|| PathBuf::from(r"C:\ProgramData"));
    pd.join("Foldy").join("Index")
}

pub fn snapshot_path(dir: &Path, stem: &str) -> PathBuf {
    let safe: String = stem.chars().map(|c| if c.is_ascii_alphanumeric() || matches!(c, '{' | '}' | '-') { c } else { '_' }).collect();
    dir.join(format!("{safe}.idx"))
}

/// 임시 파일에 다 쓰고 디스크에 내린 뒤 이름을 바꿔 넣는다. 중간에 죽어도 옛 파일이 그대로 남는다.
pub fn write_atomic(path: &Path, bytes: &[u8]) -> io::Result<()> {
    use std::io::Write;
    let tmp = path.with_extension("idx.tmp");
    let r = (|| {
        let mut f = std::fs::File::create(&tmp)?;
        f.write_all(bytes)?;
        f.sync_all()?;
        drop(f);
        std::fs::rename(&tmp, path)
    })();
    if r.is_err() {
        let _ = std::fs::remove_file(&tmp);
    }
    r
}

/// 스냅샷이 이 볼륨의 현재 저널에 이어 붙을 수 있는지 확인한다. 안 되면 이유(재구축 사유).
pub fn validate(meta: &SnapshotMeta, src: &dyn JournalSource) -> Result<JournalInfo, String> {
    if meta.volume_serial != src.serial() {
        return Err("볼륨 일련번호가 다름".into());
    }
    let j = src.journal().map_err(|e| format!("저널 조회 실패: {e}"))?;
    if j.id != meta.journal_id {
        return Err("USN 저널 ID가 바뀜".into());
    }
    if meta.next_usn < j.first_usn {
        return Err("저장한 위치가 저널의 가장 오래된 기록보다 앞".into());
    }
    if meta.next_usn > j.next_usn {
        return Err("저장한 위치가 저널 끝보다 뒤".into());
    }
    Ok(j)
}

/// 저널을 한 번 읽어 색인에 반영한다. 읽은 레코드 수 (0이면 끝까지 따라잡음).
pub fn step(src: &dyn JournalSource, index: &RwLock<Option<VolumeIndex>>, id: u64, next: &mut i64, buf: &mut [u8]) -> io::Result<usize> {
    let (n2, recs) = src.read(*next, id, buf)?;
    if recs.is_empty() {
        *next = n2.max(*next);
        return Ok(0);
    }
    let changes = usn::plan(&recs);
    // MFT 다시 읽기는 잠금 밖에서
    let mut fetched: HashMap<u32, Option<Entry>> =
        changes.iter().filter(|c| matches!(c, usn::Change::Refresh { .. })).map(|c| (c.record(), src.fetch(c.record()))).collect();
    if let Ok(mut g) = index.write()
        && let Some(idx) = g.as_mut()
    {
        usn::apply(idx, &changes, |r| fetched.remove(&r).flatten());
    }
    *next = n2;
    Ok(recs.len())
}

/// `from`부터 저널 끝까지 따라잡는다. 돌려주는 값은 다음에 읽을 USN. 중간에 읽기 오류가 나면 Err.
pub fn replay(src: &dyn JournalSource, index: &RwLock<Option<VolumeIndex>>, id: u64, from: i64, cancel: &AtomicBool) -> io::Result<i64> {
    let mut next = from;
    let mut buf = vec![0u8; 256 * 1024];
    while !cancel.load(Ordering::Relaxed) {
        if step(src, index, id, &mut next, &mut buf)? == 0 {
            break;
        }
    }
    Ok(next)
}

/// 스냅샷 바이트로 슬롯을 채우고 저널을 따라잡는다. 성공하면 (다음 USN, 컨텍스트용 저널 ID).
/// Err이면 이유 문자열이고, 슬롯은 비어 있는 상태로 돌려놓는다 (호출자는 전체 재구축).
pub fn restore(slot: &VolumeSlot, src: &dyn JournalSource, bytes: &[u8], cancel: &AtomicBool) -> Result<(i64, u64), String> {
    let (idx, meta) = VolumeIndex::from_snapshot(bytes).map_err(|e| e.to_string())?;
    if !idx.root_path().trim_end_matches('\\').eq_ignore_ascii_case(slot.name.trim_end_matches('\\')) {
        return Err("스냅샷의 볼륨 경로가 다름".into());
    }
    let j = validate(&meta, src)?;
    let loaded = idx.live_count();
    slot.publish(idx);
    slot.last_usn.store(meta.next_usn, Ordering::Relaxed);
    slot.set_message(format!("스냅샷 불러옴: {loaded}개, 저널 {}→{} 따라잡는 중", meta.next_usn, j.next_usn));
    match replay(src, &slot.index, j.id, meta.next_usn, cancel) {
        Ok(next) => {
            slot.last_usn.store(next, Ordering::Relaxed);
            Ok((next, j.id))
        }
        Err(e) => {
            clear(slot);
            Err(format!("저널 따라잡기 실패: {e}"))
        }
    }
}

/// 슬롯의 색인을 비우고 준비 안 됨으로 되돌린다 (재구축 직전).
pub fn clear(slot: &VolumeSlot) {
    slot.state.store(state::INDEXING, Ordering::Release);
    if let Ok(mut g) = slot.index.write() {
        *g = None;
    }
    slot.entries.store(0, Ordering::Relaxed);
}

/// 폴더를 만들고 SYSTEM·Administrators만 접근하게 한다 (파일 이름 목록이 들어 있고, 변조되면 안 되므로).
pub fn ensure_secure_dir(dir: &Path) -> io::Result<()> {
    std::fs::create_dir_all(dir)?;
    crate::analysis::secure_dir_private(dir)
}

#[cfg(test)]
mod tests;
