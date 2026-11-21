<#
  Foldy Store 판(MSIX)을 만든다. 같은 소스·같은 payload(dist\Foldy-Setup\payload)를 MSIX로 감싼다 (docs/store.md).
  - 색인 서비스(foldy-index.exe)와 자체 업데이트 도구(foldy-update.exe)는 넣지 않는다. 앱은 패키지로 실행되면 이를 알아채고 Store에 맡긴다.
  - 서명하지 않는다: Store가 제출받은 패키지를 Microsoft 인증서로 서명한다.
    로컬 시험 설치만 하려면 -TestCert로 자체 서명 인증서를 만들어 서명한다 (그 PC에서만 신뢰).

  Identity 값은 Partner Center → 앱 → Product management → Product identity 에서 복사한다.
  CI에서는 저장소 변수 FOLDY_STORE_IDENTITY_NAME / FOLDY_STORE_PUBLISHER / FOLDY_STORE_PUBLISHER_DISPLAY_NAME 으로 넘긴다.

  예) ./build/store/make-msix.ps1 -Version 0.9.32 -OutDir artifacts/release
#>
param(
    [Parameter(Mandatory)] [string] $Version,
    [string] $OutDir = 'artifacts/store',
    [string] $IdentityName = $(if ($env:FOLDY_STORE_IDENTITY_NAME) { $env:FOLDY_STORE_IDENTITY_NAME } else { 'Foldy.Dev' }),
    [string] $Publisher = $(if ($env:FOLDY_STORE_PUBLISHER) { $env:FOLDY_STORE_PUBLISHER } else { 'CN=Foldy Dev' }),
    [string] $PublisherDisplayName = $(if ($env:FOLDY_STORE_PUBLISHER_DISPLAY_NAME) { $env:FOLDY_STORE_PUBLISHER_DISPLAY_NAME } else { 'David CHA' }),
    [switch] $TestCert
)
$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$payload = Join-Path $root 'dist\Foldy-Setup\payload'
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
foreach ($f in 'foldy-index.exe', 'foldy-update.exe') { Remove-Item (Join-Path $stage $f) -ErrorAction SilentlyContinue }

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
[IO.File]::WriteAllText((Join-Path $stage 'AppxManifest.xml'), $manifest, [Text.UTF8Encoding]::new($false))

# ── makeappx (Windows SDK) ──
$makeappx = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\makeappx.exe" -ErrorAction SilentlyContinue |
    Sort-Object { [version]($_.Directory.Parent.Name) } -Descending | Select-Object -First 1
if (-not $makeappx) { throw 'makeappx.exe를 찾지 못했습니다 (Windows 10/11 SDK 필요)' }
New-Item -ItemType Directory -Force $OutDir | Out-Null
$msix = Join-Path (Resolve-Path $OutDir) "Foldy-$core-x64.msix"
Remove-Item $msix -ErrorAction SilentlyContinue
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

Write-Host "MSIX: $msix ($([math]::Round((Get-Item $msix).Length / 1MB, 1)) MB), 버전 $msixVersion, Identity $IdentityName"
