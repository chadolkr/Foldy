<#
  Store 판(MSIX)을 Partner Center에 새 제출로 올린다 (.github/workflows/store.yml이 부른다, docs/store.md "PC 없이 제출").
  1) 패키지가 Foldy 것이고 버전이 태그와 같은지 본다.
  2) 패키지를 올려 초안을 만든다 (msstore publish --noCommit: 인증에 내지 않음).
     -Mode text-draft·text-submit이면 패키지 없이, 마지막으로 공개된 제출을 그대로 복사한 초안을 만든다 (설명 글만 고칠 때).
  3) 5개 언어 등록 정보(설명, 새로운 기능, 제품 기능, 짧은 설명, 검색어, 웹 사이트·지원 주소)를 docs/store-listing 글로 바꾼다.
  4) -Mode submit·text-submit일 때만 인증에 낸다. 앞의 어느 단계든 안 되면 내지 않는다.

  인증 값은 환경 변수로만 받는다: STORE_TENANT_ID, STORE_SELLER_ID, STORE_CLIENT_ID, STORE_CLIENT_SECRET.
  -CheckOnly: 1)과 글만 확인하고 끝난다 (네트워크·인증 값 필요 없음). CI가 글 모양을 이걸로 본다.
#>
param(
    [string] $Msix,
    [string] $Version,
    [ValidateSet('draft', 'submit', 'text-draft', 'text-submit')] [string] $Mode = 'draft',
    [string] $ProductId = '9NS1PWL5QGP6',
    [string] $Identity = 'DavidCHA.FoldyFileManager',
    [string] $ListingDir = (Join-Path $PSScriptRoot '../../docs/store-listing'),
    [switch] $CheckOnly
)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$textOnly = $Mode -like 'text-*'
$commit = $Mode -in 'submit', 'text-submit'
if (-not $textOnly -and (-not $Msix -or -not $Version)) { throw "-Mode $Mode 에는 -Msix와 -Version이 필요함" }

# Partner Center 언어 → 등록 정보 파일과 소개 페이지 (site/src/i18n/index.ts의 언어별 주소)
$site = 'https://chadolkr.github.io/Foldy/'
$languages = [ordered]@{
    'ko-kr' = @{ File = 'ko.md'; Site = $site }
    'en-us' = @{ File = 'en.md'; Site = "${site}en/" }
    'es-es' = @{ File = 'es.md'; Site = "${site}es/" }
    'ja-jp' = @{ File = 'ja.md'; Site = "${site}ja/" }
    'zh-cn' = @{ File = 'zh-cn.md'; Site = "${site}zh-cn/" }
}
$support = 'https://github.com/chadolkr/Foldy/issues'

# 파일의 "## " 칸 순서: 1 제품 이름, 2 설명, 3 새로운 기능, 4 제품 기능, 5 짧은 설명, 6 검색어 (뒤는 사람이 보는 참고)
# 글자 수 제한은 Partner Center 등록 정보 안내를 따른다.
function Get-Listing([string] $file) {
    $text = (Get-Content -LiteralPath $file -Raw -Encoding UTF8) -replace "`r`n", "`n"
    $sections = [regex]::Split($text, '(?m)^## ')
    if ($sections.Count -lt 7) { throw "등록 정보 파일 모양이 다름: $file" }
    $body = { param($i) ($sections[$i] -split "`n", 2)[1].Trim() }
    $lines = { param($i) @((& $body $i) -split "`n" | ForEach-Object { $_.Trim() } | Where-Object { $_ }) }
    $listing = [ordered]@{
        description      = & $body 2
        releaseNotes     = & $body 3
        features         = @(& $lines 4 | ForEach-Object { $_ -replace '^-\s*', '' })
        shortDescription = & $body 5
        keywords         = @(& $lines 6)
    }
    $problems = @()
    foreach ($k in 'description', 'releaseNotes', 'shortDescription') { if (-not $listing[$k]) { $problems += "$k 비어 있음" } }
    if ($listing.description.Length -gt 10000) { $problems += "설명이 10,000자를 넘음 ($($listing.description.Length)자)" }
    if ($listing.description -notmatch [regex]::Escape($site)) { $problems += '설명에 소개 페이지 주소가 없음' }
    if ($listing.releaseNotes.Length -gt 1500) { $problems += "새로운 기능이 1,500자를 넘음 ($($listing.releaseNotes.Length)자)" }
    if ($listing.shortDescription.Length -gt 1000) { $problems += "짧은 설명이 1,000자를 넘음 ($($listing.shortDescription.Length)자)" }
    if ($listing.features.Count -lt 1 -or $listing.features.Count -gt 20) { $problems += "제품 기능이 1~20개가 아님 ($($listing.features.Count)개)" }
    $listing.features | Where-Object { $_.Length -gt 200 } | ForEach-Object { $problems += "제품 기능 한 줄이 200자를 넘음: $_" }
    if ($listing.keywords.Count -lt 1 -or $listing.keywords.Count -gt 7) { $problems += "검색어가 1~7개가 아님 ($($listing.keywords.Count)개)" }
    $listing.keywords | Where-Object { $_.Length -gt 30 } | ForEach-Object { $problems += "검색어가 30자를 넘음: $_" }
    $words = ($listing.keywords | ForEach-Object { @($_ -split '\s+').Count } | Measure-Object -Sum).Sum
    if ($words -gt 21) { $problems += "검색어 낱말이 모두 21개를 넘음 ($words개)" }
    if ($problems.Count -gt 0) { throw "등록 정보 글 확인 실패: $file`n  $($problems -join "`n  ")" }
    return $listing
}

# 1) 패키지 확인 (글만 고칠 때는 패키지가 없다)
if (-not $textOnly) {
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
}

$listings = [ordered]@{}
foreach ($lang in $languages.Keys) {
    $l = Get-Listing (Join-Path $ListingDir $languages[$lang].File)
    $listings[$lang] = $l
    Write-Host "등록 정보 $lang : 설명 $($l.description.Length)자 $(@($l.description -split "`n").Count)줄, 새로운 기능 $($l.releaseNotes.Length)자, 기능 $($l.features.Count)개, 검색어 $($l.keywords.Count)개"
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

# 공개 로그에 초안을 바꿀 수 있는 주소(FileUploadUrl의 SAS 서명)가 남지 않게 한다.
# 초안 JSON(msstore 출력, API 응답)은 절대 그대로 내보내지 않는다
function Hide-Secret([string] $line) {
    if ($line -match 'FileUploadUrl|blob\.core\.windows\.net|[?&]sig=|skoid') { return '(가림: 올리기 주소)' }
    return $line
}
function Invoke-Store([string] $what, [scriptblock] $run) {
    $all = @(& $run 2>&1)
    if ($LASTEXITCODE -ne 0) {
        # 까닭을 볼 수 있게 끝의 짧은 줄만. 초안 JSON은 빼고 주소는 가린다
        $text = @($all | ForEach-Object { "$_" })
        if ($text -match 'FileUploadUrl') { $text = @('(초안 내용이 들어 있어 찍지 않음)') } # 줄이 나뉘어도 새지 않게
        $text | Where-Object { $_ -notmatch '^\s*["{}\[\]]' } | Select-Object -Last 15 | ForEach-Object { Write-Host (Hide-Secret $_) }
        throw "$what 실패 (exit $LASTEXITCODE)"
    }
    return @($all | Where-Object { $_ -isnot [System.Management.Automation.ErrorRecord] } | ForEach-Object { "$_" }) # 오류 줄이 JSON에 섞이지 않게
}
function Show-Store([string[]] $lines) { $lines | ForEach-Object { Write-Host (Hide-Secret $_) } }

# Partner Center 제출 API (읽기·쓰기). 응답 본문은 찍지 않는다: 초안에는 올리기 주소(SAS)가 들어 있다
$api = "https://manage.devcenter.microsoft.com/v1.0/my/applications/$ProductId"
function Invoke-Dev([string] $what, [string] $method, [string] $path, [string] $body) {
    $call = @{ Method = $method; Uri = "$api$path"; Headers = @{ Authorization = "Bearer $token" }; SkipHttpErrorCheck = $true }
    if ($null -ne $body) { $call.Body = [Text.Encoding]::UTF8.GetBytes($body); $call.ContentType = 'application/json; charset=utf-8' }
    $answer = Invoke-WebRequest @call
    if ($answer.StatusCode -ge 300) {
        $why = try { $e = ([string] $answer.Content | ConvertFrom-Json); "$($e.code) $($e.message) $(@($e.validationErrors | ForEach-Object { "$($_.target): $($_.message)" }) -join '; ')" } catch { '' }
        throw "$what 실패: HTTP $($answer.StatusCode) $(Hide-Secret $why)"
    }
    if (-not $answer.Content) { return $null }
    $data = [string] $answer.Content | ConvertFrom-Json -Depth 100
    if ($env:GITHUB_ACTIONS -and $data.fileUploadUrl) { Write-Host "::add-mask::$($data.fileUploadUrl)" }
    return $data
}

# 있는 칸만 바꾼다 (API가 모르는 칸을 더하면 거절된다). 이름의 대소문자는 응답을 따른다
function Set-Field($target, [string] $name, $value) {
    $prop = $target.PSObject.Properties | Where-Object { $_.Name -eq $name } | Select-Object -First 1
    if ($prop) { $prop.Value = $value; return $true }
    return $false
}

# 2) 초안 만들기
if ($textOnly) {
    # 글만: 열린 초안을 지우고, 마지막으로 공개된 제출(패키지 포함)을 복사한 새 초안을 만든다
    if ($pending) {
        $null = Invoke-Dev '열린 초안 지우기' Delete "/submissions/$($pending.id)" $null
        Write-Host '열려 있던 초안을 지움'
    }
    $draft = Invoke-Dev '초안 만들기' Post '/submissions' ''
    $draftId = $draft.id
}
else {
    # 패키지: msstore가 열린 초안을 지우고 새 초안에 패키지를 올린다
    $null = Invoke-Store '인증 설정' { msstore reconfigure --tenantId $env:STORE_TENANT_ID --sellerId $env:STORE_SELLER_ID --clientId $env:STORE_CLIENT_ID --clientSecret $env:STORE_CLIENT_SECRET }
    Show-Store (Invoke-Store '패키지 올리기' { msstore publish $Msix -id $ProductId --noCommit })
    $app = Invoke-Dev '앱 다시 읽기' Get '' $null
    if (-not $app.pendingApplicationSubmission) { throw '패키지를 올린 초안을 찾지 못함' }
    $draftId = $app.pendingApplicationSubmission.id
}
Write-Host "초안 $draftId"

# 3) 등록 정보 글
$submission = Invoke-Dev '초안 읽기' Get "/submissions/$draftId" $null
foreach ($package in $submission.applicationPackages) { Write-Host "패키지 $($package.fileName) : $($package.fileStatus)" }
Write-Host "가격 $($submission.pricing.priceId), 분류 $($submission.applicationCategory), 언어 $(@($submission.listings.PSObject.Properties.Name) -join ', ')"
$missing = @()
foreach ($lang in $listings.Keys) {
    $entry = $submission.listings.PSObject.Properties | Where-Object { $_.Name -eq $lang } | Select-Object -First 1
    if (-not $entry) { $missing += $lang; continue }
    $base = $entry.Value.baseListing
    $text = $listings[$lang]
    foreach ($field in 'description', 'releaseNotes', 'shortDescription') {
        if (-not (Set-Field $base $field $text[$field])) { $base | Add-Member -NotePropertyName $field -NotePropertyValue $text[$field] }
    }
    foreach ($field in 'features', 'keywords') {
        if (-not (Set-Field $base $field ([string[]] $text[$field]))) { $base | Add-Member -NotePropertyName $field -NotePropertyValue ([string[]] $text[$field]) }
    }
    $null = Set-Field $base 'websiteUrl' $languages[$lang].Site
    $null = Set-Field $base 'supportContact' $support
}
# 인증 담당자에게 남기는 글: 패키지에 색인 서비스(제한 기능 packagedServices·localSystemServices)가 있으면 그 까닭 (1.15.0)
if (-not $textOnly) {
    $notes = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'certification-notes.txt')).Trim()
    if (-not (Set-Field $submission 'notesForCertification' $notes)) { $submission | Add-Member -NotePropertyName 'notesForCertification' -NotePropertyValue $notes }
    Write-Host "인증 담당자용 글을 넣음 ($($notes.Length)자)"
}
if ($missing.Count -gt 0) { throw "초안 등록 정보에 없는 언어: $($missing -join ', '). 초안은 남아 있어요" }
$null = Invoke-Dev '글 바꾸기' Put "/submissions/$draftId" ($submission | ConvertTo-Json -Depth 100 -Compress)
Write-Host '등록 정보 글을 바꿈 (설명, 새로운 기능, 제품 기능, 짧은 설명, 검색어, 웹 사이트)'

$summary = if ($textOnly) { 'Store 설명 글을 바꾼 초안을 만들었어요 (패키지는 지금 공개된 것 그대로).' } else { "Foldy $Version 패키지와 설명 글로 초안을 만들었어요." }

# 4) 인증에 내기
if ($commit) {
    $result = Invoke-Dev '인증에 내기' Post "/submissions/$draftId/commit" ''
    Write-Host "제출 상태: $($result.status)"
    $summary = if ($textOnly) { 'Store 설명 글을 인증에 냈어요. 통과하면 Store에 보여요.' } else { "Foldy $Version 를 인증에 냈어요. 통과하면 Store에 공개돼요." }
}
else {
    $again = if ($textOnly) { 'text-submit' } else { 'submit' }
    $summary += " 인증에는 내지 않았어요. Partner Center에서 보기만 하고(고치면 API로 제출할 수 없어요), 괜찮으면 mode를 $again 으로 다시 실행해 주세요."
}
Write-Host $summary
if ($env:GITHUB_STEP_SUMMARY) { Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY -Value $summary -Encoding utf8 }
