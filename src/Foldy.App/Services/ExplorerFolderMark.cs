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

    /// <summary>이 폴더에 탐색기 표시를 하면 안 되는 이유 (null이면 해도 됨). 동기화 폴더면 미리보기 경고에도 쓴다.</summary>
    public static string? SyncWarning(string folder)
    {
        if (CloudVolumes.IsCloudPath(folder))
        {
            return "동기화 폴더예요 (Google Drive). 옮기면 다른 PC에도 똑같이 반영되고, 파일을 내려받을 수 있어요.";
        }

        foreach (var v in new[] { "OneDrive", "OneDriveConsumer", "OneDriveCommercial" })
        {
            if (Environment.GetEnvironmentVariable(v) is { Length: > 0 } od && folder.StartsWith(od, StringComparison.OrdinalIgnoreCase))
            {
                return "동기화 폴더예요 (OneDrive). 옮기면 다른 PC에도 똑같이 반영되고, 파일을 내려받을 수 있어요.";
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

            if (File.Exists(ini))
            {
                File.SetAttributes(ini, FileAttributes.Normal);
            }

            var exe = Environment.ProcessPath ?? "";
            File.WriteAllText(ini, $"[.ShellClassInfo]\r\nIconResource={exe},0\r\nInfoTip={method.Name} 방법으로 관리 중 (Foldy)\r\n{Marker}\r\n", System.Text.Encoding.Unicode);
            File.SetAttributes(ini, FileAttributes.Hidden | FileAttributes.System);
            var d = new DirectoryInfo(folder);
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
            if (File.Exists(ini) && File.ReadAllText(ini).Contains(Marker, StringComparison.Ordinal))
            {
                File.SetAttributes(ini, FileAttributes.Normal);
                File.Delete(ini);
                var d = new DirectoryInfo(folder);
                d.Attributes &= ~FileAttributes.ReadOnly;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
