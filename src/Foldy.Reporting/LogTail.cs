using System.Text;

namespace Foldy.Reporting;

/// <summary>Serilog 롤링 로그(logs/foldy-YYYYMMDD.log)의 마지막 몇 줄. 앱이 쓰는 중이어도 읽는다.</summary>
public static class LogTail
{
    public static string Read(string logDirectory, int maxLines = 40, int maxBytes = 64 * 1024)
    {
        try
        {
            if (!Directory.Exists(logDirectory)) return "";
            var latest = new DirectoryInfo(logDirectory).EnumerateFiles("foldy-*.log")
                .OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault();
            if (latest is null) return "";

            using var fs = new FileStream(latest.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var start = Math.Max(0, fs.Length - maxBytes);
            fs.Seek(start, SeekOrigin.Begin);
            using var reader = new StreamReader(fs, Encoding.UTF8);
            var text = reader.ReadToEnd();
            var lines = text.Split('\n');
            if (start > 0) lines = lines[1..]; // 잘린 첫 줄 버림
            return string.Join('\n', lines.Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).TakeLast(maxLines));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return "";
        }
    }
}
