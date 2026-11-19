using System.Diagnostics;
using Foldy.Core.FileSystem;
using Foldy.Presentation.Navigation;
using Foldy.Presentation.Services;
using Renci.SshNet.Common;

namespace Foldy.Providers.Sftp;

public enum TransferConflictAction { Skip, Overwrite, Rename }

/// <summary>충돌 하나에 대한 사용자의 결정. ApplyToAll이면 이번 작업의 남은 충돌 전체에 같은 처리.</summary>
public readonly record struct TransferConflictDecision(TransferConflictAction Action, bool ApplyToAll = false);

/// <summary>대상에 같은 이름의 파일이 이미 있다.</summary>
public sealed record TransferConflict(string Source, string Dest);

public sealed class SftpTransferOptions
{
    /// <summary>충돌마다 불린다(UI 스레드에서 묻는 자리). 없으면 "둘 다 유지"(이름 뒤에 (2))로 처리한다.</summary>
    public Func<TransferConflict, CancellationToken, Task<TransferConflictDecision>>? ResolveConflict { get; init; }

    /// <summary>진행률 보고 최소 간격.</summary>
    public TimeSpan ProgressInterval { get; init; } = TimeSpan.FromMilliseconds(100);
}

/// <summary>
/// sftp:// 경로가 끼는 파일 작업을 처리하고, 순수 로컬 작업은 <c>local</c>(기존 서비스)로 넘기는 데코레이터.
/// <list type="bullet">
/// <item>로컬→SFTP 업로드, SFTP→로컬 다운로드(폴더는 재귀), SFTP→SFTP 복사는 임시 파일을 거친다.</item>
/// <item>같은 연결 안의 SFTP→SFTP 이동은 서버 쪽 이름 바꾸기.</item>
/// <item>이동 = 복사가 전부 성공한 뒤 원본 삭제. 실패·취소면 원본을 건드리지 않는다. 건너뛴 항목이 든 원본도 남긴다.</item>
/// <item>진행률은 바이트(Done/Total), 총량은 미리 재귀로 합산한다. 취소하면 쓰던 대상 파일을 지운다.</item>
/// <item>SFTP 삭제는 휴지통이 없다(호출자가 확인한다). recycle 값은 무시한다.</item>
/// </list>
/// </summary>
public sealed class SftpAwareFileOperationService(
    IFileOperationService local,
    SftpProvider sftp,
    SftpTransferOptions? options = null) : IFileOperationService
{
    private readonly SftpTransferOptions _options = options ?? new SftpTransferOptions();

    public Task<FileOpResult> CopyAsync(IReadOnlyList<string> sources, string destinationFolder, IProgress<FileOpProgress>? progress, CancellationToken ct) =>
        TransferRouting.Classify(sources, destinationFolder) == TransferRoute.Local
            ? local.CopyAsync(sources, destinationFolder, progress, ct)
            : TransferAsync(move: false, sources, destinationFolder, progress, ct);

    public Task<FileOpResult> MoveAsync(IReadOnlyList<string> sources, string destinationFolder, IProgress<FileOpProgress>? progress, CancellationToken ct) =>
        TransferRouting.Classify(sources, destinationFolder) == TransferRoute.Local
            ? local.MoveAsync(sources, destinationFolder, progress, ct)
            : TransferAsync(move: true, sources, destinationFolder, progress, ct);

    public async Task<FileOpResult> DeleteAsync(IReadOnlyList<string> paths, bool recycle, IProgress<FileOpProgress>? progress, CancellationToken ct)
    {
        if (!TransferRouting.TouchesSftp(paths))
        {
            return await local.DeleteAsync(paths, recycle, progress, ct).ConfigureAwait(false);
        }

        try
        {
            var localPaths = paths.Where(p => !PathUtil.IsSftp(p)).ToList();
            if (localPaths.Count > 0)
            {
                var lr = await local.DeleteAsync(localPaths, recycle, null, ct).ConfigureAwait(false);
                if (!lr.Succeeded)
                {
                    return lr;
                }
            }

            var remote = paths.Where(PathUtil.IsSftp).ToList();
            for (var i = 0; i < remote.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report(new FileOpProgress(i, remote.Count, PathUtil.DisplayName(remote[i])));
                await new RemoteEndpoint(sftp).DeleteTreeAsync(remote[i], ct).ConfigureAwait(false);
            }

            progress?.Report(new FileOpProgress(remote.Count, remote.Count, null));
            return FileOpResult.Ok();
        }
        catch (OperationCanceledException)
        {
            return FileOpResult.Canceled;
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            return FileOpResult.Fail(ex.Message);
        }
    }

    public async Task<FileOpResult> RenameAsync(string path, string newName, CancellationToken ct)
    {
        if (!PathUtil.IsSftp(path))
        {
            return await local.RenameAsync(path, newName, ct).ConfigureAwait(false);
        }

        try
        {
            var loc = SftpLocation.Parse(path);
            var parent = loc.Parent() ?? throw new IOException("루트는 이름을 바꿀 수 없습니다.");
            var target = parent.Combine(newName).ToString();
            if (await sftp.ExistsAsync(Remote(target), ct).ConfigureAwait(false))
            {
                return FileOpResult.Fail($"'{newName}' 이름이 이미 있습니다.");
            }

            await sftp.RenameAsync(Remote(path), Remote(target), ct).ConfigureAwait(false);
            return FileOpResult.Ok(target);
        }
        catch (OperationCanceledException)
        {
            return FileOpResult.Canceled;
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            return FileOpResult.Fail(ex.Message);
        }
    }

    public async Task<FileOpResult> NewFolderAsync(string parent, string name, CancellationToken ct)
    {
        if (!PathUtil.IsSftp(parent))
        {
            return await local.NewFolderAsync(parent, name, ct).ConfigureAwait(false);
        }

        try
        {
            var loc = SftpLocation.Parse(parent);
            var final = name;
            if (await sftp.ExistsAsync(Remote(loc.Combine(name).ToString()), ct).ConfigureAwait(false))
            {
                final = await ConflictNaming.UniqueAsync(name, true,
                    c => sftp.ExistsAsync(Remote(loc.Combine(c).ToString()), ct)).ConfigureAwait(false);
            }

            var target = loc.Combine(final).ToString();
            await sftp.CreateDirectoryAsync(Remote(target), ct).ConfigureAwait(false);
            return FileOpResult.Ok(target);
        }
        catch (OperationCanceledException)
        {
            return FileOpResult.Canceled;
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            return FileOpResult.Fail(ex.Message);
        }
    }

    // ───────────── 복사·이동 ─────────────

    private async Task<FileOpResult> TransferAsync(bool move, IReadOnlyList<string> sources, string dest, IProgress<FileOpProgress>? progress, CancellationToken ct)
    {
        if (sources.Count == 0)
        {
            return FileOpResult.Ok();
        }

        var run = new Run(progress, _options.ProgressInterval, _options.ResolveConflict);
        try
        {
            ct.ThrowIfCancellationRequested();
            var destEp = EndpointFor(dest);
            foreach (var s in sources.Where(PathUtil.IsSftp))
            {
                if (PathUtil.IsSftp(dest) && IsSameOrInside(s, dest))
                {
                    return FileOpResult.Fail($"'{PathUtil.DisplayName(s)}'을(를) 자기 자신 안으로 옮길 수 없습니다.");
                }
            }

            // 서버 안에서 이름만 바꾸면 되는 항목은 바이트가 없다. 나머지는 재귀로 총량을 미리 센다.
            var renames = move ? sources.Where(s => IsServerSideMove(s, dest)).ToHashSet(StringComparer.Ordinal) : [];
            foreach (var s in sources)
            {
                ct.ThrowIfCancellationRequested();
                run.Total += renames.Contains(s) ? 1 : await ScanAsync(EndpointFor(s), s, ct).ConfigureAwait(false) * Factor(s, dest);
            }

            run.Report(force: true);
            var toDelete = new List<string>();
            foreach (var s in sources)
            {
                ct.ThrowIfCancellationRequested();
                run.Skipped = false;
                if (renames.Contains(s))
                {
                    if (await TryServerSideMoveAsync(s, dest, run, ct).ConfigureAwait(false))
                    {
                        continue;
                    }
                }

                var srcEp = EndpointFor(s);
                var node = await srcEp.StatAsync(s, ct).ConfigureAwait(false)
                    ?? throw new FileNotFoundException($"원본을 찾을 수 없습니다: {s}");
                await CopyNodeAsync(srcEp, node, destEp, dest, Factor(s, dest), run, ct).ConfigureAwait(false);
                if (move && !run.Skipped)
                {
                    toDelete.Add(s);
                }
            }

            // 복사가 전부 성공한 뒤에만 원본을 지운다.
            foreach (var s in toDelete)
            {
                ct.ThrowIfCancellationRequested();
                await EndpointFor(s).DeleteTreeAsync(s, ct).ConfigureAwait(false);
            }

            run.Done = Math.Max(run.Done, run.Total);
            run.Current = null;
            run.Report(force: true);
            return run.SkippedLinks > 0
                ? new FileOpResult(true, false, $"심볼릭 링크 {run.SkippedLinks:N0}개는 건너뛰었습니다")
                : FileOpResult.Ok();
        }
        catch (OperationCanceledException)
        {
            return FileOpResult.Canceled;
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            return FileOpResult.Fail(ex.Message);
        }
    }

    /// <summary>원본과 대상이 같은 SFTP 연결이면 서버 쪽 이름 바꾸기로 옮긴다.</summary>
    private static bool IsServerSideMove(string source, string destFolder) =>
        PathUtil.IsSftp(source) && PathUtil.IsSftp(destFolder) && TransferRouting.SameConnection(source, destFolder);

    /// <summary>SFTP→SFTP 복사는 임시 파일을 거치므로 바이트가 두 번(내려받기 + 올리기) 든다.</summary>
    private static int Factor(string source, string dest) => PathUtil.IsSftp(source) && PathUtil.IsSftp(dest) ? 2 : 1;

    private static bool IsSameOrInside(string source, string dest)
    {
        if (!TransferRouting.SameConnection(source, dest))
        {
            return false;
        }

        var s = SftpLocation.Parse(source).Path;
        var d = SftpLocation.Parse(dest).Path;
        return d == s || d.StartsWith(s == "/" ? "/" : s + "/", StringComparison.Ordinal);
    }

    /// <summary>true면 처리했다. false면 일반 복사 후 삭제로 처리해야 한다(폴더 병합 등).</summary>
    private async Task<bool> TryServerSideMoveAsync(string source, string destFolder, Run run, CancellationToken ct)
    {
        var name = PathUtil.DisplayName(source);
        var destLoc = SftpLocation.Parse(destFolder);
        var target = destLoc.Combine(name).ToString();
        var src = Remote(source);
        run.Current = name;

        if (SftpLocation.Parse(source).Path == SftpLocation.Parse(target).Path)
        {
            run.Done += 1; // 같은 폴더로 옮기기: 할 일 없음
            run.Report();
            return true;
        }

        var existing = await sftp.StatAsync(Remote(target), ct).ConfigureAwait(false);
        var me = await sftp.StatAsync(src, ct).ConfigureAwait(false)
            ?? throw new FileNotFoundException($"원본을 찾을 수 없습니다: {source}");
        if (existing is not null)
        {
            if (existing.IsDirectory || me.IsDirectory)
            {
                return false; // 폴더 병합은 일반 경로(복사 후 삭제)가 한다.
            }

            switch (await run.DecideAsync(source, target, ct).ConfigureAwait(false))
            {
                case TransferConflictAction.Skip:
                    run.Skipped = true;
                    run.Done += 1;
                    run.Report();
                    return true;
                case TransferConflictAction.Overwrite:
                    await sftp.DeleteFileAsync(Remote(target), ct).ConfigureAwait(false);
                    break;
                default:
                    target = destLoc.Combine(await ConflictNaming.UniqueAsync(name, false,
                        c => sftp.ExistsAsync(Remote(destLoc.Combine(c).ToString()), ct)).ConfigureAwait(false)).ToString();
                    break;
            }
        }

        await sftp.RenameAsync(src, Remote(target), ct).ConfigureAwait(false);
        run.Done += 1;
        run.Report();
        return true;
    }

    private static async Task<long> ScanAsync(IEndpoint ep, string path, CancellationToken ct)
    {
        var node = await ep.StatAsync(path, ct).ConfigureAwait(false)
            ?? throw new FileNotFoundException($"원본을 찾을 수 없습니다: {path}");
        return await ScanNodeAsync(ep, node, ct).ConfigureAwait(false);
    }

    private static async Task<long> ScanNodeAsync(IEndpoint ep, Node node, CancellationToken ct)
    {
        if (!node.IsDir)
        {
            return node.Size;
        }

        long sum = 0;
        foreach (var child in await ep.ListAsync(node.Path, null, ct).ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();
            sum += await ScanNodeAsync(ep, child, ct).ConfigureAwait(false);
        }

        return sum;
    }

    private async Task CopyNodeAsync(IEndpoint src, Node node, IEndpoint dst, string destFolder, int factor, Run run, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var name = node.Name;
        var destPath = dst.Combine(destFolder, name);
        var existing = await dst.StatAsync(destPath, ct).ConfigureAwait(false);

        if (node.IsDir)
        {
            if (existing is { IsDir: false })
            {
                name = await UniqueNameAsync(dst, destFolder, name, true, ct).ConfigureAwait(false);
                destPath = dst.Combine(destFolder, name);
                existing = null;
            }

            if (existing is null)
            {
                await dst.MakeDirAsync(destPath, ct).ConfigureAwait(false);
            }

            foreach (var child in await src.ListAsync(node.Path, run, ct).ConfigureAwait(false))
            {
                await CopyNodeAsync(src, child, dst, destPath, factor, run, ct).ConfigureAwait(false);
            }

            return;
        }

        if (existing is not null)
        {
            if (existing.IsDir)
            {
                name = await UniqueNameAsync(dst, destFolder, name, false, ct).ConfigureAwait(false);
            }
            else
            {
                switch (await run.DecideAsync(node.Path, destPath, ct).ConfigureAwait(false))
                {
                    case TransferConflictAction.Skip:
                        run.Skipped = true;
                        run.Done += node.Size * factor;
                        run.Report();
                        return;
                    case TransferConflictAction.Rename:
                        name = await UniqueNameAsync(dst, destFolder, name, false, ct).ConfigureAwait(false);
                        break;
                    default:
                        break; // 덮어쓰기
                }
            }

            destPath = dst.Combine(destFolder, name);
        }

        await CopyFileAsync(src, node, dst, destPath, run, ct).ConfigureAwait(false);
    }

    private static Task<string> UniqueNameAsync(IEndpoint dst, string folder, string name, bool isFolder, CancellationToken ct) =>
        ConflictNaming.UniqueAsync(name, isFolder, async c => await dst.StatAsync(dst.Combine(folder, c), ct).ConfigureAwait(false) is not null);

    /// <summary>임시 이름(<c>.이름.foldy-part</c>)에 끝까지 쓴 뒤에만 대상과 바꿔 넣는다. 취소·실패하면 임시 파일만 지우고 옛 대상은 그대로다.</summary>
    internal static string PartName(string name) => "." + name + ".foldy-part";

    private async Task CopyFileAsync(IEndpoint src, Node node, IEndpoint dst, string destPath, Run run, CancellationToken ct)
    {
        run.Current = node.Name;
        var baseDone = run.Done;
        var first = new SyncProgress(n => { run.Done = baseDone + n; run.Report(); });
        var part = dst.Combine(dst.Parent(destPath), PartName(dst.NameOf(destPath)));
        string? temp = null;
        try
        {
            if (!src.IsRemote && dst.IsRemote)
            {
                var fs = new FileStream(node.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81920, useAsync: true);
                await using (fs.ConfigureAwait(false))
                {
                    await sftp.UploadAsync(fs, Remote(part), first, ct).ConfigureAwait(false);
                }
            }
            else if (src.IsRemote && !dst.IsRemote)
            {
                var fs = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
                await using (fs.ConfigureAwait(false))
                {
                    await sftp.DownloadAsync(Remote(node.Path), fs, first, ct).ConfigureAwait(false);
                }
            }
            else if (src.IsRemote && dst.IsRemote)
            {
                temp = Path.Combine(Path.GetTempPath(), "foldy-sftp-" + Guid.NewGuid().ToString("N") + ".tmp");
                var down = new FileStream(temp, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 81920, useAsync: true);
                await using (down.ConfigureAwait(false))
                {
                    await sftp.DownloadAsync(Remote(node.Path), down, first, ct).ConfigureAwait(false);
                    down.Position = 0;
                    var second = new SyncProgress(n => { run.Done = baseDone + node.Size + n; run.Report(); });
                    await sftp.UploadAsync(down, Remote(part), second, ct).ConfigureAwait(false);
                }
            }
            else
            {
                throw new InvalidOperationException("로컬끼리 복사는 이 서비스가 하지 않습니다.");
            }

            ct.ThrowIfCancellationRequested();
            await dst.ReplaceAsync(part, destPath, ct).ConfigureAwait(false);
        }
        catch
        {
            await dst.TryDeleteFileAsync(part).ConfigureAwait(false); // 임시 파일만 지운다. 옛 대상 파일은 건드리지 않는다.
            throw;
        }
        finally
        {
            if (temp is not null)
            {
                try
                {
                    File.Delete(temp);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }

        run.Done = baseDone + node.Size * (src.IsRemote && dst.IsRemote ? 2 : 1);
        run.Report();
    }

    // ───────────── 끝점(로컬 / 원격) ─────────────

    private IEndpoint EndpointFor(string path) => PathUtil.IsSftp(path) ? new RemoteEndpoint(sftp) : LocalEndpoint.Instance;

    private static FsPath Remote(string path) => new(ProviderId.Sftp, path);

    private static bool IsExpected(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or FormatException or SshException;

    private sealed record Node(string Name, string Path, bool IsDir, long Size);

    private interface IEndpoint
    {
        bool IsRemote { get; }

        string Combine(string folder, string name);

        Task<Node?> StatAsync(string path, CancellationToken ct);

        /// <summary>자식 목록. 심볼릭 링크·재분석 지점은 건너뛴다(순환 방지).</summary>
        Task<IReadOnlyList<Node>> ListAsync(string folder, Run? run, CancellationToken ct);

        Task MakeDirAsync(string path, CancellationToken ct);

        /// <summary>파일이나 폴더(하위 포함)를 지운다.</summary>
        Task DeleteTreeAsync(string path, CancellationToken ct);

        Task TryDeleteFileAsync(string path);

        string Parent(string path);

        string NameOf(string path);

        /// <summary>임시 파일을 목적지로 바꿔 넣는다(목적지가 있으면 교체).</summary>
        Task ReplaceAsync(string temp, string target, CancellationToken ct);
    }

    private sealed class LocalEndpoint : IEndpoint
    {
        public static readonly LocalEndpoint Instance = new();

        public bool IsRemote => false;

        public string Combine(string folder, string name) => Path.Combine(folder, name);

        public Task<Node?> StatAsync(string path, CancellationToken ct)
        {
            Node? n = null;
            if (File.Exists(path))
            {
                n = new Node(Path.GetFileName(path), path, false, new FileInfo(path).Length);
            }
            else if (Directory.Exists(path))
            {
                n = new Node(PathUtil.DisplayName(path), path, true, 0);
            }

            return Task.FromResult(n);
        }

        public Task<IReadOnlyList<Node>> ListAsync(string folder, Run? run, CancellationToken ct)
        {
            var list = new List<Node>();
            foreach (var e in new DirectoryInfo(folder).EnumerateFileSystemInfos())
            {
                if (e.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    run?.SkipLink();
                    continue;
                }

                list.Add(e is FileInfo f ? new Node(f.Name, f.FullName, false, f.Length) : new Node(e.Name, e.FullName, true, 0));
            }

            return Task.FromResult<IReadOnlyList<Node>>(list);
        }

        public Task MakeDirAsync(string path, CancellationToken ct)
        {
            Directory.CreateDirectory(path);
            return Task.CompletedTask;
        }

        public Task DeleteTreeAsync(string path, CancellationToken ct)
        {
            if (Directory.Exists(path))
            {
                ClearReadOnly(path);
                Directory.Delete(path, recursive: true);
            }
            else if (File.Exists(path))
            {
                File.SetAttributes(path, FileAttributes.Normal);
                File.Delete(path);
            }

            return Task.CompletedTask;
        }

        public string Parent(string path) => Path.GetDirectoryName(path) ?? path;

        public string NameOf(string path) => Path.GetFileName(path);

        public Task ReplaceAsync(string temp, string target, CancellationToken ct)
        {
            File.Move(temp, target, overwrite: true);
            return Task.CompletedTask;
        }

        public Task TryDeleteFileAsync(string path)
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }

            return Task.CompletedTask;
        }

        private static void ClearReadOnly(string dir)
        {
            foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                try
                {
                    File.SetAttributes(f, FileAttributes.Normal);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }
    }

    private sealed class RemoteEndpoint(SftpProvider sftp) : IEndpoint
    {
        public bool IsRemote => true;

        public string Combine(string folder, string name) => SftpLocation.Parse(folder).Combine(name).ToString();

        public async Task<Node?> StatAsync(string path, CancellationToken ct)
        {
            var info = await sftp.StatAsync(new FsPath(ProviderId.Sftp, path), ct).ConfigureAwait(false);
            return info is null ? null : new Node(PathUtil.DisplayName(path), path, info.IsDirectory, info.IsDirectory ? 0 : info.Size);
        }

        public async Task<IReadOnlyList<Node>> ListAsync(string folder, Run? run, CancellationToken ct)
        {
            var entries = await sftp.ListAsync(new FsPath(ProviderId.Sftp, folder), ct).ConfigureAwait(false);
            foreach (var _ in entries.Where(e => e.Kind == FsKind.Link))
            {
                run?.SkipLink();
            }

            return [.. entries.Where(e => e.Kind != FsKind.Link)
                .Select(e => new Node(e.Name, e.Path.Value, e.Kind == FsKind.Folder, e.Kind == FsKind.Folder ? 0 : e.Size))];
        }

        public Task MakeDirAsync(string path, CancellationToken ct) => sftp.CreateDirectoryAsync(new FsPath(ProviderId.Sftp, path), ct);

        public string Parent(string path) => (SftpLocation.Parse(path).Parent() ?? SftpLocation.Parse(path)).ToString();

        public string NameOf(string path) => PathUtil.DisplayName(path);

        public Task ReplaceAsync(string temp, string target, CancellationToken ct) =>
            sftp.ReplaceFileAsync(new FsPath(ProviderId.Sftp, temp), new FsPath(ProviderId.Sftp, target), ct);

        public async Task DeleteTreeAsync(string path, CancellationToken ct)
        {
            var fs = new FsPath(ProviderId.Sftp, path);
            var info = await sftp.StatAsync(fs, ct).ConfigureAwait(false);
            if (info is null)
            {
                return;
            }

            if (info.IsDirectory && !info.IsSymbolicLink)
            {
                foreach (var e in await sftp.ListAsync(fs, ct).ConfigureAwait(false))
                {
                    ct.ThrowIfCancellationRequested();
                    await DeleteTreeAsync(e.Path.Value, ct).ConfigureAwait(false);
                }

                await sftp.DeleteDirectoryAsync(fs, ct).ConfigureAwait(false);
            }
            else
            {
                await sftp.DeleteFileAsync(fs, ct).ConfigureAwait(false);
            }
        }

        public async Task TryDeleteFileAsync(string path)
        {
            try
            {
                await sftp.DeleteFileAsync(new FsPath(ProviderId.Sftp, path), CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsExpected(ex))
            {
                // 정리 실패는 원래 오류를 가리지 않는다.
            }
        }
    }

    private sealed class SyncProgress(Action<long> report) : IProgress<long>
    {
        public void Report(long value) => report(value);
    }

    /// <summary>작업 하나의 진행 상태와 충돌 결정 기억.</summary>
    private sealed class Run(
        IProgress<FileOpProgress>? progress,
        TimeSpan interval,
        Func<TransferConflict, CancellationToken, Task<TransferConflictDecision>>? resolver)
    {
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private long _lastReport = -1;
        private TransferConflictAction? _sticky;

        public long Total { get; set; }

        /// <summary>건너뛴 심볼릭 링크 수. 링크가 든 원본은 이동해도 지우지 않는다.</summary>
        public int SkippedLinks { get; private set; }

        public void SkipLink()
        {
            SkippedLinks++;
            Skipped = true;
        }

        public long Done { get; set; }

        public string? Current { get; set; }

        /// <summary>현재 최상위 원본 안에서 건너뛴 항목이 있다(이동이면 그 원본을 남긴다).</summary>
        public bool Skipped { get; set; }

        public void Report(bool force = false)
        {
            if (progress is null)
            {
                return;
            }

            var now = _clock.ElapsedMilliseconds;
            if (!force && _lastReport >= 0 && now - _lastReport < interval.TotalMilliseconds)
            {
                return;
            }

            _lastReport = now;
            progress.Report(new FileOpProgress(Done, Math.Max(Total, Done), Current));
        }

        public async Task<TransferConflictAction> DecideAsync(string source, string dest, CancellationToken ct)
        {
            if (_sticky is { } s)
            {
                return s;
            }

            if (resolver is null)
            {
                return TransferConflictAction.Rename; // 데이터를 잃지 않는 쪽
            }

            var d = await resolver(new TransferConflict(source, dest), ct).ConfigureAwait(false);
            if (d.ApplyToAll)
            {
                _sticky = d.Action;
            }

            return d.Action;
        }
    }
}
