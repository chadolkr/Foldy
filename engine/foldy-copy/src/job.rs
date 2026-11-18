use std::collections::HashMap;
use std::fs::File;
use std::io::{Read, Write};
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicBool, AtomicI32, AtomicU32, AtomicU64, Ordering::*};
use std::sync::{Arc, LazyLock, Mutex, MutexGuard};
use std::time::Duration;

use serde::Deserialize;

use crate::ffi::*;

#[repr(u32)]
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum JobState {
    Created = 0,
    Running = 1,
    Paused = 2,
    Done = 3,
    Failed = 4,
    Canceled = 5,
}

#[derive(Debug, Deserialize)]
pub struct Spec {
    pub sources: Vec<String>,
    pub dest_dir: String,
}

pub struct Job {
    spec: Spec,
    state: AtomicU32,
    last_error: AtomicI32,
    files_done: AtomicU64,
    files_total: AtomicU64,
    bytes_done: AtomicU64,
    bytes_total: AtomicU64,
    cancel: AtomicBool,
    paused: AtomicBool,
}

type JobMap = HashMap<u64, Arc<Job>>;
static JOBS: LazyLock<Mutex<JobMap>> = LazyLock::new(Default::default);
static NEXT: AtomicU64 = AtomicU64::new(1);

fn jobs() -> MutexGuard<'static, JobMap> {
    // 다른 스레드의 panic으로 잠금이 오염돼도 맵 자체는 일관적이므로 계속 쓴다.
    JOBS.lock().unwrap_or_else(|p| p.into_inner())
}

pub fn create(json: &str) -> Result<u64, i32> {
    let spec: Spec = serde_json::from_str(json).map_err(|_| FC_ERR_INVALID_ARG)?;
    if spec.dest_dir.is_empty() {
        return Err(FC_ERR_INVALID_ARG);
    }
    let job = Arc::new(Job {
        spec,
        state: AtomicU32::new(JobState::Created as u32),
        last_error: AtomicI32::new(FC_OK),
        files_done: AtomicU64::new(0),
        files_total: AtomicU64::new(0),
        bytes_done: AtomicU64::new(0),
        bytes_total: AtomicU64::new(0),
        cancel: AtomicBool::new(false),
        paused: AtomicBool::new(false),
    });
    let h = NEXT.fetch_add(1, Relaxed);
    jobs().insert(h, job);
    Ok(h)
}

pub fn with(handle: u64, f: impl FnOnce(&Arc<Job>) -> i32) -> i32 {
    let job = jobs().get(&handle).cloned();
    match job {
        Some(j) => f(&j),
        None => FC_ERR_INVALID_HANDLE,
    }
}

pub fn free(handle: u64) -> i32 {
    let removed = jobs().remove(&handle);
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
    let spawned = std::thread::Builder::new().name("foldy-copy-worker".into()).spawn(move || {
        let r = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| j.run()));
        let final_state = match r {
            Ok(Ok(())) if j.cancel.load(Acquire) => JobState::Canceled,
            Ok(Ok(())) => JobState::Done,
            Ok(Err(code)) => {
                j.last_error.store(code, Release);
                if j.cancel.load(Acquire) { JobState::Canceled } else { JobState::Failed }
            }
            Err(_) => {
                j.last_error.store(FC_ERR_PANIC, Release);
                JobState::Failed
            }
        };
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
    pub fn set_paused(&self, p: bool) -> i32 {
        let s = self.state.load(Acquire);
        if s != JobState::Running as u32 && s != JobState::Paused as u32 {
            return FC_ERR_INVALID_STATE;
        }
        self.paused.store(p, Release);
        let (from, to) = if p {
            (JobState::Running, JobState::Paused)
        } else {
            (JobState::Paused, JobState::Running)
        };
        let _ = self.state.compare_exchange(from as u32, to as u32, AcqRel, Acquire);
        FC_OK
    }

    pub fn cancel(&self) -> i32 {
        self.cancel.store(true, Release);
        self.paused.store(false, Release);
        // 시작 전이면 바로 취소 상태로.
        let _ = self.state.compare_exchange(JobState::Created as u32, JobState::Canceled as u32, AcqRel, Acquire);
        FC_OK
    }

    pub fn snapshot(&self) -> FcProgress {
        FcProgress {
            state: self.state.load(Acquire),
            last_error: self.last_error.load(Acquire),
            files_done: self.files_done.load(Acquire),
            files_total: self.files_total.load(Acquire),
            bytes_done: self.bytes_done.load(Acquire),
            bytes_total: self.bytes_total.load(Acquire),
        }
    }

    /// 일시정지 중이면 대기한다. 취소되면 false.
    fn checkpoint(&self) -> bool {
        while self.paused.load(Acquire) && !self.cancel.load(Acquire) {
            std::thread::sleep(Duration::from_millis(20));
        }
        !self.cancel.load(Acquire)
    }

    fn run(&self) -> Result<(), i32> {
        let mut total = 0u64;
        for s in &self.spec.sources {
            let m = std::fs::metadata(s).map_err(|_| FC_ERR_IO)?;
            if !m.is_file() {
                return Err(FC_ERR_INVALID_ARG);
            }
            total += m.len();
        }
        self.files_total.store(self.spec.sources.len() as u64, Release);
        self.bytes_total.store(total, Release);
        let dest = PathBuf::from(&self.spec.dest_dir);
        std::fs::create_dir_all(&dest).map_err(|_| FC_ERR_IO)?;

        let mut buf = vec![0u8; 1 << 20];
        for s in &self.spec.sources {
            if !self.checkpoint() {
                return Ok(());
            }
            let src = Path::new(s);
            let name = src.file_name().ok_or(FC_ERR_INVALID_ARG)?;
            let mut r = File::open(src).map_err(|_| FC_ERR_IO)?;
            let mut w = File::create(dest.join(name)).map_err(|_| FC_ERR_IO)?;
            loop {
                if !self.checkpoint() {
                    return Ok(());
                }
                let n = r.read(&mut buf).map_err(|_| FC_ERR_IO)?;
                if n == 0 {
                    break;
                }
                w.write_all(&buf[..n]).map_err(|_| FC_ERR_IO)?;
                self.bytes_done.fetch_add(n as u64, AcqRel);
            }
            w.flush().map_err(|_| FC_ERR_IO)?;
            self.files_done.fetch_add(1, AcqRel);
        }
        Ok(())
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn create_job(json: &str) -> (i32, u64) {
        let w: Vec<u16> = json.encode_utf16().collect();
        let mut h = 0u64;
        // SAFETY: w와 h는 이 스코프에서 유효하다.
        let rc = unsafe { fc_job_create(w.as_ptr(), w.len(), &mut h) };
        (rc, h)
    }

    fn wait(h: u64) -> FcProgress {
        let mut p = FcProgress::default();
        for _ in 0..500 {
            // SAFETY: p는 유효한 지역 변수.
            assert_eq!(unsafe { fc_job_poll(h, &mut p) }, FC_OK);
            if p.state >= JobState::Done as u32 {
                return p;
            }
            std::thread::sleep(Duration::from_millis(10));
        }
        panic!("job did not finish: {p:?}");
    }

    #[test]
    fn copies_files() {
        let dir = std::env::temp_dir().join(format!("foldy-copy-test-{}", std::process::id()));
        let src = dir.join("src");
        let dst = dir.join("dst");
        std::fs::create_dir_all(&src).unwrap();
        let a = src.join("a.bin");
        std::fs::write(&a, vec![7u8; 3 * 1024 * 1024 + 5]).unwrap();
        let json = serde_json::json!({"sources": [a], "dest_dir": dst}).to_string();
        let (rc, h) = create_job(&json);
        assert_eq!(rc, FC_OK);
        assert_eq!(fc_job_start(h), FC_OK);
        let p = wait(h);
        assert_eq!(p.state, JobState::Done as u32, "{p:?}");
        assert_eq!(p.bytes_done, p.bytes_total);
        assert_eq!(std::fs::read(dst.join("a.bin")).unwrap().len(), 3 * 1024 * 1024 + 5);
        assert_eq!(fc_job_free(h), FC_OK);
        assert_eq!(fc_job_free(h), FC_ERR_INVALID_HANDLE);
        let _ = std::fs::remove_dir_all(dir);
    }

    #[test]
    fn bad_input_returns_codes() {
        assert_eq!(create_job("not json").0, FC_ERR_INVALID_ARG);
        assert_eq!(fc_job_start(999_999), FC_ERR_INVALID_HANDLE);
        // SAFETY: null 출력 포인터는 함수가 검사한다.
        assert_eq!(unsafe { fc_job_poll(1, std::ptr::null_mut()) }, FC_ERR_INVALID_ARG);
        assert_eq!(fc_selftest_panic(), FC_ERR_PANIC);
        let (rc, h) = create_job(r#"{"sources":["Z:\\definitely\\missing.bin"],"dest_dir":"C:\\x"}"#);
        assert_eq!(rc, FC_OK);
        assert_eq!(fc_job_start(h), FC_OK);
        let p = wait(h);
        assert_eq!(p.state, JobState::Failed as u32);
        assert_eq!(p.last_error, FC_ERR_IO);
        fc_job_free(h);
    }
}
