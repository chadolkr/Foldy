using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Foldy.Core.FileSystem;
using Foldy.Presentation.Services;

namespace Foldy.Presentation.Tree;

/// <summary>폴더 트리 노드. 펼칠 때만 하위 폴더를 불러온다 (지연 로드, 설계서 6.1 FolderTreeViewModel).</summary>
public sealed partial class FolderNode : ObservableObject
{
    private static readonly FolderNode Placeholder = new(null!, "…", "", isPlaceholder: true);
    private readonly IFileSystemProvider? _provider;
    private bool _loaded;
    private CancellationTokenSource? _cts;

    [ObservableProperty]
    private bool _isExpanded;

    [ObservableProperty]
    private bool _isLoading;

    public FolderNode(IFileSystemProvider? provider, string name, string path, bool isPlaceholder = false, string? glyph = null)
    {
        _provider = provider;
        Name = name;
        Path = path;
        Glyph = glyph ?? "";
        if (!isPlaceholder)
        {
            Children.Add(Placeholder); // 펼침 화살표가 보이게
        }
    }

    public string Name { get; }

    public string Path { get; }

    /// <summary>Segoe Fluent Icons 글리프.</summary>
    public string Glyph { get; }

    public ObservableCollection<FolderNode> Children { get; } = [];

    partial void OnIsExpandedChanged(bool value)
    {
        if (value && !_loaded)
        {
            _ = LoadAsync();
        }
    }

    public async Task LoadAsync()
    {
        if (_provider is null || string.IsNullOrEmpty(Path))
        {
            return;
        }

        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        IsLoading = true;
        var folders = new List<FsEntry>();
        try
        {
            var at = Navigation.PathUtil.IsSftp(Path) ? new FsPath(ProviderId.Sftp, Path) : FsPath.Local(Path);
            await foreach (var batch in _provider.ListBatchedAsync(at, cts.Token))
            {
                folders.AddRange(batch.Where(e =>
                    (e.Kind == FsKind.Folder || (e.Kind == FsKind.Link && (e.Attributes & FileAttributes.Directory) != 0)) &&
                    (e.Attributes & (FileAttributes.Hidden | FileAttributes.System)) == 0));
            }

            folders.Sort((a, b) => Files.NaturalComparer.Instance.Compare(a.Name, b.Name));
            Children.Clear();
            foreach (var f in folders)
            {
                Children.Add(new FolderNode(_provider, f.Name, Navigation.PathUtil.IsSftp(Path) ? JoinSftp(Path, f.Name) : f.Path.Value));
            }

            _loaded = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            Children.Clear();
        }
        finally
        {
            IsLoading = false;
        }
    }

    private static string JoinSftp(string parent, string name) => parent.TrimEnd('/') + "/" + name;
}

/// <summary>
/// 트리 뿌리: 즐겨찾기(바탕 화면·문서·다운로드·사진) + 드라이브.
/// 패널의 현재 탭이 SFTP면 <see cref="ShowFor"/>가 그 서버의 트리(처음 연 폴더 + /)로 바꾸고, 로컬 탭이면 되돌린다.
/// </summary>
public sealed class FolderTreeViewModel
{
    private readonly IFileSystemProvider _provider;

    public FolderTreeViewModel(IFileSystemProvider provider, IEnumerable<DriveItem> drives)
    {
        _provider = provider;
        (Environment.SpecialFolder Folder, string Name, string Glyph)[] quick =
        [
            (Environment.SpecialFolder.Desktop, "바탕 화면", ""),
            (Environment.SpecialFolder.MyDocuments, "문서", ""),
            (Environment.SpecialFolder.UserProfile, "다운로드", ""),
            (Environment.SpecialFolder.MyPictures, "사진", ""),
        ];
        foreach (var (folder, name, glyph) in quick)
        {
            var p = Environment.GetFolderPath(folder);
            if (name == "다운로드")
            {
                p = System.IO.Path.Combine(p, "Downloads");
            }

            if (!string.IsNullOrEmpty(p))
            {
                Roots.Add(new FolderNode(provider, name, p, glyph: glyph));
            }
        }

        SetDrives(drives);
    }

    private readonly List<FolderNode> _local = [];
    private readonly Dictionary<string, string> _sftpStart = new(StringComparer.OrdinalIgnoreCase);

    public ObservableCollection<FolderNode> Roots { get; } = [];

    /// <summary>지금 보여 주는 SFTP 서버 뿌리(sftp://user@host:port/). 로컬 트리면 null.</summary>
    public string? SftpRoot { get; private set; }

    /// <summary>패널의 현재 위치에 맞춰 트리를 고른다. 같은 서버 안에서 옮겨 다닐 때는 그대로 둔다.</summary>
    public void ShowFor(string? location)
    {
        var root = Navigation.PathUtil.SftpRoot(location);
        if (root is null)
        {
            if (SftpRoot is not null)
            {
                SftpRoot = null;
                Replace(_local);
            }

            return;
        }

        _sftpStart.TryAdd(root, location!);
        if (string.Equals(root, SftpRoot, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        SftpRoot = root;
        var server = root[Navigation.PathUtil.SftpScheme.Length..].TrimEnd('/');
        var nodes = new List<FolderNode>();
        var start = _sftpStart[root];
        if (!string.Equals(start.TrimEnd('/'), root.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
        {
            nodes.Add(new FolderNode(_provider, Navigation.PathUtil.DisplayName(start), start, glyph: "\uE80F"));
        }

        nodes.Add(new FolderNode(_provider, server + " (/)", root, glyph: "\uE968"));
        Replace(nodes);
    }

    private void Replace(IEnumerable<FolderNode> nodes)
    {
        Roots.Clear();
        foreach (var n in nodes)
        {
            Roots.Add(n);
        }
    }

    public void SetDrives(IEnumerable<DriveItem> drives)
    {
        if (_local.Count == 0)
        {
            _local.AddRange(Roots); // 즐겨찾기 (생성자에서 넣은 것)
        }

        _local.RemoveAll(n => n.Path.Length == 3 && n.Path[1] == ':');

        foreach (var d in drives)
        {
            var glyph = d.Kind switch
            {
                DriveKind.Network => "",
                DriveKind.Removable => "",
                DriveKind.CdRom => "",
                _ => "",
            };
            _local.Add(new FolderNode(_provider, d.Display, d.Root, glyph: glyph));
        }

        if (SftpRoot is null)
        {
            Replace(_local);
        }
    }
}
