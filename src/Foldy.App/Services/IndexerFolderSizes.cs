using System.IO;
using Foldy.Indexer.Client;
using Foldy.Presentation.Tabs;

namespace Foldy.App.Services;

/// <summary>색인 서비스에서 폴더 크기(하위 전체)를 가져온다. 색인 밖이거나 서비스가 없으면 null.</summary>
public sealed class IndexerFolderSizes(Func<IndexerClient?> client) : IFolderSizeProvider
{
    public async Task<IReadOnlyDictionary<string, long>?> GetAsync(string folder, CancellationToken ct)
    {
        if (client() is not { IsFaulted: false } c || folder.Length < 3 || folder[1] != ':')
        {
            return null;
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            var r = await c.FolderSizesAsync(folder, timeout.Token);
            if (r.Status != FolderSizesStatus.Ok)
            {
                return null;
            }

            var map = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in r.Entries)
            {
                if (e.IsDir)
                {
                    map[e.Name] = (long)e.Size;
                }
            }

            return map;
        }
        catch (Exception ex) when (ex is IOException or ProtocolException or OperationCanceledException)
        {
            return null;
        }
    }
}
