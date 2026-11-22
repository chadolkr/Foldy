using System.IO;
using Foldy.Core.Downloads;
using Foldy.Core.Logs;
using Foldy.Presentation.Files;

namespace Foldy.App.Services;

/// <summary>알림 한 줄과 [버튼]이 열 정리 창 ("logs" + 드라이브 / "downloads" + 폴더).</summary>
public sealed record CleanupFinding(string Text, string Tool, string Target);

/// <summary>
/// 백그라운드 정리 확인: 로그는 색인으로만(색인이 없으면 확인하지 않음, 로컬 고정 드라이브만), 다운로드는 폴더 한 단계만
/// (중복 비교는 하지 않음). 정리할 게 많을 때만 결과를 돌려준다. 알림은 일주일에 한 번까지.
/// </summary>
public static class CleanupNotifier
{
    private static readonly TimeSpan Interval = TimeSpan.FromDays(7);

    private static string StampPath => Path.Combine(App.DataDir, "cleanup-notice.txt");

    public static bool ShownRecently()
    {
        try
        {
            return File.Exists(StampPath) && DateTime.UtcNow - File.GetLastWriteTimeUtc(StampPath) < Interval;
        }
        catch (IOException)
        {
            return true;
        }
    }

    public static void MarkShown()
    {
        try
        {
            File.WriteAllText(StampPath, DateTime.UtcNow.ToString("O"));
            File.SetLastWriteTimeUtc(StampPath, DateTime.UtcNow);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    public static async Task<CleanupFinding?> CheckAsync(CancellationToken ct)
    {
        foreach (var d in DriveInfo.GetDrives())
        {
            if (d.DriveType != DriveType.Fixed || !d.IsReady)
            {
                continue;
            }

            var files = await LogFinder.FromIndexAsync(d.RootDirectory.FullName, ct);
            if (files is null)
            {
                break; // 색인이 없으면 백그라운드에서는 훑지 않는다
            }

            var groups = LogPlanner.Plan(files, DateTime.UtcNow);
            var saving = groups.Sum(g => g.EstimatedSaving);
            if (LogPlanner.IsSerious(saving, d.AvailableFreeSpace))
            {
                return new CleanupFinding(
                    Foldy.Core.Localization.Loc.F("cn.logs", d.Name.TrimEnd('\\'), FileItem.FormatBytes(groups.Sum(g => g.Size)), FileItem.FormatBytes(saving)),
                    "logs", d.RootDirectory.FullName);
            }
        }

        // 관리 방식을 정한 폴더 (로컬 고정 드라이브만)
        foreach (var root in Foldy.Core.Managed.ManagedFolders.KnownRoots())
        {
            if (root.Length < 3 || root[1] != ':' || !Directory.Exists(root) || Foldy.Presentation.Services.DriveSpeedTracker.Shared.IsSlow(root)
                || Foldy.Platform.SystemInfo.Drives.TypeOf(root[..3]) != Foldy.Platform.SystemInfo.NativeDriveType.Fixed)
            {
                continue;
            }

            if (Foldy.Core.Managed.ManagedFolders.Resolve(root) is { } m)
            {
                var list = await Task.Run(() => Foldy.Core.Managed.ManagedFolders.Suggest(m.Root, m.Method, DateTime.UtcNow), ct);
                var moves = list.Sum(s => s.Moves.Count);
                if (moves >= 5)
                {
                    return new CleanupFinding(Foldy.Core.Localization.Loc.F("cn.managed", Path.GetFileName(root), m.Method.DisplayName, list.Count), "managed", root);
                }
            }
        }

        if (CleanupPaths.Downloads() is { } dl)
        {
            var items = await Task.Run(() =>
            {
                var installed = InstalledPrograms.Names();
                return DownloadsPlanner.Plan(dl, p => DownloadsPlanner.MatchInstalled(p, installed), DateTime.UtcNow, findDuplicates: false, ct);
            }, ct);
            var total = items.Sum(i => i.Size);
            if (total >= DownloadsPlanner.NoticeBytes)
            {
                return new CleanupFinding(Foldy.Core.Localization.Loc.F("cn.downloads", FileItem.FormatBytes(total)), "downloads", dl);
            }
        }

        var desktop = CleanupPaths.Desktop;
        if (Directory.Exists(desktop))
        {
            var moves = await Task.Run(() => Foldy.Core.Desktop.DesktopTidy.Plan(desktop, CleanupPaths.DesktopArchive, DateTime.UtcNow), ct);
            if (moves.Count >= Foldy.Core.Desktop.DesktopTidy.NoticeCount)
            {
                return new CleanupFinding(Foldy.Core.Localization.Loc.F("cn.desktop", moves.Count), "desktop", desktop);
            }
        }

        return null;
    }
}
