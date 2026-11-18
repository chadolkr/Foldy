//! 스캐너: 원본 목록을 펼쳐 폴더(부모가 먼저)와 파일 목록을 만든다.
//! 심볼릭 링크·정션은 따라가지 않는다 (순환 방지). MFT 색인 활용은 이후 단계.

use std::ffi::OsString;
use std::path::{Path, PathBuf};
use std::time::SystemTime;

use crate::ffi::{FC_ERR_INVALID_ARG, FC_ERR_IO};

#[derive(Debug, Clone)]
pub struct Top {
    pub src: PathBuf,
    pub name: OsString,
    pub is_dir: bool,
    /// 이 항목 아래 파일 수·바이트 (같은 볼륨 이동을 한 번에 처리할 때 진행률에 씀)
    pub files: u64,
    pub bytes: u64,
}

#[derive(Debug, Clone)]
pub struct DirItem {
    pub src: PathBuf,
    /// 대상 폴더 기준 상대 경로 (최상위 이름 포함)
    pub rel: PathBuf,
    pub top: usize,
}

#[derive(Debug, Clone)]
pub struct FileItem {
    pub src: PathBuf,
    pub rel: PathBuf,
    pub size: u64,
    pub mtime: SystemTime,
    pub top: usize,
}

#[derive(Debug, Default)]
pub struct Scan {
    pub tops: Vec<Top>,
    /// 부모가 자식보다 앞에 온다 (만들 때는 순서대로, 시간 복원·삭제는 거꾸로)
    pub dirs: Vec<DirItem>,
    pub files: Vec<FileItem>,
    /// 읽지 못한 하위 폴더 (작업 전체를 실패시키지 않고 실패 목록으로)
    pub errors: Vec<(PathBuf, String)>,
    pub links_skipped: u64,
}

impl Scan {
    pub fn bytes_total(&self) -> u64 {
        self.files.iter().map(|f| f.size).sum()
    }
}

/// 최상위 원본이 없거나 읽을 수 없으면 FC_ERR_IO (작업 실패), 경로가 잘못되면 FC_ERR_INVALID_ARG.
pub fn scan(sources: &[String], stop: &dyn Fn() -> bool) -> Result<Scan, i32> {
    let mut out = Scan::default();
    for (i, s) in sources.iter().enumerate() {
        let p = std::path::absolute(s).map_err(|_| FC_ERR_INVALID_ARG)?;
        let name = p.file_name().ok_or(FC_ERR_INVALID_ARG)?.to_os_string();
        let m = std::fs::symlink_metadata(&p).map_err(|_| FC_ERR_IO)?;
        let mut top = Top { src: p.clone(), name: name.clone(), is_dir: m.is_dir(), files: 0, bytes: 0 };
        if m.is_symlink() {
            out.links_skipped += 1;
        } else if m.is_dir() {
            out.dirs.push(DirItem { src: p.clone(), rel: PathBuf::from(&name), top: i });
            walk(&p, Path::new(&name), i, &mut out, stop);
        } else {
            out.files.push(FileItem {
                src: p.clone(),
                rel: PathBuf::from(&name),
                size: m.len(),
                mtime: m.modified().unwrap_or(SystemTime::UNIX_EPOCH),
                top: i,
            });
        }
        for f in out.files.iter().rev().take_while(|f| f.top == i) {
            top.files += 1;
            top.bytes += f.size;
        }
        out.tops.push(top);
    }
    Ok(out)
}

fn walk(root: &Path, root_rel: &Path, top: usize, out: &mut Scan, stop: &dyn Fn() -> bool) {
    let mut stack = vec![(root.to_path_buf(), root_rel.to_path_buf())];
    while let Some((dir, rel)) = stack.pop() {
        if stop() {
            return;
        }
        let rd = match std::fs::read_dir(&dir) {
            Ok(rd) => rd,
            Err(e) => {
                out.errors.push((dir, e.to_string()));
                continue;
            }
        };
        let mut subdirs = Vec::new();
        for entry in rd {
            let entry = match entry {
                Ok(e) => e,
                Err(e) => {
                    out.errors.push((dir.clone(), e.to_string()));
                    continue;
                }
            };
            let Ok(ft) = entry.file_type() else { continue };
            let name = entry.file_name();
            if ft.is_symlink() {
                out.links_skipped += 1;
            } else if ft.is_dir() {
                let child = DirItem { src: entry.path(), rel: rel.join(&name), top };
                subdirs.push((child.src.clone(), child.rel.clone()));
                out.dirs.push(child);
            } else {
                // FindNextFile 결과에서 바로 나오므로 추가 I/O가 없다.
                let (size, mtime) = match entry.metadata() {
                    Ok(m) => (m.len(), m.modified().unwrap_or(SystemTime::UNIX_EPOCH)),
                    Err(e) => {
                        out.errors.push((entry.path(), e.to_string()));
                        continue;
                    }
                };
                out.files.push(FileItem { src: entry.path(), rel: rel.join(&name), size, mtime, top });
            }
        }
        // 스택이라 거꾸로 넣어야 이름 순서대로 내려간다
        stack.extend(subdirs.into_iter().rev());
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn scans_tree_parent_first() {
        let root = std::env::temp_dir().join(format!("foldy-scan-{}", std::process::id()));
        let _ = std::fs::remove_dir_all(&root);
        let src = root.join("원본");
        std::fs::create_dir_all(src.join("a").join("b")).unwrap();
        std::fs::create_dir_all(src.join("빈 폴더")).unwrap();
        std::fs::write(src.join("1.txt"), "1").unwrap();
        std::fs::write(src.join("a").join("2.txt"), "22").unwrap();
        std::fs::write(src.join("a").join("b").join("3.txt"), "333").unwrap();
        let single = root.join("single.bin");
        std::fs::write(&single, [0u8; 10]).unwrap();

        let s = scan(&[src.to_string_lossy().into(), single.to_string_lossy().into()], &|| false).unwrap();
        assert_eq!(s.tops.len(), 2);
        assert!(s.tops[0].is_dir);
        assert_eq!((s.tops[0].files, s.tops[0].bytes), (3, 6));
        assert_eq!((s.tops[1].files, s.tops[1].bytes), (1, 10));
        assert_eq!(s.files.len(), 4);
        assert_eq!(s.bytes_total(), 16);
        assert_eq!(s.dirs.len(), 4, "원본, a, a\\b, 빈 폴더");
        for (i, d) in s.dirs.iter().enumerate() {
            if let Some(parent) = d.rel.parent().filter(|p| !p.as_os_str().is_empty()) {
                assert!(s.dirs[..i].iter().any(|x| x.rel == parent), "부모가 먼저: {:?}", d.rel);
            }
        }
        assert!(s.files.iter().any(|f| f.rel == Path::new("원본").join("a").join("b").join("3.txt")));

        assert_eq!(scan(&[root.join("없음").to_string_lossy().into()], &|| false).unwrap_err(), FC_ERR_IO);
        assert_eq!(scan(&["C:\\".into()], &|| false).unwrap_err(), FC_ERR_INVALID_ARG);
        let _ = std::fs::remove_dir_all(root);
    }
}
