using System.IO;
using Foldy.Core.Managed;
using Foldy.Platform.SystemInfo;

namespace Foldy.App.Services;

/// <summary>
/// 파일 탐색기에서도 관리 폴더가 보이게: 그 폴더 하나에만 desktop.ini(아이콘 + 설명)를 둔다.
/// 로컬 고정 드라이브만, 클라우드 동기화·느린 드라이브·OneDrive 폴더는 건너뛴다(다른 PC로 퍼지거나 내려받기가 생김).
/// 이미 다른 desktop.ini가 있으면 건드리지 않고, 끌 때는 Foldy가 만든 것만 지운다.
/// </summary>
public static class ExplorerFolderMark
{
    private const string Marker = "; Foldy";

    /// <summary>표시하기 전부터 폴더에 읽기 전용 속성이 있었다는 기록. 끌 때 그 속성은 남긴다.</summary>
    private const string KeepReadOnly = "; Foldy keep-readonly";

    /// <summary>이 폴더에 탐색기 표시를 하면 안 되는 이유 (null이면 해도 됨). 동기화 폴더면 미리보기 경고에도 쓴다.</summary>
    public static string? SyncWarning(string folder)
    {
        if (CloudVolumes.IsCloudPath(folder))
        {
            return Foldy.Core.Localization.Loc.F("sync.warn", "Google Drive");
        }

        foreach (var v in new[] { "OneDrive", "OneDriveConsumer", "OneDriveCommercial" })
        {
            if (Environment.GetEnvironmentVariable(v) is { Length: > 0 } od && folder.StartsWith(od, StringComparison.OrdinalIgnoreCase))
            {
                return Foldy.Core.Localization.Loc.F("sync.warn", "OneDrive");
            }
        }

        return null;
    }

    private static bool Allowed(string folder) =>
        folder.Length > 2 && folder[1] == ':' && Drives.TypeOf(folder[..3]) == NativeDriveType.Fixed
        && SyncWarning(folder) is null && !Foldy.Presentation.Services.DriveSpeedTracker.Shared.IsSlow(folder);

    public static void Set(string folder, FolderMethod method)
    {
        if (!Allowed(folder))
        {
            return;
        }

        var ini = Path.Combine(folder, "desktop.ini");
        try
        {
            if (File.Exists(ini) && !File.ReadAllText(ini).Contains(Marker, StringComparison.Ordinal))
            {
                return; // 사용자가 만든 것
            }

            var d = new DirectoryInfo(folder);
            bool keep;
            if (File.Exists(ini))
            {
                keep = File.ReadAllText(ini).Contains(KeepReadOnly, StringComparison.Ordinal);
                File.SetAttributes(ini, FileAttributes.Normal);
            }
            else
            {
                keep = d.Attributes.HasFlag(FileAttributes.ReadOnly);
            }

            var exe = IconPath(Environment.ProcessPath ?? "");
            var extra = keep ? KeepReadOnly + "\r\n" : "";
            File.WriteAllText(ini, $"[.ShellClassInfo]\r\nIconResource={exe},0\r\nInfoTip={Foldy.Core.Localization.Loc.F("pane.managed", method.DisplayName)} (Foldy)\r\n{Marker}\r\n{extra}", System.Text.Encoding.Unicode);
            File.SetAttributes(ini, FileAttributes.Hidden | FileAttributes.System);
            d.Attributes |= FileAttributes.ReadOnly; // 탐색기가 desktop.ini를 읽게 하는 표시 (폴더 안 파일은 그대로 쓸 수 있음)
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    public static void Clear(string folder)
    {
        var ini = Path.Combine(folder, "desktop.ini");
        try
        {
            var text = File.Exists(ini) ? File.ReadAllText(ini) : "";
            if (text.Contains(Marker, StringComparison.Ordinal))
            {
                File.SetAttributes(ini, FileAttributes.Normal);
                File.Delete(ini);
                if (!text.Contains(KeepReadOnly, StringComparison.Ordinal))
                {
                    var d = new DirectoryInfo(folder);
                    d.Attributes &= ~FileAttributes.ReadOnly;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// desktop.ini에 적을 아이콘 경로. 설치 폴더·사용자 폴더는 환경 변수로 적어서 사용자 이름이 파일에 남지 않게 한다
    /// (폴더를 압축하거나 공유해도 경로가 따라가지 않도록).
    /// </summary>
    internal static string IconPath(string exe)
    {
        foreach (var (name, folder) in new[]
        {
            ("%ProgramFiles%", Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)),
            ("%LOCALAPPDATA%", Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)),
            ("%USERPROFILE%", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)),
        })
        {
            if (folder.Length > 0 && exe.StartsWith(folder.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
            {
                return name + exe[folder.TrimEnd('\\').Length..];
            }
        }

        return exe;
    }
}
