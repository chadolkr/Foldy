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

    /// <summary>지금 패널이 보고 있는 폴더 (트리에서 강조).</summary>
    [ObservableProperty]
    private bool _isSelected;

    /// <summary>하위 폴더를 아직 안 불러왔으면 불러온다 (이미 불러왔으면 바로 끝).</summary>
    public Task EnsureLoadedAsync() => _loaded ? Task.CompletedTask : LoadAsync();

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

    /// <summary>트리 항목의 화면 낭독기 이름 (폴더 이름).</summary>
    public override string ToString() => Name;

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
                var childPath = Navigation.PathUtil.IsSftp(Path) ? JoinSftp(Path, f.Name) : f.Path.Value;
                Children.Add(new FolderNode(_provider, f.Name, childPath, glyph: Foldy.Core.Managed.ManagedFolders.IsRoot(childPath) ? ManagedGlyph : null));
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

    /// <summary>관리 방식을 정한 폴더의 트리 표시 (깃발).</summary>
    public const string ManagedGlyph = "";

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

        // 접힌 채로 두면 서버에 무엇이 있는지 안 보인다: 처음 연 폴더와 / 를 한 단계 펼친다 (폴더마다 목록 한 번, 느린 서버는 각자 불러오는 중 표시)
        foreach (var n in nodes)
        {
            n.IsExpanded = true;
        }
    }

    private void Replace(IEnumerable<FolderNode> nodes)
    {
        Roots.Clear();
        foreach (var n in nodes)
        {
            Roots.Add(n);
        }
    }

    private int _revealVersion;
    private FolderNode? _selected;

    /// <summary>
    /// 패널의 지금 폴더를 트리에서 찾아 위 폴더들을 펼치고 그 폴더를 강조한다 (사용자 2026-11-21: "양쪽 탐색기에 현재 폴더 위치가 표시되어야").
    /// 폴더 목록은 펼칠 때처럼 비동기로 불러온다(느린 드라이브도 화면을 막지 않음). 더 새 요청이 오면 그만둔다.
    /// 돌려주는 것: 뿌리부터 그 폴더까지의 노드 (화면이 스크롤해 보이게 할 때 쓴다). 트리에 없는 곳이면 빈 목록.
    /// </summary>
    public async Task<IReadOnlyList<FolderNode>> RevealAsync(string? path)
    {
        var version = ++_revealVersion;
        if (string.IsNullOrEmpty(path))
        {
            Select(null);
            return [];
        }

        // 가장 깊이 맞는 뿌리 (문서·바탕 화면 같은 즐겨찾기가 드라이브보다 가깝다)
        var node = Roots.Where(r => Under(path, r.Path)).OrderByDescending(r => r.Path.TrimEnd('\\', '/').Length).FirstOrDefault();
        var chain = new List<FolderNode>();
        while (node is not null)
        {
            chain.Add(node);
            if (Same(node.Path, path))
            {
                break;
            }

            await node.EnsureLoadedAsync();
            if (version != _revealVersion)
            {
                return [];
            }

            node.IsExpanded = true;
            node = node.Children.FirstOrDefault(c => !string.IsNullOrEmpty(c.Path) && Under(path, c.Path));
        }

        if (version != _revealVersion)
        {
            return [];
        }

        Select(chain.Count > 0 && Same(chain[^1].Path, path) ? chain[^1] : null);
        return chain;
    }

    private void Select(FolderNode? node)
    {
        if (ReferenceEquals(_selected, node))
        {
            return;
        }

        if (_selected is not null)
        {
            _selected.IsSelected = false;
        }

        _selected = node;
        if (node is not null)
        {
            node.IsSelected = true;
        }
    }

    private static string Norm(string p) => p.Length > 3 || Navigation.PathUtil.IsSftp(p) ? p.TrimEnd('\\', '/') : p;

    public static bool Same(string a, string b) => string.Equals(Norm(a), Norm(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>path가 folder 자신이거나 그 아래인가.</summary>
    public static bool Under(string path, string folder)
    {
        if (Same(path, folder))
        {
            return true;
        }

        var f = folder.TrimEnd('\\', '/');
        if (!path.StartsWith(f, StringComparison.OrdinalIgnoreCase) || path.Length <= f.Length)
        {
            return false;
        }

        var c = path[f.Length];
        return c is '\\' or '/';
    }

    public void SetDrives(IEnumerable<DriveItem> drives)
    {
        if (_local.Count == 0)
        {
            _local.AddRange(Roots); // 즐겨찾기 (생성자에서 넣은 것)
        }

        // 드라이브 목록은 색인 상태 글 등으로 자주 다시 온다: 같은 드라이브는 그 노드를 그대로 써서 펼친 상태·강조를 지킨다
        // (예전에는 매번 새 노드로 바꿔 트리가 저절로 접혔다)
        var old = _local.Where(n => n.Path.Length == 3 && n.Path[1] == ':').ToList();
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
            var keep = old.FirstOrDefault(n => string.Equals(n.Path, d.Root, StringComparison.OrdinalIgnoreCase) && n.Name == d.Display && n.Glyph == glyph);
            _local.Add(keep ?? new FolderNode(_provider, d.Display, d.Root, glyph: glyph));
        }

        if (SftpRoot is null && !Roots.SequenceEqual(_local))
        {
            Replace(_local);
        }
    }
}
