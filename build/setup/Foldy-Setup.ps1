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

$log = Join-Path $env:TEMP 'Foldy-Setup.log'
Start-Transcript -Path $log -Force | Out-Null
trap {
    Write-Host ''
    Write-Host "설치 중 오류: $($_.Exception.Message)" -ForegroundColor Red
    Write-Host "기록: $log"
    Stop-Transcript | Out-Null
    Read-Host 'Enter 키를 누르면 닫힙니다'
    exit 1
}

function Step($text) { Write-Host "  · $text" }

Write-Host ''
Write-Host ($(if ($Uninstall) { 'Foldy 제거' } else { 'Foldy 설치' })) -ForegroundColor Cyan
if (-not $Uninstall) {
    # 개인정보 안내 (전문: payload\PRIVACY.md, https://github.com/chadolkr/Foldy/blob/main/PRIVACY.md)
    Write-Host ''
    Write-Host '개인정보: Foldy는 업데이트 확인(GitHub), 동의한 경우의 오류 보고, 사용자가 연 SFTP·Git 연결 말고는'
    Write-Host '어떤 정보도 밖으로 보내지 않습니다. 업데이트 확인과 오류 보고는 설정에서 끌 수 있습니다. 전문: PRIVACY.md'
    Write-Host 'Privacy: Foldy sends nothing to other systems except update checks (GitHub), crash reports you agree to,'
    Write-Host 'and SFTP/Git connections you open. Both update checks and crash reports can be turned off in Settings.'
    Write-Host ''
}

# MSI(자동 업데이트가 설치하는 정식 설치본)로 깔려 있으면 이 스크립트로 덮어쓰거나 지우지 않는다.
# 파일만 지우면 Windows Installer 등록(프로그램 추가/제거)이 남아 고장 난 항목이 된다.
$msiFoldy = Get-ChildItem 'HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall' -ErrorAction SilentlyContinue |
    ForEach-Object { Get-ItemProperty $_.PSPath -ErrorAction SilentlyContinue } |
    Where-Object { $_.DisplayName -eq 'Foldy' -and $_.WindowsInstaller -eq 1 } | Select-Object -First 1
if ($msiFoldy) {
    $what = if ($Uninstall) { '제거' } else { '설치' }
    Write-Host "Foldy가 MSI로 설치돼 있습니다 (버전 $($msiFoldy.DisplayVersion)). 이 스크립트로는 $what 하지 않습니다." -ForegroundColor Yellow
    if ($Uninstall) { Write-Host '제거: 설정 > 앱 > 설치된 앱 > Foldy > 제거 (또는 msiexec /x). 설정·세션은 남습니다.' }
    else { Write-Host '새 버전은 Foldy의 자동 업데이트가 설치합니다. 수동 설치는 릴리스의 .msi를 실행하세요.' }
    Stop-Transcript | Out-Null
    Start-Sleep -Seconds 4
    return
}
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
    Stop-Transcript | Out-Null
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
# Windows PowerShell 5.1은 네이티브 명령 인수 안의 따옴표를 지워 버리므로 sc.exe create 대신 New-Service / CIM을 쓴다
$desc = 'Foldy Indexer by David CHA - 파일 이름 즉시 검색을 위한 디스크 색인'
$existing = Get-CimInstance Win32_Service -Filter "Name='$service'" -ErrorAction SilentlyContinue
if ($existing) {
    $r = Invoke-CimMethod -InputObject $existing -MethodName Change -Arguments @{ PathName = $bin }
    if ($r.ReturnValue -ne 0) { throw "서비스 경로 변경 실패 (코드 $($r.ReturnValue))" }
} else {
    New-Service -Name $service -BinaryPathName $bin -DisplayName 'Foldy Indexer' -Description $desc -StartupType Automatic | Out-Null
}
Set-Service -Name $service -Description $desc
$out = & sc.exe config $service start= delayed-auto
if ($LASTEXITCODE -ne 0) { Write-Host "    (지연 시작 설정 실패: $out)" }
$out = & sc.exe failure $service reset= 86400 actions= restart/60000/restart/60000//
if ($LASTEXITCODE -ne 0) { Write-Host "    (실패 시 재시작 설정 실패: $out)" }
Start-Service $service
if ((Get-Service $service).Status -ne 'Running') { throw '색인 서비스를 시작하지 못했습니다' }

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

Stop-Transcript | Out-Null
Start-Sleep -Seconds 4
