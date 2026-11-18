//! 이어받기 다운로드 (설계서 10.5 다운로드와 검증 2: 끊기면 Range 요청으로 이어받기).
//!
//! `<대상>.part`에 받다가 끝나면 이름을 바꾼다. 다음 시도에서는 `.part` 길이부터 `Range: bytes=N-`로
//! 이어서 받는다. 서버가 Range를 무시하고 200을 주면 처음부터 다시 쓴다.
//! 크기는 서명된 update.json의 값을 믿고, 그보다 많이 오면 즉시 중단한다.

use std::fs::{self, File, OpenOptions};
use std::io::{Read, Write};
use std::path::{Path, PathBuf};

use crate::error::{Result, UpdateError};
use crate::http::{HttpClient, net, parse_content_range};

pub fn part_path(dest: &Path) -> PathBuf {
    let mut s = dest.as_os_str().to_owned();
    s.push(".part");
    PathBuf::from(s)
}

/// 한 번 시도한다. 끊기면 [`UpdateError::Incomplete`]나 네트워크 오류를 돌려주고 `.part`는 남긴다.
/// `progress(받은 바이트, 전체)`.
pub fn download_resumable<C: HttpClient + ?Sized>(
    client: &C,
    url: &str,
    dest: &Path,
    expected_size: u64,
    progress: &mut dyn FnMut(u64, u64),
) -> Result<()> {
    let part = part_path(dest);
    let mut have = fs::metadata(&part).map(|m| m.len()).unwrap_or(0);
    if have > expected_size {
        fs::remove_file(&part)?;
        have = 0;
    }

    if have < expected_size {
        let range = format!("bytes={have}-");
        let headers: Vec<(&str, &str)> = if have > 0 {
            vec![("Range", range.as_str())]
        } else {
            Vec::new()
        };
        let resp = client.get(url, &headers)?;
        let mut file = match resp.status {
            206 => {
                let start = resp
                    .header("content-range")
                    .and_then(parse_content_range)
                    .map(|(s, _)| s);
                if start != Some(have) {
                    // 요청과 다른 위치를 주면 믿지 않고 처음부터
                    fs::remove_file(&part).ok();
                    return Err(UpdateError::Network(format!(
                        "Content-Range 불일치 (요청 {have})"
                    )));
                }
                OpenOptions::new().append(true).open(&part)?
            }
            200 => {
                have = 0;
                File::create(&part)?
            }
            416 => {
                // 이미 다 받았다고 하는데 길이가 다르면 처음부터 다시
                fs::remove_file(&part).ok();
                return Err(UpdateError::Network("416 Range Not Satisfiable".into()));
            }
            s => return Err(UpdateError::HttpStatus(s)),
        };
        progress(have, expected_size);
        let mut body = resp.body;
        let mut buf = vec![0u8; 64 * 1024];
        loop {
            let n = match body.read(&mut buf) {
                Ok(0) => break,
                Ok(n) => n,
                Err(e) if e.kind() == std::io::ErrorKind::Interrupted => continue,
                Err(e) => {
                    file.flush().ok();
                    return Err(net(e));
                }
            };
            if have + n as u64 > expected_size {
                drop(file);
                fs::remove_file(&part).ok();
                return Err(UpdateError::SizeMismatch {
                    expected: expected_size,
                    actual: have + n as u64,
                });
            }
            // BufWriter를 쓰지 않는다: 끊겨도 받은 만큼은 파일에 남아야 이어받을 수 있다
            file.write_all(&buf[..n])?;
            have += n as u64;
            progress(have, expected_size);
        }
        file.sync_all()?;
    }

    if have != expected_size {
        return Err(UpdateError::Incomplete {
            received: have,
            expected: expected_size,
        });
    }
    if dest.exists() {
        fs::remove_file(dest)?;
    }
    fs::rename(&part, dest)?;
    Ok(())
}

/// 일시적 오류면 `attempts`번까지 이어받기로 다시 시도한다.
pub fn download_with_retries<C: HttpClient + ?Sized>(
    client: &C,
    url: &str,
    dest: &Path,
    expected_size: u64,
    attempts: u32,
    progress: &mut dyn FnMut(u64, u64),
) -> Result<()> {
    let mut last = None;
    for _ in 0..attempts.max(1) {
        match download_resumable(client, url, dest, expected_size, progress) {
            Ok(()) => return Ok(()),
            Err(e) if e.is_transient() => last = Some(e),
            Err(e) => return Err(e),
        }
    }
    Err(last.unwrap_or(UpdateError::Incomplete {
        received: 0,
        expected: expected_size,
    }))
}
