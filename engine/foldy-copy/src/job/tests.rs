//! 파이프라인 전체 시험 (FFI 경계를 그대로 통과한다).

use std::path::{Path, PathBuf};
use std::time::{Duration, SystemTime};

use serde_json::{Value, json};

use super::*;

struct Tmp(PathBuf);

impl Tmp {
    fn new(name: &str) -> Tmp {
        let p = std::env::temp_dir().join(format!("foldy-copy-{name}-{}", std::process::id()));
        let _ = std::fs::remove_dir_all(&p);
        std::fs::create_dir_all(&p).unwrap();
        Tmp(p)
    }
}

impl Drop for Tmp {
    fn drop(&mut self) {
        let _ = std::fs::remove_dir_all(&self.0);
    }
}

fn create_job(json: &str) -> (i32, u64) {
    let w: Vec<u16> = json.encode_utf16().collect();
    let mut h = 0u64;
    // SAFETY: w와 h는 이 스코프에서 유효하다.
    let rc = unsafe { fc_job_create(w.as_ptr(), w.len(), &mut h) };
    (rc, h)
}

fn poll(h: u64) -> FcProgress {
    let mut p = FcProgress::default();
    // SAFETY: p는 유효한 지역 변수.
    assert_eq!(unsafe { fc_job_poll(h, &mut p) }, FC_OK);
    p
}

fn wait_until(h: u64, cond: impl Fn(&FcProgress) -> bool) -> FcProgress {
    for _ in 0..3000 {
        let p = poll(h);
        if cond(&p) {
            return p;
        }
        std::thread::sleep(Duration::from_millis(10));
    }
    panic!("timeout: {:?}", poll(h));
}

fn wait(h: u64) -> FcProgress {
    wait_until(h, |p| JobState::is_terminal(p.state))
}

fn run(spec: Value) -> (u64, FcProgress) {
    let (rc, h) = create_job(&spec.to_string());
    assert_eq!(rc, FC_OK);
    assert_eq!(fc_job_start(h), FC_OK);
    (h, wait(h))
}

fn issues(h: u64) -> Vec<Value> {
    let mut len = 0usize;
    // SAFETY: 길이만 묻는다 (buf null).
    let rc = unsafe { fc_job_issues(h, std::ptr::null_mut(), 0, &mut len) };
    if len == 0 {
        return vec![];
    }
    assert_eq!(rc, FC_ERR_BUFFER_TOO_SMALL);
    let mut buf = vec![0u16; len];
    // SAFETY: buf는 len개의 u16을 쓸 수 있다.
    assert_eq!(unsafe { fc_job_issues(h, buf.as_mut_ptr(), buf.len(), &mut len) }, FC_OK);
    serde_json::from_str::<Vec<Value>>(&String::from_utf16(&buf[..len]).unwrap()).unwrap()
}

fn mtime(p: &Path) -> SystemTime {
    std::fs::metadata(p).unwrap().modified().unwrap()
}

#[test]
fn copies_files() {
    let t = Tmp::new("files");
    let src = t.0.join("src");
    let dst = t.0.join("dst");
    std::fs::create_dir_all(&src).unwrap();
    let a = src.join("a.bin");
    std::fs::write(&a, vec![7u8; 3 * 1024 * 1024 + 5]).unwrap();
    let (h, p) = run(json!({"sources": [a], "dest_dir": dst}));
    assert_eq!(p.state, JobState::Done as u32, "{p:?}");
    assert_eq!(p.bytes_done, p.bytes_total);
    assert_eq!(p.phase, PHASE_FINISHED);
    assert_eq!(std::fs::read(dst.join("a.bin")).unwrap().len(), 3 * 1024 * 1024 + 5);
    assert_eq!(fc_job_free(h), FC_OK);
    assert_eq!(fc_job_free(h), FC_ERR_INVALID_HANDLE);
}

#[test]
fn bad_input_returns_codes() {
    assert_eq!(create_job("not json").0, FC_ERR_INVALID_ARG);
    assert_eq!(create_job(r#"{"sources":[],"dest_dir":"x","mode":"teleport"}"#).0, FC_ERR_INVALID_ARG);
    assert_eq!(fc_job_start(999_999), FC_ERR_INVALID_HANDLE);
    assert_eq!(fc_job_resolve(999_999, 1, 0, 0), FC_ERR_INVALID_HANDLE);
    // SAFETY: null 출력 포인터는 함수가 검사한다.
    assert_eq!(unsafe { fc_job_poll(1, std::ptr::null_mut()) }, FC_ERR_INVALID_ARG);
    assert_eq!(fc_selftest_panic(), FC_ERR_PANIC);
    let (rc, h) = create_job(r#"{"sources":["Z:\\definitely\\missing.bin"],"dest_dir":"C:\\x"}"#);
    assert_eq!(rc, FC_OK);
    assert_eq!(fc_job_start(h), FC_OK);
    assert_eq!(fc_job_start(h), FC_ERR_INVALID_STATE);
    let p = wait(h);
    assert_eq!(p.state, JobState::Failed as u32);
    assert_eq!(p.last_error, FC_ERR_IO);
    fc_job_free(h);
}

#[test]
fn copies_tree_with_korean_and_long_paths_and_restores_folder_times() {
    let t = Tmp::new("tree");
    let src = t.0.join("원본 폴더");
    // 260자를 넘는 깊은 경로
    let mut deep = src.clone();
    for i in 0..6 {
        deep = deep.join(format!("{i}-{}", "가".repeat(50)));
    }
    std::fs::create_dir_all(&deep).unwrap();
    assert!(deep.as_os_str().len() > 260);
    std::fs::write(deep.join("깊은 파일 😀.txt"), "깊다").unwrap();
    std::fs::write(src.join("a.txt"), "a").unwrap();
    std::fs::write(src.join("빈 파일"), "").unwrap();
    std::fs::create_dir_all(src.join("빈 폴더")).unwrap();
    // 폴더 시간을 과거로 돌려 놓고 복원되는지 본다
    let old = SystemTime::now() - Duration::from_secs(86_400 * 30);
    for d in [src.join("빈 폴더"), src.clone()] {
        set_dir_mtime(&d, old);
    }
    let dst = t.0.join("대상");
    let (_, p) = run(json!({"sources": [src], "dest_dir": dst, "verify": "hash"}));
    assert_eq!(p.state, JobState::Done as u32, "{p:?}");
    assert_eq!((p.files_done, p.files_total, p.files_failed), (3, 3, 0));
    let out = dst.join("원본 폴더");
    let rel = deep.strip_prefix(&src).unwrap();
    assert_eq!(std::fs::read_to_string(out.join(rel).join("깊은 파일 😀.txt")).unwrap(), "깊다");
    assert_eq!(std::fs::metadata(out.join("빈 파일")).unwrap().len(), 0);
    assert!(out.join("빈 폴더").is_dir());
    assert!(verify::times_close(mtime(&out.join("빈 폴더")), old), "폴더 시간 복원");
    assert!(verify::times_close(mtime(&out), old));
}

fn set_dir_mtime(d: &Path, t: SystemTime) {
    use std::os::windows::fs::OpenOptionsExt;
    use windows_sys::Win32::Storage::FileSystem::{FILE_FLAG_BACKUP_SEMANTICS, FILE_WRITE_ATTRIBUTES};
    let f = std::fs::OpenOptions::new()
        .access_mode(FILE_WRITE_ATTRIBUTES)
        .custom_flags(FILE_FLAG_BACKUP_SEMANTICS)
        .open(d)
        .unwrap();
    f.set_modified(t).unwrap();
}

fn conflict_setup(t: &Tmp) -> (PathBuf, PathBuf) {
    let src = t.0.join("s");
    let dst = t.0.join("d");
    std::fs::create_dir_all(src.join("sub")).unwrap();
    std::fs::create_dir_all(dst.join("s").join("sub")).unwrap();
    std::fs::write(src.join("x.txt"), "new").unwrap();
    std::fs::write(src.join("sub").join("y.txt"), "new-y").unwrap();
    std::fs::write(src.join("free.txt"), "free").unwrap();
    std::fs::write(dst.join("s").join("x.txt"), "old").unwrap();
    std::fs::write(dst.join("s").join("sub").join("y.txt"), "old-y").unwrap();
    (src, dst)
}

#[test]
fn conflict_policies_skip_overwrite_rename() {
    let read = |p: PathBuf| std::fs::read_to_string(p).unwrap();
    {
        let t = Tmp::new("skip");
        let (src, dst) = conflict_setup(&t);
        let (_, p) = run(json!({"sources": [src], "dest_dir": dst, "conflict": "skip"}));
        assert_eq!((p.state, p.files_done, p.files_skipped), (JobState::Done as u32, 3, 2));
        assert_eq!(read(dst.join("s").join("x.txt")), "old");
        assert_eq!(read(dst.join("s").join("free.txt")), "free");
    }
    {
        let t = Tmp::new("overwrite");
        let (src, dst) = conflict_setup(&t);
        // 읽기 전용 대상도 덮어쓴다
        let ro = dst.join("s").join("x.txt");
        let mut perm = std::fs::metadata(&ro).unwrap().permissions();
        perm.set_readonly(true);
        std::fs::set_permissions(&ro, perm).unwrap();
        let (_, p) = run(json!({"sources": [src], "dest_dir": dst, "conflict": "overwrite"}));
        assert_eq!((p.state, p.files_done, p.files_failed), (JobState::Done as u32, 3, 0), "{:?}", p);
        assert_eq!(read(ro), "new");
        assert_eq!(read(dst.join("s").join("sub").join("y.txt")), "new-y");
    }
    {
        let t = Tmp::new("rename");
        let (src, dst) = conflict_setup(&t);
        let (_, p) = run(json!({"sources": [src], "dest_dir": dst, "conflict": "rename"}));
        assert_eq!(p.state, JobState::Done as u32);
        assert_eq!(read(dst.join("s").join("x.txt")), "old");
        assert_eq!(read(dst.join("s").join("x (2).txt")), "new");
        assert_eq!(read(dst.join("s").join("sub").join("y (2).txt")), "new-y");
    }
}

#[test]
fn ask_conflicts_wait_for_resolution() {
    let t = Tmp::new("ask");
    let (src, dst) = conflict_setup(&t);
    let (rc, h) = create_job(&json!({"sources": [src], "dest_dir": dst}).to_string());
    assert_eq!(rc, FC_OK);
    assert_eq!(fc_job_start(h), FC_OK);
    let p = wait_until(h, |p| p.state == JobState::NeedsAttention as u32 && p.files_done == 1);
    assert_eq!(p.issues_pending, 2);
    let list = issues(h);
    assert_eq!(list.len(), 2);
    assert!(list.iter().all(|i| i["kind"] == "conflict" && i["resolved"] == false));
    let x = list.iter().find(|i| i["source"].as_str().unwrap().ends_with("x.txt")).unwrap();
    let id = x["id"].as_u64().unwrap();
    assert_eq!(fc_job_resolve(h, id, 9, 0), FC_ERR_INVALID_ARG);
    assert_eq!(fc_job_resolve(h, id, RESOLVE_RENAME, 0), FC_OK);
    assert_eq!(fc_job_resolve(h, id, RESOLVE_RENAME, 0), FC_ERR_NOT_FOUND, "이미 해결");
    // 하나 남았으니 아직 확인 필요
    let p = wait_until(h, |p| p.files_done == 2);
    assert_eq!(p.state, JobState::NeedsAttention as u32);
    assert_eq!(fc_job_resolve(h, 0, RESOLVE_OVERWRITE, 1), FC_OK);
    let p = wait(h);
    assert_eq!((p.state, p.files_done, p.issues_pending), (JobState::Done as u32, 3, 0));
    assert_eq!(std::fs::read_to_string(dst.join("s").join("x (2).txt")).unwrap(), "new");
    assert_eq!(std::fs::read_to_string(dst.join("s").join("sub").join("y.txt")).unwrap(), "new-y");
    let list = issues(h);
    assert!(list.iter().all(|i| i["resolved"] == true));
    assert!(list.iter().any(|i| i["dest"].as_str().unwrap().ends_with("x (2).txt")));
    fc_job_free(h);
}

#[test]
fn move_same_volume_renames_or_merges() {
    let t = Tmp::new("move");
    let a = t.0.join("a");
    std::fs::create_dir_all(a.join("in")).unwrap();
    std::fs::write(a.join("in").join("1.txt"), "1").unwrap();
    let single = t.0.join("single.txt");
    std::fs::write(&single, "s").unwrap();
    let dst = t.0.join("out");
    // 폴더째 이름 변경 (대상에 없음)
    let (_, p) = run(json!({"sources": [a, single], "dest_dir": dst, "mode": "move"}));
    assert_eq!((p.state, p.files_done, p.files_total), (JobState::Done as u32, 2, 2), "{p:?}");
    assert!(!a.exists() && !single.exists());
    assert_eq!(std::fs::read_to_string(dst.join("a").join("in").join("1.txt")).unwrap(), "1");

    // 대상에 같은 폴더가 있으면 파일 단위로 합친다
    std::fs::create_dir_all(a.join("in")).unwrap();
    std::fs::write(a.join("in").join("2.txt"), "2").unwrap();
    std::fs::write(a.join("in").join("1.txt"), "1b").unwrap();
    let (_, p) = run(json!({"sources": [a], "dest_dir": dst, "mode": "move", "conflict": "skip"}));
    assert_eq!((p.state, p.files_skipped), (JobState::Done as u32, 1));
    assert_eq!(std::fs::read_to_string(dst.join("a").join("in").join("2.txt")).unwrap(), "2");
    assert_eq!(std::fs::read_to_string(dst.join("a").join("in").join("1.txt")).unwrap(), "1");
    assert!(a.join("in").join("1.txt").exists(), "건너뛴 원본은 남는다");
    assert!(!a.join("in").join("2.txt").exists());
}

#[test]
fn move_by_copy_verifies_before_deleting_source() {
    let t = Tmp::new("movecopy");
    let src = t.0.join("m");
    std::fs::create_dir_all(src.join("x")).unwrap();
    std::fs::write(src.join("x").join("f.bin"), vec![3u8; 100_000]).unwrap();
    std::fs::write(src.join("g.txt"), "g").unwrap();
    let dst = t.0.join("o");
    let (_, p) = run(json!({"sources": [src], "dest_dir": dst, "mode": "move", "move_by_copy": true, "verify": "hash"}));
    assert_eq!((p.state, p.files_done), (JobState::Done as u32, 2), "{p:?}");
    assert!(!src.exists(), "검증이 끝나면 원본과 빈 폴더를 지운다");
    assert_eq!(std::fs::read(dst.join("m").join("x").join("f.bin")).unwrap().len(), 100_000);

    // 복사가 실패하면 (대상 이름이 폴더) 원본은 그대로 남는다
    std::fs::create_dir_all(&src).unwrap();
    std::fs::write(src.join("g.txt"), "g2").unwrap();
    std::fs::remove_file(dst.join("m").join("g.txt")).unwrap();
    std::fs::create_dir_all(dst.join("m").join("g.txt").join("blocker")).unwrap();
    let (_, p) = run(json!({"sources": [src], "dest_dir": dst, "mode": "move", "move_by_copy": true, "conflict": "overwrite"}));
    assert_eq!((p.state, p.files_failed, p.last_error), (JobState::Done as u32, 1, FC_ERR_IO));
    assert_eq!(std::fs::read_to_string(src.join("g.txt")).unwrap(), "g2");
}

#[test]
fn resumes_from_journal_after_crash() {
    let t = Tmp::new("resume");
    let src = t.0.join("many");
    std::fs::create_dir_all(&src).unwrap();
    for i in 0..40 {
        std::fs::write(src.join(format!("{i:03}.txt")), format!("{i}")).unwrap();
    }
    let dst = t.0.join("out");
    let journal = t.0.join("jobs").join("1.journal");
    let spec = json!({"sources": [src], "dest_dir": dst, "journal_path": journal, "threads": 2});
    let mut crash = spec.clone();
    crash["_test_abort_after_files"] = json!(10);
    let (_, p) = run(crash);
    assert_eq!((p.state, p.last_error), (JobState::Failed as u32, FC_ERR_ABORTED));
    assert!(p.files_done >= 10 && p.files_done < 40, "{p:?}");
    assert!(journal.exists(), "크래시면 저널이 남는다");

    // 저널에서 명세를 읽어 이어 간다
    let jw: Vec<u16> = journal.to_string_lossy().encode_utf16().collect();
    let mut len = 0;
    let mut buf = vec![0u16; 4096];
    // SAFETY: jw, buf, len 모두 유효한 지역 버퍼.
    assert_eq!(unsafe { fc_journal_read_spec(jw.as_ptr(), jw.len(), buf.as_mut_ptr(), buf.len(), &mut len) }, FC_OK);
    let mut resumed: Value = serde_json::from_str(&String::from_utf16(&buf[..len]).unwrap()).unwrap();
    assert!(resumed.get("_test_abort_after_files").is_none());
    resumed["resume"] = json!(true);
    let (_, p) = run(resumed);
    assert_eq!(p.state, JobState::Done as u32, "{p:?}");
    assert_eq!(p.files_done, 40);
    assert!(p.files_skipped >= 10, "이미 끝난 파일은 건너뜀: {p:?}");
    for i in 0..40 {
        assert_eq!(std::fs::read_to_string(dst.join("many").join(format!("{i:03}.txt"))).unwrap(), format!("{i}"));
    }
    assert!(!journal.exists(), "성공하면 저널을 지운다");
}

#[test]
fn pause_resume_cancel_and_current_name() {
    let t = Tmp::new("pause");
    let big = t.0.join("큰 파일.bin");
    std::fs::File::create(&big).unwrap().set_len(512 * 1024 * 1024).unwrap();
    let (rc, h) = create_job(&json!({"sources": [big], "dest_dir": t.0.join("o"), "large_threshold": 1}).to_string());
    assert_eq!(rc, FC_OK);
    assert_eq!(fc_job_pause(h), FC_ERR_INVALID_STATE, "시작 전에는 일시정지 불가");
    assert_eq!(fc_job_start(h), FC_OK);
    wait_until(h, |p| p.bytes_done > 0 || JobState::is_terminal(p.state));
    assert_eq!(fc_job_pause(h), FC_OK);
    let p1 = poll(h);
    if p1.state == JobState::Paused as u32 {
        std::thread::sleep(Duration::from_millis(300));
        let p2 = poll(h);
        assert!(p2.bytes_done <= p1.bytes_done + (8 << 20), "일시정지 중에는 덩어리 하나 이상 진행하지 않음");
        let mut len = 0usize;
        let mut small = [0u16; 2];
        // SAFETY: small은 2개, len은 지역 변수.
        assert_eq!(unsafe { fc_job_current_name(h, small.as_mut_ptr(), small.len(), &mut len) }, FC_ERR_BUFFER_TOO_SMALL);
        let mut buf = vec![0u16; len];
        // SAFETY: buf는 len개.
        assert_eq!(unsafe { fc_job_current_name(h, buf.as_mut_ptr(), buf.len(), &mut len) }, FC_OK);
        assert!(String::from_utf16(&buf).unwrap().ends_with("큰 파일.bin"));
        assert_eq!(fc_job_resume(h), FC_OK);
        assert_eq!(fc_job_cancel(h), FC_OK);
        let p = wait(h);
        assert_eq!(p.state, JobState::Canceled as u32);
        assert!(!t.0.join("o").join("큰 파일.bin").exists(), "취소하면 미완성 대상은 지운다");
    }
    fc_job_free(h);
}

#[test]
fn large_lane_and_manual_threads() {
    let t = Tmp::new("lanes");
    let src = t.0.join("mix");
    std::fs::create_dir_all(&src).unwrap();
    for i in 0..3 {
        std::fs::write(src.join(format!("big{i}.bin")), vec![i as u8; 3 << 20]).unwrap();
    }
    for i in 0..50 {
        std::fs::write(src.join(format!("s{i}.txt")), [b'x'; 100]).unwrap();
    }
    let (_, p) = run(json!({"sources": [src], "dest_dir": t.0.join("o"), "large_threshold": 1 << 20, "threads": 3, "verify": "hash"}));
    assert_eq!((p.state, p.files_done, p.files_failed), (JobState::Done as u32, 53, 0), "{p:?}");
    assert_eq!(p.bytes_done, p.bytes_total);
    assert_eq!(p.thread_limit, 3);
    assert_eq!(std::fs::read(t.0.join("o").join("mix").join("big2.bin")).unwrap(), vec![2u8; 3 << 20]);
}

#[test]
fn copy_into_itself_is_rejected() {
    let t = Tmp::new("self");
    let src = t.0.join("a");
    std::fs::create_dir_all(&src).unwrap();
    let (_, p) = run(json!({"sources": [src], "dest_dir": src.join("inner")}));
    assert_eq!((p.state, p.last_error), (JobState::Failed as u32, FC_ERR_INVALID_ARG));
}

#[test]
fn auto_tuner_saves_best_value() {
    let t = Tmp::new("tune");
    let src = t.0.join("s");
    std::fs::create_dir_all(&src).unwrap();
    for i in 0..20 {
        std::fs::write(src.join(format!("{i}.txt")), "x").unwrap();
    }
    let tuning = t.0.join("copy_tuning.json");
    let (h, p) = run(json!({"sources": [src], "dest_dir": t.0.join("o"), "tuning_path": tuning}));
    assert_eq!(p.state, JobState::Done as u32);
    let saved: std::collections::BTreeMap<String, u32> = serde_json::from_slice(&std::fs::read(&tuning).unwrap()).unwrap();
    assert_eq!(saved.len(), 1);
    let v = *saved.values().next().unwrap();
    assert!((1..=32).contains(&v));
    with(h, |j| {
        assert_eq!(j.best_threads(), v);
        FC_OK
    });
    fc_job_free(h);
}
