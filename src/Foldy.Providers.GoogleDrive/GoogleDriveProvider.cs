using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Foldy.Core.FileSystem;

namespace Foldy.Providers.GoogleDrive;

/// <summary>
/// Google Drive Provider. 경로는 <c>gdrive://계정/폴더/파일</c> (<see cref="GDriveLocation"/>).
/// <list type="bullet">
/// <item>Drive는 경로가 아니라 id로 동작하므로, 목록을 읽을 때 (경로 → 파일) 캐시를 채우고 경로를 id로 바꿀 때 쓴다.
/// 캐시에 없으면 이름으로 찾고, 그래도 없으면 부모 폴더를 다시 읽는다.</item>
/// <item>같은 폴더의 같은 이름은 "이름 (2).확장자"로 구분해 보여 준다(Drive는 같은 이름을 허용한다).</item>
/// <item>Google 문서·시트·슬라이드는 .gdoc/.gsheet/.gslides처럼 보여 주고, 내려받을 때 Office 형식으로 내보낸다.</item>
/// <item>바로가기는 대상(폴더·파일)처럼 다룬다.</item>
/// </list>
/// 스레드 소유권: 공개 멤버는 아무 스레드에서나 호출 가능. 네트워크 호출은 모두 비동기다(UI·드라이브 큐를 막지 않는다).
/// </summary>
public sealed class GoogleDriveProvider : IFileSystemProvider
{
    public static readonly ProviderId GDrive = new("gdrive");

    public const int BatchSize = 200;

    private readonly Func<string, IGDriveApi?> _apiFor;
    private readonly ConcurrentDictionary<string, GDriveFile> _nodes = new(StringComparer.Ordinal);

    /// <param name="apiFor">계정 이메일 → API. 연결하지 않은 계정이면 null (<see cref="GDriveAuthException"/>).</param>
    public GoogleDriveProvider(Func<string, IGDriveApi?> apiFor)
    {
        _apiFor = apiFor;
    }

    public ProviderId Id => GDrive;

    /// <summary>열기·미리보기용 로컬 캐시 폴더 (<c>%LOCALAPPDATA%\Foldy\gdrive-cache</c>).</summary>
    public string CacheDirectory { get; init; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Foldy", "gdrive-cache");

    public IGDriveApi Api(string account) =>
        _apiFor(account) ?? throw new GDriveAuthException($"Google Drive 계정이 연결되어 있지 않습니다: {account}");

    // ───────────── 목록 ─────────────

    public async IAsyncEnumerable<IReadOnlyList<FsEntry>> ListBatchedAsync(FsPath path, [EnumeratorCancellation] CancellationToken ct)
    {
        var loc = GDriveLocation.Parse(path.Value);
        var folder = await ResolveAsync(loc, ct).ConfigureAwait(false);
        if (!folder.IsFolder)
        {
            throw new DirectoryNotFoundException($"폴더가 아닙니다: {loc.Name}");
        }

        await foreach (var page in ListChildrenAsync(loc, folder, ct).ConfigureAwait(false))
        {
            for (var i = 0; i < page.Count; i += BatchSize)
            {
                var n = Math.Min(BatchSize, page.Count - i);
                var batch = new FsEntry[n];
                for (var k = 0; k < n; k++)
                {
                    var (childLoc, f) = page[i + k];
                    batch[k] = ToEntry(childLoc, f);
                }

                yield return batch;
            }
        }
    }

    /// <summary>자식을 쪽 단위로 읽으며 캐시를 채운다. 보이는 이름은 같은 폴더 안에서 겹치지 않게 만든다.</summary>
    internal async IAsyncEnumerable<IReadOnlyList<(GDriveLocation Location, GDriveFile File)>> ListChildrenAsync(
        GDriveLocation folderLoc, GDriveFile folder, [EnumeratorCancellation] CancellationToken ct)
    {
        var api = Api(folderLoc.Account);
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase); // Windows에 내려받을 때도 겹치지 않게 대소문자 무시
        string? token = null;
        var first = true;
        do
        {
            var page = await api.ListChildrenAsync(folder.EffectiveId, token, ct).ConfigureAwait(false);
            if (first)
            {
                Forget(folderLoc, includeSelf: false); // 지워졌거나 이름이 바뀐 자식의 옛 캐시를 버린다
                first = false;
            }

            var list = new List<(GDriveLocation, GDriveFile)>(page.Files.Count);
            foreach (var f in page.Files)
            {
                var name = Unique(DisplayName(f), used);
                var child = folderLoc.Combine(name);
                _nodes[child.Key] = f;
                list.Add((child, f));
            }

            yield return list;
            token = page.NextPageToken;
        }
        while (token is not null);
    }

    // ───────────── 경로 → 파일 ─────────────

    /// <summary>경로를 Drive 파일로. 없으면 <see cref="FileNotFoundException"/>.</summary>
    public async Task<GDriveFile> ResolveAsync(GDriveLocation loc, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(loc);
        if (_nodes.TryGetValue(loc.Key, out var cached))
        {
            return cached;
        }

        var api = Api(loc.Account);
        if (loc.IsRoot)
        {
            var root = await api.GetAsync("root", ct).ConfigureAwait(false);
            _nodes[loc.Key] = root;
            return root;
        }

        var parentLoc = loc.Parent!;
        var parent = await ResolveAsync(parentLoc, ct).ConfigureAwait(false);
        if (!parent.IsFolder)
        {
            throw new DirectoryNotFoundException($"폴더가 아닙니다: {parentLoc.Name}");
        }

        // 1) 이름으로 바로 찾기 (큰 폴더를 다 읽지 않으려고)
        foreach (var candidate in DriveNamesFor(loc.Name))
        {
            var found = await api.FindChildrenByNameAsync(parent.EffectiveId, candidate, ct).ConfigureAwait(false);
            if (found.Count == 1 && DisplayName(found[0]) == loc.Name)
            {
                _nodes[loc.Key] = found[0];
                return found[0];
            }

            if (found.Count > 1)
            {
                break; // 같은 이름이 여러 개: 목록 순서로 매긴 (2)·(3)을 따라야 하므로 부모를 읽는다
            }
        }

        // 2) 부모 폴더를 읽어 캐시를 채운 뒤 다시 본다 ("이름 (2)" 같은 구분 이름)
        await foreach (var _ in ListChildrenAsync(parentLoc, parent, ct).ConfigureAwait(false))
        {
        }

        return _nodes.TryGetValue(loc.Key, out var f2)
            ? f2
            : throw new FileNotFoundException($"Google Drive에서 찾을 수 없습니다: {loc}");
    }

    public Task<GDriveFile> ResolveAsync(string path, CancellationToken ct) => ResolveAsync(GDriveLocation.Parse(path), ct);

    /// <summary>캐시에 있으면 바로 돌려준다(네트워크 없음). 열기 판단처럼 UI 스레드에서 빠르게 볼 때.</summary>
    public GDriveFile? TryGetCached(string path) =>
        GDriveLocation.TryParse(path, out var l) && _nodes.TryGetValue(l.Key, out var f) ? f : null;

    /// <summary>경로와 그 아래 캐시를 버린다(이름 바꾸기·삭제·이동 뒤).</summary>
    public void Forget(GDriveLocation loc, bool includeSelf = true)
    {
        ArgumentNullException.ThrowIfNull(loc);
        var key = loc.Key;
        var prefix = loc.IsRoot ? key : key + "/";
        foreach (var k in _nodes.Keys)
        {
            if ((includeSelf && k == key) || (k.StartsWith(prefix, StringComparison.Ordinal) && k != key))
            {
                _nodes.TryRemove(k, out _);
            }
        }
    }

    /// <summary>계정 연결을 끊을 때 그 계정 캐시를 모두 버린다.</summary>
    public void ForgetAccount(string account) => Forget(GDriveLocation.Root(account));

    // ───────────── 쓰기 ─────────────

    /// <summary>새 폴더. 같은 이름이 있으면 IOException. 만든 폴더의 경로를 돌려준다.</summary>
    public async Task<string> CreateFolderAsync(string parentPath, string name, CancellationToken ct)
    {
        var parentLoc = GDriveLocation.Parse(parentPath);
        var parent = await ResolveAsync(parentLoc, ct).ConfigureAwait(false);
        var api = Api(parentLoc.Account);
        var driveName = UnescapeName(name);
        if ((await api.FindChildrenByNameAsync(parent.EffectiveId, driveName, ct).ConfigureAwait(false)).Count > 0)
        {
            throw new IOException($"같은 이름이 이미 있습니다: {name}");
        }

        var created = await api.CreateFolderAsync(parent.EffectiveId, driveName, ct).ConfigureAwait(false);
        var loc = parentLoc.Combine(DisplayName(created));
        _nodes[loc.Key] = created;
        return loc.ToString();
    }

    /// <summary>이름 바꾸기. Google 문서는 .gdoc 같은 붙임 확장자를 떼고 바꾼다. 새 경로를 돌려준다.</summary>
    public async Task<string> RenameAsync(string path, string newName, CancellationToken ct)
    {
        var loc = GDriveLocation.Parse(path);
        if (loc.IsRoot)
        {
            throw new IOException("내 드라이브의 이름은 바꿀 수 없습니다");
        }

        var file = await ResolveAsync(loc, ct).ConfigureAwait(false);
        var parent = await ResolveAsync(loc.Parent!, ct).ConfigureAwait(false);
        var api = Api(loc.Account);
        var driveName = UnescapeName(file.IsGoogleApp ? StripAppSuffix(newName, file) : newName);
        var clash = await api.FindChildrenByNameAsync(parent.EffectiveId, driveName, ct).ConfigureAwait(false);
        if (clash.Any(c => c.Id != file.Id))
        {
            throw new IOException($"같은 이름이 이미 있습니다: {newName}");
        }

        var updated = await api.UpdateAsync(file.Id, new GDriveUpdate(Name: driveName), ct).ConfigureAwait(false);
        Forget(loc);
        var newLoc = loc.Parent!.Combine(DisplayName(updated));
        _nodes[newLoc.Key] = updated;
        return newLoc.ToString();
    }

    /// <summary>휴지통으로 (Drive 휴지통, 30일 뒤 자동 삭제). permanent면 바로 삭제.</summary>
    public async Task DeleteAsync(string path, bool permanent, CancellationToken ct)
    {
        var loc = GDriveLocation.Parse(path);
        if (loc.IsRoot)
        {
            throw new IOException("내 드라이브는 지울 수 없습니다");
        }

        var file = await ResolveAsync(loc, ct).ConfigureAwait(false);
        var api = Api(loc.Account);
        if (permanent)
        {
            await api.DeleteAsync(file.Id, ct).ConfigureAwait(false);
        }
        else
        {
            _ = await api.UpdateAsync(file.Id, new GDriveUpdate(Trashed: true), ct).ConfigureAwait(false);
        }

        Forget(loc);
    }

    /// <summary>
    /// 파일 내용을 스트림에 쓴다. Google 문서는 Office 형식으로 내보낸다.
    /// 내보낼 수 없는 종류(설문지 등)는 <see cref="NotSupportedException"/>.
    /// </summary>
    public async Task DownloadAsync(string path, Stream destination, IProgress<long>? progress, CancellationToken ct)
    {
        var loc = GDriveLocation.Parse(path);
        var file = await ResolveAsync(loc, ct).ConfigureAwait(false);
        await DownloadAsync(loc.Account, file, destination, progress, ct).ConfigureAwait(false);
    }

    internal async Task DownloadAsync(string account, GDriveFile file, Stream destination, IProgress<long>? progress, CancellationToken ct)
    {
        var api = Api(account);
        if (file.IsFolder)
        {
            throw new IOException($"폴더는 내려받을 수 없습니다: {file.Name}");
        }

        if (file.IsGoogleApp)
        {
            var export = ExportFormat(file.EffectiveMimeType) ?? throw new NotSupportedException($"이 Google 파일은 내려받을 수 없습니다 (브라우저에서 여세요): {file.Name}");
            await api.ExportAsync(file.EffectiveId, export.MimeType, destination, progress, ct).ConfigureAwait(false);
        }
        else
        {
            await api.DownloadAsync(file.EffectiveId, destination, progress, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 열기·미리보기용: 로컬 캐시에 내려받아 그 경로를 돌려준다. 같은 수정 시각·크기면 다시 받지 않는다.
    /// 캐시 파일은 읽기 전용으로 둔다(고쳐도 Drive에 올라가지 않으므로 실수로 고치지 않게).
    /// </summary>
    public async Task<string> FetchToCacheAsync(string path, IProgress<long>? progress, CancellationToken ct)
    {
        var loc = GDriveLocation.Parse(path);
        var file = await ResolveAsync(loc, ct).ConfigureAwait(false);
        var dir = Path.Combine(CacheDirectory, SafeFileName(file.EffectiveId));
        var local = Path.Combine(dir, LocalFileName(file));
        if (File.Exists(local))
        {
            var info = new FileInfo(local);
            if (info.LastWriteTimeUtc == file.ModifiedUtc && (file.IsGoogleApp || info.Length == file.Size))
            {
                return local;
            }

            info.IsReadOnly = false;
        }

        Directory.CreateDirectory(dir);
        var part = local + ".part";
        try
        {
            await using (var fs = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
            {
                await DownloadAsync(loc.Account, file, fs, progress, ct).ConfigureAwait(false);
            }

            File.Move(part, local, overwrite: true);
            File.SetLastWriteTimeUtc(local, file.ModifiedUtc);
            new FileInfo(local).IsReadOnly = true;
            return local;
        }
        catch
        {
            TryDelete(part);
            throw;
        }
    }

    // ───────────── 이름 규칙 ─────────────

    /// <summary>Google 앱 종류 → 보이는 확장자.</summary>
    internal static string? AppSuffix(string mimeType) => mimeType switch
    {
        "application/vnd.google-apps.document" => ".gdoc",
        "application/vnd.google-apps.spreadsheet" => ".gsheet",
        "application/vnd.google-apps.presentation" => ".gslides",
        "application/vnd.google-apps.drawing" => ".gdraw",
        "application/vnd.google-apps.form" => ".gform",
        "application/vnd.google-apps.map" => ".gmap",
        "application/vnd.google-apps.site" => ".gsite",
        "application/vnd.google-apps.script" => ".gscript",
        "application/vnd.google-apps.jam" => ".gjam",
        _ when mimeType.StartsWith(GDriveFile.GoogleAppsPrefix, StringComparison.Ordinal) => ".glink",
        _ => null,
    };

    /// <summary>내보내기 형식 (MIME, 확장자). 내보낼 수 없으면 null.</summary>
    public static (string MimeType, string Extension)? ExportFormat(string mimeType) => mimeType switch
    {
        "application/vnd.google-apps.document" => ("application/vnd.openxmlformats-officedocument.wordprocessingml.document", ".docx"),
        "application/vnd.google-apps.spreadsheet" => ("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", ".xlsx"),
        "application/vnd.google-apps.presentation" => ("application/vnd.openxmlformats-officedocument.presentationml.presentation", ".pptx"),
        "application/vnd.google-apps.drawing" => ("image/png", ".png"),
        "application/vnd.google-apps.script" => ("application/vnd.google-apps.script+json", ".json"),
        _ => null,
    };

    /// <summary>목록에 보이는 이름: '/'는 '∕'로, Google 문서는 .gdoc 등을 붙인다.</summary>
    public static string DisplayName(GDriveFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        var name = GDriveLocation.EscapeName(file.Name.Length == 0 ? "(이름 없음)" : file.Name);
        return file.IsGoogleApp && AppSuffix(file.EffectiveMimeType) is { } sfx ? name + sfx : name;
    }

    /// <summary>로컬에 내려받을 때의 이름: Google 문서는 .docx 등, Windows에 못 쓰는 문자는 _로.</summary>
    public static string LocalFileName(GDriveFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        var name = file.Name.Length == 0 ? "(이름 없음)" : file.Name;
        if (file.IsGoogleApp && ExportFormat(file.EffectiveMimeType) is { } ex)
        {
            name += ex.Extension;
        }

        return SafeFileName(name);
    }

    internal static string SafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Select(c => Array.IndexOf(invalid, c) >= 0 || c is '<' or '>' or ':' or '"' or '/' or '\\' or '|' or '?' or '*' || c < ' ' ? '_' : c).ToArray();
        var s = new string(chars).TrimEnd('.', ' ');
        return s.Length == 0 ? "_" : s;
    }

    /// <summary>경로 조각 → Drive 이름('∕' → '/').</summary>
    public static string UnescapeName(string segment) => segment.Replace(GDriveLocation.SlashSubstitute, '/');

    /// <summary>"이름 (2).txt"처럼 겹치지 않는 이름. used에 추가한다.</summary>
    internal static string Unique(string name, HashSet<string> used)
    {
        if (used.Add(name))
        {
            return name;
        }

        var ext = Path.GetExtension(name);
        var stem = ext.Length > 0 && ext.Length < name.Length ? name[..^ext.Length] : name;
        if (ext.Length == name.Length)
        {
            ext = "";
        }

        for (var i = 2; ; i++)
        {
            var candidate = $"{stem} ({i}){ext}";
            if (used.Add(candidate))
            {
                return candidate;
            }
        }
    }

    private static string StripAppSuffix(string name, GDriveFile file) =>
        AppSuffix(file.EffectiveMimeType) is { } sfx && name.EndsWith(sfx, StringComparison.OrdinalIgnoreCase) && name.Length > sfx.Length
            ? name[..^sfx.Length]
            : name;

    /// <summary>보이는 이름 하나에 해당할 수 있는 Drive 이름들(그대로, 또는 .gdoc 같은 붙임 확장자를 뗀 것).</summary>
    private static IEnumerable<string> DriveNamesFor(string segment)
    {
        var raw = UnescapeName(segment);
        yield return raw;
        var ext = Path.GetExtension(raw);
        if (ext.StartsWith(".g", StringComparison.OrdinalIgnoreCase) && ext.Length < raw.Length &&
            ext is ".gdoc" or ".gsheet" or ".gslides" or ".gdraw" or ".gform" or ".gmap" or ".gsite" or ".gscript" or ".gjam" or ".glink")
        {
            yield return raw[..^ext.Length];
        }
    }

    private static FsEntry ToEntry(GDriveLocation loc, GDriveFile f)
    {
        var folder = f.IsFolder;
        var attrs = folder ? FileAttributes.Directory : FileAttributes.Normal;
        if (f.ShortcutTargetId is not null)
        {
            attrs |= FileAttributes.ReparsePoint;
        }

        return new FsEntry(
            loc.Name,
            new FsPath(GDrive, loc.ToString()),
            folder ? FsKind.Folder : FsKind.File,
            folder ? 0 : f.Size,
            null,
            f.ModifiedUtc,
            f.CreatedUtc == DateTime.MinValue ? f.ModifiedUtc : f.CreatedUtc,
            attrs,
            null);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
