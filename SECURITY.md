# Foldy 보안 / Security

**한국어** · [English](#english)

Foldy가 나쁜 일을 하지 않는다는 것을 "믿어 주세요"라고 말하는 대신, **누구나 직접 확인할 수 있는 근거**를 공개합니다.
완벽한 증명은 어떤 프로그램도 할 수 없지만, 아래 근거는 모두 Foldy를 만든 사람이 아닌 **GitHub·OpenSSF·VirusTotal 같은 제3자**가 만들어 줍니다.

## 공식 주소는 두 곳뿐입니다

| | 주소 |
| --- | --- |
| 소스 코드·내려받기 | **https://github.com/chadolkr/Foldy** (내려받기는 [Releases](https://github.com/chadolkr/Foldy/releases)에서만) |
| 소개 사이트 | **https://chadolkr.github.io/Foldy/** |
| Microsoft Store | **Foldy File Manager** (게시자 David CHA, 출시 예정) |

- 다른 곳(다른 사이트, 메일·메신저로 받은 파일, 광고, "무료 정품" 블로그)에서 받은 Foldy는 **가짜일 수 있습니다.** 지우고 위 주소에서 다시 받으세요.
- Foldy는 **돈을 받지 않습니다.** 결제, 회원 가입, "라이선스 키"를 요구하면 가짜입니다.
- Foldy 업데이트는 앱이 스스로 GitHub에서 받습니다. "Foldy 업데이트"라며 다른 파일을 받으라는 메일·창은 가짜입니다.

## 내려받은 파일이 진짜인지 확인하기

쉬운 것부터 차례로 적었습니다. 하나만 해도 됩니다.

### 1. VirusTotal에서 보기 (가장 쉬움)

[릴리스 페이지](https://github.com/chadolkr/Foldy/releases/latest) 아래쪽 "Verify this download"에 VirusTotal 링크가 있습니다. 누르면 70여 개 백신 회사가 그 설치 파일을 검사한 결과가 나옵니다.
내가 받은 파일을 [virustotal.com](https://www.virustotal.com/)에 직접 끌어 놓아도 됩니다.

> 새로 나온 서명 없는 프로그램은 백신 한두 곳이 "의심"으로 표시하는 일이 가끔 있습니다(오탐). 대부분이 깨끗하다고 하면 괜찮습니다.

### 2. 지문(SHA-256) 맞춰 보기

릴리스마다 `SHA256SUMS.txt`에 파일마다 고유한 "지문"이 있습니다. 내려받은 폴더에서 PowerShell을 열고:

```powershell
Get-FileHash .\Foldy-0.9.37-x64.msi -Algorithm SHA256
```

나온 글자가 `SHA256SUMS.txt`의 같은 파일 줄과 똑같으면, 받은 파일은 GitHub에 올라간 파일과 한 글자도 다르지 않습니다.

### 3. 빌드 출처 증명 (attestation) 확인하기

릴리스 파일은 사람 컴퓨터가 아니라 **공개된 GitHub Actions 서버**에서, 이 저장소의 공개 소스로만 만들어집니다([release.yml](.github/workflows/release.yml)).
GitHub는 그때 "이 파일은 chadolkr/Foldy의 이 커밋을 이 워크플로가 만들었다"는 **위조할 수 없는 서명된 증명**을 남깁니다. [GitHub CLI](https://cli.github.com/)로 확인합니다:

```powershell
gh attestation verify .\Foldy-0.9.37-x64.msi --repo chadolkr/Foldy
```

`Verification succeeded!`가 나오면 그 파일은 공개 소스에서 공개 빌드로 만들어진 진짜입니다. 가짜 사이트가 만든 파일은 이 확인을 통과할 수 없습니다.
모든 증명 목록: https://github.com/chadolkr/Foldy/attestations

## 업데이트에 가짜가 끼어들 수 없는 이유

- Foldy는 업데이트를 **github.com/chadolkr/Foldy의 Releases에서만** 찾습니다. 주소는 앱 안에 고정돼 있습니다.
- 업데이트 정보(`update.json`)는 **Ed25519 전자 서명**이 돼 있고, 서명 열쇠(비밀 키)는 GitHub 저장소 시크릿에만 있습니다. 앱 안에는 짝이 되는 공개 키만 들어 있어서, 서명이 맞지 않으면 설치하지 않습니다 ([docs/release-signing.md](docs/release-signing.md)).
- 내려받은 설치 파일은 서명된 `update.json`에 적힌 **크기와 SHA-256 지문**이 정확히 같아야만 설치됩니다.
- 그래서 누가 중간에서 파일을 바꿔치기하거나(회사·공공 와이파이 포함) 가짜 사이트를 만들어도, 이미 설치된 Foldy는 가짜 업데이트를 받지 않습니다.

## 누구나 볼 수 있는 근거

| 근거 | 무엇을 보여 주나요 | 어디서 보나요 |
| --- | --- | --- |
| 공개 소스 코드 (GPL-3.0) | 모든 코드가 공개돼 있어 누구나 읽고 직접 빌드할 수 있음 | [저장소](https://github.com/chadolkr/Foldy) |
| 공개 빌드 | 릴리스 파일을 사람 손 없이 공개 서버에서 만든 기록 | [Actions → Release](https://github.com/chadolkr/Foldy/actions/workflows/release.yml) |
| 빌드 출처 증명 | 파일과 소스 커밋을 잇는 GitHub의 서명된 증명 | [Attestations](https://github.com/chadolkr/Foldy/attestations) |
| SHA-256 지문 | 받은 파일이 올린 파일과 같은지 | 릴리스의 `SHA256SUMS.txt` |
| VirusTotal | 70여 개 백신의 검사 결과 | 릴리스 노트의 링크 |
| CodeQL | GitHub의 코드 보안 검사(C#·Rust·TypeScript·워크플로), PR과 매주 | [codeql.yml](.github/workflows/codeql.yml) · Security 탭 |
| 의존성 검사 | 알려진 취약점이 있는 라이브러리를 들이면 PR을 막음, 보안 업데이트는 Dependabot이 자동 PR | [dependency-review.yml](.github/workflows/dependency-review.yml) · [dependabot.yml](.github/dependabot.yml) |
| OpenSSF Scorecard | 오픈소스 보안 재단(OpenSSF)이 프로젝트 관리 방식에 매긴 점수 | [scorecard.dev](https://scorecard.dev/viewer/?uri=github.com/chadolkr/Foldy) |
| SBOM | 릴리스에 들어간 모든 라이브러리 목록(SPDX) | 릴리스의 `*.spdx.json` |
| 독립 보안 리뷰 | 0.9 출시 전 코드·보안 리뷰와 처리 결과 | [docs/reviews/2026-11-19-security-review.md](docs/reviews/2026-11-19-security-review.md) |
| 네트워크 통신 목록 | Foldy가 인터넷에 연결하는 경우 전부 | [PRIVACY.md](PRIVACY.md) |

### Foldy가 인터넷에 연결하는 경우 (요약)

1. **업데이트 확인**: GitHub(`api.github.com`, `github.com`)에서 최신 버전 정보와 설치 파일만 받습니다. 사용자 정보는 보내지 않습니다. 설정에서 끌 수 있습니다.
2. **오류 자동 보고**: 기본 꺼짐. 켠 사람만, 경로·사용자 이름·컴퓨터 이름을 지운 뒤 보냅니다.
3. **사용자가 직접 쓰는 연결**: SFTP·SSH·Git 서버(내가 적은 주소), 메모 속 인터넷 그림(내가 [불러오기]를 눌렀을 때만), 버그 신고 단추(브라우저에서 내가 직접 제출).

그 밖에는 아무 데도 연결하지 않습니다. 광고·사용 통계·추적은 없습니다. 파일 이름 색인은 내 PC에만 있습니다.
직접 확인하고 싶다면 Windows의 리소스 모니터(`resmon`) → 네트워크 탭에서 `Foldy.exe`, `foldy-index.exe`의 연결을 볼 수 있습니다.

### 아직 없는 것

- **코드 서명(Authenticode)**: 아직 없어서 Windows가 "PC 보호" 창을 띄울 수 있습니다. 개인 개발자용 인증서(Certum)를 준비하고 있고, Microsoft Store 판은 Microsoft가 서명합니다. 그 전까지는 위의 출처 증명·지문으로 확인해 주세요.

## 보안 문제를 알리는 방법

- **보안 약점(취약점)**: 공개 이슈 대신 [비공개 보안 신고](https://github.com/chadolkr/Foldy/security/advisories/new)로 알려 주세요. 3일 안에 답하고, 고친 뒤 함께 공개합니다.
- **가짜 Foldy 사이트·파일을 발견했을 때**: [이슈](https://github.com/chadolkr/Foldy/issues/new)에 주소를 남겨 주세요(그 파일은 내려받거나 실행하지 마세요). Google [피싱 신고](https://safebrowsing.google.com/safebrowsing/report_phish/)와 Microsoft [안전하지 않은 사이트 신고](https://www.microsoft.com/wdsi/support/report-unsafe-site)에도 알려 주시면 브라우저가 다른 사람을 막아 줍니다.
- 보안 수정은 항상 최신 버전에만 나옵니다. 앱의 자동 업데이트를 켜 두세요.

---

<a id="english"></a>

# Security (English)

Instead of asking you to trust us, Foldy publishes **evidence anyone can check**. No program can prove it is perfectly safe, but every item below is produced by **third parties (GitHub, OpenSSF, VirusTotal)**, not by the author.

## Official sources (only these)

- Source and downloads: **https://github.com/chadolkr/Foldy** (downloads only from [Releases](https://github.com/chadolkr/Foldy/releases))
- Website: **https://chadolkr.github.io/Foldy/**
- Microsoft Store: **Foldy File Manager** by David CHA (coming soon)

Foldy is free. Anything asking for payment, sign-up or a "license key" is fake. Foldy updates itself from GitHub; an e-mail or pop-up asking you to download a "Foldy update" is fake.

## Verify a download

1. **VirusTotal**: each release note links to the VirusTotal report of the installer (70+ antivirus engines). You can also drop your file on [virustotal.com](https://www.virustotal.com/).
2. **SHA-256**: `Get-FileHash .\Foldy-<version>-x64.msi -Algorithm SHA256` must match the line in the release's `SHA256SUMS.txt`.
3. **Build provenance**: release files are built only by public GitHub Actions from this repository ([release.yml](.github/workflows/release.yml)), and GitHub signs an attestation linking each file to its commit:
   `gh attestation verify .\Foldy-<version>-x64.msi --repo chadolkr/Foldy` → `Verification succeeded!`. A file from a fake site cannot pass this. All attestations: https://github.com/chadolkr/Foldy/attestations

## Why a fake update cannot get in

The updater only looks at the Releases of `chadolkr/Foldy` (hard-coded). `update.json` is signed with Ed25519; the private key lives only in a repository secret and the app embeds the public key. The installer must match the size and SHA-256 listed in the signed manifest. A man-in-the-middle or a fake site cannot make an installed Foldy accept an update ([docs/release-signing.md](docs/release-signing.md)).

## Evidence

Open source (GPL-3.0) · public release builds · [GitHub attestations](https://github.com/chadolkr/Foldy/attestations) · `SHA256SUMS.txt` · VirusTotal links · [CodeQL](.github/workflows/codeql.yml) (C#, Rust, TypeScript, workflows) · [dependency review](.github/workflows/dependency-review.yml) and [Dependabot](.github/dependabot.yml) · [OpenSSF Scorecard](https://scorecard.dev/viewer/?uri=github.com/chadolkr/Foldy) · SBOM (`*.spdx.json`) · [independent pre-0.9 security review](docs/reviews/2026-11-19-security-review.md) · [every network connection the app makes](PRIVACY.md).

Network, in short: update checks to GitHub (can be turned off), opt-in crash reports with personal data removed, and connections you start yourself (SFTP/SSH/Git servers you enter, remote images in a note only when you click to load them, the bug-report button in your browser). No ads, analytics or tracking.

Not yet: Authenticode code signing (a Certum certificate is planned; the Microsoft Store build is signed by Microsoft). Until then please use the checks above.

## Reporting

- **Vulnerabilities**: please use [private vulnerability reporting](https://github.com/chadolkr/Foldy/security/advisories/new), not a public issue. We reply within 3 days and publish a fix together.
- **Fake Foldy sites or files**: open an [issue](https://github.com/chadolkr/Foldy/issues/new) with the address (do not download or run it), and report it to [Google Safe Browsing](https://safebrowsing.google.com/safebrowsing/report_phish/) and [Microsoft](https://www.microsoft.com/wdsi/support/report-unsafe-site).
- Security fixes ship only in the latest version; keep automatic updates on.
