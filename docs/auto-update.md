# 자동 업데이트 (UAC 없음)

설계서 10.5의 구현입니다. 확인·다운로드·검증·설치를 모두 **SYSTEM으로 도는 색인 서비스(`FoldyIndexer`)** 가 맡고,
앱은 상태를 보여 주고 설치 시점만 알립니다. 그래서 업데이트할 때 UAC 창이 뜨지 않습니다.
서비스가 없거나(포터블·서비스 미설치) 업데이트를 모르는 이전 서비스면 앱은 예전 방식(`AppUpdater`, UAC 한 번)으로 폴백합니다.

| 위치 | 역할 |
|---|---|
| `engine/foldy-update` | GitHub 확인(ETag), update.json Ed25519 검증, 이어받기, 크기·SHA-256, `msiexec` 명령, 되돌리기 규칙 |
| `engine/foldy-index/src/update/` | 서비스 쪽 업데이트 작업: 상태 기계(`machine.rs`), 주기·설치·시작 감시(`mod.rs`), Windows API(`win.rs`), 시험용 호스트(`host.rs`) |
| `engine/foldy-proto` | 파이프 메시지 (`0x00A1`~`0x00A6`, 응답 `0x80A1`) |
| `src/Foldy.Indexer.Client` | `UpdateStatusAsync` 등 클라이언트 메서드 |
| `src/Foldy.App/Services/ServiceUpdater.cs` | ShellWindow가 쓰는 `IUpdateSource` 구현 (서비스 우선, 안 되면 `AppUpdater`) |
| `build/installer/Foldy.wxs` | MSI: Major Upgrade, 서비스 교체, 되돌리기용 다운그레이드 허용 조건 |

## 흐름

```text
서비스 시작 ─1분─▶ 확인 ─(6시간마다, 또는 CheckNow)─▶ GitHub releases/latest (If-None-Match)
   │                    └─ 새 버전 → update.json + .sig (내장 공개키로 Ed25519) → MSI 이어받기 → 크기·SHA-256 → ReadyToInstall
   │
앱 ── UpdateStatus (1분마다) ──▶ 상태바 "Foldy x.y.z · 재시작하고 업데이트"
   ├─ [재시작하고 업데이트] → InstallWhenIdle(relaunch=true) → 앱 종료
   └─ 앱 종료(진행 중 작업 없음)  → InstallWhenIdle(relaunch=false)
                                   │
서비스: Foldy.exe가 모두 끝날 때까지 대기(최대 2분, 넘으면 미룸)
      → MSI를 쓰기 금지로 열고 해시 재검사 → state.json에 pending 기록
      → msiexec /i <Updates\Foldy-x.y.z-x64.msi> /qn /norestart /l*v <Updates\install-x.y.z.log> (분리 프로세스)
      → MSI: 서비스 정지 → 이전 제품 제거 → 파일 교체 → 새 서비스 시작
새 서비스 시작: pending.version == 자기 버전이면 성공 → (relaunch면) 요청한 사용자 세션에 Foldy를 다시 띄움
      → 시작 감시: Foldy.exe가 60초 넘게 살아 있거나 AppStarted가 오면 "정상 시작" 확정
```

상태 기계 (`UpdateStatusReply.state`):

| 값 | 상태 | 뜻 |
|---|---|---|
| 0 | Idle | 할 일 없음 (최신이거나 건너뛴 버전) |
| 1 | Checking | GitHub 확인 중 |
| 2 | Downloading | MSI 받는 중 (`downloaded`/`total`, `progress_permille`) |
| 3 | ReadyToInstall | 서명·해시가 맞는 MSI가 준비됨 (`available_version`, `notes`) |
| 4 | Installing | 설치 요청을 받아 Foldy 종료를 기다리거나 msiexec 실행 중 |
| 5 | Failed | 마지막 확인·설치가 실패 (`last_error`). 준비된 MSI가 있으면 ReadyToInstall로 남는다 |

네트워크·GitHub 장애는 조용히 다음 주기에 다시 시도합니다. 준비된 MSI는 다음 확인이 실패해도 지우지 않습니다.
서비스가 다시 시작하면 ETag를 버리고 한 번 새로 확인합니다(받아 둔 MSI는 해시가 맞으면 다시 받지 않음).

## 파이프 메시지 (foldy-proto, 추가만)

| 요청 | 코드 | 본문 | 응답 |
|---|---|---|---|
| UpdateStatus | `0x00A1` | 없음 | UpdateStatusReply |
| CheckNow | `0x00A2` | 없음 | UpdateStatusReply (확인은 뒤에서) |
| InstallWhenIdle | `0x00A3` | `relaunch: bool, session_id: u32` | UpdateStatusReply, 준비 안 됨이면 Error `0xA1` |
| SkipVersion | `0x00A4` | `version: String` | UpdateStatusReply, 형식 오류 Error `0xA2` |
| SetChannel | `0x00A5` | `channel: u8` (0 stable, 1 beta) | UpdateStatusReply |
| AppStarted | `0x00A6` | `version: String` | UpdateStatusReply |

UpdateStatusReply(`0x80A1`): `state, current_version, available_version, channel, progress_permille, downloaded, total,
last_check_unix, last_error, mandatory, install_pending, notes`. 골든 프레임: `engine/foldy-proto/tests/golden/update_*.bin` 등.

- **프로토콜 버전은 올리지 않았습니다** (`PROTOCOL_VERSION = 2`). 이전 클라이언트는 새 메시지를 보내지 않으므로 그대로 동작합니다.
- 이전 서비스는 모르는 타입에 Error를 보내고 연결을 끊습니다. 그래서 `ServiceUpdater`는 검색과 **다른 연결**로
  `UpdateStatus`를 먼저 보내 보고, 오류(`ProtocolException`)면 `AppUpdater`로 폴백합니다.
- 업데이트가 꺼진 서비스(콘솔 실행)는 Error `0xA0`(Disabled)로 답합니다.

## 보안

- **메시지에 경로가 없습니다.** 설치하는 파일은 언제나 서비스가 자기 업데이트 폴더에 받아 검증한 MSI이고,
  `install_command_with`가 그 파일이 업데이트 폴더 바로 안의 `.msi`인지 다시 확인합니다(정규화 후, `\\?\` 접두어는 떼어 msiexec에 넘김).
  설치 기록 파일(`/l*v`)도 같은 폴더로 제한합니다.
- 설치 직전에 MSI를 **읽기 공유만 허용**하는 핸들로 열어 크기·SHA-256을 다시 확인하고, msiexec가 끝날 때까지 쥐고 있습니다.
  그 사이 파일을 바꿔치기할 수 없습니다.
- 업데이트 폴더 `%ProgramData%\Foldy\Updates`: 소유자 SYSTEM, 상속을 끊은 DACL
  `O:SYD:PAI(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;0x1200a9;;;BU)` (SYSTEM·Administrators 모두, Users 읽기·실행).
  서비스가 시작할 때마다 다시 적용합니다. 폴더나 부모(`%ProgramData%\Foldy`)가 링크(정션)면 링크를 지우고,
  일반 사용자가 미리 만든 Updates 폴더면 내용을 비운 뒤 씁니다(미리 심은 MSI·state.json 차단). 부모의 소유자가 일반 사용자면 부모도 같은 ACL로 바로잡습니다.
- 되돌리기용 이전 MSI도 크기·SHA-256(설치할 때 update.json에서 검증한 값, `state.json`의 `rollback_asset`)을 다시 확인한 뒤에만 설치합니다.
- 다시 띄우기: `WTSQueryUserToken`으로 사용자 토큰을 받아 `CreateProcessAsUserW`(winsta0\default). 세션은 **파이프 연결의 실제 세션**
  (`GetNamedPipeClientSessionId`)을 쓰고, 요청의 `session_id`는 그 값을 알 수 없을 때 활성 콘솔 세션과 같을 때만 씁니다.
  실행 파일은 서비스 폴더의 `Foldy.exe`로 고정입니다. 사용자의 일반(제한) 토큰이라 Foldy는 관리자 권한으로 뜨지 않습니다.
- 파이프는 기존대로 로그온(Interactive) 사용자만 연결합니다. 그 사용자는 확인·건너뛰기·채널 변경·"작업이 없을 때 설치"를 요청할 수 있지만,
  설치되는 것은 서명 키로 서명된 Foldy MSI뿐입니다.

## 설치 방식 (msiexec와 서비스 교체)

- 명령: `%SystemRoot%\System32\msiexec.exe /i <MSI> /qn /norestart /l*v <기록>` (되돌리기는 `FOLDY_ROLLBACK=1` 추가).
- `DETACHED_PROCESS | CREATE_NEW_PROCESS_GROUP | CREATE_BREAKAWAY_FROM_JOB`으로 띄웁니다. 서비스는 작업 개체에 들어 있지 않고,
  들어 있더라도 분리를 시도하므로 MSI가 서비스를 멈춰도 msiexec는 계속 돕니다. stdio는 모두 null.
- 서비스는 msiexec를 기다리다가 MSI의 `StopServices`로 정지됩니다. 정지할 때 런타임을 2초만 기다리고 끝냅니다(`shutdown_timeout`).
- 결과 판단은 **새 서비스**가 합니다: `state.json`의 `pending.version`이 자기 버전(설치 폴더 `Foldy.exe`의 ProductVersion)과 같으면 성공,
  다르면 실패(설치 기록을 되돌리고, 같은 버전이 두 번 연속 실패하면 그 버전을 건너뜀). 2시간보다 오래된 기록이면 다시 띄우지 않습니다.
- msiexec가 서비스를 멈추지 않고 끝나면(예: 1618 다른 설치 진행 중, 1603 실패) 그 자리에서 종료 코드로 판단합니다. 0·3010·1641은 성공.

## 되돌리기 (설계서 10.5 실패 대비)

- 업데이트 폴더에는 새 MSI와 지금 설치된 버전의 MSI(다음 업데이트의 "직전 MSI")만 남기고 나머지 `.msi`/`.zip`/`.part`는 지웁니다.
- 새 버전이 설치된 뒤 아직 확정되지 않았으면 서비스가 2초마다 `Foldy.exe` 프로세스를 봅니다. 60초 안에 끝나고 그 전에 AppStarted가 없었으면 "시작 실패".
  앱은 창이 뜨면 바로 AppStarted(자기 버전)를 보냅니다.
- 시작 실패가 **두 번 연속**이면 `foldy-update`의 `record_start` 규칙대로 실패 버전을 건너뛸 버전으로 기록하고 직전 MSI를
  `FOLDY_ROLLBACK=1`로 설치한 뒤 Foldy를 콘솔 세션에 다시 띄웁니다.
- MSI는 `MajorUpgrade AllowDowngrades="yes"`라 높은 버전을 지우고 낮은 버전을 설치할 수 있고, 평소의 다운그레이드는
  `Launch` 조건(`Installed OR NOT FOLDY_NEWER_FOUND OR FOLDY_ROLLBACK = "1"`)이 막습니다. 이 조건은 이번 변경으로 처음 들어가므로,
  되돌리기는 **이 MSI 이후 버전끼리**만 가능합니다. 처음 자동 업데이트(스크립트 설치본·이전 MSI에서)는 직전 MSI가 없어 되돌리지 않습니다(경고만).

## 스크립트 설치본 위에 MSI 설치

docs/installer.md "스크립트 설치본에서의 업그레이드" 참고. 이번에 `ServiceControl Remove="both"`로 바꿔, 스크립트가 만든 같은 이름의
`FoldyIndexer` 서비스를 `DeleteServices`(설치 순서 2000)가 지운 뒤 `InstallServices`(5800)가 새로 만듭니다.
스크립트 설치본의 서비스가 자동 업데이트를 모르는 버전이면, 첫 MSI 설치는 예전 방식(`AppUpdater`, UAC 한 번)으로 진행됩니다.

## 시험

- `cargo test -p foldy-index update::` — 상태 기계, 시작 감시, 세션 선택, 경로 주입 차단(메시지에 경로를 덧붙여도 무시, 폴더 밖 경로 거부,
  바뀐 파일 해시 차단), 잠금 중 쓰기 금지, 재시작 후 성공/실패 판단, 두 번 실패 → 되돌리기, 검증 안 된 이전 MSI 거부, 서명 불일치.
  로컬 HTTP 서버와 `FakeHost`(명령을 기록만 함)를 쓰며 실제 msiexec는 실행하지 않습니다.
- `cargo test -p foldy-proto` — 골든 프레임 (`FOLDY_UPDATE_GOLDEN=1`로 다시 생성).
- `tests/Foldy.Interop.Tests/UpdatePipeTests.cs` — 골든 프레임 C# 인코더/디코더, 실제 `foldy-index.exe` 콘솔 프로세스 + 가짜 릴리스 서버:
  확인 → 다운로드 → 서명·해시 검증 → InstallWhenIdle → `--update-fake-install` 기록에 msiexec 명령과 다시 띄우기가 남는지.

콘솔 시험 인자 (서비스 모드는 읽지 않음):

```powershell
foldy-index --pipe Foldy.Test --no-index `
  --update-api http://127.0.0.1:8080 --update-dir C:\temp\upd --update-pubkey test-signing.pub `
  --update-current 1.0.0 --update-install-dir C:\temp\app --update-fake-install C:\temp\fake.log `
  [--update-first-delay-ms 60000] [--update-idle-timeout-ms 120000] [--update-start-window-ms 60000] [--update-poll-ms 500]
```

`--update-fake-install`이 없으면 콘솔에서도 실제 msiexec를 실행하므로 격리된 환경에서만 쓰세요. 콘솔 모드는 폴더 ACL을 건드리지 않습니다.

## 실제 장비에서 확인할 것

이 저장소의 시험은 설치·서비스 등록을 하지 않습니다. 가상 머신에서 다음을 확인해야 합니다.

1. 스크립트 설치본 위에 MSI 설치 → `FoldyIndexer`가 지워졌다 다시 등록되는지, ARP 항목이 하나인지.
2. SYSTEM 서비스에서 WinHTTP 확인·다운로드 (SYSTEM 계정의 프록시 설정은 사용자와 다르다: `netsh winhttp show proxy`).
3. `msiexec /qn`이 서비스에 의해 정지된 뒤에도 끝까지 진행되는지, 새 서비스가 pending을 보고 Foldy를 같은 세션에 띄우는지(`updater.log`, `install-x.y.z.log`).
4. 읽기 공유 핸들을 쥔 상태에서 msiexec가 MSI를 열 수 있는지.
5. 두 번 연속 시작 실패 → `FOLDY_ROLLBACK=1` 다운그레이드가 되는지, 평소 낮은 버전 MSI 설치는 막히는지.
6. 빠른 사용자 전환·원격 데스크톱 세션에서 다시 띄우기 세션이 맞는지.

기록: `%ProgramData%\Foldy\Updates\updater.log`(서비스), `install-<버전>.log`/`rollback-<버전>.log`(msiexec), `state.json`(상태).
