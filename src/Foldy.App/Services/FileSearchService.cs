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

    /// <summary>색인 상태를 확인해 둔다 (준비됨이면 색인 검색 사용).</summary>
    public async Task RefreshStateAsync()
    {
        try
        {
            _ready = indexer() is { } c && (await c.StatusAsync()).State == IndexState.Ready;
        }
        catch (Exception ex) when (ex is IOException or ProtocolException or OperationCanceledException)
        {
            _ready = false;
        }
    }

    public IAsyncEnumerable<IReadOnlyList<FsEntry>> SearchAsync(string query, string? scope, CancellationToken ct) =>
        Ready is { } c ? IndexedAsync(c, query, ct) : WalkAsync(query, scope, ct);

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
