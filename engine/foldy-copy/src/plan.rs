//! 계획기: 충돌 확인과 이름 짓기, 대형/소형 줄 나누기. 순수 함수라 파일 시스템 없이 시험한다.

use std::collections::HashSet;
use std::path::{Path, PathBuf};

use crate::spec::ConflictPolicy;

/// 같은 이름 비교용 키 (NTFS는 대소문자를 구분하지 않음).
pub fn path_key(p: &Path) -> String {
    p.to_string_lossy().to_lowercase()
}

/// `a`가 `b`와 같거나 `b` 아래에 있는지 (대소문자 무시).
pub fn path_is_within(a: &Path, b: &Path) -> bool {
    let a = path_key(a);
    let b = path_key(b);
    let b = b.trim_end_matches('\\');
    a == b || a.starts_with(&format!("{b}\\"))
}

/// "이름.ext" → n번째 후보 "이름 (n).ext". 이미 " (k)"가 붙어 있으면 떼고 다시 붙인다.
/// 점으로 시작하는 이름(.gitignore)은 확장자가 없는 것으로 본다.
pub fn rename_candidate(name: &str, n: u32) -> String {
    let (stem, ext) = match name.rfind('.') {
        Some(i) if i > 0 => (&name[..i], &name[i..]),
        _ => (name, ""),
    };
    let stem = strip_counter(stem);
    format!("{stem} ({n}){ext}")
}

fn strip_counter(stem: &str) -> &str {
    if let Some(open) = stem.rfind(" (")
        && stem.ends_with(')')
    {
        let digits = &stem[open + 2..stem.len() - 1];
        if !digits.is_empty() && digits.bytes().all(|b| b.is_ascii_digit()) {
            return &stem[..open];
        }
    }
    stem
}

/// `dst`와 겹치지 않는 첫 이름 "(2)", "(3)" ... 을 찾는다. `taken(p)`가 false인 첫 후보.
pub fn unique_path(dst: &Path, taken: &dyn Fn(&Path) -> bool) -> PathBuf {
    let name = dst.file_name().map(|n| n.to_string_lossy().into_owned()).unwrap_or_default();
    let parent = dst.parent().unwrap_or(Path::new(""));
    // 이미 "(k)"가 붙은 이름이면 k+1부터
    let start = {
        let (stem, _) = match name.rfind('.') {
            Some(i) if i > 0 => (&name[..i], &name[i..]),
            _ => (name.as_str(), ""),
        };
        if strip_counter(stem).len() != stem.len() {
            stem[stem.rfind(" (").map(|i| i + 2).unwrap_or(0)..stem.len() - 1].parse::<u32>().unwrap_or(1) + 1
        } else {
            2
        }
    };
    let mut n = start.max(2);
    loop {
        let cand = parent.join(rename_candidate(&name, n));
        if !taken(&cand) {
            return cand;
        }
        n += 1;
    }
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum Decision {
    /// 이 대상으로 진행. overwrite면 기존 파일을 덮어쓴다.
    Go { dst: PathBuf, overwrite: bool },
    Skip,
    /// 사용자에게 묻는다 (문제 목록 + NeedsAttention)
    Ask,
    Fail(String),
}

/// 대상 하나에 대한 충돌 판단. `existing(p)`: 없으면 None, 있으면 Some(폴더인지).
/// `claimed`는 이 작업에서 이미 쓰기로 한 대상 (서로 다른 원본이 같은 이름으로 모이는 경우).
pub fn decide(
    policy: ConflictPolicy,
    dst: &Path,
    claimed: &mut HashSet<String>,
    existing: &dyn Fn(&Path) -> Option<bool>,
) -> Decision {
    let key = path_key(dst);
    let in_plan = claimed.contains(&key);
    let on_disk = existing(dst);
    if !in_plan && on_disk.is_none() {
        claimed.insert(key);
        return Decision::Go { dst: dst.to_path_buf(), overwrite: false };
    }
    match policy {
        ConflictPolicy::Skip => Decision::Skip,
        ConflictPolicy::Ask => Decision::Ask,
        ConflictPolicy::Overwrite if on_disk == Some(true) => {
            Decision::Fail("같은 이름의 폴더가 있어 파일로 덮어쓸 수 없습니다".into())
        }
        // 이 작업 안의 두 원본이 같은 대상으로 모이면 덮어쓰지 않고 이름을 바꾼다 (병렬 경쟁 방지).
        ConflictPolicy::Overwrite if !in_plan => {
            claimed.insert(key);
            Decision::Go { dst: dst.to_path_buf(), overwrite: true }
        }
        ConflictPolicy::Overwrite | ConflictPolicy::Rename => {
            let taken = |p: &Path| claimed.contains(&path_key(p)) || existing(p).is_some();
            let new_dst = unique_path(dst, &taken);
            claimed.insert(path_key(&new_dst));
            Decision::Go { dst: new_dst, overwrite: false }
        }
    }
}

/// 크기 기준으로 대형 줄 여부.
pub fn is_large(size: u64, threshold: u64) -> bool {
    size >= threshold
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn rename_naming() {
        assert_eq!(rename_candidate("보고서.docx", 2), "보고서 (2).docx");
        assert_eq!(rename_candidate("폴더", 3), "폴더 (3)");
        assert_eq!(rename_candidate(".gitignore", 2), ".gitignore (2)");
        assert_eq!(rename_candidate("a.tar.gz", 2), "a.tar (2).gz");
        assert_eq!(rename_candidate("a (2).txt", 3), "a (3).txt");
        assert_eq!(rename_candidate("a (x).txt", 2), "a (x) (2).txt");
        assert_eq!(rename_candidate("a ().txt", 2), "a () (2).txt");
    }

    #[test]
    fn unique_path_skips_taken_names() {
        let taken: HashSet<String> =
            ["d:\\o\\a.txt", "d:\\o\\a (2).txt", "d:\\o\\a (3).txt"].iter().map(|s| s.to_string()).collect();
        let f = |p: &Path| taken.contains(&path_key(p));
        assert_eq!(unique_path(Path::new("D:\\o\\a.txt"), &f), PathBuf::from("D:\\o\\a (4).txt"));
        assert_eq!(unique_path(Path::new("D:\\o\\a (2).txt"), &f), PathBuf::from("D:\\o\\a (4).txt"));
        assert_eq!(unique_path(Path::new("D:\\o\\b"), &f), PathBuf::from("D:\\o\\b (2)"));
    }

    #[test]
    fn decide_policies() {
        let exists_file = |p: &Path| (path_key(p) == "d:\\o\\x.txt").then_some(false);
        let exists_dir = |p: &Path| (path_key(p) == "d:\\o\\x.txt").then_some(true);
        let dst = Path::new("D:\\o\\x.txt");
        let free = Path::new("D:\\o\\y.txt");

        let mut c = HashSet::new();
        assert_eq!(
            decide(ConflictPolicy::Ask, free, &mut c, &exists_file),
            Decision::Go { dst: free.into(), overwrite: false }
        );
        assert_eq!(decide(ConflictPolicy::Ask, dst, &mut c, &exists_file), Decision::Ask);
        assert_eq!(decide(ConflictPolicy::Skip, dst, &mut c, &exists_file), Decision::Skip);
        assert_eq!(
            decide(ConflictPolicy::Overwrite, dst, &mut c, &exists_file),
            Decision::Go { dst: dst.into(), overwrite: true }
        );
        assert!(matches!(decide(ConflictPolicy::Overwrite, dst, &mut HashSet::new(), &exists_dir), Decision::Fail(_)));

        let mut c = HashSet::new();
        assert_eq!(
            decide(ConflictPolicy::Rename, dst, &mut c, &exists_file),
            Decision::Go { dst: "D:\\o\\x (2).txt".into(), overwrite: false }
        );
        assert_eq!(
            decide(ConflictPolicy::Rename, dst, &mut c, &exists_file),
            Decision::Go { dst: "D:\\o\\x (3).txt".into(), overwrite: false },
            "이미 계획에 잡은 이름도 피한다"
        );
    }

    #[test]
    fn duplicate_targets_inside_one_job() {
        let none = |_: &Path| None;
        let mut c = HashSet::new();
        let dst = Path::new("D:\\o\\same.txt");
        assert!(matches!(decide(ConflictPolicy::Overwrite, dst, &mut c, &none), Decision::Go { overwrite: false, .. }));
        assert_eq!(
            decide(ConflictPolicy::Overwrite, dst, &mut c, &none),
            Decision::Go { dst: "D:\\o\\same (2).txt".into(), overwrite: false }
        );
        assert_eq!(decide(ConflictPolicy::Ask, Path::new("d:\\O\\SAME.txt"), &mut c, &none), Decision::Ask);
    }

    #[test]
    fn within_and_large() {
        assert!(path_is_within(Path::new("C:\\A\\b\\c"), Path::new("c:\\a")));
        assert!(path_is_within(Path::new("C:\\A"), Path::new("c:\\a\\")));
        assert!(!path_is_within(Path::new("C:\\ab"), Path::new("c:\\a")));
        assert!(is_large(256 << 20, 256 << 20));
        assert!(!is_large((256 << 20) - 1, 256 << 20));
    }
}
