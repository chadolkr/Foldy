<#
 문서용 화면 캡처 (README·도움말 그림): 가짜 샘플 폴더를 만들고 장면마다 PNG를 docs/images 에 저장한다.
 사용자 폴더·이름이 화면에 보이지 않게 샘플 폴더를 C:\FDemo 에 만든다 (끝나면 지운다).
 설치된 Foldy와 색인 서비스는 건드리지 않는다 (설정은 임시 데이터 폴더 C:\FoldyDemoData).
 먼저 `dotnet build Foldy.slnx -c Release` 가 되어 있어야 한다.
 다른 언어 화면: -Lang en -Out assets\help\images\en  (짧은 소개는 화면 언어의 그림을 먼저 쓰고, 없으면 한국어판 그림).
#>
param(
    [string]$Out = (Join-Path $PSScriptRoot '..\docs\images'),
    [string]$Root = 'C:\FDemo',
    [string]$Exe = (Join-Path $PSScriptRoot '..\src\Foldy.App\bin\x64\Release\net10.0-windows\Foldy.exe'),
    [switch]$Keep,
    # 화면 언어 (ko, en, es, ja, zh-Hans). 비우면 한국어
    [string]$Lang = '',
    # CI: 장면이 하나라도 실패하면 오류로 끝낸다 (화면 자동 점검)
    [switch]$Strict
)
$ErrorActionPreference = 'Stop'
$Out = [IO.Path]::GetFullPath($Out)
$data = 'C:\FoldyDemoData'
New-Item -ItemType Directory -Force -Path $Out | Out-Null
if (Test-Path $data) { Remove-Item $data -Recurse -Force }
New-Item -ItemType Directory -Force -Path $data | Out-Null
$env:FOLDY_DATA_DIR = $data
if ($Lang) { $env:FOLDY_LANGUAGE = $Lang } else { Remove-Item Env:FOLDY_LANGUAGE -ErrorAction SilentlyContinue }
$p = Start-Process -FilePath $Exe -ArgumentList @('--doc-screenshots', $Out, '--doc-root', $Root) -PassThru
if (-not $p.WaitForExit(600000)) { $p.Kill(); throw '시간 초과' }
Write-Host "종료 코드 $($p.ExitCode)"
$result = Join-Path $Out '_result.txt'
$failed = $p.ExitCode -ne 0
if (Test-Path $result) { Get-Content $result; Remove-Item $result }
if (-not $Keep) {
    foreach ($d in @($Root, $data)) {
        if (Test-Path $d) {
            Get-ChildItem $d -Recurse -Force -File -ErrorAction SilentlyContinue | ForEach-Object { $_.Attributes = 'Normal' }
            Remove-Item $d -Recurse -Force
        }
    }
}
if ($Strict -and $failed) { throw "화면 자동 점검 실패 (종료 코드 $($p.ExitCode))" }
