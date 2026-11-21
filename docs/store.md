# Microsoft Store 출시 계획

조사일 2026-11-21. 무료 오픈소스 서명(SignPath Foundation)이 보류된 뒤, 설치 경고 없이 배포할 길로 Store를 준비한다.

## Store에 올리는 두 방법

| | A. MSIX 패키지 | B. MSI 그대로 (Win32 제출) |
|---|---|---|
| 서명 | Microsoft가 무료로 서명 | 우리가 신뢰할 수 있는 CA 인증서로 MSI와 모든 exe·dll을 서명해야 함 (자체 서명 불가) |
| 추가 비용 | 없음 | 인증서 (Certum 오픈소스 클라우드 €49/년) |
| 업데이트 | Store가 함 (자체 업데이트는 끔) | 앱이 직접 (지금의 update.json 방식 그대로) |
| 색인 서비스 (SYSTEM) | **넣을 수 없음**: `localSystemServices`·`packagedServices`는 제한 기능이고 Store 문서가 "대부분 승인되지 않는다"고 함 | 그대로 설치 (제출 화면의 "NT 서비스 사용" 칸에 표시) |
| 설치 파일 위치 | Store에 올림 | 버전별로 바뀌지 않는 HTTPS 주소 (GitHub Releases 자산 주소), 오프라인 설치, 조용한 설치 지원 필요 (MSI `/qn`) |
| 할 일 | MSIX 빌드, Store 판 분기(서비스·업데이트 끔), 기능 축소 안내 | Certum 서명을 릴리스에 연결 |

## A안(MSIX)일 때 Store 판에서 달라지는 것

앱은 이미 서비스가 없을 때(포터블 설치)의 폴백 경로를 갖고 있어, 큰 수정 없이 돈다.

- **무엇이든 찾기**: 드라이브 전체 즉시 검색 대신, 고른 폴더 아래를 직접 훑는 검색 (`FileSearchService` 폴백). 큰 드라이브에서는 느리다.
- **공간 늘리기(폴더 분석)**: 색인 DB 대신 `FallbackScanner`로 그때그때 훑는다.
- **업데이트**: Store가 한다. 앱의 업데이트 확인·버튼은 숨긴다 (`AppUpdater`·`ServiceUpdater` 끔). 패키지 폴더는 읽기 전용이라 어차피 덮어쓸 수 없다.
- **설정·로그·오류 보고**: `%LOCALAPPDATA%`는 패키지별로 가상화되지만 앱에서는 같은 경로로 보인다.
- 앱 실행은 `runFullTrust`(데스크톱 앱 일반 기능)만 필요하다.

나중에 관리자 권한 없이 도는 사용자 단위 색인을 만들면 A안의 검색도 빨라진다 (별도 작업).

## 사용자가 직접 할 일 (두 안 공통)

1. https://storedeveloper.microsoft.com 에서 **개인 개발자(Individual)** 로 가입. 무료, 신용카드 필요 없음, 개인 Microsoft 계정으로 로그인.
2. 정부 발행 신분증(여권 또는 주민등록증)과 셀카로 본인 확인 (휴대폰으로 찍음).
3. 프로필 입력 후 Partner Center에서 **앱 이름 "Foldy" 예약**.
4. (B안만) Certum 인증서 구매·본인 확인.

주의: 개인 계정은 나중에 회사 계정으로 바꿀 수 없다. 공개되는 게시자 이름은 개인 이름(David CHA)이다.

## 자동화할 것

- A안: `build/store/` MSIX 빌드 스크립트 (payload에서 서비스·업데이트 도구 제외, `AppxManifest.xml`, 아이콘 크기별 생성, `makeappx`), 빌드 설정 `FOLDY_STORE`로 Store 판 분기, release.yml에서 `.msixupload` 만들기. 제출은 처음엔 Partner Center에서 손으로, 익숙해지면 Microsoft Store 제출 API/`msstore` CLI로.
- B안: release.yml의 서명 단계를 Certum(SimplySign)용으로 바꾸고, Store 제출에는 GitHub Releases의 MSI 주소를 넣는다.
- 공통: Store 소개 글·스크린샷(이미 있는 문서 스크린샷 재사용), 개인정보 정책 주소는 PRIVACY.md.

## 출처

- [개인 개발자 무료 등록 (Windows 블로그, 2025-09)](https://blogs.windows.com/windowsdeveloper/2025/09/10/free-developer-registration-for-individual-developers-on-microsoft-store/)
- [개발자 계정 만들기](https://learn.microsoft.com/en-us/windows/apps/get-started/sign-up)
- [MSI/EXE 제출](https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/msi/create-app-submission), [Win32 앱 Store 배포](https://learn.microsoft.com/en-us/windows/apps/distribute-through-store/how-to-distribute-your-win32-app-through-microsoft-store), [배포 경로 고르기](https://learn.microsoft.com/en-us/WINDOWS/APPS/package-and-deploy/choose-distribution-path)
- [코드 서명 선택지](https://learn.microsoft.com/windows/apps/package-and-deploy/code-signing-options)
- [앱 기능 선언 (제한 기능 승인)](https://learn.microsoft.com/et-ee/windows/apps/package-and-deploy/app-capability-declarations), [desktop6:Service](https://learn.microsoft.com/en-us/uwp/schemas/appxpackage/uapmanifestschema/element-desktop6-service)
