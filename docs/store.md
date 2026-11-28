# Microsoft Store 출시 계획

## 결정 (2026-11-21): 같은 소스에서 두 가지를 만든다

- **Store 판 (MSIX)**: 개인 사용자용. Microsoft가 서명하고 Store가 업데이트한다. 1.15.0부터 설치판과 같은 색인 서비스를 패키지 서비스로 넣는다(아래 "Store 판의 찾기"). 서비스가 없으면 Foldy가 사용자 권한으로 모든 드라이브의 목록을 직접 모은다(1.15.0).
- **설치본 (MSI·setup zip)**: 회사·오프라인 PC용 (Store 판은 설치할 때 인터넷과 Store가 필요하다). Certum 인증서로 서명, 업데이트는 지금처럼 update.json.
- 실행 파일은 같다. 앱이 패키지로 실행되는지(`AppPackage.IsPackaged`, `GetCurrentPackageFullName`)를 보고 자체 업데이트·서비스 설치를 끈다.
- release.yml이 태그마다 둘 다 만든다. MSIX는 워크플로 아티팩트 `foldy-store-msix`로 올라가고, Partner Center에 제출한다.

### 지금 상태
- 구현: `build/store/make-msix.ps1`, `AppxManifest.template.xml`, release.yml의 "Store MSIX" 단계(첫 실제 빌드로 검증될 때까지 실패해도 릴리스는 계속), 앱의 Store 판 분기.
- Store 제품 (2026-11-21 예약): 이름 **Foldy File Manager** ("Foldy"는 선점됨), Store ID `9NS1PWL5QGP6`, Identity `DavidCHA.FoldyFileManager`, Publisher `CN=2AAA454A-54B0-4C55-B5E3-99DD3024FB14`, 표시 이름 `David CHA`, PFN `DavidCHA.FoldyFileManager_2mprgqxmdw8d8`. make-msix.ps1의 기본값이다. 시작 메뉴·Store에는 "Foldy File Manager", 앱 안에서는 Foldy.
- 로컬 시험: `./build/setup/make-setup.ps1` 다음 `./build/store/make-msix.ps1 -Version x.y.z -TestCert`.

## Store 판의 찾기 (1.15.0, 사용자 2026-11-26 "꼭 있어야")

- 색인 서비스(패키지 서비스 FoldyIndexerStore)가 있으면 설치판과 같다: NTFS는 MFT로 몇 초, USN으로 바로 반영. 아래는 서비스가 없거나 꺼져 있을 때다.
- Foldy가 사용자 권한으로 모든 드라이브(C: 같은 NTFS, USB·SD, 네트워크, Google Drive)의 파일 목록을 `%LOCALAPPDATA%\Foldy\crawl`(패키지 저장소로 옮겨짐)에 만든다. 설치본의 "색인 없는 드라이브 모으기"와 같은 코드(`SlowCrawler`).
- 단축키 도우미 foldy-hotkey.exe가 패키지에 들어 있고, 매니페스트의 `desktop:StartupTask`가 로그인 때 띄운다(앱을 한 번 켠 뒤부터). Foldy가 꺼져 있어도 Ctrl+Space와 주기 모으기(`Foldy.exe --gather`)가 된다. 작업 관리자 → 시작 앱에서 끌 수 있다.
- 설치본과 다른 점: 관리자 권한이 없어 NTFS의 MFT를 읽지 못하므로 처음 목록은 폴더를 하나씩 읽어 만든다(노트북 C: 파일 200만 개 36초). 그동안 "처음 한 번만"이라고 안내한다(1.18.0). 그다음은 NTFS 변경 기록(`FSCTL_READ_UNPRIVILEGED_USN_JOURNAL`, 관리자 필요 없음)으로 바뀐 폴더만 읽고(1.18.0, C: 9초), Foldy가 켜져 있는 동안은 변경 알림으로 바로 반영한다. 켤 때마다 꺼져 있던 동안 바뀐 것을 바로 따라잡는다. 찾기는 이름만 메모리에 담은 색인(`NameIndex`, C: 69MB, 2~11ms)으로 한다(1.18.0). 처음 목록은 폴더 8개(USB·SD 4개)를 같이 읽는다(C: 36초 → 22초). 색인 서비스가 설치되지 않은 설치판도 같은 방식으로 돈다(1.18.0).
- CI의 "Store package manifest" 단계(`build/store/check-msix.ps1`)가 매니페스트를 makeappx로 미리 검사한다.

### 패키지 서비스 (1.15.0): 심사 사유 글

1.15.0부터 make-msix.ps1이 매니페스트에 `desktop6:Service`(foldy-index.exe, `FoldyIndexerStore`, localSystem, 자동, `--service --store`)와 제한 기능 `packagedServices`·`localSystemServices`를 넣는다. 사유 글은 `build/store/certification-notes.txt`이고 submit.ps1이 제출의 `notesForCertification`으로 넣는다. Partner Center가 "제출 옵션 → 제한된 기능을 사용하는 이유" 칸을 따로 요구하면 같은 글을 붙여 넣는다. 거절되면 저장소 변수 `FOLDY_STORE_SERVICE`를 `false`로 두면 서비스 없는 판(1.15.0 방식)으로 만든다. 아래는 처음 적어 둔 초안이다.

> (내기 전에 각 문장, 특히 파이프 권한이 지금 코드와 맞는지 다시 확인한다.)
>
> Foldy is a file manager whose main feature is instant file name search across all local drives. The packaged service (foldy-index.exe) runs as LocalSystem because reading the NTFS Master File Table and the USN change journal requires a volume handle, which standard users cannot open. The service only reads file names, sizes and times; it never reads file contents, never opens network connections, and only answers the signed-in user's Foldy app over a local named pipe restricted to that user. It is the same open-source service shipped in our MSI installer (source: https://github.com/chadolkr/Foldy, engine/foldy-index). Without it, Foldy has to walk every folder with the user's rights, which takes minutes on large drives and misses changes made while Foldy is closed.

## PC 없이 제출 (사용자 요청 2026-11-25)

휴대폰에서 **GitHub → Actions → Store → Run workflow**를 누르면 그 태그의 Store 패키지를 Partner Center에 올린다(`.github/workflows/store.yml`, `build/store/submit.ps1`).

- `draft`(기본): 패키지를 올리고 5개 언어의 등록 정보 글(설명, 새로운 기능 등)을 `docs/store-listing` 글로 바꾼 초안만 만든다. 인증에는 내지 않는다.
- `submit`: 위에 이어 인증에 낸다. 글을 바꾸지 못하면 내지 않는다.
- `text-draft`·`text-submit` (2026-11-25): 새 패키지 없이 Store 설명 글만 바꾼다. 태그는 비워 둔다. 마지막으로 공개된 제출을 복사해 초안을 만들고, `docs/store-listing`의 설명·새로운 기능·제품 기능·짧은 설명·검색어와 웹 사이트(언어별 소개 페이지)·지원 주소를 넣는다. 패키지를 낼 때도 같은 글이 함께 올라간다.
- Store 설명은 Partner Center에 손으로 붙여 넣지 않는다: 줄바꿈이 빠져 한 덩어리로 보였다(2026-11-25).
- 사람이 누를 때만 돌고, main에서만 시크릿을 읽는다. 열려 있던 초안은 마지막으로 공개된 제출에서 새로 만들어져 사라진다.
- 2026-11-25에 `draft`가 처음으로 됐다. `submit`은 아직 해 보지 못했다. 초안은 Partner Center에서 **보기만** 한다(거기서 고치면 API로 제출할 수 없다).

처음 한 번 준비 (값은 사용자가 직접 넣는다. 대화나 저장소에 적지 않는다):

1. Partner Center → 계정 설정 → 테넌트: Microsoft Entra 테넌트를 연결하거나 새로 만든다. (개인 개발자 계정에서 되는지 문서에 없다. 메뉴가 없으면 이 방법은 쓸 수 없다.)
2. 그 테넌트에 앱을 하나 등록하고, Partner Center → 사용자 관리 → Microsoft Entra applications에서 그 앱을 **관리자(Manager)** 역할로 더한다.
3. GitHub 저장소 → Settings → Environments → **store**를 만들고(배포 브랜치는 main만, 검토자로 자신을 넣으면 실행 전에 한 번 더 확인), 그 안의 시크릿에 `STORE_TENANT_ID`, `STORE_SELLER_ID`, `STORE_CLIENT_ID`, `STORE_CLIENT_SECRET`을 넣는다.
4. client secret은 기한이 있다. 끝나면 새로 만들어 바꾼다.
5. 값이 맞지 않으면 "Submit" 단계에 어느 시크릿의 모양이 다른지, 로그인 오류 번호(AADSTS…)와 까닭이 나온다. 값은 찍지 않는다. 이 확인이 끝나기 전에는 Partner Center를 바꾸지 않는다.

출처: [msstore 명령](https://learn.microsoft.com/en-us/windows/apps/publish/msstore-dev-cli/commands), [GitHub Actions로 올리기](https://learn.microsoft.com/en-us/windows/apps/publish/msstore-dev-cli/github-actions) (앱이 이미 공개돼 있어야 하고, 무료 앱만 된다고 적혀 있다).

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
| 색인 서비스 (SYSTEM) | **넣을 수 없음**: `localSystemServices`·`packagedServices`는 제한 기능이고 Store 문서가 "대부분 승인되지 않는다"고 함 (1.15.0부터는 사용자 권한 모으기로 대신) | 그대로 설치 (제출 화면의 "NT 서비스 사용" 칸에 표시) |
| 설치 파일 위치 | Store에 올림 | 버전별로 바뀌지 않는 HTTPS 주소 (GitHub Releases 자산 주소), 오프라인 설치, 조용한 설치 지원 필요 (MSI `/qn`) |
| 할 일 | MSIX 빌드, Store 판 분기(서비스·업데이트 끔), 기능 축소 안내 | Certum 서명을 릴리스에 연결 |

## A안(MSIX)일 때 Store 판에서 달라지는 것

앱은 이미 서비스가 없을 때(포터블 설치)의 폴백 경로를 갖고 있어, 큰 수정 없이 돈다.

- **무엇이든 찾기**: 드라이브 전체 즉시 검색 대신, 고른 폴더 아래를 직접 훑는 검색 (`FileSearchService` 폴백). 큰 드라이브에서는 느리다.
- **공간 늘리기(폴더 분석)**: 색인 DB 대신 `FallbackScanner`로 그때그때 훑는다.
- **업데이트**: Store가 한다. 앱의 업데이트 확인·버튼은 숨긴다 (`AppUpdater`·`ServiceUpdater` 끔). 패키지 폴더는 읽기 전용이라 어차피 덮어쓸 수 없다.
- **설정·로그·오류 보고**: `%LOCALAPPDATA%`는 패키지별로 가상화되지만 앱에서는 같은 경로로 보인다.
- 앱 실행은 `runFullTrust`(데스크톱 앱 일반 기능)만 필요하다.

1.15.0에서 관리자 권한 없이 도는 사용자 단위 모으기를 Store 판에 켰다 (위 "Store 판의 찾기").

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

## 출처

- [개인 개발자 무료 등록 (Windows 블로그, 2025-09)](https://blogs.windows.com/windowsdeveloper/2025/09/10/free-developer-registration-for-individual-developers-on-microsoft-store/)
- [개발자 계정 만들기](https://learn.microsoft.com/en-us/windows/apps/get-started/sign-up)
- [MSI/EXE 제출](https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/msi/create-app-submission), [Win32 앱 Store 배포](https://learn.microsoft.com/en-us/windows/apps/distribute-through-store/how-to-distribute-your-win32-app-through-microsoft-store), [배포 경로 고르기](https://learn.microsoft.com/en-us/WINDOWS/APPS/package-and-deploy/choose-distribution-path)
- [코드 서명 선택지](https://learn.microsoft.com/windows/apps/package-and-deploy/code-signing-options)
- [앱 기능 선언 (제한 기능 승인)](https://learn.microsoft.com/et-ee/windows/apps/package-and-deploy/app-capability-declarations), [desktop6:Service](https://learn.microsoft.com/en-us/uwp/schemas/appxpackage/uapmanifestschema/element-desktop6-service)
