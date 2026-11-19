using System.Text.Json;
using Foldy.Core.Organize;

namespace Foldy.Core.Desktop;

public sealed record DesktopMove(string Src, string Dst, string Group, long Size);

/// <summary>
/// 바탕화면 정리: 지우지 않고 보관만 한다. 바로 가기(.lnk, .url)와 최근 7일 안에 쓴 파일은 그대로 두고,
/// 나머지 파일을 바탕화면\바탕화면 보관\연-월\종류(문서·이미지·압축 …)로 옮긴다. 옮긴 목록을 저장해 한 번에 되돌릴 수 있다.
/// </summary>
public static class DesktopTidy
{
    public const int RecentDays = 7;
    public const int NoticeCount = 50;
    public const string ArchiveFolderName = "바탕화면 보관";

    private static readonly string[] KeepExtensions = [".lnk", ".url", ".appref-ms"];

    public static IReadOnlyList<DesktopMove> Plan(string desktop, string archiveRoot, DateTime nowUtc)
    {
        var month = nowUtc.ToLocalTime().ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture);
        var planned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var moves = new List<DesktopMove>();
        var files = new DirectoryInfo(desktop).EnumerateFiles("*", new EnumerationOptions { AttributesToSkip = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint, IgnoreInaccessible = true });
        foreach (var f in files.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (KeepExtensions.Contains(f.Extension, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            var last = new[] { f.LastWriteTimeUtc, f.LastAccessTimeUtc, f.CreationTimeUtc }.Max();
            if (nowUtc - last < TimeSpan.FromDays(RecentDays))
            {
                continue;
            }

            var group = FileTypeGroups.GroupOf(f.Name);
            var dir = Path.Combine(archiveRoot, month, group);
            var dst = Unique(dir, f.Name, planned);
            planned.Add(dst);
            moves.Add(new DesktopMove(f.FullName, dst, group, f.Length));
        }

        return moves;
    }

    /// <summary>옮기고, 옮긴 것만 되돌리기 기록(<paramref name="journalPath"/>)에 남긴다. 사용 중인 파일은 건너뛴다.</summary>
    public static IReadOnlyList<DesktopMove> Execute(IEnumerable<DesktopMove> moves, string journalPath)
    {
        var done = new List<DesktopMove>();
        foreach (var m in moves)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(m.Dst)!);
                if (File.Exists(m.Dst))
                {
                    continue;
                }

                File.Move(m.Src, m.Dst);
                done.Add(m);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 다음 파일로
            }
        }

        if (done.Count > 0)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(journalPath)!);
            File.WriteAllText(journalPath, JsonSerializer.Serialize(done));
        }

        return done;
    }

    public static IReadOnlyList<DesktopMove> LastMoves(string journalPath)
    {
        try
        {
            return File.Exists(journalPath) ? JsonSerializer.Deserialize<List<DesktopMove>>(File.ReadAllText(journalPath)) ?? [] : [];
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>지난 정리를 되돌린다: 보관 폴더에 아직 있고 바탕화면에 같은 이름이 없는 것만 제자리로. 되돌린 개수.</summary>
    public static int Undo(string journalPath)
    {
        var n = 0;
        foreach (var m in LastMoves(journalPath))
        {
            try
            {
                if (File.Exists(m.Dst) && !File.Exists(m.Src))
                {
                    File.Move(m.Dst, m.Src);
                    n++;
                    DeleteIfEmpty(Path.GetDirectoryName(m.Dst));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        File.Delete(journalPath);
        return n;
    }

    private static void DeleteIfEmpty(string? dir)
    {
        try
        {
            if (dir is not null && Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
            {
                Directory.Delete(dir);
            }
        }
        catch (IOException)
        {
        }
    }

    private static string Unique(string dir, string name, HashSet<string> planned)
    {
        var dst = Path.Combine(dir, name);
        for (var i = 2; File.Exists(dst) || planned.Contains(dst); i++)
        {
            dst = Path.Combine(dir, $"{Path.GetFileNameWithoutExtension(name)} ({i}){Path.GetExtension(name)}");
        }

        return dst;
    }
}
