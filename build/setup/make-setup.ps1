<#
.SYNOPSIS
  배포용 설치 폴더를 만든다: artifacts\Foldy-Setup\ (payload + Foldy-Setup.ps1 + 더블클릭용 Foldy 설치.cmd)
  그리고 zip(artifacts\Foldy-<버전>-setup.zip)으로 묶는다.
#>
[CmdletBinding()]
param([switch] $SkipRust, [string] $DesktopShortcut, [string] $Version, [string] $ZipDir)

$ErrorActionPreference = 'Stop'
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
foreach ($p in @("$env:USERPROFILE\.cargo\bin", "$env:LOCALAPPDATA\Microsoft\dotnet")) {
    if ((Test-Path $p) -and ($env:PATH -notlike "*$p*")) { $env:PATH = "$p;$env:PATH" }
}
if (-not $env:DOTNET_ROOT -and (Test-Path "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe")) { $env:DOTNET_ROOT = "$env:LOCALAPPDATA\Microsoft\dotnet" }

if (-not $SkipRust) {
    Push-Location (Join-Path $root 'engine'); try { cargo build --release --locked; if ($LASTEXITCODE) { throw 'cargo build 실패' } } finally { Pop-Location }
}

$out = Join-Path $root 'dist\Foldy-Setup'
$payload = Join-Path $out 'payload'
Remove-Item $out -Recurse -Force -ErrorAction SilentlyContinue
dotnet publish (Join-Path $root 'src\Foldy.App\Foldy.App.csproj') -c Release -r win-x64 --self-contained true -p:PublishReadyToRun=true -o $payload $(if ($Version) { "-p:Version=$Version" })
if ($LASTEXITCODE) { throw 'dotnet publish 실패' }
foreach ($f in 'foldy-index.exe', 'foldy-update.exe') {
    $src = Join-Path $root "engine\target\release\$f"
    if (Test-Path $src) { Copy-Item $src $payload }
}
Copy-Item (Join-Path $root 'LICENSE') $payload
# Windows PowerShell 5.1이 한글을 바르게 읽도록 UTF-8 BOM으로 저장
[IO.File]::WriteAllText((Join-Path $out 'Foldy-Setup.ps1'), [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'Foldy-Setup.ps1')), [Text.UTF8Encoding]::new($true))

$cmd = @'
@echo off
rem Foldy 설치: 더블클릭하면 관리자 권한(UAC)을 한 번 묻고 설치합니다.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Foldy-Setup.ps1"
'@
[IO.File]::WriteAllText((Join-Path $out 'Foldy 설치.cmd'), $cmd, [Text.Encoding]::GetEncoding(949))
$uncmd = $cmd.Replace('Foldy-Setup.ps1"', 'Foldy-Setup.ps1" -Uninstall').Replace('rem Foldy 설치', 'rem Foldy 제거')
[IO.File]::WriteAllText((Join-Path $out 'Foldy 제거.cmd'), $uncmd, [Text.Encoding]::GetEncoding(949))

$ver = ((Get-Item (Join-Path $payload 'Foldy.exe')).VersionInfo.ProductVersion -replace '\+.*$', '')
$zipDir = if ($ZipDir) { $ZipDir } else { Join-Path $root 'artifacts' }
New-Item -ItemType Directory -Force $zipDir | Out-Null
$zip = Join-Path $zipDir "Foldy-$ver-x64-setup.zip"
Remove-Item $zip -ErrorAction SilentlyContinue
Compress-Archive -Path (Join-Path $out '*') -DestinationPath $zip
Write-Host "설치 폴더: $out"
Write-Host "zip: $zip ($([math]::Round((Get-Item $zip).Length / 1MB, 1)) MB)"

if ($DesktopShortcut) {
    $shell = New-Object -ComObject WScript.Shell
    $lnk = $shell.CreateShortcut($DesktopShortcut)
    $lnk.TargetPath = Join-Path $out 'Foldy 설치.cmd'
    $lnk.WorkingDirectory = $out
    $lnk.IconLocation = (Join-Path $payload 'Foldy.exe') + ',0'
    $lnk.Description = 'Foldy 설치 (관리자 권한 한 번)'
    $lnk.Save()
    Write-Host "바탕 화면 바로가기: $DesktopShortcut"
}
$global:LASTEXITCODE = 0
