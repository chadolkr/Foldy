//! 작업 레지스트리와 복사 파이프라인 (설계서 "병렬 복사 엔진").
//!
//! 스캐너 → 계획기(충돌 확인) → 작업 큐(대형/소형 두 줄) → 워커 N개(CopyFile2) → 검증기 → 마무리.
//! 워커는 상한만큼 미리 만들고 [`Gate`]로 동시 실행 수만 조절하며, 감시 스레드가 처리량을 재서
//! [`AutoTuner`]로 상한을 바꾼다.

use std::collections::{HashMap, HashSet, VecDeque};
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicBool, AtomicI32, AtomicU32, AtomicU64, AtomicUsize, Ordering::*};
use std::sync::{Arc, LazyLock, Mutex, MutexGuard, OnceLock};
use std::time::{Duration, Instant};

use serde::Serialize;

use crate::ffi::*;
use crate::journal::{Journal, Record, ResumeState};
use crate::plan::{self, Decision};
use crate::scan;
use crate::space;
use crate::spec::{Mode, Spec};
use crate::tuner::{self, AutoTuner, DriveKind, Gate};
use crate::verify;
use crate::win;

#[repr(u32)]
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum JobState {
    Created = 0,
    Running = 1,
    Paused = 2,
    Done = 3,
    Failed = 4,
    Canceled = 5,
    /// 충돌 확인이 필요함 (fc_job_issues → fc_job_resolve). 다른 파일은 계속 복사한다.
    NeedsAttention = 6,
}

impl JobState {
    pub fn is_terminal(v: u32) -> bool {
        v == JobState::Done as u32 || v == JobState::Failed as u32 || v == JobState::Canceled as u32
    }
}

pub const PHASE_PENDING: u32 = 0;
pub const PHASE_SCANNING: u32 = 1;
pub const PHASE_PLANNING: u32 = 2;
pub const PHASE_COPYING: u32 = 3;
pub const PHASE_FINALIZING: u32 = 4;
pub const PHASE_FINISHED: u32 = 5;

pub const RESOLVE_SKIP: u32 = 0;
pub const RESOLVE_OVERWRITE: u32 = 1;
pub const RESOLVE_RENAME: u32 = 2;

fn lock<T>(m: &Mutex<T>) -> MutexGuard<'_, T> {
    // 다른 스레드의 panic으로 잠금이 오염돼도 데이터는 일관적이므로 계속 쓴다.
    m.lock().unwrap_or_else(|p| p.into_inner())
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum Op {
    /// CopyFile2 (+ 이동이면 검증 후 원본 삭제)
    Copy,
    /// 같은 볼륨 이동: MoveFileExW로 경로만 바꿈
    Rename,
}

#[derive(Debug, Clone)]
struct WorkItem {
    src: PathBuf,
    dst: PathBuf,
    size: u64,
    /// 스캔 때 본 원본 수정 시간 (FILETIME). 검증에서 원본을 다시 조회하지 않게.
    mtime: u64,
    /// 이 항목이 나타내는 파일 수 (폴더째 이름 변경이면 여러 개)
    files: u64,
    overwrite: bool,
    op: Op,
    large: bool,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize)]
#[serde(rename_all = "snake_case")]
pub enum IssueKind {
    Conflict,
    Failed,
}

/// 작업 패널의 문제 목록 항목 (모달 대신 여기에 쌓인다). fc_job_issues가 JSON 배열로 내보낸다.
#[derive(Debug, Serialize)]
pub struct Issue {
    pub id: u64,
    pub kind: IssueKind,
    pub source: String,
    pub dest: String,
    pub message: String,
    pub resolved: bool,
    #[serde(skip)]
    item: Option<WorkItem>,
}

/// 대상 폴더별 기존 이름 (소문자 → 폴더인지). 계획 단계에서 폴더마다 한 번만 열거한다.
#[derive(Default)]
struct DestIndex {
    dirs: std::cell::RefCell<HashMap<String, HashMap<String, bool>>>,
}

impl DestIndex {
    fn lookup(&self, p: &Path) -> Option<bool> {
        let (Some(parent), Some(name)) = (p.parent(), p.file_name()) else {
            return std::fs::symlink_metadata(p).ok().map(|m| m.is_dir());
        };
        let mut dirs = self.dirs.borrow_mut();
        let names = dirs.entry(plan::path_key(parent)).or_insert_with(|| {
            let mut m = HashMap::new();
            if let Ok(rd) = std::fs::read_dir(parent) {
                for e in rd.flatten() {
                    let is_dir = e.file_type().is_ok_and(|t| t.is_dir());
                    m.insert(e.file_name().to_string_lossy().to_lowercase(), is_dir);
                }
            }
            m
        });
        names.get(&name.to_string_lossy().to_lowercase()).copied()
    }
}

/// 한 줄의 작업 큐. 계획이 끝난 뒤에는 바뀌지 않으므로 원자 커서 하나로 나눠 가진다.
struct Lane {
    items: Vec<WorkItem>,
    next: AtomicUsize,
}

impl Lane {
    fn new(items: Vec<WorkItem>) -> Self {
        Lane { items, next: AtomicUsize::new(0) }
    }

    fn take(&self) -> Option<&WorkItem> {
        let i = self.next.fetch_add(1, AcqRel);
        self.items.get(i)
    }
}

pub struct Job {
    spec: Spec,
    state: AtomicU32,
    last_error: AtomicI32,
    phase: AtomicU32,
    files_done: AtomicU64,
    files_total: AtomicU64,
    bytes_done: AtomicU64,
    bytes_total: AtomicU64,
    files_failed: AtomicU64,
    files_skipped: AtomicU64,
    bytes_per_sec: AtomicU64,
    best_threads: AtomicU32,
    cancel: AtomicBool,
    paused: AtomicBool,
    /// 시험용 크래시 흉내 (spec._test_abort_after_files)
    abort: AtomicBool,
    current: Mutex<String>,
    issues: Mutex<Vec<Issue>>,
    next_issue: AtomicU64,
    unresolved: AtomicU32,
    /// 사용자가 충돌을 해결한 항목 (워커가 줄이 빈 뒤 가져감)
    late: Mutex<VecDeque<WorkItem>>,
    /// 이 작업에서 쓰기로 한 대상 경로 (소문자)
    claimed: Mutex<HashSet<String>>,
    gate: Gate,
    journal: OnceLock<Journal>,
}

type JobMap = HashMap<u64, Arc<Job>>;
static JOBS: LazyLock<Mutex<JobMap>> = LazyLock::new(Default::default);
static NEXT: AtomicU64 = AtomicU64::new(1);

/// Rust 안에서 쓰는 생성 함수 (벤치·시험). FFI는 [`create`]로 핸들을 받는다.
pub fn new_job(json: &str) -> Result<Arc<Job>, i32> {
    let spec = Spec::parse(json).ok_or(FC_ERR_INVALID_ARG)?;
    Ok(Arc::new(Job::new(spec)))
}

pub fn create(json: &str) -> Result<u64, i32> {
    let job = new_job(json)?;
    let h = NEXT.fetch_add(1, Relaxed);
    lock(&JOBS).insert(h, job);
    Ok(h)
}

pub fn with(handle: u64, f: impl FnOnce(&Arc<Job>) -> i32) -> i32 {
    let job = lock(&JOBS).get(&handle).cloned();
    match job {
        Some(j) => f(&j),
        None => FC_ERR_INVALID_HANDLE,
    }
}

pub fn free(handle: u64) -> i32 {
    let removed = lock(&JOBS).remove(&handle);
    match removed {
        Some(j) => j.cancel(),
        None => FC_ERR_INVALID_HANDLE,
    }
}

pub fn start(job: &Arc<Job>) -> i32 {
    if job
        .state
        .compare_exchange(JobState::Created as u32, JobState::Running as u32, AcqRel, Acquire)
        .is_err()
    {
        return FC_ERR_INVALID_STATE;
    }
    let j = Arc::clone(job);
    let spawned = std::thread::Builder::new().name("foldy-copy-job".into()).spawn(move || {
        let r = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| j.run()));
        let final_state = match r {
            Ok(Ok(())) if j.cancel.load(Acquire) => JobState::Canceled,
            Ok(Ok(())) => {
                if j.files_failed.load(Acquire) > 0 {
                    j.last_error.store(FC_ERR_IO, Release);
                }
                JobState::Done
            }
            Ok(Err(code)) => {
                j.last_error.store(code, Release);
                if j.cancel.load(Acquire) { JobState::Canceled } else { JobState::Failed }
            }
            Err(_) => {
                j.last_error.store(FC_ERR_PANIC, Release);
                JobState::Failed
            }
        };
        j.phase.store(PHASE_FINISHED, Release);
        j.state.store(final_state as u32, Release);
    });
    match spawned {
        Ok(_) => FC_OK,
        Err(_) => {
            job.state.store(JobState::Failed as u32, Release);
            FC_ERR_IO
        }
    }
}

impl Job {
    fn new(spec: Spec) -> Job {
        Job {
            spec,
            state: AtomicU32::new(JobState::Created as u32),
            last_error: AtomicI32::new(FC_OK),
            phase: AtomicU32::new(PHASE_PENDING),
            files_done: AtomicU64::new(0),
            files_total: AtomicU64::new(0),
            bytes_done: AtomicU64::new(0),
            bytes_total: AtomicU64::new(0),
            files_failed: AtomicU64::new(0),
            files_skipped: AtomicU64::new(0),
            bytes_per_sec: AtomicU64::new(0),
            best_threads: AtomicU32::new(0),
            cancel: AtomicBool::new(false),
            paused: AtomicBool::new(false),
            abort: AtomicBool::new(false),
            current: Mutex::new(String::new()),
            issues: Mutex::new(Vec::new()),
            next_issue: AtomicU64::new(1),
            unresolved: AtomicU32::new(0),
            late: Mutex::new(VecDeque::new()),
            claimed: Mutex::new(HashSet::new()),
            gate: Gate::new(1),
            journal: OnceLock::new(),
        }
    }

    pub fn set_paused(&self, p: bool) -> i32 {
        if self.state.load(Acquire) != JobState::Running as u32 {
            return FC_ERR_INVALID_STATE;
        }
        self.paused.store(p, Release);
        FC_OK
    }

    pub fn cancel(&self) -> i32 {
        self.cancel.store(true, Release);
        self.paused.store(false, Release);
        // 시작 전이면 바로 취소 상태로.
        let _ = self.state.compare_exchange(JobState::Created as u32, JobState::Canceled as u32, AcqRel, Acquire);
        FC_OK
    }

    fn stopped(&self) -> bool {
        self.cancel.load(Acquire) || self.abort.load(Acquire)
    }

    /// 보고용 상태: 실행 중이면 일시정지·확인 필요를 반영한다.
    pub fn state(&self) -> u32 {
        let s = self.state.load(Acquire);
        if s == JobState::Running as u32 {
            if self.paused.load(Acquire) {
                return JobState::Paused as u32;
            }
            if self.unresolved.load(Acquire) > 0 {
                return JobState::NeedsAttention as u32;
            }
        }
        s
    }

    pub fn snapshot(&self) -> FcProgress {
        FcProgress {
            state: self.state(),
            last_error: self.last_error.load(Acquire),
            files_done: self.files_done.load(Acquire),
            files_total: self.files_total.load(Acquire),
            bytes_done: self.bytes_done.load(Acquire),
            bytes_total: self.bytes_total.load(Acquire),
            bytes_per_sec: self.bytes_per_sec.load(Acquire),
            files_failed: self.files_failed.load(Acquire),
            files_skipped: self.files_skipped.load(Acquire),
            issues_pending: self.unresolved.load(Acquire),
            threads_active: self.gate.active(),
            thread_limit: self.gate.limit(),
            phase: self.phase.load(Acquire),
        }
    }

    /// 마지막으로 시작한 파일의 원본 전체 경로.
    pub fn current_name(&self) -> String {
        lock(&self.current).clone()
    }

    /// 작업이 끝난 뒤 저장한(또는 고정한) 스레드 수.
    pub fn best_threads(&self) -> u32 {
        self.best_threads.load(Acquire)
    }

    pub fn issues_json(&self) -> String {
        serde_json::to_string(&*lock(&self.issues)).unwrap_or_else(|_| "[]".into())
    }

    /// 충돌 해결. `apply_all`이면 아직 남은 충돌 전체에 같은 처리를 한다.
    pub fn resolve(&self, id: u64, action: u32, apply_all: bool) -> i32 {
        if action > RESOLVE_RENAME {
            return FC_ERR_INVALID_ARG;
        }
        let mut issues = lock(&self.issues);
        let mut claimed = lock(&self.claimed);
        let mut n = 0u32;
        let mut failed = Vec::new();
        for is in issues.iter_mut().filter(|i| i.kind == IssueKind::Conflict && !i.resolved && (apply_all || i.id == id)) {
            let Some(mut item) = is.item.take() else { continue };
            is.resolved = true;
            n += 1;
            match action {
                RESOLVE_SKIP => self.count_skipped(item.size, item.files),
                RESOLVE_OVERWRITE if item.dst.is_dir() => failed.push(item),
                RESOLVE_OVERWRITE => {
                    item.overwrite = true;
                    lock(&self.late).push_back(item);
                }
                _ => {
                    let taken = |p: &Path| claimed.contains(&plan::path_key(p)) || std::fs::symlink_metadata(p).is_ok();
                    item.dst = plan::unique_path(&item.dst, &taken);
                    item.overwrite = false;
                    claimed.insert(plan::path_key(&item.dst));
                    is.dest = item.dst.to_string_lossy().into_owned();
                    lock(&self.late).push_back(item);
                }
            }
        }
        if n == 0 {
            return FC_ERR_NOT_FOUND;
        }
        for item in failed {
            self.files_failed.fetch_add(item.files, AcqRel);
            let id = self.next_issue.fetch_add(1, AcqRel);
            issues.push(Issue {
                id,
                kind: IssueKind::Failed,
                source: item.src.to_string_lossy().into_owned(),
                dest: item.dst.to_string_lossy().into_owned(),
                message: "같은 이름의 폴더가 있어 파일로 덮어쓸 수 없습니다".into(),
                resolved: false,
                item: None,
            });
        }
        // late에 넣은 뒤에 줄여야 워커가 "남은 일 없음"으로 잘못 끝나지 않는다.
        self.unresolved.fetch_sub(n, AcqRel);
        FC_OK
    }

    fn count_skipped(&self, size: u64, files: u64) {
        self.files_skipped.fetch_add(files, AcqRel);
        self.files_done.fetch_add(files, AcqRel);
        self.bytes_done.fetch_add(size, AcqRel);
    }

    fn push_issue(&self, kind: IssueKind, source: &Path, dest: &Path, message: String) {
        let id = self.next_issue.fetch_add(1, AcqRel);
        lock(&self.issues).push(Issue {
            id,
            kind,
            source: source.to_string_lossy().into_owned(),
            dest: dest.to_string_lossy().into_owned(),
            message,
            resolved: false,
            item: None,
        });
    }

    fn fail(&self, it: &WorkItem, message: String) {
        self.files_failed.fetch_add(it.files, AcqRel);
        self.journal_append(Record::Fail { s: it.src.to_string_lossy().into_owned(), e: message.clone() });
        self.push_issue(IssueKind::Failed, &it.src, &it.dst, message);
    }

    fn journal_append(&self, rec: Record) {
        if let Some(j) = self.journal.get() {
            // 저널 쓰기 실패는 복사를 멈추지 않는다 (이어하기만 못 함).
            let _ = j.append(&rec);
        }
    }

    /// 일시정지 중이면 대기한다. 멈춰야 하면 false.
    fn checkpoint(&self) -> bool {
        while self.paused.load(Acquire) && !self.stopped() {
            std::thread::sleep(Duration::from_millis(20));
        }
        !self.stopped()
    }

    fn sleep_interruptible(&self, d: Duration) -> bool {
        let until = Instant::now() + d;
        while Instant::now() < until {
            if self.stopped() {
                return false;
            }
            std::thread::sleep(Duration::from_millis(25));
        }
        self.checkpoint()
    }

    fn stop_result(&self) -> Result<(), i32> {
        if self.abort.load(Acquire) { Err(FC_ERR_ABORTED) } else { Ok(()) }
    }

    /// 쓸 파일(복사 항목)이 대상 드라이브에 들어가는지 본다 (사용자 원칙 2026-11-22: 시작 전에 알린다).
    /// 덮어쓰기·충돌 확인 대기는 지금 파일 자리를 다시 쓰므로 차이만 센다 (건너뛰기를 고르면 더 적게 든다).
    fn check_space(&self, dest_root: &Path, small: &[WorkItem], large: &[WorkItem], asks: &[WorkItem]) -> Result<(), i32> {
        let old = |it: &WorkItem| std::fs::metadata(&it.dst).ok().filter(|m| m.is_file()).map(|m| m.len());
        let mut extra = 0u64;
        let mut biggest: Option<&WorkItem> = None;
        let planned = small.iter().chain(large).map(|it| (it, it.overwrite));
        for (it, replaces) in planned.chain(asks.iter().map(|it| (it, true))) {
            if it.op != Op::Copy {
                continue;
            }
            extra = extra.saturating_add(space::extra(it.size, if replaces { old(it) } else { None }));
            if biggest.is_none_or(|b| it.size > b.size) {
                biggest = Some(it);
            }
        }
        let Some(biggest) = biggest else { return Ok(()) };
        let (free, fs) = win::volume_room(dest_root);
        let free = self.spec.test_free_bytes.or(free);
        match space::judge(extra, biggest.size, free, fs.as_deref()) {
            space::Verdict::Ok => Ok(()),
            space::Verdict::NoSpace { need, free } => {
                self.bytes_total.store(need, Release);
                self.push_issue(IssueKind::Failed, Path::new(""), dest_root, format!("빈 공간이 모자랍니다: {need} 바이트 필요, {free} 바이트 남음"));
                Err(FC_ERR_NO_SPACE)
            }
            space::Verdict::TooBigForFat { size } => {
                self.bytes_total.store(size, Release);
                self.push_issue(IssueKind::Failed, &biggest.src, dest_root, "FAT32 드라이브에는 4GB가 넘는 파일을 쓸 수 없습니다".into());
                Err(FC_ERR_FILE_TOO_BIG)
            }
        }
    }

    fn open_journal(&self) -> Result<ResumeState, i32> {
        let Some(p) = self.spec.journal_path.as_deref() else { return Ok(ResumeState::default()) };
        let p = Path::new(p);
        let (j, st) = if self.spec.resume && p.exists() {
            Journal::open_resume(p).map_err(|_| FC_ERR_IO)?
        } else {
            (Journal::create(p, &self.spec.to_journal_json()).map_err(|_| FC_ERR_IO)?, ResumeState::default())
        };
        let _ = self.journal.set(j);
        Ok(st)
    }

    fn run(&self) -> Result<(), i32> {
        // 1) 스캐너
        self.phase.store(PHASE_SCANNING, Release);
        let scan = scan::scan(&self.spec.sources, &|| self.stopped())?;
        if self.stopped() {
            return self.stop_result();
        }
        let dest_root = std::path::absolute(&self.spec.dest_dir).map_err(|_| FC_ERR_INVALID_ARG)?;
        if scan.tops.iter().any(|t| t.is_dir && plan::path_is_within(&dest_root, &t.src)) {
            return Err(FC_ERR_INVALID_ARG); // 폴더를 자기 안으로 복사
        }
        self.files_total.store((scan.files.len() + scan.errors.len()) as u64, Release);
        self.bytes_total.store(scan.bytes_total(), Release);
        let resume = self.open_journal()?;
        for (p, e) in &scan.errors {
            self.files_failed.fetch_add(1, AcqRel);
            self.push_issue(IssueKind::Failed, p, Path::new(""), format!("폴더를 읽지 못했습니다: {e}"));
        }

        // 2) 계획기
        self.phase.store(PHASE_PLANNING, Release);
        let moving = self.spec.mode == Mode::Move;
        let fast_move: Vec<bool> = scan
            .tops
            .iter()
            .map(|t| moving && !self.spec.move_by_copy && win::same_volume(&t.src, &dest_root))
            .collect();
        let exists_now = |p: &Path| std::fs::symlink_metadata(p).ok().map(|m| m.is_dir());
        let mut claimed = HashSet::new();
        let mut whole = vec![false; scan.tops.len()];
        let mut small = Vec::new();
        let mut large = Vec::new();
        let mut asks = Vec::new();
        // 같은 볼륨 이동: 대상에 같은 이름이 없으면 폴더째 경로만 바꾼다 (즉시).
        for (i, t) in scan.tops.iter().enumerate() {
            let dst = dest_root.join(&t.name);
            if fast_move[i] && t.is_dir && exists_now(&dst).is_none() && claimed.insert(plan::path_key(&dst)) {
                whole[i] = true;
                small.push(WorkItem {
                    src: t.src.clone(),
                    dst,
                    size: t.bytes,
                    mtime: 0,
                    files: t.files,
                    overwrite: false,
                    op: Op::Rename,
                    large: false,
                });
            }
        }
        // 대상 폴더마다 한 번만 열거해 충돌을 확인한다 (파일마다 여는 것보다 훨씬 싸다).
        let index = DestIndex::default();
        let existing = |p: &Path| index.lookup(p);
        for f in scan.files.iter().filter(|f| !whole[f.top]) {
            if self.stopped() {
                return self.stop_result();
            }
            let key = f.src.to_string_lossy().into_owned();
            // 이어하기: 지난번에 끝낸 파일이 그대로면 건너뛴다 (크기 + 시간).
            if let Some(d) = resume.done.get(&key).map(PathBuf::from)
                && verify::matches(f.size, f.mtime, &d)
            {
                claimed.insert(plan::path_key(&d));
                self.count_skipped(f.size, 1);
                continue;
            }
            let op = if fast_move[f.top] { Op::Rename } else { Op::Copy };
            let mut item = WorkItem {
                src: f.src.clone(),
                dst: dest_root.join(&f.rel),
                size: f.size,
                mtime: win::filetime_of(f.mtime),
                files: 1,
                overwrite: false,
                op,
                large: op == Op::Copy && plan::is_large(f.size, self.spec.large_threshold),
            };
            // 지난번에 쓰다 만 우리 파일은 묻지 않고 다시 쓴다.
            if let Some(d) = resume.begun.get(&key) {
                item.dst = PathBuf::from(d);
                item.overwrite = existing(&item.dst) == Some(false);
                claimed.insert(plan::path_key(&item.dst));
            } else {
                match plan::decide(self.spec.conflict, &item.dst, &mut claimed, &existing) {
                    Decision::Go { dst, overwrite } => {
                        item.dst = dst;
                        item.overwrite = overwrite;
                    }
                    Decision::Skip => {
                        self.count_skipped(f.size, 1);
                        continue;
                    }
                    Decision::Ask => {
                        asks.push(item);
                        continue;
                    }
                    Decision::Fail(m) => {
                        self.fail(&item, m);
                        continue;
                    }
                }
            }
            if item.large { large.push(item) } else { small.push(item) }
        }
        // 2.5) 공간 확인: 한 바이트도 쓰기 전에 (폴더도 만들기 전에) 본다. 모자라면 아무것도 바꾸지 않고 멈춘다.
        self.check_space(&dest_root, &small, &large, &asks)?;
        std::fs::create_dir_all(&dest_root).map_err(|_| FC_ERR_IO)?;
        // 폴더는 먼저 순서대로 만든다 (실패하면 그 안의 파일이 실패 목록으로 간다).
        for d in scan.dirs.iter().filter(|d| !whole[d.top]) {
            let _ = std::fs::create_dir_all(dest_root.join(&d.rel));
        }
        // 큰 것부터 (끝 무렵 큰 파일 하나만 남아 늘어지지 않게)
        large.sort_by_key(|i| std::cmp::Reverse(i.size));
        *lock(&self.claimed) = claimed;
        if !asks.is_empty() {
            let mut issues = lock(&self.issues);
            self.unresolved.fetch_add(asks.len() as u32, AcqRel);
            for it in asks {
                let id = self.next_issue.fetch_add(1, AcqRel);
                issues.push(Issue {
                    id,
                    kind: IssueKind::Conflict,
                    source: it.src.to_string_lossy().into_owned(),
                    dest: it.dst.to_string_lossy().into_owned(),
                    message: "대상에 같은 이름이 있습니다".into(),
                    resolved: false,
                    item: Some(it),
                });
            }
        }

        // 3) 스레드 수: 수동 고정 또는 드라이브 조합별 자동 조절
        let first_src = scan.tops.first().map(|t| t.src.clone()).unwrap_or_default();
        let (src_root, src_kind) = win::drive_kind(&first_src);
        let (dst_root, dst_kind) = win::drive_kind(&dest_root);
        let (start, max) = tuner::bounds(src_kind, dst_kind);
        let pair = tuner::pair_key(&src_root, src_kind, &dst_root, dst_kind);
        let tuning_path = self.spec.tuning_path.as_ref().map(PathBuf::from);
        let (auto, workers) = if self.spec.threads > 0 {
            (None, self.spec.threads.min(64))
        } else {
            let learned = tuning_path.as_deref().and_then(|p| tuner::load_best(p, &pair));
            (Some(AutoTuner::new(learned.unwrap_or(start), 1, max)), max)
        };
        self.gate.set_limit(auto.as_ref().map_or(workers, AutoTuner::current));
        let hdd = src_kind == DriveKind::Hdd || dst_kind == DriveKind::Hdd;
        let small_lane = Lane::new(small);
        let large_lane = Lane::new(large);
        let large_workers = if large_lane.items.is_empty() { 0 } else if hdd { 1 } else { 2 };
        let interval = Duration::from_millis(self.spec.tune_interval_ms.unwrap_or(2000).max(100));

        // 4) 워커
        self.phase.store(PHASE_COPYING, Release);
        let finished = AtomicBool::new(false);
        let (best, panicked) = std::thread::scope(|s| {
            let mut hs = Vec::new();
            for _ in 0..workers.max(1) {
                hs.push(s.spawn(|| self.worker(&small_lane, None)));
            }
            for _ in 0..large_workers {
                hs.push(s.spawn(|| self.worker(&small_lane, Some(&large_lane))));
            }
            let fin = &finished;
            let mon = s.spawn(move || self.monitor(fin, auto, interval));
            let mut panicked = false;
            for h in hs {
                panicked |= h.join().is_err();
            }
            finished.store(true, Release);
            (mon.join().unwrap_or(None), panicked)
        });
        if panicked {
            return Err(FC_ERR_PANIC);
        }
        if self.stopped() {
            return self.stop_result();
        }

        // 5) 마무리: 폴더 시간 복원(깊은 것부터), 이동이면 빈 원본 폴더 삭제, 학습값 저장, 저널 닫기
        self.phase.store(PHASE_FINALIZING, Release);
        for d in scan.dirs.iter().rev().filter(|d| !whole[d.top]) {
            let _ = win::copy_dir_times(&d.src, &dest_root.join(&d.rel));
        }
        if moving {
            for d in scan.dirs.iter().rev().filter(|d| !whole[d.top]) {
                let _ = std::fs::remove_dir(&d.src); // 남은 파일(건너뜀·실패)이 있으면 지워지지 않는다
            }
        }
        match (best, tuning_path) {
            (Some(b), Some(p)) => {
                let _ = tuner::save_best(&p, &pair, b);
                self.best_threads.store(b, Release);
            }
            (Some(b), None) => self.best_threads.store(b, Release),
            _ => self.best_threads.store(workers, Release),
        }
        self.journal_append(Record::End);
        if self.files_failed.load(Acquire) == 0
            && let Some(p) = &self.spec.journal_path
        {
            // std는 FILE_SHARE_DELETE로 열기 때문에 핸들이 열린 채로도 지울 수 있다.
            let _ = std::fs::remove_file(p);
        }
        Ok(())
    }

    fn take_late(&self) -> Option<WorkItem> {
        lock(&self.late).pop_front()
    }

    fn worker(&self, small: &Lane, large: Option<&Lane>) {
        loop {
            if !self.checkpoint() {
                return;
            }
            let Some(permit) = self.gate.acquire(&|| self.stopped()) else { return };
            let late;
            let item = if let Some(i) = large.and_then(Lane::take).or_else(|| small.take()) {
                i
            } else if let Some(i) = self.take_late() {
                late = i;
                &late
            } else {
                drop(permit);
                // 사용자 확인을 기다리는 충돌이 있으면 해결될 때까지 남는다.
                if self.unresolved.load(Acquire) == 0 && lock(&self.late).is_empty() {
                    return;
                }
                std::thread::sleep(Duration::from_millis(20));
                continue;
            };
            self.process(item);
            drop(permit);
        }
    }

    fn process(&self, it: &WorkItem) {
        *lock(&self.current) = it.src.to_string_lossy().into_owned();
        let s = it.src.to_string_lossy().into_owned();
        let d = it.dst.to_string_lossy().into_owned();
        self.journal_append(Record::Begin { s: s.clone(), d: d.clone() });
        // 재시도는 1초 뒤 1회, 그래도 실패하면 실패 목록에 넣고 계속 (무한 대기 없음).
        let mut retried = false;
        let result = loop {
            match self.execute(it) {
                Ok(()) => break Ok(()),
                Err(_) if self.stopped() => return,
                Err(_) if !retried => {
                    retried = true;
                    if !self.sleep_interruptible(Duration::from_secs(1)) {
                        return;
                    }
                }
                Err(e) => break Err(e),
            }
        };
        if let Err(e) = result {
            self.fail(it, e);
            return;
        }
        // 이동은 대상 기록과 검증이 끝난 파일만 원본을 지운다.
        if self.spec.mode == Mode::Move
            && it.op == Op::Copy
            && let Err(e) = win::remove_file_force(&it.src)
        {
            self.journal_append(Record::Done { s, d });
            self.files_failed.fetch_add(it.files, AcqRel);
            self.push_issue(IssueKind::Failed, &it.src, &it.dst, format!("복사는 끝났지만 원본을 지우지 못했습니다: {e}"));
            return;
        }
        self.journal_append(Record::Done { s, d });
        let done = self.files_done.fetch_add(it.files, AcqRel) + it.files;
        if self.spec.test_abort_after_files.is_some_and(|n| done >= n) {
            self.abort.store(true, Release);
        }
    }

    fn execute(&self, it: &WorkItem) -> Result<(), String> {
        if it.overwrite {
            win::clear_readonly(&it.dst);
        }
        if it.op == Op::Rename {
            win::move_file(&it.src, &it.dst, it.overwrite).map_err(win::error_text)?;
            self.bytes_done.fetch_add(it.size, AcqRel);
            return Ok(());
        }
        let mut flags = 0;
        if it.large {
            flags |= win::COPY_FILE_NO_BUFFERING;
        }
        if !it.overwrite {
            flags |= win::COPY_FILE_FAIL_IF_EXISTS;
        }
        let stop = || self.stopped();
        let ctx = win::CopyCtx { stop: &stop, paused: &self.paused, bytes_done: &self.bytes_done, transferred: AtomicU64::new(0) };
        let r = win::copy_file(&it.src, &it.dst, flags, &ctx);
        let counted = ctx.transferred.load(Acquire);
        if let Err(code) = r {
            self.bytes_done.fetch_sub(counted, AcqRel);
            return Err(if code == win::ERROR_REQUEST_ABORTED { "취소됨".into() } else { win::error_text(code) });
        }
        // 0바이트 파일처럼 콜백이 다 세지 못한 부분을 맞춘다.
        let counted = if counted < it.size {
            self.bytes_done.fetch_add(it.size - counted, AcqRel);
            it.size
        } else {
            counted
        };
        if let Err(e) = verify::verify_expected(&it.src, &it.dst, self.spec.verify, Some((it.size, it.mtime))) {
            self.bytes_done.fetch_sub(counted, AcqRel);
            let _ = win::remove_file_force(&it.dst); // 우리가 방금 만든 잘못된 사본
            return Err(e);
        }
        Ok(())
    }

    /// 처리량 측정(0.5초마다 바이트/초)과 자동 조절(측정 창마다). 학습한 최적값을 돌려준다.
    fn monitor(&self, finished: &AtomicBool, mut auto: Option<AutoTuner>, interval: Duration) -> Option<u32> {
        let mut rate_t = Instant::now();
        let mut rate_b = self.bytes_done.load(Acquire);
        let mut win_t = Instant::now();
        let mut win_b = rate_b;
        let mut win_f = self.files_done.load(Acquire);
        let mut ema = 0f64;
        while !finished.load(Acquire) {
            std::thread::sleep(Duration::from_millis(100));
            let now = Instant::now();
            let b = self.bytes_done.load(Acquire);
            let f = self.files_done.load(Acquire);
            let dt = now - rate_t;
            if dt >= Duration::from_millis(500) {
                let r = b.saturating_sub(rate_b) as f64 / dt.as_secs_f64();
                ema = if ema == 0.0 { r } else { 0.6 * r + 0.4 * ema };
                self.bytes_per_sec.store(ema as u64, Release);
                rate_t = now;
                rate_b = b;
            }
            if self.paused.load(Acquire) {
                // 일시정지 구간은 측정에서 뺀다
                (win_t, win_b, win_f) = (now, b, f);
                continue;
            }
            if let Some(t) = auto.as_mut()
                && now - win_t >= interval
            {
                let sc = tuner::score(b.saturating_sub(win_b), f.saturating_sub(win_f), (now - win_t).as_secs_f64());
                self.gate.set_limit(t.observe(sc));
                (win_t, win_b, win_f) = (now, b, f);
            }
        }
        auto.map(|t| t.best())
    }
}

#[cfg(test)]
mod tests;
