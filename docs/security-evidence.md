# 안전 근거를 만드는 장치 (관리자용)

사용자에게 보이는 설명은 [SECURITY.md](../SECURITY.md)와 소개 사이트의 "안심" 부분. 이 문서는 그 근거를 무엇이 만들고, 한 번 해 둘 설정이 무엇인지 적는다.

## 무엇이 근거를 만드나

| 근거 | 만드는 곳 | 언제 |
| --- | --- | --- |
| 빌드 출처 증명(SLSA provenance) | `release.yml` → `actions/attest-build-provenance` | 태그 릴리스마다. msi, zip, update.json(+.sig), spdx, SHA256SUMS |
| SBOM 증명 | `release.yml` → `anchore/sbom-action`(SPDX) + `actions/attest-sbom` | 태그 릴리스마다. 대상은 페이로드(.NET deps.json·dll) + engine/Cargo.lock |
| SHA256SUMS.txt | `release.yml` "SHA-256 checksums" | 태그 릴리스마다, 최종(서명된) 파일로 |
| VirusTotal 링크 | `release.yml` → `crazy-max/ghaction-virustotal` | 시크릿 `VIRUSTOTAL_API_KEY`가 있을 때만. 실패해도 릴리스는 계속 |
| 릴리스 노트의 "Verify this download" | `release.yml` "GitHub Release" | 태그 릴리스마다 |
| CodeQL | `codeql.yml` (C#, Rust, JS/TS, Actions, `security-extended`, 빌드 없음) | PR, main push, 매주 월 |
| 의존성 검사 | `dependency-review.yml` (중간 이상 취약점이면 PR 실패), `dependabot.yml` | PR, 매주·매월 |
| OpenSSF Scorecard | `scorecard.yml` (결과를 scorecard.dev에 공개) | main push, 매주 화 |

모든 워크플로의 `uses:`는 커밋 SHA로 고정하고 뒤에 `# v4`처럼 버전을 적는다(가져다 쓰는 액션이 몰래 바뀌어도 영향을 받지 않게, Scorecard "Pinned-Dependencies"). Dependabot이 SHA를 올려 준다.
권한은 워크플로 위쪽은 `contents: read`, 쓰기가 필요한 잡에만 따로 준다.

## 한 번 해 둘 설정 (저장소 주인)

1. **비공개 보안 신고 켜기**: Settings → Code security → Private vulnerability reporting → Enable. (SECURITY.md의 신고 링크가 이걸 쓴다)
2. **Dependency graph·Dependabot 켜기**: 같은 화면의 Dependency graph, Dependabot alerts, Dependabot security updates. Dependency graph가 꺼져 있으면 dependency-review.yml은 경고만 남기고 건너뛴다.
3. **CodeQL 기본 설정이 켜져 있으면 끄기**: Code scanning이 "Default setup"이면 `codeql.yml`(고급 설정)과 부딪친다. Settings → Code security → CodeQL analysis → Switch to advanced 또는 Disable.
4. **VirusTotal 키(선택)**: virustotal.com 무료 가입 → 프로필 → API key 복사 → 저장소 Settings → Secrets and variables → Actions → New repository secret `VIRUSTOTAL_API_KEY`. 무료 키는 분당 4번, 하루 500번이라 릴리스 한 번(파일 2개)에 충분하다.
5. (권장) main 브랜치 보호: PR 필수, CI 통과 필수. Scorecard "Branch-Protection"·"Code-Review" 점수가 오른다.

## 가짜 Foldy 대비

- 공식 주소는 SECURITY.md·README·사이트에 고정해 적는다: github.com/chadolkr/Foldy, chadolkr.github.io/Foldy, Store "Foldy File Manager".
- 앱의 업데이트는 `DEFAULT_REPO = chadolkr/Foldy`(engine/foldy-update/src/lib.rs)에서만, Ed25519 서명(update.json.sig)과 크기·SHA-256이 맞을 때만 설치한다. 가짜 사이트는 이미 설치된 Foldy에 끼어들 수 없다.
- 가짜를 찾으면: 이슈로 받고, Google Safe Browsing·Microsoft에 신고하고, GitHub 저장소라면 GitHub에 사칭 신고(https://support.github.com/contact/report-abuse).
- 코드 서명(Certum)이 생기면 SECURITY.md "아직 없는 것"을 지우고 "게시자: David CHA(Open Source Developer)" 확인법을 넣는다.

## 하지 않은 것

- 재현 가능한 빌드(reproducible build): .NET self-contained·WiX MSI는 같은 입력에서도 바이트가 달라지기 쉽다. 출처 증명으로 대신한다.
- 앱 안에서 attestation 검사: 업데이트는 이미 Ed25519로 검증한다. 중복이라 넣지 않는다.
