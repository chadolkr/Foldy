//! NTFS 부트 섹터와 MFT 레코드(FILE 레코드) 파서. 순수 바이트 해석만 하므로 관리자 권한 없이 시험할 수 있다.
//!
//! 깨진 입력에서도 panic하지 않는 것이 원칙이다 (설계서 10.1 퍼징 항목): 모든 오프셋은 범위를 검사하고
//! 실패하면 `ParseError`를 돌려준다.

use std::fmt;

/// 레코드 번호(파일 참조 번호의 하위 48비트).
pub const RECORD_MASK: u64 = 0x0000_FFFF_FFFF_FFFF;

/// 속성 타입 코드.
pub const ATTR_STANDARD_INFORMATION: u32 = 0x10;
pub const ATTR_ATTRIBUTE_LIST: u32 = 0x20;
pub const ATTR_FILE_NAME: u32 = 0x30;
pub const ATTR_DATA: u32 = 0x80;
pub const ATTR_END: u32 = 0xFFFF_FFFF;

/// $FILE_NAME 이름공간. DOS(8.3)만 있는 이름은 색인에서 뺀다.
pub const NAMESPACE_POSIX: u8 = 0;
pub const NAMESPACE_WIN32: u8 = 1;
pub const NAMESPACE_DOS: u8 = 2;
pub const NAMESPACE_WIN32_AND_DOS: u8 = 3;

/// 레코드 헤더 플래그.
const RECORD_IN_USE: u16 = 0x0001;
const RECORD_IS_DIRECTORY: u16 = 0x0002;

/// 비상주 속성 플래그.
const ATTR_FLAG_COMPRESSED: u16 = 0x0001;
const ATTR_FLAG_SPARSE: u16 = 0x8000;

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum ParseError {
    NotNtfs,
    BadBootSector(&'static str),
    BadSignature,
    BadFixup,
    Truncated(&'static str),
    BadRunlist,
}

impl fmt::Display for ParseError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::NotNtfs => write!(f, "not an NTFS volume"),
            Self::BadBootSector(w) => write!(f, "bad NTFS boot sector: {w}"),
            Self::BadSignature => write!(f, "bad FILE record signature"),
            Self::BadFixup => write!(f, "update sequence (fixup) mismatch"),
            Self::Truncated(w) => write!(f, "record truncated: {w}"),
            Self::BadRunlist => write!(f, "bad data runlist"),
        }
    }
}

impl std::error::Error for ParseError {}

// ── 작은 LE 읽기 도우미 (범위 밖이면 None) ──

fn u16_at(b: &[u8], o: usize) -> Option<u16> {
    b.get(o..o.checked_add(2)?).map(|s| u16::from_le_bytes([s[0], s[1]]))
}

fn u32_at(b: &[u8], o: usize) -> Option<u32> {
    b.get(o..o.checked_add(4)?).map(|s| u32::from_le_bytes([s[0], s[1], s[2], s[3]]))
}

fn u64_at(b: &[u8], o: usize) -> Option<u64> {
    let s = b.get(o..o.checked_add(8)?)?;
    let mut a = [0u8; 8];
    a.copy_from_slice(s);
    Some(u64::from_le_bytes(a))
}

fn i64_at(b: &[u8], o: usize) -> Option<i64> {
    u64_at(b, o).map(|v| v as i64)
}

/// 부트 섹터에서 읽은 볼륨 기하 정보.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct BootSector {
    pub bytes_per_sector: u32,
    pub cluster_size: u32,
    pub total_sectors: u64,
    pub mft_lcn: u64,
    pub record_size: u32,
    /// 볼륨 일련번호 (부트 섹터 0x48)
    pub serial: u64,
}

impl BootSector {
    /// $MFT 레코드 0번의 바이트 위치.
    pub fn mft_offset(&self) -> u64 {
        self.mft_lcn * self.cluster_size as u64
    }
}

/// 부트 섹터(최소 512바이트) 해석.
pub fn parse_boot_sector(b: &[u8]) -> Result<BootSector, ParseError> {
    if b.len() < 512 {
        return Err(ParseError::BadBootSector("shorter than 512 bytes"));
    }
    if &b[3..11] != b"NTFS    " {
        return Err(ParseError::NotNtfs);
    }
    let bps = u16_at(b, 0x0B).unwrap_or(0) as u32;
    if !(256..=4096).contains(&bps) || !bps.is_power_of_two() {
        return Err(ParseError::BadBootSector("bytes per sector"));
    }
    let spc_raw = b[0x0D];
    // 64KB를 넘는 클러스터는 2^(256-x)로 기록된다.
    let spc: u32 = if spc_raw > 0x80 {
        let shift = 256 - spc_raw as u32;
        if shift > 16 {
            return Err(ParseError::BadBootSector("sectors per cluster"));
        }
        1 << shift
    } else {
        spc_raw as u32
    };
    if spc == 0 || !spc.is_power_of_two() {
        return Err(ParseError::BadBootSector("sectors per cluster"));
    }
    let cluster_size = bps * spc;
    let total_sectors = u64_at(b, 0x28).unwrap_or(0);
    let mft_lcn = u64_at(b, 0x30).unwrap_or(0);
    let cpr = b[0x40] as i8;
    // 양수면 클러스터 수, 음수면 2^-x 바이트.
    let record_size: u32 = if cpr > 0 {
        (cpr as u32).checked_mul(cluster_size).ok_or(ParseError::BadBootSector("record size"))?
    } else {
        let shift = (-(cpr as i32)) as u32;
        if !(9..=16).contains(&shift) {
            return Err(ParseError::BadBootSector("record size"));
        }
        1 << shift
    };
    if !(512..=65536).contains(&record_size) {
        return Err(ParseError::BadBootSector("record size"));
    }
    if mft_lcn == 0 || mft_lcn.checked_mul(cluster_size as u64).is_none() {
        return Err(ParseError::BadBootSector("mft lcn"));
    }
    let serial = u64_at(b, 0x48).unwrap_or(0);
    Ok(BootSector { bytes_per_sector: bps, cluster_size, total_sectors, mft_lcn, record_size, serial })
}

/// Update Sequence Array(fixup)를 적용한다. 각 512바이트 단위의 마지막 2바이트가
/// USN과 같은지 확인하고 원래 값으로 되돌린다. 레코드 크기와 USA 길이로 간격을 계산한다.
pub fn apply_fixup(rec: &mut [u8]) -> Result<(), ParseError> {
    if rec.len() < 48 || &rec[0..4] != b"FILE" {
        return Err(ParseError::BadSignature);
    }
    let usa_off = u16_at(rec, 4).ok_or(ParseError::BadFixup)? as usize;
    let usa_count = u16_at(rec, 6).ok_or(ParseError::BadFixup)? as usize;
    if usa_count < 2 {
        return Err(ParseError::BadFixup);
    }
    let sectors = usa_count - 1;
    if !rec.len().is_multiple_of(sectors) {
        return Err(ParseError::BadFixup);
    }
    let stride = rec.len() / sectors;
    if stride < 256 || usa_off + usa_count * 2 > rec.len() {
        return Err(ParseError::BadFixup);
    }
    let usn = [rec[usa_off], rec[usa_off + 1]];
    for i in 0..sectors {
        let end = (i + 1) * stride - 2;
        if rec[end..end + 2] != usn {
            return Err(ParseError::BadFixup);
        }
    }
    for i in 0..sectors {
        let end = (i + 1) * stride - 2;
        let src = usa_off + 2 + i * 2;
        rec[end] = rec[src];
        rec[end + 1] = rec[src + 1];
    }
    Ok(())
}

/// 데이터 런 하나. `lcn`이 None이면 희소(sparse) 구간.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct Run {
    pub lcn: Option<u64>,
    pub clusters: u64,
}

/// 런리스트 디코딩. 헤더 바이트의 하위 4비트 = 길이 바이트 수, 상위 4비트 = 오프셋 바이트 수.
/// 오프셋은 직전 LCN에 대한 부호 있는 상대값이다.
pub fn decode_runlist(b: &[u8]) -> Result<Vec<Run>, ParseError> {
    let mut runs = Vec::new();
    let mut pos = 0usize;
    let mut lcn: i64 = 0;
    loop {
        let Some(&h) = b.get(pos) else { return Err(ParseError::BadRunlist) };
        if h == 0 {
            return Ok(runs);
        }
        let len_n = (h & 0x0F) as usize;
        let off_n = (h >> 4) as usize;
        if len_n == 0 || len_n > 8 || off_n > 8 {
            return Err(ParseError::BadRunlist);
        }
        let lb = b.get(pos + 1..pos + 1 + len_n).ok_or(ParseError::BadRunlist)?;
        let mut clusters: u64 = 0;
        for (i, &x) in lb.iter().enumerate() {
            clusters |= (x as u64) << (8 * i);
        }
        let ob = b.get(pos + 1 + len_n..pos + 1 + len_n + off_n).ok_or(ParseError::BadRunlist)?;
        let run_lcn = if off_n == 0 {
            None
        } else {
            let mut delta: i64 = 0;
            for (i, &x) in ob.iter().enumerate() {
                delta |= (x as i64) << (8 * i);
            }
            // 부호 확장
            let bits = 8 * off_n as u32;
            if bits < 64 && (delta >> (bits - 1)) & 1 == 1 {
                delta |= -1i64 << bits;
            }
            lcn = lcn.checked_add(delta).ok_or(ParseError::BadRunlist)?;
            if lcn < 0 {
                return Err(ParseError::BadRunlist);
            }
            Some(lcn as u64)
        };
        runs.push(Run { lcn: run_lcn, clusters });
        pos += 1 + len_n + off_n;
    }
}

/// 레코드 안의 속성 하나(헤더 포함 전체 바이트).
#[derive(Debug, Clone, Copy)]
pub struct Attr<'a> {
    pub type_code: u32,
    pub non_resident: bool,
    pub name_len: u8,
    pub flags: u16,
    pub raw: &'a [u8],
}

impl<'a> Attr<'a> {
    /// 상주 속성의 값.
    pub fn resident_value(&self) -> Option<&'a [u8]> {
        if self.non_resident {
            return None;
        }
        let len = u32_at(self.raw, 0x10)? as usize;
        let off = u16_at(self.raw, 0x14)? as usize;
        self.raw.get(off..off.checked_add(len)?)
    }

    pub fn starting_vcn(&self) -> u64 {
        if self.non_resident { u64_at(self.raw, 0x10).unwrap_or(0) } else { 0 }
    }

    /// 비상주 속성의 런리스트.
    pub fn runs(&self) -> Result<Vec<Run>, ParseError> {
        if !self.non_resident {
            return Ok(Vec::new());
        }
        let off = u16_at(self.raw, 0x20).ok_or(ParseError::Truncated("runlist offset"))? as usize;
        decode_runlist(self.raw.get(off..).ok_or(ParseError::Truncated("runlist"))?)
    }

    /// (논리 크기, 실제 차지 공간). 압축·희소 파일은 compressed size가 실제 할당량이다.
    pub fn sizes(&self) -> Option<(u64, u64)> {
        if self.non_resident {
            let alloc = u64_at(self.raw, 0x28)?;
            let size = u64_at(self.raw, 0x30)?;
            let real_alloc = if self.flags & (ATTR_FLAG_COMPRESSED | ATTR_FLAG_SPARSE) != 0 {
                u64_at(self.raw, 0x40).unwrap_or(alloc)
            } else {
                alloc
            };
            Some((size, real_alloc))
        } else {
            // 상주 데이터는 MFT 레코드 안에 있으므로 추가로 차지하는 클러스터가 없다.
            Some((u32_at(self.raw, 0x10)? as u64, 0))
        }
    }
}

/// 레코드 안의 속성들을 차례로 돌려준다. 깨진 길이를 만나면 거기서 멈춘다.
pub fn attributes(rec: &[u8]) -> impl Iterator<Item = Attr<'_>> {
    let first = u16_at(rec, 0x14).unwrap_or(0) as usize;
    let used = (u32_at(rec, 0x18).unwrap_or(0) as usize).min(rec.len());
    let mut pos = if first >= 0x18 { first } else { usize::MAX };
    std::iter::from_fn(move || {
        if pos >= used {
            return None;
        }
        let type_code = u32_at(rec, pos)?;
        if type_code == ATTR_END {
            pos = usize::MAX;
            return None;
        }
        let len = u32_at(rec, pos + 4)? as usize;
        if len < 0x18 || pos + len > used || !len.is_multiple_of(8) {
            pos = usize::MAX;
            return None;
        }
        let raw = &rec[pos..pos + len];
        pos += len;
        Some(Attr { type_code, non_resident: raw[8] != 0, name_len: raw[9], flags: u16_at(raw, 0x0C)?, raw })
    })
}

/// $FILE_NAME 하나.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct FileName {
    pub parent: u64,
    pub parent_seq: u16,
    pub namespace: u8,
    pub name: String,
    /// $FILE_NAME에 기록된 크기 (자주 낡았으므로 $DATA가 없을 때만 쓴다)
    pub size: u64,
    pub alloc: u64,
}

/// $ATTRIBUTE_LIST 항목. 속성이 다른(확장) 레코드에 있을 때 그 위치를 알려 준다.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct AttrListEntry {
    pub type_code: u32,
    pub starting_vcn: u64,
    pub record: u64,
}

/// 레코드 하나를 해석한 결과.
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct ParsedRecord {
    pub record_no: u64,
    pub sequence: u16,
    /// 확장 레코드면 기본 레코드 번호, 아니면 0
    pub base_record: u64,
    pub in_use: bool,
    pub is_dir: bool,
    /// DOS 전용(8.3) 이름을 뺀 이름들. 둘 이상이면 하드링크.
    pub names: Vec<FileName>,
    /// 이름 없는 $DATA의 (논리 크기, 실제 할당). 시작 VCN 0 조각에서만 읽는다.
    pub data: Option<(u64, u64)>,
    /// $STANDARD_INFORMATION 수정 시각(FILETIME)과 파일 특성
    pub mtime: Option<i64>,
    pub attributes: u32,
    pub has_attr_list: bool,
    /// 상주 $ATTRIBUTE_LIST의 항목들 (비상주 목록은 최선 노력으로 무시)
    pub attr_list: Vec<AttrListEntry>,
}

impl ParsedRecord {
    /// $DATA가 없으면 $FILE_NAME의 크기로 대신한다.
    pub fn sizes_or_fallback(&self) -> (u64, u64, bool) {
        match self.data {
            Some((s, a)) => (s, a, true),
            None => self.names.first().map(|n| (n.size, n.alloc, false)).unwrap_or((0, 0, false)),
        }
    }
}

fn parse_file_name(v: &[u8]) -> Option<FileName> {
    let parent_ref = u64_at(v, 0)?;
    let alloc = u64_at(v, 0x28)?;
    let size = u64_at(v, 0x30)?;
    let name_len = *v.get(0x40)? as usize;
    let namespace = *v.get(0x41)?;
    let nb = v.get(0x42..0x42 + name_len * 2)?;
    let units: Vec<u16> = nb.as_chunks::<2>().0.iter().map(|c| u16::from_le_bytes(*c)).collect();
    Some(FileName {
        parent: parent_ref & RECORD_MASK,
        parent_seq: (parent_ref >> 48) as u16,
        namespace,
        name: String::from_utf16_lossy(&units),
        size,
        alloc,
    })
}

fn parse_attr_list(v: &[u8]) -> Vec<AttrListEntry> {
    let mut out = Vec::new();
    let mut pos = 0usize;
    while pos + 0x1A <= v.len() {
        let (Some(t), Some(len)) = (u32_at(v, pos), u16_at(v, pos + 4)) else { break };
        let len = len as usize;
        if len < 0x1A || pos + len > v.len() {
            break;
        }
        let vcn = u64_at(v, pos + 8).unwrap_or(0);
        let rec = u64_at(v, pos + 0x10).unwrap_or(0) & RECORD_MASK;
        out.push(AttrListEntry { type_code: t, starting_vcn: vcn, record: rec });
        pos += len;
    }
    out
}

/// fixup이 적용된 레코드를 해석한다. `record_no`는 읽은 위치로 계산한 번호
/// (헤더 0x2C의 번호는 XP 이후에만 있으므로 위치를 우선한다).
pub fn parse_record(rec: &[u8], record_no: u64) -> Result<ParsedRecord, ParseError> {
    if rec.len() < 48 || &rec[0..4] != b"FILE" {
        return Err(ParseError::BadSignature);
    }
    let flags = u16_at(rec, 0x16).ok_or(ParseError::Truncated("flags"))?;
    let mut p = ParsedRecord {
        record_no,
        sequence: u16_at(rec, 0x10).unwrap_or(0),
        base_record: u64_at(rec, 0x20).unwrap_or(0) & RECORD_MASK,
        in_use: flags & RECORD_IN_USE != 0,
        is_dir: flags & RECORD_IS_DIRECTORY != 0,
        ..Default::default()
    };
    if !p.in_use {
        return Ok(p);
    }
    for a in attributes(rec) {
        match a.type_code {
            ATTR_STANDARD_INFORMATION => {
                if let Some(v) = a.resident_value() {
                    p.mtime = i64_at(v, 8);
                    p.attributes = u32_at(v, 32).unwrap_or(0);
                }
            }
            ATTR_FILE_NAME => {
                if let Some(fname) = a.resident_value().and_then(parse_file_name)
                    && fname.namespace != NAMESPACE_DOS
                {
                    p.names.push(fname);
                }
            }
            ATTR_ATTRIBUTE_LIST => {
                p.has_attr_list = true;
                if let Some(v) = a.resident_value() {
                    p.attr_list = parse_attr_list(v);
                }
            }
            ATTR_DATA if a.name_len == 0 && a.starting_vcn() == 0 && p.data.is_none() => {
                p.data = a.sizes();
            }
            _ => {}
        }
    }
    Ok(p)
}

/// $MFT(레코드 0번)의 이름 없는 $DATA: (논리 크기, 런리스트).
pub fn mft_data_runs(rec: &[u8]) -> Result<(u64, Vec<Run>), ParseError> {
    let mut size = None;
    let mut runs = Vec::new();
    for a in attributes(rec) {
        if a.type_code == ATTR_DATA && a.name_len == 0 && a.non_resident {
            if a.starting_vcn() == 0 {
                size = a.sizes().map(|s| s.0);
            }
            runs.extend(a.runs()?);
        }
    }
    match size {
        Some(s) if !runs.is_empty() => Ok((s, runs)),
        _ => Err(ParseError::Truncated("$MFT $DATA")),
    }
}

/// 시험용 합성 MFT 레코드 생성기. 실제 NTFS 형식 그대로 바이트를 만든다.
#[cfg(test)]
pub mod fixture {
    use super::*;

    pub const RECORD_SIZE: usize = 1024;

    pub struct RecordBuilder {
        attrs: Vec<Vec<u8>>,
        flags: u16,
        base: u64,
        seq: u16,
    }

    fn put_u16(v: &mut [u8], o: usize, x: u16) {
        v[o..o + 2].copy_from_slice(&x.to_le_bytes());
    }
    fn put_u32(v: &mut [u8], o: usize, x: u32) {
        v[o..o + 4].copy_from_slice(&x.to_le_bytes());
    }
    fn put_u64(v: &mut [u8], o: usize, x: u64) {
        v[o..o + 8].copy_from_slice(&x.to_le_bytes());
    }

    fn resident(type_code: u32, name: &str, value: &[u8]) -> Vec<u8> {
        let name16: Vec<u16> = name.encode_utf16().collect();
        let name_off = 0x18;
        let value_off = (name_off + name16.len() * 2 + 7) & !7;
        let len = (value_off + value.len() + 7) & !7;
        let mut a = vec![0u8; len];
        put_u32(&mut a, 0, type_code);
        put_u32(&mut a, 4, len as u32);
        a[8] = 0;
        a[9] = name16.len() as u8;
        put_u16(&mut a, 0x0A, name_off as u16);
        put_u32(&mut a, 0x10, value.len() as u32);
        put_u16(&mut a, 0x14, value_off as u16);
        for (i, u) in name16.iter().enumerate() {
            put_u16(&mut a, name_off + i * 2, *u);
        }
        a[value_off..value_off + value.len()].copy_from_slice(value);
        a
    }

    /// 런리스트 인코딩 (시험용)
    pub fn encode_runs(runs: &[(i64, u64)]) -> Vec<u8> {
        let mut out = Vec::new();
        let mut prev = 0i64;
        for &(lcn, len) in runs {
            let lb: Vec<u8> = {
                let mut v = len.to_le_bytes().to_vec();
                while v.len() > 1 && *v.last().unwrap() == 0 {
                    v.pop();
                }
                v
            };
            let delta = lcn - prev;
            prev = lcn;
            let mut ob = delta.to_le_bytes().to_vec();
            // 부호를 유지하는 최소 바이트 수
            while ob.len() > 1 {
                let last = ob[ob.len() - 1];
                let prev_hi = ob[ob.len() - 2] & 0x80;
                if (last == 0 && prev_hi == 0) || (last == 0xFF && prev_hi != 0) {
                    ob.pop();
                } else {
                    break;
                }
            }
            out.push(((ob.len() as u8) << 4) | lb.len() as u8);
            out.extend(lb);
            out.extend(ob);
        }
        out.push(0);
        out
    }

    impl RecordBuilder {
        pub fn new(is_dir: bool) -> Self {
            Self { attrs: Vec::new(), flags: RECORD_IN_USE | if is_dir { RECORD_IS_DIRECTORY } else { 0 }, base: 0, seq: 1 }
        }

        pub fn not_in_use(mut self) -> Self {
            self.flags &= !RECORD_IN_USE;
            self
        }

        pub fn extension_of(mut self, base: u64) -> Self {
            self.base = base | (1u64 << 48);
            self
        }

        pub fn std_info(mut self, mtime: i64, attributes: u32) -> Self {
            let mut v = vec![0u8; 72];
            put_u64(&mut v, 0, mtime as u64);
            put_u64(&mut v, 8, mtime as u64);
            put_u64(&mut v, 16, mtime as u64);
            put_u64(&mut v, 24, mtime as u64);
            put_u32(&mut v, 32, attributes);
            self.attrs.push(resident(ATTR_STANDARD_INFORMATION, "", &v));
            self
        }

        pub fn file_name(mut self, parent: u64, name: &str, namespace: u8) -> Self {
            let n16: Vec<u16> = name.encode_utf16().collect();
            let mut v = vec![0u8; 0x42 + n16.len() * 2];
            put_u64(&mut v, 0, parent | (1u64 << 48));
            put_u64(&mut v, 0x28, 4096);
            put_u64(&mut v, 0x30, 1);
            v[0x40] = n16.len() as u8;
            v[0x41] = namespace;
            for (i, u) in n16.iter().enumerate() {
                put_u16(&mut v, 0x42 + i * 2, *u);
            }
            self.attrs.push(resident(ATTR_FILE_NAME, "", &v));
            self
        }

        pub fn resident_data(mut self, name: &str, data: &[u8]) -> Self {
            self.attrs.push(resident(ATTR_DATA, name, data));
            self
        }

        /// 비상주 $DATA. `compressed`면 compressed size 필드를 쓴다.
        pub fn nonresident_data(mut self, size: u64, alloc: u64, runs: &[(i64, u64)], compressed: Option<u64>) -> Self {
            let rl = encode_runs(runs);
            let header = if compressed.is_some() { 0x48 } else { 0x40 };
            let len = (header + rl.len() + 7) & !7;
            let mut a = vec![0u8; len];
            put_u32(&mut a, 0, ATTR_DATA);
            put_u32(&mut a, 4, len as u32);
            a[8] = 1;
            put_u16(&mut a, 0x0A, header as u16);
            if compressed.is_some() {
                put_u16(&mut a, 0x0C, ATTR_FLAG_COMPRESSED);
                put_u16(&mut a, 0x22, 4);
            }
            let clusters: u64 = runs.iter().map(|r| r.1).sum();
            put_u64(&mut a, 0x10, 0);
            put_u64(&mut a, 0x18, clusters.saturating_sub(1));
            put_u16(&mut a, 0x20, header as u16);
            put_u64(&mut a, 0x28, alloc);
            put_u64(&mut a, 0x30, size);
            put_u64(&mut a, 0x38, size);
            if let Some(c) = compressed {
                put_u64(&mut a, 0x40, c);
            }
            a[header..header + rl.len()].copy_from_slice(&rl);
            self.attrs.push(a);
            self
        }

        pub fn attr_list(mut self, entries: &[(u32, u64)]) -> Self {
            let mut v = Vec::new();
            for &(t, rec) in entries {
                let mut e = vec![0u8; 0x20];
                put_u32(&mut e, 0, t);
                put_u16(&mut e, 4, 0x20);
                put_u64(&mut e, 0x10, rec | (1u64 << 48));
                v.extend(e);
            }
            self.attrs.push(resident(ATTR_ATTRIBUTE_LIST, "", &v));
            self
        }

        /// fixup을 적용하기 전의(디스크에 있는 그대로의) 레코드 바이트.
        pub fn build(self, record_no: u32) -> Vec<u8> {
            let mut r = vec![0u8; RECORD_SIZE];
            r[0..4].copy_from_slice(b"FILE");
            let usa_off = 0x30usize;
            let sectors = RECORD_SIZE / 512;
            put_u16(&mut r, 4, usa_off as u16);
            put_u16(&mut r, 6, (sectors + 1) as u16);
            put_u16(&mut r, 0x10, self.seq);
            put_u16(&mut r, 0x12, 1);
            let first = (usa_off + (sectors + 1) * 2 + 7) & !7;
            put_u16(&mut r, 0x14, first as u16);
            put_u16(&mut r, 0x16, self.flags);
            put_u64(&mut r, 0x20, self.base);
            put_u32(&mut r, 0x2C, record_no);
            let mut pos = first;
            for a in &self.attrs {
                r[pos..pos + a.len()].copy_from_slice(a);
                pos += a.len();
            }
            put_u32(&mut r, pos, ATTR_END);
            pos += 8;
            assert!(pos <= RECORD_SIZE - 2, "fixture record too large");
            put_u32(&mut r, 0x18, pos as u32);
            put_u32(&mut r, 0x1C, RECORD_SIZE as u32);
            // fixup: 섹터 끝 2바이트를 USA에 보관하고 USN으로 덮는다.
            let usn: u16 = 0x0A0B;
            put_u16(&mut r, usa_off, usn);
            for i in 0..sectors {
                let end = (i + 1) * 512 - 2;
                let orig = [r[end], r[end + 1]];
                r[usa_off + 2 + i * 2] = orig[0];
                r[usa_off + 3 + i * 2] = orig[1];
                put_u16(&mut r, end, usn);
            }
            r
        }
    }

    /// 합성 부트 섹터.
    pub fn boot_sector(bps: u16, spc: u8, mft_lcn: u64, cpr: i8) -> Vec<u8> {
        let mut b = vec![0u8; 512];
        b[0..3].copy_from_slice(&[0xEB, 0x52, 0x90]);
        b[3..11].copy_from_slice(b"NTFS    ");
        put_u16(&mut b, 0x0B, bps);
        b[0x0D] = spc;
        put_u64(&mut b, 0x28, 1_000_000);
        put_u64(&mut b, 0x30, mft_lcn);
        b[0x40] = cpr as u8;
        b[510] = 0x55;
        b[511] = 0xAA;
        b
    }
}

#[cfg(test)]
mod tests {
    use super::fixture::*;
    use super::*;

    fn parse(mut raw: Vec<u8>, no: u64) -> ParsedRecord {
        apply_fixup(&mut raw).unwrap();
        parse_record(&raw, no).unwrap()
    }

    #[test]
    fn boot_sector_geometry() {
        let b = parse_boot_sector(&boot_sector(512, 8, 786_432, -10)).unwrap();
        assert_eq!(b.cluster_size, 4096);
        assert_eq!(b.record_size, 1024);
        assert_eq!(b.mft_offset(), 786_432 * 4096);
        // 큰 클러스터(2^(256-x) 표기), 양수 cpr
        let b = parse_boot_sector(&boot_sector(512, 0xF4, 10, -10)).unwrap();
        assert_eq!(b.cluster_size, 512 << 12);
        assert_eq!(b.record_size, 1024);
        let b = parse_boot_sector(&boot_sector(512, 1, 10, 2)).unwrap();
        assert_eq!(b.record_size, 1024);
        assert!(parse_boot_sector(&boot_sector(512, 0xF4, 10, 1)).is_err(), "2MB 레코드는 거부");
    }

    #[test]
    fn boot_sector_rejects_non_ntfs_and_garbage() {
        let mut b = boot_sector(512, 8, 4, -10);
        b[3..11].copy_from_slice(b"EXFAT   ");
        assert_eq!(parse_boot_sector(&b), Err(ParseError::NotNtfs));
        assert!(parse_boot_sector(&boot_sector(513, 8, 4, -10)).is_err());
        assert!(parse_boot_sector(&boot_sector(512, 0, 4, -10)).is_err());
        assert!(parse_boot_sector(&boot_sector(512, 8, 0, -10)).is_err());
        assert!(parse_boot_sector(&[0u8; 100]).is_err());
    }

    #[test]
    fn fixup_restores_sector_tails_and_detects_torn_writes() {
        let raw = RecordBuilder::new(false).std_info(1, 0).file_name(5, "a.txt", NAMESPACE_WIN32).build(40);
        // 섹터 끝에 USN이 들어 있어야 한다
        assert_eq!(&raw[510..512], &0x0A0Bu16.to_le_bytes());
        let mut ok = raw.clone();
        apply_fixup(&mut ok).unwrap();
        assert_eq!(&ok[510..512], &[0, 0]);
        // 두 번째 섹터의 끝이 다르면(찢어진 쓰기) 거부
        let mut torn = raw.clone();
        torn[1022] ^= 0xFF;
        assert_eq!(apply_fixup(&mut torn), Err(ParseError::BadFixup));
        let mut bad = raw;
        bad[0] = b'B';
        assert_eq!(apply_fixup(&mut bad), Err(ParseError::BadSignature));
    }

    #[test]
    fn file_record_names_sizes_and_mtime() {
        let raw = RecordBuilder::new(false)
            .std_info(133_000_000_000_000_000, 0x20)
            .file_name(5, "LONGFI~1.TXT", NAMESPACE_DOS)
            .file_name(5, "Long file name 한글 😀.txt", NAMESPACE_WIN32)
            .nonresident_data(10_000, 12_288, &[(1000, 3)], None)
            .build(42);
        let p = parse(raw, 42);
        assert!(p.in_use && !p.is_dir);
        assert_eq!(p.record_no, 42);
        assert_eq!(p.base_record, 0);
        assert_eq!(p.names.len(), 1, "DOS 8.3 이름은 빠진다");
        assert_eq!(p.names[0].name, "Long file name 한글 😀.txt");
        assert_eq!(p.names[0].parent, 5);
        assert_eq!(p.data, Some((10_000, 12_288)));
        assert_eq!(p.mtime, Some(133_000_000_000_000_000));
        assert_eq!(p.attributes, 0x20);
    }

    #[test]
    fn resident_data_named_streams_and_compression() {
        let p = parse(
            RecordBuilder::new(false)
                .std_info(1, 0)
                .file_name(5, "small", NAMESPACE_WIN32_AND_DOS)
                .resident_data("Zone.Identifier", &[1; 30])
                .resident_data("", b"hello")
                .build(50),
            50,
        );
        assert_eq!(p.names[0].name, "small");
        assert_eq!(p.data, Some((5, 0)), "이름 있는 스트림(ADS)은 크기에 넣지 않는다");

        let p = parse(
            RecordBuilder::new(false)
                .std_info(1, 0)
                .file_name(5, "packed.bin", NAMESPACE_POSIX)
                .nonresident_data(1 << 20, 1 << 20, &[(500, 256)], Some(64 * 1024))
                .build(51),
            51,
        );
        assert_eq!(p.data, Some((1 << 20, 64 * 1024)), "압축 파일은 compressed size가 실제 할당");
    }

    #[test]
    fn hard_links_directories_and_unused_records() {
        let p = parse(
            RecordBuilder::new(false)
                .std_info(1, 0)
                .file_name(5, "one.txt", NAMESPACE_WIN32)
                .file_name(77, "two.txt", NAMESPACE_WIN32)
                .build(60),
            60,
        );
        assert_eq!(p.names.iter().map(|n| (n.parent, n.name.as_str())).collect::<Vec<_>>(), [(5, "one.txt"), (77, "two.txt")]);
        let d = parse(RecordBuilder::new(true).std_info(1, 0x10).file_name(5, "dir", NAMESPACE_WIN32).build(61), 61);
        assert!(d.is_dir);
        assert_eq!(d.data, None);
        let free = parse(RecordBuilder::new(false).file_name(5, "gone", NAMESPACE_WIN32).not_in_use().build(62), 62);
        assert!(!free.in_use);
        assert!(free.names.is_empty());
    }

    #[test]
    fn extension_record_and_attribute_list() {
        let base = parse(
            RecordBuilder::new(false)
                .std_info(7, 0)
                .attr_list(&[(ATTR_FILE_NAME, 70), (ATTR_DATA, 71)])
                .file_name(5, "big.vhdx", NAMESPACE_WIN32)
                .build(70),
            70,
        );
        assert!(base.has_attr_list);
        assert_eq!(base.attr_list.len(), 2);
        assert_eq!(base.attr_list[1], AttrListEntry { type_code: ATTR_DATA, starting_vcn: 0, record: 71 });
        assert_eq!(base.data, None);
        let ext = parse(RecordBuilder::new(false).extension_of(70).nonresident_data(5 << 30, 5 << 30, &[(9000, 1 << 18)], None).build(71), 71);
        assert_eq!(ext.base_record, 70);
        assert_eq!(ext.data, Some((5 << 30, 5 << 30)));
    }

    #[test]
    fn runlist_decoding() {
        let enc = fixture::encode_runs(&[(1000, 16), (900, 300), (70_000, 1)]);
        let runs = decode_runlist(&enc).unwrap();
        assert_eq!(
            runs,
            [
                Run { lcn: Some(1000), clusters: 16 },
                Run { lcn: Some(900), clusters: 300 },
                Run { lcn: Some(70_000), clusters: 1 }
            ]
        );
        // 실제 형식 예: 0x21 0x18 0x34 0x56 0x00 → 길이 0x18, LCN 0x5634
        assert_eq!(decode_runlist(&[0x21, 0x18, 0x34, 0x56, 0]).unwrap(), [Run { lcn: Some(0x5634), clusters: 0x18 }]);
        // 희소 구간 (오프셋 바이트 0)
        assert_eq!(decode_runlist(&[0x01, 0x10, 0]).unwrap(), [Run { lcn: None, clusters: 16 }]);
        assert!(decode_runlist(&[0x21, 0x18]).is_err());
        assert!(decode_runlist(&[]).is_err());
        assert!(decode_runlist(&[0x11, 0x01, 0x80, 0]).is_err(), "음수 LCN");
    }

    #[test]
    fn mft_record_zero_runs() {
        let mut raw = RecordBuilder::new(false)
            .std_info(1, 6)
            .file_name(5, "$MFT", NAMESPACE_WIN32_AND_DOS)
            .nonresident_data(4096 * 1024, 4096 * 1024, &[(786_432, 512), (2_000_000, 512)], None)
            .build(0);
        apply_fixup(&mut raw).unwrap();
        let (size, runs) = mft_data_runs(&raw).unwrap();
        assert_eq!(size, 4096 * 1024);
        assert_eq!(runs.len(), 2);
        assert_eq!(runs[1].lcn, Some(2_000_000));
    }

    #[test]
    fn garbage_records_do_not_panic() {
        let good = RecordBuilder::new(false)
            .std_info(1, 0)
            .file_name(5, "x", NAMESPACE_WIN32)
            .nonresident_data(1, 4096, &[(10, 1)], None)
            .build(1);
        let mut seed: u32 = 12345;
        for i in 0..2000 {
            let mut r = good.clone();
            for _ in 0..(1 + i % 16) {
                seed = seed.wrapping_mul(1_103_515_245).wrapping_add(12345);
                let pos = (seed >> 8) as usize % r.len();
                r[pos] = (seed >> 3) as u8;
            }
            let _ = apply_fixup(&mut r);
            let _ = parse_record(&r, 1);
            let _ = mft_data_runs(&r);
            let _ = decode_runlist(&r[(i % 900)..]);
        }
        for n in 0..64 {
            let _ = parse_record(&good[..n], 0);
            let _ = parse_boot_sector(&good[..n]);
        }
    }
}
