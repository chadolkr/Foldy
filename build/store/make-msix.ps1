<#
  Foldy Store 판(MSIX)을 만든다. 같은 소스·같은 payload(dist\Foldy-Setup\payload)를 MSIX로 감싼다 (docs/store.md).
  - 자체 업데이트 도구(foldy-update.exe)는 넣지 않는다 (업데이트는 Store가 한다).
  - 색인 서비스(foldy-index.exe)는 1.15.0부터 패키지 서비스로 넣는다 (서비스 이름 FoldyIndexerStore, LocalSystem, 자동 시작).
    -NoService(또는 저장소 변수 FOLDY_STORE_SERVICE=false)면 빼고, 앱이 사용자 권한으로 드라이브 목록을 직접 모은다(1.15.0).
  - 단축키 도우미(foldy-hotkey.exe)는 넣는다: 매니페스트의 startupTask로 로그인 때 떠서 Ctrl+Space·주기 모으기를 한다.
  - 서명하지 않는다: Store가 제출받은 패키지를 Microsoft 인증서로 서명한다.
    로컬 시험 설치만 하려면 -TestCert로 자체 서명 인증서를 만들어 서명한다 (그 PC에서만 신뢰).

  Identity 기본값은 Partner Center의 "Foldy File Manager"(Store ID 9NS1PWL5QGP6) 제품 ID다 (공개 정보).
  다른 제품으로 내려면 저장소 변수 FOLDY_STORE_IDENTITY_NAME / FOLDY_STORE_PUBLISHER / FOLDY_STORE_PUBLISHER_DISPLAY_NAME 으로 덮어쓴다.

  예) ./build/store/make-msix.ps1 -Version 0.9.32 -OutDir artifacts/release
#>
param(
    [Parameter(Mandatory)] [string] $Version,
    [string] $OutDir = 'artifacts/store',
    [string] $IdentityName = $(if ($env:FOLDY_STORE_IDENTITY_NAME) { $env:FOLDY_STORE_IDENTITY_NAME } else { 'DavidCHA.FoldyFileManager' }),
    [string] $Publisher = $(if ($env:FOLDY_STORE_PUBLISHER) { $env:FOLDY_STORE_PUBLISHER } else { 'CN=2AAA454A-54B0-4C55-B5E3-99DD3024FB14' }),
    [string] $PublisherDisplayName = $(if ($env:FOLDY_STORE_PUBLISHER_DISPLAY_NAME) { $env:FOLDY_STORE_PUBLISHER_DISPLAY_NAME } else { 'David CHA' }),
    [switch] $TestCert,
    # CI 검사용: 다른 payload 폴더로 만든다 (build/store/check-msix.ps1이 가짜 exe로 매니페스트·패키지 모양만 확인)
    [string] $Payload,
    # 색인 서비스를 넣지 않는다 (Store 심사에서 제한 기능이 거절되면 저장소 변수 FOLDY_STORE_SERVICE=false로)
    [switch] $NoService = $($env:FOLDY_STORE_SERVICE -eq 'false')
)
$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$payload = if ($Payload) { (Resolve-Path $Payload).Path } else { Join-Path $root 'dist\Foldy-Setup\payload' }
if (-not (Test-Path (Join-Path $payload 'Foldy.exe'))) { throw "payload가 없습니다: $payload (먼저 build/setup/make-setup.ps1)" }

# Store 버전은 네 자리, 마지막은 0이어야 한다 (1.2.3 → 1.2.3.0). 미리보기 접미사(-beta 등)는 뗀다.
$core = ($Version -replace '[-+].*$', '')
$parts = $core.Split('.')
if ($parts.Count -ne 3) { throw "버전 형식이 1.2.3이 아님: $Version" }
$msixVersion = "$core.0"

# ── 패키지 폴더 ──
$stage = Join-Path $root 'dist\Foldy-Store'
Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $stage | Out-Null
Copy-Item (Join-Path $payload '*') $stage -Recurse
foreach ($f in @('foldy-update.exe') + $(if ($NoService) { @('foldy-index.exe') } else { @() })) { Remove-Item (Join-Path $stage $f) -ErrorAction SilentlyContinue }

# ── 로고 (foldy-256.png에서 크기별로) ──
Add-Type -AssemblyName System.Drawing
$assets = Join-Path $stage 'Assets'
New-Item -ItemType Directory -Force $assets | Out-Null
$src = [System.Drawing.Image]::FromFile((Join-Path $root 'assets\icons\png\foldy-256.png'))
function Save-Logo([string] $name, [int] $w, [int] $h, [double] $scale) {
    $bmp = [System.Drawing.Bitmap]::new($w, $h)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.Clear([System.Drawing.Color]::Transparent)
    $side = [int]([Math]::Min($w, $h) * $scale)
    $g.DrawImage($src, [int](($w - $side) / 2), [int](($h - $side) / 2), $side, $side)
    $g.Dispose()
    $bmp.Save((Join-Path $assets $name), [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
}
Save-Logo 'StoreLogo.png' 50 50 1.0
Save-Logo 'Square44x44Logo.png' 44 44 1.0
Save-Logo 'Square150x150Logo.png' 150 150 0.66
Save-Logo 'Wide310x150Logo.png' 310 150 0.66
$src.Dispose()

# ── 매니페스트 ──
function Escape-Xml([string] $s) { [Security.SecurityElement]::Escape($s) }
$manifest = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'AppxManifest.template.xml'))
$manifest = $manifest.Replace('{{IdentityName}}', (Escape-Xml $IdentityName)).Replace('{{Publisher}}', (Escape-Xml $Publisher)).
    Replace('{{PublisherDisplayName}}', (Escape-Xml $PublisherDisplayName)).Replace('{{Version}}', $msixVersion)
# 색인 서비스: 설치판의 FoldyIndexer와 이름이 겹치면 둘 다 깐 PC에서 패키지 설치가 실패하므로 이름을 달리 한다
# (한 프로세스 서비스라 서비스 안의 이름은 무엇이든 된다). 둘 다 있으면 먼저 뜬 쪽이 찾기 통로를 맡는다.
$svcExt = if ($NoService) { '' } else {
    "        <desktop6:Extension Category=`"windows.service`" Executable=`"foldy-index.exe`" EntryPoint=`"Windows.FullTrustApplication`">`r`n" +
    "          <desktop6:Service Name=`"FoldyIndexerStore`" StartupType=`"auto`" StartAccount=`"localSystem`" Arguments=`"--service --store`" />`r`n" +
    "        </desktop6:Extension>`r`n"
}
$svcCaps = if ($NoService) { '' } else {
    "    <rescap:Capability Name=`"packagedServices`" />`r`n    <rescap:Capability Name=`"localSystemServices`" />`r`n"
}
$manifest = $manifest.Replace("{{ServiceExtension}}`r`n", $svcExt).Replace("{{ServiceExtension}}`n", $svcExt).Replace('{{ServiceExtension}}', $svcExt)
$manifest = $manifest.Replace("{{ServiceCapabilities}}`r`n", $svcCaps).Replace("{{ServiceCapabilities}}`n", $svcCaps).Replace('{{ServiceCapabilities}}', $svcCaps)
if ($manifest -match '\{\{') { throw '매니페스트에 채우지 않은 {{…}}가 남았습니다' }
[IO.File]::WriteAllText((Join-Path $stage 'AppxManifest.xml'), $manifest, [Text.UTF8Encoding]::new($false))

# ── makeappx (Windows SDK) ──
$makeappx = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\makeappx.exe" -ErrorAction SilentlyContinue |
    Sort-Object { [version]($_.Directory.Parent.Name) } -Descending | Select-Object -First 1
if (-not $makeappx) { throw 'makeappx.exe를 찾지 못했습니다 (Windows 10/11 SDK 필요)' }
New-Item -ItemType Directory -Force $OutDir | Out-Null
$msix = Join-Path (Resolve-Path $OutDir) "Foldy-$core-x64.msix"
Remove-Item $msix -ErrorAction SilentlyContinue
if (-not (Test-Path (Join-Path $stage 'foldy-hotkey.exe'))) { throw 'foldy-hotkey.exe가 payload에 없습니다 (매니페스트의 startupTask가 가리킴)' }
& $makeappx.FullName pack /o /h SHA256 /d $stage /p $msix
if ($LASTEXITCODE -ne 0) { throw 'makeappx 실패' }

if ($TestCert) {
    # 로컬 시험용: 자체 서명 인증서로 서명 (Publisher와 주체가 같아야 함). 이 인증서를 '신뢰할 수 있는 사용자'에 넣어야 설치된다.
    $cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject $Publisher -CertStoreLocation Cert:\CurrentUser\My `
        -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3', '2.5.29.19={text}')
    $signtool = Join-Path $makeappx.Directory.FullName 'signtool.exe'
    & $signtool sign /fd SHA256 /sha1 $cert.Thumbprint $msix
    if ($LASTEXITCODE -ne 0) { throw 'signtool 실패' }
    Write-Host "시험 인증서: $($cert.Thumbprint) (CurrentUser\My). 설치 전에 '신뢰할 수 있는 사용자' 저장소로 복사하세요."
}

Write-Host "색인 서비스: $(if ($NoService) { '넣지 않음' } else { 'FoldyIndexerStore (LocalSystem, 자동)' })"
Write-Host "MSIX: $msix ($([math]::Round((Get-Item $msix).Length / 1MB, 1)) MB), 버전 $msixVersion, Identity $IdentityName"
