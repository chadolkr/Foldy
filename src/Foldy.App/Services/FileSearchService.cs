using System.IO;
using System.IO.Enumeration;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Foldy.Core.FileSystem;
using Foldy.Indexer.Client;
using Foldy.Presentation.Tabs;

namespace Foldy.App.Services;

/// <summary>
/// 파일 이름 검색: 색인 서비스가 준비돼 있으면 전체 로컬 NTFS 드라이브를 즉시 검색하고(Everything 방식, 설계서 M2),
/// 없으면 지정 폴더 아래를 백그라운드에서 직접 훑는다 (권한 없음·서비스 미설치·NTFS 아님 폴백).
/// </summary>
public sealed class FileSearchService(Func<IndexerClient?> indexer, Func<bool> showHidden) : IFileSearchService
{
    public const int WalkLimit = 20_000;

    private IndexerClient? Ready => indexer() is { IsFaulted: false } c && _ready ? c : null;

    private bool _ready;

    public bool IsIndexed => Ready is not null;

    /// <summary>마지막으로 받은 색인 상태 (상태 줄·빠른 검색 안내용). 서비스에 연결되지 않았으면 null.</summary>
    public StatusReply? LastStatus { get; private set; }

    /// <summary>색인 상태를 확인해 둔다 (준비됨이면 색인 검색 사용).</summary>
    public async Task RefreshStateAsync()
    {
        try
        {
            LastStatus = indexer() is { IsFaulted: false } c ? await c.StatusAsync() : null;
            _ready = LastStatus?.State == IndexState.Ready;
        }
        catch (Exception ex) when (ex is IOException or ProtocolException or OperationCanceledException)
        {
            LastStatus = null;
            _ready = false;
        }
    }

    /// <summary>이 경로가 색인이 끝난 드라이브에 있나 (네트워크·보안 드라이브, NTFS가 아닌 드라이브는 색인하지 않는다).</summary>
    public bool IsIndexedPath(string? path)
    {
        if (string.IsNullOrEmpty(path) || path.Length < 2 || path[1] != ':' || LastStatus is not { } st)
        {
            return false;
        }

        var drive = path[..2].ToUpperInvariant();
        return st.Volumes.Any(v => v.State == IndexState.Ready && string.Equals(v.Volume.TrimEnd('\\'), drive, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 색인이 준비됐으면 색인으로 전체를 찾고, 지금 폴더가 색인 없는 드라이브(네트워크·회사 보안 드라이브 등)에 있으면
    /// 그 폴더 안도 함께 훑는다 (사용자 2026-11-21: "수집 안 되었을 때?"). 색인이 없으면 지금 폴더만 훑는다.
    /// </summary>
    public IAsyncEnumerable<IReadOnlyList<FsEntry>> SearchAsync(string query, string? scope, CancellationToken ct) =>
        Ready is { } c
            ? (!string.IsNullOrEmpty(scope) && !IsIndexedPath(scope) && !Foldy.Presentation.Navigation.PathUtil.IsSftp(scope)
                ? BothAsync(c, query, scope, ct)
                : IndexedAsync(c, query, ct))
            : WalkAsync(query, scope, ct);

    private async IAsyncEnumerable<IReadOnlyList<FsEntry>> BothAsync(IndexerClient c, string query, string scope, [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var b in IndexedAsync(c, query, ct))
        {
            yield return b;
        }

        await foreach (var b in WalkAsync(query, scope, ct))
        {
            yield return b;
        }
    }

    private static async IAsyncEnumerable<IReadOnlyList<FsEntry>> IndexedAsync(IndexerClient c, string query, [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var b in c.SearchStreamAsync(query, 5000, 0, ct))
        {
            if (b.Hits.Count > 0)
            {
                yield return [.. b.Hits.Select(h => new FsEntry(
                    Path.GetFileName(h.Path.TrimEnd('\\')),
                    FsPath.Local(h.Path),
                    h.IsDir ? FsKind.Folder : FsKind.File,
                    h.IsDir ? 0 : (long)h.Size,
                    (long)h.AllocSize,
                    h.LastWriteTimeUtc,
                    default,
                    h.IsDir ? FileAttributes.Directory : FileAttributes.Normal,
                    h.IsDir ? (long)h.Size : null))];
            }
        }
    }

    private async IAsyncEnumerable<IReadOnlyList<FsEntry>> WalkAsync(string query, string? scope, [EnumeratorCancellation] CancellationToken ct)
    {
        if (string.IsNullOrEmpty(scope))
        {
            yield break;
        }

        var hidden = showHidden();
        var ch = Channel.CreateUnbounded<IReadOnlyList<FsEntry>>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
        var opts = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint | (hidden ? 0 : FileAttributes.Hidden | FileAttributes.System),
            BufferSize = 64 * 1024,
        };
        _ = Task.Run(() =>
        {
            try
            {
                var e = new FileSystemEnumerable<FsEntry>(scope, (ref FileSystemEntry fe) => new FsEntry(
                    fe.FileName.ToString(), FsPath.Local(fe.ToFullPath()), fe.IsDirectory ? FsKind.Folder : FsKind.File,
                    fe.IsDirectory ? 0 : fe.Length, null, fe.LastWriteTimeUtc.UtcDateTime, fe.CreationTimeUtc.UtcDateTime, fe.Attributes, null), opts)
                {
                    ShouldIncludePredicate = (ref FileSystemEntry fe) => fe.FileName.Contains(query, StringComparison.CurrentCultureIgnoreCase),
                };
                var batch = new List<FsEntry>(200);
                var count = 0;
                var last = Environment.TickCount64;
                foreach (var item in e)
                {
                    ct.ThrowIfCancellationRequested();
                    batch.Add(item);
                    if (++count >= WalkLimit)
                    {
                        break;
                    }

                    if (batch.Count >= 200 || Environment.TickCount64 - last > 150)
                    {
                        ch.Writer.TryWrite(batch);
                        batch = new List<FsEntry>(200);
                        last = Environment.TickCount64;
                    }
                }

                ch.Writer.TryWrite(batch);
                ch.Writer.TryComplete();
            }
            catch (Exception ex)
            {
                ch.Writer.TryComplete(ex);
            }
        }, ct);

        await foreach (var b in ch.Reader.ReadAllAsync(ct))
        {
            yield return b;
        }
    }
}
