use super::*;
use foldy_ntfs::usn::reason;
use foldy_proto::state;
use std::sync::atomic::AtomicBool;

fn entry(parent: u32, name: &str, is_dir: bool, size: u64) -> Entry {
    Entry { names: vec![(parent, name.into())], is_dir, size, alloc: size, has_data: !is_dir, mtime: 1, attributes: 0 }
}

/// C:\ (5) ─ Users(30) ─ me(31) ─ a.txt(40, 100B), b.bin(41, 5000B) ─ top.log(42)
fn sample() -> VolumeIndex {
    let mut v = VolumeIndex::new_ntfs("C:", 64);
    v.insert_build(5, &entry(5, ".", true, 0));
    v.insert_build(30, &entry(5, "Users", true, 0));
    v.insert_build(31, &entry(30, "me", true, 0));
    v.insert_build(40, &entry(31, "a.txt", false, 100));
    v.insert_build(41, &entry(31, "b.bin", false, 5000));
    v.insert_build(42, &entry(5, "top.log", false, 10));
    v.finalize();
    v
}

const SERIAL: u64 = 0x1234;
const JID: u64 = 7;

fn meta(next: i64) -> SnapshotMeta {
    SnapshotMeta { volume_serial: SERIAL, journal_id: JID, next_usn: next }
}

fn rec(r: u64, p: u64, usn: i64, rsn: u32, name: &str) -> UsnRecord {
    UsnRecord { record: r, parent: p, usn, reason: rsn, attributes: 0, name: name.into() }
}

/// 메모리 안의 가짜 저널. 한 번에 2개씩만 돌려줘서 여러 번 읽는 경로도 시험한다.
struct Fake {
    serial: u64,
    info: JournalInfo,
    recs: Vec<UsnRecord>,
    entries: HashMap<u32, Entry>,
    fail_read: bool,
}

impl Fake {
    fn new(recs: Vec<UsnRecord>, entries: HashMap<u32, Entry>) -> Self {
        Self { serial: SERIAL, info: JournalInfo { id: JID, first_usn: 50, next_usn: 200 }, recs, entries, fail_read: false }
    }
}

impl JournalSource for Fake {
    fn serial(&self) -> u64 {
        self.serial
    }
    fn journal(&self) -> io::Result<JournalInfo> {
        Ok(self.info)
    }
    fn read(&self, start: i64, journal_id: u64, _buf: &mut [u8]) -> io::Result<(i64, Vec<UsnRecord>)> {
        if self.fail_read || journal_id != self.info.id {
            return Err(io::Error::from_raw_os_error(1181));
        }
        let batch: Vec<UsnRecord> = self.recs.iter().filter(|r| r.usn >= start).take(2).cloned().collect();
        let next = batch.last().map(|r| r.usn + 1).unwrap_or(self.info.next_usn);
        Ok((next, batch))
    }
    fn fetch(&self, rec: u32) -> Option<Entry> {
        self.entries.get(&rec).cloned()
    }
}

fn journal_changes() -> Fake {
    // 스냅샷(next=100) 이후: 새 파일 60, a.txt 이동·이름 변경, b.bin 삭제, 새 폴더 61 안의 파일 62
    let recs = vec![
        rec(60, 31, 100, reason::FILE_CREATE, "new.dat"),
        rec(40, 31, 101, reason::RENAME_OLD_NAME, "a.txt"),
        rec(40, 30, 102, reason::RENAME_NEW_NAME, "moved.txt"),
        rec(41, 31, 103, reason::FILE_DELETE | reason::CLOSE, "b.bin"),
        rec(61, 5, 104, reason::FILE_CREATE, "dir"),
        rec(62, 61, 105, reason::FILE_CREATE, "inner.txt"),
    ];
    let entries = HashMap::from([
        (60, entry(31, "new.dat", false, 777)),
        (40, entry(30, "moved.txt", false, 100)),
        (61, entry(5, "dir", true, 0)),
        (62, entry(61, "inner.txt", false, 9)),
    ]);
    Fake::new(recs, entries)
}

fn cancel() -> AtomicBool {
    AtomicBool::new(false)
}

#[test]
fn restore_replays_journal_from_saved_position() {
    let bytes = sample().to_snapshot(&meta(100));
    let slot = VolumeSlot::new("C:");
    let (next, id) = restore(&slot, &journal_changes(), &bytes, &cancel()).unwrap();
    assert_eq!((next, id), (200, JID));
    assert_eq!(slot.state.load(Ordering::Acquire), state::READY);
    assert_eq!(slot.last_usn.load(Ordering::Relaxed), 200);
    let g = slot.index.read().unwrap();
    let idx = g.as_ref().unwrap();
    assert_eq!(idx.path_of(60), r"C:\Users\me\new.dat");
    assert_eq!(idx.path_of(40), r"C:\Users\moved.txt");
    assert!(!idx.node(41).unwrap().in_use());
    assert_eq!(idx.path_of(62), r"C:\dir\inner.txt");
    assert_eq!(idx.node(31).unwrap().size, 777);
    assert_eq!(idx.node(61).unwrap().size, 9);
    assert_eq!(idx.node(5).unwrap().size, 777 + 100 + 10 + 9);
}

#[test]
fn restored_index_equals_applying_the_same_journal_to_the_original() {
    // 스냅샷을 거치지 않은 색인에 같은 저널을 적용한 결과와 같아야 한다.
    let direct = RwLock::new(Some(sample()));
    replay(&journal_changes(), &direct, JID, 100, &cancel()).unwrap();
    let slot = VolumeSlot::new("C:");
    restore(&slot, &journal_changes(), &sample().to_snapshot(&meta(100)), &cancel()).unwrap();
    let (a, b) = (direct.read().unwrap(), slot.index.read().unwrap());
    let (a, b) = (a.as_ref().unwrap(), b.as_ref().unwrap());
    assert_eq!(a.live_count(), b.live_count());
    for id in [5, 30, 31, 40, 41, 42, 60, 61, 62] {
        assert_eq!(a.path_of(id), b.path_of(id));
        assert_eq!(a.node(id).map(|n| (n.size, n.alloc, n.in_use())), b.node(id).map(|n| (n.size, n.alloc, n.in_use())));
    }
}

#[test]
fn replay_is_idempotent_when_snapshot_is_ahead_of_its_position() {
    // 저장 도중 색인이 위치보다 앞서 있을 수 있다: 같은 변경을 두 번 적용해도 결과가 같다.
    let idx = RwLock::new(Some(sample()));
    replay(&journal_changes(), &idx, JID, 100, &cancel()).unwrap();
    let before = idx.read().unwrap().as_ref().unwrap().node(5).map(|n| n.size);
    replay(&journal_changes(), &idx, JID, 100, &cancel()).unwrap();
    assert_eq!(idx.read().unwrap().as_ref().unwrap().node(5).map(|n| n.size), before);
}

fn assert_rebuild(slot: &VolumeSlot, src: &Fake, bytes: &[u8], why: &str) {
    let e = restore(slot, src, bytes, &cancel()).unwrap_err();
    assert!(e.contains(why), "{e}");
    assert!(slot.index.read().unwrap().is_none(), "복원 실패 뒤에는 옛 내용이 남으면 안 됨: {e}");
    assert_ne!(slot.state.load(Ordering::Acquire), state::READY, "{e}");
}

#[test]
fn falls_back_when_snapshot_does_not_match_the_journal() {
    let bytes = sample().to_snapshot(&meta(100));
    let base = || journal_changes();

    let mut f = base();
    f.serial = 99;
    assert_rebuild(&VolumeSlot::new("C:"), &f, &bytes, "일련번호");

    let mut f = base();
    f.info.id = 8;
    assert_rebuild(&VolumeSlot::new("C:"), &f, &bytes, "저널 ID");

    // 저장한 위치가 저널의 가장 오래된 기록보다 앞 (저널이 잘려 나감)
    let mut f = base();
    f.info.first_usn = 150;
    assert_rebuild(&VolumeSlot::new("C:"), &f, &bytes, "오래된");

    // 저널이 초기화돼 위치가 끝보다 뒤
    let mut f = base();
    f.info.next_usn = 90;
    assert_rebuild(&VolumeSlot::new("C:"), &f, &bytes, "끝보다");

    // 다른 볼륨의 스냅샷
    assert_rebuild(&VolumeSlot::new("D:"), &base(), &bytes, "볼륨 경로");
}

#[test]
fn falls_back_on_corrupt_snapshot() {
    let bytes = sample().to_snapshot(&meta(100));
    let slot = VolumeSlot::new("C:");
    let mut bad = bytes.clone();
    let mid = bad.len() / 2;
    bad[mid] ^= 0xFF;
    assert_rebuild(&slot, &journal_changes(), &bad, "snapshot");
    assert_rebuild(&slot, &journal_changes(), &bytes[..bytes.len() - 3], "snapshot");
    assert_rebuild(&slot, &journal_changes(), &[], "snapshot");
}

#[test]
fn falls_back_when_journal_read_fails_midway() {
    let mut f = journal_changes();
    f.fail_read = true;
    assert_rebuild(&VolumeSlot::new("C:"), &f, &sample().to_snapshot(&meta(100)), "따라잡기");
}

#[test]
fn replay_stops_on_cancel() {
    let idx = RwLock::new(Some(sample()));
    let stop = AtomicBool::new(true);
    assert_eq!(replay(&journal_changes(), &idx, JID, 100, &stop).unwrap(), 100);
}

fn temp_dir(tag: &str) -> PathBuf {
    let d = std::env::temp_dir().join(format!("foldy-persist-{tag}-{}", std::process::id()));
    let _ = std::fs::remove_dir_all(&d);
    std::fs::create_dir_all(&d).unwrap();
    d
}

#[test]
fn write_atomic_replaces_and_leaves_no_temp_file() {
    let d = temp_dir("atomic");
    let p = snapshot_path(&d, "{abc-123}");
    write_atomic(&p, b"first").unwrap();
    write_atomic(&p, b"second").unwrap();
    assert_eq!(std::fs::read(&p).unwrap(), b"second");
    let names: Vec<String> = std::fs::read_dir(&d).unwrap().map(|e| e.unwrap().file_name().to_string_lossy().into_owned()).collect();
    assert_eq!(names, ["{abc-123}.idx"]);
    // 쓸 수 없는 경로면 오류이고 기존 파일은 그대로다.
    assert!(write_atomic(&d.join("no-such-dir").join("x.idx"), b"x").is_err());
    assert_eq!(std::fs::read(&p).unwrap(), b"second");
    let _ = std::fs::remove_dir_all(&d);
}

#[test]
fn snapshot_path_is_sanitized() {
    let p = snapshot_path(Path::new(r"C:\x"), r"..\evil\name");
    assert_eq!(p.parent().unwrap(), Path::new(r"C:\x"));
    assert!(!p.file_name().unwrap().to_string_lossy().contains(['\\', '/']));
}

#[test]
fn slot_save_then_restore_round_trip() {
    let d = temp_dir("slot");
    let path = snapshot_path(&d, "{vol}");
    let slot = VolumeSlot::new("C:");
    // 저널을 따라가기 전에는 저장하지 않는다.
    slot.publish(sample());
    assert!(!slot.save_snapshot(&path).unwrap());
    assert!(!path.exists());
    *slot.persist.lock().unwrap() = Some(Context { stem: "{vol}".into(), serial: SERIAL, journal_id: JID });
    slot.last_usn.store(100, Ordering::Relaxed);
    assert!(slot.save_snapshot(&path).unwrap());

    let again = VolumeSlot::new("C:");
    let bytes = std::fs::read(&path).unwrap();
    let (next, _) = restore(&again, &journal_changes(), &bytes, &cancel()).unwrap();
    assert_eq!(next, 200);
    assert_eq!(again.index.read().unwrap().as_ref().unwrap().path_of(62), r"C:\dir\inner.txt");
    let _ = std::fs::remove_dir_all(&d);
}
