<#
.SYNOPSIS
  소스 저장소(비공개)의 Release 워크플로가 만든 파일을 공개 릴리스 저장소 chadolkr/Foldy-releases에 올린다.
  RELEASES_TOKEN secret이 없을 때 PC에서 쓴다 (gh 로그인 필요).
.EXAMPLE
  ./build/publish-release.ps1 -Tag v0.3.13
#>
param([Parameter(Mandatory)][string]$Tag)
$ErrorActionPreference = 'Stop'
$src = 'chadolkr/Foldy'
$pub = 'chadolkr/Foldy-releases'
$dir = Join-Path ([IO.Path]::GetTempPath()) "foldy-release-$Tag"
if (Test-Path $dir) { Remove-Item $dir -Recurse -Force }
New-Item -ItemType Directory $dir | Out-Null

$run = gh run list --repo $src --workflow release.yml --json databaseId,headBranch,conclusion --limit 20 | ConvertFrom-Json |
    Where-Object { $_.headBranch -eq $Tag -and $_.conclusion -eq 'success' } | Select-Object -First 1
if (-not $run) { throw "$Tag 의 성공한 Release 워크플로 실행이 없습니다" }
gh run download $run.databaseId --repo $src --name "release-$Tag" --dir $dir
if ($LASTEXITCODE -ne 0) { throw '아티팩트 다운로드 실패' }

$files = Get-ChildItem $dir -File | ForEach-Object FullName
gh release create $Tag @files --repo $pub --title "Foldy $Tag" --notes "Foldy $Tag"
if ($LASTEXITCODE -ne 0) { throw '공개 릴리스 생성 실패' }
Remove-Item $dir -Recurse -Force
Write-Host "올림: https://github.com/$pub/releases/tag/$Tag"
