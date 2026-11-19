//! 볼륨별 색인 상태와 백그라운드 구축·USN 갱신 스레드.

use foldy_ntfs::VolumeIndex;
use foldy_proto::{AnalysisProgress, StatusReply, VolumeStatus, analysis_state, state};
use std::sync::atomic::{AtomicBool, AtomicI64, AtomicU8, AtomicU16, AtomicU64, Ordering};
use std::sync::{Arc, Mutex, RwLock};

/// 볼륨 하나의 분석 DB 작업 상태 (설계서 7.3). StatusReply.analysis로 보인다.
#[derive(Default)]
pub struct AnalysisJob {
    pub state: AtomicU8,
    pub progress: AtomicU16,
    pub message: Mutex<String>,
}

impl AnalysisJob {
    pub fn set(&self, state: u8, progress: u16, message: &str) {
        self.progress.store(progress, Ordering::Relaxed);
        if let Ok(mut g) = self.message.lock()
            && *g != message
        {
            *g = message.to_string();
        }
        self.state.store(state, Ordering::Release);
    }

    /// 아직 만드는 중이 아니면 BUILDING으로 바꾸고 true.
    pub fn try_start(&self) -> bool {
        let cur = self.state.load(Ordering::Acquire);
        cur != analysis_state::BUILDING
            && self.state.compare_exchange(cur, analysis_state::BUILDING, Ordering::AcqRel, Ordering::Acquire).is_ok()
    }
}

pub struct VolumeSlot {
    /// "C:" 또는 디렉터리 스캔 루트 경로
    pub name: String,
    pub state: AtomicU8,
    pub progress: AtomicU16,
    pub entries: AtomicU64,
    pub last_usn: AtomicI64,
    pub message: Mutex<String>,
    pub index: RwLock<Option<VolumeIndex>>,
    pub analysis: AnalysisJob,
    /// 스냅샷 저장에 필요한 식별 정보. 저널을 따라가기 시작한 뒤에만 Some (그 전에는 저장하지 않는다).
    pub persist: Mutex<Option<crate::persist::Context>>,
    /// 마지막 저장 뒤에 색인이 바뀌었는지
    pub dirty: AtomicBool,
    /// 저장을 한 번에 하나만 (주기 저장과 종료 저장이 겹치지 않게)
    pub save_lock: Mutex<()>,
}

impl VolumeSlot {
    pub fn new(name: impl Into<String>) -> Self {
        Self {
            name: name.into(),
            state: AtomicU8::new(state::NOT_READY),
            progress: AtomicU16::new(0),
            entries: AtomicU64::new(0),
            last_usn: AtomicI64::new(0),
            message: Mutex::new(String::new()),
            index: RwLock::new(None),
            analysis: AnalysisJob::default(),
            persist: Mutex::new(None),
            dirty: AtomicBool::new(false),
            save_lock: Mutex::new(()),
        }
    }

    /// 색인을 스냅샷 파일로 저장한다. 저널을 따라가는 중이 아니면 저장하지 않고 false.
    pub fn save_snapshot(&self, path: &std::path::Path) -> std::io::Result<bool> {
        let Some(ctx) = self.persist.lock().ok().and_then(|g| g.clone()) else { return Ok(false) };
        let _one = self.save_lock.lock();
        self.dirty.store(false, Ordering::Relaxed);
        // 위치를 먼저 읽는다: 색인이 이보다 조금 앞서 있어도 다시 적용하면 같은 결과가 된다.
        let meta = foldy_ntfs::SnapshotMeta { volume_serial: ctx.serial, journal_id: ctx.journal_id, next_usn: self.last_usn.load(Ordering::Relaxed) };
        let bytes = {
            let Ok(g) = self.index.read() else { return Ok(false) };
            let Some(idx) = g.as_ref() else { return Ok(false) };
            idx.to_snapshot(&meta)
        };
        crate::persist::write_atomic(path, &bytes).inspect_err(|_| self.dirty.store(true, Ordering::Relaxed))?;
        Ok(true)
    }

    pub fn analysis_status(&self) -> AnalysisProgress {
        AnalysisProgress {
            volume: self.name.clone(),
            state: self.analysis.state.load(Ordering::Acquire),
            progress_permille: self.analysis.progress.load(Ordering::Relaxed),
            message: self.analysis.message.lock().map(|g| g.clone()).unwrap_or_default(),
        }
    }

    pub fn set_message(&self, m: impl Into<String>) {
        let m = m.into();
        eprintln!("[{}] {m}", self.name);
        if let Ok(mut g) = self.message.lock() {
            *g = m;
        }
    }

    /// 새 색인으로 바꾸고 준비됨으로 표시한다.
    pub fn publish(&self, idx: VolumeIndex) {
        self.entries.store(idx.live_count() as u64, Ordering::Relaxed);
        if let Ok(mut g) = self.index.write() {
            *g = Some(idx);
        }
        self.progress.store(1000, Ordering::Relaxed);
        self.state.store(state::READY, Ordering::Release);
    }

    pub fn status(&self) -> VolumeStatus {
        VolumeStatus {
            volume: self.name.clone(),
            state: self.state.load(Ordering::Acquire),
            entries: self.entries.load(Ordering::Relaxed),
            last_usn: self.last_usn.load(Ordering::Relaxed),
            progress_permille: self.progress.load(Ordering::Relaxed),
            message: self.message.lock().map(|g| g.clone()).unwrap_or_default(),
        }
    }
}

#[derive(Default)]
pub struct Indexer {
    pub volumes: Vec<Arc<VolumeSlot>>,
    pub shutdown: AtomicBool,
    /// 분석 DB 폴더. None이면 %ProgramData%\Foldy\Analysis (폴더 ACL도 설정한다).
    pub analysis_dir: Option<std::path::PathBuf>,
    /// 색인 스냅샷 폴더. None이면 %ProgramData%\Foldy\Index (폴더 ACL도 설정한다).
    pub index_dir: Option<std::path::PathBuf>,
}

impl Indexer {
    pub fn index_dir(&self) -> std::path::PathBuf {
        self.index_dir.clone().unwrap_or_else(crate::persist::default_dir)
    }

    /// 슬롯 하나를 저장한다. 실패해도 색인에는 영향이 없고 기록만 남긴다.
    pub fn save_slot(&self, slot: &VolumeSlot) {
        let dir = self.index_dir();
        let prepared = if self.index_dir.is_none() { crate::persist::ensure_secure_dir(&dir) } else { std::fs::create_dir_all(&dir) };
        let stem = slot.persist.lock().ok().and_then(|g| g.as_ref().map(|c| c.stem.clone()));
        if let Err(e) = prepared {
            slot.set_message(format!("스냅샷 폴더를 만들 수 없음: {e}"));
            return;
        }
        let Some(stem) = stem else { return };
        let t = std::time::Instant::now();
        match slot.save_snapshot(&crate::persist::snapshot_path(&dir, &stem)) {
            Ok(true) => eprintln!("[{}] 스냅샷 저장 {:?}", slot.name, t.elapsed()),
            Ok(false) => {}
            Err(e) => slot.set_message(format!("스냅샷 저장 실패: {e}")),
        }
    }

    /// 종료 직전에 바뀐 볼륨을 모두 저장한다 (정상 종료 때만 불린다).
    pub fn save_all(&self) {
        for slot in &self.volumes {
            if slot.dirty.load(Ordering::Relaxed) {
                self.save_slot(slot);
            }
        }
    }

    /// 볼륨 이름("C:", "c:\")이나 --scan-dir 경로로 슬롯을 찾는다 (대소문자 무시).
    pub fn find_volume(&self, volume: &str) -> Option<&Arc<VolumeSlot>> {
        let want = foldy_ntfs::fold_name(volume.trim().trim_end_matches(['\\', '/']));
        self.volumes.iter().find(|v| foldy_ntfs::fold_name(v.name.trim_end_matches(['\\', '/'])) == want)
    }

    pub fn analysis_dir(&self) -> std::path::PathBuf {
        self.analysis_dir.clone().unwrap_or_else(crate::analysis::default_dir)
    }
    pub fn status(&self) -> StatusReply {
        let vols: Vec<VolumeStatus> = self.volumes.iter().map(|v| v.status()).collect();
        let indexing: Vec<&VolumeStatus> = vols.iter().filter(|v| v.state == state::INDEXING).collect();
        let overall = if !indexing.is_empty() {
            state::INDEXING
        } else if vols.iter().any(|v| v.state == state::READY) {
            state::READY
        } else {
            state::NOT_READY
        };
        let progress = if indexing.is_empty() {
            if overall == state::READY { 1000 } else { 0 }
        } else {
            (indexing.iter().map(|v| v.progress_permille as u32).sum::<u32>() / indexing.len() as u32) as u16
        };
        StatusReply {
            state: overall,
            indexed_entries: vols.iter().map(|v| v.entries).sum(),
            last_usn: vols.iter().map(|v| v.last_usn).max().unwrap_or(0),
            progress_permille: progress,
            volumes: vols,
            analysis: self
                .volumes
                .iter()
                .filter(|v| v.analysis.state.load(Ordering::Acquire) != analysis_state::IDLE)
                .map(|v| v.analysis_status())
                .collect(),
        }
    }
}

/// 일반 폴더를 열거해 색인을 만든다 (관리자 권한 없이 쓰는 시험·폴백 모드).
/// 루트 노드 id는 0이고 경로는 `root` 그대로다.
pub fn scan_dir(root: &str) -> std::io::Result<VolumeIndex> {
    use foldy_ntfs::Entry;
    use std::os::windows::fs::MetadataExt;
    let meta = std::fs::metadata(root)?;
    if !meta.is_dir() {
        return Err(std::io::Error::new(std::io::ErrorKind::NotADirectory, root.to_string()));
    }
    let mut idx = VolumeIndex::new(root.trim_end_matches(['\\', '/']).to_string(), 0, false);
    idx.insert_build(0, &Entry { names: vec![(0, ".".into())], is_dir: true, ..Default::default() });
    let mut next_id: u32 = 1;
    let mut stack = vec![(std::path::PathBuf::from(root), 0u32)];
    while let Some((dir, id)) = stack.pop() {
        let Ok(rd) = std::fs::read_dir(&dir) else { continue };
        for de in rd.flatten() {
            let Ok(md) = de.metadata() else { continue };
            let child = next_id;
            next_id += 1;
            let is_dir = md.is_dir();
            let size = if is_dir { 0 } else { md.len() };
            idx.insert_build(
                child,
                &Entry {
                    names: vec![(id, de.file_name().to_string_lossy().into_owned())],
                    is_dir,
                    size,
                    alloc: size.div_ceil(4096) * 4096,
                    has_data: !is_dir,
                    mtime: md.last_write_time() as i64,
                    attributes: md.file_attributes(),
                },
            );
            // 심볼릭 링크·정션은 따라가지 않는다 (순환 방지).
            if is_dir && !md.file_type().is_symlink() {
                stack.push((de.path(), child));
            }
        }
    }
    idx.finalize();
    Ok(idx)
}

/// 디렉터리 스캔 슬롯을 백그라운드에서 채운다.
pub fn start_dir_scan(slot: Arc<VolumeSlot>) {
    std::thread::spawn(move || {
        slot.state.store(state::INDEXING, Ordering::Release);
        let t = std::time::Instant::now();
        match scan_dir(&slot.name) {
            Ok(idx) => {
                let n = idx.live_count();
                slot.publish(idx);
                slot.set_message(format!("폴더 색인 완료: {n}개, {:?}", t.elapsed()));
            }
            Err(e) => {
                slot.state.store(state::NOT_READY, Ordering::Release);
                slot.set_message(format!("폴더 색인 실패: {e}"));
            }
        }
    });
}

/// 스냅샷을 주기적으로 저장하는 간격 (바뀐 것이 있을 때만).
const SAVE_INTERVAL: std::time::Duration = std::time::Duration::from_secs(10 * 60);

/// NTFS 볼륨: 가능하면 스냅샷을 불러와 저널을 따라잡고(빠른 시작), 아니면 $MFT를 전부 읽어 구축한 뒤
/// USN 저널을 따라간다. 저널이 끊기면 다시 전부 구축한다.
pub fn start_ntfs(slot: Arc<VolumeSlot>, indexer: Arc<Indexer>) {
    use crate::persist;
    use foldy_ntfs::volume::{self, Volume};
    use std::time::{Duration, Instant};

    std::thread::spawn(move || {
        // 스냅샷 복원은 시작할 때 한 번만 시도한다. 그 뒤 재구축은 항상 전체 구축이다.
        let mut try_restore = true;
        'rebuild: loop {
            if let Ok(mut g) = slot.persist.lock() {
                *g = None;
            }
            let vol = match Volume::open(&slot.name) {
                Ok(v) => v,
                Err(e) => {
                    slot.state.store(state::NOT_READY, Ordering::Release);
                    if e.kind() == std::io::ErrorKind::PermissionDenied {
                        slot.set_message("관리자 권한이 없어 색인하지 않음 (not ready, 클라이언트는 폴백)");
                    } else {
                        slot.set_message(format!("볼륨을 열 수 없음: {e}"));
                    }
                    return;
                }
            };
            let stem = volume::volume_guid(&slot.name).unwrap_or_else(|| format!("serial-{:016x}", vol.boot.serial));
            let snap_path = persist::snapshot_path(&indexer.index_dir(), &stem);
            // 구축 전에 저널 위치를 잡아 두면, 구축하는 동안 생긴 변경도 뒤이어 반영된다.
            let journal = vol.query_journal().ok();
            slot.state.store(state::INDEXING, Ordering::Release);
            slot.progress.store(0, Ordering::Relaxed);
            let t = Instant::now();

            let mut restored: Option<(i64, u64)> = None;
            if std::mem::take(&mut try_restore) && journal.is_some() {
                match std::fs::read(&snap_path) {
                    Ok(bytes) => match persist::restore(&slot, &vol, &bytes, &indexer.shutdown) {
                        Ok(r) => restored = Some(r),
                        Err(why) => slot.set_message(format!("스냅샷을 쓰지 않음 ({why}) - 전체 색인")),
                    },
                    Err(e) if e.kind() == std::io::ErrorKind::NotFound => slot.set_message("스냅샷 없음 - 전체 색인"),
                    Err(e) => slot.set_message(format!("스냅샷을 읽을 수 없음 ({e}) - 전체 색인")),
                }
            }

            let (mut next, journal_id) = if let Some(r) = restored {
                if let Ok(g) = slot.index.read()
                    && let Some(idx) = g.as_ref()
                {
                    slot.entries.store(idx.live_count() as u64, Ordering::Relaxed);
                }
                slot.set_message(format!("스냅샷 복원 완료: {}개, {:?}", slot.entries.load(Ordering::Relaxed), t.elapsed()));
                r
            } else {
                let built = vol.build_index(&indexer.shutdown, |done, total| {
                    let p = (done * 1000).checked_div(total).unwrap_or(0) as u16;
                    slot.progress.store(p.min(999), Ordering::Relaxed);
                });
                let idx = match built {
                    Ok(i) => i,
                    Err(e) => {
                        slot.state.store(state::NOT_READY, Ordering::Release);
                        slot.set_message(format!("MFT 색인 실패: {e}"));
                        return;
                    }
                };
                let n = idx.live_count();
                slot.publish(idx);
                slot.set_message(format!("MFT 색인 완료: {n}개, {:?}", t.elapsed()));
                let Some(j) = journal else {
                    slot.set_message(format!("MFT 색인 완료: {n}개 (USN 저널 없음 - 실시간 갱신 안 됨)"));
                    return;
                };
                slot.last_usn.store(j.next_usn, Ordering::Relaxed);
                (j.next_usn, j.id)
            };

            if let Ok(mut g) = slot.persist.lock() {
                *g = Some(persist::Context { stem, serial: vol.boot.serial, journal_id });
            }
            // 전체 구축 직후에는 바로 저장해 둔다 (다음 시작이 빨라진다). 복원했다면 주기 저장만 한다.
            slot.dirty.store(restored.is_none(), Ordering::Relaxed);
            let mut last_save = Instant::now();
            if restored.is_none() {
                indexer.save_slot(&slot);
            }

            let mut buf = vec![0u8; 256 * 1024];
            loop {
                if indexer.shutdown.load(Ordering::Relaxed) {
                    return;
                }
                match persist::step(&vol, &slot.index, journal_id, &mut next, &mut buf) {
                    Ok(0) => {
                        slot.last_usn.store(next, Ordering::Relaxed);
                        if slot.dirty.load(Ordering::Relaxed) && last_save.elapsed() >= SAVE_INTERVAL {
                            indexer.save_slot(&slot);
                            last_save = Instant::now();
                        }
                        std::thread::sleep(Duration::from_millis(500));
                    }
                    Ok(_) => {
                        if let Ok(g) = slot.index.read()
                            && let Some(idx) = g.as_ref()
                        {
                            slot.entries.store(idx.live_count() as u64, Ordering::Relaxed);
                        }
                        slot.last_usn.store(next, Ordering::Relaxed);
                        slot.dirty.store(true, Ordering::Relaxed);
                    }
                    Err(e)
                        if matches!(
                            e.raw_os_error(),
                            Some(
                                volume::ERROR_JOURNAL_ENTRY_DELETED
                                    | volume::ERROR_JOURNAL_NOT_ACTIVE
                                    | volume::ERROR_JOURNAL_DELETE_IN_PROGRESS
                            )
                        ) =>
                    {
                        slot.set_message(format!("USN 저널 범위를 벗어남 ({e}) - 다시 색인"));
                        continue 'rebuild;
                    }
                    Err(e) => {
                        slot.set_message(format!("USN 읽기 오류: {e}"));
                        std::thread::sleep(Duration::from_secs(5));
                    }
                }
            }
        }
    });
}
