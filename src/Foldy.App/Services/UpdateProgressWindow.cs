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

    /// <summary>진행 창을 띄운다. 실패해도 업데이트에는 영향이 없다 (창만 안 보인다).</summary>
    public static void Show(string version)
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
            foreach (var a in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-WindowStyle", "Hidden", "-File", script, "-Version", version, "-Log", LogPath, "-Since", since.ToString(System.Globalization.CultureInfo.InvariantCulture), "-OldPid", Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture) })
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
        param([string]$Version, [string]$Log, [long]$Since, [int]$OldPid)
        Add-Type -AssemblyName System.Windows.Forms
        Add-Type -AssemblyName System.Drawing
        [System.Windows.Forms.Application]::EnableVisualStyles()
        $f = New-Object System.Windows.Forms.Form
        $f.Text = "Foldy 업데이트"
        $f.Width = 520; $f.Height = 210
        $f.StartPosition = "CenterScreen"; $f.TopMost = $true
        $f.FormBorderStyle = "FixedDialog"; $f.MaximizeBox = $false; $f.MinimizeBox = $false
        $f.Font = New-Object System.Drawing.Font("Malgun Gothic", 12)
        $title = New-Object System.Windows.Forms.Label
        $title.Text = "Foldy $Version(으)로 업데이트하고 있어요"
        $title.Font = New-Object System.Drawing.Font("Malgun Gothic", 13, [System.Drawing.FontStyle]::Bold)
        $title.SetBounds(20, 16, 470, 30)
        $msg = New-Object System.Windows.Forms.Label
        $msg.Text = "Foldy가 닫히기를 기다리는 중이에요… 끝나면 Foldy가 저절로 다시 열려요."
        $msg.SetBounds(20, 54, 470, 60)
        $bar = New-Object System.Windows.Forms.ProgressBar
        $bar.Style = "Marquee"; $bar.SetBounds(20, 118, 470, 14)
        $close = New-Object System.Windows.Forms.Button
        $close.Text = "닫기"; $close.SetBounds(400, 140, 90, 32); $close.Visible = $false
        $close.Add_Click({ $f.Close() })
        $f.Controls.AddRange(@($title, $msg, $bar, $close))
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
            if ($done) { $timer.Stop(); $f.Close(); return }
            if ($failed) { $timer.Stop(); $bar.Visible = $false; $close.Visible = $true; return }
            if (((Get-Date) - $start).TotalMinutes -gt 5) {
                $msg.Text = "생각보다 오래 걸리고 있어요. 이 창을 닫고 잠시 뒤 Foldy를 직접 열어 보세요."
                $timer.Stop(); $bar.Visible = $false; $close.Visible = $true
            }
        })
        $timer.Start()
        [void]$f.ShowDialog()
        """;
}
