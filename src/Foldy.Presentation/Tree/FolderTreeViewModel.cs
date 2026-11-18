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
            await foreach (var batch in _provider.ListBatchedAsync(FsPath.Local(Path), cts.Token))
            {
                folders.AddRange(batch.Where(e =>
                    (e.Kind == FsKind.Folder || (e.Kind == FsKind.Link && (e.Attributes & FileAttributes.Directory) != 0)) &&
                    (e.Attributes & (FileAttributes.Hidden | FileAttributes.System)) == 0));
            }

            folders.Sort((a, b) => Files.NaturalComparer.Instance.Compare(a.Name, b.Name));
            Children.Clear();
            foreach (var f in folders)
            {
                Children.Add(new FolderNode(_provider, f.Name, f.Path.Value));
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
}

/// <summary>트리 뿌리: 즐겨찾기(바탕 화면·문서·다운로드·사진) + 드라이브.</summary>
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

    public ObservableCollection<FolderNode> Roots { get; } = [];

    public void SetDrives(IEnumerable<DriveItem> drives)
    {
        for (var i = Roots.Count - 1; i >= 0; i--)
        {
            if (Roots[i].Path.Length == 3 && Roots[i].Path[1] == ':')
            {
                Roots.RemoveAt(i);
            }
        }

        foreach (var d in drives)
        {
            var glyph = d.Kind switch
            {
                DriveKind.Network => "",
                DriveKind.Removable => "",
                DriveKind.CdRom => "",
                _ => "",
            };
            Roots.Add(new FolderNode(_provider, d.Display, d.Root, glyph: glyph));
        }
    }
}
