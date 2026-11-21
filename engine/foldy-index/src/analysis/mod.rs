//! 디스크 분석 스냅샷 DB (설계서 7.3 "디스크 분석 DB (SQLite)").
//!
//! 메모리 색인(폴더 누적 크기가 이미 계산된 상태)을 그대로 훑어 볼륨별 SQLite 파일을 만든다. 디스크를 다시
//! 읽지 않는다. 순서:
//!
//! 1. [`extract`]: 색인 읽기 잠금을 잡은 채 필요한 값만 복사한다 (100만 개에 수백 ms). 잠금을 오래 잡으면
//!    USN 갱신 스레드(쓰기 잠금)가 기다리고, 그 뒤에 검색까지 줄을 서므로 SQLite 쓰기는 잠금 밖에서 한다.
//! 2. [`aggregate`]: 폴더별 파일 수·폴더 수·최신/최고(最古) 수정 시각을 깊이 역순으로 올려 쌓는다.
//! 3. 규칙 엔진([`rules`])이 `dir.tag`를 붙이고 `candidate`(정리 제안)를 채운다.
//! 4. [`write_db`]: 임시 파일에 journal_mode=OFF, synchronous=OFF로 한 트랜잭션 대량 INSERT →
//!    삽입이 끝난 뒤 인덱스 생성 → ANALYZE → 원자적 이름 변경.
//!
//! 파일 이름은 `<볼륨 키>.<yyyyMMddTHHmmssZ>.db`이다. 설계서는 `<볼륨GUID>.db` 하나를 덮어쓰지만, 앱이
//! 이전 스냅샷을 열어 둔 채면 Windows에서 덮어쓰는 이름 변경이 공유 위반으로 실패하므로 시각을 붙인 새 이름으로
//! 바꾸고(항상 성공), 보관 정책([`retention_plan`])으로 오래된 것을 지운다. 최신 경로는 AnalysisInfo로 알려 준다.
//!
//! 시각은 모두 FILETIME(UTC 100ns)이다 (색인의 mtime과 같은 단위).

pub mod rules;

use foldy_ntfs::VolumeIndex;
use foldy_ntfs::index::flags;
use rules::{DirFacts, FileFacts, RuleSet};
use rusqlite::{Connection, params};
use std::collections::HashMap;
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicBool, Ordering};
use std::time::Instant;

/// 스키마 버전 (PRAGMA user_version). C# Foldy.Analysis.AnalysisSchema.Version과 같아야 한다.
pub const SCHEMA_VERSION: i32 = 1;
/// PRAGMA application_id = "Fold"
pub const APPLICATION_ID: i32 = 0x466F_6C64;
/// 비교용 이전 스냅샷 최대 개수 (주 1회 간격)
pub const MAX_HISTORY: usize = 4;
/// 파일 규칙 하나가 낼 수 있는 최대 제안 수 (큰 것부터)
const MAX_FILE_CANDIDATES_PER_RULE: usize = 5000;
const NONE: u32 = u32::MAX;
const MAX_DEPTH: usize = 4096;

/// 설계서 7.3 스키마. 인덱스는 삽입 뒤에 만든다 ([`INDEXES`]).
pub const SCHEMA: &str = "
CREATE TABLE scan   (id INTEGER PRIMARY KEY, volume TEXT, started_at INTEGER, finished_at INTEGER,
                     usn INTEGER, total_files INTEGER, total_dirs INTEGER, total_alloc INTEGER);
CREATE TABLE dir    (id INTEGER PRIMARY KEY, parent_id INTEGER, name TEXT, depth INTEGER,
                     size INTEGER, alloc INTEGER,
                     own_files INTEGER, total_files INTEGER, total_dirs INTEGER,
                     newest_mtime INTEGER, oldest_mtime INTEGER, tag TEXT);
CREATE TABLE file   (id INTEGER PRIMARY KEY, dir_id INTEGER, name TEXT, ext TEXT,
                     size INTEGER, alloc INTEGER, mtime INTEGER, atime INTEGER, attrs INTEGER);
CREATE TABLE ext_stat (ext TEXT PRIMARY KEY, files INTEGER, size INTEGER, alloc INTEGER);
CREATE TABLE candidate (id INTEGER PRIMARY KEY, kind TEXT, dir_id INTEGER, file_id INTEGER,
                     reclaim INTEGER, rule TEXT, reason TEXT);
";

/// 삽입 후 만드는 인덱스. dir_parent는 (parent_id, name)으로 만들어 트리맵 자식 조회와
/// "지난 분석 대비"(이전 DB를 ATTACH해 이름으로 짝 맞추기)를 함께 받친다.
pub const INDEXES: &str = "
CREATE INDEX dir_alloc  ON dir(alloc DESC);
CREATE INDEX dir_files  ON dir(total_files DESC);
CREATE INDEX dir_parent ON dir(parent_id, name);
CREATE INDEX file_alloc ON file(alloc DESC);
CREATE INDEX file_dir   ON file(dir_id);
CREATE INDEX file_ext   ON file(ext, alloc DESC);
CREATE INDEX file_mtime ON file(mtime);
";

// ───────────── 1. 색인에서 복사 ─────────────

#[derive(Debug, Clone, Copy)]
pub struct XDir {
    /// 색인 노드 id (MFT 레코드 번호). DB의 dir.id
    pub id: u32,
    /// 부모의 `dirs` 위치 (루트는 NONE)
    pub parent: u32,
    name_off: u32,
    name_len: u32,
    pub size: u64,
    pub alloc: u64,
    pub mtime: i64,
    pub flags: u16,
}

#[derive(Debug, Clone, Copy)]
pub struct XFile {
    pub id: u32,
    /// `dirs` 위치
    pub dir: u32,
    name_off: u32,
    name_len: u32,
    pub size: u64,
    pub alloc: u64,
    pub mtime: i64,
    pub flags: u16,
}

/// 색인에서 복사한 분석 재료. `dirs[0]`이 루트다.
pub struct Extracted {
    pub root_path: String,
    names: String,
    pub dirs: Vec<XDir>,
    pub files: Vec<XFile>,
}

impl Extracted {
    pub fn dir_name(&self, d: &XDir) -> &str {
        &self.names[d.name_off as usize..(d.name_off + d.name_len) as usize]
    }
    pub fn file_name(&self, f: &XFile) -> &str {
        &self.names[f.name_off as usize..(f.name_off + f.name_len) as usize]
    }
}

/// 색인에 넣을 노드인지: 사용 중, NTFS 메타 파일 아님, 하드링크 추가 이름 아님(크기를 두 번 세지 않는다).
fn wanted(n: &foldy_ntfs::Node) -> bool {
    n.in_use() && n.flags & (flags::META | flags::EXTRA_LINK) == 0
}

/// 색인을 훑어 폴더·파일을 복사한다. 루트까지 부모 사슬이 이어지지 않는 고아 항목은 뺀다.
pub fn extract(idx: &VolumeIndex) -> Extracted {
    let root = idx.root();
    let mut n: u32 = 0;
    while idx.node(n).is_some() {
        n += 1;
    }
    // 노드 id → 후보 폴더 순번
    let mut cand = vec![NONE; n as usize];
    let mut cand_ids: Vec<u32> = vec![root];
    if (root as usize) < cand.len() {
        cand[root as usize] = 0;
    }
    for i in 0..n {
        if i == root {
            continue;
        }
        if let Some(nd) = idx.node(i)
            && wanted(nd)
            && nd.is_dir()
        {
            cand[i as usize] = cand_ids.len() as u32;
            cand_ids.push(i);
        }
    }
    // 루트까지 닿는지 (0=미정, 1=닿음, 2=고아)
    let mut reach = vec![0u8; cand_ids.len()];
    reach[0] = 1;
    let mut chain = Vec::new();
    for start in 0..cand_ids.len() {
        if reach[start] != 0 {
            continue;
        }
        chain.clear();
        let mut cur = start;
        let verdict = loop {
            if reach[cur] != 0 {
                break reach[cur];
            }
            chain.push(cur);
            let p = idx.node(cand_ids[cur]).map(|x| x.parent).unwrap_or(NONE);
            let pc = if (p as usize) < cand.len() { cand[p as usize] } else { NONE };
            if pc == NONE || pc as usize == cur || chain.len() > MAX_DEPTH {
                break 2;
            }
            cur = pc as usize;
        };
        for &c in &chain {
            reach[c] = verdict;
        }
    }
    // 최종 폴더 순번
    let mut fid = vec![NONE; n.max(root + 1) as usize];
    let mut dir_nodes = Vec::with_capacity(cand_ids.len());
    for (c, &id) in cand_ids.iter().enumerate() {
        if reach[c] == 1 {
            fid[id as usize] = dir_nodes.len() as u32;
            dir_nodes.push(id);
        }
    }
    let mut names = String::with_capacity(n as usize * 14);
    let mut push = |s: &str| {
        let off = names.len() as u32;
        names.push_str(s);
        (off, s.len() as u32)
    };
    let mut dirs = Vec::with_capacity(dir_nodes.len());
    for (k, &id) in dir_nodes.iter().enumerate() {
        let nd = idx.node(id).copied().unwrap_or_default();
        let (name_off, name_len) = if k == 0 { push(idx.root_path()) } else { push(idx.name(id)) };
        let parent = if k == 0 { NONE } else { fid[nd.parent as usize] };
        dirs.push(XDir { id, parent, name_off, name_len, size: nd.size, alloc: nd.alloc, mtime: nd.mtime, flags: nd.flags });
    }
    let mut files = Vec::with_capacity((n as usize).saturating_sub(dirs.len()));
    for i in 0..n {
        let Some(nd) = idx.node(i) else { continue };
        if i == root || !wanted(nd) || nd.is_dir() {
            continue;
        }
        let d = fid.get(nd.parent as usize).copied().unwrap_or(NONE);
        if d == NONE {
            continue;
        }
        let (name_off, name_len) = push(idx.name(i));
        files.push(XFile { id: i, dir: d, name_off, name_len, size: nd.size, alloc: nd.alloc, mtime: nd.mtime, flags: nd.flags });
    }
    Extracted { root_path: idx.root_path().to_string(), names, dirs, files }
}

// ───────────── 2. 폴더별 집계 ─────────────

pub struct Aggregates {
    pub depth: Vec<u32>,
    /// 깊이 오름차순 폴더 순번 (부모가 항상 자식보다 앞)
    pub order: Vec<u32>,
    pub own_files: Vec<u64>,
    pub total_files: Vec<u64>,
    pub total_dirs: Vec<u64>,
    pub newest: Vec<i64>,
    pub oldest: Vec<i64>,
}

pub fn aggregate(ex: &Extracted) -> Aggregates {
    let nd = ex.dirs.len();
    let mut depth = vec![u32::MAX; nd];
    if nd > 0 {
        depth[0] = 0;
    }
    let mut stack = Vec::new();
    for s in 0..nd {
        let mut cur = s;
        while depth[cur] == u32::MAX {
            stack.push(cur);
            cur = ex.dirs[cur].parent as usize;
        }
        let mut d = depth[cur];
        while let Some(c) = stack.pop() {
            d += 1;
            depth[c] = d;
        }
    }
    // 깊이별 계수 정렬
    let max_d = depth.iter().copied().max().unwrap_or(0) as usize;
    let mut buckets = vec![0u32; max_d + 2];
    for &d in &depth {
        buckets[d as usize + 1] += 1;
    }
    for i in 1..buckets.len() {
        buckets[i] += buckets[i - 1];
    }
    let mut order = vec![0u32; nd];
    for (i, &d) in depth.iter().enumerate() {
        let slot = &mut buckets[d as usize];
        order[*slot as usize] = i as u32;
        *slot += 1;
    }
    let mut own_files = vec![0u64; nd];
    let mut newest = vec![i64::MIN; nd];
    let mut oldest = vec![i64::MAX; nd];
    for f in &ex.files {
        let d = f.dir as usize;
        own_files[d] += 1;
        if f.mtime > 0 {
            newest[d] = newest[d].max(f.mtime);
            oldest[d] = oldest[d].min(f.mtime);
        }
    }
    let mut total_files = own_files.clone();
    let mut total_dirs = vec![0u64; nd];
    for &d in order.iter().rev() {
        let d = d as usize;
        let p = ex.dirs[d].parent;
        if p == NONE {
            continue;
        }
        let p = p as usize;
        total_files[p] += total_files[d];
        total_dirs[p] += total_dirs[d] + 1;
        newest[p] = newest[p].max(newest[d]);
        oldest[p] = oldest[p].min(oldest[d]);
    }
    // 파일이 하나도 없는 폴더는 폴더 자신의 수정 시각을 쓴다.
    for d in 0..nd {
        if newest[d] == i64::MIN {
            newest[d] = ex.dirs[d].mtime;
            oldest[d] = ex.dirs[d].mtime;
        }
    }
    Aggregates { depth, order, own_files, total_files, total_dirs, newest, oldest }
}

/// 확장자: 마지막 점 뒤 (점이 맨 앞·맨 끝이면 없음), 소문자, 16자 넘으면 없음. C# AnalysisText.Extension과 같다.
pub fn extension(name: &str) -> String {
    match name.rfind('.') {
        Some(i) if i > 0 && i + 1 < name.len() => {
            let e = &name[i + 1..];
            if e.chars().count() > 16 { String::new() } else { e.to_lowercase() }
        }
        _ => String::new(),
    }
}

// ───────────── 3. 규칙: 태그와 정리 제안 ─────────────

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Candidate {
    pub kind: &'static str,
    /// 폴더 제안은 그 폴더, 파일 제안은 파일이 든 폴더 (DB dir.id)
    pub dir_id: u32,
    pub file_id: Option<u32>,
    pub reclaim: u64,
    pub rule: String,
    pub reason: String,
}

pub struct Classified {
    /// 폴더 순번별 태그
    pub tags: Vec<Option<String>>,
    pub candidates: Vec<Candidate>,
}

fn csr(n: usize, keys: impl Iterator<Item = u32> + Clone) -> (Vec<u32>, Vec<u32>) {
    let mut off = vec![0u32; n + 1];
    for k in keys.clone() {
        if k != NONE {
            off[k as usize + 1] += 1;
        }
    }
    for i in 1..off.len() {
        off[i] += off[i - 1];
    }
    let mut fill = off.clone();
    let total = off[n] as usize;
    let mut items = vec![0u32; total];
    for (i, k) in keys.enumerate() {
        if k != NONE {
            items[fill[k as usize] as usize] = i as u32;
            fill[k as usize] += 1;
        }
    }
    (off, items)
}

pub fn classify(ex: &Extracted, ag: &Aggregates, rules: &RuleSet, now: i64) -> Classified {
    let nd = ex.dirs.len();
    let mut tags: Vec<Option<String>> = vec![None; nd];
    let mut candidates = Vec::new();
    if nd == 0 {
        return Classified { tags, candidates };
    }
    // 규칙의 paths는 볼륨 루트 기준이므로 --scan-dir "C:\Users\me"여도 상대 경로는 "\Users\me\..."로 만든다.
    // 드라이브가 없는 경로(UNC)는 분석 루트 기준. C# SnapshotBuilder.Classify와 같다.
    let trimmed = ex.root_path.trim_end_matches('\\');
    let has_drive = trimmed.len() >= 2 && trimmed.as_bytes()[1] == b':' && trimmed.is_char_boundary(2);
    let base = if has_drive { trimmed[..2].to_string() } else { trimmed.to_string() };
    let mut rel: Vec<String> = vec![String::new(); nd];
    if has_drive {
        rel[0] = trimmed[2..].to_string();
    }
    for &d in ag.order.iter().skip(1) {
        let d = d as usize;
        let p = ex.dirs[d].parent as usize;
        let mut s = String::with_capacity(rel[p].len() + 1 + ex.dirs[d].name_len as usize);
        s.push_str(&rel[p]);
        s.push('\\');
        s.push_str(ex.dir_name(&ex.dirs[d]));
        rel[d] = s;
    }
    let (doff, ditems) = csr(nd, ex.dirs.iter().map(|d| d.parent));
    let (foff, fitems) = csr(nd, ex.files.iter().map(|f| f.dir));
    let siblings_of = |p: usize| -> Vec<String> {
        let mut v = Vec::new();
        for &c in &ditems[doff[p] as usize..doff[p + 1] as usize] {
            v.push(ex.dir_name(&ex.dirs[c as usize]).to_lowercase());
        }
        for &f in &fitems[foff[p] as usize..foff[p + 1] as usize] {
            v.push(ex.file_name(&ex.files[f as usize]).to_lowercase());
        }
        v
    };
    // covered[d]: d 자신이나 조상이 이미 제안됨 → 그 아래는 제안하지 않는다 (용량을 두 번 세지 않게)
    let mut covered = vec![false; nd];
    for &d in ag.order.iter().skip(1) {
        let d = d as usize;
        let x = &ex.dirs[d];
        let p = x.parent as usize;
        let parent_covered = covered[p];
        let full = format!("{base}{}", rel[d]);
        let facts = DirFacts {
            name: ex.dir_name(x),
            rel_path: &rel[d],
            full_path: &full,
            alloc: x.alloc,
            newest_mtime: ag.newest[d],
            system_or_reparse: x.flags & (flags::SYSTEM | flags::REPARSE) != 0,
        };
        let verdict = rules.match_dir(&facts, now, &mut || siblings_of(p));
        let mut here = false;
        if let Some(v) = verdict {
            tags[d] = v.rule.tag.clone();
            if v.suggest && !parent_covered {
                here = true;
                candidates.push(Candidate {
                    kind: "dir",
                    dir_id: x.id,
                    file_id: None,
                    reclaim: x.alloc,
                    rule: v.rule.id.clone(),
                    reason: v.rule.reason_text(),
                });
            }
        }
        covered[d] = parent_covered || here;
    }
    if rules.has_file_rules() {
        // 확장자로 먼저 거른다 (확장자 조건이 없는 파일 규칙이 있으면 모든 파일을 본다)
        let file_rules: Vec<&rules::Rule> = rules.rules.iter().filter(|r| r.kind == rules::RuleKind::File && r.suggest).collect();
        let any_ext = file_rules.iter().any(|r| r.extensions.is_empty());
        let exts: std::collections::HashSet<&str> = file_rules.iter().flat_map(|r| r.extensions.iter().map(String::as_str)).collect();
        let mut per_rule: HashMap<String, Vec<Candidate>> = HashMap::new();
        for f in &ex.files {
            let d = f.dir as usize;
            if covered[d] {
                continue;
            }
            let name = ex.file_name(f);
            let ext = extension(name);
            if !any_ext && !exts.contains(ext.as_str()) {
                continue;
            }
            let rel_path = format!("{}\\{name}", rel[d]);
            let full = format!("{base}{rel_path}");
            let facts = FileFacts {
                name,
                ext: &ext,
                rel_path: &rel_path,
                full_path: &full,
                alloc: f.alloc,
                mtime: f.mtime,
                system_or_reparse: f.flags & (flags::SYSTEM | flags::REPARSE) != 0,
            };
            if let Some(r) = rules.match_file(&facts, now) {
                per_rule.entry(r.id.clone()).or_default().push(Candidate {
                    kind: "file",
                    dir_id: ex.dirs[d].id,
                    file_id: Some(f.id),
                    reclaim: f.alloc,
                    rule: r.id.clone(),
                    reason: r.reason_text(),
                });
            }
        }
        let mut keys: Vec<String> = per_rule.keys().cloned().collect();
        keys.sort();
        for k in keys {
            let mut v = per_rule.remove(&k).unwrap_or_default();
            v.sort_by_key(|c| std::cmp::Reverse(c.reclaim));
            v.truncate(MAX_FILE_CANDIDATES_PER_RULE);
            candidates.extend(v);
        }
    }
    Classified { tags, candidates }
}

// ───────────── 4. SQLite 쓰기 ─────────────

#[derive(Debug, Clone, Default)]
pub struct BuildOptions {
    /// scan.volume에 넣을 이름 ("C:" 또는 폴더 경로)
    pub volume: String,
    /// 분석 시작 시각 (FILETIME). 규칙의 "N일 넘게" 기준이기도 하다.
    pub started_at: i64,
    pub usn: i64,
    /// 이보다 작은 파일은 file 테이블에 넣지 않고 dir 집계와 ext_stat에만 남긴다 (0 = 모두 넣음, 설계서 7.3 옵션)
    pub min_file_alloc: u64,
}

#[derive(Debug, Clone, Default)]
pub struct BuildStats {
    pub dirs: u64,
    pub files: u64,
    pub file_rows: u64,
    pub candidates: u64,
    pub total_alloc: u64,
    pub db_bytes: u64,
    pub ms_aggregate: u128,
    pub ms_insert: u128,
    pub ms_index: u128,
    pub ms_analyze: u128,
}

fn i(v: u64) -> i64 {
    v.min(i64::MAX as u64) as i64
}

fn attrs_of(fl: u16, dir: bool) -> i64 {
    let mut a = 0i64;
    if fl & flags::HIDDEN != 0 {
        a |= 0x2;
    }
    if fl & flags::SYSTEM != 0 {
        a |= 0x4;
    }
    if dir {
        a |= 0x10;
    }
    if fl & flags::REPARSE != 0 {
        a |= 0x400;
    }
    a
}

/// 진행률 콜백: (0~1000, 단계 설명)
pub type Progress<'a> = &'a dyn Fn(u16, &str);

fn cancelled(c: &AtomicBool) -> Result<(), String> {
    if c.load(Ordering::Relaxed) { Err("취소됨".into()) } else { Ok(()) }
}

/// `path`(임시 파일)에 스냅샷 DB를 쓴다. 이미 있으면 지우고 새로 만든다.
pub fn write_db(
    path: &Path,
    ex: &Extracted,
    rules: &RuleSet,
    opts: &BuildOptions,
    progress: Progress<'_>,
    cancel: &AtomicBool,
) -> Result<BuildStats, String> {
    let e = |x: rusqlite::Error| x.to_string();
    let mut st = BuildStats::default();
    let t = Instant::now();
    progress(150, "폴더 집계");
    let ag = aggregate(ex);
    let cls = classify(ex, &ag, rules, opts.started_at);
    st.ms_aggregate = t.elapsed().as_millis();
    cancelled(cancel)?;

    let _ = std::fs::remove_file(path);
    let mut conn = Connection::open(path).map_err(e)?;
    conn.execute_batch(
        "PRAGMA journal_mode=OFF; PRAGMA synchronous=OFF; PRAGMA locking_mode=EXCLUSIVE;
         PRAGMA temp_store=MEMORY; PRAGMA cache_size=-131072;",
    )
    .map_err(e)?;
    conn.execute_batch(SCHEMA).map_err(e)?;
    conn.pragma_update(None, "user_version", SCHEMA_VERSION).map_err(e)?;
    conn.pragma_update(None, "application_id", APPLICATION_ID).map_err(e)?;

    let t = Instant::now();
    let total_alloc = ex.dirs.first().map(|r| r.alloc).unwrap_or(0);
    let tx = conn.transaction().map_err(e)?;
    {
        progress(200, "폴더 쓰는 중");
        let mut ins = tx
            .prepare("INSERT INTO dir (id,parent_id,name,depth,size,alloc,own_files,total_files,total_dirs,newest_mtime,oldest_mtime,tag) VALUES (?1,?2,?3,?4,?5,?6,?7,?8,?9,?10,?11,?12)")
            .map_err(e)?;
        for (k, d) in ex.dirs.iter().enumerate() {
            let parent = if d.parent == NONE { None } else { Some(ex.dirs[d.parent as usize].id as i64) };
            ins.execute(params![
                d.id as i64,
                parent,
                ex.dir_name(d),
                ag.depth[k] as i64,
                i(d.size),
                i(d.alloc),
                i(ag.own_files[k]),
                i(ag.total_files[k]),
                i(ag.total_dirs[k]),
                ag.newest[k],
                ag.oldest[k],
                cls.tags[k].as_deref(),
            ])
            .map_err(e)?;
        }
        cancelled(cancel)?;
        progress(300, "파일 쓰는 중");
        let mut ins = tx.prepare("INSERT INTO file (id,dir_id,name,ext,size,alloc,mtime,atime,attrs) VALUES (?1,?2,?3,?4,?5,?6,?7,NULL,?8)").map_err(e)?;
        let mut ext_stat: HashMap<String, (u64, u64, u64)> = HashMap::new();
        let n = ex.files.len().max(1);
        for (k, f) in ex.files.iter().enumerate() {
            let name = ex.file_name(f);
            let ext = extension(name);
            if f.alloc >= opts.min_file_alloc {
                ins.execute(params![f.id as i64, ex.dirs[f.dir as usize].id as i64, name, &ext, i(f.size), i(f.alloc), f.mtime, attrs_of(f.flags, false)])
                    .map_err(e)?;
                st.file_rows += 1;
            }
            let s = ext_stat.entry(ext).or_default();
            s.0 += 1;
            s.1 += f.size;
            s.2 += f.alloc;
            if k % 65536 == 0 {
                cancelled(cancel)?;
                progress(300 + (k * 450 / n) as u16, "파일 쓰는 중");
            }
        }
        let mut ins = tx.prepare("INSERT INTO ext_stat (ext,files,size,alloc) VALUES (?1,?2,?3,?4)").map_err(e)?;
        for (ext, (c, s, a)) in &ext_stat {
            ins.execute(params![ext, i(*c), i(*s), i(*a)]).map_err(e)?;
        }
        let mut ins = tx.prepare("INSERT INTO candidate (kind,dir_id,file_id,reclaim,rule,reason) VALUES (?1,?2,?3,?4,?5,?6)").map_err(e)?;
        for c in &cls.candidates {
            ins.execute(params![c.kind, c.dir_id as i64, c.file_id.map(|x| x as i64), i(c.reclaim), &c.rule, &c.reason]).map_err(e)?;
        }
        let finished = now_filetime();
        tx.execute(
            "INSERT INTO scan (id,volume,started_at,finished_at,usn,total_files,total_dirs,total_alloc) VALUES (1,?1,?2,?3,?4,?5,?6,?7)",
            params![&opts.volume, opts.started_at, finished, opts.usn, ex.files.len() as i64, ex.dirs.len() as i64, i(total_alloc)],
        )
        .map_err(e)?;
    }
    tx.commit().map_err(e)?;
    st.ms_insert = t.elapsed().as_millis();
    cancelled(cancel)?;

    let t = Instant::now();
    progress(780, "인덱스 만드는 중");
    // 정렬 작업 스레드를 쓰게 해 CREATE INDEX를 빠르게 한다.
    conn.execute_batch("PRAGMA threads=4;").map_err(e)?;
    conn.execute_batch(INDEXES).map_err(e)?;
    st.ms_index = t.elapsed().as_millis();
    let t = Instant::now();
    progress(950, "통계 계산 (ANALYZE)");
    // 근사 통계로 충분하다 (인덱스마다 일부 행만 본다). 쿼리 계획에는 이 정도면 된다.
    conn.execute_batch("PRAGMA analysis_limit=1000; ANALYZE;").map_err(e)?;
    st.ms_analyze = t.elapsed().as_millis();
    conn.close().map_err(|(_, x)| x.to_string())?;

    st.dirs = ex.dirs.len() as u64;
    st.files = ex.files.len() as u64;
    st.candidates = cls.candidates.len() as u64;
    st.total_alloc = total_alloc;
    st.db_bytes = std::fs::metadata(path).map(|m| m.len()).unwrap_or(0);
    Ok(st)
}

/// 임시 파일에 만든 뒤 `<키>.<시각>.db`로 원자적 이름 변경하고, 보관 정책을 적용한다.
pub fn build_into_dir(
    dir: &Path,
    key: &str,
    ex: &Extracted,
    rules: &RuleSet,
    opts: &BuildOptions,
    progress: Progress<'_>,
    cancel: &AtomicBool,
) -> Result<(PathBuf, BuildStats), String> {
    std::fs::create_dir_all(dir).map_err(|x| format!("{}: {x}", dir.display()))?;
    // 지난번에 중간에 끊긴 임시 파일 정리
    if let Ok(rd) = std::fs::read_dir(dir) {
        for de in rd.flatten() {
            let n = de.file_name().to_string_lossy().into_owned();
            if n.starts_with(&format!("{key}.")) && n.ends_with(".tmp") {
                let _ = std::fs::remove_file(de.path());
            }
        }
    }
    let tmp = dir.join(format!("{key}.{}.tmp", std::process::id()));
    let r = write_db(&tmp, ex, rules, opts, progress, cancel);
    let st = match r {
        Ok(s) => s,
        Err(x) => {
            let _ = std::fs::remove_file(&tmp);
            return Err(x);
        }
    };
    let mut t = opts.started_at;
    let target = loop {
        let p = dir.join(snapshot_file_name(key, t));
        if !p.exists() {
            break p;
        }
        t += 10_000_000; // 같은 초에 두 번 만들면 1초 뒤 이름
    };
    std::fs::rename(&tmp, &target).map_err(|x| {
        let _ = std::fs::remove_file(&tmp);
        format!("이름 변경 실패: {x}")
    })?;
    for p in retention_plan(&list_snapshots(dir, key)) {
        // 앱이 아직 열어 둔 파일은 지워지지 않는다 → 다음 분석 때 다시 시도
        let _ = std::fs::remove_file(p);
    }
    progress(1000, "완료");
    Ok((target, st))
}

// ───────────── 파일 이름, 보관 정책 ─────────────

pub const FILETIME_UNIX_EPOCH: i64 = 116_444_736_000_000_000;

pub fn now_filetime() -> i64 {
    let d = std::time::SystemTime::now().duration_since(std::time::UNIX_EPOCH).unwrap_or_default();
    FILETIME_UNIX_EPOCH + (d.as_nanos() / 100) as i64
}

// 그레고리력 ↔ 일 수 (H. Hinnant의 civil_from_days / days_from_civil)
fn civil_from_days(z: i64) -> (i64, u32, u32) {
    let z = z + 719_468;
    let era = z.div_euclid(146_097);
    let doe = z.rem_euclid(146_097);
    let yoe = (doe - doe / 1460 + doe / 36_524 - doe / 146_096) / 365;
    let y = yoe + era * 400;
    let doy = doe - (365 * yoe + yoe / 4 - yoe / 100);
    let mp = (5 * doy + 2) / 153;
    let d = (doy - (153 * mp + 2) / 5 + 1) as u32;
    let m = if mp < 10 { mp + 3 } else { mp - 9 } as u32;
    (if m <= 2 { y + 1 } else { y }, m, d)
}

fn days_from_civil(y: i64, m: u32, d: u32) -> i64 {
    let y = if m <= 2 { y - 1 } else { y };
    let era = y.div_euclid(400);
    let yoe = y.rem_euclid(400);
    let m = m as i64;
    let doy = (153 * (if m > 2 { m - 3 } else { m + 9 }) + 2) / 5 + d as i64 - 1;
    let doe = yoe * 365 + yoe / 4 - yoe / 100 + doy;
    era * 146_097 + doe - 719_468
}

/// `<키>.yyyyMMddTHHmmssZ.db`
pub fn snapshot_file_name(key: &str, filetime: i64) -> String {
    let secs = (filetime - FILETIME_UNIX_EPOCH).div_euclid(10_000_000);
    let (days, sod) = (secs.div_euclid(86_400), secs.rem_euclid(86_400));
    let (y, m, d) = civil_from_days(days);
    format!("{key}.{y:04}{m:02}{d:02}T{:02}{:02}{:02}Z.db", sod / 3600, sod % 3600 / 60, sod % 60)
}

/// 파일 이름의 시각 부분을 FILETIME으로 (초 단위).
pub fn parse_snapshot_time(stamp: &str) -> Option<i64> {
    let b = stamp.as_bytes();
    if b.len() != 16 || b[8] != b'T' || b[15] != b'Z' || !stamp.is_ascii() {
        return None;
    }
    let num = |r: std::ops::Range<usize>| stamp[r].parse::<i64>().ok();
    let (y, mo, d, h, mi, s) = (num(0..4)?, num(4..6)?, num(6..8)?, num(9..11)?, num(11..13)?, num(13..15)?);
    if !(1..=12).contains(&mo) || !(1..=31).contains(&d) || h > 23 || mi > 59 || s > 60 {
        return None;
    }
    let days = days_from_civil(y, mo as u32, d as u32);
    Some(FILETIME_UNIX_EPOCH + (days * 86_400 + h * 3600 + mi * 60 + s) * 10_000_000)
}

/// 볼륨 키의 스냅샷들, 최신순.
pub fn list_snapshots(dir: &Path, key: &str) -> Vec<(PathBuf, i64)> {
    let mut v = Vec::new();
    let prefix = format!("{key}.");
    if let Ok(rd) = std::fs::read_dir(dir) {
        for de in rd.flatten() {
            let n = de.file_name().to_string_lossy().into_owned();
            if let Some(rest) = n.strip_prefix(&prefix)
                && let Some(stamp) = rest.strip_suffix(".db")
                && let Some(t) = parse_snapshot_time(stamp)
            {
                v.push((de.path(), t));
            }
        }
    }
    v.sort_by_key(|a| std::cmp::Reverse(a.1));
    v
}

/// 지울 스냅샷: 최신 1개 + 바로 이전 1개 + 그보다 오래된 것은 앞서 남긴 것보다 6.5일 이상 오래된 것만
/// (주 1회 간격), 이전 스냅샷은 최대 [`MAX_HISTORY`]개 (설계서 7.3 보관 정책).
pub fn retention_plan(snaps: &[(PathBuf, i64)]) -> Vec<PathBuf> {
    let week = 13 * rules::FILETIME_DAY / 2;
    let mut del = Vec::new();
    let mut kept_hist = 0usize;
    let mut last: Option<i64> = None;
    for (k, (p, t)) in snaps.iter().enumerate() {
        if k == 0 {
            continue;
        }
        let keep = match last {
            None => true,
            Some(l) => kept_hist < MAX_HISTORY && l - t >= week,
        };
        if keep && kept_hist < MAX_HISTORY {
            kept_hist += 1;
            last = Some(*t);
        } else {
            del.push(p.clone());
        }
    }
    del
}

/// 볼륨 키: 드라이브("C:")는 볼륨 GUID("{…}", 드라이브 문자가 바뀌어도 같은 파일), 못 얻으면 문자("C"),
/// 폴더 경로(--scan-dir)는 "dir-<FNV-1a 64 16진수>". C# AnalysisPaths.VolumeKey와 같은 규칙.
pub fn volume_key(volume: &str) -> String {
    let norm = volume.trim().replace('/', "\\");
    let v = norm.trim_end_matches('\\');
    let b = v.as_bytes();
    if b.len() == 2 && b[1] == b':' && b[0].is_ascii_alphabetic() {
        if let Some(g) = volume_guid(&format!("{v}\\")) {
            return g;
        }
        return v[..1].to_uppercase();
    }
    let mut h: u64 = 0xcbf2_9ce4_8422_2325;
    for byte in v.to_lowercase().as_bytes() {
        h ^= *byte as u64;
        h = h.wrapping_mul(0x0000_0100_0000_01b3);
    }
    format!("dir-{h:016x}")
}

fn volume_guid(root: &str) -> Option<String> {
    use windows_sys::Win32::Storage::FileSystem::GetVolumeNameForVolumeMountPointW;
    let wide: Vec<u16> = root.encode_utf16().chain([0]).collect();
    let mut buf = [0u16; 64];
    // SAFETY: wide는 NUL로 끝나는 UTF-16, buf는 길이를 정확히 넘긴 지역 버퍼다.
    let ok = unsafe { GetVolumeNameForVolumeMountPointW(wide.as_ptr(), buf.as_mut_ptr(), buf.len() as u32) };
    if ok == 0 {
        return None;
    }
    let s = String::from_utf16_lossy(&buf[..buf.iter().position(|&c| c == 0).unwrap_or(buf.len())]);
    let a = s.find('{')?;
    let z = s[a..].find('}')? + a;
    Some(s[a..=z].to_lowercase())
}

/// 서비스가 쓰는 기본 폴더 %ProgramData%\Foldy\Analysis
pub fn default_dir() -> PathBuf {
    let pd = std::env::var_os("ProgramData").map(PathBuf::from).unwrap_or_else(|| PathBuf::from(r"C:\ProgramData"));
    pd.join("Foldy").join("Analysis")
}

/// 분석 DB를 읽어도 되는 사용자 (Foldy에서 분석을 요청한 사람). 서비스가 도는 동안 기억한다.
static READERS: std::sync::Mutex<Vec<String>> = std::sync::Mutex::new(Vec::new());

/// 분석 폴더 ACL (설계서 7.3 보안): 상속을 끊고 SYSTEM·Administrators·서비스 계정은 모두, 그리고
/// Foldy에서 분석을 요청한 사용자([`allow_reader`])만 읽기. 예전에는 로그온한 모든 사용자(Interactive)가
/// 읽을 수 있어서, 같은 PC의 다른 계정도 폴더 이름·크기를 볼 수 있었다 (2026-11-21 보안 점검, 사용자 승인).
/// ProgramData 기본 ACL은 Users에게 쓰기까지 주므로, 일반 사용자가 가짜 DB를 넣지 못하게 막는 의미도 있다.
pub fn secure_dir(dir: &Path) -> std::io::Result<()> {
    let readers = READERS.lock().map(|r| r.clone()).unwrap_or_default();
    secure_dir_with(dir, Some(&readers))
}

/// 이 사용자에게 분석 DB 읽기를 허락한다. 이미 허락한 사용자면 아무것도 하지 않는다(요청마다 드는 비용 없음).
/// 처음일 때만 폴더 ACL을 한 번 다시 쓰고, 이미 있는 DB 파일에도 이어 준다.
pub fn allow_reader(dir: &Path, sid: &str) -> std::io::Result<()> {
    if !is_plain_sid(sid) {
        return Ok(());
    }
    {
        let mut r = READERS.lock().map_err(|_| std::io::Error::other("lock"))?;
        if r.iter().any(|s| s.eq_ignore_ascii_case(sid)) {
            return Ok(());
        }
        r.push(sid.to_ascii_uppercase());
    }
    if !dir.exists() {
        return Ok(()); // 첫 분석 때 secure_dir이 READERS로 만든다
    }
    secure_dir(dir)
}

/// SDDL에 그대로 넣어도 되는 SID 문자열인가 (S-1-숫자-…).
fn is_plain_sid(s: &str) -> bool {
    s.len() > 4 && s[..2].eq_ignore_ascii_case("S-") && s[2..].chars().all(|c| c.is_ascii_digit() || c == '-')
}

/// 분석 폴더 SDDL (시험용으로 따로 둔다).
pub(crate) fn analysis_sddl(me: Option<&str>, readers: &[String]) -> String {
    let mut s = String::from("D:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)");
    if let Some(me) = me.filter(|m| is_plain_sid(m)) {
        s.push_str(&format!("(A;OICI;FA;;;{me})"));
    }
    for r in readers.iter().filter(|r| is_plain_sid(r)) {
        s.push_str(&format!("(A;OICI;FRFX;;;{r})"));
    }
    s
}

/// 색인 스냅샷 폴더: SYSTEM·Administrators만 (일반 사용자는 읽기도 불가: 다른 사용자의 파일 이름이 들어 있다).
pub fn secure_dir_private(dir: &Path) -> std::io::Result<()> {
    secure_dir_with(dir, None)
}

fn secure_dir_with(dir: &Path, readers: Option<&[String]>) -> std::io::Result<()> {
    use windows_sys::Win32::Foundation::{HLOCAL, LocalFree};
    use windows_sys::Win32::Security::Authorization::{ConvertStringSecurityDescriptorToSecurityDescriptorW, SDDL_REVISION_1, SE_FILE_OBJECT, SetNamedSecurityInfoW};
    use windows_sys::Win32::Security::{ACL, DACL_SECURITY_INFORMATION, GetSecurityDescriptorDacl, PROTECTED_DACL_SECURITY_INFORMATION, PSECURITY_DESCRIPTOR};
    let sddl = match readers {
        Some(r) => analysis_sddl(crate::acl::current_user_sid().as_deref(), r),
        None => "D:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)".to_string(),
    };
    let wide: Vec<u16> = sddl.encode_utf16().chain([0]).collect();
    let mut sd: PSECURITY_DESCRIPTOR = std::ptr::null_mut();
    // SAFETY: wide는 NUL로 끝나는 UTF-16. 성공하면 sd는 LocalAlloc 메모리이고 아래에서 해제한다.
    if unsafe { ConvertStringSecurityDescriptorToSecurityDescriptorW(wide.as_ptr(), SDDL_REVISION_1, &mut sd, std::ptr::null_mut()) } == 0 {
        return Err(std::io::Error::last_os_error());
    }
    let mut present = 0i32;
    let mut defaulted = 0i32;
    let mut dacl: *mut ACL = std::ptr::null_mut();
    // SAFETY: sd는 위에서 만든 유효한 보안 설명자. dacl은 sd 안을 가리키므로 sd를 풀기 전까지만 쓴다.
    let got = unsafe { GetSecurityDescriptorDacl(sd, &mut present, &mut dacl, &mut defaulted) };
    let path: Vec<u16> = dir.as_os_str().to_string_lossy().encode_utf16().chain([0]).collect();
    // SetNamedSecurityInfoW는 상속되는 ACE를 이미 있는 하위 파일에도 이어 준다 (예전 DB의 Interactive 읽기를 지운다)
    let r = if got == 0 || present == 0 {
        Err(std::io::Error::last_os_error())
    } else {
        // SAFETY: path는 NUL로 끝나는 UTF-16, dacl은 sd 안의 유효한 ACL.
        let code = unsafe {
            SetNamedSecurityInfoW(
                path.as_ptr(),
                SE_FILE_OBJECT,
                DACL_SECURITY_INFORMATION | PROTECTED_DACL_SECURITY_INFORMATION,
                std::ptr::null_mut(),
                std::ptr::null_mut(),
                dacl,
                std::ptr::null(),
            )
        };
        if code == 0 { Ok(()) } else { Err(std::io::Error::from_raw_os_error(code as i32)) }
    };
    // SAFETY: ConvertStringSecurityDescriptorToSecurityDescriptorW가 할당한 메모리를 한 번 해제한다.
    unsafe { LocalFree(sd as HLOCAL) };
    r
}

/// 분석 스레드의 CPU·I/O 우선순위를 낮춘다 (사용자 작업 방해 금지).
pub fn lower_thread_priority() {
    use windows_sys::Win32::System::Threading::{GetCurrentThread, SetThreadPriority, THREAD_MODE_BACKGROUND_BEGIN};
    // SAFETY: 현재 스레드 의사 핸들에 우선순위만 바꾼다.
    unsafe { SetThreadPriority(GetCurrentThread(), THREAD_MODE_BACKGROUND_BEGIN) };
}

#[cfg(test)]
pub(crate) mod tests;
