# 병렬 복사 엔진 (foldy-copy, M3)

robocopy를 호출하지 않고 `robocopy /MT`와 같은 병렬 복사를 Foldy 안에 직접 구현한 Rust cdylib입니다.
설계 근거는 [DESIGN.md "병렬 복사 엔진"](DESIGN.md#병렬-복사-엔진)과 상세 설계서 5.8(FFI), 7.2(저널), 8.2(벤치)입니다.

## 파이프라인

```
스캐너 ─▶ 계획기 ─▶ 대형 줄 (≥256MB, 1~2 스레드, COPY_FILE_NO_BUFFERING, 큰 것부터)
 (scan.rs)  (plan.rs)  소형 줄 (나머지, 워커 N개)            ─▶ 검증기 ─▶ 마무리
                        사용자 해결 줄 (Ask 충돌을 해결한 항목)     (verify.rs)
```

| 단계 | 하는 일 | 파일 |
| --- | --- | --- |
| 스캐너 | 원본을 펼쳐 폴더(부모 먼저)·파일 목록. 링크·정션은 따라가지 않음. 읽지 못한 하위 폴더는 실패 목록 | `scan.rs` |
| 계획기 | 대상 폴더를 먼저 순서대로 만들고, 대상 폴더마다 한 번만 열거해 충돌 판단 (skip/overwrite/rename/ask), 같은 작업 안의 중복 대상도 잡음 | `plan.rs`, `job.rs` |
| 워커 | 상한(SSD 16, HDD 2, 네트워크 32)만큼 미리 만들고 `Gate`로 동시 실행 수만 조절. CopyFile2 + 진행률 콜백 | `job.rs`, `win.rs` |
| 검증기 | 크기 + 수정 시간(GetFileAttributesExW, 핸들 안 엶, 2초 허용), 선택 시 CRC32 내용 비교 | `verify.rs` |
| 마무리 | 폴더 시간 복원(깊은 것부터), 이동이면 빈 원본 폴더 삭제, 최적 스레드 저장, 저널 End | `job.rs` |

- **복사 API**: CopyFile2 (CopyFileEx와 같은 엔진, 덩어리마다 `COPYFILE2_CALLBACK_CHUNK_FINISHED`).
  처음엔 CopyFileExW를 썼지만, 이 개발 환경(Windows 11 빌드 29671)에서 longPathAware 프로세스(.NET 앱 호스트 =
  Foldy.exe, 시험 호스트)가 260자를 넘는 `\\?\` 경로로 CopyFileExW를 부르면 프로세스가 0xC0000417로 즉시 종료됐습니다
  (P/Invoke로 직접 불러도 같음, Rust exe에서는 정상). CopyFile2와 MoveFileExW는 같은 경로에서 정상입니다.
- **일시정지**: 워커는 파일 사이에서, CopyFile2는 진행률 콜백 안에서 기다립니다 (다음 덩어리를 읽지 않음).
- **취소**: 콜백이 `COPYFILE2_PROGRESS_CANCEL`을 돌려주면 API가 미완성 대상을 지웁니다.
- **재시도**: 파일당 1초 뒤 1회, 그래도 실패하면 실패 목록에 넣고 계속합니다. 무한 대기 없음.
- **이동**: 같은 볼륨이면 MoveFileExW (대상에 같은 이름 폴더가 없으면 폴더째 즉시). 다른 볼륨이면 복사 →
  검증 통과 → 원본 삭제. 검증·복사에 실패한 파일의 원본은 남고, 그 폴더도 지우지 않습니다.
- **긴 경로**: Win32 호출은 모두 `\\?\`(UNC는 `\\?\UNC\`) 접두사를 붙입니다.

## 스레드 자동 조절 (`tuner.rs`)

| 경로 | 시작값 | 상한 |
| --- | --- | --- |
| SSD → SSD | 4 | 16 |
| HDD 포함 | 1 | 2 |
| 네트워크 | 8 | 32 |

SSD/HDD는 `IOCTL_STORAGE_QUERY_PROPERTY(StorageDeviceSeekPenaltyProperty)`의 `IncursSeekPenalty`로,
네트워크는 UNC 또는 `GetDriveTypeW == DRIVE_REMOTE`로 판별합니다. 측정 창(기본 2초)마다
점수 = 바이트/초 + 파일/초 × 64KB 를 재서 5% **넘게** 좋아지면 +2, 아니면 되돌리고 5개 창 동안 쉽니다.
일시정지·진행 없는 창은 판단에서 뺍니다. 작업이 끝나면 가장 좋았던 값을 `tuning_path`(JSON, 키
`"ssd:C:\|hdd:D:\"`)에 저장하고, 다음 작업은 그 값에서 시작합니다. `threads`를 주면 그 수로 고정합니다.

## 작업 저널 (`journal.rs`, 설계서 7.2)

`jobs/<id>.journal`은 추가 전용 레코드 `[본문 길이 u32 LE][본문 CRC32 u32 LE][본문 UTF-8 JSON]`의 나열입니다.

| 레코드 | 의미 |
| --- | --- |
| `{"t":"plan","spec":"..."}` | 첫 레코드. 작업 명세 (resume·시험 옵션 제외) |
| `{"t":"begin","s":원본,"d":대상}` | 파일 복사 시작 |
| `{"t":"done","s":..,"d":..}` | 검증까지 끝남 |
| `{"t":"fail","s":..,"e":오류}` | 실패 목록 |
| `{"t":"end"}` | 마무리 끝 |

- 레코드마다 WriteFile 한 번 (프로세스 크래시에 안전, 정전 대비 fsync는 하지 않음).
- 읽을 때 길이가 모자라거나 CRC가 틀린 첫 레코드에서 멈추고, 이어 쓰기 전에 그 길이로 파일을 자릅니다.
- 이어하기 (`"resume": true`): 다시 스캔한 뒤 `done`인 파일은 대상이 크기+시간이 같으면 건너뛰고,
  `begin`만 있는 파일(쓰다 만 우리 파일)은 묻지 않고 다시 씁니다.
- 성공(실패 0)하면 엔진이 저널을 지웁니다. 실패·크래시면 남기고, C# 어댑터는 사용자가 취소한 작업의 저널을 지웁니다.
- C#: `NativeCopyEngine.ReadJournalSpec(path)` / `ResumeFromJournal(path)`.

## C ABI (v2)

ABI 버전 1 → 2: `FcProgress`가 커졌습니다 (함수 시그니처는 그대로). C#은 버전이 다르면 IFileOperation으로 폴백합니다.

| 함수 | 설명 |
| --- | --- |
| `fc_abi_version() -> u32` | 2 |
| `fc_job_create(spec_json, len, *out_handle)` | UTF-16 JSON 명세 (아래) |
| `fc_job_start / pause / resume / cancel / free(handle)` | 기존과 같음 |
| `fc_job_poll(handle, *FcProgress)` | 진행 스냅샷 (초당 10회 권장) |
| `fc_job_current_name(handle, buf, cap, *out_len)` | 마지막으로 시작한 파일의 원본 경로 (UTF-16, NUL 없음) |
| `fc_job_issues(handle, buf, cap, *out_len)` | 문제 목록 JSON `[{"id","kind":"conflict|failed","source","dest","message","resolved"}]` |
| `fc_job_resolve(handle, issue_id, action, apply_all)` | action 0 건너뛰기, 1 덮어쓰기, 2 이름 바꾸기 |
| `fc_journal_read_spec(path, len, buf, cap, *out_len)` | 저널의 명세 JSON |

문자열을 돌려주는 함수는 `out_len`에 항상 필요한 u16 개수를 쓰고, 버퍼가 작으면 `FC_ERR_BUFFER_TOO_SMALL(-6)`.
오류 코드: `-1` 인자, `-2` 핸들, `-3` panic, `-4` 상태, `-5` I/O(Done인데 -5면 일부 파일 실패), `-6` 버퍼, `-7` 해결할 항목 없음, `-8` 시험용 중단.

`FcProgress` (80바이트): `state`(0 Created 1 Running 2 Paused 3 Done 4 Failed 5 Canceled 6 NeedsAttention),
`last_error`, `files_done`(건너뜀 포함), `files_total`, `bytes_done`, `bytes_total`, `bytes_per_sec`,
`files_failed`, `files_skipped`, `issues_pending`, `threads_active`, `thread_limit`, `phase`(0 대기 1 스캔 2 계획 3 복사 4 마무리 5 끝).

### 작업 명세 JSON

```json
{
  "sources": ["C:\\원본\\폴더", "C:\\a.txt"],
  "dest_dir": "D:\\대상",
  "mode": "copy",              // copy | move
  "conflict": "ask",           // skip | overwrite | rename ("이름 (2).ext") | ask
  "verify": "size_time",       // none | size_time | hash
  "threads": 0,                // 0 = 자동
  "journal_path": "%LOCALAPPDATA%\\Foldy\\jobs\\<id>.journal",
  "resume": false,
  "tuning_path": "%LOCALAPPDATA%\\Foldy\\copy_tuning.json",
  "large_threshold": 268435456,
  "tune_interval_ms": 2000,
  "move_by_copy": false         // 진단용: 같은 볼륨 이동도 복사→검증→삭제
}
```

`ask` 충돌은 문제 목록에 쌓이고 작업은 `NeedsAttention`이 됩니다. 다른 파일은 그동안 계속 복사합니다.

## C# (`src/Foldy.Copy.Native`)

- `ICopyEngine` / `ICopyJob`: 엔진 추상화 (나중에 foldy-copy-host.exe로 격리할 자리). 기본 구현 `NativeCopyEngineService.Shared`.
- `NativeCopyJob`: `Poll()`(`NativeProgress`), `CurrentName()`, `GetIssues()`, `Resolve()`.
- `NativeCopyFileOperationService`: `IFileOperationService` 어댑터. 복사·이동은 엔진으로, 삭제·이름 바꾸기·새 폴더와
  DLL이 없을 때는 넘겨받은 폴백(IFileOperation)으로. `ResolveConflict` 콜백이 없으면 Ask 대신 Rename.

## 벤치마크 (`foldy-copy-bench`)

```powershell
engine\target\release\foldy-copy-bench.exe            # 4KB × 10,000개, 1GB × 1개, 2회씩 번갈아
engine\target\release\foldy-copy-bench.exe --threads 8 --runs 3 --json artifacts\bench-copy.json
```

데이터는 `%TEMP%\foldy-bench`에 한 번 만들고 다시 씁니다 (의사 난수 내용, 작은 파일은 100개씩 하위 폴더).
같은 원본을 Foldy와 `robocopy /MT:8 /E /NFL /NDL /NJH /NJS /NP /R:1 /W:1`로 번갈아 복사해 중앙값을 비교합니다.
Foldy 쪽은 크기·시간 검증을 켠 상태입니다 (robocopy는 검증 없음).

### 측정 결과 (2026-11-18, 같은 NVMe SSD 안 %TEMP% → %TEMP%, Windows 11, Defender 실시간 검사 켜짐)

3회씩 번갈아 실행한 중앙값입니다. 실행마다 ±1초 정도 흔들립니다 (실시간 검사와 쓰기 캐시 영향).

| 시나리오 | Foldy 자동(첫 작업, 4→6) | Foldy 고정 8 | robocopy /MT:8 | Foldy/robocopy |
| --- | --- | --- | --- | --- |
| 4KB × 10,000개 (40MB) | 9.73s | 8.80s | 9.85s / 7.85s | 0.99× (자동) / 1.12× (8) |
| 1GB × 1개 | 2.96s (346MB/s) | 2.95s | 4.97s / 4.57s | 0.60× / 0.65× |

(robocopy 칸은 각 실행 묶음의 값. 실행별 원자료는 `--json`으로 남길 수 있습니다.)

해석:

- 작은 파일은 파일마다 드는 복사 API 비용(이 환경에서 파일당 약 2.5ms, 대부분 실시간 검사)이 지배적이라 스레드 수를 늘려도
  8개 근처에서 포화합니다. 측정 잡음(±1초) 안에서 robocopy와 같은 수준입니다 (0.99×~1.12×).
  자동 모드는 첫 작업에서 4 → 6으로 올라가는 동안 시작이 조금 느립니다. 드라이브 조합별 최적값을 저장하므로 두 번째 작업부터는 그 값에서 시작합니다.
- 큰 파일은 대형 줄(COPY_FILE_NO_BUFFERING)이 캐시를 거치지 않아 robocopy(버퍼 사용)보다 빠릅니다.
- 처음 구현에서 작은 파일이 1.3배 느렸던 원인은 계획 단계에서 대상 파일마다 존재 여부를 연 것(1.5초)과
  검증에서 `std::fs::metadata`가 파일을 두 번 연 것이었습니다. 대상 폴더를 한 번씩만 열거하고 GetFileAttributesExW로
  바꿔 계획 0.17초, 복사 단계는 robocopy와 같은 수준이 됐습니다.

## 알려진 한계 (다음 단계)

- 스캐너는 FindFirstFile 기반입니다 (MFT 색인 활용은 색인 서비스 연동 때).
- 심볼릭 링크·정션은 건너뜁니다 (복사하지 않음). ADS·ACL·압축 속성은 CopyFile2 기본 동작을 따릅니다.
- 같은 드라이브를 쓰는 작업끼리의 스레드 예산 공유(JobScheduler)는 C# 쪽 작업입니다.
- 저널은 fsync하지 않아 정전 직전 몇 레코드는 잃을 수 있습니다 (그 파일은 다시 복사됨).
- CopyFile2의 SMB 압축 옵션은 아직 쓰지 않습니다.
