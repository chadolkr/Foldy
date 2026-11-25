# Microsoft Store 출시 계획

## 결정 (2026-11-21): 같은 소스에서 두 가지를 만든다

- **Store 판 (MSIX)**: 개인 사용자용. Microsoft가 서명하고 Store가 업데이트한다. 색인 서비스 없이 폴백 검색.
- **설치본 (MSI·setup zip)**: 회사·오프라인 PC용 (Store 판은 설치할 때 인터넷과 Store가 필요하다). Certum 인증서로 서명, 업데이트는 지금처럼 update.json.
- 실행 파일은 같다. 앱이 패키지로 실행되는지(`AppPackage.IsPackaged`, `GetCurrentPackageFullName`)를 보고 자체 업데이트·서비스 설치를 끈다.
- release.yml이 태그마다 둘 다 만든다. MSIX는 워크플로 아티팩트 `foldy-store-msix`로 올라가고, Partner Center에 제출한다.

### 지금 상태
- 구현: `build/store/make-msix.ps1`, `AppxManifest.template.xml`, release.yml의 "Store MSIX" 단계(첫 실제 빌드로 검증될 때까지 실패해도 릴리스는 계속), 앱의 Store 판 분기.
- Store 제품 (2026-11-21 예약): 이름 **Foldy File Manager** ("Foldy"는 선점됨), Store ID `9NS1PWL5QGP6`, Identity `DavidCHA.FoldyFileManager`, Publisher `CN=2AAA454A-54B0-4C55-B5E3-99DD3024FB14`, 표시 이름 `David CHA`, PFN `DavidCHA.FoldyFileManager_2mprgqxmdw8d8`. make-msix.ps1의 기본값이다. 시작 메뉴·Store에는 "Foldy File Manager", 앱 안에서는 Foldy.
- 로컬 시험: `./build/setup/make-setup.ps1` 다음 `./build/store/make-msix.ps1 -Version x.y.z -TestCert`.

## Certum 서명 (설치본)

1. shop.certum.eu → **Open Source Code Signing in the Cloud** (€49) 구매.
2. My account → Data security products → 활성화: 신분증 이름 그대로 입력, 자동 본인 확인(메일 링크 + 신분증·카메라) 또는 신분증 스캔, 주소 입력, 프로젝트 주소(GitHub). 본인 명의 공과금 고지서가 필요할 수 있다.
3. 휴대폰에 SimplySign 앱 설치, 클라우드에 키 생성, SimplySign 계정 연결 후 발급 요청.
4. 발급되면 노트북에 SimplySign Desktop을 설치해 로그인 확인.
5. 그다음 CI 연결(우리 작업): Windows 러너에 SimplySign Desktop을 설치하고 휴대폰 앱의 일회용 코드 원천(TOTP 시드)을 GitHub 시크릿으로 둬 자동 로그인 → `signtool`로 payload와 MSI 서명. 공식 지원 방식이 아니어서 막히면 노트북에서 서명하는 스크립트로 대신한다. 서명 비밀은 사용자가 직접 시크릿에 넣는다(대화나 저장소에 남기지 않음).


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
- 공통: Store 소개 글·스크린샷·제출 칸별 값은 [store-listing/](store-listing/README.md), 개인정보 처리방침은 https://chadolkr.github.io/Foldy/privacy/.

## PC 없이 제출 (휴대폰만으로, 2026-11-25)

사용자 "모바일로 모든걸 해야하는 환경이야". `.github/workflows/store.yml`이 릴리스 실행의 `foldy-store-msix`를 받아 `build/store/submit-store.ps1`(Microsoft Store 제출 API)로 제출한다. Partner Center에서 하던 일과 같다: 새 제출 만들기(지난번 등록 정보·가격·시장 복사) → 예전 패키지 지우고 새 패키지 → `build/store/whats-new.json`으로 5개 언어 "이 버전의 새로운 기능" → 인증 제출. 진행 중인 제출이 이미 있으면 아무것도 바꾸지 않고 멈춘다.

처음 한 번 (휴대폰 브라우저를 "데스크톱 사이트"로):
1. Partner Center → 톱니바퀴 → 계정 설정 → **테넌트**: Microsoft Entra(옛 Azure AD)를 연결하거나 새로 만든다. (개인 개발자 계정에서 되는지는 문서에 없어 화면에서 확인.)
2. 계정 설정 → **사용자 관리** → **Microsoft Entra 애플리케이션** → 새로 만들기. 이름 예: Foldy GitHub, 역할 **관리자(Manager)**.
3. 만든 앱 화면의 **테넌트 ID**, **클라이언트 ID**를 복사하고, **새 키 추가**로 비밀 키를 만든다(한 번만 보인다).
4. GitHub 저장소 → Settings → Secrets and variables → Actions → New repository secret: `STORE_TENANT_ID`, `STORE_CLIENT_ID`, `STORE_CLIENT_SECRET`. 이 값은 다른 곳(채팅·메모·저장소)에 적지 않는다.

그다음부터: GitHub → Actions → **Store** → Run workflow → 버전(비우면 가장 최근 릴리스) → Run. 저장소 변수 `STORE_AUTO_SUBMIT`을 `true`로 두면 릴리스가 끝날 때마다 저절로 제출한다(처음 몇 번은 손으로 눌러 본 뒤).

"이 버전의 새로운 기능"은 `build/store/whats-new.json`(ko·en·es·ja·zh-cn). 기능 변경판(둘째 자리)에서 바꾸고, Store 판에 없는 기능(빠른 찾기 도우미·단축키 도우미에 기대는 것)은 넣지 않는다.

## 출처

- [Microsoft Store 제출 API](https://learn.microsoft.com/en-us/windows/uwp/monetize/create-and-manage-submissions-using-windows-store-services), [앱 제출 관리](https://learn.microsoft.com/en-us/windows/uwp/monetize/manage-app-submissions)
- [개인 개발자 무료 등록 (Windows 블로그, 2025-09)](https://blogs.windows.com/windowsdeveloper/2025/09/10/free-developer-registration-for-individual-developers-on-microsoft-store/)
- [개발자 계정 만들기](https://learn.microsoft.com/en-us/windows/apps/get-started/sign-up)
- [MSI/EXE 제출](https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/msi/create-app-submission), [Win32 앱 Store 배포](https://learn.microsoft.com/en-us/windows/apps/distribute-through-store/how-to-distribute-your-win32-app-through-microsoft-store), [배포 경로 고르기](https://learn.microsoft.com/en-us/WINDOWS/APPS/package-and-deploy/choose-distribution-path)
- [코드 서명 선택지](https://learn.microsoft.com/windows/apps/package-and-deploy/code-signing-options)
- [앱 기능 선언 (제한 기능 승인)](https://learn.microsoft.com/et-ee/windows/apps/package-and-deploy/app-capability-declarations), [desktop6:Service](https://learn.microsoft.com/en-us/uwp/schemas/appxpackage/uapmanifestschema/element-desktop6-service)
