<#
.SYNOPSIS
  Foldy를 이 PC의 사용자 폴더에 설치한다 (관리자 권한 불필요).
  %LOCALAPPDATA%\Programs\Foldy 에 자체 포함(self-contained) 게시본을 두고 시작 메뉴 바로가기를 만든다.
  .NET 런타임이 따로 설치돼 있지 않아도 실행된다. MSI 설치 파일은 M6에서 만든다.

.EXAMPLE
  ./build/install-local.ps1            # 빌드 후 설치
  ./build/install-local.ps1 -Uninstall # 제거 (설정·세션은 %LOCALAPPDATA%\Foldy 에 남김)
#>
[CmdletBinding()]
param([switch] $Uninstall, [switch] $SkipRust)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$target = Join-Path $env:LOCALAPPDATA 'Programs\Foldy'
$startMenu = Join-Path ([Environment]::GetFolderPath('Programs')) 'Foldy.lnk'

Get-Process Foldy -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$target*" } | Stop-Process -Force

if ($Uninstall) {
    Remove-Item $target -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item $startMenu -Force -ErrorAction SilentlyContinue
    Write-Host "Foldy 제거됨: $target"
    return
}

foreach ($p in @("$env:USERPROFILE\.cargo\bin", "$env:LOCALAPPDATA\Microsoft\dotnet")) {
    if ((Test-Path $p) -and ($env:PATH -notlike "*$p*")) { $env:PATH = "$p;$env:PATH" }
}
if (-not $env:DOTNET_ROOT -and (Test-Path "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe")) { $env:DOTNET_ROOT = "$env:LOCALAPPDATA\Microsoft\dotnet" }

if (-not $SkipRust) {
    Push-Location (Join-Path $root 'engine'); try { cargo build --release --locked; if ($LASTEXITCODE) { throw 'cargo build 실패' } } finally { Pop-Location }
}

$staging = Join-Path $root 'artifacts\publish'
Remove-Item $staging -Recurse -Force -ErrorAction SilentlyContinue
dotnet publish (Join-Path $root 'src\Foldy.App\Foldy.App.csproj') -c Release -r win-x64 --self-contained true -p:PublishReadyToRun=true -o $staging
if ($LASTEXITCODE) { throw 'dotnet publish 실패' }
Copy-Item (Join-Path $root 'engine\target\release\foldy-index.exe') $staging -ErrorAction SilentlyContinue
Copy-Item (Join-Path $root 'LICENSE') $staging

New-Item -ItemType Directory -Force $target | Out-Null
robocopy $staging $target /MIR /NFL /NDL /NJH /NJS /NP | Out-Null
if ($LASTEXITCODE -ge 8) { throw "파일 복사 실패 (robocopy $LASTEXITCODE)" }

$shell = New-Object -ComObject WScript.Shell
$lnk = $shell.CreateShortcut($startMenu)
$lnk.TargetPath = Join-Path $target 'Foldy.exe'
$lnk.WorkingDirectory = $target
$lnk.IconLocation = (Join-Path $target 'Foldy.exe') + ',0'
$lnk.Description = 'Foldy 파일 관리자'
$lnk.Save()

$ver = (Get-Item (Join-Path $target 'Foldy.exe')).VersionInfo.ProductVersion
Write-Host "Foldy $ver 설치됨: $target"
Write-Host "시작 메뉴: $startMenu"
$global:LASTEXITCODE = 0
