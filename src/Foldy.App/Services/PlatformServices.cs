using System.Diagnostics;
using System.IO;
using System.Windows.Threading;
using Foldy.Core.FileSystem;
using Foldy.Core.Threading;
using Foldy.Platform.Shell;
using Foldy.Platform.SystemInfo;
using NativeDriveType = Foldy.Platform.SystemInfo.NativeDriveType;
using Foldy.Presentation.Persistence;
using Foldy.Presentation.Services;
using Foldy.Presentation.Tabs;

namespace Foldy.App.Services;

/// <summary>Presentation 추상화를 Windows 구현(Foldy.Platform)에 잇는 어댑터들. 조립 루트(App)에만 둔다.</summary>
public sealed class WpfDispatcher(Dispatcher dispatcher) : IUiDispatcher
{
    public bool CheckAccess() => dispatcher.CheckAccess();

    public void Post(Action action)
    {
        if (dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            dispatcher.BeginInvoke(action);
        }
    }
}

/// <summary>IFileOperation 기반 파일 작업. 소유 창 핸들은 늦게 정해지므로 함수로 받는다.</summary>
public sealed class ShellFileOperationService(Func<IntPtr> owner) : IFileOperationService
{
    private static FileOpResult Map(FileOpOutcome o) =>
        o.Succeeded ? FileOpResult.Ok(o.CreatedPath) : o.Aborted ? FileOpResult.Canceled : FileOpResult.Fail(o.Error ?? "실패");

    private static FileOpProgressCallback? Cb(IProgress<FileOpProgress>? p) =>
        p is null ? null : (done, total, cur) => p.Report(new FileOpProgress(done, total, cur));

    public async Task<FileOpResult> CopyAsync(IReadOnlyList<string> sources, string destinationFolder, IProgress<FileOpProgress>? progress, CancellationToken ct) =>
        Map(await FileOperations.RunAsync(new FileOpRequest(FileOpKind.Copy, sources, destinationFolder), owner(), Cb(progress), ct));

    public async Task<FileOpResult> MoveAsync(IReadOnlyList<string> sources, string destinationFolder, IProgress<FileOpProgress>? progress, CancellationToken ct) =>
        Map(await FileOperations.RunAsync(new FileOpRequest(FileOpKind.Move, sources, destinationFolder), owner(), Cb(progress), ct));

    /// <summary>
    /// 폴더가 섞인 휴지통·삭제는 도우미 프로세스에서 한다: Windows 파일 작업 안의 남의 코드가 프로세스를 끝내도
    /// Foldy 창은 남고 실패만 알린다 (1.6.1, <see cref="FileOpWorker"/>). 파일 몇 개는 그 자리에서 바로.
    /// </summary>
    public async Task<FileOpResult> DeleteAsync(IReadOnlyList<string> paths, bool recycle, IProgress<FileOpProgress>? progress, CancellationToken ct)
    {
        var req = new FileOpRequest(recycle ? FileOpKind.Recycle : FileOpKind.Delete, paths);
        if (!await Task.Run(() => FileOpWorkerProtocol.ShouldIsolate(true, paths, Directory.Exists), CancellationToken.None))
        {
            return Map(await FileOperations.RunAsync(req, owner(), Cb(progress), ct));
        }

        Foldy.Core.Diagnostics.RunMarker.Note($"{req.Kind} {paths.Count}: {paths[0]}");
        try
        {
            return Map(await FileOpWorker.RunAsync(req, owner(), Cb(progress), ct));
        }
        finally
        {
            Foldy.Core.Diagnostics.RunMarker.Note(null);
        }
    }

    public async Task<FileOpResult> RenameAsync(string path, string newName, CancellationToken ct) =>
        Map(await FileOperations.RunAsync(new FileOpRequest(FileOpKind.Rename, [path], NewName: newName), owner(), null, ct));

    public async Task<FileOpResult> NewFolderAsync(string parent, string name, CancellationToken ct) =>
        Map(await FileOperations.RunAsync(new FileOpRequest(FileOpKind.NewFolder, [parent], parent, name), owner(), null, ct));
}

public sealed class ShellOpenFileService : IOpenFileService
{
    public Task<bool> ShouldUseBuiltInMarkdownAsync(string path) =>
        Task.Run(() => Associations.Executable(Path.GetExtension(path)) is null);

    public void OpenWithShell(string path)
    {
        using var _ = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(path) ?? "" });
    }
}

public sealed class TerminalService(Func<AppSettings> settings, Action<string, bool>? sftpTerminal = null) : ITerminalService
{
    public void Open(string folder, TerminalKind kind, bool elevated)
    {
        if (Foldy.Presentation.Navigation.PathUtil.IsSftp(folder) && sftpTerminal is not null)
        {
            sftpTerminal(folder, elevated); // SFTP 탭: 같은 서버로 SSH, 관리자 버튼은 sudo 셸
            return;
        }

        var s = settings().Terminal;
        var shell = kind switch
        {
            TerminalKind.Cmd => ShellKind.Cmd,
            TerminalKind.PowerShell => s.Shell == "powershell" ? ShellKind.WindowsPowerShell : ShellKind.Pwsh,
            _ => s.Shell switch { "cmd" => ShellKind.Cmd, "powershell" => ShellKind.WindowsPowerShell, _ => ShellKind.Pwsh },
        };
        var useWt = s.Host != "console";
        Terminals.Open(folder, shell, elevated, useWt, elevated ? s.WtAdminProfile : s.WtProfile);
    }
}

/// <summary>드라이브 목록: 즉시(문자·종류) + 볼륨 이름·용량은 드라이브 큐에서 나중에 (끊긴 SMB가 있어도 막히지 않음).</summary>
public sealed class DriveService(DriveQueueRegistry queues) : IDriveService
{
    public IReadOnlyList<DriveItem> GetDrivesFast() =>
        [.. Drives.List()
            .Where(d => d.Type != NativeDriveType.NoRootDir)
            .Select(d => new DriveItem(d.Root, d.Type switch
            {
                NativeDriveType.Fixed => DriveKind.Fixed,
                NativeDriveType.Removable => DriveKind.Removable,
                NativeDriveType.Remote => DriveKind.Network,
                NativeDriveType.CdRom => DriveKind.CdRom,
                NativeDriveType.RamDisk => DriveKind.Ram,
                _ => DriveKind.Unknown,
            }))];

    public async Task<DriveItem> FillDetailsAsync(DriveItem drive, CancellationToken ct)
    {
        if (drive.Kind == DriveKind.CdRom)
        {
            return drive;
        }

        try
        {
            var details = await queues.ForPath(drive.Root)
                .Enqueue(_ => Drives.Details(drive.Root), WorkPriority.SizeCalc, ct)
                .WaitAsync(TimeSpan.FromSeconds(3), ct);
            var cloud = CloudVolumes.Remember(drive.Root, details.Label, details.FileSystem);
            return drive with { Label = details.Label, FreeBytes = (long?)details.Free, TotalBytes = (long?)details.Total, Cloud = cloud };
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or DriveUnresponsiveException or OperationCanceledException)
        {
            return drive with { Offline = true };
        }
    }
}

/// <summary>로컬 고정·이동식 드라이브만 FileSystemWatcher로 감시 (네트워크는 폴링 — M2).</summary>
public sealed class FolderWatchService : IFolderWatchService
{
    public IDisposable? Watch(string folder, Action changed)
    {
        if (folder.Length < 3 || folder[1] != ':' || Drives.TypeOf(folder[..3]) is not (NativeDriveType.Fixed or NativeDriveType.Removable))
        {
            return null;
        }

        try
        {
            var w = new FileSystemWatcher(folder)
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size | NotifyFilters.LastWrite | NotifyFilters.Attributes,
                InternalBufferSize = 64 * 1024,
            };
            FileSystemEventHandler h = (_, _) => changed();
            w.Created += h;
            w.Deleted += h;
            w.Changed += h;
            w.Renamed += (_, _) => changed();
            w.Error += (_, _) => changed();
            w.EnableRaisingEvents = true;
            return w;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

public static class TypeNames
{
    /// <summary>폴더·드라이브·확장자 없는 파일은 화면 언어 글, 나머지는 Windows가 주는 종류 이름(Windows 언어).</summary>
    public static string Get(FsEntry e) =>
        e.Kind is FsKind.Folder or FsKind.Drive || !System.IO.Path.HasExtension(e.Name)
            ? Foldy.Presentation.Files.FileItem.DefaultTypeName(e)
            : ShellInfo.TypeName(e.Name, false, ext => Foldy.Core.Localization.Loc.F("file.typeExt", ext));
}
