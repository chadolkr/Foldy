<#
  Store 판(MSIX)을 실제로 설치해서 색인 서비스가 패키지 서비스로 뜨는지 본다 (1.15.0, CI 전용: 관리자 권한 러너).
  1) payload(make-setup.ps1) → 2) make-msix.ps1 -TestCert (자체 서명) → 3) 인증서를 이 PC의 '신뢰할 수 있는 사용자'에 넣고 설치
  → 4) 서비스 FoldyIndexerStore가 실행 중이 되고, 찾기 통로(이름 있는 파이프)가 열리는지 → 5) 지우고 인증서도 지운다.
  실제 사용자 PC에서는 Store가 Microsoft 인증서로 서명해서 설치한다.
#>
param([string] $Version = '0.0.1')
$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$out = Join-Path ([IO.Path]::GetTempPath()) ('foldy-msix-test-' + [guid]::NewGuid().ToString('N'))
$pkgName = 'DavidCHA.FoldyFileManager'
$thumb = $null
try {
    if (-not (Test-Path (Join-Path $root 'dist\Foldy-Setup\payload\foldy-index.exe'))) {
        & (Join-Path $root 'build\setup\make-setup.ps1') -SkipRust -Version $Version
    }

    $before = @(Get-ChildItem Cert:\CurrentUser\My | ForEach-Object Thumbprint)
    & (Join-Path $PSScriptRoot 'make-msix.ps1') -Version $Version -OutDir $out -TestCert -NoService:$false
    $cert = Get-ChildItem Cert:\CurrentUser\My | Where-Object { $before -notcontains $_.Thumbprint } | Select-Object -First 1
    if (-not $cert) { throw '시험 인증서를 찾지 못함' }
    $thumb = $cert.Thumbprint
    $store = [Security.Cryptography.X509Certificates.X509Store]::new('TrustedPeople', 'LocalMachine')
    $store.Open('ReadWrite'); $store.Add($cert); $store.Close()

    $msix = Get-ChildItem $out -Filter *.msix | Select-Object -First 1
    Add-AppxPackage -Path $msix.FullName
    $pkg = Get-AppxPackage -Name $pkgName
    if (-not $pkg) { throw '패키지가 설치되지 않음' }
    Write-Host "설치됨: $($pkg.PackageFullName)"

    $svc = $null
    foreach ($i in 1..60) {
        $svc = Get-Service FoldyIndexerStore -ErrorAction SilentlyContinue
        if ($svc -and $svc.Status -ne 'Running' -and $i -eq 5) { Start-Service FoldyIndexerStore -ErrorAction SilentlyContinue } # 자동 시작이 늦으면 한 번 깨운다
        if ($svc -and $svc.Status -eq 'Running') { break }
        Start-Sleep -Seconds 1
    }
    if (-not $svc) { throw '서비스 FoldyIndexerStore가 등록되지 않음' }
    sc.exe qc FoldyIndexerStore
    if ($svc.Status -ne 'Running') { sc.exe query FoldyIndexerStore; throw "서비스가 실행되지 않음: $($svc.Status)" }

    $pipe = [IO.Pipes.NamedPipeClientStream]::new('.', 'Foldy.Indexer.v1', [IO.Pipes.PipeDirection]::InOut)
    try { $pipe.Connect(20000) } catch { throw "찾기 통로(파이프)에 연결하지 못함: $($_.Exception.Message)" }
    $pipe.Dispose()
    Write-Host '패키지 서비스 확인: 설치 → FoldyIndexerStore 실행 중 → 찾기 통로 열림'
}
finally {
    Get-AppxPackage -Name $pkgName -ErrorAction SilentlyContinue | Remove-AppxPackage -ErrorAction SilentlyContinue
    if ($thumb) {
        foreach ($loc in 'Cert:\LocalMachine\TrustedPeople', 'Cert:\CurrentUser\My') { Remove-Item (Join-Path $loc $thumb) -ErrorAction SilentlyContinue }
    }
    Remove-Item $out -Recurse -Force -ErrorAction SilentlyContinue
}
