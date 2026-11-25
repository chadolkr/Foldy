<#
.SYNOPSIS
  Microsoft Store에 새 패키지를 제출한다 (Microsoft Store 제출 API, docs/store.md "PC 없이 제출").

.DESCRIPTION
  사용자 2026-11-25: "모바일로 모든걸 해야하는 환경이야". 휴대폰 GitHub에서 [Run workflow]를 누르면
  store.yml이 이 스크립트를 돌린다. Partner Center 웹에서 하던 일과 같다:
    1. 새 제출을 만든다 (지난번 등록 정보·가격·시장은 그대로 복사된다)
    2. 예전 패키지는 지우고 새 .msix를 올린다
    3. build/store/whats-new.json이 있으면 언어마다 "이 버전의 새로운 기능"을 바꾼다
    4. 인증에 제출한다
  이미 진행 중인 제출이 있으면 아무것도 바꾸지 않고 멈춘다 (사람이 Partner Center에서 하던 것을 지우지 않게).

  필요한 시크릿 (Partner Center → 계정 설정 → 사용자 관리 → Microsoft Entra 애플리케이션, 역할 "관리자"):
    STORE_TENANT_ID, STORE_CLIENT_ID, STORE_CLIENT_SECRET
#>
param(
    [Parameter(Mandatory)] [string]$Msix,
    [string]$AppId = '9NS1PWL5QGP6',
    [string]$WhatsNew = (Join-Path $PSScriptRoot 'whats-new.json'),
    [int]$WaitMinutes = 10
)

$ErrorActionPreference = 'Stop'
$tenant = $env:STORE_TENANT_ID
$client = $env:STORE_CLIENT_ID
$secret = $env:STORE_CLIENT_SECRET
if (-not $tenant -or -not $client -or -not $secret) { throw '시크릿 STORE_TENANT_ID·STORE_CLIENT_ID·STORE_CLIENT_SECRET이 필요해요 (docs/store.md)' }
if (-not (Test-Path $Msix)) { throw "패키지가 없어요: $Msix" }

$api = "https://manage.devcenter.microsoft.com/v1.0/my/applications/$AppId"

$token = (Invoke-RestMethod -Method Post -Uri "https://login.microsoftonline.com/$tenant/oauth2/token" -ContentType 'application/x-www-form-urlencoded' -Body @{
        grant_type    = 'client_credentials'
        client_id     = $client
        client_secret = $secret
        resource      = 'https://manage.devcenter.microsoft.com'
    }).access_token
$headers = @{ Authorization = "Bearer $token" }

function Api([string]$method, [string]$path, $body = $null) {
    $req = @{ Method = $method; Uri = "$api$path"; Headers = $headers; ContentType = 'application/json; charset=utf-8' }
    if ($null -ne $body) { $req.Body = [Text.Encoding]::UTF8.GetBytes(($body | ConvertTo-Json -Depth 30)) }
    Invoke-RestMethod @req
}

$app = Api Get ''
if ($app.pendingApplicationSubmission) {
    throw "이미 진행 중인 제출이 있어요 (id $($app.pendingApplicationSubmission.id)). Partner Center에서 그 제출을 끝내거나 지운 뒤 다시 눌러 주세요."
}

Write-Host '새 제출 만드는 중'
$sub = Api Post '/submissions'

$name = Split-Path $Msix -Leaf
foreach ($p in @($sub.applicationPackages)) { $p.fileStatus = 'PendingDelete' }
$sub.applicationPackages = @($sub.applicationPackages) + [pscustomobject]@{ fileName = $name; fileStatus = 'PendingUpload'; minimumDirectXVersion = 'None'; minimumSystemRam = 'None' }

if (Test-Path $WhatsNew) {
    $notes = Get-Content $WhatsNew -Raw -Encoding UTF8 | ConvertFrom-Json
    foreach ($l in $sub.listings.PSObject.Properties) {
        # Partner Center 언어 이름(ko-kr, en-us, zh-cn …)을 whats-new.json 키(ko, en, es, ja, zh-cn)에 맞춘다
        $key = $l.Name.ToLowerInvariant()
        $text = $notes.$key
        if (-not $text) { $text = $notes.($key.Split('-')[0]) }
        if ($text) { $l.Value.baseListing.releaseNotes = $text; Write-Host "새로운 기능 글: $($l.Name)" }
    }
}

Api Put "/submissions/$($sub.id)" $sub | Out-Null

Write-Host "패키지 올리는 중: $name"
$zip = Join-Path ([IO.Path]::GetTempPath()) 'foldy-store-upload.zip'
if (Test-Path $zip) { Remove-Item $zip }
Compress-Archive -Path $Msix -DestinationPath $zip
$upload = $sub.fileUploadUrl.Replace('+', '%2B') # 문서에 적힌 알려진 문제: SAS 주소의 +
Invoke-RestMethod -Method Put -Uri $upload -Headers @{ 'x-ms-blob-type' = 'BlockBlob' } -InFile $zip -ContentType 'application/octet-stream' | Out-Null

Write-Host '인증에 제출하는 중'
Api Post "/submissions/$($sub.id)/commit" | Out-Null

$until = (Get-Date).AddMinutes($WaitMinutes)
do {
    Start-Sleep -Seconds 20
    $st = Api Get "/submissions/$($sub.id)/status"
    Write-Host "상태: $($st.status)"
} while ($st.status -eq 'CommitStarted' -and (Get-Date) -lt $until)

if ($st.status -eq 'CommitFailed') {
    $st.statusDetails | ConvertTo-Json -Depth 10 | Write-Host
    throw '제출이 거절됐어요 (위 내용). Partner Center에서 이 제출을 열어 보면 같은 내용이 있어요.'
}

Write-Host "제출했어요 (id $($sub.id), 상태 $($st.status)). 인증은 Microsoft가 하고 보통 1~3일 걸려요."
