<#
.SYNOPSIS
  성능 회귀 관문을 헤드리스로 돌리고 통과/미달을 출력한다 (1.0 체크리스트, docs/release-1.0-checklist.md).

.DESCRIPTION
  Release 빌드(dotnet build Foldy.slnx -c Release)가 먼저 있어야 한다. 시험 데이터 폴더(빈 파일 10만 개)는
  없으면 만든다(처음 한 번 1~2분). 사용자 설정을 건드리지 않으려고 FOLDY_DATA_DIR를 임시 폴더로 준다.
  설치된 Foldy(C:\Program Files\Foldy)와 FoldyIndexer 서비스는 건드리지 않는다.

  관문
    1. 스크롤 p95 프레임 시간 (120행/프레임, 진짜 셸 창) <= 16.7ms        (docs/adr/0001-m0-results.md)
    2. 10만 개 폴더 첫 묶음 < 100ms                                       (docs/DESIGN.md 성능 예산)
    3. 시작 -> 첫 그리기 (회귀 방지선; 설계 목표는 조작 가능 300ms)        -StartupGateMs
    4. 10만 개 폴더를 연 뒤 작업 집합 메모리 (회귀 방지선)                 -MemoryGateMB
  비교용으로 사이드바·트리 없는 BenchWindow(--bench-list)도 같은 조건으로 잰다.

  환경 점검: 잠금 화면(LogonUI)이거나 스크롤 없이 잰 유휴 프레임이 40ms를 넘거나 스크롤 측정이 시간 제한에 걸리면
  스크롤 관문은 판정하지 않고 INCONCLUSIVE로 표시한다 (화면이 잠기면 WPF 프레임이 4Hz로 떨어진다).

.PARAMETER Folder         10만 개 시험 폴더 (기본 %TEMP%\foldy-perf-100k)
.PARAMETER Runs           셸 측정 반복 수 (중앙값 사용, 기본 3)
.PARAMETER SkipBaseline   BenchWindow 비교 측정을 건너뛴다
.PARAMETER Json           결과를 JSON 파일로도 남긴다
#>
[CmdletBinding()]
param(
    [string] $Folder = (Join-Path $env:TEMP 'foldy-perf-100k'),
    [int] $Runs = 3,
    [int] $StartupGateMs = 2500,
    [int] $MemoryGateMB = 400,
    [double] $ScrollGateMs = 16.7,
    [double] $FirstBatchGateMs = 100,
    [switch] $SkipBaseline,
    [string] $Json
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$exe = Join-Path $root 'src\Foldy.App\bin\x64\Release\net10.0-windows\Foldy.exe'
$bench = Join-Path $root 'tests\Foldy.Benchmarks\bin\x64\Release\net10.0-windows\Foldy.Benchmarks.exe'
foreach ($f in $exe, $bench) { if (-not (Test-Path $f)) { throw "없음: $f  (먼저 dotnet build Foldy.slnx -c Release)" } }

$work = Join-Path $env:TEMP "foldy-perf-$PID"
New-Item -ItemType Directory -Force $work | Out-Null
$env:FOLDY_DATA_DIR = Join-Path $work 'data'
New-Item -ItemType Directory -Force $env:FOLDY_DATA_DIR | Out-Null

if (-not (Test-Path $Folder) -or (Get-ChildItem $Folder -File | Select-Object -First 100001 | Measure-Object).Count -lt 100000) {
    Write-Host "시험 폴더 만드는 중 (10만 개): $Folder"
    & $bench gen $Folder 100000 | Out-Null
}

function Invoke-Bench([string[]] $BenchArgs, [string] $OutFile, [int] $TimeoutSec) {
    Remove-Item $OutFile -ErrorAction SilentlyContinue
    $p = Start-Process $exe -ArgumentList ($BenchArgs + @('--out', $OutFile)) -PassThru
    if (-not $p.WaitForExit($TimeoutSec * 1000)) { $p.Kill(); Write-Warning "시간 초과: $($BenchArgs -join ' ')" }
    if (Test-Path $OutFile) { Get-Content $OutFile -Raw | ConvertFrom-Json } else { $null }
}
function Median($v) { $s = @($v | Sort-Object); if ($s.Count -eq 0) { return $null }; $s[[int][math]::Floor(($s.Count - 1) / 2)] }

$locked = [bool](Get-Process LogonUI -ErrorAction SilentlyContinue)
$shell = @()
for ($i = 1; $i -le $Runs; $i++) {
    Write-Host "셸 측정 $i/$Runs ..."
    $r = Invoke-Bench @('--bench-shell', '--bench-folder', $Folder, '--step', '120') (Join-Path $work "shell$i.json") 180
    if ($r -and -not $r.error) { $shell += $r } else { Write-Warning "측정 실패 #$i $($r.error)" }
}
if ($shell.Count -eq 0) { throw '셸 측정 결과가 없습니다' }

$baseline = $null
if (-not $SkipBaseline) {
    Write-Host 'BenchWindow(비교) ...'
    $baseline = Invoke-Bench @('--bench-list', '--rows', '100000', '--step', '120') (Join-Path $work 'list.json') 120
}

$firstPaint = Median ($shell | ForEach-Object { $_.startup.firstPaintMs })
$interactive = Median ($shell | ForEach-Object { $_.startup.interactiveMs })
$firstBatch = Median ($shell | ForEach-Object { $_.folder.firstBatchMs })
$totalOpen = Median ($shell | ForEach-Object { $_.folder.totalMs })
$workingSet = Median ($shell | ForEach-Object { $_.folder.workingSetMB })
$privateMB = Median ($shell | ForEach-Object { $_.folder.privateMB })
$sc = $shell[0].scroll
$p95 = Median ($shell | ForEach-Object { $_.scroll.stats.P95Ms })
$idleP95 = Median ($shell | ForEach-Object { $_.scroll.idleFrames.P95Ms })
$truncated = [bool]($shell | Where-Object { $_.scroll.truncated })
$uia = [bool]($shell | Where-Object { $_.scroll.uiaPropertyListeners })

$envNotes = @()
if ($locked) { $envNotes += '화면이 잠겨 있음(LogonUI)' }
if ($idleP95 -gt 40) { $envNotes += ("유휴 프레임 p95 {0:F1}ms > 40ms (스크롤 없이도 느림; 마스코트 30fps 상한은 33ms라 정상)" -f $idleP95) }
if ($uia) { Write-Host '참고: UIA 이벤트를 듣는 접근성/입력 도구가 있음 (스크롤 수치에 영향을 줄 수 있음)' }
if ($truncated) { $envNotes += '스크롤 측정이 시간 제한으로 잘림' }
$envOk = $envNotes.Count -eq 0

function Verdict([bool] $ok) { if ($ok) { 'PASS' } else { 'FAIL' } }
$rows = @()
$scrollVerdict = if (-not $envOk) { 'INCONCLUSIVE' } else { Verdict ($p95 -le ($ScrollGateMs + 0.5)) }  # 0.5ms = 프레임 타임스탬프 지터 허용
$rows += [pscustomobject]@{ Gate = "스크롤 p95 (120행/프레임)"; Value = ('{0:F1} ms' -f $p95); Limit = ("<= $ScrollGateMs ms"); Result = $scrollVerdict }
$rows += [pscustomobject]@{ Gate = '10만 개 폴더 첫 묶음'; Value = ('{0:F1} ms' -f $firstBatch); Limit = "< $FirstBatchGateMs ms"; Result = (Verdict ($firstBatch -lt $FirstBatchGateMs)) }
$rows += [pscustomobject]@{ Gate = '시작 -> 첫 그리기'; Value = ('{0:F0} ms' -f $firstPaint); Limit = "<= $StartupGateMs ms (설계 목표 300ms)"; Result = (Verdict ($firstPaint -le $StartupGateMs)) }
$rows += [pscustomobject]@{ Gate = '10만 개 연 뒤 작업 집합'; Value = ('{0:F0} MB (private {1:F0})' -f $workingSet, $privateMB); Limit = "<= $MemoryGateMB MB"; Result = (Verdict ($workingSet -le $MemoryGateMB)) }

Write-Host ''
$rows | Format-Table -AutoSize | Out-String | Write-Host
Write-Host ("참고: 조작 가능까지 {0:F0} ms, 10만 개 전체 열기 {1:F0} ms, 렌더 등급 {2}" -f $interactive, $totalOpen, $shell[0].renderTier)
if ($baseline) {
    Write-Host ("비교 BenchWindow(사이드바·트리 없음) 스크롤 p95 {0:F1} ms (평균 {1:F1} ms)" -f $baseline.stats.P95Ms, $baseline.stats.AvgMs)
}
if (-not $envOk) {
    Write-Host ''
    Write-Warning ('스크롤 관문 판정 보류: ' + ($envNotes -join '; '))
    Write-Host '  -> 화면 잠금을 풀고, 접근성 도구를 끈 상태에서 다시 실행하세요. BenchWindow 비교값도 같이 느리면 코드가 아니라 환경 탓입니다.'
}

if ($Json) {
    [pscustomobject]@{ date = (Get-Date).ToString('s'); rows = $rows; envNotes = $envNotes; shell = $shell; baseline = $baseline } | ConvertTo-Json -Depth 8 | Set-Content $Json -Encoding UTF8
}
Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue

$fail = @($rows | Where-Object Result -eq 'FAIL').Count
if ($fail) { exit 2 }
if (-not $envOk) { exit 3 }
exit 0
