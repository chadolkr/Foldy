# SFTP Provider와 SSH 터미널 (M5)

프로젝트: `src/Foldy.Providers.Sftp` (net10.0-windows, SSH.NET 2026.0.1 / MIT), 테스트: `tests/Foldy.Sftp.Tests`.
UI 연결은 아직 없다. 이 모듈은 Provider·저장소·명령 조립만 제공한다.

## 경로

`sftp://user@host:port/path` (`SftpLocation`). 기본 포트 22는 표준 문자열에서 생략한다. 경로는 원격 POSIX 경로를 **그대로** 보관한다(퍼센트 인코딩 없음).
사용자 이름만 `%40` 같은 인코딩을 허용한다. IPv6는 `[::1]`. `-`로 시작하는 호스트는 옵션 주입 방지를 위해 거부한다.

## SftpProvider

- `new SftpProvider(factory, credentials, hostKeyHandler?)`
  - `factory`: 실제 앱은 `new SshNetSftpClientFactory(new KnownHostsFile(KnownHostsFile.DefaultPath))`, 테스트는 가짜 `ISftpClientFactory`.
  - `credentials`: `SftpLocation -> SftpAuth?` (프로필 저장소 조회 또는 UI 프롬프트). null이면 `InvalidOperationException`.
  - `hostKeyHandler`: 알 수 없거나 바뀐 호스트 키일 때 UI가 `HostKeyDecision`(Reject/AcceptOnce/AcceptAndSave)을 돌려준다. null이면 거부.
- 연결(`user@host:port`)마다 세션 1개 + 전용 `DriveWorkQueue` 1개. 큐 키는 `\\user@host:port\SFTP` 형태로 만들어 Core 수정 없이 `DriveKey` 규칙에 맞춘다.
  큐 워치독은 30초(`DefaultStallTimeout`)로 느슨하게 둔다.
- 연결/재연결은 호스트 키 UI 대기가 있을 수 있어 **큐 밖**(Task.Run)에서 하고, 큐에서는 목록·열기처럼 짧은 호출만 실행한다. 대용량 전송은 큐를 점유하지 않는다.
- `ListBatchedAsync`: 200개 또는 100ms 단위 스트리밍. `.`/`..` 제외. 심볼릭 링크는 `FsKind.Link`. 취소 지원.
  연결이 끊기면 **첫 묶음을 내보내기 전**에 한해 한 번 재연결 후 재시도한다. 이미 일부를 내보냈다면 예외를 올린다(UI가 새로고침).
- `OpenReadAsync`, `DownloadAsync(src, stream, IProgress<long>, ct)`, `UploadAsync(stream, dst, IProgress<long>, ct)`: 복사 엔진 연동용 단순 헬퍼. 진행률은 누적 바이트. 전송 중 끊김은 재시도하지 않는다.
- `ConnectAsync`로 미리 연결(호스트 키 확인 흐름 시작), `Disconnect`로 닫기.

## known_hosts

`KnownHostsFile` (`%USERPROFILE%\.ssh\known_hosts`)가 외부 `ssh.exe`와 같은 파일을 읽고 쓴다.

- 평문 항목(`host1,host2`, `[host]:port`, `*`/`?`, `!`부정)과 해시 항목(`|1|salt|hash`, HMAC-SHA1) 지원. `@revoked`는 항상 Changed, `@cert-authority`는 무시.
- 키 종류는 키 blob 안의 이름을 그대로 쓰므로 ed25519 / ecdsa-sha2-nistp256·384·521 / ssh-rsa 모두 같은 경로로 처리된다.
- `Check` 결과: Match / Unknown(해당 호스트·키 종류 항목 없음) / Changed(같은 종류의 다른 키). `Add`는 끝에 덧붙이고(기본 평문, `hashed: true` 옵션), `Remove`는 임시 파일 교체로 기록.
- `HostKeyVerifier`가 SSH.NET `HostKeyReceived`에 연결된다. Changed + AcceptAndSave는 같은 종류의 옛 항목을 지우고 새 키를 기록한다.
- 지문은 `SHA256:base64(패딩 없음)`로 ssh와 같은 형식.

## 연결 프로필

`%LOCALAPPDATA%\Foldy\connections.json` (`ConnectionProfileStore`). `{ "version": 1, "profiles": [...] }`.
비밀번호/패스프레이즈는 `SecretProtector`(DPAPI CurrentUser + 앱 고유 엔트로피, crypt32 직접 호출)로 암호화된 base64만 저장한다.
저장은 `.tmp`에 쓰고 `File.Replace`로 교체(원자적). 손상된 파일은 `.corrupt`로 옮기고 빈 목록으로 시작한다.
인증은 비밀번호(+keyboard-interactive 응답)와 키 파일(+패스프레이즈). `SftpProfile.ToAuth()`로 메모리 안의 평문 `SftpAuth`를 만든다.

## SSH 터미널 실행 명령

`SshTerminalLauncher.Build(request, wtPath)`는 순수 함수이며 `TerminalLaunch(FileName, Arguments, UseShellExecute)`를 돌려준다.

```
wt ssh -t -p 22 [-i key] user@host "cd '/path' && exec $SHELL -l"
ssh.exe -t -p 22 [-i key] user@host "cd '/path' && exec $SHELL -l"     # wt가 없을 때(새 콘솔)
exec sudo -i sh -c 'cd '\''/path'\'' && exec "$SHELL" -l'              # sudo 셸(원격 명령 부분)
```

- 원격 경로는 POSIX 작은따옴표 인용(`'` → `'\''`), 로컬 인수는 CommandLineToArgvW 규칙으로 인용한다.
- wt는 `;`를 명령 구분자로 쓰므로 인수 안의 `;`를 `\;`로 이스케이프한다.
- 사용자/호스트가 `-`로 시작하거나 공백·메타문자를 포함하면 `ArgumentException`(`-oProxyCommand=…` 주입 방지).
- 비밀번호는 명령줄에 넣지 않는다. 비밀번호 인증과 sudo 비밀번호는 터미널에서 직접 입력한다.
- `FindWindowsTerminal()`이 앱 실행 별칭 폴더 → PATH 순으로 wt.exe를 찾는다.

## 알려진 한계

- ssh-agent / Pageant 인증은 SSH.NET이 간단히 지원하지 않아 넣지 않았다(외부 ssh 터미널은 에이전트를 그대로 쓴다).
- 호스트 키 blob은 SSH.NET `HostKeyEventArgs.HostKey`(wire 형식)를 가정한다. 실서버 검증은 UI 연결 단계에서 수동 확인이 필요하다.
- 쓰기 작업(삭제/이름 바꾸기/폴더 만들기)과 `Watch`는 아직 없다.
- 내장 터미널(ShellStream)은 2단계 과제.
