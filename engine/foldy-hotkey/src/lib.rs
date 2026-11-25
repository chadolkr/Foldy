//! Foldy 단축키 도우미의 순수 부분 (설정 읽기, Foldy에 보내는 요청 만들기). Windows가 아니어도 시험할 수 있다.
//!
//! 설정 파일 `%LOCALAPPDATA%\Foldy\quick-search-key.txt`는 Foldy가 쓴다 (`QuickSearchKeyAgent.cs`).
//! 한 줄: `off`(쓰지 않음) 또는 `<수식 키 비트> <가상 키 코드>` (RegisterHotKey 값 그대로, 예: Ctrl+Space = `2 32`).
//! 파일이 없으면(Foldy를 아직 한 번도 켜지 않음) 기본 Ctrl+Space.
//!
//! 1.13.0부터 느린 드라이브(네트워크·클라우드·USB) 목록을 주기마다 다시 모으는 일도 한다: Foldy가 꺼져 있으면
//! `Foldy.exe --gather`를 창 없이 띄우고, Foldy는 바뀐 폴더만 읽은 뒤 스스로 끝난다 (사용자 2026-11-25 "단축키 도우미가 하도록 하자").
//! 주기는 Foldy가 `%LOCALAPPDATA%\Foldy\gather-every.txt`에 적는다: 몇 시간마다(`1`·`6`·`24`) 또는 `off`.

/// RegisterHotKey 수식 키 비트 (MOD_ALT·MOD_CONTROL·MOD_SHIFT·MOD_WIN).
pub const MOD_ALT: u32 = 0x1;
pub const MOD_CONTROL: u32 = 0x2;
pub const MOD_SHIFT: u32 = 0x4;
pub const MOD_WIN: u32 = 0x8;
const MOD_ALL: u32 = MOD_ALT | MOD_CONTROL | MOD_SHIFT | MOD_WIN;

/// VK_SPACE
pub const VK_SPACE: u32 = 0x20;

/// Foldy에 넘기는 인자: 주 창은 그대로 두고 찾기 창만 연다.
pub const QUICK_SEARCH_ARG: &str = "--quick-search";

/// 도우미 창(메시지 전용) 클래스 이름. Foldy가 이 이름으로 도우미를 찾아 끄거나 켜졌는지 본다.
pub const WINDOW_CLASS: &str = "FoldyQuickSearchKey";

/// 설정 파일 이름 (`%LOCALAPPDATA%\Foldy\` 아래).
pub const CONFIG_FILE: &str = "quick-search-key.txt";

/// 주기 모으기 설정 파일 이름 (`%LOCALAPPDATA%\Foldy\` 아래).
pub const GATHER_FILE: &str = "gather-every.txt";

/// Foldy에 넘기는 인자: 창 없이 느린 드라이브 목록만 다시 모으고 끝난다.
pub const GATHER_ARG: &str = "--gather";

/// 단축키를 쥐고 있을 때 도우미 창의 이름. 모으기만 하는 도우미(단축키 꺼짐·겹침)는 이름이 없다:
/// Foldy는 이 이름으로 "도우미가 키를 받는 중"인지 본다.
pub const KEY_WINDOW_NAME: &str = "key";

/// 로그인한 뒤 처음 모으기까지 기다리는 시간 (로그인 직후 컴퓨터가 바쁠 때를 피한다).
pub const FIRST_GATHER_AFTER_SECS: u64 = 10 * 60;

/// 주기 모으기 설정을 읽는다: 몇 시간마다 (1~168). 없거나 `off`거나 알아볼 수 없으면 None (모으지 않음).
pub fn parse_gather(text: &str) -> Option<u32> {
    let line = text.lines().map(str::trim).find(|l| !l.is_empty())?;
    line.parse::<u32>().ok().filter(|h| (1..=168).contains(h))
}

/// 지금 Foldy를 띄워 모을 때인가 (초 단위 벽시계). 처음은 도우미가 뜬 뒤 10분, 그다음은 지난번 뒤 주기만큼.
/// 시계가 거꾸로 갔으면(시각을 고침) 바로 한 번 한다.
pub fn gather_due(now: u64, started: u64, last: Option<u64>, hours: u32) -> bool {
    match last {
        None => now >= started.saturating_add(FIRST_GATHER_AFTER_SECS) || now < started,
        Some(l) => now < l || now - l >= u64::from(hours) * 3600,
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Config {
    /// 사용자가 껐다.
    Off,
    /// Foldy가 확인하고 적어 둔 키.
    Key { mods: u32, vk: u32 },
    /// 아직 설정이 없다: 기본 Ctrl+Space (중국어·일본어 입력기가 있으면 쓰지 않는다).
    Default,
}

/// 설정 파일 내용을 읽는다. 알아볼 수 없으면 기본값 (도우미가 멈추지 않게).
pub fn parse_config(text: &str) -> Config {
    let line = text
        .lines()
        .map(str::trim)
        .find(|l| !l.is_empty())
        .unwrap_or("");
    if line.eq_ignore_ascii_case("off") {
        return Config::Off;
    }

    let mut parts = line.split_ascii_whitespace();
    let (Some(m), Some(v), None) = (parts.next(), parts.next(), parts.next()) else {
        return Config::Default;
    };
    match (m.parse::<u32>(), v.parse::<u32>()) {
        // 다른 프로그램에서도 눌리는 키라 수식 키가 꼭 하나는 있어야 한다 (Foldy GlobalHotkey.TryParse와 같은 규칙)
        (Ok(mods), Ok(vk))
            if mods & MOD_ALL != 0 && mods & !MOD_ALL == 0 && (1..=0xFE).contains(&vk) =>
        {
            Config::Key { mods, vk }
        }
        _ => Config::Default,
    }
}

/// 설치된 키보드 배열 중 Ctrl+Space를 입력기 전환에 쓰는 언어(중국어 0x04, 일본어 0x11)가 있나.
/// 값은 GetKeyboardLayoutList가 주는 HKL (아래 16비트가 LANGID, 그 아래 10비트가 주 언어).
pub fn layouts_conflict_with_ctrl_space(layouts: &[usize]) -> bool {
    layouts
        .iter()
        .any(|&hkl| matches!(hkl & 0x3FF, 0x04 | 0x11))
}

/// 사용자 이름의 짧은 고정 해시 (FNV-1a 32비트, 대문자 UTF-8). Foldy `SingleInstance.UserHash`와 같아야 한다.
pub fn user_hash(user: &str) -> u32 {
    let mut h: u32 = 0x811c_9dc5;
    for b in user.to_uppercase().bytes() {
        h ^= u32::from(b);
        h = h.wrapping_mul(0x0100_0193);
    }
    h
}

/// 실행 중인 Foldy가 "앞으로 와 달라" 요청을 받는 파이프 이름 (Foldy `SingleInstance.Acquire("Foldy")`와 같은 규칙).
pub fn activate_pipe_path(session: u32, user: &str) -> String {
    format!(r"\\.\pipe\Foldy.{session}.{:08x}.activate", user_hash(user))
}

/// 첫 Foldy가 만들어 두는 이벤트 이름 (Foldy `SingleInstance.Acquire`와 같은 규칙). 있으면 Foldy가 켜져 있다.
pub fn instance_event_name(session: u32, user: &str) -> String {
    format!(r"Local\Foldy.{session}.{:08x}.instance", user_hash(user))
}

/// 파이프로 보내는 요청: 인자 개수(int32 LE) 뒤에 인자마다 길이(int32 LE)와 UTF-8 바이트.
pub fn encode_args(args: &[&str]) -> Vec<u8> {
    let mut out = Vec::new();
    out.extend_from_slice(&(args.len() as i32).to_le_bytes());
    for a in args {
        out.extend_from_slice(&(a.len() as i32).to_le_bytes());
        out.extend_from_slice(a.as_bytes());
    }
    out
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn config_reads_off_key_and_falls_back_to_default() {
        assert_eq!(parse_config("off"), Config::Off);
        assert_eq!(parse_config(" OFF \r\n"), Config::Off);
        assert_eq!(
            parse_config("2 32"),
            Config::Key {
                mods: MOD_CONTROL,
                vk: VK_SPACE
            }
        );
        assert_eq!(
            parse_config("\n6 70\n"),
            Config::Key {
                mods: MOD_CONTROL | MOD_SHIFT,
                vk: 0x46
            }
        );
        assert_eq!(parse_config(""), Config::Default);
        assert_eq!(parse_config("Ctrl+Space"), Config::Default);
        assert_eq!(
            parse_config("0 32"),
            Config::Default,
            "수식 키 없이는 안 됨"
        );
        assert_eq!(
            parse_config("16 32"),
            Config::Default,
            "모르는 수식 키 비트"
        );
        assert_eq!(parse_config("2 0"), Config::Default);
        assert_eq!(parse_config("2 32 1"), Config::Default);
    }

    #[test]
    fn chinese_and_japanese_layouts_conflict() {
        assert!(
            !layouts_conflict_with_ctrl_space(&[0x0412_0412, 0x0409_0409]),
            "한국어·영어"
        );
        assert!(
            layouts_conflict_with_ctrl_space(&[0x0412_0412, 0x0804_0804]),
            "중국어(간체)"
        );
        assert!(
            layouts_conflict_with_ctrl_space(&[0xE001_0411]),
            "일본어 IME"
        );
        assert!(!layouts_conflict_with_ctrl_space(&[]));
    }

    /// Foldy 쪽 시험(SingleInstanceTests.User_hash_matches_the_helper)과 같은 값.
    #[test]
    fn user_hash_is_stable_and_case_insensitive() {
        assert_eq!(user_hash("david"), 0x73d6_ce3d);
        assert_eq!(user_hash("DAVID"), 0x73d6_ce3d);
        assert_eq!(user_hash("차선생"), 0x8ffa_604b);
        assert_eq!(
            activate_pipe_path(1, "David"),
            r"\\.\pipe\Foldy.1.73d6ce3d.activate"
        );
    }

    #[test]
    fn gather_setting_reads_hours_or_off() {
        assert_eq!(parse_gather("1"), Some(1));
        assert_eq!(parse_gather("\n 6 \r\n"), Some(6));
        assert_eq!(parse_gather("24"), Some(24));
        assert_eq!(parse_gather("off"), None);
        assert_eq!(parse_gather(""), None);
        assert_eq!(parse_gather("0"), None);
        assert_eq!(parse_gather("1000"), None);
        assert_eq!(parse_gather("1h"), None);
    }

    #[test]
    fn gather_waits_ten_minutes_then_the_interval() {
        let start = 1_000_000;
        assert!(!gather_due(start, start, None, 1));
        assert!(!gather_due(start + 599, start, None, 1));
        assert!(gather_due(start + 600, start, None, 1));
        let last = start + 600;
        assert!(!gather_due(last + 3599, start, Some(last), 1));
        assert!(gather_due(last + 3600, start, Some(last), 1));
        assert!(!gather_due(last + 5 * 3600, start, Some(last), 6));
        assert!(gather_due(last + 6 * 3600, start, Some(last), 6));
        assert!(
            gather_due(last - 10, start, Some(last), 6),
            "시계가 거꾸로 감"
        );
    }

    #[test]
    fn instance_event_matches_foldy() {
        assert_eq!(
            instance_event_name(1, "David"),
            r"Local\Foldy.1.73d6ce3d.instance"
        );
    }

    #[test]
    fn request_matches_foldy_pipe_protocol() {
        let b = encode_args(&[QUICK_SEARCH_ARG]);
        assert_eq!(&b[..4], &1i32.to_le_bytes());
        assert_eq!(&b[4..8], &14i32.to_le_bytes());
        assert_eq!(&b[8..], b"--quick-search");
    }
}
