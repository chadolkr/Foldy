<#
.SYNOPSIS
  Foldy 한 번에 설치: 프로그램(Program Files\Foldy) + 시작 메뉴 + 색인 서비스(FoldyIndexer) + 프로그램 추가/제거 등록.
  관리자 권한이 필요하며, 일반 권한으로 실행하면 UAC 창을 한 번 띄워 스스로 다시 실행한다.
  이 스크립트 옆의 payload 폴더(자체 포함 게시본)를 설치한다. 정식 MSI(WiX)가 나오면 대체된다.

.PARAMETER Uninstall
  Foldy와 색인 서비스를 제거한다 (설정·세션 %LOCALAPPDATA%\Foldy 는 남긴다).
#>
[CmdletBinding()]
param([switch] $Uninstall, [switch] $NoLaunch)

$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$target = Join-Path $env:ProgramFiles 'Foldy'
$service = 'FoldyIndexer'
$uninstallKey = 'HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\Foldy'
$startMenu = Join-Path ([Environment]::GetFolderPath('CommonPrograms')) 'Foldy.lnk'

# ── 관리자 권한으로 다시 실행 (UAC 한 번) ──
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    $args2 = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"")
    if ($Uninstall) { $args2 += '-Uninstall' }
    if ($NoLaunch) { $args2 += '-NoLaunch' }
    Start-Process powershell -Verb RunAs -ArgumentList $args2
    return
}

function Step($text) { Write-Host "  · $text" }

Write-Host ''
Write-Host ($(if ($Uninstall) { 'Foldy 제거' } else { 'Foldy 설치' })) -ForegroundColor Cyan

# 실행 중인 Foldy와 서비스 정지
Get-Process Foldy -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
if (Get-Service $service -ErrorAction SilentlyContinue) {
    Step '색인 서비스 정지'
    Stop-Service $service -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 500
}

if ($Uninstall) {
    if (Get-Service $service -ErrorAction SilentlyContinue) { Step '색인 서비스 삭제'; sc.exe delete $service | Out-Null }
    Step '파일 삭제'
    Remove-Item $target -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item $startMenu -Force -ErrorAction SilentlyContinue
    Remove-Item $uninstallKey -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item (Join-Path $env:ProgramData 'Foldy') -Recurse -Force -ErrorAction SilentlyContinue
    Write-Host '제거했습니다. 설정과 세션은 %LOCALAPPDATA%\Foldy 에 남아 있습니다.' -ForegroundColor Green
    Start-Sleep -Seconds 3
    return
}

$payload = Join-Path $here 'payload'
if (-not (Test-Path (Join-Path $payload 'Foldy.exe'))) { throw "설치할 파일이 없습니다: $payload" }

Step "파일 복사 → $target"
New-Item -ItemType Directory -Force $target | Out-Null
robocopy $payload $target /MIR /NFL /NDL /NJH /NJS /NP | Out-Null
if ($LASTEXITCODE -ge 8) { throw "파일 복사 실패 (robocopy $LASTEXITCODE)" }
Copy-Item $PSCommandPath (Join-Path $target 'Foldy-Setup.ps1') -Force

# 예전 사용자별 설치(%LOCALAPPDATA%\Programs\Foldy)가 있으면 정리
$userInstall = Join-Path $env:LOCALAPPDATA 'Programs\Foldy'
if (Test-Path $userInstall) {
    Step '이전 사용자별 설치 정리'
    Remove-Item $userInstall -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item (Join-Path ([Environment]::GetFolderPath('Programs')) 'Foldy.lnk') -Force -ErrorAction SilentlyContinue
}

Step '시작 메뉴 바로가기'
$exe = Join-Path $target 'Foldy.exe'
$shell = New-Object -ComObject WScript.Shell
$lnk = $shell.CreateShortcut($startMenu)
$lnk.TargetPath = $exe
$lnk.WorkingDirectory = $target
$lnk.IconLocation = "$exe,0"
$lnk.Description = 'Foldy 파일 관리자'
$lnk.Save()

Step '색인 서비스 등록 (FoldyIndexer, SYSTEM, 지연된 자동 시작)'
$indexer = Join-Path $target 'foldy-index.exe'
$bin = "`"$indexer`" --service"
if (Get-Service $service -ErrorAction SilentlyContinue) {
    sc.exe config $service binPath= $bin start= delayed-auto | Out-Null
} else {
    sc.exe create $service binPath= $bin start= delayed-auto DisplayName= 'Foldy Indexer' | Out-Null
}
sc.exe description $service 'Foldy Indexer by David CHA - 파일 이름 즉시 검색을 위한 디스크 색인' | Out-Null
sc.exe failure $service reset= 86400 actions= restart/60000/restart/60000// | Out-Null
Start-Service $service -ErrorAction SilentlyContinue

Step '프로그램 추가/제거에 등록'
$ver = (Get-Item $exe).VersionInfo.ProductVersion -replace '\+.*$', ''
New-Item -Path $uninstallKey -Force | Out-Null
Set-ItemProperty $uninstallKey -Name DisplayName -Value 'Foldy'
Set-ItemProperty $uninstallKey -Name DisplayVersion -Value $ver
Set-ItemProperty $uninstallKey -Name Publisher -Value 'David CHA'
Set-ItemProperty $uninstallKey -Name URLInfoAbout -Value 'https://github.com/chadolkr/Foldy'
Set-ItemProperty $uninstallKey -Name HelpLink -Value 'https://github.com/chadolkr/Foldy/issues'
Set-ItemProperty $uninstallKey -Name DisplayIcon -Value "$exe,0"
Set-ItemProperty $uninstallKey -Name InstallLocation -Value $target
Set-ItemProperty $uninstallKey -Name UninstallString -Value "powershell.exe -NoProfile -ExecutionPolicy Bypass -File `"$target\Foldy-Setup.ps1`" -Uninstall"
Set-ItemProperty $uninstallKey -Name NoModify -Value 1 -Type DWord
Set-ItemProperty $uninstallKey -Name NoRepair -Value 1 -Type DWord
$size = [int]((Get-ChildItem $target -Recurse | Measure-Object Length -Sum).Sum / 1KB)
Set-ItemProperty $uninstallKey -Name EstimatedSize -Value $size -Type DWord

$state = (Get-Service $service -ErrorAction SilentlyContinue).Status
Write-Host ''
Write-Host "Foldy $ver 설치 완료 · 색인 서비스: $state" -ForegroundColor Green
Write-Host '시작 메뉴에서 Foldy를 실행하세요. 첫 색인은 드라이브 크기에 따라 수십 초 걸립니다.'

if (-not $NoLaunch) {
    # 관리자 창에서 바로 띄우면 Foldy도 관리자로 실행되므로, 탐색기를 거쳐 일반 권한으로 연다
    Start-Process explorer.exe -ArgumentList "`"$exe`""
}

Start-Sleep -Seconds 4
