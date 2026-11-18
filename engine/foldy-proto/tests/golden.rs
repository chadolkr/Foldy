//! C# 클라이언트와 공유하는 골든 프레임. 인코딩이 바뀌면 이 테스트가 실패하고,
//! `FOLDY_UPDATE_GOLDEN=1 cargo test`로 다시 만든 뒤 C# 테스트도 함께 통과해야 한다.
use foldy_proto::*;
use std::path::PathBuf;

fn check(name: &str, bytes: &[u8]) {
    let p = PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("tests/golden").join(name);
    if std::env::var_os("FOLDY_UPDATE_GOLDEN").is_some() || !p.exists() {
        std::fs::write(&p, bytes).unwrap();
    }
    assert_eq!(std::fs::read(&p).unwrap(), bytes, "golden mismatch: {name}");
}

#[test]
fn golden_frames() {
    check("hello.bin", &encode(MsgType::Hello, &Hello { protocol: 1, client: "Foldy 0.0.1".into() }));
    check(
        "hello_reply.bin",
        &encode(
            MsgType::HelloReply,
            &HelloReply { protocol: 1, service_version: "0.0.1".into(), volumes: vec!["C:".into(), "D:".into()] },
        ),
    );
    check(
        "status_reply.bin",
        &encode(
            MsgType::StatusReply,
            &StatusReply {
                state: 2,
                indexed_entries: 1_234_567,
                last_usn: -42,
                progress_permille: 1000,
                volumes: vec![
                    VolumeStatus {
                        volume: "C:".into(),
                        state: 2,
                        entries: 1_234_567,
                        last_usn: -42,
                        progress_permille: 1000,
                        message: String::new(),
                    },
                    VolumeStatus {
                        volume: "D:".into(),
                        state: 0,
                        entries: 0,
                        last_usn: 0,
                        progress_permille: 0,
                        message: "관리자 권한 필요".into(),
                    },
                ],
                analysis: vec![AnalysisProgress {
                    volume: "C:".into(),
                    state: analysis_state::BUILDING,
                    progress_permille: 420,
                    message: "파일 쓰는 중".into(),
                }],
            },
        ),
    );
}

#[test]
fn golden_analysis_frames() {
    check("build_analysis.bin", &encode(MsgType::BuildAnalysis, &BuildAnalysis { volume: "C:".into() }));
    check("analysis_info.bin", &encode(MsgType::AnalysisInfo, &AnalysisInfo { volume: r"D:\작업".into() }));
    check(
        "build_analysis_reply.bin",
        &encode(MsgType::BuildAnalysisReply, &BuildAnalysisReply { status: analysis_status::ALREADY_RUNNING, message: "이미 분석 중".into() }),
    );
    check(
        "analysis_info_reply.bin",
        &encode(
            MsgType::AnalysisInfoReply,
            &AnalysisInfoReply {
                status: analysis_status::OK,
                volume: "C:".into(),
                db_path: r"C:\ProgramData\Foldy\Analysis\{guid}.20261119T010203Z.db".into(),
                scan_time: 134_080_000_000_000_000,
                previous: vec![AnalysisSnapshot {
                    db_path: r"C:\ProgramData\Foldy\Analysis\{guid}.20261112T010203Z.db".into(),
                    scan_time: 134_074_000_000_000_000,
                }],
            },
        ),
    );
}

#[test]
fn golden_search_frames() {
    check(
        "search.bin",
        &encode(MsgType::Search, &Search { search_id: 7, query: "보고서.PDF".into(), limit: 500, cursor: 0x0100_0000_0000_1234 }),
    );
    check("cancel_search.bin", &encode(MsgType::CancelSearch, &CancelSearch { search_id: 300 }));
    check(
        "search_batch.bin",
        &encode(
            MsgType::SearchBatch,
            &SearchBatch {
                search_id: 7,
                hits: vec![
                    SearchHit {
                        path: r"C:\Users\me\보고서.pdf".into(),
                        size: 123_456,
                        alloc_size: 126_976,
                        mtime: 133_800_000_000_000_000,
                        is_dir: false,
                    },
                    SearchHit { path: r"C:\Docs".into(), size: 1 << 40, alloc_size: 1 << 40, mtime: -1, is_dir: true },
                ],
                done: true,
                status: search_status::OK,
                next_cursor: 99,
            },
        ),
    );
    check("folder_sizes.bin", &encode(MsgType::FolderSizes, &FolderSizes { path: r"C:\Users".into() }));
    check(
        "folder_sizes_reply.bin",
        &encode(
            MsgType::FolderSizesReply,
            &FolderSizesReply {
                status: folder_status::OK,
                total_size: 5100,
                total_alloc: 12_288,
                entries: vec![
                    FolderEntry { name: "a.txt".into(), is_dir: false, size: 100, alloc_size: 4096 },
                    FolderEntry { name: "하위".into(), is_dir: true, size: 5000, alloc_size: 8192 },
                ],
            },
        ),
    );
}

#[test]
fn golden_update_frames() {
    check("update_status.bin", &encode(MsgType::UpdateStatus, &UpdateStatus));
    check("check_now.bin", &encode(MsgType::CheckNow, &CheckNow));
    check(
        "install_when_idle.bin",
        &encode(MsgType::InstallWhenIdle, &InstallWhenIdle { relaunch: true, session_id: 300 }),
    );
    check("skip_version.bin", &encode(MsgType::SkipVersion, &SkipVersion { version: "1.3.0-beta.2".into() }));
    check("set_channel.bin", &encode(MsgType::SetChannel, &SetChannel { channel: update_channel::BETA }));
    check("app_started.bin", &encode(MsgType::AppStarted, &AppStarted { version: "1.2.3".into() }));
    check(
        "update_status_reply.bin",
        &encode(
            MsgType::UpdateStatusReply,
            &UpdateStatusReply {
                state: update_state::DOWNLOADING,
                current_version: "1.2.2".into(),
                available_version: "1.2.3".into(),
                channel: update_channel::STABLE,
                progress_permille: 512,
                downloaded: 150_000,
                total: 300_000,
                last_check_unix: 1_795_000_000,
                last_error: "네트워크 오류".into(),
                mandatory: true,
                install_pending: false,
                notes: "변경 내역".into(),
            },
        ),
    );
}
