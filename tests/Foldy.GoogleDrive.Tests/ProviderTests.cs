using System.Text;
using Foldy.Core.FileSystem;
using Foldy.Providers.GoogleDrive;

namespace Foldy.GoogleDrive.Tests;

public class ProviderTests
{
    private const string Me = "gdrive://me@gmail.com/";
    private readonly FakeDriveApi _api = new();
    private readonly GoogleDriveProvider _p;

    public ProviderTests()
    {
        _p = new GoogleDriveProvider(a => a == "me@gmail.com" ? _api : null)
        {
            CacheDirectory = Path.Combine(Path.GetTempPath(), "foldy-gd-cache-" + Guid.NewGuid().ToString("N")),
        };
    }

    private async Task<List<FsEntry>> List(string path)
    {
        var all = new List<FsEntry>();
        await foreach (var b in _p.ListBatchedAsync(new FsPath(GoogleDriveProvider.GDrive, path), CancellationToken.None))
        {
            all.AddRange(b);
        }

        return all;
    }

    [Fact]
    public async Task Lists_root_with_folders_docs_duplicates_and_slashes()
    {
        _api.AddFolder("root-id", "Work");
        _api.Add("root-id", "a.txt", content: [1, 2, 3]);
        _api.Add("root-id", "a.txt", content: [4]);
        _api.Add("root-id", "Plan", "application/vnd.google-apps.document");
        _api.Add("root-id", "x/y");

        var items = await List(Me);
        items.Select(i => i.Name).Should().Equal("Work", "Plan.gdoc", "a.txt", "a (2).txt", "x∕y");
        items[0].Kind.Should().Be(FsKind.Folder);
        items[0].Attributes.Should().HaveFlag(FileAttributes.Directory);
        items[2].Size.Should().Be(3);
        items[3].Path.Value.Should().Be(Me + "a (2).txt");
        items.Should().OnlyContain(i => i.Path.Provider == GoogleDriveProvider.GDrive);
    }

    [Fact]
    public async Task Pages_are_streamed_in_batches()
    {
        _api.PageSize = 150;
        for (var i = 0; i < 450; i++)
        {
            _api.Add("root-id", $"f{i:000}");
        }

        var batches = new List<int>();
        await foreach (var b in _p.ListBatchedAsync(new FsPath(GoogleDriveProvider.GDrive, Me), CancellationToken.None))
        {
            batches.Add(b.Count);
        }

        batches.Sum().Should().Be(450);
        _api.ListCalls.Should().Be(3);
        batches.Should().OnlyContain(n => n <= GoogleDriveProvider.BatchSize);
    }

    [Fact]
    public async Task Resolves_nested_path_by_name_without_listing()
    {
        var work = _api.AddFolder("root-id", "Work");
        var sub = _api.AddFolder(work, "Sub");
        var f = _api.Add(sub, "r.md");

        (await _p.ResolveAsync(Me + "Work/Sub/r.md", CancellationToken.None)).Id.Should().Be(f);
        _api.ListCalls.Should().Be(0);
    }

    [Fact]
    public async Task Resolves_duplicate_and_doc_names_by_listing_parent()
    {
        _api.Add("root-id", "a.txt");
        var second = _api.Add("root-id", "a.txt");
        var doc = _api.Add("root-id", "Plan", "application/vnd.google-apps.spreadsheet");

        (await _p.ResolveAsync(Me + "a (2).txt", CancellationToken.None)).Id.Should().Be(second);
        (await _p.ResolveAsync(Me + "Plan.gsheet", CancellationToken.None)).Id.Should().Be(doc);
        var missing = () => _p.ResolveAsync(Me + "nope.txt", CancellationToken.None);
        await missing.Should().ThrowAsync<FileNotFoundException>();
    }

    [Fact]
    public async Task Shortcut_to_folder_is_navigable()
    {
        var target = _api.AddFolder("root-id", "Real");
        _api.Add(target, "inside.txt");
        _api.Add("root-id", "Link", GDriveFile.ShortcutMimeType, shortcutTarget: target, shortcutMime: GDriveFile.FolderMimeType);

        var items = await List(Me + "Link");
        items.Select(i => i.Name).Should().Equal("inside.txt");
    }

    [Fact]
    public async Task Unconnected_account_throws_auth()
    {
        var act = () => List("gdrive://other@gmail.com/");
        await act.Should().ThrowAsync<GDriveAuthException>();
    }

    [Fact]
    public async Task Rename_create_and_trash()
    {
        var f = _api.Add("root-id", "old.txt");
        var doc = _api.Add("root-id", "Notes", "application/vnd.google-apps.document");
        _api.Add("root-id", "taken.txt");

        (await _p.RenameAsync(Me + "old.txt", "new.txt", CancellationToken.None)).Should().Be(Me + "new.txt");
        _api.File(f).Name.Should().Be("new.txt");
        var clash = () => _p.RenameAsync(Me + "new.txt", "taken.txt", CancellationToken.None);
        await clash.Should().ThrowAsync<IOException>();

        (await _p.RenameAsync(Me + "Notes.gdoc", "Memo.gdoc", CancellationToken.None)).Should().Be(Me + "Memo.gdoc");
        _api.File(doc).Name.Should().Be("Memo");

        (await _p.CreateFolderAsync(Me, "New", CancellationToken.None)).Should().Be(Me + "New");
        var dup = () => _p.CreateFolderAsync(Me, "New", CancellationToken.None);
        await dup.Should().ThrowAsync<IOException>();

        await _p.DeleteAsync(Me + "new.txt", permanent: false, CancellationToken.None);
        _api.IsTrashed(f).Should().BeTrue();
        (await List(Me)).Select(i => i.Name).Should().NotContain("new.txt");
    }

    [Fact]
    public async Task Fetch_to_cache_downloads_once_and_exports_docs()
    {
        var f = _api.Add("root-id", "a.txt", content: Encoding.UTF8.GetBytes("hello"));
        _api.Add("root-id", "Plan", "application/vnd.google-apps.document");
        try
        {
            var local = await _p.FetchToCacheAsync(Me + "a.txt", null, CancellationToken.None);
            File.ReadAllText(local).Should().Be("hello");
            Path.GetFileName(local).Should().Be("a.txt");
            _ = await _p.FetchToCacheAsync(Me + "a.txt", null, CancellationToken.None);
            _api.DownloadCalls.Should().Be(1);

            var doc = await _p.FetchToCacheAsync(Me + "Plan.gdoc", null, CancellationToken.None);
            Path.GetFileName(doc).Should().Be("Plan.docx");
            _api.Log.Should().Contain(l => l.StartsWith("export", StringComparison.Ordinal) && l.Contains("wordprocessingml", StringComparison.Ordinal));
            _ = f;
        }
        finally
        {
            foreach (var file in Directory.EnumerateFiles(_p.CacheDirectory, "*", SearchOption.AllDirectories))
            {
                new FileInfo(file).IsReadOnly = false;
            }

            Directory.Delete(_p.CacheDirectory, true);
        }
    }

    [Fact]
    public void Local_names_are_windows_safe() =>
        GoogleDriveProvider.LocalFileName(new GDriveFile("i", "a:b?.", "text/plain", 0, default, default)).Should().Be("a_b_");
}

public sealed class TransferTests : IDisposable
{
    private const string Me = "gdrive://me@gmail.com/";
    private readonly FakeDriveApi _api = new();
    private readonly FakeDriveApi _other = new();
    private readonly GoogleDriveProvider _p;
    private readonly GDriveTransfer _t;
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("foldy-gd-t");

    public TransferTests()
    {
        _p = new GoogleDriveProvider(a => a switch { "me@gmail.com" => _api, "other@gmail.com" => _other, _ => null });
        _t = new GDriveTransfer(_p);
    }

    public void Dispose() => _dir.Delete(true);

    [Fact]
    public async Task Uploads_local_tree_with_progress_and_renames_on_conflict()
    {
        var src = Directory.CreateDirectory(Path.Combine(_dir.FullName, "Proj"));
        File.WriteAllText(Path.Combine(src.FullName, "a.txt"), "aaaa");
        Directory.CreateDirectory(Path.Combine(src.FullName, "sub"));
        File.WriteAllText(Path.Combine(src.FullName, "sub", "b.txt"), "bb");
        _api.AddFolder("root-id", "Proj");

        long last = 0, total = 0;
        var err = await _t.TransferAsync([src.FullName], Me, move: false, (d, t, _) => { last = d; total = t; }, CancellationToken.None);

        err.Should().BeNull();
        last.Should().Be(total);
        total.Should().Be(4 + 2 + 4); // 바이트 + 항목 4개(폴더 2, 파일 2)
        var top = _api.Children("root-id").Select(f => f.Name);
        top.Should().BeEquivalentTo(["Proj", "Proj (2)"]);
        var copy = _api.Children("root-id").Single(f => f.Name == "Proj (2)");
        _api.Children(copy.Id).Select(f => f.Name).Should().BeEquivalentTo(["sub", "a.txt"]);
        var sub = _api.Children(copy.Id).Single(f => f.Name == "sub");
        Encoding.UTF8.GetString(_api.Content(_api.Children(sub.Id).Single().Id)).Should().Be("bb");
        Directory.Exists(src.FullName).Should().BeTrue();
    }

    [Fact]
    public async Task Move_local_to_drive_removes_source_after_upload()
    {
        var f = Path.Combine(_dir.FullName, "m.bin");
        File.WriteAllBytes(f, [1, 2, 3]);
        (await _t.TransferAsync([f], Me, move: true, null, CancellationToken.None)).Should().BeNull();
        File.Exists(f).Should().BeFalse();
        _api.Children("root-id").Single().Name.Should().Be("m.bin");
    }

    [Fact]
    public async Task Downloads_drive_folder_exporting_docs_and_linking_forms()
    {
        var folder = _api.AddFolder("root-id", "Share");
        _api.Add(folder, "x.txt", content: Encoding.UTF8.GetBytes("x"));
        _api.Add(folder, "x.txt", content: Encoding.UTF8.GetBytes("y"));
        _api.Add(folder, "Sheet", "application/vnd.google-apps.spreadsheet");
        _api.Add(folder, "Survey", "application/vnd.google-apps.form");

        (await _t.TransferAsync([Me + "Share"], _dir.FullName, move: false, null, CancellationToken.None)).Should().BeNull();

        var outDir = Path.Combine(_dir.FullName, "Share");
        Directory.EnumerateFiles(outDir).Select(Path.GetFileName).Should().BeEquivalentTo(["x.txt", "x (2).txt", "Sheet.xlsx", "Survey.url"]);
        File.ReadAllText(Path.Combine(outDir, "Sheet.xlsx")).Should().Contain("spreadsheetml");
        File.ReadAllText(Path.Combine(outDir, "Survey.url")).Should().Contain("URL=https://drive.google.com/");
        Directory.EnumerateFiles(outDir, "*.foldy-part").Should().BeEmpty();
    }

    [Fact]
    public async Task Move_from_drive_to_local_trashes_source()
    {
        var f = _api.Add("root-id", "t.txt", content: [9]);
        (await _t.TransferAsync([Me + "t.txt"], _dir.FullName, move: true, null, CancellationToken.None)).Should().BeNull();
        File.ReadAllBytes(Path.Combine(_dir.FullName, "t.txt")).Should().Equal(9);
        _api.IsTrashed(f).Should().BeTrue();
    }

    [Fact]
    public async Task Move_within_account_only_changes_parent()
    {
        var a = _api.AddFolder("root-id", "A");
        var b = _api.AddFolder("root-id", "B");
        var f = _api.Add(a, "doc.txt", content: [1]);
        _api.Add(b, "doc.txt");

        (await _t.TransferAsync([Me + "A/doc.txt"], Me + "B", move: true, null, CancellationToken.None)).Should().BeNull();
        _api.ParentOf(f).Should().Be(b);
        _api.File(f).Name.Should().Be("doc (2).txt");
        _api.DownloadCalls.Should().Be(0);
    }

    [Fact]
    public async Task Copy_within_account_uses_server_copy()
    {
        var a = _api.AddFolder("root-id", "A");
        _api.Add(a, "one.txt", content: [1]);
        _api.Add(a, "Doc", "application/vnd.google-apps.document");

        (await _t.TransferAsync([Me + "A"], Me, move: false, null, CancellationToken.None)).Should().BeNull();
        _api.Log.Should().Contain(["copy one.txt", "copy Doc"]);
        _api.DownloadCalls.Should().Be(0);
        _api.Children("root-id").Select(f => f.Name).Should().BeEquivalentTo(["A", "A (2)"]);
    }

    [Fact]
    public async Task Copy_across_accounts_downloads_and_uploads()
    {
        _api.Add("root-id", "Doc", "application/vnd.google-apps.document");
        _api.Add("root-id", "p.bin", content: [1, 2]);

        (await _t.TransferAsync([Me + "Doc.gdoc", Me + "p.bin"], "gdrive://other@gmail.com/", move: false, null, CancellationToken.None)).Should().BeNull();
        _other.Children("root-id").Select(f => f.Name).Should().BeEquivalentTo(["Doc.docx", "p.bin"]);
    }

    [Fact]
    public async Task Refuses_to_copy_folder_into_itself()
    {
        var a = _api.AddFolder("root-id", "A");
        _api.AddFolder(a, "B");
        (await _t.TransferAsync([Me + "A"], Me + "A/B", move: false, null, CancellationToken.None)).Should().Contain("자기 안");
    }

    [Fact]
    public async Task Cancel_leaves_no_partial_file()
    {
        _api.Add("root-id", "big.bin", content: new byte[10]);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var act = () => _t.TransferAsync([Me + "big.bin"], _dir.FullName, false, null, cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
        Directory.EnumerateFiles(_dir.FullName).Should().BeEmpty();
    }
}
