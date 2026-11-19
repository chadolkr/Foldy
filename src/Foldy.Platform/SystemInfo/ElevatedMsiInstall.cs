using System.Diagnostics;
using System.Text;

namespace Foldy.Platform.SystemInfo;

/// <summary>
/// 색인 서비스 없이 업데이트할 때(사용자 프록시로 받은 MSI) 관리자 권한으로 설치하는 명령.
/// <para>
/// 내려받은 MSI는 사용자 폴더에 있어서, 확인한 뒤 설치하기 전까지 같은 사용자 권한의 다른 프로그램이 바꿔치기할 수 있다.
/// 그래서 관리자 권한으로 뜬 뒤에 먼저 관리자만 쓸 수 있는 폴더(<c>%SystemRoot%\Temp\Foldy-Update-…</c>)로 복사하고,
/// 그 복사본의 SHA-256을 update.json 값과 다시 비교한 다음에만 그 복사본을 설치한다. 설치 기록도 그 폴더에 둔다
/// (사용자 폴더에 관리자 권한으로 쓰면 링크로 다른 파일을 덮어쓰게 만들 수 있다).
/// </para>
/// 경로는 작은따옴표 문자열로 넣고 명령 전체를 <c>-EncodedCommand</c>로 넘겨, 경로에 따옴표·공백이 있어도 깨지지 않는다.
/// </summary>
public static class ElevatedMsiInstall
{
    /// <summary>PowerShell 작은따옴표 문자열 (안의 '는 ''로).</summary>
    public static string Quote(string s) => "'" + s.Replace("'", "''", StringComparison.Ordinal) + "'";

    public static string BuildScript(string msi, string sha256, string relaunchExe)
    {
        ArgumentException.ThrowIfNullOrEmpty(msi);
        if (sha256.Length != 64 || !sha256.All(Uri.IsHexDigit))
        {
            throw new ArgumentException("sha256은 16진수 64자여야 해요", nameof(sha256));
        }

        var sb = new StringBuilder();
        sb.AppendLine("$ErrorActionPreference = 'Stop'");
        sb.AppendLine("Start-Sleep -Seconds 2");
        sb.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"$src = {Quote(msi)}");
        sb.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"$hash = {Quote(sha256.ToUpperInvariant())}");
        sb.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"$exe = {Quote(relaunchExe)}");
        sb.AppendLine("$dir = Join-Path $env:SystemRoot ('Temp\\Foldy-Update-' + [guid]::NewGuid().ToString('N'))");
        sb.AppendLine("New-Item -ItemType Directory -Path $dir | Out-Null");
        // Windows\Temp는 일반 사용자도 파일을 만들 수 있으므로 상속을 끊고 관리자·SYSTEM만 남긴다
        sb.AppendLine("& (Join-Path $env:SystemRoot 'System32\\icacls.exe') $dir /inheritance:r /grant:r '*S-1-5-32-544:(OI)(CI)F' '*S-1-5-18:(OI)(CI)F' | Out-Null");
        sb.AppendLine("if ($LASTEXITCODE -ne 0) { exit 3 }");
        sb.AppendLine("$msi = Join-Path $dir 'Foldy-update.msi'");
        sb.AppendLine("Copy-Item -LiteralPath $src -Destination $msi");
        sb.AppendLine("if ((Get-FileHash -LiteralPath $msi -Algorithm SHA256).Hash -ne $hash) { Remove-Item -LiteralPath $msi -Force; exit 2 }");
        sb.AppendLine("$log = Join-Path $dir 'install.log'");
        sb.AppendLine("$p = Start-Process -FilePath (Join-Path $env:SystemRoot 'System32\\msiexec.exe') -Wait -PassThru -ArgumentList @('/i', ('\"' + $msi + '\"'), '/passive', '/norestart', '/l*v', ('\"' + $log + '\"'))");
        sb.AppendLine("Remove-Item -LiteralPath $msi -Force -ErrorAction SilentlyContinue");
        // 설치한 Foldy를 다시 띄운다 (기본 설치 폴더에 있으면 그것, 없으면 업데이트를 시작한 exe).
        // 개발 빌드나 다른 폴더에서 업데이트해도 옛 exe가 다시 뜨지 않게 한다.
        sb.AppendLine("$installed = Join-Path $env:ProgramFiles 'Foldy\\Foldy.exe'");
        sb.AppendLine("if (Test-Path -LiteralPath $installed) { $exe = $installed }");
        // 다시 띄우기는 탐색기를 거쳐 일반 권한으로
        sb.AppendLine("Start-Process -FilePath (Join-Path $env:SystemRoot 'explorer.exe') -ArgumentList ('\"' + $exe + '\"')");
        sb.AppendLine("exit $p.ExitCode");
        return sb.ToString();
    }

    /// <summary>UAC 한 번으로 위 스크립트를 실행하는 시작 정보.</summary>
    public static ProcessStartInfo BuildStartInfo(string msi, string sha256, string relaunchExe) => new(
        Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"),
        "-NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -EncodedCommand "
            + Convert.ToBase64String(Encoding.Unicode.GetBytes(BuildScript(msi, sha256, relaunchExe))))
    {
        UseShellExecute = true,
        Verb = "runas",
        WindowStyle = ProcessWindowStyle.Hidden,
    };
}
