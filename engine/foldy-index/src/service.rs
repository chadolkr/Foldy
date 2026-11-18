//! Windows 서비스 진입점 (`foldy-index --service`). 서비스 등록(sc create / MSI)은 아직 하지 않는다.
//!
//! 수동 시험: 관리자 PowerShell에서
//! `sc.exe create FoldyIndexer binPath= "<경로>\foldy-index.exe --service" start= demand` 후 `sc.exe start FoldyIndexer`.

use crate::state::Indexer;
use std::ffi::OsString;
use std::sync::Arc;
use std::time::Duration;
use windows_service::service::{ServiceControl, ServiceControlAccept, ServiceExitCode, ServiceState, ServiceStatus, ServiceType};
use windows_service::service_control_handler::{self, ServiceControlHandlerResult};
use windows_service::{define_windows_service, service_dispatcher};

pub const SERVICE_NAME: &str = "FoldyIndexer";

define_windows_service!(ffi_service_main, service_main);

pub fn run() -> windows_service::Result<()> {
    service_dispatcher::start(SERVICE_NAME, ffi_service_main)
}

fn service_main(_args: Vec<OsString>) {
    if let Err(e) = run_service() {
        eprintln!("service error: {e}");
    }
}

fn status(state: ServiceState) -> ServiceStatus {
    ServiceStatus {
        service_type: ServiceType::OWN_PROCESS,
        current_state: state,
        controls_accepted: if state == ServiceState::Running {
            ServiceControlAccept::STOP | ServiceControlAccept::SHUTDOWN
        } else {
            ServiceControlAccept::empty()
        },
        exit_code: ServiceExitCode::Win32(0),
        checkpoint: 0,
        wait_hint: Duration::from_secs(5),
        process_id: None,
    }
}

fn run_service() -> windows_service::Result<()> {
    let stop = Arc::new(tokio::sync::Notify::new());
    let stop2 = stop.clone();
    let handle = service_control_handler::register(SERVICE_NAME, move |c| match c {
        ServiceControl::Stop | ServiceControl::Shutdown => {
            stop2.notify_one();
            ServiceControlHandlerResult::NoError
        }
        ServiceControl::Interrogate => ServiceControlHandlerResult::NoError,
        _ => ServiceControlHandlerResult::NotImplemented,
    })?;
    handle.set_service_status(status(ServiceState::Running))?;

    let indexer: Arc<Indexer> = crate::start_indexer(&crate::Args::default());
    let rt = tokio::runtime::Builder::new_multi_thread().worker_threads(2).enable_all().build();
    match rt {
        Ok(rt) => {
            // 자동 업데이트 (설계서 10.5). 내장 공개키가 없거나 설정을 만들 수 없으면 끄고 색인만 한다.
            match crate::update::UpdateConfig::for_service() {
                Ok(cfg) => {
                    let svc = crate::update::UpdateService::new(cfg, Arc::new(crate::update::win::WinHost));
                    crate::update::set_global(svc.clone());
                    rt.spawn(svc.run());
                }
                Err(e) => eprintln!("자동 업데이트 꺼짐: {e}"),
            }
            let r = rt.block_on(crate::server::run(foldy_proto::PIPE_NAME, false, indexer, async move { stop.notified().await }));
            if let Err(e) = r {
                eprintln!("pipe server error: {e}");
            }
            // 업데이트 다운로드 중이어도 서비스 정지(MSI 업그레이드 포함)를 오래 막지 않는다
            rt.shutdown_timeout(Duration::from_secs(2));
        }
        Err(e) => eprintln!("tokio runtime error: {e}"),
    }
    handle.set_service_status(status(ServiceState::Stopped))?;
    Ok(())
}
