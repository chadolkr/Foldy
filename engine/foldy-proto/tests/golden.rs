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
