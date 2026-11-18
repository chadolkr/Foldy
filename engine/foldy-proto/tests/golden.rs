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
        &encode(MsgType::StatusReply, &StatusReply { state: 2, indexed_entries: 1_234_567, last_usn: -42 }),
    );
}
