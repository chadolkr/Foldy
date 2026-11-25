<#
  Store 판(MSIX)을 Partner Center에 새 제출로 올린다 (.github/workflows/store.yml이 부른다, docs/store.md "PC 없이 제출").
  1) 패키지가 Foldy 것이고 버전이 태그와 같은지 본다.
  2) 패키지를 올려 초안을 만든다 (msstore publish --noCommit: 인증에 내지 않음).
  3) 5개 언어의 "이 버전의 새로운 기능"을 docs/store-listing 글로 바꾼다.
  4) -Mode submit일 때만 인증에 낸다. 앞의 어느 단계든 안 되면 내지 않는다.

  인증 값은 환경 변수로만 받는다: STORE_TENANT_ID, STORE_SELLER_ID, STORE_CLIENT_ID, STORE_CLIENT_SECRET.
  -CheckOnly: 1)과 글만 확인하고 끝난다 (네트워크·인증 값 필요 없음).
#>
param(
    [Parameter(Mandatory)] [string] $Msix,
    [Parameter(Mandatory)] [string] $Version,
    [ValidateSet('draft', 'submit')] [string] $Mode = 'draft',
    [string] $ProductId = '9NS1PWL5QGP6',
    [string] $Identity = 'DavidCHA.FoldyFileManager',
    [string] $ListingDir = (Join-Path $PSScriptRoot '../../docs/store-listing'),
    [switch] $CheckOnly
)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false

# Partner Center 언어 → 등록 정보 파일. 글은 파일의 세 번째 "## " 칸(이 버전의 새로운 기능)
$languages = [ordered]@{ 'ko-kr' = 'ko.md'; 'en-us' = 'en.md'; 'es-es' = 'es.md'; 'ja-jp' = 'ja.md'; 'zh-cn' = 'zh-cn.md' }
$noteLimit = 1500 # Store의 "새로운 기능" 글자 수 제한

function Get-ReleaseNotes([string] $file) {
    $text = (Get-Content -LiteralPath $file -Raw -Encoding UTF8) -replace "`r`n", "`n"
    $sections = [regex]::Split($text, '(?m)^## ')
    if ($sections.Count -lt 4) { throw "등록 정보 파일 모양이 다름: $file" }
    $body = ($sections[3] -split "`n", 2)[1].Trim()
    if ($body.Length -eq 0) { throw "새로운 기능 글이 비어 있음: $file" }
    if ($body.Length -gt $noteLimit) { throw "새로운 기능 글이 $noteLimit자를 넘음 ($($body.Length)자): $file" }
    return $body
}

# 1) 패키지 확인
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [System.IO.Compression.ZipFile]::OpenRead((Resolve-Path -LiteralPath $Msix).Path)
try {
    $entry = $zip.GetEntry('AppxManifest.xml')
    if (-not $entry) { throw 'AppxManifest.xml이 없음 (MSIX가 아님)' }
    $reader = [System.IO.StreamReader]::new($entry.Open())
    try { [xml] $manifest = $reader.ReadToEnd() } finally { $reader.Dispose() }
}
finally { $zip.Dispose() }
$id = $manifest.Package.Identity
if ($id.Name -ne $Identity) { throw "패키지 Identity가 다름: $($id.Name) (기대: $Identity)" }
if ($id.Version -ne "$Version.0") { throw "패키지 버전이 다름: $($id.Version) (기대: $Version.0)" }
Write-Host "패키지 확인: $($id.Name) $($id.Version)"

$notes = [ordered]@{}
foreach ($lang in $languages.Keys) {
    $notes[$lang] = Get-ReleaseNotes (Join-Path $ListingDir $languages[$lang])
    Write-Host "새로운 기능 글 $lang : $($notes[$lang].Length)자"
}
if ($CheckOnly) { Write-Host '확인만 하고 끝냄'; return }

foreach ($name in 'STORE_TENANT_ID', 'STORE_SELLER_ID', 'STORE_CLIENT_ID', 'STORE_CLIENT_SECRET') {
    $value = [Environment]::GetEnvironmentVariable($name)
    if (-not $value -or -not $value.Trim()) { throw "$name 시크릿이 없음 (docs/store.md)" }
    $trimmed = $value.Trim()
    if ($trimmed -cne $value) {
        # 휴대폰에서 붙여 넣으면 빈칸·줄바꿈이 따라온다. 다듬은 값도 로그에서 가린다
        Write-Host "$name 앞뒤의 빈칸을 빼고 써요"
        if ($env:GITHUB_ACTIONS) { Write-Host "::add-mask::$trimmed" }
        [Environment]::SetEnvironmentVariable($name, $trimmed)
    }
}

# 값의 모양을 먼저 본다 (값은 절대 찍지 않는다). 비슷하게 생긴 다른 값을 넣기 쉽다
$guid = '^[0-9a-fA-F]{8}(-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12}$'
$wrong = @()
if ($env:STORE_TENANT_ID -notmatch $guid) { $wrong += 'STORE_TENANT_ID: 디렉터리(테넌트) ID 모양이 아님' }
if ($env:STORE_CLIENT_ID -notmatch $guid) { $wrong += 'STORE_CLIENT_ID: 애플리케이션(클라이언트) ID 모양이 아님' }
if ($env:STORE_SELLER_ID -notmatch '^\d{1,9}$') { $wrong += 'STORE_SELLER_ID: 판매자 ID(숫자)가 아님' }
if ($env:STORE_CLIENT_SECRET -match $guid) { $wrong += 'STORE_CLIENT_SECRET: "비밀 ID"로 보임. "값"을 넣어야 함' }
if ($env:STORE_TENANT_ID -eq $env:STORE_CLIENT_ID) { $wrong += 'STORE_TENANT_ID와 STORE_CLIENT_ID가 같음' }
if ($wrong.Count -gt 0) { throw "시크릿 값의 모양이 다름`n  $($wrong -join "`n  ")" }

# 토큰을 직접 받아 본다. msstore는 까닭을 알려 주지 않아서 오류 번호(AADSTS…)만 골라 찍는다 (설명에는 ID가 들어 있다)
$hints = @{
    'AADSTS7000215' = 'STORE_CLIENT_SECRET이 맞지 않음 ("비밀 ID"가 아니라 "값"인지, 다른 앱의 값이 아닌지)'
    'AADSTS7000222' = 'STORE_CLIENT_SECRET 기한이 지남 (새 비밀을 만들어 바꾸기)'
    'AADSTS700016'  = 'STORE_CLIENT_ID의 앱이 STORE_TENANT_ID 테넌트에 없음 (개체 ID가 아니라 클라이언트 ID인지)'
    'AADSTS90002'   = 'STORE_TENANT_ID의 테넌트가 없음'
    'AADSTS500011'  = '테넌트에 이 API가 아직 없음 (앱 값의 문제는 아님)'
}
function Get-StoreToken([string] $resource) {
    $answer = Invoke-WebRequest -Method Post -Uri "https://login.microsoftonline.com/$env:STORE_TENANT_ID/oauth2/v2.0/token" -SkipHttpErrorCheck -Body @{
        grant_type = 'client_credentials'; client_id = $env:STORE_CLIENT_ID; client_secret = $env:STORE_CLIENT_SECRET; scope = "$resource/.default"
    }
    if ($answer.StatusCode -eq 200) { return ($answer.Content | ConvertFrom-Json).access_token }
    $code = [regex]::Match([string] $answer.Content, 'AADSTS\d+').Value
    throw "로그인 실패 ($resource): HTTP $($answer.StatusCode) $code $(if ($code -and $hints.ContainsKey($code)) { $hints[$code] })"
}
$token = Get-StoreToken 'https://manage.devcenter.microsoft.com'
$null = Get-StoreToken 'https://api.store.microsoft.com'
Write-Host '로그인 확인'

# 이 앱을 볼 수 있는 권한인지 (읽기만, 아무것도 바꾸지 않는다)
$answer = Invoke-WebRequest -Uri "https://manage.devcenter.microsoft.com/v1.0/my/applications/$ProductId" -Headers @{ Authorization = "Bearer $token" } -SkipHttpErrorCheck
if ($answer.StatusCode -ne 200) { throw "앱 $ProductId 을(를) 읽지 못함: HTTP $($answer.StatusCode) (파트너 센터에서 이 앱의 역할이 관리자(Windows)인지)" }
$pending = ($answer.Content | ConvertFrom-Json).pendingApplicationSubmission
Write-Host "앱 확인: $ProductId, 열려 있는 초안 $(if ($pending) { '있음 (새로 만들면 지워져요)' } else { '없음' })"

function Invoke-Store([string] $what, [scriptblock] $run) {
    $out = & $run
    if ($LASTEXITCODE -ne 0) { throw "$what 실패 (exit $LASTEXITCODE)" }
    return $out
}

# 2) 초안 만들기 (이미 열린 초안이 있으면 마지막으로 공개된 제출에서 새로 만든다)
$null = Invoke-Store '인증 설정' { msstore reconfigure --tenantId $env:STORE_TENANT_ID --sellerId $env:STORE_SELLER_ID --clientId $env:STORE_CLIENT_ID --clientSecret $env:STORE_CLIENT_SECRET }
Invoke-Store '패키지 올리기' { msstore publish $Msix -id $ProductId --noCommit } | Out-Host

# 3) 새로운 기능 글. 명령줄 길이 제한(약 32,000자)을 넘으면 글은 사람이 고치도록 초안으로 남긴다
$raw = (Invoke-Store '초안 읽기' { msstore submission get $ProductId }) -join "`n"
$start = $raw.IndexOf('{')
if ($start -lt 0) { throw '초안 내용을 알아볼 수 없음. 초안은 남아 있으니 Partner Center에서 확인해 주세요' }
$submission = $raw.Substring($start, $raw.LastIndexOf('}') - $start + 1) | ConvertFrom-Json -Depth 100
$missing = @()
foreach ($lang in $notes.Keys) {
    $listing = $submission.Listings.PSObject.Properties | Where-Object { $_.Name -eq $lang } | Select-Object -First 1
    if ($listing) { $listing.Value.BaseListing | Add-Member -NotePropertyName ReleaseNotes -NotePropertyValue $notes[$lang] -Force } else { $missing += $lang }
}
$json = $submission | ConvertTo-Json -Depth 100 -Compress
$notesDone = $false
if ($missing.Count -gt 0) { Write-Warning "등록 정보에 없는 언어: $($missing -join ', ')" }
elseif ($json.Length -gt 30000) { Write-Warning "초안이 커서($($json.Length)자) 글을 자동으로 바꾸지 못함" }
else {
    Invoke-Store '글 바꾸기' { msstore submission update $ProductId $json } | Out-Host
    $notesDone = $true
}

$summary = "Foldy $Version 패키지로 초안을 만들었어요."
if (-not $notesDone) { $summary += ' "이 버전의 새로운 기능" 글은 바꾸지 못했어요. Partner Center에서 고친 뒤 제출해 주세요.' }

# 4) 인증에 내기
if ($Mode -eq 'submit' -and $notesDone) {
    Invoke-Store '인증에 내기' { msstore submission publish $ProductId } | Out-Host
    $summary = "Foldy $Version 를 인증에 냈어요. 통과하면 Store에 공개돼요."
}
elseif ($Mode -eq 'submit') { $summary += ' 그래서 인증에 내지 않았어요.' }
else { $summary += ' 초안만 만들었고 인증에 내지 않았어요. Partner Center에서 확인하고 제출하거나, mode를 submit으로 다시 실행해 주세요.' }
Write-Host $summary
if ($env:GITHUB_STEP_SUMMARY) { Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY -Value $summary -Encoding utf8 }
if ($Mode -eq 'submit' -and -not $notesDone) { exit 1 }
