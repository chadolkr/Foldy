//! NTFS $MFT 파서, 메모리 색인, 이름 검색, USN 저널 리더 (설계서 "MFT/USN 기반 디스크 색인", M2).
//!
//! - [`record`]: 부트 섹터·FILE 레코드·런리스트 해석 (순수 함수, 관리자 권한 없이 시험)
//! - [`index`]: 노드 배열 + 이름 풀 색인, 폴더 누적 크기, 증분 갱신
//! - [`search`]: 접힌 이름 풀 병렬 부분 문자열 검색
//! - [`usn`]: USN 레코드 해석과 색인 반영
//! - [`volume`]: Win32 경계 (볼륨 열기, $MFT 순차 읽기, FSCTL). `unsafe`는 여기에만 있다.

pub mod index;
pub mod record;
pub mod search;
pub mod usn;
#[cfg(windows)]
pub mod volume;

pub use index::{Entry, Node, VolumeIndex, fold_name};
