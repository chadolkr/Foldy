# 버그 신고와 크래시 자동 보고

두 가지 경로가 있다.

| | 버그 신고 버튼 | 크래시 자동 보고 |
|---|---|---|
| 언제 | 사용자가 "버그 신고…"를 누를 때 (정보 창) | 처리되지 않은 예외가 났을 때 |
| 어떻게 | 앱 버전·Windows 버전·최근 로그(스크럽됨)를 채운 GitHub 새 이슈 페이지를 브라우저로 연다 | 디스크에 저장 → 다음 실행 때 동의를 묻고 → 크래시 릴레이(Cloudflare Worker)로 전송 |
| 토큰 | 필요 없음 (사용자 계정으로 제출) | Worker secret에만 있음. 앱에는 없음 |
| 중복 | 사용자가 판단 | 릴레이가 서명(예외 타입 + 상위 Foldy 프레임)으로 묶어 기존 이슈에 댓글 |

## 앱 쪽 (src/Foldy.Reporting)

UI 의존성이 없는 작은 라이브러리다. 앱은 `App.xaml.cs`의 몇 줄과 `Diagnostics/CrashConsentPrompt.cs`로만 연결한다.

- `CrashReporting.Initialize(DataDir, assembly)` — 시작 시
- `CrashReporting.Record(ex, fatal)` — 전역 예외 처리기 3곳 (UI 스레드, 관찰되지 않은 Task, AppDomain)
- `CrashConsentPrompt.ScheduleOnIdle(mainWindow)` — 창이 뜬 뒤 대기 중 리포트가 있으면 한 번 묻는다. 거절하면 지운다
- `App.OpenBugReport()` — 버그 신고 버튼·메뉴에서 호출 (정보 창에 연결됨)

저장 위치: `%LOCALAPPDATA%\Foldy\crashes\crash-*.json`. 같은 예외는 하나만, 최대 20건.

### 스크럽 (ReportScrubber)

공개 이슈로 나가므로 보내기 전에 지운다. 릴레이도 같은 규칙으로 한 번 더 지운다.

- 경로 → `<path>\파일이름.확장자` (폴더 이름은 버림). 따옴표로 감싼 경로는 공백이 있어도 통째로
- 사용자 이름, `도메인\사용자` → `<user>`, 컴퓨터 이름 → `<machine>`
- 메일 주소 → `<email>`
- 스택 프레임은 줄 번호 없이 `Namespace.Type.Method`

### 프록시

`CrashSender`는 `SocketsHttpHandler { Proxy = HttpClient.DefaultProxy, DefaultProxyCredentials = CredentialCache.DefaultCredentials }`를 쓴다.
Windows에서 `HttpClient.DefaultProxy`는 인터넷 옵션(WinINet)의 수동 프록시, 자동 구성 스크립트(PAC), 자동 검색(WPAD), 예외 목록을 따른다.
인증 프록시(NTLM/Kerberos)는 로그인한 사용자 자격 증명으로 통과한다. 407이면 리포트를 남겨 두고 다음 실행 때 다시 묻는다.
버그 신고 버튼은 브라우저가 열므로 브라우저의 프록시 설정을 그대로 쓴다.

### 릴레이 주소

`src/Foldy.Reporting/Foldy.Reporting.csproj`의 `FoldyCrashRelayUrl`(빌드 때 `-p:FoldyCrashRelayUrl=...`로도 지정).
비어 있으면 크래시는 로컬에만 저장되고 동의 창도 뜨지 않는다.

## 릴레이 (tools/crash-relay)

Cloudflare Worker (TypeScript). `POST /v1/crash`, `GET /health`.

1. IP당 1분 5건 (Workers Rate Limiting), 본문 64KB, 스키마 v1 검증
2. 2차 스크럽, 서명 계산 (SHA-256 앞 12자리)
3. KV `sig:<서명>` → 이슈 번호. 없으면 제목의 `[crash:<서명>]`으로 검색
4. 열린 이슈 있음 → 댓글 (발생 횟수). 20회를 넘으면 50회마다만 댓글
5. 닫힌 이슈 → 재발 이슈를 새로 만들고 이전 이슈를 링크
6. 없음 → 새 이슈 (라벨 `crash`, `auto-report`). 하루 새 이슈 20건 상한

### 배포

```powershell
cd tools/crash-relay
npm ci
npx wrangler login                                   # 브라우저에서 Cloudflare 로그인
npx wrangler kv namespace create CRASH_KV            # 나온 id를 wrangler.jsonc에 넣는다
npx wrangler secret put GITHUB_TOKEN                 # 아래 토큰을 붙여 넣는다 (화면에 표시되지 않음)
npx wrangler deploy
```

GitHub 토큰: Settings → Developer settings → Fine-grained personal access tokens →
Repository access "Only select repositories" = `chadolkr/Foldy`, Permissions → Issues: Read and write. 다른 권한은 주지 않는다.

배포 주소: `https://foldy-crash-relay.foldy-crash-relay.workers.dev/v1/crash` (`FoldyCrashRelayUrl`에 들어 있음).

### 테스트

```powershell
npm test          # vitest: 스크럽, 서명, 검증, 이슈/댓글/재발/상한
npm run typecheck
```
