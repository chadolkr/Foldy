<#
.SYNOPSIS
  Foldy MSI를 만든다: artifacts\Foldy-<버전>-x64.msi (WiX 6, 사전 설치: dotnet tool install --global wix --version 6.0.2).
  페이로드는 build/setup/make-setup.ps1(-SkipRust)로 만든 self-contained 게시 폴더를 쓴다.

.PARAMETER Version   MSI 버전. 생략하면 Directory.Build.props의 <Version>. 프리릴리스 접미사(-beta.1)는 MSI 버전에서 떼어낸다.
.PARAMETER SkipPayload  이미 만들어 둔 dist\Foldy-Setup\payload를 재사용한다 (CI에서 setup zip 단계 직후).
.PARAMETER OutDir    MSI 출력 폴더 (기본 artifacts).
#>
[CmdletBinding()]
param([string] $Version, [switch] $SkipPayload, [string] $OutDir)

$ErrorActionPreference = 'Stop'
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
foreach ($p in @("$env:USERPROFILE\.cargo\bin", "$env:LOCALAPPDATA\Microsoft\dotnet", "$env:USERPROFILE\.dotnet\tools")) {
    if ((Test-Path $p) -and ($env:PATH -notlike "*$p*")) { $env:PATH = "$p;$env:PATH" }
}
if (-not $env:DOTNET_ROOT -and (Test-Path "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe")) { $env:DOTNET_ROOT = "$env:LOCALAPPDATA\Microsoft\dotnet" }
if (-not (Get-Command wix -ErrorAction SilentlyContinue)) {
    throw "wix가 없습니다: dotnet tool install --global wix --version 6.0.2 (WiX 7은 OSMF EULA 동의가 필요해 6.x를 쓴다)"
}

# ── 버전: 인자 > Directory.Build.props ──
if (-not $Version) {
    $props = [xml](Get-Content (Join-Path $root 'Directory.Build.props') -Raw)
    $Version = ($props.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1)
    if (-not $Version) { throw 'Directory.Build.props에서 <Version>을 찾지 못했습니다' }
}
$full = $Version.Trim()
if ($full -notmatch '^(\d+)\.(\d+)\.(\d+)') { throw "버전 형식이 x.y.z가 아님: $full" }
$msiVersion = "$($Matches[1]).$($Matches[2]).$($Matches[3])"   # MSI는 숫자만 (최대 255.255.65535)

# ── 페이로드 ──
$payload = Join-Path $root 'dist\Foldy-Setup\payload'
if (-not $SkipPayload) {
    $zipTmp = Join-Path ([IO.Path]::GetTempPath()) "foldy-msi-zip-$PID"   # make-setup이 만드는 zip은 버린다
    & (Join-Path $root 'build\setup\make-setup.ps1') -SkipRust -Version $full -ZipDir $zipTmp
    Remove-Item $zipTmp -Recurse -Force -ErrorAction SilentlyContinue
}
foreach ($f in 'Foldy.exe', 'foldy-index.exe') {
    if (-not (Test-Path (Join-Path $payload $f))) { throw "페이로드에 $f 가 없습니다: $payload (engine 빌드 필요: cd engine; cargo build --release)" }
}

# ── LICENSE → RTF (설치 UI의 라이선스 화면) ──
$work = Join-Path $root 'dist\installer'
New-Item -ItemType Directory -Force $work | Out-Null
$rtfPath = Join-Path $work 'license.rtf'
$sb = [Text.StringBuilder]::new()
[void]$sb.Append('{\rtf1\ansi\ansicpg1252\deff0{\fonttbl{\f0\fnil\fcharset129 Malgun Gothic;}}\viewkind4\uc1\pard\f0\fs18' + "`r`n")
# 개인정보 정책을 라이선스 앞에 붙여 설치 중에 보여 준다 (SignPath Foundation 요건: 설치 중 개인정보 정책 표시)
$lines = @(Get-Content (Join-Path $root 'PRIVACY.md') -Encoding UTF8) + @('', '----------------------------------------', '') + @(Get-Content (Join-Path $root 'LICENSE') -Encoding UTF8)
foreach ($line in $lines) {
    foreach ($ch in $line.ToCharArray()) {
        $c = [int]$ch
        if ($ch -eq '\' -or $ch -eq '{' -or $ch -eq '}') { [void]$sb.Append('\').Append($ch) }
        # RTF \uN은 부호 있는 16비트: 32767보다 큰 코드(한글 등)는 65536을 뺀다
        elseif ($c -gt 126) { $n = if ($c -gt 32767) { $c - 65536 } else { $c }; [void]$sb.Append("\u$n?") }
        else { [void]$sb.Append($ch) }
    }
    [void]$sb.Append("\par`r`n")
}
[void]$sb.Append('}')
[IO.File]::WriteAllText($rtfPath, $sb.ToString(), [Text.Encoding]::ASCII)

# ── wix build ──
$outDir = if ($OutDir) { $OutDir } else { Join-Path $root 'artifacts' }
New-Item -ItemType Directory -Force $outDir | Out-Null
$msi = Join-Path $outDir "Foldy-$full-x64.msi"   # 파일명은 프리릴리스 접미사 포함, 내부 ProductVersion은 숫자만
Remove-Item $msi -ErrorAction SilentlyContinue
$ui = (wix extension list -g) -join "`n"
if ($ui -notmatch 'WixToolset\.UI\.wixext' -or $ui -notmatch 'WixToolset\.Util\.wixext') {
    throw "WiX 확장이 없습니다: wix extension add -g WixToolset.UI.wixext/6.0.2 ; wix extension add -g WixToolset.Util.wixext/6.0.2"
}
wix build (Join-Path $PSScriptRoot 'Foldy.wxs') `
    -arch x64 `
    -ext WixToolset.UI.wixext -ext WixToolset.Util.wixext `
    -loc (Join-Path $PSScriptRoot 'Strings.en-us.wxl') `
    -d "PayloadDir=$payload" -d "Version=$msiVersion" -d "RepoRoot=$root" -d "LicenseRtf=$rtfPath" `
    -o $msi
if ($LASTEXITCODE) { throw 'wix build 실패' }
# ICE 검증. 억제 항목은 per-machine 설치의 알려진 오탐이다 (docs/installer.md):
#  ICE43/57 = 공용 시작 메뉴(ProgramMenuFolder)의 비광고 바로가기를 사용자별 데이터로 오인
#  ICE61    = AllowSameVersionUpgrades(같은 숫자 버전의 프리릴리스 교체) 때문에 최대 버전이 현재와 같음
wix msi validate -sice ICE43 -sice ICE57 -sice ICE61 $msi
if ($LASTEXITCODE) { throw 'wix msi validate 실패' }
Write-Host "MSI:$msi ($([math]::Round((Get-Item $msi).Length / 1MB, 1)) MB, 버전 $msiVersion)"
$global:LASTEXITCODE = 0
