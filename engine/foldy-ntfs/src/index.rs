//! 볼륨 하나의 메모리 색인 (설계서 "MFT/USN 기반 디스크 색인").
//!
//! - 노드 배열은 MFT 레코드 번호로 바로 찾는다 (USN 레코드의 파일 참조 번호와 같은 공간).
//! - 하드링크의 두 번째 이후 이름은 별도 배열(`links`)에 두고 id 최상위 비트로 구분한다.
//! - 이름은 UTF-8 원본 풀(`names`)과, 검색용 소문자 풀(`fold`, 이름마다 NUL로 끝남) 두 벌이다.
//!   `fold_index`는 (fold 오프셋, 노드 id)를 오프셋 순으로 가진다. 이름이 바뀌면 끝에 덧붙이고
//!   옛 항목은 노드의 `fold_off`와 맞지 않으므로 검색에서 저절로 걸러진다.
//! - 폴더 노드의 size/alloc은 하위 전체 누적값이다 (폴더 자신은 $DATA가 없다).

use crate::record::ParsedRecord;
use rayon::prelude::*;
use std::collections::HashMap;

mod snap;
pub use snap::{SnapshotError, SnapshotMeta};

pub const NO_PARENT: u32 = u32::MAX;
/// 하드링크 추가 이름 노드 id 표시 비트.
pub const LINK_BIT: u32 = 0x8000_0000;
/// 부모 사슬을 따라갈 때의 최대 깊이 (깨진 순환 방지).
const MAX_DEPTH: usize = 4096;

pub mod flags {
    pub const IN_USE: u16 = 0x0001;
    pub const DIR: u16 = 0x0002;
    /// 하드링크의 추가 이름. 크기 누적에서 빼서 두 번 세지 않는다.
    pub const EXTRA_LINK: u16 = 0x0004;
    /// $MFT, $Extend 아래 같은 NTFS 메타 파일. 검색에서 뺀다.
    pub const META: u16 = 0x0008;
    pub const HIDDEN: u16 = 0x0010;
    pub const SYSTEM: u16 = 0x0020;
    /// 실제 $DATA에서 크기를 읽었음 ($FILE_NAME 대체값이 아님)
    pub const HAS_DATA: u16 = 0x0040;
    pub const REPARSE: u16 = 0x0080;
}

/// 노드 하나 (40바이트).
#[derive(Debug, Clone, Copy, Default, PartialEq, Eq)]
#[repr(C)]
pub struct Node {
    pub parent: u32,
    pub name_off: u32,
    pub fold_off: u32,
    pub name_len: u16,
    pub flags: u16,
    pub size: u64,
    pub alloc: u64,
    pub mtime: i64,
}

impl Node {
    pub fn in_use(&self) -> bool {
        self.flags & flags::IN_USE != 0
    }
    pub fn is_dir(&self) -> bool {
        self.flags & flags::DIR != 0
    }
}

/// 레코드 하나가 색인에 넣을 내용 (기본 레코드 + 확장 레코드를 합친 것).
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct Entry {
    /// (부모 레코드, 이름). 첫 번째가 주 이름, 나머지는 하드링크.
    pub names: Vec<(u32, String)>,
    pub is_dir: bool,
    pub size: u64,
    pub alloc: u64,
    pub has_data: bool,
    pub mtime: i64,
    /// Win32 파일 특성 (FILE_ATTRIBUTE_*)
    pub attributes: u32,
}

impl Entry {
    pub fn from_parsed(p: &ParsedRecord) -> Self {
        let (size, alloc, has_data) = p.sizes_or_fallback();
        let mut e = Entry {
            names: Vec::new(),
            is_dir: p.is_dir,
            size,
            alloc,
            has_data,
            mtime: p.mtime.unwrap_or(0),
            attributes: p.attributes,
        };
        e.add_names(p);
        e
    }

    fn add_names(&mut self, p: &ParsedRecord) {
        for n in &p.names {
            let Ok(parent) = u32::try_from(n.parent) else { continue };
            if parent & LINK_BIT != 0 || self.names.iter().any(|(pp, nn)| *pp == parent && *nn == n.name) {
                continue;
            }
            self.names.push((parent, n.name.clone()));
        }
    }

    /// 확장 레코드($ATTRIBUTE_LIST가 가리키는)의 이름과 $DATA를 합친다.
    pub fn merge_extension(&mut self, p: &ParsedRecord) {
        self.add_names(p);
        if !self.has_data
            && let Some((s, a)) = p.data
        {
            self.size = s;
            self.alloc = a;
            self.has_data = true;
        }
    }

    fn node_flags(&self) -> u16 {
        let mut f = flags::IN_USE;
        if self.is_dir {
            f |= flags::DIR;
        }
        if self.has_data {
            f |= flags::HAS_DATA;
        }
        if self.attributes & 0x2 != 0 {
            f |= flags::HIDDEN;
        }
        if self.attributes & 0x4 != 0 {
            f |= flags::SYSTEM;
        }
        if self.attributes & 0x400 != 0 {
            f |= flags::REPARSE;
        }
        f
    }
}

/// 검색용 접기(case fold). 쿼리와 이름에 같은 함수를 쓴다.
pub fn fold_name(s: &str) -> String {
    s.chars().filter(|&c| c != '\0').flat_map(char::to_lowercase).collect()
}

pub struct VolumeIndex {
    /// 루트 노드의 경로. NTFS 볼륨은 "C:\", 디렉터리 스캔은 그 폴더의 전체 경로.
    root_path: String,
    root: u32,
    /// 레코드 번호 0~15(루트 제외)를 메타 파일로 볼지 (NTFS만)
    ntfs_meta: bool,
    nodes: Vec<Node>,
    links: Vec<Node>,
    names: Vec<u8>,
    fold: Vec<u8>,
    fold_index: Vec<(u32, u32)>,
    /// 주 레코드 → 하드링크 추가 노드 id들
    extra: HashMap<u32, Vec<u32>>,
    free_links: Vec<u32>,
    garbage: usize,
    ready: bool,
}

impl VolumeIndex {
    /// 빈 색인. `root`는 루트 폴더의 레코드 번호(NTFS는 5).
    pub fn new(root_path: impl Into<String>, root: u32, ntfs_meta: bool) -> Self {
        Self {
            root_path: root_path.into(),
            root,
            ntfs_meta,
            nodes: Vec::new(),
            links: Vec::new(),
            names: Vec::new(),
            fold: vec![0],
            fold_index: Vec::new(),
            extra: HashMap::new(),
            free_links: Vec::new(),
            garbage: 0,
            ready: false,
        }
    }

    /// NTFS 볼륨 색인 (루트 레코드 5, "C:\").
    pub fn new_ntfs(volume: &str, expected_records: usize) -> Self {
        let mut v = Self::new(format!("{}\\", volume.trim_end_matches('\\')), 5, true);
        v.nodes.reserve(expected_records);
        v.names.reserve(expected_records * 16);
        v.fold.reserve(expected_records * 17);
        v.fold_index.reserve(expected_records);
        v
    }

    pub fn root(&self) -> u32 {
        self.root
    }
    pub fn root_path(&self) -> &str {
        &self.root_path
    }
    pub fn is_ready(&self) -> bool {
        self.ready
    }

    pub fn node(&self, id: u32) -> Option<&Node> {
        if id & LINK_BIT != 0 { self.links.get((id & !LINK_BIT) as usize) } else { self.nodes.get(id as usize) }
    }

    fn node_mut(&mut self, id: u32) -> Option<&mut Node> {
        if id & LINK_BIT != 0 { self.links.get_mut((id & !LINK_BIT) as usize) } else { self.nodes.get_mut(id as usize) }
    }

    pub fn name(&self, id: u32) -> &str {
        let Some(n) = self.node(id) else { return "" };
        let s = n.name_off as usize;
        self.names.get(s..s + n.name_len as usize).and_then(|b| std::str::from_utf8(b).ok()).unwrap_or("")
    }

    /// 검색용 접힌 이름 (NUL 제외).
    pub fn folded(&self, id: u32) -> &[u8] {
        let Some(n) = self.node(id) else { return &[] };
        let s = n.fold_off as usize;
        let rest = self.fold.get(s..).unwrap_or(&[]);
        let end = memchr::memchr(0, rest).unwrap_or(rest.len());
        &rest[..end]
    }

    pub fn fold_pool(&self) -> &[u8] {
        &self.fold
    }
    pub fn fold_entries(&self) -> &[(u32, u32)] {
        &self.fold_index
    }

    /// `fold_index` 항목이 아직 유효하고 검색에 보여도 되는지.
    pub fn entry_visible(&self, off: u32, id: u32) -> bool {
        self.node(id).is_some_and(|n| n.in_use() && n.fold_off == off && n.flags & flags::META == 0 && id != self.root)
    }

    /// 사용 중인 항목 수 (하드링크 추가 이름 포함, 메타 파일 제외).
    pub fn live_count(&self) -> usize {
        let f = |n: &Node| n.in_use() && n.flags & flags::META == 0;
        self.nodes.iter().filter(|n| f(n)).count() + self.links.iter().filter(|n| f(n)).count()
    }

    pub fn memory_bytes(&self) -> usize {
        (self.nodes.capacity() + self.links.capacity()) * size_of::<Node>()
            + self.names.capacity()
            + self.fold.capacity()
            + self.fold_index.capacity() * 8
            + self.extra.len() * 48
    }

    fn ensure(&mut self, rec: u32) {
        if rec as usize >= self.nodes.len() {
            self.nodes.resize(rec as usize + 1, Node::default());
        }
    }

    fn push_name(&mut self, id: u32, name: &str) -> (u32, u16, u32) {
        let name_off = self.names.len() as u32;
        let bytes = name.as_bytes();
        let len = bytes.len().min(u16::MAX as usize);
        self.names.extend_from_slice(&bytes[..len]);
        let fold_off = self.fold.len() as u32;
        self.fold.extend_from_slice(fold_name(name).as_bytes());
        self.fold.push(0);
        self.fold_index.push((fold_off, id));
        (name_off, len as u16, fold_off)
    }

    fn alloc_link(&mut self) -> u32 {
        if let Some(i) = self.free_links.pop() {
            return i | LINK_BIT;
        }
        self.links.push(Node::default());
        (self.links.len() as u32 - 1) | LINK_BIT
    }

    fn set_name(&mut self, id: u32, parent: u32, name: &str) {
        let same = self.node(id).is_some_and(|n| n.name_len > 0 && n.parent == parent) && self.name(id) == name;
        if same {
            return;
        }
        let old_len = self.node(id).map(|n| n.name_len as usize).unwrap_or(0);
        self.garbage += old_len;
        let (no, nl, fo) = self.push_name(id, name);
        if let Some(n) = self.node_mut(id) {
            n.parent = parent;
            n.name_off = no;
            n.name_len = nl;
            n.fold_off = fo;
        }
    }

    fn drop_links(&mut self, rec: u32) {
        if let Some(ids) = self.extra.remove(&rec) {
            for id in ids {
                if let Some(n) = self.node_mut(id) {
                    let len = n.name_len as usize;
                    *n = Node::default();
                    self.garbage += len;
                }
                self.free_links.push(id & !LINK_BIT);
            }
        }
    }

    /// 노드 하나를 Entry 내용으로 채운다 (크기 누적은 건드리지 않음).
    fn write_entry(&mut self, rec: u32, e: &Entry, keep_dir_totals: bool) {
        self.ensure(rec);
        let Some((p0, n0)) = e.names.first() else { return };
        self.set_name(rec, *p0, n0);
        let meta = self.ntfs_meta && rec < 16 && rec != self.root;
        let node = &mut self.nodes[rec as usize];
        node.flags = e.node_flags() | if meta { flags::META } else { 0 };
        node.mtime = e.mtime;
        if !(e.is_dir && keep_dir_totals) {
            if e.is_dir {
                node.size = 0;
                node.alloc = 0;
            } else {
                node.size = e.size;
                node.alloc = e.alloc;
            }
        }
        self.drop_links(rec);
        let primary = self.nodes[rec as usize];
        for (p, n) in e.names.iter().skip(1) {
            self.add_link(rec, *p, n, &primary);
        }
    }

    fn add_link(&mut self, rec: u32, parent: u32, name: &str, primary: &Node) {
        let id = self.alloc_link();
        let (no, nl, fo) = self.push_name(id, name);
        if let Some(n) = self.node_mut(id) {
            *n = Node { parent, name_off: no, name_len: nl, fold_off: fo, flags: primary.flags | flags::EXTRA_LINK, ..*primary };
        }
        self.extra.entry(rec).or_default().push(id);
    }

    // ── 최초 구축 ──

    /// 기본 레코드 하나를 넣는다 (구축 중, 크기 누적 없음).
    pub fn insert_build(&mut self, rec: u32, e: &Entry) {
        self.ensure(rec);
        let pre = self.nodes[rec as usize];
        if pre.name_len == 0 && pre.flags == 0 && !self.extra.contains_key(&rec) && !e.names.is_empty() {
            self.write_entry(rec, e, false);
            return;
        }
        // 확장 레코드가 먼저 와서 이름이나 $DATA를 넣어 두었다: 덮어쓰지 않고 합친다.
        let meta = self.ntfs_meta && rec < 16 && rec != self.root;
        let had_data = pre.flags & flags::HAS_DATA != 0;
        let n = &mut self.nodes[rec as usize];
        n.flags = e.node_flags() | (pre.flags & flags::HAS_DATA) | if meta { flags::META } else { 0 };
        n.mtime = e.mtime;
        if !had_data {
            n.size = if e.is_dir { 0 } else { e.size };
            n.alloc = if e.is_dir { 0 } else { e.alloc };
        }
        for (p, name) in &e.names {
            if self.nodes[rec as usize].name_len == 0 {
                self.set_name(rec, *p, name);
                continue;
            }
            let dup_primary = self.nodes[rec as usize].parent == *p && self.name(rec) == name;
            let dup_link = self
                .extra
                .get(&rec)
                .is_some_and(|ids| ids.iter().any(|&id| self.node(id).is_some_and(|l| l.parent == *p) && self.name(id) == name));
            if !dup_primary && !dup_link {
                let primary = self.nodes[rec as usize];
                self.add_link(rec, *p, name, &primary);
            }
        }
    }

    /// 확장 레코드를 기본 레코드에 합친다 (구축 중). 기본 레코드가 먼저든 나중이든 상관없다.
    pub fn merge_extension_build(&mut self, base: u32, p: &ParsedRecord) {
        self.ensure(base);
        let primary_name = self.nodes[base as usize].name_len > 0;
        for fname in &p.names {
            let Ok(parent) = u32::try_from(fname.parent) else { continue };
            if !primary_name && self.nodes[base as usize].name_len == 0 {
                self.set_name(base, parent, &fname.name);
            } else if !(self.nodes[base as usize].parent == parent && self.name(base) == fname.name) {
                let primary = self.nodes[base as usize];
                self.add_link(base, parent, &fname.name, &primary);
            }
        }
        if let Some((s, a)) = p.data {
            let n = &mut self.nodes[base as usize];
            if n.flags & flags::HAS_DATA == 0 {
                n.size = s;
                n.alloc = a;
                n.flags |= flags::HAS_DATA;
            }
        }
    }

    /// 구축을 마친다: 하드링크 노드 동기화, 메타 표시 전파, 폴더 누적 크기 계산.
    pub fn finalize(&mut self) {
        self.ensure(self.root);
        let root = self.root as usize;
        self.nodes[root].parent = NO_PARENT;
        self.nodes[root].flags |= flags::IN_USE | flags::DIR;
        self.nodes[root].flags &= !flags::META;
        // 하드링크 추가 이름은 주 레코드의 상태를 따른다.
        let extra: Vec<(u32, Vec<u32>)> = self.extra.iter().map(|(k, v)| (*k, v.clone())).collect();
        for (rec, ids) in extra {
            let p = self.nodes[rec as usize];
            for id in ids {
                if let Some(n) = self.node_mut(id) {
                    n.flags = p.flags | flags::EXTRA_LINK;
                    n.size = p.size;
                    n.alloc = p.alloc;
                    n.mtime = p.mtime;
                }
            }
        }
        self.propagate_meta();
        // 폴더 누적 크기
        for n in self.nodes.iter_mut() {
            if n.is_dir() {
                n.size = 0;
                n.alloc = 0;
            }
        }
        for i in 0..self.nodes.len() {
            let n = self.nodes[i];
            if n.in_use() && !n.is_dir() && n.flags & flags::EXTRA_LINK == 0 {
                self.add_to_ancestors(n.parent, n.size, n.alloc, true);
            }
        }
        self.ready = true;
    }

    /// 메타 노드 아래($Extend\$UsnJrnl 등)도 메타로 표시한다. 0=미정, 1=일반, 2=메타.
    fn propagate_meta(&mut self) {
        let n = self.nodes.len();
        let mut state = vec![0u8; n];
        state[self.root as usize] = 1;
        let mut chain = Vec::new();
        for i in 0..n {
            if state[i] != 0 || !self.nodes[i].in_use() {
                continue;
            }
            chain.clear();
            let mut cur = i;
            let verdict = loop {
                if state[cur] != 0 {
                    break state[cur];
                }
                if self.nodes[cur].flags & flags::META != 0 {
                    break 2;
                }
                chain.push(cur);
                let p = self.nodes[cur].parent as usize;
                if p >= n || chain.len() > MAX_DEPTH || p == cur {
                    break 1;
                }
                cur = p;
            };
            for &c in &chain {
                state[c] = verdict;
            }
            if verdict == 2 {
                state[cur] = 2;
            }
        }
        for (i, s) in state.iter().enumerate() {
            if *s == 2 && i != self.root as usize {
                self.nodes[i].flags |= flags::META;
            }
        }
        for l in self.links.iter_mut() {
            if (l.parent as usize) < n && state[l.parent as usize] == 2 {
                l.flags |= flags::META;
            }
        }
    }

    fn add_to_ancestors(&mut self, mut p: u32, size: u64, alloc: u64, add: bool) {
        if size == 0 && alloc == 0 {
            return;
        }
        for _ in 0..MAX_DEPTH {
            let Some(n) = self.nodes.get_mut(p as usize) else { return };
            // 이미 지워진(또는 아직 없는) 폴더에서 멈춘다. 같은 배치에서 폴더가 먼저 지워져도 두 번 빼지 않는다.
            if !n.in_use() {
                return;
            }
            if add {
                n.size = n.size.wrapping_add(size);
                n.alloc = n.alloc.wrapping_add(alloc);
            } else {
                n.size = n.size.saturating_sub(size);
                n.alloc = n.alloc.saturating_sub(alloc);
            }
            if p == self.root {
                return;
            }
            p = n.parent;
        }
    }

    // ── USN 갱신 (구축 후) ──

    fn is_meta_parent(&self, parent: u32) -> bool {
        self.node(parent).is_some_and(|n| n.flags & flags::META != 0)
    }

    /// 레코드를 지운다. 조상 폴더 누적 크기에서 빼고, 하드링크 이름도 지운다.
    pub fn remove_record(&mut self, rec: u32) {
        let Some(n) = self.nodes.get(rec as usize).copied() else { return };
        if rec == self.root {
            return;
        }
        if n.in_use() {
            self.add_to_ancestors(n.parent, n.size, n.alloc, false);
        }
        self.drop_links(rec);
        self.garbage += n.name_len as usize;
        self.nodes[rec as usize] = Node::default();
        self.maybe_compact();
    }

    /// 레코드를 새 내용으로 바꾼다 (생성·이름 변경·이동·크기 변경). 폴더는 누적 크기를 유지한 채 옮긴다.
    pub fn replace_record(&mut self, rec: u32, e: &Entry) {
        if e.names.is_empty() {
            return;
        }
        if rec == self.root {
            return;
        }
        self.ensure(rec);
        let old = self.nodes[rec as usize];
        if old.in_use() {
            self.add_to_ancestors(old.parent, old.size, old.alloc, false);
        }
        let keep = old.in_use() && old.is_dir() && e.is_dir;
        self.write_entry(rec, e, keep);
        if self.is_meta_parent(e.names[0].0) {
            self.nodes[rec as usize].flags |= flags::META;
            if let Some(ids) = self.extra.get(&rec).cloned() {
                for id in ids {
                    if let Some(n) = self.node_mut(id) {
                        n.flags |= flags::META;
                    }
                }
            }
        }
        let n = self.nodes[rec as usize];
        self.add_to_ancestors(n.parent, n.size, n.alloc, true);
        self.maybe_compact();
    }

    /// 이름 변경으로 쌓인 쓰레기가 많으면 풀을 다시 만든다. (커서는 무효가 된다)
    fn maybe_compact(&mut self) {
        if self.garbage > 1 << 20 && self.garbage > self.names.len() / 2 {
            self.compact();
        }
    }

    pub fn compact(&mut self) {
        let mut names = Vec::with_capacity(self.names.len() - self.garbage.min(self.names.len()));
        let mut fold = vec![0u8];
        let mut fold_index = Vec::with_capacity(self.fold_index.len());
        let ids: Vec<u32> = (0..self.nodes.len() as u32).chain((0..self.links.len() as u32).map(|i| i | LINK_BIT)).collect();
        for id in ids {
            let Some(n) = self.node(id).copied() else { continue };
            if n.name_len == 0 {
                continue;
            }
            let name = self.name(id).to_owned();
            let no = names.len() as u32;
            names.extend_from_slice(name.as_bytes());
            let fo = fold.len() as u32;
            fold.extend_from_slice(fold_name(&name).as_bytes());
            fold.push(0);
            fold_index.push((fo, id));
            if let Some(m) = self.node_mut(id) {
                m.name_off = no;
                m.fold_off = fo;
            }
        }
        self.names = names;
        self.fold = fold;
        self.fold_index = fold_index;
        self.garbage = 0;
    }

    // ── 조회 ──

    /// 경로 재구성. 루트까지 부모를 따라 올라간다.
    pub fn path_of(&self, id: u32) -> String {
        let mut comps: Vec<&str> = Vec::new();
        let mut cur = id;
        for _ in 0..MAX_DEPTH {
            if cur == self.root {
                break;
            }
            let Some(n) = self.node(cur) else { break };
            comps.push(self.name(cur));
            if n.parent == NO_PARENT || n.parent == cur {
                break;
            }
            cur = n.parent;
        }
        if comps.is_empty() {
            return self.root_path.clone();
        }
        let base = self.root_path.trim_end_matches('\\');
        let mut s = String::with_capacity(base.len() + comps.iter().map(|c| c.len() + 1).sum::<usize>());
        s.push_str(base);
        for c in comps.iter().rev() {
            s.push('\\');
            s.push_str(c);
        }
        s
    }

    /// 폴더 바로 아래 항목들 (병렬 스캔).
    pub fn children(&self, dir: u32) -> Vec<u32> {
        let mut v: Vec<u32> = self
            .nodes
            .par_iter()
            .enumerate()
            .filter(|(i, n)| n.parent == dir && n.in_use() && *i as u32 != dir)
            .map(|(i, _)| i as u32)
            .collect();
        v.extend(
            self.links
                .iter()
                .enumerate()
                .filter(|(_, n)| n.parent == dir && n.in_use())
                .map(|(i, _)| i as u32 | LINK_BIT),
        );
        v
    }

    /// 전체 경로를 노드 id로. 루트 경로와 대소문자 구분 없이 비교한다.
    pub fn resolve(&self, path: &str) -> Option<u32> {
        let norm = path.replace('/', "\\");
        let norm = norm.trim_end_matches('\\');
        let root = self.root_path.trim_end_matches('\\');
        if norm.len() < root.len() || !norm.is_char_boundary(root.len()) || fold_name(&norm[..root.len()]) != fold_name(root) {
            return None;
        }
        let rest = &norm[root.len()..];
        if !rest.is_empty() && !rest.starts_with('\\') {
            return None;
        }
        let mut cur = self.root;
        for comp in rest.split('\\').filter(|c| !c.is_empty()) {
            let want = fold_name(comp);
            let want = want.as_bytes();
            let found = self
                .nodes
                .par_iter()
                .enumerate()
                .find_any(|(i, n)| {
                    n.parent == cur && n.in_use() && *i as u32 != cur && self.folded(*i as u32) == want
                })
                .map(|(i, _)| i as u32)
                .or_else(|| {
                    self.links
                        .iter()
                        .enumerate()
                        .find(|(i, n)| n.parent == cur && n.in_use() && self.folded(*i as u32 | LINK_BIT) == want)
                        .map(|(i, _)| i as u32 | LINK_BIT)
                })?;
            cur = found;
        }
        Some(cur)
    }
}

#[cfg(test)]
pub(crate) mod tests {
    use super::*;

    pub fn entry(parent: u32, name: &str, is_dir: bool, size: u64) -> Entry {
        Entry {
            names: vec![(parent, name.into())],
            is_dir,
            size,
            alloc: size.div_ceil(4096) * 4096,
            has_data: !is_dir,
            mtime: 1,
            attributes: 0,
        }
    }

    /// C:\ (5) ─ Users(30) ─ me(31) ─ a.txt(40, 100B), b.bin(41, 5000B)
    ///         ─ $Extend(11) ─ $UsnJrnl(12)  (메타)
    ///         ─ top.log(42, 10B)
    pub fn sample() -> VolumeIndex {
        let mut v = VolumeIndex::new_ntfs("C:", 64);
        v.insert_build(5, &entry(5, ".", true, 0));
        v.insert_build(11, &entry(5, "$Extend", true, 0));
        v.insert_build(12, &entry(11, "$UsnJrnl", false, 1 << 20));
        v.insert_build(30, &entry(5, "Users", true, 0));
        v.insert_build(31, &entry(30, "me", true, 0));
        v.insert_build(40, &entry(31, "a.txt", false, 100));
        v.insert_build(41, &entry(31, "b.bin", false, 5000));
        v.insert_build(42, &entry(5, "top.log", false, 10));
        v.finalize();
        v
    }

    #[test]
    fn node_is_40_bytes() {
        assert_eq!(size_of::<Node>(), 40);
    }

    #[test]
    fn paths_and_subtree_sizes() {
        let v = sample();
        assert_eq!(v.path_of(40), r"C:\Users\me\a.txt");
        assert_eq!(v.path_of(5), r"C:\");
        assert_eq!(v.node(31).unwrap().size, 5100);
        assert_eq!(v.node(31).unwrap().alloc, 4096 + 8192);
        assert_eq!(v.node(30).unwrap().size, 5100);
        assert_eq!(v.node(5).unwrap().size, 5110 + (1 << 20));
        assert!(v.node(12).unwrap().flags & flags::META != 0, "$Extend 아래는 메타");
        assert_eq!(v.live_count(), 6, "루트 포함, 메타 제외");
    }

    #[test]
    fn resolve_and_children() {
        let v = sample();
        assert_eq!(v.resolve(r"c:\users\ME"), Some(31));
        assert_eq!(v.resolve(r"C:\Users\me\"), Some(31));
        assert_eq!(v.resolve("C:/Users/me/a.txt"), Some(40));
        assert_eq!(v.resolve(r"C:\"), Some(5));
        assert_eq!(v.resolve(r"C:\nope"), None);
        assert_eq!(v.resolve(r"D:\Users"), None);
        let mut c = v.children(31);
        c.sort();
        assert_eq!(c, [40, 41]);
    }

    #[test]
    fn hard_links_are_counted_once() {
        let mut v = VolumeIndex::new_ntfs("C:", 64);
        v.insert_build(5, &entry(5, ".", true, 0));
        v.insert_build(30, &entry(5, "A", true, 0));
        v.insert_build(31, &entry(5, "B", true, 0));
        let mut e = entry(30, "x.dll", false, 1000);
        e.names.push((31, "x-link.dll".into()));
        v.insert_build(40, &e);
        v.finalize();
        assert_eq!(v.node(30).unwrap().size, 1000);
        assert_eq!(v.node(31).unwrap().size, 0, "하드링크 추가 이름은 크기를 두 번 세지 않는다");
        assert_eq!(v.node(5).unwrap().size, 1000);
        let c = v.children(31);
        assert_eq!(c.len(), 1);
        assert_eq!(v.path_of(c[0]), r"C:\B\x-link.dll");
        assert_eq!(v.node(c[0]).unwrap().size, 1000);
        v.remove_record(40);
        assert!(v.children(31).is_empty());
        assert_eq!(v.node(5).unwrap().size, 0);
    }

    #[test]
    fn extension_records_merge_in_any_order() {
        use crate::record::{FileName, ParsedRecord};
        let ext = ParsedRecord {
            record_no: 71,
            base_record: 70,
            in_use: true,
            names: vec![FileName { parent: 5, parent_seq: 1, namespace: 1, name: "big.vhdx".into(), size: 0, alloc: 0 }],
            data: Some((5 << 30, 5 << 30)),
            ..Default::default()
        };
        let base = ParsedRecord { record_no: 70, in_use: true, mtime: Some(9), ..Default::default() };
        for ext_first in [true, false] {
            let mut v = VolumeIndex::new_ntfs("C:", 64);
            v.insert_build(5, &entry(5, ".", true, 0));
            if ext_first {
                v.merge_extension_build(70, &ext);
                v.insert_build(70, &Entry::from_parsed(&base));
            } else {
                v.insert_build(70, &Entry::from_parsed(&base));
                v.merge_extension_build(70, &ext);
            }
            v.finalize();
            assert_eq!(v.path_of(70), r"C:\big.vhdx");
            assert_eq!(v.node(70).unwrap().size, 5 << 30);
            assert_eq!(v.node(70).unwrap().mtime, 9);
            assert_eq!(v.node(5).unwrap().size, 5 << 30);
        }
    }

    #[test]
    fn incremental_updates_keep_totals_consistent() {
        let mut v = sample();
        // 크기 변경
        v.replace_record(40, &entry(31, "a.txt", false, 300));
        assert_eq!(v.node(31).unwrap().size, 5300);
        // 생성
        v.replace_record(43, &entry(30, "new.txt", false, 7));
        assert_eq!(v.node(30).unwrap().size, 5307);
        // 폴더 이동: me(5300) 를 루트 바로 아래로
        v.replace_record(31, &entry(5, "me2", true, 0));
        assert_eq!(v.node(31).unwrap().size, 5300, "폴더 누적 크기는 이동해도 유지");
        assert_eq!(v.node(30).unwrap().size, 7);
        assert_eq!(v.path_of(41), r"C:\me2\b.bin");
        // 삭제
        v.remove_record(41);
        assert_eq!(v.node(31).unwrap().size, 300);
        assert_eq!(v.node(5).unwrap().size, 300 + 7 + 10 + (1 << 20));
        // 압축 후에도 이름 유지
        v.compact();
        assert_eq!(v.path_of(40), r"C:\me2\a.txt");
        assert_eq!(v.resolve(r"C:\me2\a.txt"), Some(40));
    }
}
