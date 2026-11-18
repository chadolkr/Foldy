//! `foldy-index --bench C:` : MFT 색인 구축 시간, 항목 수, 메모리, 검색 지연을 잰다.
//! 볼륨 핸들이 필요하므로 관리자 PowerShell에서 실행해야 한다 (아니면 종료 코드 2).

use foldy_ntfs::search::{self, Outcome};
use foldy_ntfs::volume::Volume;
use foldy_ntfs::{VolumeIndex, fold_name};
use std::sync::atomic::AtomicBool;
use std::time::Instant;

fn process_memory() -> (usize, usize) {
    use windows_sys::Win32::System::ProcessStatus::{K32GetProcessMemoryInfo, PROCESS_MEMORY_COUNTERS};
    use windows_sys::Win32::System::Threading::GetCurrentProcess;
    // SAFETY: 정수 필드만 있는 POD 구조체를 0으로 초기화한다.
    let mut c: PROCESS_MEMORY_COUNTERS = unsafe { std::mem::zeroed() };
    c.cb = size_of::<PROCESS_MEMORY_COUNTERS>() as u32;
    // SAFETY: 현재 프로세스 의사 핸들과 크기를 정확히 넘긴 지역 구조체.
    let ok = unsafe { K32GetProcessMemoryInfo(GetCurrentProcess(), &mut c, c.cb) };
    if ok == 0 { (0, 0) } else { (c.WorkingSetSize, c.PagefileUsage) }
}

fn mb(b: usize) -> f64 {
    b as f64 / (1024.0 * 1024.0)
}

fn bench_search(idx: &VolumeIndex, q: &str) {
    let needle = fold_name(q);
    let cancel = AtomicBool::new(false);
    // 첫 결과까지 (limit 100, UI 첫 화면)
    let t = Instant::now();
    let mut first = None;
    let mut n = 0usize;
    let _ = search::search(idx, needle.as_bytes(), 0, 100, &cancel, |h| {
        if first.is_none() {
            first = Some(t.elapsed());
        }
        n += h.len();
        true
    });
    let page = t.elapsed();
    // 전체 (모든 일치 + 경로 재구성)
    let t = Instant::now();
    let mut total = 0usize;
    let mut path_bytes = 0usize;
    let out = search::search(idx, needle.as_bytes(), 0, usize::MAX, &cancel, |h| {
        total += h.len();
        path_bytes += h.iter().map(|x| idx.path_of(x.id).len()).sum::<usize>();
        true
    });
    let all = t.elapsed();
    debug_assert_eq!(out, Outcome::Exhausted);
    println!(
        "search {q:>10}: first hit {:>8.2?}, first {n} hits {:>8.2?}, all {total} hits (+paths {:.1}MB) {:>8.2?}",
        first.unwrap_or_default(),
        page,
        mb(path_bytes),
        all
    );
}

pub fn run(volume: &str) -> i32 {
    let vol = match Volume::open(volume) {
        Ok(v) => v,
        Err(e) if e.kind() == std::io::ErrorKind::PermissionDenied => {
            eprintln!("관리자 권한이 필요합니다. 관리자 PowerShell에서 `foldy-index --bench {volume}` 을 실행하세요. ({e})");
            return 2;
        }
        Err(e) => {
            eprintln!("볼륨을 열 수 없음: {e}");
            return 1;
        }
    };
    println!(
        "volume {}: cluster {} B, MFT record {} B, MFT at LCN {}",
        vol.name, vol.boot.cluster_size, vol.boot.record_size, vol.boot.mft_lcn
    );
    let (ws0, _) = process_memory();
    let cancel = AtomicBool::new(false);
    let t = Instant::now();
    let idx = match vol.build_index(&cancel, |_, _| {}) {
        Ok(i) => i,
        Err(e) => {
            eprintln!("색인 실패: {e}");
            return 1;
        }
    };
    let build = t.elapsed();
    let (ws1, private) = process_memory();
    let entries = idx.live_count();
    println!("entries        : {entries}");
    println!("build time     : {build:.2?}  ({:.0} entries/s)", entries as f64 / build.as_secs_f64().max(1e-9));
    println!(
        "index memory   : {:.1} MB ({:.1} B/entry)   process working set {:.1} MB (+{:.1} MB), private {:.1} MB",
        mb(idx.memory_bytes()),
        idx.memory_bytes() as f64 / entries.max(1) as f64,
        mb(ws1),
        mb(ws1.saturating_sub(ws0)),
        mb(private)
    );
    println!("root total     : {:.2} GB logical, {:.2} GB allocated", idx.node(5).map(|n| n.size).unwrap_or(0) as f64 / 1e9, idx.node(5).map(|n| n.alloc).unwrap_or(0) as f64 / 1e9);
    match vol.query_journal() {
        Ok(j) => println!("usn journal    : id {:#x}, next usn {}", j.id, j.next_usn),
        Err(e) => println!("usn journal    : unavailable ({e})"),
    }
    for q in ["exe", ".dll", "foldy", "readme", "a"] {
        bench_search(&idx, q);
    }
    let t = Instant::now();
    let win = idx.resolve(&format!("{}\\Windows", vol.name));
    let kids = win.map(|w| idx.children(w).len()).unwrap_or(0);
    println!("folder sizes   : {}\\Windows → {kids} children in {:.2?}", vol.name, t.elapsed());
    0
}
