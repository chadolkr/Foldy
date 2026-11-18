//! foldy-copy-bench: 자체 엔진과 `robocopy /MT:8 /E`를 같은 데이터로 비교한다 (설계서 8.2, docs/copy-engine.md).
//!
//! 사용법: foldy-copy-bench [--root DIR] [--small N] [--small-kb K] [--big-mb M] [--runs R] [--threads T]
//!                          [--skip-small] [--skip-big] [--json OUT]
//! 기본값: %TEMP%\foldy-bench, 4KB 파일 10,000개, 1GB 파일 1개, 2회씩 번갈아 실행.

use std::io::Write;
use std::path::{Path, PathBuf};
use std::process::Command;
use std::time::{Duration, Instant};

use foldy_copy::{JobState, new_job, start};

struct Args {
    root: PathBuf,
    small: usize,
    small_kb: usize,
    big_mb: u64,
    runs: usize,
    threads: u32,
    skip_small: bool,
    skip_big: bool,
    json: Option<PathBuf>,
}

fn parse_args() -> Args {
    let mut a = Args {
        root: std::env::temp_dir().join("foldy-bench"),
        small: 10_000,
        small_kb: 4,
        big_mb: 1024,
        runs: 2,
        threads: 0,
        skip_small: false,
        skip_big: false,
        json: None,
    };
    let mut it = std::env::args().skip(1);
    while let Some(k) = it.next() {
        let mut v = || it.next().unwrap_or_default();
        match k.as_str() {
            "--root" => a.root = PathBuf::from(v()),
            "--small" => a.small = v().parse().unwrap_or(a.small),
            "--small-kb" => a.small_kb = v().parse().unwrap_or(a.small_kb),
            "--big-mb" => a.big_mb = v().parse().unwrap_or(a.big_mb),
            "--runs" => a.runs = v().parse().unwrap_or(a.runs).max(1),
            "--threads" => a.threads = v().parse().unwrap_or(0),
            "--skip-small" => a.skip_small = true,
            "--skip-big" => a.skip_big = true,
            "--json" => a.json = Some(PathBuf::from(v())),
            _ => {
                eprintln!("알 수 없는 인자: {k}");
                std::process::exit(2);
            }
        }
    }
    a
}

/// 압축·희소 파일 최적화에 걸리지 않는 의사 난수 내용
fn fill(buf: &mut [u8], seed: &mut u64) {
    for chunk in buf.chunks_mut(8) {
        *seed ^= *seed << 13;
        *seed ^= *seed >> 7;
        *seed ^= *seed << 17;
        let b = seed.to_le_bytes();
        chunk.copy_from_slice(&b[..chunk.len()]);
    }
}

fn prepare_small(dir: &Path, n: usize, kb: usize) -> std::io::Result<()> {
    let marker = dir.join(format!(".ready-{n}-{kb}"));
    if marker.exists() {
        return Ok(());
    }
    let _ = std::fs::remove_dir_all(dir);
    let mut seed = 0x9E37_79B9_7F4A_7C15u64;
    let mut buf = vec![0u8; kb * 1024];
    for i in 0..n {
        // 100개씩 하위 폴더 (실제 프로젝트 폴더처럼)
        let sub = dir.join(format!("d{:03}", i / 100));
        if i % 100 == 0 {
            std::fs::create_dir_all(&sub)?;
        }
        fill(&mut buf, &mut seed);
        std::fs::write(sub.join(format!("f{i:05}.bin")), &buf)?;
    }
    std::fs::write(marker, b"")
}

fn prepare_big(dir: &Path, mb: u64) -> std::io::Result<()> {
    let file = dir.join("big.bin");
    if std::fs::metadata(&file).is_ok_and(|m| m.len() == mb << 20) {
        return Ok(());
    }
    std::fs::create_dir_all(dir)?;
    let mut f = std::io::BufWriter::with_capacity(8 << 20, std::fs::File::create(&file)?);
    let mut seed = 0x2545_F491_4F6C_DD1Du64;
    let mut buf = vec![0u8; 8 << 20];
    for _ in 0..(mb / 8).max(1) {
        fill(&mut buf, &mut seed);
        f.write_all(&buf)?;
    }
    f.flush()
}

fn clean(dst: &Path) {
    let _ = std::fs::remove_dir_all(dst);
}

fn run_foldy(src: &Path, dst: &Path, threads: u32) -> Result<(Duration, u32), String> {
    let spec = serde_json::json!({
        "sources": [src], "dest_dir": dst, "conflict": "overwrite", "verify": "size_time", "threads": threads,
    });
    let job = new_job(&spec.to_string()).map_err(|e| format!("create {e}"))?;
    let t = Instant::now();
    if start(&job) != 0 {
        return Err("start".into());
    }
    let mut copy_started = None;
    loop {
        let p = job.snapshot();
        if copy_started.is_none() && p.phase >= 3 {
            copy_started = Some(t.elapsed());
        }
        if JobState::is_terminal(p.state) {
            let el = t.elapsed();
            if std::env::var_os("FOLDY_BENCH_VERBOSE").is_some() {
                eprintln!("    스캔+계획 {:.2}s, 전체 {:.2}s", copy_started.unwrap_or_default().as_secs_f64(), el.as_secs_f64());
            }
            if p.state != JobState::Done as u32 || p.files_failed > 0 {
                return Err(format!("{p:?}"));
            }
            return Ok((el, job.best_threads()));
        }
        std::thread::sleep(Duration::from_millis(5));
    }
}

fn run_robocopy(src: &Path, dst: &Path) -> Result<Duration, String> {
    let t = Instant::now();
    let st = Command::new("robocopy")
        .arg(src)
        .arg(dst)
        .args(["/MT:8", "/E", "/NFL", "/NDL", "/NJH", "/NJS", "/NP", "/R:1", "/W:1"])
        .stdout(std::process::Stdio::null())
        .status()
        .map_err(|e| e.to_string())?;
    let el = t.elapsed();
    // robocopy 종료 코드 0~7은 성공
    match st.code() {
        Some(c) if c < 8 => Ok(el),
        c => Err(format!("robocopy exit {c:?}")),
    }
}

struct Row {
    name: String,
    bytes: u64,
    foldy: Vec<Duration>,
    robo: Vec<Duration>,
    threads: u32,
}

fn median(v: &[Duration]) -> Duration {
    let mut s = v.to_vec();
    s.sort();
    s[s.len() / 2]
}

fn bench(name: &str, src: &Path, root: &Path, bytes: u64, a: &Args) -> Result<Row, String> {
    let mut row = Row { name: name.into(), bytes, foldy: vec![], robo: vec![], threads: 0 };
    let d1 = root.join("out-foldy");
    let d2 = root.join("out-robocopy");
    for r in 0..a.runs {
        clean(&d1);
        clean(&d2);
        // 캐시 효과가 한쪽에만 쏠리지 않게 순서를 번갈아 돈다
        let foldy_first = r % 2 == 0;
        for step in 0..2 {
            if (step == 0) == foldy_first {
                let (t, th) = run_foldy(src, &d1, a.threads)?;
                row.threads = th;
                row.foldy.push(t);
            } else {
                row.robo.push(run_robocopy(src, &d2)?);
            }
        }
        eprintln!(
            "  {name} #{}: foldy {:.2}s, robocopy {:.2}s",
            r + 1,
            row.foldy.last().unwrap().as_secs_f64(),
            row.robo.last().unwrap().as_secs_f64()
        );
    }
    clean(&d1);
    clean(&d2);
    Ok(row)
}

fn main() {
    let a = parse_args();
    std::fs::create_dir_all(&a.root).expect("bench root");
    let mut rows = Vec::new();
    if !a.skip_small {
        let dir = a.root.join("small");
        eprintln!("작은 파일 {}개 × {}KB 준비 중… ({})", a.small, a.small_kb, dir.display());
        prepare_small(&dir, a.small, a.small_kb).expect("prepare small");
        let bytes = (a.small * a.small_kb * 1024) as u64;
        match bench(&format!("{}×{}KB", a.small, a.small_kb), &dir, &a.root, bytes, &a) {
            Ok(r) => rows.push(r),
            Err(e) => eprintln!("작은 파일 벤치 실패: {e}"),
        }
    }
    if !a.skip_big {
        let dir = a.root.join("big");
        eprintln!("큰 파일 {}MB 준비 중…", a.big_mb);
        prepare_big(&dir, a.big_mb).expect("prepare big");
        match bench(&format!("1×{}MB", a.big_mb), &dir, &a.root, a.big_mb << 20, &a) {
            Ok(r) => rows.push(r),
            Err(e) => eprintln!("큰 파일 벤치 실패: {e}"),
        }
    }

    println!("| 시나리오 | Foldy (중앙값) | robocopy /MT:8 (중앙값) | Foldy/robocopy | Foldy MB/s | 스레드 |");
    println!("| --- | --- | --- | --- | --- | --- |");
    let mut out = Vec::new();
    for r in &rows {
        let f = median(&r.foldy).as_secs_f64();
        let b = median(&r.robo).as_secs_f64();
        let mbps = r.bytes as f64 / (1 << 20) as f64 / f.max(1e-9);
        println!("| {} | {f:.2}s | {b:.2}s | {:.2}× | {mbps:.0} | {} |", r.name, f / b.max(1e-9), r.threads);
        out.push(serde_json::json!({
            "scenario": r.name, "bytes": r.bytes, "foldy_s": f, "robocopy_s": b,
            "foldy_runs": r.foldy.iter().map(Duration::as_secs_f64).collect::<Vec<_>>(),
            "robocopy_runs": r.robo.iter().map(Duration::as_secs_f64).collect::<Vec<_>>(),
            "threads": r.threads,
        }));
    }
    if let Some(p) = a.json {
        let _ = std::fs::write(p, serde_json::to_vec_pretty(&out).unwrap_or_default());
    }
}
