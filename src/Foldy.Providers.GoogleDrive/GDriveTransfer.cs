namespace Foldy.Providers.GoogleDrive;

/// <summary>진행 보고: 끝낸 양, 전체 양(바이트 + 항목마다 1), 지금 다루는 이름.</summary>
public delegate void GDriveTransferProgress(long done, long total, string? current);

public enum GDriveConflictAction { Skip, Overwrite, Rename }

/// <summary>충돌 하나에 대한 결정. ApplyToAll이면 이번 작업의 남은 충돌에도 같은 처리.</summary>
public readonly record struct GDriveConflictDecision(GDriveConflictAction Action, bool ApplyToAll = false);

/// <summary>대상에 같은 이름의 파일이 이미 있다 (Source는 원본 경로, Dest는 대상 경로).</summary>
public sealed record GDriveConflict(string Source, string Dest);

/// <summary>
/// 로컬 ↔ Google Drive, Drive ↔ Drive 복사·이동 (F5/F6, 붙여넣기, 끌어 놓기 공통). SFTP 전송과 같은 규칙을 따른다.
/// <list type="bullet">
/// <item>먼저 전체를 훑어 양을 잰 뒤(진행률·마스코트 위치), 위에서부터 폴더를 만들고 파일을 옮긴다.</item>
/// <item>같은 이름의 폴더는 합친다. 같은 이름의 파일은 <c>resolve</c>로 묻는다(둘 다 유지·덮어쓰기·건너뛰기). 묻지 못하면 둘 다 유지("이름 (2)").</item>
/// <item>Drive에서 덮어쓰기는 기존 파일을 Drive 휴지통으로 보낸 뒤 새로 올린다(되살릴 수 있게).</item>
/// <item>같은 계정 안의 이동은 부모만 바꾼다(내려받지 않음, 즉시). 같은 계정 안의 복사는 Drive 서버에서 복사한다.</item>
/// <item>이동은 그 항목의 복사가 모두 성공한 뒤 원본을 정리한다: 로컬 원본은 지우고, Drive 원본은 Drive 휴지통으로. 건너뛴 것이 있으면 원본을 남긴다.</item>
/// <item>로컬로 받을 때는 <c>.foldy-part</c>에 받은 뒤 이름을 바꾼다(끊기거나 취소해도 반쪽 파일이 남지 않음).</item>
/// </list>
/// </summary>
public sealed class GDriveTransfer(GoogleDriveProvider drive, Func<GDriveConflict, CancellationToken, Task<GDriveConflictDecision>>? resolve = null)
{
    private sealed record Item(string RelativePath, bool IsFolder, long Size, string? LocalPath, GDriveFile? File);

    private sealed record Root(string Source, bool IsDrive, GDriveLocation? DriveLoc, List<Item> Items);

    /// <summary>작업 하나의 상태: 진행량, "남은 충돌에도 똑같이" 결정.</summary>
    private sealed class Run(long total, GDriveTransferProgress? progress)
    {
        public long Done;
        public GDriveConflictDecision? All;
        public bool Skipped;

        public void Add(long n, string? cur)
        {
            Done += n;
            progress?.Invoke(Done, total, cur);
        }

        public void Finish() => progress?.Invoke(total, total, null);
    }

    /// <summary>
    /// sources를 destinationFolder 안으로 복사(move면 이동). 경로는 로컬(C:\...) 또는 gdrive://. 성공하면 null, 실패하면 오류 문장.
    /// 취소하면 <see cref="OperationCanceledException"/>.
    /// </summary>
    public async Task<string?> TransferAsync(IReadOnlyList<string> sources, string destinationFolder, bool move, GDriveTransferProgress? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(sources);
        var destLoc = GDriveLocation.IsGDrive(destinationFolder) ? GDriveLocation.Parse(destinationFolder) : null;
        GDriveFile? destFolder = null;
        if (destLoc is not null)
        {
            destFolder = await drive.ResolveAsync(destLoc, ct).ConfigureAwait(false);
            if (!destFolder.IsFolder)
            {
                return $"폴더가 아닙니다: {destLoc.Name}";
            }
        }
        else if (!Directory.Exists(destinationFolder))
        {
            return $"대상 폴더가 없습니다: {destinationFolder}";
        }

        // 같은 계정 안의 이동은 훑을 필요 없이 부모만 바꾼다.
        var fastMoves = new List<string>();
        var roots = new List<Root>();
        foreach (var s in sources)
        {
            ct.ThrowIfCancellationRequested();
            if (GDriveLocation.IsGDrive(s))
            {
                var loc = GDriveLocation.Parse(s);
                if (destLoc is not null && IsSameOrInside(destLoc, loc))
                {
                    return $"폴더를 자기 안으로 {(move ? "옮길" : "복사할")} 수 없습니다: {loc.Name}";
                }

                if (move && destLoc is not null && loc.Account == destLoc.Account)
                {
                    fastMoves.Add(s);
                    continue;
                }

                var file = await drive.ResolveAsync(loc, ct).ConfigureAwait(false);
                roots.Add(new Root(s, true, loc, await ScanDriveAsync(loc, file, ct).ConfigureAwait(false)));
            }
            else
            {
                if (destLoc is null)
                {
                    return "로컬끼리 복사는 이 경로를 쓰지 않습니다";
                }

                roots.Add(new Root(s, false, null, ScanLocal(s)));
            }
        }

        var run = new Run(roots.Sum(r => r.Items.Sum(i => i.Size + 1)) + fastMoves.Count, progress);
        run.Add(0, null);

        // 대상 폴더마다 이미 있는 항목 (폴더 합치기·충돌 판단). 새로 만든 폴더는 비어 있다.
        var existing = new Dictionary<string, Dictionary<string, GDriveFile>>(StringComparer.Ordinal);

        foreach (var s in fastMoves)
        {
            ct.ThrowIfCancellationRequested();
            await FastMoveAsync(s, destLoc!, destFolder!, existing, run, ct).ConfigureAwait(false);
        }

        foreach (var root in roots)
        {
            ct.ThrowIfCancellationRequested();
            run.Skipped = false;
            if (destLoc is not null)
            {
                await CopyToDriveAsync(root, destLoc, destFolder!, existing, run, ct).ConfigureAwait(false);
            }
            else
            {
                await CopyToLocalAsync(root, destinationFolder, run, ct).ConfigureAwait(false);
            }

            if (move && !run.Skipped)
            {
                await RemoveSourceAsync(root, ct).ConfigureAwait(false);
            }
        }

        if (destLoc is not null)
        {
            drive.Forget(destLoc, includeSelf: false);
        }

        run.Finish();
        return null;
    }

    private async Task FastMoveAsync(string source, GDriveLocation destLoc, GDriveFile destFolder,
        Dictionary<string, Dictionary<string, GDriveFile>> existing, Run run, CancellationToken ct)
    {
        var loc = GDriveLocation.Parse(source);
        var api = drive.Api(loc.Account);
        var file = await drive.ResolveAsync(loc, ct).ConfigureAwait(false);
        var parent = await drive.ResolveAsync(loc.Parent!, ct).ConfigureAwait(false);
        if (parent.EffectiveId == destFolder.EffectiveId)
        {
            run.Add(1, loc.Name);
            return; // 같은 폴더로 이동: 할 일 없음
        }

        var map = await ChildrenAsync(api, destFolder.EffectiveId, existing, ct).ConfigureAwait(false);
        var name = file.Name;
        if (map.TryGetValue(name, out var clash))
        {
            if (file.IsFolder || clash.IsFolder)
            {
                name = Unique(name, map.Keys); // 폴더 이동은 합치지 않고 둘 다 유지
            }
            else
            {
                var d = await DecideAsync(source, destLoc.Combine(GDriveLocation.EscapeName(name)).ToString(), run, ct).ConfigureAwait(false);
                switch (d)
                {
                    case GDriveConflictAction.Skip:
                        run.Add(1, loc.Name);
                        return;
                    case GDriveConflictAction.Overwrite:
                        _ = await api.UpdateAsync(clash.Id, new GDriveUpdate(Trashed: true), ct).ConfigureAwait(false);
                        map.Remove(name);
                        break;
                    default:
                        name = Unique(name, map.Keys);
                        break;
                }
            }
        }

        var moved = await api.UpdateAsync(file.Id, new GDriveUpdate(
            Name: name == file.Name ? null : name, AddParent: destFolder.EffectiveId, RemoveParent: parent.EffectiveId), ct).ConfigureAwait(false);
        map[name] = moved;
        drive.Forget(loc);
        run.Add(1, loc.Name);
    }

    private async Task CopyToDriveAsync(Root root, GDriveLocation destLoc, GDriveFile destFolder,
        Dictionary<string, Dictionary<string, GDriveFile>> existing, Run run, CancellationToken ct)
    {
        var api = drive.Api(destLoc.Account);
        // 상대 경로 → (Drive 폴더 id, 그 폴더의 gdrive 경로)
        var folders = new Dictionary<string, (string Id, GDriveLocation Loc)>(StringComparer.Ordinal) { [""] = (destFolder.EffectiveId, destLoc) };
        var skippedFolders = new List<string>();
        foreach (var item in root.Items)
        {
            ct.ThrowIfCancellationRequested();
            var parentRel = ParentOf(item.RelativePath);
            if (skippedFolders.Any(f => parentRel == f || parentRel.StartsWith(f + "/", StringComparison.Ordinal)) || !folders.TryGetValue(parentRel, out var parent))
            {
                continue;
            }

            var name = item.File is { } fi ? fi.Name : LeafOf(item.RelativePath);
            var map = await ChildrenAsync(api, parent.Id, existing, ct).ConfigureAwait(false);
            run.Add(0, name);

            if (item.IsFolder)
            {
                // 같은 이름 폴더는 합친다. 단 자기 자신(같은 폴더에 복사)이면 "이름 (2)"로 새로 만든다.
                if (map.TryGetValue(name, out var same) && same.IsFolder && same.ShortcutTargetId is null && same.Id != item.File?.Id)
                {
                    folders[item.RelativePath] = (same.Id, parent.Loc.Combine(GDriveLocation.EscapeName(name))); // 합치기
                }
                else
                {
                    if (map.ContainsKey(name))
                    {
                        name = Unique(name, map.Keys);
                    }

                    var created = await api.CreateFolderAsync(parent.Id, name, ct).ConfigureAwait(false);
                    map[name] = created;
                    existing[created.Id] = new Dictionary<string, GDriveFile>(StringComparer.Ordinal);
                    folders[item.RelativePath] = (created.Id, parent.Loc.Combine(GDriveLocation.EscapeName(name)));
                }

                run.Add(1, name);
                continue;
            }

            // 업로드할 이름: 다른 계정으로 가는 Google 문서는 Office 파일이 된다.
            var crossAccount = root.IsDrive && root.DriveLoc!.Account != destLoc.Account;
            if (crossAccount && item.File!.IsGoogleApp && GoogleDriveProvider.ExportFormat(item.File.EffectiveMimeType) is { } fmt)
            {
                name += fmt.Extension;
            }

            if (map.TryGetValue(name, out var clash))
            {
                var action = clash.IsFolder || clash.Id == item.File?.Id
                    ? GDriveConflictAction.Rename
                    : await DecideAsync(SourcePath(root, item), parent.Loc.Combine(GDriveLocation.EscapeName(name)).ToString(), run, ct).ConfigureAwait(false);
                if (action == GDriveConflictAction.Skip)
                {
                    run.Skipped = true;
                    run.Add(item.Size + 1, name);
                    continue;
                }

                if (action == GDriveConflictAction.Overwrite)
                {
                    _ = await api.UpdateAsync(clash.Id, new GDriveUpdate(Trashed: true), ct).ConfigureAwait(false);
                    map.Remove(name);
                }
                else
                {
                    name = Unique(name, map.Keys);
                }
            }

            var start = run.Done;
            GDriveFile made;
            if (root.IsDrive && !crossAccount)
            {
                // 같은 계정: 서버에서 복사 (Google 문서도 그대로)
                made = await api.CopyAsync(item.File!.EffectiveId, parent.Id, name, ct).ConfigureAwait(false);
            }
            else if (root.IsDrive)
            {
                // 다른 계정: 임시 파일로 받아서 올린다.
                var tmp = Path.Combine(Path.GetTempPath(), "foldy-gdrive-" + Guid.NewGuid().ToString("N"));
                try
                {
                    await using var fs = new FileStream(tmp, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1 << 16, FileOptions.DeleteOnClose | FileOptions.Asynchronous);
                    long half = 0, up = 0;
                    var label = name;
                    await drive.DownloadAsync(root.DriveLoc!.Account, item.File!, fs, new Progress(n => { var d = n / 2 - half; half += d; run.Add(d, label); }), ct).ConfigureAwait(false);
                    fs.Position = 0;
                    made = await api.UploadAsync(parent.Id, name, fs, fs.Length, new Progress(n => { var d = n / 2 - up; up += d; run.Add(d, label); }), ct).ConfigureAwait(false);
                }
                finally
                {
                    TryDelete(tmp);
                }
            }
            else
            {
                // 로컬 → Drive
                await using var fs = new FileStream(item.LocalPath!, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true);
                long sent = 0;
                var label = name;
                made = await api.UploadAsync(parent.Id, name, fs, fs.Length, new Progress(n => { var d = n - sent; sent += d; run.Add(d, label); }), ct).ConfigureAwait(false);
            }

            map[name] = made;
            run.Add(Math.Max(0, item.Size + 1 - (run.Done - start)), name);
        }
    }

    private async Task CopyToLocalAsync(Root root, string destFolder, Run run, CancellationToken ct)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal) { [""] = destFolder };
        foreach (var item in root.Items)
        {
            ct.ThrowIfCancellationRequested();
            if (!map.TryGetValue(ParentOf(item.RelativePath), out var parentDir))
            {
                continue;
            }

            var file = item.File!;
            var name = item.IsFolder ? GoogleDriveProvider.SafeFileName(file.Name) : GoogleDriveProvider.LocalFileName(file);
            var linkOnly = !item.IsFolder && file.IsGoogleApp && GoogleDriveProvider.ExportFormat(file.EffectiveMimeType) is null;
            if (linkOnly)
            {
                name = Path.ChangeExtension(name, ".url");
            }

            var target = Path.Combine(parentDir, name);
            run.Add(0, name);
            if (item.IsFolder)
            {
                if (File.Exists(target))
                {
                    target = Path.Combine(parentDir, UniqueOnDisk(parentDir, name));
                }

                Directory.CreateDirectory(target); // 같은 이름 폴더는 합친다
                map[item.RelativePath] = target;
                run.Add(1, name);
                continue;
            }

            var overwrite = false;
            if (Directory.Exists(target))
            {
                target = Path.Combine(parentDir, UniqueOnDisk(parentDir, name));
            }
            else if (File.Exists(target))
            {
                var action = await DecideAsync(SourcePath(root, item), target, run, ct).ConfigureAwait(false);
                if (action == GDriveConflictAction.Skip)
                {
                    run.Skipped = true;
                    run.Add(item.Size + 1, name);
                    continue;
                }

                if (action == GDriveConflictAction.Overwrite)
                {
                    overwrite = true;
                }
                else
                {
                    target = Path.Combine(parentDir, UniqueOnDisk(parentDir, name));
                }
            }

            if (linkOnly)
            {
                // 내보낼 수 없는 Google 파일(설문지·사이트 등): 브라우저로 여는 바로가기를 만든다.
                await File.WriteAllTextAsync(target,
                    $"[InternetShortcut]\r\nURL={file.WebViewLink ?? "https://drive.google.com/open?id=" + file.EffectiveId}\r\n", ct).ConfigureAwait(false);
                run.Add(1, name);
                continue;
            }

            var part = target + ".foldy-part";
            long got = 0;
            try
            {
                await using (var fs = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
                {
                    var label = name;
                    await drive.DownloadAsync(root.DriveLoc!.Account, file, fs, new Progress(n => { var d = n - got; got += d; run.Add(d, label); }), ct).ConfigureAwait(false);
                }

                if (overwrite && File.Exists(target))
                {
                    File.SetAttributes(target, FileAttributes.Normal);
                }

                File.Move(part, target, overwrite);
                if (file.ModifiedUtc != DateTime.MinValue)
                {
                    File.SetLastWriteTimeUtc(target, file.ModifiedUtc);
                }
            }
            catch
            {
                TryDelete(part);
                throw;
            }

            run.Add(Math.Max(0, item.Size - got) + 1, name);
        }
    }

    /// <summary>충돌 처리 결정: "남은 충돌에도 똑같이"가 있으면 그것, 묻는 함수가 없으면 둘 다 유지.</summary>
    private async Task<GDriveConflictAction> DecideAsync(string source, string dest, Run run, CancellationToken ct)
    {
        if (run.All is { } all)
        {
            return all.Action;
        }

        if (resolve is null)
        {
            return GDriveConflictAction.Rename;
        }

        var d = await resolve(new GDriveConflict(source, dest), ct).ConfigureAwait(false);
        if (d.ApplyToAll)
        {
            run.All = d;
        }

        return d.Action;
    }

    private static async Task<Dictionary<string, GDriveFile>> ChildrenAsync(IGDriveApi api, string folderId,
        Dictionary<string, Dictionary<string, GDriveFile>> existing, CancellationToken ct)
    {
        if (existing.TryGetValue(folderId, out var map))
        {
            return map;
        }

        map = new Dictionary<string, GDriveFile>(StringComparer.Ordinal);
        string? token = null;
        do
        {
            var page = await api.ListChildrenAsync(folderId, token, ct).ConfigureAwait(false);
            foreach (var f in page.Files)
            {
                map.TryAdd(f.Name, f);
            }

            token = page.NextPageToken;
        }
        while (token is not null);
        existing[folderId] = map;
        return map;
    }

    private static string SourcePath(Root root, Item item) =>
        item.LocalPath ?? (root.Source.TrimEnd('/') + item.RelativePath[item.RelativePath.IndexOf('/') is var i and >= 0 ? i.. : ^0..]);

    private async Task RemoveSourceAsync(Root root, CancellationToken ct)
    {
        if (root.IsDrive)
        {
            await drive.DeleteAsync(root.Source, permanent: false, ct).ConfigureAwait(false);
            return;
        }

        if (Directory.Exists(root.Source))
        {
            Directory.Delete(root.Source, recursive: true);
        }
        else if (File.Exists(root.Source))
        {
            File.Delete(root.Source);
        }
    }

    // ───────────── 훑기 ─────────────

    private async Task<List<Item>> ScanDriveAsync(GDriveLocation loc, GDriveFile file, CancellationToken ct)
    {
        var list = new List<Item>();
        var rootName = loc.IsRoot ? "내 드라이브" : loc.Name;
        list.Add(new Item(rootName, file.IsFolder, file.IsFolder ? 0 : file.Size, null, file.IsFolder && loc.IsRoot ? file with { Name = rootName } : file));
        if (!file.IsFolder)
        {
            return list;
        }

        var stack = new Stack<(GDriveLocation Loc, GDriveFile File, string Rel)>();
        stack.Push((loc, file, rootName));
        while (stack.Count > 0)
        {
            var (l, f, rel) = stack.Pop();
            await foreach (var page in drive.ListChildrenAsync(l, f, ct).ConfigureAwait(false))
            {
                foreach (var (childLoc, child) in page)
                {
                    var childRel = rel + "/" + childLoc.Name;
                    list.Add(new Item(childRel, child.IsFolder, child.IsFolder ? 0 : child.Size, null, child));
                    if (child.IsFolder && child.ShortcutTargetId is null)
                    {
                        stack.Push((childLoc, child, childRel)); // 바로가기 폴더는 따라 들어가지 않는다(순환 방지)
                    }
                }
            }
        }

        // 부모가 자식보다 먼저 오게
        return [.. list.OrderBy(i => i.RelativePath.Count(c => c == '/'))];
    }

    private static List<Item> ScanLocal(string path)
    {
        var name = Path.GetFileName(path.TrimEnd('\\'));
        if (File.Exists(path))
        {
            return [new Item(name, false, new FileInfo(path).Length, path, null)];
        }

        if (!Directory.Exists(path))
        {
            throw new FileNotFoundException($"찾을 수 없습니다: {path}");
        }

        var list = new List<Item> { new(name, true, 0, path, null) };
        var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = false, AttributesToSkip = 0 };
        foreach (var d in Directory.EnumerateDirectories(path, "*", opts))
        {
            list.Add(new Item(name + "/" + Path.GetRelativePath(path, d).Replace('\\', '/'), true, 0, d, null));
        }

        foreach (var f in new DirectoryInfo(path).EnumerateFiles("*", opts))
        {
            list.Add(new Item(name + "/" + Path.GetRelativePath(path, f.FullName).Replace('\\', '/'), false, f.Length, f.FullName, null));
        }

        return [.. list.OrderBy(i => i.RelativePath.Count(c => c == '/'))];
    }

    // ───────────── 도우미 ─────────────

    private static bool IsSameOrInside(GDriveLocation dest, GDriveLocation src) =>
        dest.Account == src.Account && dest.Segments.Count >= src.Segments.Count &&
        dest.Segments.Take(src.Segments.Count).SequenceEqual(src.Segments, StringComparer.Ordinal);

    private static string ParentOf(string rel)
    {
        var i = rel.LastIndexOf('/');
        return i < 0 ? "" : rel[..i];
    }

    private static string LeafOf(string rel)
    {
        var i = rel.LastIndexOf('/');
        return GoogleDriveProvider.UnescapeName(i < 0 ? rel : rel[(i + 1)..]);
    }

    private static string Unique(string name, IEnumerable<string> taken) =>
        GoogleDriveProvider.Unique(name, new HashSet<string>(taken, StringComparer.Ordinal));

    private static string UniqueOnDisk(string dir, string name)
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(Path.Combine(dir, name)) && !Directory.Exists(Path.Combine(dir, name)))
        {
            return name;
        }

        foreach (var e in Directory.EnumerateFileSystemEntries(dir))
        {
            used.Add(Path.GetFileName(e));
        }

        return GoogleDriveProvider.Unique(name, used);
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

    /// <summary>동기 진행률(Progress&lt;T&gt;는 SynchronizationContext로 넘겨 순서가 섞일 수 있어 쓰지 않는다).</summary>
    private sealed class Progress(Action<long> report) : IProgress<long>
    {
        public void Report(long value) => report(value);
    }
}
