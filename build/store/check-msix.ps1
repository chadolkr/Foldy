<#
  Store 판(MSIX) 매니페스트를 CI에서 미리 확인한다 (1.15.0). 실제 패키지는 릴리스 때만 만들어서, 매니페스트가 틀리면
  릴리스의 "Store MSIX" 단계에서야 알게 되고 Store 제출이 멈춘다. 여기서는 가짜 exe(Windows의 cmd.exe 사본)로
  같은 스크립트(make-msix.ps1)를 돌려 makeappx가 스키마와 매니페스트가 가리키는 파일(Foldy.exe, startupTask의 foldy-hotkey.exe)을 검사하게 한다.
#>
$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$fake = Join-Path ([IO.Path]::GetTempPath()) ('foldy-msix-check-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $fake | Out-Null
try {
    # 진짜 payload에 들어가는 exe 이름과 같아야 한다 (make-setup.ps1). 빠지면 Store 판에서 그 기능이 사라진다.
    $setup = Get-Content (Join-Path $root 'build\setup\make-setup.ps1') -Raw
    foreach ($exe in 'Foldy.exe', 'foldy-hotkey.exe') {
        if ($exe -ne 'Foldy.exe' -and $setup -notmatch [regex]::Escape($exe)) { throw "make-setup.ps1이 $exe 를 payload에 넣지 않습니다" }
        Copy-Item "$env:SystemRoot\System32\cmd.exe" (Join-Path $fake $exe)
    }
    foreach ($exe in 'foldy-index.exe', 'foldy-update.exe') { Copy-Item "$env:SystemRoot\System32\cmd.exe" (Join-Path $fake $exe) }

    $out = Join-Path $fake 'out'
    & (Join-Path $PSScriptRoot 'make-msix.ps1') -Version '0.0.1' -OutDir $out -Payload $fake

    $stage = Join-Path $root 'dist\Foldy-Store'
    foreach ($gone in 'foldy-index.exe', 'foldy-update.exe') {
        if (Test-Path (Join-Path $stage $gone)) { throw "Store 패키지에 $gone 이 들어갔습니다 (넣지 않아야 함)" }
    }
    [xml] $m = Get-Content (Join-Path $stage 'AppxManifest.xml') -Raw
    $ns = [Xml.XmlNamespaceManager]::new($m.NameTable)
    $ns.AddNamespace('d', 'http://schemas.microsoft.com/appx/manifest/desktop/windows10')
    $task = $m.SelectSingleNode('//d:Extension[@Category="windows.startupTask"]', $ns)
    if (-not $task -or $task.Executable -ne 'foldy-hotkey.exe') { throw 'startupTask가 foldy-hotkey.exe를 가리키지 않습니다' }
    Write-Host 'Store 매니페스트 확인: makeappx 통과, startupTask = foldy-hotkey.exe, 색인 서비스·업데이트 도구 없음'
}
finally {
    Remove-Item $fake -Recurse -Force -ErrorAction SilentlyContinue
}
