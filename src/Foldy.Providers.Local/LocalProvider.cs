using System.Diagnostics;
using System.IO.Enumeration;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Foldy.Core.FileSystem;
using Foldy.Core.Threading;

namespace Foldy.Providers.Local;

/// <summary>
/// 로컬·이동식·SMB 폴더 Provider (설계서 5.3).
/// 열거는 해당 드라이브의 <see cref="DriveWorkQueue"/> 스레드에서만 돌고, 결과는
/// <see cref="BatchSize"/>개 또는 <see cref="BatchInterval"/> 중 먼저 오는 쪽에서 묶음으로 내보낸다.
/// <para>
/// 열거는 .NET의 FileSystemEnumerable(NtQueryDirectoryFile 기반, FindFirstFileEx LARGE_FETCH와 같은 계층)을 쓴다.
/// 색인 서비스가 살아 있으면 M2에서 IndexedLocalProvider가 앞에 선다.
/// </para>
/// </summary>
public sealed class LocalProvider(DriveQueueRegistry queues) : IFileSystemProvider
{
    public const int BatchSize = 200;
    public static readonly TimeSpan BatchInterval = TimeSpan.FromMilliseconds(100);

    private static readonly EnumerationOptions Options = new()
    {
        IgnoreInaccessible = true,
        RecurseSubdirectories = false,
        ReturnSpecialDirectories = false,
        AttributesToSkip = 0,
        BufferSize = 64 * 1024,
    };

    public ProviderId Id => ProviderId.Local;

    public async IAsyncEnumerable<IReadOnlyList<FsEntry>> ListBatchedAsync(
        FsPath path, [EnumeratorCancellation] CancellationToken ct)
    {
        var channel = Channel.CreateUnbounded<IReadOnlyList<FsEntry>>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
        var queue = queues.ForPath(path.Value);

        var work = queue.Enqueue(c => Enumerate(path, channel.Writer, c), WorkPriority.ActiveTab, ct);
        _ = work.ContinueWith(
            static (t, s) => ((ChannelWriter<IReadOnlyList<FsEntry>>)s!).TryComplete(
                t.IsFaulted ? t.Exception!.InnerException : t.IsCanceled ? new OperationCanceledException() : null),
            channel.Writer,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        await foreach (var batch in channel.Reader.ReadAllAsync(ct))
        {
            yield return batch;
        }
    }

    /// <summary>드라이브 큐 스레드에서 실행된다.</summary>
    private static bool Enumerate(FsPath dir, ChannelWriter<IReadOnlyList<FsEntry>> writer, CancellationToken ct)
    {
        var provider = dir.Provider;
        var e = new FileSystemEnumerable<FsEntry>(
            dir.Value,
            (ref FileSystemEntry fe) => ToEntry(ref fe, provider),
            Options);

        var batch = new List<FsEntry>(BatchSize);
        var sw = Stopwatch.StartNew();
        foreach (var item in e)
        {
            batch.Add(item);
            if (batch.Count >= BatchSize || sw.Elapsed >= BatchInterval)
            {
                ct.ThrowIfCancellationRequested();
                writer.TryWrite(batch);
                batch = new List<FsEntry>(BatchSize);
                sw.Restart();
            }
        }

        if (batch.Count > 0)
        {
            writer.TryWrite(batch);
        }

        return true;
    }

    private static FsEntry ToEntry(ref FileSystemEntry fe, ProviderId provider)
    {
        var attrs = fe.Attributes;
        var kind = (attrs & FileAttributes.ReparsePoint) != 0 ? FsKind.Link
            : fe.IsDirectory ? FsKind.Folder
            : FsKind.File;
        return new FsEntry(
            fe.FileName.ToString(),
            new FsPath(provider, fe.ToFullPath()),
            kind,
            fe.IsDirectory ? 0 : fe.Length,
            null,
            fe.LastWriteTimeUtc.UtcDateTime,
            fe.CreationTimeUtc.UtcDateTime,
            attrs,
            null);
    }
}
