//! foldy-index: 디스크 색인 서비스 (설계서 5.6, "MFT/USN 기반 디스크 색인", M2).
//!
//! 시작하면 로컬 고정 NTFS 볼륨마다 $MFT를 읽어 메모리 색인을 만들고 USN 저널로 갱신하며,
//! Named Pipe로 Hello / Status / Search / FolderSizes 에 응답한다. 볼륨을 열 권한이 없으면(관리자 아님)
//! 로그만 남기고 상태 "not ready"로 응답해 클라이언트가 LocalProvider로 폴백하게 한다.
//!
//! 사용법:
//!   foldy-index [--pipe <이름>] [--once] [--no-index] [--volumes C:,D:] [--scan-dir <폴더>]...
//!   foldy-index --bench C:      MFT 색인 벤치 (관리자 권한 필요)
//!   foldy-index --service       Windows 서비스로 실행 (서비스 관리자가 띄울 때)
//!
//!   --pipe      파이프 이름 (기본 \\.\pipe\Foldy.Indexer.v1, 테스트는 임의 이름 사용)
//!   --once      클라이언트 하나를 처리한 뒤 종료 (통합 테스트용)
//!   --no-index  볼륨을 색인하지 않음 (파이프만 시험)
//!   --volumes   색인할 볼륨 (기본: 모든 로컬 고정 NTFS 볼륨)
//!   --scan-dir  일반 폴더 열거로 색인 (관리자 권한 없이 검색 시험·폴백용, 여러 번 가능)
//!   --update-*  콘솔 모드 자동 업데이트 시험 (update/mod.rs `from_console_args`, docs/auto-update.md).
//!               `--update-api <URL>`이 있을 때만 켜진다. 서비스 모드는 항상 켜지고 이 인자를 읽지 않는다.
//!   --analysis-dir  분석 DB 폴더 (기본 %ProgramData%\Foldy\Analysis, 시험용. 지정하면 ACL을 건드리지 않는다)
//!   --index-dir     색인 스냅샷 폴더 (기본 %ProgramData%\Foldy\Index, 시험용. 지정하면 ACL을 건드리지 않는다)

mod acl;
mod analysis;
mod analysis_job;
mod bench;
mod persist;
mod query;
mod server;
mod service;
mod state;
mod update;

use state::{Indexer, VolumeSlot};
use std::sync::Arc;

#[derive(Default)]
pub(crate) struct Args {
    pipe: Option<String>,
    once: bool,
    no_index: bool,
    volumes: Option<Vec<String>>,
    scan_dirs: Vec<String>,
    bench: Option<String>,
    bench_scan: Option<String>,
    service: bool,
    /// `--update-…` 인자 (이름, 값)
    update: Vec<(String, String)>,
    analysis_dir: Option<String>,
    index_dir: Option<String>,
}

fn parse_args() -> Args {
    let mut a = Args::default();
    let mut it = std::env::args().skip(1);
    while let Some(x) = it.next() {
        match x.as_str() {
            "--pipe" => {
                if let Some(v) = it.next() {
                    a.pipe = Some(if v.starts_with(r"\\.\pipe\") { v } else { format!(r"\\.\pipe\{v}") });
                }
            }
            "--once" => a.once = true,
            "--no-index" => a.no_index = true,
            "--volumes" => a.volumes = it.next().map(|v| v.split(',').map(|s| s.trim().trim_end_matches('\\').to_uppercase()).filter(|s| !s.is_empty()).collect()),
            "--scan-dir" => a.scan_dirs.extend(it.next()),
            "--bench" => a.bench = Some(it.next().unwrap_or_else(|| "C:".into())),
            "--bench-scan" => a.bench_scan = it.next(),
            "--service" => a.service = true,
            x if x.starts_with("--update-") => a.update.push((x.to_string(), it.next().unwrap_or_default())),
            "--analysis-dir" => a.analysis_dir = it.next(),
            "--index-dir" => a.index_dir = it.next(),
            _ => eprintln!("unknown argument: {x}"),
        }
    }
    a
}

/// 색인 대상 슬롯을 만들고 백그라운드 구축을 시작한다.
pub(crate) fn start_indexer(args: &Args) -> Arc<Indexer> {
    let mut ix = Indexer {
        analysis_dir: args.analysis_dir.as_ref().map(std::path::PathBuf::from),
        index_dir: args.index_dir.as_ref().map(std::path::PathBuf::from),
        ..Default::default()
    };
    let mut ntfs = Vec::new();
    for d in &args.scan_dirs {
        ix.volumes.push(Arc::new(VolumeSlot::new(d.trim_end_matches(['\\', '/']))));
    }
    if !args.no_index {
        let vols = args.volumes.clone().unwrap_or_else(foldy_ntfs::volume::ntfs_fixed_volumes);
        for v in vols {
            let slot = Arc::new(VolumeSlot::new(v));
            ntfs.push(slot.clone());
            ix.volumes.push(slot);
        }
    }
    let ix = Arc::new(ix);
    for slot in &ix.volumes {
        if ntfs.iter().any(|n| Arc::ptr_eq(n, slot)) {
            state::start_ntfs(slot.clone(), ix.clone());
        } else {
            state::start_dir_scan(slot.clone());
        }
    }
    ix
}

fn main() {
    let args = parse_args();
    if let Some(d) = &args.bench_scan {
        std::process::exit(bench::run_scan(d));
    }
    if let Some(v) = &args.bench {
        std::process::exit(bench::run(v));
    }
    if args.service {
        if let Err(e) = service::run() {
            eprintln!("서비스 관리자에 연결할 수 없음 (콘솔에서는 --service 없이 실행): {e}");
            std::process::exit(1);
        }
        return;
    }
    let indexer = start_indexer(&args);
    let pipe = args.pipe.clone().unwrap_or_else(|| foldy_proto::PIPE_NAME.to_string());
    let rt = tokio::runtime::Builder::new_multi_thread().worker_threads(2).enable_all().build().expect("tokio runtime");
    match update::UpdateConfig::from_console_args(&args.update) {
        Ok(Some((cfg, fake))) => {
            // 시험: --update-fake-install이 있으면 msiexec 대신 기록만 한다
            let host: Arc<dyn update::host::Host> = match fake {
                Some(log) => Arc::new(update::host::FakeHost::new(Some(log))),
                None => Arc::new(update::win::WinHost),
            };
            let svc = update::UpdateService::new(cfg, host);
            update::set_global(svc.clone());
            rt.spawn(svc.run());
        }
        Ok(None) => {}
        Err(e) => eprintln!("업데이트 인자 오류: {e}"),
    }
    let r = rt.block_on(server::run(&pipe, args.once, indexer.clone(), async {
        let _ = tokio::signal::ctrl_c().await;
    }));
    indexer.shutdown.store(true, std::sync::atomic::Ordering::Relaxed);
    indexer.save_all();
    // 업데이트 다운로드 중인 블로킹 스레드를 오래 기다리지 않는다
    rt.shutdown_timeout(std::time::Duration::from_secs(2));
    if let Err(e) = r {
        eprintln!("pipe server error: {e}");
        std::process::exit(1);
    }
}

/// 드라이브 문자 목록. GetLogicalDrives는 디스크에 접근하지 않으므로
/// 끊긴 네트워크 드라이브가 있어도 막히지 않는다.
pub(crate) fn volumes() -> Vec<String> {
    // SAFETY: 인자가 없고 비트마스크만 반환하는 Win32 함수.
    let mask = unsafe { windows_sys::Win32::Storage::FileSystem::GetLogicalDrives() };
    (0..26u8).filter(|i| mask & (1 << i) != 0).map(|i| format!("{}:", (b'A' + i) as char)).collect()
}
