# 크래시 릴레이 Worker에 GitHub 토큰을 secret으로 등록한다. 토큰은 이 창에만 입력한다.
$Host.UI.RawUI.WindowTitle = 'Foldy - GitHub 토큰 등록'
Set-Location $PSScriptRoot
Write-Host ''
Write-Host 'GitHub 토큰을 붙여 넣고 Enter를 누르세요 (오른쪽 클릭 또는 Ctrl+V). 글자는 화면에 보이지 않습니다.' -ForegroundColor Cyan
Write-Host ''
npx wrangler secret put GITHUB_TOKEN
Write-Host ''
if ($LASTEXITCODE -eq 0) { Write-Host '등록 완료. 이 창을 닫아도 됩니다.' -ForegroundColor Green }
else { Write-Host '등록 실패. 위 메시지를 확인해 주세요.' -ForegroundColor Red }
Read-Host 'Enter를 누르면 창이 닫힙니다'
