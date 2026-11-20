using System.Diagnostics;
using System.IO;
using System.Text;

namespace Foldy.App.Services;

/// <summary>
/// [재시작하고 업데이트]를 누른 뒤 Foldy가 닫혀 있는 동안 보이는 작은 진행 창 (사용자 발견 2026-11-20:
/// "눌렀는데 아무것도 안 보여 언제 업데이트되는지 모르겠다"). 설치는 SYSTEM 서비스가 하므로 서비스는 화면을 띄울 수 없고,
/// Foldy 실행 파일은 설치 중에 바뀌므로 Foldy 자신도 떠 있을 수 없다. 그래서 Windows에 들어 있는 PowerShell로
/// 임시 폴더에서 작은 창을 띄워, 서비스의 업데이트 기록(updater.log)을 읽어 같은 자리에 단계를 보여 준다.
/// 새 Foldy가 뜨면 저절로 닫힌다. 실패하거나 다른 Foldy 창 때문에 기다리면 쉬운 말로 알린다.
/// </summary>
internal static class UpdateProgressWindow
{
    public static string LogPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Foldy", "Updates", "updater.log");

    /// <summary>
    /// 연주·우진 농구 장면을 그림 파일로 미리 그려 둔다 (진행 창은 Foldy가 닫힌 뒤에 뜨므로 WPF로 그릴 수 없다).
    /// 설치하는 동안: 코트에서 드리블 기술과 패스(반복). 끝날 때: 연주의 패스를 받은 우진이 레이업 2점슛 → 하이파이브(한 번). UI 스레드에서 부른다. 실패하면 null(글자만).
    /// </summary>
    public static string? RenderFrames()
    {
        try
        {
            var dir = Path.Combine(Path.GetTempPath(), "Foldy", "update-anim");
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }

            Directory.CreateDirectory(dir);
            var painter = new Foldy.UI.Mascot.MascotPainter(Foldy.UI.Mascot.MascotPalette.Default);
            const double width = 300, scale = 1.5;
            void Render(string prefix, Foldy.Presentation.Mascot.MascotScene scene, Foldy.Presentation.Mascot.MascotFinish finish, int frames, double seconds, double progress = 1)
            {
                var frame = new Foldy.Presentation.Mascot.MascotFrame(scene, 1, Foldy.Presentation.Mascot.MascotBubble.None, Foldy.Presentation.Mascot.TossOutcome.None, progress, Foldy.Presentation.Mascot.MascotPhase.None, false, 4, finish);
                for (var i = 0; i < frames; i++)
                {
                    var t = seconds * i / frames;
                    var bmp = Foldy.UI.Mascot.MascotBitmap.RenderVector(painter, width, frame, Math.Clamp(t / seconds, 0, 1), t, scale);
                    var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
                    enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
                    using var fs = File.Create(Path.Combine(dir, $"{prefix}_{i:00}.png"));
                    enc.Save(fs);
                }
            }

            var catalog = Foldy.UI.Mascot.MascotView.SceneCatalog;
            // 설치하는 동안: 코트에서 1:1처럼 드리블 기술(크로스오버·스핀·페이크)과 패스를 주고받는다 (실제 시간 8초, 0.1초마다 한 장)
            Render("work", Foldy.Presentation.Mascot.MascotScene.Dribble, Foldy.Presentation.Mascot.MascotFinish.Dunk, 80, 8, progress: -1);
            // 끝: 연주의 패스를 받은 우진이 레이업 2점슛 → 하이파이브 (사용자 요청)
            Render("done", Foldy.Presentation.Mascot.MascotScene.Layup, Foldy.Presentation.Mascot.MascotFinish.Dunk, 40, Math.Max(1.5, catalog.Get(Foldy.Presentation.Mascot.MascotScene.Layup).LoopSeconds));
            return dir;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            Serilog.Log.Warning(ex, "업데이트 진행 창 그림을 만들지 못함 (글자만 보임)");
            return null;
        }
    }

    /// <summary>진행 창을 띄운다. 실패해도 업데이트에는 영향이 없다 (창만 안 보인다).</summary>
    public static void Show(string version, string? animDir = null)
    {
        try
        {
            var dir = Path.Combine(Path.GetTempPath(), "Foldy");
            Directory.CreateDirectory(dir);
            var script = Path.Combine(dir, "update-progress.ps1");
            // Windows PowerShell 5.1은 BOM이 없으면 한글을 ANSI로 읽는다
            File.WriteAllText(script, Script, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            var since = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 5;
            var psi = new ProcessStartInfo("powershell.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var a in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-WindowStyle", "Hidden", "-File", script, "-Version", version, "-Log", LogPath, "-Since", since.ToString(System.Globalization.CultureInfo.InvariantCulture), "-OldPid", Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture), "-Anim", animDir ?? "" })
            {
                psi.ArgumentList.Add(a);
            }

            Process.Start(psi)?.Dispose();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            Serilog.Log.Warning(ex, "업데이트 진행 창을 띄우지 못함");
        }
    }

    /// <summary>업데이트 기록 한 줄을 화면 문구로 (테스트용으로 C#에도 같은 규칙을 둔다). null이면 바꾸지 않음.</summary>
    public static (string Text, bool Done, bool Failed)? Describe(string line)
    {
        if (line.Contains("설치 실패", StringComparison.Ordinal) || line.Contains("검증 실패", StringComparison.Ordinal) || line.Contains("설치 후에도 버전이", StringComparison.Ordinal))
        {
            return ("업데이트를 설치하지 못했어요. 지금 쓰던 버전은 그대로예요. Foldy를 다시 열고 [업데이트]를 한 번 더 눌러 주세요.", true, true);
        }

        if (line.Contains("끝나지 않아 설치를 미룸", StringComparison.Ordinal))
        {
            return ("열려 있는 다른 Foldy 창이 닫히기를 기다리고 있어요. 다른 Foldy 창을 모두 닫아 주세요.", false, false);
        }

        if (line.Contains("설치 시작", StringComparison.Ordinal))
        {
            return ("설치하고 있어요… 보통 1분 안에 끝나요.", false, false);
        }

        if (line.Contains("설치 완료", StringComparison.Ordinal))
        {
            return ("설치가 끝났어요. Foldy를 다시 여는 중이에요…", false, false);
        }

        if (line.Contains("다시 띄움", StringComparison.Ordinal) || line.Contains("정상 시작", StringComparison.Ordinal))
        {
            return ("다 됐어요!", true, false);
        }

        return null;
    }

    private const string Script = """
        param([string]$Version, [string]$Log, [long]$Since, [int]$OldPid, [string]$Anim = "")
        Add-Type -AssemblyName System.Windows.Forms
        Add-Type -AssemblyName System.Drawing
        [System.Windows.Forms.Application]::EnableVisualStyles()
        $f = New-Object System.Windows.Forms.Form
        $f.Text = "Foldy 업데이트"
        # 연주·우진 농구 그림 (Foldy가 닫히기 전에 그려 둔 것): 설치 중엔 패스, 끝나면 3점슛 → 하이파이브
        $work = @(); $donePics = @()
        if ($Anim -and (Test-Path $Anim)) {
            $work = @(Get-ChildItem -Path $Anim -Filter "work_*.png" | Sort-Object Name | ForEach-Object { [System.Drawing.Image]::FromFile($_.FullName) })
            $donePics = @(Get-ChildItem -Path $Anim -Filter "done_*.png" | Sort-Object Name | ForEach-Object { [System.Drawing.Image]::FromFile($_.FullName) })
        }
        $picH = 0
        if ($work.Count -gt 0) { $picH = $work[0].Height + 8 }
        $f.Width = 520; $f.Height = 210 + $picH
        $f.StartPosition = "CenterScreen"; $f.TopMost = $true
        $f.FormBorderStyle = "FixedDialog"; $f.MaximizeBox = $false; $f.MinimizeBox = $false
        $f.Font = New-Object System.Drawing.Font("Malgun Gothic", 12)
        $title = New-Object System.Windows.Forms.Label
        $title.Text = "Foldy $Version(으)로 업데이트하고 있어요"
        $title.Font = New-Object System.Drawing.Font("Malgun Gothic", 13, [System.Drawing.FontStyle]::Bold)
        $title.SetBounds(20, 16, 470, 30)
        $msg = New-Object System.Windows.Forms.Label
        $msg.Text = "Foldy가 닫히기를 기다리는 중이에요… 끝나면 Foldy가 저절로 다시 열려요."
        $msg.SetBounds(20, 54 + $picH, 470, 60)
        $bar = New-Object System.Windows.Forms.ProgressBar
        $bar.Style = "Marquee"; $bar.SetBounds(20, 118 + $picH, 470, 14)
        $close = New-Object System.Windows.Forms.Button
        $close.Text = "닫기"; $close.SetBounds(400, 140 + $picH, 90, 32); $close.Visible = $false
        $close.Add_Click({ $f.Close() })
        $f.Controls.AddRange(@($title, $msg, $bar, $close))
        $pic = $null
        if ($work.Count -gt 0) {
            $pic = New-Object System.Windows.Forms.PictureBox
            $pic.SizeMode = "CenterImage"; $pic.SetBounds(20, 50, 470, $picH - 4); $pic.Image = $work[0]
            $f.Controls.Add($pic)
            $script:k = 0; $script:finishing = $false; $script:d = 0
            $animTimer = New-Object System.Windows.Forms.Timer  # $Anim(매개변수)와 이름이 겹치면 문자열이 된다
            $animTimer.Interval = 100
            $animTimer.Add_Tick({
                if ($script:finishing) {
                    if ($script:d -lt $donePics.Count) { $pic.Image = $donePics[$script:d]; $script:d++ }
                    else { $animTimer.Stop(); $f.Close() }
                } else {
                    $script:k = ($script:k + 1) % $work.Count; $pic.Image = $work[$script:k]
                }
            })
            $animTimer.Start()
        }
        $start = Get-Date
        $timer = New-Object System.Windows.Forms.Timer
        $timer.Interval = 1000
        $timer.Add_Tick({
            $done = $false; $failed = $false
            if (Test-Path $Log) {
                $lines = Get-Content -Path $Log -Encoding UTF8 -Tail 40 -ErrorAction SilentlyContinue
                foreach ($l in $lines) {
                    $ts = 0L
                    $parts = $l.Split(' ', 2)
                    if ($parts.Count -lt 2 -or -not [long]::TryParse($parts[0], [ref]$ts) -or $ts -lt $Since) { continue }
                    $t = $parts[1]
                    if ($t -match '설치 실패|검증 실패|설치 후에도 버전이') { $msg.Text = "업데이트를 설치하지 못했어요. 지금 쓰던 버전은 그대로예요. Foldy를 다시 열고 [업데이트]를 한 번 더 눌러 주세요."; $failed = $true }
                    elseif ($t -match '끝나지 않아 설치를 미룸') { $msg.Text = "열려 있는 다른 Foldy 창이 닫히기를 기다리고 있어요. 다른 Foldy 창을 모두 닫아 주세요." }
                    elseif ($t -match '설치 시작') { $msg.Text = "설치하고 있어요… 보통 1분 안에 끝나요." }
                    elseif ($t -match '설치 완료') { $msg.Text = "설치가 끝났어요. Foldy를 다시 여는 중이에요…" }
                    elseif ($t -match '다시 띄움|정상 시작') { $done = $true }
                }
            }
            # 새 Foldy가 떠 있으면 끝
            $new = Get-Process -Name Foldy -ErrorAction SilentlyContinue | Where-Object { $_.Id -ne $OldPid -and $_.StartTime -gt $start }
            if ($new) { $done = $true }
            if ($done) {
                $timer.Stop(); $msg.Text = "다 됐어요! 새 Foldy가 열려요."; $bar.Visible = $false
                if ($pic -and $donePics.Count -gt 0) { $script:finishing = $true } else { $f.Close() }  # 3점슛 → 하이파이브 뒤에 닫힘
                return
            }
            if ($failed) { $timer.Stop(); $bar.Visible = $false; $close.Visible = $true; if ($animTimer) { $animTimer.Stop() }; return }
            if (((Get-Date) - $start).TotalMinutes -gt 5) {
                $msg.Text = "생각보다 오래 걸리고 있어요. 이 창을 닫고 잠시 뒤 Foldy를 직접 열어 보세요."
                $timer.Stop(); $bar.Visible = $false; $close.Visible = $true
            }
        })
        $timer.Start()
        [void]$f.ShowDialog()
        """;
}
