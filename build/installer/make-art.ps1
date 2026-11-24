# MSI 설치 화면 그림 만들기 (2026-11-22 사용자: "msi 설치 화면도 디자인을 반영… 쇼츠처럼 문구도").
# WixUI 규격: dialog.bmp 493×312 (환영·끝 화면, 왼쪽 164px에 그림·오른쪽은 글자가 올라감), banner.bmp 493×58 (위 띠, 오른쪽에 아이콘).
# 문구는 한 줄 소개와 대표 기능 넷 (assets/i18n의 tagline·side.*와 같은 말). 결과 그림은 저장소에 함께 둔다.
# 실행: powershell -File build/installer/make-art.ps1
param([string]$Out = $PSScriptRoot)
Add-Type -AssemblyName System.Drawing
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$icon = [System.Drawing.Image]::FromFile((Join-Path $root 'assets\icons\png\foldy-128.png'))
$orange = [System.Drawing.ColorTranslator]::FromHtml('#F28C1B')
$deep = [System.Drawing.ColorTranslator]::FromHtml('#D9740B')
$white = [System.Drawing.Color]::White
$ink = [System.Drawing.ColorTranslator]::FromHtml('#1C1C1F')
$font = 'Malgun Gothic'

function New-Canvas([int]$w, [int]$h) {
    $b = New-Object System.Drawing.Bitmap $w, $h, ([System.Drawing.Imaging.PixelFormat]::Format24bppRgb)
    $g = [System.Drawing.Graphics]::FromImage($b)
    $g.SmoothingMode = 'AntiAlias'; $g.InterpolationMode = 'HighQualityBicubic'; $g.TextRenderingHint = 'AntiAliasGridFit'
    return $b, $g
}

# ── 환영·끝 화면 (493×312) ──
$b, $g = New-Canvas 493 312
$g.Clear($white)
$panel = New-Object System.Drawing.Rectangle 0, 0, 170, 312
$grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush $panel, $orange, $deep, 90
$g.FillRectangle($grad, $panel)
$g.DrawImage($icon, 18, 22, 56, 56)
$g.DrawString('Foldy', (New-Object System.Drawing.Font $font, 20, ([System.Drawing.FontStyle]::Bold)), (New-Object System.Drawing.SolidBrush $white), 78, 32)
$tag = New-Object System.Drawing.Font $font, 8.5, ([System.Drawing.FontStyle]::Bold)
$wb = New-Object System.Drawing.SolidBrush $white
$g.DrawString('빠르고 똑똑한 파일 관리자,', $tag, $wb, 18, 94)
$g.DrawString('오프라인에서도', $tag, $wb, 18, 112)
$item = New-Object System.Drawing.Font $font, 8
$soft = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 255, 244, 230))
$y = 146
foreach ($lines in @(@('무엇이든 찾기', 'Ctrl+Space'), @('공간 늘리기'), @('바탕화면, 다운로드,', '폴더 정리'), @('폴더 틀로 한 번에 만들기'))) {
    $g.FillEllipse($wb, 18, $y + 5, 5, 5)
    foreach ($l in $lines) {
        $g.DrawString($l, $item, $soft, 27, $y)
        $y += 15
    }
    $y += 7
}
$g.DrawString('AI와 연결하면 더 편하게', (New-Object System.Drawing.Font $font, 8), $soft, 18, 280)
$b.Save((Join-Path $Out 'dialog.bmp'), [System.Drawing.Imaging.ImageFormat]::Bmp)
$g.Dispose(); $b.Dispose()

# ── 위 띠 (493×58): 글자는 WixUI가 왼쪽에 쓰고, 오른쪽에 아이콘과 주황 선 ──
$b, $g = New-Canvas 493 58
$g.Clear($white)
$g.FillRectangle((New-Object System.Drawing.SolidBrush $orange), 0, 55, 493, 3)
$g.DrawImage($icon, 437, 9, 40, 40)
$b.Save((Join-Path $Out 'banner.bmp'), [System.Drawing.Imaging.ImageFormat]::Bmp)
$g.Dispose(); $b.Dispose()
$icon.Dispose()
Write-Host "made dialog.bmp, banner.bmp in $Out"
