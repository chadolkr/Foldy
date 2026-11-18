//! 합성 색인으로 분석 DB를 만들어 스키마·집계·규칙·보관 정책을 확인한다.

use super::*;
use foldy_ntfs::Entry;
use rusqlite::OpenFlags;

const DAY: i64 = rules::FILETIME_DAY;
const NOW: i64 = 134_080_000_000_000_000;

fn e(parent: u32, name: &str, is_dir: bool, size: u64, mtime: i64) -> Entry {
    Entry {
        names: vec![(parent, name.into())],
        is_dir,
        size,
        alloc: size.div_ceil(4096) * 4096,
        has_data: !is_dir,
        mtime,
        attributes: 0,
    }
}

/// D:\ (5)
///   proj(20) ─ package.json(21, 10B), node_modules(22) ─ lib(23) ─ big.js(24, 30MB, 60일 전)
///            └ src(25) ─ app.ts(26, 2000B, 오늘)
///   Windows(30, 시스템 보호) ─ Temp(31) ─ junk.tmp(32, 500MB, 1년 전)
///   Users(40) ─ me(41) ─ Downloads(42) ─ setup.exe(43, 80MB, 200일 전), photo.JPG(44, 3MB)
///   $Extend(11) ─ $UsnJrnl(12) (메타, 빠져야 함)
///   orphan.txt(50, 부모 99는 없음 → 빠져야 함)
pub(crate) fn sample_index() -> VolumeIndex {
    let old = NOW - 60 * DAY;
    let mut v = VolumeIndex::new_ntfs("D:", 128);
    v.insert_build(5, &e(5, ".", true, 0, NOW));
    v.insert_build(11, &e(5, "$Extend", true, 0, NOW));
    v.insert_build(12, &e(11, "$UsnJrnl", false, 1 << 20, NOW));
    v.insert_build(20, &e(5, "proj", true, 0, NOW));
    v.insert_build(21, &e(20, "package.json", false, 10, old));
    v.insert_build(22, &e(20, "node_modules", true, 0, old));
    v.insert_build(23, &e(22, "lib", true, 0, old));
    v.insert_build(24, &e(23, "big.js", false, 30 << 20, old));
    v.insert_build(25, &e(20, "src", true, 0, NOW));
    v.insert_build(26, &e(25, "app.ts", false, 2000, NOW));
    v.insert_build(30, &e(5, "Windows", true, 0, NOW));
    v.insert_build(31, &e(30, "Temp", true, 0, NOW));
    v.insert_build(32, &e(31, "junk.tmp", false, 500 << 20, NOW - 365 * DAY));
    v.insert_build(40, &e(5, "Users", true, 0, NOW));
    v.insert_build(41, &e(40, "me", true, 0, NOW));
    v.insert_build(42, &e(41, "Downloads", true, 0, NOW));
    v.insert_build(43, &e(42, "setup.exe", false, 80 << 20, NOW - 200 * DAY));
    v.insert_build(44, &e(42, "photo.JPG", false, 3 << 20, NOW - 5 * DAY));
    v.insert_build(50, &e(99, "orphan.txt", false, 7, NOW));
    v.finalize();
    v
}

fn temp_dir(tag: &str) -> PathBuf {
    let d = std::env::temp_dir().join(format!("foldy-analysis-{tag}-{}", std::process::id()));
    let _ = std::fs::remove_dir_all(&d);
    std::fs::create_dir_all(&d).unwrap();
    d
}

fn opts() -> BuildOptions {
    BuildOptions { volume: "D:".into(), started_at: NOW, usn: 77, min_file_alloc: 0 }
}

fn build(dir: &Path, idx: &VolumeIndex, o: &BuildOptions) -> PathBuf {
    let ex = extract(idx);
    let (p, _) = build_into_dir(dir, "test", &ex, &RuleSet::defaults(), o, &|_, _| {}, &AtomicBool::new(false)).unwrap();
    p
}

fn ro(p: &Path) -> Connection {
    Connection::open_with_flags(p, OpenFlags::SQLITE_OPEN_READ_ONLY).unwrap()
}

fn one<T: rusqlite::types::FromSql>(c: &Connection, sql: &str) -> T {
    c.query_row(sql, [], |r| r.get(0)).unwrap()
}

#[test]
fn builds_schema_rows_and_aggregates() {
    let dir = temp_dir("schema");
    let p = build(&dir, &sample_index(), &opts());
    assert!(p.file_name().unwrap().to_string_lossy().starts_with("test.20"));
    let c = ro(&p);
    assert_eq!(one::<i64>(&c, "PRAGMA user_version"), SCHEMA_VERSION as i64);
    assert_eq!(one::<i64>(&c, "PRAGMA application_id"), APPLICATION_ID as i64);
    // 메타와 고아는 빠진다: 폴더 10개(루트 포함), 파일 6개
    assert_eq!(one::<i64>(&c, "SELECT COUNT(*) FROM dir"), 10);
    assert_eq!(one::<i64>(&c, "SELECT COUNT(*) FROM file"), 6);
    assert_eq!(one::<i64>(&c, "SELECT COUNT(*) FROM dir WHERE name LIKE '$%'"), 0);
    assert_eq!(one::<i64>(&c, "SELECT COUNT(*) FROM file WHERE name = 'orphan.txt'"), 0);
    // 루트
    let (name, parent, depth, files, dirs): (String, Option<i64>, i64, i64, i64) = c
        .query_row("SELECT name, parent_id, depth, total_files, total_dirs FROM dir WHERE id = 5", [], |r| {
            Ok((r.get(0)?, r.get(1)?, r.get(2)?, r.get(3)?, r.get(4)?))
        })
        .unwrap();
    assert_eq!((name.as_str(), parent, depth, files, dirs), (r"D:\", None, 0, 6, 9));
    // proj: 하위 전체 크기(색인 값), 파일 수, 최신/최고 수정 시각
    let (alloc, own, total, newest, oldest): (i64, i64, i64, i64, i64) = c
        .query_row("SELECT alloc, own_files, total_files, newest_mtime, oldest_mtime FROM dir WHERE name = 'proj'", [], |r| {
            Ok((r.get(0)?, r.get(1)?, r.get(2)?, r.get(3)?, r.get(4)?))
        })
        .unwrap();
    assert_eq!(alloc, 4096 + (30 << 20) + 4096);
    assert_eq!((own, total), (1, 3));
    assert_eq!(newest, NOW);
    assert_eq!(oldest, NOW - 60 * DAY);
    assert_eq!(one::<i64>(&c, "SELECT depth FROM dir WHERE name = 'lib'"), 3);
    // 확장자 소문자, ext_stat
    assert_eq!(one::<String>(&c, "SELECT ext FROM file WHERE name = 'photo.JPG'"), "jpg");
    assert_eq!(one::<i64>(&c, "SELECT files FROM ext_stat WHERE ext = 'js'"), 1);
    assert_eq!(one::<i64>(&c, "SELECT SUM(files) FROM ext_stat"), 6);
    // scan
    let (vol, usn, tf): (String, i64, i64) =
        c.query_row("SELECT volume, usn, total_files FROM scan WHERE id = 1", [], |r| Ok((r.get(0)?, r.get(1)?, r.get(2)?))).unwrap();
    assert_eq!((vol.as_str(), usn, tf), ("D:", 77, 6));
    // 인덱스는 삽입 뒤에 만들어져 있고 ANALYZE 통계가 있다
    assert_eq!(one::<i64>(&c, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name IN ('dir_alloc','dir_files','dir_parent','file_alloc','file_ext','file_mtime')"), 6);
    assert!(one::<i64>(&c, "SELECT COUNT(*) FROM sqlite_stat1") > 0);
    // 경로 재구성 (재귀 CTE)
    let path: String = c
        .query_row(
            "WITH RECURSIVE up(id, parent_id, p) AS (SELECT id, parent_id, name FROM dir WHERE id = 23
               UNION ALL SELECT d.id, d.parent_id, d.name || '\\' || up.p FROM dir d JOIN up ON d.id = up.parent_id WHERE d.parent_id IS NOT NULL)
             SELECT p FROM up ORDER BY length(p) DESC LIMIT 1",
            [],
            |r| r.get(0),
        )
        .unwrap();
    assert_eq!(path, r"proj\node_modules\lib");
    drop(c);
    let _ = std::fs::remove_dir_all(&dir);
}

#[test]
fn tags_and_candidates_follow_rules_but_never_system_folders() {
    let dir = temp_dir("rules");
    let p = build(&dir, &sample_index(), &opts());
    let c = ro(&p);
    assert_eq!(one::<String>(&c, "SELECT tag FROM dir WHERE name = 'node_modules'"), "node_modules");
    // Windows\Temp 는 이름이 temp라 태그는 붙지만 제안은 없다
    assert_eq!(one::<String>(&c, "SELECT tag FROM dir WHERE id = 31"), "temp");
    let mut st = c.prepare("SELECT kind, dir_id, file_id, reclaim, rule FROM candidate ORDER BY reclaim DESC").unwrap();
    let rows: Vec<(String, i64, Option<i64>, i64, String)> =
        st.query_map([], |r| Ok((r.get(0)?, r.get(1)?, r.get(2)?, r.get(3)?, r.get(4)?))).unwrap().map(|r| r.unwrap()).collect();
    assert_eq!(rows.len(), 2, "{rows:?}");
    assert_eq!(rows[0], ("file".into(), 42, Some(43), 80 << 20, "old-installers".into()));
    assert_eq!(rows[1], ("dir".into(), 22, None, 30 << 20, "node-modules".into()));
    assert_eq!(one::<i64>(&c, "SELECT COUNT(*) FROM candidate WHERE dir_id IN (30, 31) OR file_id = 32"), 0);
    drop(st);
    drop(c);
    let _ = std::fs::remove_dir_all(&dir);
}

#[test]
fn small_files_option_keeps_aggregates() {
    let dir = temp_dir("small");
    let mut o = opts();
    o.min_file_alloc = 1 << 20;
    let p = build(&dir, &sample_index(), &o);
    let c = ro(&p);
    assert_eq!(one::<i64>(&c, "SELECT COUNT(*) FROM file"), 4, "1MB 이상만");
    assert_eq!(one::<i64>(&c, "SELECT total_files FROM dir WHERE id = 5"), 6, "집계에는 모두 남는다");
    assert_eq!(one::<i64>(&c, "SELECT SUM(files) FROM ext_stat"), 6);
    drop(c);
    let _ = std::fs::remove_dir_all(&dir);
}

#[test]
fn snapshot_names_roundtrip_and_retention() {
    let n = snapshot_file_name("{abc}", NOW);
    let stamp = n.strip_prefix("{abc}.").unwrap().strip_suffix(".db").unwrap();
    assert_eq!(stamp.len(), 16);
    let whole_secs = FILETIME_UNIX_EPOCH + (NOW - FILETIME_UNIX_EPOCH) / 10_000_000 * 10_000_000;
    assert_eq!(parse_snapshot_time(stamp), Some(whole_secs));
    assert_eq!(snapshot_file_name("k", FILETIME_UNIX_EPOCH), "k.19700101T000000Z.db");
    assert_eq!(parse_snapshot_time("20261119T010203Z").map(snapshot_time_str), Some("20261119T010203Z".into()));
    assert!(parse_snapshot_time("2026111901020Z").is_none());
    // 매일 분석한 20일치: 최신 + 바로 이전 + 주 간격 → 이전 스냅샷 최대 4개
    let snaps: Vec<(PathBuf, i64)> = (0..20).map(|d| (PathBuf::from(format!("s{d}")), NOW - d * DAY)).collect();
    let del = retention_plan(&snaps);
    let kept: Vec<&str> = snaps.iter().map(|(p, _)| p.to_str().unwrap()).filter(|p| !del.iter().any(|d| d.to_str() == Some(*p))).collect();
    assert_eq!(kept, ["s0", "s1", "s8", "s15"]);
    let snaps: Vec<(PathBuf, i64)> = (0..10).map(|w| (PathBuf::from(format!("w{w}")), NOW - w * 7 * DAY)).collect();
    assert_eq!(retention_plan(&snaps).len(), 10 - 1 - MAX_HISTORY);
}

fn snapshot_time_str(ft: i64) -> String {
    snapshot_file_name("", ft).trim_start_matches('.').trim_end_matches(".db").to_string()
}

#[test]
fn rebuild_keeps_previous_snapshot_for_growth() {
    let dir = temp_dir("growth");
    let idx = sample_index();
    let p1 = build(&dir, &idx, &BuildOptions { started_at: NOW - 3 * DAY, ..opts() });
    // 앱이 이전 스냅샷을 열어 둔 상태에서도 새 스냅샷 만들기가 성공한다
    let held = ro(&p1);
    let p2 = build(&dir, &idx, &opts());
    assert_ne!(p1, p2);
    let list = list_snapshots(&dir, "test");
    assert_eq!(list.len(), 2);
    assert_eq!(list[0].0, p2);
    drop(held);
    // 중간에 끊긴 임시 파일은 다음 분석이 지운다
    std::fs::write(dir.join("test.1234.tmp"), b"x").unwrap();
    build(&dir, &idx, &BuildOptions { started_at: NOW + DAY, ..opts() });
    assert!(!dir.join("test.1234.tmp").exists());
    let _ = std::fs::remove_dir_all(&dir);
}

#[test]
fn cancel_leaves_no_files() {
    let dir = temp_dir("cancel");
    let ex = extract(&sample_index());
    let r = build_into_dir(&dir, "test", &ex, &RuleSet::defaults(), &opts(), &|_, _| {}, &AtomicBool::new(true));
    assert!(r.is_err());
    assert_eq!(std::fs::read_dir(&dir).unwrap().count(), 0);
    let _ = std::fs::remove_dir_all(&dir);
}

#[test]
fn volume_keys() {
    assert!(volume_key("C:").starts_with('{') || volume_key("C:") == "C");
    assert_eq!(volume_key(r"D:\Work\"), volume_key("d:/work"));
    assert!(volume_key(r"D:\Work").starts_with("dir-"));
    // C#과 같은 값 (tests/Foldy.Analysis.Tests 의 같은 시험)
    assert_eq!(volume_key(r"C:\Temp\foldy"), "dir-6f71d92211e373a8");
    assert_eq!(extension("a.TXT"), "txt");
    assert_eq!(extension(".gitignore"), "");
    assert_eq!(extension("noext."), "");
    assert_eq!(extension("x.verylongextension123"), "");
}

/// 합성 색인 (`dirs_per_level`^3 폴더, 폴더마다 `files_per_leaf` 파일).
pub(crate) fn synthetic(total_files: usize) -> VolumeIndex {
    let mut v = VolumeIndex::new_ntfs("S:", total_files + total_files / 8);
    v.insert_build(5, &e(5, ".", true, 0, NOW));
    let mut next: u32 = 16;
    let exts = ["txt", "jpg", "dll", "js", "mp4", "zip", "cs", "json", "png", "log"];
    let mut leaves = Vec::new();
    for a in 0..40 {
        let ida = next;
        next += 1;
        v.insert_build(ida, &e(5, &format!("top-{a:02}"), true, 0, NOW));
        for b in 0..16 {
            let idb = next;
            next += 1;
            let name = if b == 3 { "node_modules".to_string() } else { format!("mid-{b:02}") };
            v.insert_build(idb, &e(ida, &name, true, 0, NOW));
            if b == 3 {
                v.insert_build(next, &e(ida, "package.json", false, 100, NOW));
                next += 1;
            }
            for c in 0..16 {
                let idc = next;
                next += 1;
                v.insert_build(idc, &e(idb, &format!("leaf-{c:02}"), true, 0, NOW));
                leaves.push(idc);
            }
        }
    }
    let per = total_files.div_ceil(leaves.len());
    let mut seed: u64 = 0x9e37_79b9_7f4a_7c15;
    let mut made = 0usize;
    'outer: for &leaf in &leaves {
        for k in 0..per {
            if made >= total_files {
                break 'outer;
            }
            seed ^= seed << 13;
            seed ^= seed >> 7;
            seed ^= seed << 17;
            let size = seed % (4 << 20);
            let age = (seed >> 40) as i64 % 900;
            let ext = exts[(seed >> 20) as usize % exts.len()];
            v.insert_build(next, &e(leaf, &format!("file-{k:05}-{:08x}.{ext}", seed as u32), false, size, NOW - age * DAY));
            next += 1;
            made += 1;
        }
    }
    v.finalize();
    v
}

#[test]
fn synthetic_tree_builds() {
    let dir = temp_dir("synth");
    let idx = synthetic(20_000);
    let p = build(&dir, &idx, &opts());
    let c = ro(&p);
    assert_eq!(one::<i64>(&c, "SELECT COUNT(*) FROM file"), 20_000 + 40);
    assert_eq!(one::<i64>(&c, "SELECT total_files FROM dir WHERE id = 5"), 20_000 + 40);
    let root_alloc: i64 = one(&c, "SELECT alloc FROM dir WHERE id = 5");
    assert_eq!(one::<i64>(&c, "SELECT SUM(alloc) FROM file"), root_alloc, "파일 합 = 루트 누적 크기");
    assert_eq!(one::<i64>(&c, "SELECT SUM(alloc) FROM dir WHERE depth = 1"), root_alloc);
    drop(c);
    let _ = std::fs::remove_dir_all(&dir);
}

/// 100만 개 합성 색인 측정 (설계서 7.3 "프로토타입에서 재야 한다").
/// `cargo test --release -p foldy-index bench_1m -- --ignored --nocapture`
#[test]
#[ignore]
fn bench_1m() {
    let dir = temp_dir("bench");
    let t = Instant::now();
    let idx = synthetic(1_000_000);
    println!("합성 색인: {} 항목, {:?}", idx.live_count(), t.elapsed());
    for min in [0u64, 1 << 20] {
        let t = Instant::now();
        let ex = extract(&idx);
        let ms_extract = t.elapsed().as_millis();
        let o = BuildOptions { min_file_alloc: min, ..opts() };
        let (p, st) = build_into_dir(&dir, "bench", &ex, &RuleSet::defaults(), &o, &|_, _| {}, &AtomicBool::new(false)).unwrap();
        println!(
            "min_file_alloc={min}: 복사 {ms_extract}ms, 집계+규칙 {}ms, INSERT {}ms, 인덱스 {}ms, ANALYZE {}ms, 전체 {:?}, DB {:.1}MB (폴더 {}, 파일 {}, file 행 {}, 제안 {})",
            st.ms_aggregate,
            st.ms_insert,
            st.ms_index,
            st.ms_analyze,
            t.elapsed(),
            st.db_bytes as f64 / 1048576.0,
            st.dirs,
            st.files,
            st.file_rows,
            st.candidates
        );
        let _ = std::fs::remove_file(p);
    }
    let _ = std::fs::remove_dir_all(&dir);
}
