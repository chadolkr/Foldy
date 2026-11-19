using System.Collections.Concurrent;
using Foldy.Providers.GoogleDrive;

namespace Foldy.GoogleDrive.Tests;

/// <summary>메모리 안의 Drive. id → 노드, 부모 하나. 호출 수를 센다.</summary>
internal sealed class FakeDriveApi : IGDriveApi
{
    private sealed class Node
    {
        public required GDriveFile File;
        public string? Parent;
        public byte[] Content = [];
        public bool Trashed;
    }

    private readonly ConcurrentDictionary<string, Node> _nodes = new();
    private int _next;

    public FakeDriveApi()
    {
        _nodes["root-id"] = new Node { File = new GDriveFile("root-id", "My Drive", GDriveFile.FolderMimeType, 0, T0, T0) };
    }

    public static readonly DateTime T0 = new(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);

    public int PageSize { get; set; } = 1000;

    public int ListCalls;
    public int FindCalls;
    public int DownloadCalls;
    public List<string> Log { get; } = [];

    public string Add(string parentId, string name, string mime = "text/plain", byte[]? content = null, string? shortcutTarget = null, string? shortcutMime = null)
    {
        var id = "id" + Interlocked.Increment(ref _next);
        content ??= [];
        _nodes[id] = new Node
        {
            File = new GDriveFile(id, name, mime, mime == GDriveFile.FolderMimeType || mime.StartsWith(GDriveFile.GoogleAppsPrefix, StringComparison.Ordinal) ? 0 : content.Length,
                T0.AddMinutes(_next), T0.AddMinutes(_next), "https://drive.google.com/file/d/" + id, shortcutTarget, shortcutMime),
            Parent = parentId,
            Content = content,
        };
        return id;
    }

    public string AddFolder(string parentId, string name) => Add(parentId, name, GDriveFile.FolderMimeType);

    public GDriveFile File(string id) => _nodes[id].File;

    public byte[] Content(string id) => _nodes[id].Content;

    public bool IsTrashed(string id) => _nodes[id].Trashed;

    public string? ParentOf(string id) => _nodes[id].Parent;

    public IReadOnlyList<GDriveFile> Children(string parentId) =>
        [.. _nodes.Values.Where(n => n.Parent == parentId && !n.Trashed).OrderBy(n => n.File.IsFolder ? 0 : 1).ThenBy(n => n.File.Name, StringComparer.Ordinal).ThenBy(n => n.File.CreatedUtc).Select(n => n.File)];

    public bool Exists(string id) => _nodes.ContainsKey(id);

    public Task<GDriveFile> GetAsync(string id, CancellationToken ct) =>
        Task.FromResult(_nodes.TryGetValue(id == "root" ? "root-id" : id, out var n) ? n.File : throw new FileNotFoundException(id));

    public Task<GDriveFilePage> ListChildrenAsync(string folderId, string? pageToken, CancellationToken ct)
    {
        Interlocked.Increment(ref ListCalls);
        var all = Children(folderId);
        var start = pageToken is null ? 0 : int.Parse(pageToken, System.Globalization.CultureInfo.InvariantCulture);
        var page = all.Skip(start).Take(PageSize).ToList();
        var next = start + page.Count < all.Count ? (start + page.Count).ToString(System.Globalization.CultureInfo.InvariantCulture) : null;
        return Task.FromResult(new GDriveFilePage(page, next));
    }

    public Task<IReadOnlyList<GDriveFile>> FindChildrenByNameAsync(string folderId, string name, CancellationToken ct)
    {
        Interlocked.Increment(ref FindCalls);
        return Task.FromResult<IReadOnlyList<GDriveFile>>([.. Children(folderId).Where(f => f.Name == name).OrderBy(f => f.CreatedUtc)]);
    }

    public async Task DownloadAsync(string id, Stream destination, IProgress<long>? progress, CancellationToken ct)
    {
        Interlocked.Increment(ref DownloadCalls);
        var c = _nodes[id].Content;
        await destination.WriteAsync(c, ct);
        progress?.Report(c.Length);
    }

    public async Task ExportAsync(string id, string mimeType, Stream destination, IProgress<long>? progress, CancellationToken ct)
    {
        Log.Add($"export {id} {mimeType}");
        var c = System.Text.Encoding.UTF8.GetBytes("exported:" + mimeType);
        await destination.WriteAsync(c, ct);
        progress?.Report(c.Length);
    }

    public async Task<GDriveFile> UploadAsync(string parentId, string name, Stream content, long length, IProgress<long>? progress, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        await content.CopyToAsync(ms, ct);
        progress?.Report(ms.Length);
        var id = Add(parentId, name, "application/octet-stream", ms.ToArray());
        Log.Add($"upload {name}");
        return _nodes[id].File;
    }

    public Task<GDriveFile> CreateFolderAsync(string parentId, string name, CancellationToken ct)
    {
        Log.Add($"mkdir {name}");
        return Task.FromResult(_nodes[AddFolder(parentId, name)].File);
    }

    public Task<GDriveFile> UpdateAsync(string id, GDriveUpdate update, CancellationToken ct)
    {
        var n = _nodes[id];
        if (update.Name is not null)
        {
            n.File = n.File with { Name = update.Name };
        }

        if (update.Trashed is { } t)
        {
            n.Trashed = t;
        }

        if (update.AddParent is not null)
        {
            if (update.RemoveParent != n.Parent)
            {
                throw new IOException("wrong parent");
            }

            n.Parent = update.AddParent;
        }

        Log.Add($"update {n.File.Name}");
        return Task.FromResult(n.File);
    }

    public Task<GDriveFile> CopyAsync(string id, string parentId, string name, CancellationToken ct)
    {
        var src = _nodes[id];
        var nid = Add(parentId, name, src.File.MimeType, src.Content);
        Log.Add($"copy {name}");
        return Task.FromResult(_nodes[nid].File);
    }

    public Task DeleteAsync(string id, CancellationToken ct)
    {
        _nodes.TryRemove(id, out _);
        Log.Add($"delete {id}");
        return Task.CompletedTask;
    }

    public Task<GDriveAbout> AboutAsync(CancellationToken ct) => Task.FromResult(new GDriveAbout("me@example.com", "Me", 15L << 30, 1L << 30));
}
