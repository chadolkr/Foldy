//! 운영체제와 닿는 부분 (프로세스 목록, msiexec 실행, 사용자 세션에 Foldy 띄우기, 폴더 ACL).
//! 시험에서는 [`FakeHost`]로 바꿔 끼운다: 실제 msiexec는 시험에서 절대 실행하지 않는다.

use std::io::{self, Write};
use std::path::{Path, PathBuf};
use std::process::Command;
use std::sync::Mutex;

pub trait Host: Send + Sync {
    /// `exe`(설치 폴더의 Foldy.exe)로 실행 중인 Foldy 창 프로세스 ID들. AI 연결용(`--mcp`)은 빼고 센다.
    fn foldy_processes(&self, exe: &Path) -> Vec<u32>;
    /// AI 앱이 띄운 AI 연결용 Foldy(`Foldy.exe --mcp`, 창 없음) 프로세스 ID들.
    fn mcp_processes(&self, exe: &Path) -> Vec<u32>;
    /// 프로세스를 끝낸다 (AI 연결용 Foldy만 부른다). 끝냈으면 참.
    fn end_process(&self, pid: u32) -> bool;
    /// 설치 명령을 서비스와 분리된 프로세스로 띄우고 끝날 때까지 기다려 종료 코드를 돌려준다.
    /// MSI가 서비스를 멈추면 이 함수는 돌아오지 못할 수 있다 (그때는 다음 시작에서 판단).
    fn run_installer(&self, cmd: Command) -> io::Result<i32>;
    /// 사용자 세션에 Foldy를 띄운다. 프로세스 ID.
    fn launch_foldy(&self, exe: &Path, session_id: u32) -> io::Result<u32>;
    fn active_console_session(&self) -> u32;
    /// 업데이트 폴더를 만들고 ACL을 정한다 (SYSTEM·Administrators 모두, Users 읽기).
    /// 다른 사용자가 미리 만든 폴더였으면 내용을 비우고 참을 돌려준다.
    fn secure_updates_dir(&self, dir: &Path) -> io::Result<bool>;
}

/// 명령을 사람이 읽을 한 줄로 (기록·시험용).
pub fn describe(cmd: &Command) -> String {
    let mut s = cmd.get_program().to_string_lossy().into_owned();
    for a in cmd.get_args() {
        s.push(' ');
        s.push_str(&a.to_string_lossy());
    }
    s
}

/// 시험용 호스트: 명령을 실행하지 않고 기록만 한다. `log`가 있으면 파일에도 한 줄씩 덧붙인다
/// (C# 통합 시험이 `--update-fake-install <파일>`로 결과를 확인).
pub struct FakeHost {
    pub log: Option<PathBuf>,
    pub lines: Mutex<Vec<String>>,
    pub exit_code: Mutex<i32>,
    pub processes: Mutex<Vec<u32>>,
    /// AI 연결용 Foldy (`--mcp`).
    pub mcp: Mutex<Vec<u32>>,
    /// end_process로 끝낸 프로세스.
    pub ended: Mutex<Vec<u32>>,
    pub console_session: u32,
}

impl FakeHost {
    pub fn new(log: Option<PathBuf>) -> Self {
        FakeHost {
            log,
            lines: Mutex::default(),
            exit_code: Mutex::new(0),
            processes: Mutex::default(),
            mcp: Mutex::default(),
            ended: Mutex::default(),
            console_session: 1,
        }
    }

    fn record(&self, line: String) {
        if let Some(p) = &self.log
            && let Ok(mut f) = std::fs::OpenOptions::new().create(true).append(true).open(p)
        {
            let _ = writeln!(f, "{line}");
        }
        self.lines.lock().unwrap_or_else(|e| e.into_inner()).push(line);
    }

    #[cfg(test)]
    pub fn lines(&self) -> Vec<String> {
        self.lines.lock().unwrap_or_else(|e| e.into_inner()).clone()
    }
}

impl Host for FakeHost {
    fn foldy_processes(&self, _exe: &Path) -> Vec<u32> {
        self.processes.lock().unwrap_or_else(|e| e.into_inner()).clone()
    }

    fn mcp_processes(&self, _exe: &Path) -> Vec<u32> {
        self.mcp.lock().unwrap_or_else(|e| e.into_inner()).clone()
    }

    fn end_process(&self, pid: u32) -> bool {
        self.mcp.lock().unwrap_or_else(|e| e.into_inner()).retain(|&p| p != pid);
        self.ended.lock().unwrap_or_else(|e| e.into_inner()).push(pid);
        true
    }

    fn run_installer(&self, cmd: Command) -> io::Result<i32> {
        self.record(format!("install {}", describe(&cmd)));
        Ok(*self.exit_code.lock().unwrap_or_else(|e| e.into_inner()))
    }

    fn launch_foldy(&self, exe: &Path, session_id: u32) -> io::Result<u32> {
        self.record(format!("relaunch {} session={session_id}", exe.display()));
        Ok(4242)
    }

    fn active_console_session(&self) -> u32 {
        self.console_session
    }

    fn secure_updates_dir(&self, dir: &Path) -> io::Result<bool> {
        std::fs::create_dir_all(dir)?;
        Ok(false)
    }
}
