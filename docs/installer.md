# Foldy MSI 설치 프로그램

`build/installer/` 의 WiX Toolset 6 프로젝트가 `artifacts/Foldy-<버전>-x64.msi` 를 만든다. x64 전용, per-machine(관리자 권한).

## 구성

| 항목 | 내용 |
|---|---|
| 설치 위치 | `C:\Program Files\Foldy` (설치 화면에서 변경 가능) |
| 제조사 / ARP | Manufacturer `David CHA`, 도움말·정보 링크 `https://github.com/chadolkr/Foldy`, 아이콘 `assets/icons/foldy.ico` |
| UI | WixUI_InstallDir: 환영(SYSTEM 색인 서비스 안내) → 라이선스(LICENSE를 RTF로 변환) → 설치 경로 → 설치 |
| 바로가기 | 시작 메뉴(모든 사용자) `Foldy` |
| 색인 서비스 | `FoldyIndexer`: `foldy-index.exe --service`, LocalSystem, 자동(1.17.1까지는 지연된 시작), 설명 `Foldy Indexer by David CHA`, 실패 시 60초 뒤 재시작(2회, 24시간 후 초기화). 설치 시 시작, 제거·업그레이드 시 정지 |
| 업그레이드 | MajorUpgrade(UpgradeCode 고정, `AllowDowngrades`). 같은 숫자 버전도 교체한다. 낮은 버전 설치는 `Launch` 조건으로 차단하고, 자동 업데이트의 되돌리기(`FOLDY_ROLLBACK=1`)만 허용 (docs/auto-update.md) |
| 파일 | `dist/Foldy-Setup/payload`(make-setup.ps1 산출물)를 `<Files>`로 통째로 수집. `Foldy.exe`, `foldy-index.exe`만 별도 컴포넌트 |

설정·세션(`%LOCALAPPDATA%\Foldy`)과 서비스 데이터(`%ProgramData%\Foldy`)는 제거 시 남긴다.

## 빌드

```powershell
dotnet tool install --global wix --version 6.0.2
wix extension add -g WixToolset.UI.wixext/6.0.2
wix extension add -g WixToolset.Util.wixext/6.0.2

# engine\target\release\foldy-index.exe 등이 있어야 한다 (없으면 cd engine; cargo build --release)
pwsh build/installer/build-msi.ps1                 # 페이로드 생성 + MSI
pwsh build/installer/build-msi.ps1 -SkipPayload    # dist\Foldy-Setup\payload 재사용
pwsh build/installer/build-msi.ps1 -Version 1.2.3-beta.1 -OutDir artifacts/release
```

- 버전: `-Version` 이 없으면 `Directory.Build.props` 의 `<Version>`. MSI 내부 ProductVersion은 숫자 3자리(`1.2.3`)만 쓰고, 파일명에는 접미사를 포함한다.
- WiX 7은 OSMF EULA 동의가 필요해 **6.x 로 고정**했다. 7로 올릴 때는 EULA 동의(조직 결정)가 선행되어야 한다.
- 빌드 마지막에 `wix msi validate` (ICE)를 실행한다. 억제하는 ICE43/ICE57(공용 시작 메뉴 바로가기를 사용자별 데이터로 오인)과 ICE61(같은 버전 교체 허용)은 per-machine 설치의 알려진 오탐이다.
- 릴리스: `.github/workflows/release.yml` 이 setup zip 직후 MSI를 `artifacts/release` 에 만들고, `update.json` 매니페스트와 GitHub Release에 함께 올라간다. 코드 서명(SignPath)은 아직 없다.

## 스크립트 설치본(Foldy-Setup.ps1)에서의 업그레이드

스크립트 설치본은 같은 `Program Files\Foldy`, 서비스 `FoldyIndexer`, 시작 메뉴 `Foldy.lnk`, 레지스트리 `HKLM\...\Uninstall\Foldy`(MSI의 GUID 키와 별개)를 만든다. MSI를 그 위에 설치하면:

- **파일**: 같은 경로이므로 Windows Installer 파일 버전 규칙으로 덮어쓴다(버전 있는 파일은 더 높은 버전이 우선, 같은 버전이면 기존 유지. 버전 없는 파일은 해시가 다르면 교체). 개발 중 같은 `0.x` 버전의 다른 빌드가 엇갈린 파일이 남을 수 있으면, 먼저 스크립트의 `-Uninstall` 로 제거하고 MSI를 설치하는 것이 가장 확실하다.
- **서비스**: 서비스 이름과 경로가 같다. `ServiceControl`(`Stop="both" Remove="both"`)이 먼저 정지하고 `DeleteServices`(순서 2000)에서 스크립트가 만든 서비스를 지운 뒤, `InstallServices`(5800)가 새로 만들고 자동 시작·실패 복구를 다시 지정하고 시작한다(실설치 미검증: 격리 환경에서 확인 필요).
- **ARP 중복 항목**: 설치 시 스크립트의 `Uninstall\Foldy` 키와 `Foldy-Setup.ps1` 을 제거(`RemoveRegistryKey`/`RemoveFile`)하므로 ARP에는 MSI 항목만 남는다. 이후 제거는 MSI로만 한다.
- **시작 메뉴**: 같은 경로(`Programs\Foldy.lnk`)이므로 MSI 바로가기가 대체한다.
- 실행 중인 `Foldy.exe` 가 있으면 사용 중 파일 안내가 뜬다(자동 UI) 또는 재시작 요청이 생길 수 있다.

## 검증 (설치 없이)

```powershell
msiexec /a artifacts\Foldy-0.1.0-x64.msi /qn TARGETDIR=C:\temp\foldy-admin   # 관리자 추출
wix msi validate -sice ICE43 -sice ICE57 -sice ICE61 artifacts\Foldy-0.1.0-x64.msi
```

실제 설치(`msiexec /i`)는 서비스를 등록하므로 가상 머신 등 격리된 환경에서 확인한다.

## 알려진 한계

- 설치 UI는 영어(환영 화면에 한국어 안내 병기). 로캘별 UI는 없다.
- 서비스 시작 유형은 `ServiceInstall Start="auto"`에 더해 `sc.exe config ... start= auto` 사용자 지정 동작(`SetIndexerAutoStart`)으로 한 번 더 지정한다. 1.17.1까지는 같은 자리에서 `delayed-auto`(지연된 시작)로 바꿨는데, 그러면 PC를 켜고 2~3분 동안 서비스가 없어 빠른 찾기가 "연 폴더만"으로 돌았다(DECISIONS 2026-11-28). 업데이트로 예전 서비스의 지연 표시가 남지 않게 이 동작을 남겨 둔다. MsiServiceConfig는 Microsoft 문서상 불안정해서 쓰지 않았다.
- 제거 시 `%ProgramData%\Foldy` 와 사용자 설정은 삭제하지 않는다.
