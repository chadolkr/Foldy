using System.Diagnostics;
using System.Text;

namespace Foldy.Platform.SystemInfo;

/// <summary>
/// "나만 설치"(1.19.0, <c>build/installer/FoldyUser.wxs</c>): 관리자 권한 없이 <c>%LOCALAPPDATA%\Programs\Foldy</c>에 설치한 Foldy의 업데이트.
/// 그 MSI의 새 버전(<c>Foldy-x.y.z-x64-user.msi</c>)을 이 사용자 권한 그대로 설치하므로 UAC가 뜨지 않는다.
/// 받은 MSI는 이 사용자 폴더에 있고 설치도 이 사용자 폴더에 하므로 관리자 쪽처럼 따로 옮겨 검사할 권한 경계가 없다.
/// 그래도 설치 직전에 SHA-256을 update.json 값과 한 번 더 비교한다.
/// </summary>
public static class UserMsiInstall
{
    /// <summary>"나만 설치" MSI 파일 이름의 끝 (모든 사용자용은 <c>-x64.msi</c>).</summary>
    public const string AssetSuffix = "-x64-user.msi";

    /// <summary>"나만 설치" 자리: <c>%LOCALAPPDATA%\Programs\Foldy</c>.</summary>
    public static string InstallFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Foldy");

    /// <summary>지금 Foldy가 "나만 설치" 자리에서 돌고 있나.</summary>
    public static bool IsUserInstall => IsUnder(AppContext.BaseDirectory, InstallFolder) && !AppPackage.IsPackaged;

    public static bool IsUnder(string path, string folder)
    {
        var p = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var f = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        return p.Equals(f, StringComparison.OrdinalIgnoreCase) || p.StartsWith(f + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    public static string BuildScript(string msi, string sha256)
    {
        ArgumentException.ThrowIfNullOrEmpty(msi);
        if (sha256.Length != 64 || !sha256.All(Uri.IsHexDigit))
        {
            throw new ArgumentException("sha256은 16진수 64자여야 해요", nameof(sha256));
        }

        var sb = new StringBuilder();
        sb.AppendLine("$ErrorActionPreference = 'Stop'");
        sb.AppendLine("Start-Sleep -Seconds 2"); // Foldy가 끝날 틈
        sb.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"$msi = {ElevatedMsiInstall.Quote(msi)}");
        sb.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"$hash = {ElevatedMsiInstall.Quote(sha256.ToUpperInvariant())}");
        sb.AppendLine("if ((Get-FileHash -LiteralPath $msi -Algorithm SHA256).Hash -ne $hash) { exit 2 }");
        sb.AppendLine("$log = [IO.Path]::ChangeExtension($msi, '.log')");
        sb.AppendLine("$p = Start-Process -FilePath (Join-Path $env:SystemRoot 'System32\\msiexec.exe') -Wait -PassThru -ArgumentList @('/i', ('\"' + $msi + '\"'), '/passive', '/norestart', '/l*v', ('\"' + $log + '\"'))");
        sb.AppendLine("$exe = Join-Path $env:LOCALAPPDATA 'Programs\\Foldy\\Foldy.exe'");
        sb.AppendLine("if (Test-Path -LiteralPath $exe) { Start-Process -FilePath $exe }");
        sb.AppendLine("exit $p.ExitCode");
        return sb.ToString();
    }

    /// <summary>이 사용자 권한으로(UAC 없이) 위 스크립트를 실행하는 시작 정보.</summary>
    public static ProcessStartInfo BuildStartInfo(string msi, string sha256) => new(
        Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"),
        "-NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -EncodedCommand "
            + Convert.ToBase64String(Encoding.Unicode.GetBytes(BuildScript(msi, sha256))))
    {
        UseShellExecute = false,
        CreateNoWindow = true,
    };
}
