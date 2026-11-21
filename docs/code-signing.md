# 코드 서명 (SignPath Foundation — 2026-11-19 보류, 재신청 대비 기록)

Windows 실행 파일과 MSI는 SignPath Foundation의 오픈소스 무료 인증서로 Authenticode 서명한다.
업데이트 정보(`update.json`)의 Ed25519 서명은 이와 별개다 ([release-signing.md](release-signing.md)).

## 요건과 이 저장소의 대응

| SignPath Foundation 요건 | 대응 |
| --- | --- |
| OSI 라이선스, 독점 코드 없음 | GPL-3.0-or-later ([LICENSE](../LICENSE)) |
| 이미 배포 중, 기능 설명 | GitHub Releases, [README](../README.md) |
| 홈페이지·릴리스 페이지에 "Code signing policy" | README의 `## Code signing policy` 절. 릴리스 노트에도 링크 (아래) |
| 역할(작성자·리뷰어·승인자) | README에 David CHA (GitHub `chadolkr`) |
| 개인정보 정책, 설치 중 표시, 끄는 방법 | [PRIVACY.md](../PRIVACY.md). MSI 라이선스 화면 앞, 스크립트 설치 안내에 표시. 업데이트·오류 보고는 설정에서 끔 |
| 시스템 변경 안내 | MSI 환영 화면의 SYSTEM 색인 서비스 안내 |
| 제거 방법 | README `## 설치와 제거`, `Foldy 제거.cmd`, Windows 앱 제거 |
| 소스에서 검증 가능한 빌드, 릴리스마다 수동 승인 | GitHub Actions(release.yml)만 서명 요청. SignPath 서명 정책에서 승인자 1명 필수 |
| MFA | GitHub·SignPath 계정 모두 2단계 인증 |
| 파일 메타데이터 제한 | `.signpath/artifact-configurations/*.xml` (제품 이름 Foldy) |

## 승인 뒤 설정 (한 번)

1. SignPath에 로그인해 프로젝트(`Foldy`)를 만들고 Trusted Build System으로 GitHub.com을 연결한다.
2. Artifact Configurations에 두 개를 만든다: slug `payload` ← `.signpath/artifact-configurations/payload.xml`, slug `msi` ← `msi.xml`.
3. Signing Policy `release-signing`: 인증서는 Foundation이 준 것, 승인자 David CHA, Trusted Build System 필수.
4. API 토큰을 만든다 (CI용 사용자, 해당 정책에 submitter 권한).
5. GitHub 저장소 Settings → Secrets and variables → Actions:
   - Secret `SIGNPATH_API_TOKEN` = 4의 토큰
   - Variable `SIGNPATH_ORGANIZATION_ID` = SignPath 조직 ID
   - (다르면) Variable `SIGNPATH_PROJECT_SLUG`, `SIGNPATH_POLICY_SLUG`

둘(시크릿·조직 ID)이 다 있어야 release.yml의 서명 단계가 돈다. 없으면 건너뛰고 서명 없이 릴리스한다.

## 릴리스 때 흐름

태그 push → 빌드·테스트 → payload(exe·dll) 서명 요청 → **SignPath에서 승인** (메일 알림, 휴대폰 브라우저로 가능, 최대 1시간 대기)
→ 서명된 payload로 setup zip 재생성 → MSI 빌드 → MSI 서명 요청 → **다시 승인** → update.json 해시·Ed25519 서명 → GitHub Release.

릴리스 노트에는 "Code signing policy: https://github.com/chadolkr/Foldy#code-signing-policy"를 남긴다 (release.yml이 자동으로 붙임).

## 남은 일

- Rust 실행 파일(`foldy-index.exe`, `foldy-update.exe`, `foldy_copy.dll`)에 버전 리소스(제품 이름 Foldy)를 넣고 payload.xml에 `product-name` 제한을 추가한다.
