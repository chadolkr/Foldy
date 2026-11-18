//! foldy-copy: 병렬 복사 엔진 코어 (설계서 "병렬 복사 엔진", 5.8, 7.2).
//!
//! 설계서 5.8의 FFI 규칙:
//! - 모든 내보낸 함수는 `catch_unwind`로 감싸고 오류 코드를 반환한다 (panic이 Foldy.exe를 죽이지 않음).
//! - 문자열은 UTF-16 (포인터 + 길이)로 주고받는다.
//! - 작업은 포인터가 아닌 정수 핸들로 다룬다. 잘못된/해제된 핸들은 오류 코드만 돌려준다.
//! - 진행률은 C#이 초당 약 10회 `fc_job_poll`로 가져간다 (콜백 없음).
//!
//! 파이프라인: [`scan`] → [`plan`] → 대형/소형 두 줄 → 워커 N개(CopyFile2, [`win`]) → [`verify`] → 마무리.
//! 스레드 수는 [`tuner`]가, 이어하기는 [`journal`]이 맡는다. 자세한 내용은 docs/copy-engine.md.

pub mod ffi;
mod job;
pub mod journal;
pub mod plan;
pub mod scan;
pub mod spec;
pub mod tuner;
pub mod verify;
pub mod win;

pub use job::{Job, JobState, new_job, start};
pub use spec::Spec;
