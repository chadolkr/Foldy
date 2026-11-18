# 릴리스 서명과 자동 업데이트

설계서 10.5(GitHub Releases 자동 업데이트), 10.6(코드 서명)의 운영 절차입니다.

## 구조

| 자산 | 내용 |
|---|---|
| `Foldy-<v>-x64-portable.zip` | `Foldy.App` publish 결과 + `foldy_copy.dll` + `foldy-index.exe` |
| `Foldy-<v>-x64.msi` | WiX MSI (docs/installer.md). 색인 서비스가 UAC 없이 자동 설치한다 (docs/auto-update.md) |
| `update.json` | 버전, 채널(stable/beta), 자산별 이름·크기·SHA-256, 최소 지원 버전, 필수 여부 |
| `update.json.sig` | `update.json` **바이트 그대로**에 대한 Ed25519 서명 (16진수 128자) |

앱(foldy-index 서비스의 `engine/foldy-update`)은 `update.json.sig`를 **앱에 내장된 공개키**
(`engine/foldy-update/keys/update-signing.pub`)로 검사한 뒤에만 `update.json`을 읽고, 내려받은
파일의 크기·SHA-256이 맞아야 "설치 준비됨"이 됩니다. GitHub 계정이 탈취되어도 서명 키 없이는 설치되지 않습니다.
공개키 파일이 비어 있으면 앱은 어떤 업데이트도 설치하지 않습니다(fail closed).

Authenticode(WinVerifyTrust) 확인은 SignPath Foundation 인증서를 받기 전까지 꺼져 있습니다
(`install::AUTHENTICODE_POLICY = Disabled`). 켜면 구현 전까지는 항상 실패하도록 막아 두었습니다.

## 1. 키 만들기 (한 번만)

저장소 **밖**의 폴더에서 실행합니다.

```powershell
cd engine
cargo run --release -p foldy-update -- keygen --out-dir $env:USERPROFILE\foldy-keys
```

- `foldy-update.signing.key` — 비밀키(Ed25519 시드, 16진수 64자). **절대 커밋하지 않습니다.**
  `*.signing.key`는 `.gitignore`에 들어 있지만, 애초에 저장소 안에 만들지 마세요.
- `foldy-update.signing.pub` — 공개키.

## 2. 비밀키는 GitHub Actions 시크릿에만

1. GitHub 저장소 → Settings → Secrets and variables → Actions → New repository secret
2. 이름 `FOLDY_UPDATE_SIGNING_KEY`, 값은 `foldy-update.signing.key`의 16진수 줄(주석 줄은 있어도 됨).
3. 로컬 파일은 암호화된 오프라인 보관소(비밀번호 관리자 등)로 옮기고 PC에서는 지웁니다.
4. 가능하면 시크릿을 `release` Environment에 두고 보호 규칙(수동 승인)을 겁니다.

## 3. 공개키를 앱에 내장

`foldy-update.signing.pub`의 16진수 줄을 `engine/foldy-update/keys/update-signing.pub`에 붙여 넣고 커밋합니다.
이 파일은 `include_str!`로 바이너리에 들어갑니다. 릴리스 워크플로는 서명 후 내장 공개키로 다시 검증하므로,
시크릿과 공개키가 짝이 아니면 릴리스가 만들어지지 않습니다.

## 4. 릴리스

```powershell
git tag v1.2.3      # 베타는 v1.3.0-beta.1 (GitHub pre-release로 올라가고 beta 채널만 받음)
git push origin v1.2.3
```

`.github/workflows/release.yml`이 `./build.ps1`(빌드+테스트) → 포터블 zip → `foldy-update manifest` →
`foldy-update sign`(시크릿 사용) → `foldy-update verify`(내장 공개키) → `gh release create` 순으로 진행합니다.
Release 본문(변경 내역)은 앱의 "새 버전" 창에 그대로 표시됩니다.

### 수동 서명 (비상시)

```powershell
foldy-update manifest --version 1.2.3 --channel stable --min-supported 1.0.0 --out update.json Foldy-1.2.3-x64-portable.zip
$env:FOLDY_UPDATE_SIGNING_KEY = '<16진수>'   # 또는 --key <파일>
foldy-update sign update.json                # → update.json.sig
foldy-update verify update.json              # 내장 공개키로 확인 (--pubkey <파일>로 다른 키)
foldy-update check --repo chadolkr/Foldy [--beta] [--current 1.0.0]
```

`--mandatory`를 주거나 설치 버전이 `--min-supported`보다 낮으면 사용자가 건너뛴 버전이어도 설치됩니다.

## 키 교체·유출 시

1. 새 키를 만들고 새 공개키를 커밋한 버전을 **이전 키로 서명해** 릴리스합니다(기존 설치본이 받을 수 있도록).
2. 그 다음 릴리스부터 시크릿을 새 비밀키로 바꿉니다.
3. 유출이 의심되면 시크릿을 즉시 지우고 릴리스를 멈춘 뒤 위 순서를 따릅니다. 기존 설치본은 공개키가 바뀐
   버전을 설치하기 전까지 유출된 키를 믿으므로, 비밀키는 시크릿 외의 곳에 두지 않는 것이 가장 중요합니다.

## 네트워크와 프록시

업데이트 확인·다운로드는 **WinHTTP**로 하며 프록시 설정은 Windows에서 가져옵니다.

- Windows 8.1+: `WINHTTP_ACCESS_TYPE_AUTOMATIC_PROXY` — 시스템 프록시, PAC 스크립트, WPAD 자동 검색, 우회 목록을 그대로 적용.
  그보다 오래된 OS에서는 `WinHttpGetIEProxyConfigForCurrentUser` + `WinHttpGetProxyForUrl`로 요청마다 정합니다.
- 인증 프록시: 로그온 사용자의 기본 자격 증명(NTLM/Negotiate)을 자동으로 씁니다
  (`WINHTTP_AUTOLOGON_SECURITY_LEVEL_LOW`, 407이면 `WinHttpSetCredentials`로 기본 자격 증명 재시도). 암호를 따로 저장하지 않습니다.
- TLS는 SChannel(Windows 인증서 저장소)을 씁니다. 회사 TLS 검사 프록시의 루트 인증서가 Windows에 설치돼 있으면 그대로 동작합니다.
- foldy-index 서비스는 SYSTEM 계정으로 돌기 때문에 "현재 사용자" 설정은 SYSTEM의 것입니다. 회사망에서 SYSTEM에 프록시가
  필요하면 `netsh winhttp set proxy <proxy:port> "<bypass>"`나 그룹 정책(WPAD/PAC)으로 설정하세요.
- 확인 주기는 실행 1분 후 + 6시간마다, ETag로 304를 받아 GitHub 무인증 호출 한도를 넘지 않습니다.
  네트워크·GitHub 장애는 조용히 다음 주기에 재시도하고, 설정 화면의 "마지막 확인" 시간으로만 드러납니다.
- UI(C#)는 HTTP를 직접 쓰지 않고 서비스 상태만 봅니다. 나중에 C#에서 HTTP를 쓰게 되면
  `HttpClientHandler.DefaultProxyCredentials = CredentialCache.DefaultCredentials`를 설정하세요.
