//! 정리 규칙 엔진 (설계서 9장 cleanup-rules.json, 7.3 "정리 제안").
//!
//! 규칙은 이름·경로 패턴으로 폴더에 태그(`dir.tag`)를 붙이고, 크기·수정 시각 조건을 만족하면 정리 제안
//! (`candidate`)을 만든다. 시스템 폴더는 규칙 내용과 상관없이 [`is_protected`]가 막는다 — 규칙 파일을
//! 잘못 고쳐도 Windows, Program Files 같은 곳은 절대 제안되지 않는다.
//!
//! C# 폴백 스캐너(src/Foldy.Analysis/CleanupRules.cs)가 같은 규칙 파일과 같은 의미로 동작한다.
//! 한쪽을 바꾸면 다른 쪽도 함께 바꾼다.

use serde::Deserialize;

/// 기본 규칙 (assets/cleanup-rules.json). 실행 파일 옆이나 ProgramData에 파일이 없으면 이것을 쓴다.
pub const DEFAULT_RULES_JSON: &str = include_str!("../../../../assets/cleanup-rules.json");

/// FILETIME 하루 (100ns 단위).
pub const FILETIME_DAY: i64 = 864_000_000_000;

#[derive(Debug, Clone, Copy, PartialEq, Eq, Deserialize, Default)]
#[serde(rename_all = "lowercase")]
pub enum RuleKind {
    #[default]
    Dir,
    File,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Rule {
    pub id: String,
    #[serde(default)]
    pub tag: Option<String>,
    #[serde(default)]
    pub title: Option<String>,
    #[serde(default)]
    pub kind: RuleKind,
    /// 폴더(또는 파일) 이름 패턴 중 하나와 맞아야 한다.
    #[serde(default)]
    pub names: Vec<String>,
    /// 볼륨 루트 기준 경로 패턴 (`\Users\*\AppData\Local\Temp`) 중 하나와 맞아야 한다.
    #[serde(default)]
    pub paths: Vec<String>,
    /// 같은 부모 폴더에 이 패턴과 맞는 항목이 하나는 있어야 한다 (폴더 규칙만).
    #[serde(default)]
    pub parent_has: Vec<String>,
    /// 파일 규칙: 확장자 (점 없이, 소문자 비교).
    #[serde(default)]
    pub extensions: Vec<String>,
    /// 하위에서 가장 최근 수정이 이보다 오래돼야 제안한다.
    #[serde(default)]
    pub older_than_days: Option<u32>,
    /// 차지 공간(alloc)이 이 이상이어야 제안한다.
    #[serde(default)]
    pub min_size: Option<u64>,
    #[serde(default)]
    pub reason: Option<String>,
    /// false면 태그만 붙이고 제안하지 않는다 (예: .git).
    #[serde(default = "yes")]
    pub suggest: bool,
}

fn yes() -> bool {
    true
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct RuleSet {
    #[serde(default)]
    pub schema_version: u32,
    pub rules: Vec<Rule>,
}

/// 평가할 폴더 하나의 정보.
pub struct DirFacts<'a> {
    pub name: &'a str,
    /// 볼륨 루트 기준 경로 (`\Users\me\proj\node_modules`). 루트는 빈 문자열.
    pub rel_path: &'a str,
    /// 전체 경로 (`C:\Users\...`). 보호 폴더 판정용.
    pub full_path: &'a str,
    pub alloc: u64,
    pub newest_mtime: i64,
    pub system_or_reparse: bool,
}

pub struct FileFacts<'a> {
    pub name: &'a str,
    pub ext: &'a str,
    pub rel_path: &'a str,
    pub full_path: &'a str,
    pub alloc: u64,
    pub mtime: i64,
    pub system_or_reparse: bool,
}

/// 규칙 판정 결과
#[derive(Debug, Clone)]
pub struct Verdict<'r> {
    pub rule: &'r Rule,
    /// 조건까지 만족해 정리 제안으로 낼지
    pub suggest: bool,
}

impl Rule {
    /// candidate.reason에 넣을 설명 (reason이 없으면 title)
    pub fn reason_text(&self) -> String {
        self.reason.clone().or_else(|| self.title.clone()).unwrap_or_default()
    }
}

impl RuleSet {
    pub fn parse(json: &str) -> Result<Self, String> {
        let mut rs: RuleSet = serde_json::from_str(json).map_err(|e| format!("cleanup-rules.json: {e}"))?;
        if rs.schema_version > 1 {
            return Err(format!("cleanup-rules.json: 모르는 schemaVersion {}", rs.schema_version));
        }
        for r in &mut rs.rules {
            r.names = r.names.iter().map(|s| s.to_lowercase()).collect();
            r.paths = r.paths.iter().map(|s| normalize_rel(&s.to_lowercase())).collect();
            r.parent_has = r.parent_has.iter().map(|s| s.to_lowercase()).collect();
            r.extensions = r.extensions.iter().map(|s| s.trim_start_matches('.').to_lowercase()).collect();
        }
        Ok(rs)
    }

    pub fn defaults() -> Self {
        Self::parse(DEFAULT_RULES_JSON).expect("기본 cleanup-rules.json은 항상 올바르다")
    }

    /// 실행 파일 옆 → %ProgramData%\Foldy\cleanup-rules.json 순으로 찾고, 없거나 깨졌으면 기본값.
    pub fn load_default_locations() -> Self {
        let mut cands = Vec::new();
        if let Ok(exe) = std::env::current_exe()
            && let Some(d) = exe.parent()
        {
            cands.push(d.join("cleanup-rules.json"));
        }
        if let Some(pd) = std::env::var_os("ProgramData") {
            cands.push(std::path::PathBuf::from(pd).join("Foldy").join("cleanup-rules.json"));
        }
        for c in cands {
            if let Ok(s) = std::fs::read_to_string(&c) {
                match Self::parse(&s) {
                    Ok(r) => return r,
                    Err(e) => eprintln!("{}: {e} (기본 규칙 사용)", c.display()),
                }
            }
        }
        Self::defaults()
    }

    pub fn has_file_rules(&self) -> bool {
        self.rules.iter().any(|r| r.kind == RuleKind::File)
    }

    /// 폴더에 맞는 첫 규칙. `siblings`는 부모 폴더의 항목 이름들(소문자)을 돌려준다.
    /// parentHas가 있는 규칙이 이름·경로까지 맞았을 때만, 그리고 한 번만 부른다.
    pub fn match_dir<'r>(&'r self, d: &DirFacts<'_>, now: i64, siblings: &mut dyn FnMut() -> Vec<String>) -> Option<Verdict<'r>> {
        let name = d.name.to_lowercase();
        let rel = d.rel_path.to_lowercase();
        let mut sib_cache: Option<Vec<String>> = None;
        for r in self.rules.iter().filter(|r| r.kind == RuleKind::Dir) {
            if r.names.is_empty() && r.paths.is_empty() {
                continue;
            }
            if !r.names.is_empty() && !r.names.iter().any(|p| glob(p, &name)) {
                continue;
            }
            if !r.paths.is_empty() && !r.paths.iter().any(|p| glob(p, &rel)) {
                continue;
            }
            if !r.parent_has.is_empty() {
                let sib = sib_cache.get_or_insert_with(&mut *siblings);
                if !r.parent_has.iter().any(|p| sib.iter().any(|s| glob(p, s))) {
                    continue;
                }
            }
            let suggest = r.suggest
                && !d.system_or_reparse
                && !is_protected(d.full_path)
                && conditions(r, d.alloc, d.newest_mtime, now);
            return Some(Verdict { rule: r, suggest });
        }
        None
    }

    /// 파일 규칙 판정 (제안할 때만 Some).
    pub fn match_file<'r>(&'r self, f: &FileFacts<'_>, now: i64) -> Option<&'r Rule> {
        let mut name: Option<String> = None;
        let mut rel: Option<String> = None;
        for r in self.rules.iter().filter(|r| r.kind == RuleKind::File && r.suggest) {
            if r.names.is_empty() && r.paths.is_empty() && r.extensions.is_empty() {
                continue;
            }
            if !r.extensions.is_empty() && !r.extensions.iter().any(|e| e == f.ext) {
                continue;
            }
            if !conditions(r, f.alloc, f.mtime, now) {
                continue;
            }
            if !r.names.is_empty() {
                let n = name.get_or_insert_with(|| f.name.to_lowercase());
                if !r.names.iter().any(|p| glob(p, n)) {
                    continue;
                }
            }
            if !r.paths.is_empty() {
                let p0 = rel.get_or_insert_with(|| f.rel_path.to_lowercase());
                if !r.paths.iter().any(|p| glob(p, p0)) {
                    continue;
                }
            }
            if f.system_or_reparse || is_protected_file(f.full_path) {
                return None;
            }
            return Some(r);
        }
        None
    }
}

/// 파일 보호: 보호 폴더 아래이거나 보호 폴더 바로 안의 파일 (볼륨 루트의 pagefile.sys, 프로필 루트의 NTUSER.DAT 등).
/// 다운로드·문서 같은 사용자 콘텐츠 폴더는 폴더 자체만 보호하고 그 안 파일은 제안할 수 있다. C# IsProtectedFile과 같다.
pub fn is_protected_file(full: &str) -> bool {
    if is_protected(full) {
        return true;
    }
    let p = full.replace('/', "\\");
    let p = p.trim_end_matches('\\');
    let Some(i) = p.rfind('\\') else { return true };
    let parent = &p[..i];
    let parent_name = parent.rsplit('\\').next().unwrap_or("").to_lowercase();
    let content = matches!(parent_name.as_str(), "desktop" | "documents" | "downloads" | "pictures" | "music" | "videos" | "onedrive");
    is_protected(parent) && !content
}

fn conditions(r: &Rule, alloc: u64, newest: i64, now: i64) -> bool {
    if let Some(min) = r.min_size
        && alloc < min
    {
        return false;
    }
    if let Some(days) = r.older_than_days {
        // 수정 시각을 모르면(0) 오래됐다고 보지 않는다.
        if newest <= 0 || now - newest < days as i64 * FILETIME_DAY {
            return false;
        }
    }
    true
}

fn normalize_rel(p: &str) -> String {
    let p = p.replace('/', "\\");
    if p.starts_with('\\') { p } else { format!("\\{p}") }
}

/// 보호 폴더(시스템): 이 아래는 어떤 규칙도 정리를 제안하지 않는다. `full`은 `C:\...` 형태 전체 경로.
pub fn is_protected(full: &str) -> bool {
    let p = full.replace('/', "\\").to_lowercase();
    // 드라이브 문자("c:")나 \\?\ 접두사를 떼어 볼륨 기준 경로로 만든다.
    let p = p.strip_prefix(r"\\?\").unwrap_or(&p);
    let rel = match p.find(':') {
        Some(i) if i <= 2 => &p[i + 1..],
        _ => p,
    };
    let segs: Vec<&str> = rel.split('\\').filter(|s| !s.is_empty()).collect();
    let Some(first) = segs.first() else { return true }; // 루트 자체
    const TOP: &[&str] = &[
        "windows",
        "program files",
        "program files (x86)",
        "programdata",
        "$recycle.bin",
        "system volume information",
        "recovery",
        "boot",
        "efi",
        "perflogs",
        "$winreagent",
        "$windows.~bt",
        "$windows.~ws",
        "windows.old",
        "config.msi",
        "msocache",
        "intel",
        "amd",
        "nvidia",
        "drivers",
    ];
    if TOP.contains(first) {
        return true;
    }
    if *first == "users" {
        // \Users, \Users\<이름> (프로필 자체), Default/Public/All Users 아래, AppData\Local\Microsoft\Windows 같은 OS 영역
        if segs.len() <= 2 {
            return true;
        }
        if matches!(segs[1], "default" | "default user" | "all users" | "public") {
            return true;
        }
        let tail = &segs[2..];
        if tail.len() <= 1 && matches!(tail.first(), Some(&"appdata") | Some(&"desktop") | Some(&"documents") | Some(&"downloads") | Some(&"pictures") | Some(&"music") | Some(&"videos") | Some(&"onedrive")) {
            return true;
        }
        if tail.len() <= 2 && tail.first() == Some(&"appdata") {
            return true;
        }
        if tail.len() >= 3 && tail[0] == "appdata" && tail[2] == "microsoft" && tail.get(3).is_none_or(|s| matches!(*s, "windows" | "windowsapps" | "credentials" | "protect" | "crypto" | "vault" | "systemcertificates")) {
            return true;
        }
    }
    false
}

/// 대소문자 구분 없는 글롭 (호출자가 둘 다 소문자로 준다). `*`는 `\`를 넘지 않고 `**`는 넘는다.
pub fn glob(pat: &str, s: &str) -> bool {
    glob_bytes(pat.as_bytes(), s.as_bytes())
}

/// 재귀 매칭. 규칙 패턴은 짧고 별이 몇 개 없으므로 단순한 역추적으로 충분하다.
fn glob_bytes(p: &[u8], s: &[u8]) -> bool {
    match p.first() {
        None => s.is_empty(),
        Some(b'*') if p.get(1) == Some(&b'*') => {
            let rest = &p[2..];
            (0..=s.len()).any(|i| glob_bytes(rest, &s[i..]))
        }
        Some(b'*') => {
            let rest = &p[1..];
            for i in 0..=s.len() {
                if glob_bytes(rest, &s[i..]) {
                    return true;
                }
                if i < s.len() && s[i] == b'\\' {
                    break;
                }
            }
            false
        }
        Some(b'?') => !s.is_empty() && s[0] != b'\\' && glob_bytes(&p[1..], &s[1..]),
        Some(c) => !s.is_empty() && s[0] == *c && glob_bytes(&p[1..], &s[1..]),
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    const NOW: i64 = 134_000_000_000_000_000;

    fn dir<'a>(name: &'a str, rel: &'a str, full: &'a str, alloc: u64, age_days: i64) -> DirFacts<'a> {
        DirFacts { name, rel_path: rel, full_path: full, alloc, newest_mtime: NOW - age_days * FILETIME_DAY, system_or_reparse: false }
    }

    #[test]
    fn glob_semantics() {
        assert!(glob("node_modules", "node_modules"));
        assert!(glob("*.csproj", "app.csproj"));
        assert!(!glob("*.csproj", "app.csproj.user"));
        assert!(glob(r"\users\*\appdata\local\temp", r"\users\me\appdata\local\temp"));
        assert!(!glob(r"\users\*\appdata\local\temp", r"\users\me\x\appdata\local\temp"), "*는 \\를 넘지 않는다");
        assert!(glob(r"\users\*\downloads\**", r"\users\me\downloads\a\b\setup.exe"));
        assert!(glob("a?c", "abc"));
        assert!(!glob("a?c", r"a\c"));
        assert!(glob("**", ""));
    }

    #[test]
    fn default_rules_parse_and_tag() {
        let rs = RuleSet::defaults();
        assert!(rs.rules.len() >= 10);
        let sib = vec!["package.json".to_string(), "src".to_string()];
        let d = dir("node_modules", r"\Users\me\proj\node_modules", r"C:\Users\me\proj\node_modules", 50 << 20, 60);
        let v = rs.match_dir(&d, NOW, &mut || sib.clone()).unwrap();
        assert_eq!(v.rule.tag.as_deref(), Some("node_modules"));
        assert!(v.suggest);
        // 최근에 바뀌었으면 태그만
        let d = dir("node_modules", r"\Users\me\proj\node_modules", r"C:\Users\me\proj\node_modules", 50 << 20, 2);
        assert!(!rs.match_dir(&d, NOW, &mut || sib.clone()).unwrap().suggest);
        // package.json이 없으면 node_modules 규칙이 아니다
        let none: Vec<String> = vec![];
        let d = dir("node_modules", r"\x\node_modules", r"D:\x\node_modules", 50 << 20, 60);
        assert!(rs.match_dir(&d, NOW, &mut || none.clone()).is_none());
        // bin은 프로젝트 파일 옆에서만
        let cs = vec!["app.csproj".to_string()];
        let d = dir("bin", r"\src\app\bin", r"D:\src\app\bin", 20 << 20, 30);
        assert_eq!(rs.match_dir(&d, NOW, &mut || cs.clone()).unwrap().rule.id, "dotnet-bin-obj");
        // .git은 태그만, 절대 제안 안 함
        let d = dir(".git", r"\src\app\.git", r"D:\src\app\.git", 5 << 30, 400);
        let v = rs.match_dir(&d, NOW, &mut || none.clone()).unwrap();
        assert_eq!(v.rule.tag.as_deref(), Some("git"));
        assert!(!v.suggest);
        // %TEMP%
        let d = dir("Temp", r"\Users\me\AppData\Local\Temp", r"C:\Users\me\AppData\Local\Temp", 1 << 30, 10);
        let v = rs.match_dir(&d, NOW, &mut || none.clone()).unwrap();
        assert_eq!(v.rule.id, "user-temp");
        assert!(v.suggest);
    }

    #[test]
    fn system_folders_are_never_suggested() {
        let rs = RuleSet::defaults();
        let none: Vec<String> = vec![];
        for (name, rel) in [
            ("Temp", r"\Windows\Temp"),
            ("temp", r"\ProgramData\temp"),
            ("tmp", r"\Program Files\App\tmp"),
            ("node_modules", r"\Program Files (x86)\x\node_modules"),
        ] {
            let full = format!("C:{rel}");
            let d = dir(name, rel, &full, 10 << 30, 365);
            let pkg = vec!["package.json".to_string()];
            let sib: &Vec<String> = if name == "node_modules" { &pkg } else { &none };
            if let Some(v) = rs.match_dir(&d, NOW, &mut || sib.clone()) {
                assert!(!v.suggest, "{rel}는 제안하면 안 된다");
            }
        }
        // 악의적·실수로 고친 규칙 파일이라도 보호 폴더는 막힌다
        let evil = RuleSet::parse(r#"{"rules":[{"id":"all","names":["*"]},{"id":"f","kind":"file","extensions":["dll"]}]}"#).unwrap();
        for p in [r"C:\Windows", r"C:\Windows\System32", r"C:\Users", r"C:\Users\me", r"C:\Users\me\AppData", r"C:\Users\me\AppData\Local", r"C:\"] {
            assert!(is_protected(p), "{p}");
        }
        let d = dir("System32", r"\Windows\System32", r"C:\Windows\System32", 10 << 30, 365);
        assert!(!evil.match_dir(&d, NOW, &mut || none.clone()).unwrap().suggest);
        let f = FileFacts {
            name: "kernel32.dll",
            ext: "dll",
            rel_path: r"\Windows\System32\kernel32.dll",
            full_path: r"C:\Windows\System32\kernel32.dll",
            alloc: 1 << 20,
            mtime: NOW - 400 * FILETIME_DAY,
            system_or_reparse: false,
        };
        assert!(evil.match_file(&f, NOW).is_none());
        assert!(!is_protected(r"C:\Users\me\proj\node_modules"));
        assert!(!is_protected(r"D:\work\bin"));
        // 시스템 특성이 있으면 막는다
        let mut d = dir("node_modules", r"\a\node_modules", r"D:\a\node_modules", 50 << 20, 60);
        d.system_or_reparse = true;
        let pkg = vec!["package.json".to_string()];
        assert!(!rs.match_dir(&d, NOW, &mut || pkg.clone()).unwrap().suggest);
    }

    #[test]
    fn file_rules() {
        let rs = RuleSet::defaults();
        let mk = |rel: &'static str, ext: &'static str, alloc: u64, age: i64| FileFacts {
            name: rel.rsplit('\\').next().unwrap(),
            ext,
            rel_path: rel,
            full_path: rel,
            alloc,
            mtime: NOW - age * FILETIME_DAY,
            system_or_reparse: false,
        };
        // full_path에 드라이브가 없어도 is_protected는 볼륨 기준으로 본다
        assert_eq!(rs.match_file(&mk(r"\Users\me\Downloads\setup.exe", "exe", 100 << 20, 120), NOW).unwrap().id, "old-installers");
        assert!(rs.match_file(&mk(r"\Users\me\Downloads\setup.exe", "exe", 100 << 20, 10), NOW).is_none());
        assert!(rs.match_file(&mk(r"\Users\me\Documents\setup.exe", "exe", 100 << 20, 120), NOW).is_none());
        assert_eq!(rs.match_file(&mk(r"\Users\me\dumps\crash.dmp", "dmp", 50 << 20, 30), NOW).unwrap().id, "crash-dumps");
        assert!(rs.match_file(&mk(r"\Users\me\crash.dmp", "dmp", 50 << 20, 30), NOW).is_none(), "프로필 루트 바로 안의 파일은 보호");
        assert!(rs.match_file(&mk(r"\big.dmp", "dmp", 50 << 20, 30), NOW).is_none(), "볼륨 루트 바로 안의 파일은 보호");
        assert!(rs.match_file(&mk(r"\Windows\Minidump\x.dmp", "dmp", 50 << 20, 30), NOW).is_none());
    }

    #[test]
    fn broken_json_is_an_error() {
        assert!(RuleSet::parse("{").is_err());
        assert!(RuleSet::parse(r#"{"rules":[{"id":"x","kind":"weird"}]}"#).is_err());
    }
}
