<#
.SYNOPSIS
  Foldy 전체 빌드: Rust 엔진 → (csbindgen 바인딩 생성) → .NET 솔루션 → 테스트.
  CI(GitHub Actions)도 이 스크립트를 그대로 호출한다 (설계서 10.2).

.EXAMPLE
  ./build.ps1                 # Release 빌드 + 전체 테스트
  ./build.ps1 -SkipTests      # 빌드만
  ./build.ps1 -Bench          # 빌드 후 10만 개 목록 스크롤 벤치 (창이 잠깐 뜸)
#>
[CmdletBinding()]
param(
    [ValidateSet('Release', 'Debug')] [string] $Configuration = 'Release',
    [switch] $SkipTests,
    [switch] $SkipRust,
    [switch] $Bench
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = $PSScriptRoot

function Step($name, [scriptblock] $body) {
    Write-Host "`n==> $name" -ForegroundColor Cyan
    $sw = [Diagnostics.Stopwatch]::StartNew()
    & $body
    if ($LASTEXITCODE -and $LASTEXITCODE -ne 0) { throw "$name 실패 (exit $LASTEXITCODE)" }
    Write-Host ("    완료 {0:N1}s" -f $sw.Elapsed.TotalSeconds) -ForegroundColor DarkGray
}

# 사용자 단위로 설치한 도구도 찾을 수 있게 PATH 보강
foreach ($p in @("$env:USERPROFILE\.cargo\bin", "$env:LOCALAPPDATA\Microsoft\dotnet")) {
    if ((Test-Path $p) -and ($env:PATH -notlike "*$p*")) { $env:PATH = "$p;$env:PATH" }
}
if (-not $env:DOTNET_ROOT -and (Test-Path "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe")) {
    $env:DOTNET_ROOT = "$env:LOCALAPPDATA\Microsoft\dotnet"
}
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'

if (-not $SkipRust) {
    Push-Location (Join-Path $root 'engine')
    try {
        Step 'Rust: cargo build --release (csbindgen 바인딩 생성 포함)' { cargo build --release --locked }
        if (-not $SkipTests) {
            Step 'Rust: cargo test' { cargo test --release --locked }
            Step 'Rust: cargo clippy -D warnings' { cargo clippy --release --all-targets --locked -- -D warnings }
        }
    }
    finally { Pop-Location }
    $env:FOLDY_REQUIRE_NATIVE = '1'   # Rust를 빌드했으면 경계 시험은 건너뛰지 않는다
}

Push-Location $root
try {
    Step ".NET: dotnet build ($Configuration)" { dotnet build Foldy.slnx -c $Configuration }
    if (-not $SkipTests) {
        Step '.NET: 테스트 (Core, Interop)' { dotnet test --solution Foldy.slnx -c $Configuration --no-build }
    }
    if ($Bench) {
        $exe = Join-Path $root "src\Foldy.App\bin\$Configuration\net10.0-windows\Foldy.exe"
        $out = Join-Path $root 'artifacts\bench-list.json'
        New-Item -ItemType Directory -Force (Split-Path $out) | Out-Null
        Step '벤치: 10만 개 목록 스크롤' {
            $p = Start-Process $exe -ArgumentList '--bench-list', '--out', $out -PassThru -Wait
            Get-Content $out | Write-Host
            $global:LASTEXITCODE = $p.ExitCode
        }
    }
}
finally { Pop-Location }

Write-Host "`n빌드 성공" -ForegroundColor Green
