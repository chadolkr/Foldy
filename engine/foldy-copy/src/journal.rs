//! 작업 저널 (설계서 7.2: jobs/<id>.journal, 추가 전용 레코드 = 길이 + CRC32 + 본문).
//!
//! 레코드 배치: `[본문 길이 u32 LE][본문 CRC32 u32 LE][본문 (UTF-8 JSON)]`.
//! 크래시로 마지막 레코드가 잘렸거나 CRC가 맞지 않으면 그 앞까지만 유효로 보고,
//! 이어 쓰기 전에 파일을 그 길이로 자른다.

use std::collections::HashMap;
use std::fs::{File, OpenOptions};
use std::io::{self, Read, Seek, SeekFrom, Write};
use std::path::Path;
use std::sync::Mutex;

use serde::{Deserialize, Serialize};

/// 레코드 하나의 최대 본문 길이 (손상된 길이 필드로 큰 메모리를 잡지 않게).
pub const MAX_RECORD: u32 = 16 * 1024 * 1024;

const fn make_table() -> [u32; 256] {
    let mut t = [0u32; 256];
    let mut i = 0;
    while i < 256 {
        let mut c = i as u32;
        let mut k = 0;
        while k < 8 {
            c = if c & 1 != 0 { 0xEDB8_8320 ^ (c >> 1) } else { c >> 1 };
            k += 1;
        }
        t[i] = c;
        i += 1;
    }
    t
}

static CRC_TABLE: [u32; 256] = make_table();

/// CRC-32 (IEEE 802.3, zlib과 같은 값). 저널과 선택적 해시 검증에 쓴다.
#[derive(Clone, Copy)]
pub struct Crc32(u32);

impl Default for Crc32 {
    fn default() -> Self {
        Crc32(!0)
    }
}

impl Crc32 {
    pub fn update(&mut self, data: &[u8]) {
        let mut c = self.0;
        for &b in data {
            c = CRC_TABLE[((c ^ b as u32) & 0xff) as usize] ^ (c >> 8);
        }
        self.0 = c;
    }

    pub fn finish(self) -> u32 {
        !self.0
    }
}

pub fn crc32(data: &[u8]) -> u32 {
    let mut c = Crc32::default();
    c.update(data);
    c.finish()
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(tag = "t", rename_all = "snake_case")]
pub enum Record {
    /// 첫 레코드: 작업 명세(JSON 문자열). 이어하기 때 C#이 fc_journal_read_spec으로 읽는다.
    Plan { spec: String },
    /// 파일 복사를 시작함 (끝나지 않았으면 대상은 우리가 만든 미완성 파일).
    Begin { s: String, d: String },
    Done { s: String, d: String },
    Fail { s: String, e: String },
    End,
}

pub fn encode(rec: &Record) -> Vec<u8> {
    let body = serde_json::to_vec(rec).unwrap_or_default();
    let mut out = Vec::with_capacity(body.len() + 8);
    out.extend_from_slice(&(body.len() as u32).to_le_bytes());
    out.extend_from_slice(&crc32(&body).to_le_bytes());
    out.extend_from_slice(&body);
    out
}

#[derive(Debug, Default)]
pub struct Replay {
    pub records: Vec<Record>,
    /// 유효한 레코드가 끝나는 바이트 위치
    pub valid_len: u64,
    /// 뒤에 잘리거나 손상된 부분이 있었는지
    pub torn: bool,
}

/// 바이트열을 앞에서부터 읽어 유효한 레코드만 돌려준다.
pub fn replay_bytes(data: &[u8]) -> Replay {
    let mut r = Replay::default();
    let mut pos = 0usize;
    while pos < data.len() {
        if data.len() - pos < 8 {
            r.torn = true;
            break;
        }
        let len = u32::from_le_bytes([data[pos], data[pos + 1], data[pos + 2], data[pos + 3]]);
        let crc = u32::from_le_bytes([data[pos + 4], data[pos + 5], data[pos + 6], data[pos + 7]]);
        if len > MAX_RECORD || data.len() - pos - 8 < len as usize {
            r.torn = true;
            break;
        }
        let body = &data[pos + 8..pos + 8 + len as usize];
        if crc32(body) != crc {
            r.torn = true;
            break;
        }
        let Ok(rec) = serde_json::from_slice::<Record>(body) else {
            r.torn = true;
            break;
        };
        r.records.push(rec);
        pos += 8 + len as usize;
        r.valid_len = pos as u64;
    }
    r
}

pub fn read(path: &Path) -> io::Result<Replay> {
    let mut data = Vec::new();
    File::open(path)?.read_to_end(&mut data)?;
    Ok(replay_bytes(&data))
}

/// 이어하기에 필요한 요약.
#[derive(Debug, Default)]
pub struct ResumeState {
    pub spec: Option<String>,
    /// 원본 경로 → 대상 경로 (검증까지 끝난 파일)
    pub done: HashMap<String, String>,
    /// 시작했지만 끝나지 않은 파일 (원본 → 대상)
    pub begun: HashMap<String, String>,
    pub ended: bool,
}

impl ResumeState {
    pub fn from_records(records: &[Record]) -> Self {
        let mut st = ResumeState::default();
        for r in records {
            match r {
                Record::Plan { spec } => st.spec = Some(spec.clone()),
                Record::Begin { s, d } => {
                    st.begun.insert(s.clone(), d.clone());
                }
                Record::Done { s, d } => {
                    st.begun.remove(s);
                    st.done.insert(s.clone(), d.clone());
                }
                Record::Fail { s, .. } => {
                    st.begun.remove(s);
                }
                Record::End => st.ended = true,
            }
        }
        st
    }
}

pub struct Journal {
    file: Mutex<File>,
}

impl Journal {
    /// 새 저널을 만들고 Plan 레코드를 쓴다 (기존 파일은 덮어씀).
    pub fn create(path: &Path, spec_json: &str) -> io::Result<Journal> {
        if let Some(dir) = path.parent() {
            std::fs::create_dir_all(dir)?;
        }
        let file = OpenOptions::new().create(true).write(true).truncate(true).open(path)?;
        let j = Journal { file: Mutex::new(file) };
        j.append(&Record::Plan { spec: spec_json.to_owned() })?;
        Ok(j)
    }

    /// 기존 저널을 읽고 손상된 꼬리를 잘라낸 뒤 이어 쓰기용으로 연다.
    pub fn open_resume(path: &Path) -> io::Result<(Journal, ResumeState)> {
        let replay = read(path)?;
        let mut file = OpenOptions::new().read(true).write(true).open(path)?;
        file.set_len(replay.valid_len)?;
        file.seek(SeekFrom::End(0))?;
        Ok((Journal { file: Mutex::new(file) }, ResumeState::from_records(&replay.records)))
    }

    /// 레코드 하나를 한 번의 WriteFile로 추가한다. 프로세스 크래시에는 안전하고
    /// (OS 캐시에 남음), 정전 대비 fsync는 하지 않는다 (작은 파일마다 하면 너무 느림).
    pub fn append(&self, rec: &Record) -> io::Result<()> {
        let bytes = encode(rec);
        let mut f = self.file.lock().unwrap_or_else(|p| p.into_inner());
        f.write_all(&bytes)
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn crc32_known_vector() {
        assert_eq!(crc32(b"123456789"), 0xCBF4_3926);
        assert_eq!(crc32(b""), 0);
        let mut c = Crc32::default();
        c.update(b"1234");
        c.update(b"56789");
        assert_eq!(c.finish(), 0xCBF4_3926);
    }

    fn sample() -> Vec<Record> {
        vec![
            Record::Plan { spec: "{}".into() },
            Record::Begin { s: "C:\\a\\한글.txt".into(), d: "D:\\o\\한글.txt".into() },
            Record::Done { s: "C:\\a\\한글.txt".into(), d: "D:\\o\\한글.txt".into() },
            Record::Begin { s: "C:\\a\\b.bin".into(), d: "D:\\o\\b.bin".into() },
            Record::Fail { s: "C:\\a\\c.bin".into(), e: "x".into() },
            Record::End,
        ]
    }

    #[test]
    fn encode_replay_roundtrip() {
        let bytes: Vec<u8> = sample().iter().flat_map(encode).collect();
        let r = replay_bytes(&bytes);
        assert!(!r.torn);
        assert_eq!(r.records, sample());
        assert_eq!(r.valid_len, bytes.len() as u64);
        let st = ResumeState::from_records(&r.records);
        assert_eq!(st.spec.as_deref(), Some("{}"));
        assert_eq!(st.done.len(), 1);
        assert_eq!(st.begun.get("C:\\a\\b.bin").map(String::as_str), Some("D:\\o\\b.bin"));
        assert!(st.ended);
    }

    #[test]
    fn torn_tail_and_bad_crc_stop_replay() {
        let recs = sample();
        let a = encode(&recs[0]);
        let b = encode(&recs[1]);
        let mut bytes = [a.clone(), b.clone()].concat();
        // 잘린 꼬리: 마지막 레코드의 일부만 있음
        bytes.extend_from_slice(&encode(&recs[2])[..5]);
        let r = replay_bytes(&bytes);
        assert!(r.torn);
        assert_eq!(r.records.len(), 2);
        assert_eq!(r.valid_len, (a.len() + b.len()) as u64);

        // 본문 한 바이트 손상 → CRC 불일치로 그 레코드부터 버림
        let mut bad = [a.clone(), b.clone()].concat();
        let last = bad.len() - 2;
        bad[last] ^= 0x55;
        let r = replay_bytes(&bad);
        assert!(r.torn);
        assert_eq!(r.records.len(), 1);

        // 터무니없는 길이 필드
        let mut huge = a.clone();
        huge.extend_from_slice(&u32::MAX.to_le_bytes());
        huge.extend_from_slice(&[0; 8]);
        let r = replay_bytes(&huge);
        assert!(r.torn);
        assert_eq!(r.records.len(), 1);
    }

    #[test]
    fn resume_truncates_torn_tail_and_appends() {
        let dir = std::env::temp_dir().join(format!("foldy-journal-{}", std::process::id()));
        let path = dir.join("jobs").join("t.journal");
        {
            let j = Journal::create(&path, "{\"x\":1}").unwrap();
            j.append(&Record::Done { s: "a".into(), d: "b".into() }).unwrap();
        }
        // 크래시 흉내: 쓰다 만 레코드
        let mut f = OpenOptions::new().append(true).open(&path).unwrap();
        f.write_all(&[9, 0, 0]).unwrap();
        drop(f);

        let (j, st) = Journal::open_resume(&path).unwrap();
        assert_eq!(st.spec.as_deref(), Some("{\"x\":1}"));
        assert!(st.done.contains_key("a"));
        j.append(&Record::Done { s: "c".into(), d: "d".into() }).unwrap();
        drop(j);
        let r = read(&path).unwrap();
        assert!(!r.torn, "잘린 꼬리를 지우고 이어 써야 한다");
        assert_eq!(r.records.len(), 3);
        let _ = std::fs::remove_dir_all(dir);
    }
}
