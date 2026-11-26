<#
  Store 판(MSIX) 매니페스트를 CI에서 미리 확인한다 (1.15.0). 실제 패키지는 릴리스 때만 만들어서, 매니페스트가 틀리면
  릴리스의 "Store MSIX" 단계에서야 알게 되고 Store 제출이 멈춘다. 여기서는 가짜 exe(Windows의 cmd.exe 사본)로
  같은 스크립트(make-msix.ps1)를 돌려 makeappx가 스키마와 매니페스트가 가리키는 파일(Foldy.exe, startupTask의 foldy-hotkey.exe,
  서비스의 foldy-index.exe)을 검사하게 한다. 서비스를 넣은 판과 뺀 판(-NoService) 둘 다 본다.
#>
$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$fake = Join-Path ([IO.Path]::GetTempPath()) ('foldy-msix-check-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $fake | Out-Null
try {
    # 진짜 payload에 들어가는 exe 이름과 같아야 한다 (make-setup.ps1). 빠지면 Store 판에서 그 기능이 사라진다.
    $setup = Get-Content (Join-Path $root 'build\setup\make-setup.ps1') -Raw
    foreach ($exe in 'Foldy.exe', 'foldy-hotkey.exe', 'foldy-index.exe') {
        if ($exe -ne 'Foldy.exe' -and $setup -notmatch [regex]::Escape($exe)) { throw "make-setup.ps1이 $exe 를 payload에 넣지 않습니다" }
        Copy-Item "$env:SystemRoot\System32\cmd.exe" (Join-Path $fake $exe)
    }
    Copy-Item "$env:SystemRoot\System32\cmd.exe" (Join-Path $fake 'foldy-update.exe')

    $stage = Join-Path $root 'dist\Foldy-Store'
    foreach ($withService in $true, $false) {
        $out = Join-Path $fake 'out'
        if ($withService) {
            & (Join-Path $PSScriptRoot 'make-msix.ps1') -Version '0.0.1' -OutDir $out -Payload $fake -NoService:$false
        } else {
            & (Join-Path $PSScriptRoot 'make-msix.ps1') -Version '0.0.1' -OutDir $out -Payload $fake -NoService
        }

        if (Test-Path (Join-Path $stage 'foldy-update.exe')) { throw 'Store 패키지에 foldy-update.exe가 들어갔습니다 (Store가 업데이트한다)' }
        if ((Test-Path (Join-Path $stage 'foldy-index.exe')) -ne $withService) { throw "foldy-index.exe가 있어야 하는지 틀림 (서비스 넣기: $withService)" }
        [xml] $m = Get-Content (Join-Path $stage 'AppxManifest.xml') -Raw
        $ns = [Xml.XmlNamespaceManager]::new($m.NameTable)
        $ns.AddNamespace('d', 'http://schemas.microsoft.com/appx/manifest/desktop/windows10')
        $ns.AddNamespace('d6', 'http://schemas.microsoft.com/appx/manifest/desktop/windows10/6')
        $ns.AddNamespace('r', 'http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities')
        $task = $m.SelectSingleNode('//d:Extension[@Category="windows.startupTask"]', $ns)
        if (-not $task -or $task.Executable -ne 'foldy-hotkey.exe') { throw 'startupTask가 foldy-hotkey.exe를 가리키지 않습니다' }
        $svc = $m.SelectSingleNode('//d6:Extension[@Category="windows.service"]/d6:Service', $ns)
        $caps = @($m.SelectNodes('//r:Capability', $ns) | ForEach-Object { $_.Name })
        if ($withService) {
            if (-not $svc -or $svc.ParentNode.Executable -ne 'foldy-index.exe' -or $svc.StartAccount -ne 'localSystem' -or $svc.Arguments -ne '--service --store') { throw '색인 서비스(desktop6:Service)가 바르지 않습니다' }
            foreach ($c in 'packagedServices', 'localSystemServices') { if ($caps -notcontains $c) { throw "제한 기능 $c 가 없습니다" } }
        } elseif ($svc -or $caps -contains 'packagedServices') { throw '-NoService인데 서비스가 들어갔습니다' }
        Write-Host "Store 매니페스트 확인 (서비스 $withService): makeappx 통과"
        Remove-Item $out -Recurse -Force -ErrorAction SilentlyContinue
    }
}
finally {
    Remove-Item $fake -Recurse -Force -ErrorAction SilentlyContinue
}
