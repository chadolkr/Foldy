namespace Foldy.Providers.GoogleDrive;

/// <summary>진행 보고: 끝낸 양, 전체 양(바이트 + 항목마다 1), 지금 다루는 이름.</summary>
public delegate void GDriveTransferProgress(long done, long total, string? current);

/// <summary>
/// 로컬 ↔ Google Drive, Drive ↔ Drive 복사·이동 (F5/F6, 붙여넣기, 끌어 놓기 공통).
/// <list type="bullet">
/// <item>먼저 전체를 훑어 양을 잰 뒤(진행률·마스코트 위치), 위에서부터 폴더를 만들고 파일을 옮긴다.</item>
/// <item>대상에 같은 이름이 있으면 덮어쓰지 않고 "이름 (2)"로 만든다(데이터를 잃지 않는 쪽).</item>
/// <item>같은 계정 안의 이동은 부모만 바꾼다(내려받지 않음, 즉시). 같은 계정 안의 복사는 Drive 서버에서 복사한다.</item>
/// <item>이동의 원본 정리: 로컬 원본은 모두 올린 뒤 지우고, Drive 원본은 Drive 휴지통으로 보낸다.</item>
/// <item>로컬로 받을 때는 <c>.foldy-part</c>에 받은 뒤 이름을 바꾼다(끊겨도 반쪽 파일이 남지 않음).</item>
/// </list>
/// </summary>
public sealed class GDriveTransfer(GoogleDriveProvider drive)
{
    private sealed record Item(string RelativePath, bool IsFolder, long Size, string? LocalPath, GDriveFile? File);

    private sealed record Root(string Source, bool IsDrive, GDriveLocation? DriveLoc, List<Item> Items);

    /// <summary>
    /// sources를 destinationFolder 안으로 복사(move면 이동). 경로는 로컬(C:\...) 또는 gdrive://. 성공하면 null, 실패하면 오류 문장.
    /// 취소하면 <see cref="OperationCanceledException"/>.
    /// </summary>
    public async Task<string?> TransferAsync(IReadOnlyList<string> sources, string destinationFolder, bool move, GDriveTransferProgress? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(sources);
        var destIsDrive = GDriveLocation.IsGDrive(destinationFolder);
        var destLoc = destIsDrive ? GDriveLocation.Parse(destinationFolder) : null;
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

        var total = roots.Sum(r => r.Items.Sum(i => i.Size + 1)) + fastMoves.Count;
        long done = 0;
        void Report(string? cur) => progress?.Invoke(done, total, cur);
        Report(null);

        // 대상 폴더에 이미 있는 이름 (겹치면 "(2)")
        var used = destLoc is not null
            ? await ExistingDriveNamesAsync(destLoc, destFolder!, ct).ConfigureAwait(false)
            : new HashSet<string>(Directory.EnumerateFileSystemEntries(destinationFolder).Select(Path.GetFileName).OfType<string>(), StringComparer.OrdinalIgnoreCase);

        foreach (var s in fastMoves)
        {
            ct.ThrowIfCancellationRequested();
            var loc = GDriveLocation.Parse(s);
            Report(loc.Name);
            var file = await drive.ResolveAsync(loc, ct).ConfigureAwait(false);
            var parent = await drive.ResolveAsync(loc.Parent!, ct).ConfigureAwait(false);
            if (parent.EffectiveId == destFolder!.EffectiveId)
            {
                done++;
                continue; // 같은 폴더로 이동: 할 일 없음
            }

            var name = Unique(file.Name, used);
            await drive.Api(loc.Account).UpdateAsync(file.Id, new GDriveUpdate(
                Name: name == file.Name ? null : name, AddParent: destFolder.EffectiveId, RemoveParent: parent.EffectiveId), ct).ConfigureAwait(false);
            drive.Forget(loc);
            drive.Forget(destLoc!, includeSelf: false);
            done++;
            Report(loc.Name);
        }

        foreach (var root in roots)
        {
            ct.ThrowIfCancellationRequested();
            if (destLoc is not null)
            {
                await CopyToDriveAsync(root, destLoc, destFolder!, used, () => done, d => { done += d; }, Report, ct).ConfigureAwait(false);
            }
            else
            {
                await CopyToLocalAsync(root, destinationFolder, used, d => { done += d; }, Report, ct).ConfigureAwait(false);
            }

            if (move)
            {
                await RemoveSourceAsync(root, ct).ConfigureAwait(false);
            }
        }

        if (destLoc is not null)
        {
            drive.Forget(destLoc, includeSelf: false);
        }

        done = total;
        Report(null);
        return null;
    }

    private async Task CopyToDriveAsync(Root root, GDriveLocation destLoc, GDriveFile destFolder, HashSet<string> used,
        Func<long> done, Action<long> add, Action<string?> report, CancellationToken ct)
    {
        var api = drive.Api(destLoc.Account);
        var folderIds = new Dictionary<string, string>(StringComparer.Ordinal) { [""] = destFolder.EffectiveId };
        foreach (var item in root.Items)
        {
            ct.ThrowIfCancellationRequested();
            var parentRel = ParentOf(item.RelativePath);
            var parentId = folderIds[parentRel];
            var name = LeafOf(item.RelativePath);
            if (parentRel.Length == 0)
            {
                // 맨 위 항목만 대상과 겹칠 수 있다(아래는 새로 만든 폴더 안).
                name = Unique(item.File is { } f0 && root.IsDrive ? f0.Name : name, used);
            }
            else if (item.File is { } fi)
            {
                name = fi.Name;
            }

            report(name);
            if (item.IsFolder)
            {
                var created = await api.CreateFolderAsync(parentId, name, ct).ConfigureAwait(false);
                folderIds[item.RelativePath] = created.Id;
                add(1);
                continue;
            }

            if (root.IsDrive && item.File is { } src && root.DriveLoc!.Account == destLoc.Account)
            {
                // 같은 계정: 서버에서 복사 (Google 문서도 그대로)
                _ = await api.CopyAsync(src.EffectiveId, parentId, name, ct).ConfigureAwait(false);
                add(item.Size + 1);
                continue;
            }

            if (root.IsDrive && item.File is { } other)
            {
                // 다른 계정: 임시 파일로 받아서 올린다. Google 문서는 Office 파일로 바뀐다.
                var tmp = Path.Combine(Path.GetTempPath(), "foldy-gdrive-" + Guid.NewGuid().ToString("N"));
                try
                {
                    await using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1 << 16, FileOptions.DeleteOnClose | FileOptions.Asynchronous))
                    {
                        var start = done();
                        long half = 0;
                        await drive.DownloadAsync(root.DriveLoc!.Account, other, fs, new Progress(n => { var d = n / 2 - half; half += d; add(d); report(name); }), ct).ConfigureAwait(false);
                        fs.Position = 0;
                        var upName = other.IsGoogleApp && GoogleDriveProvider.ExportFormat(other.EffectiveMimeType) is { } fmt ? name + fmt.Extension : name;
                        long up = 0;
                        _ = await api.UploadAsync(parentId, upName, fs, fs.Length, new Progress(n => { var d = n / 2 - up; up += d; add(d); report(name); }), ct).ConfigureAwait(false);
                        add(item.Size + 1 - (done() - start));
                    }
                }
                finally
                {
                    TryDelete(tmp);
                }

                continue;
            }

            // 로컬 → Drive
            await using (var fs = new FileStream(item.LocalPath!, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true))
            {
                long sent = 0;
                _ = await api.UploadAsync(parentId, name, fs, fs.Length, new Progress(n => { var d = n - sent; sent += d; add(d); report(name); }), ct).ConfigureAwait(false);
                add(item.Size + 1 - sent);
            }
        }

    }

    private async Task CopyToLocalAsync(Root root, string destFolder, HashSet<string> used, Action<long> add, Action<string?> report, CancellationToken ct)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal) { [""] = destFolder };
        foreach (var item in root.Items)
        {
            ct.ThrowIfCancellationRequested();
            var parentRel = ParentOf(item.RelativePath);
            var parentDir = map[parentRel];
            var name = item.IsFolder ? GoogleDriveProvider.SafeFileName(item.File!.Name) : GoogleDriveProvider.LocalFileName(item.File!);
            if (parentRel.Length == 0)
            {
                name = Unique(name, used);
            }
            else
            {
                // Drive는 같은 이름을 허용하므로 폴더 안에서도 겹칠 수 있다.
                name = UniqueOnDisk(parentDir, name);
            }

            var target = Path.Combine(parentDir, name);
            report(name);
            if (item.IsFolder)
            {
                Directory.CreateDirectory(target);
                map[item.RelativePath] = target;
                add(1);
                continue;
            }

            if (item.File!.IsGoogleApp && GoogleDriveProvider.ExportFormat(item.File.EffectiveMimeType) is null)
            {
                // 내보낼 수 없는 Google 파일(설문지·사이트 등): 브라우저로 여는 바로가기를 만든다.
                await File.WriteAllTextAsync(Path.ChangeExtension(target, ".url"),
                    $"[InternetShortcut]\r\nURL={item.File.WebViewLink ?? "https://drive.google.com/open?id=" + item.File.EffectiveId}\r\n", ct).ConfigureAwait(false);
                add(1);
                continue;
            }

            var part = target + ".foldy-part";
            long got = 0;
            try
            {
                await using (var fs = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
                {
                    await drive.DownloadAsync(root.DriveLoc!.Account, item.File, fs, new Progress(n => { var d = n - got; got += d; add(d); report(name); }), ct).ConfigureAwait(false);
                }

                File.Move(part, target);
                if (item.File.ModifiedUtc != DateTime.MinValue)
                {
                    File.SetLastWriteTimeUtc(target, item.File.ModifiedUtc);
                }
            }
            catch
            {
                TryDelete(part);
                throw;
            }

            add(Math.Max(0, item.Size - got) + 1);
        }
    }

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

    private async Task<HashSet<string>> ExistingDriveNamesAsync(GDriveLocation loc, GDriveFile folder, CancellationToken ct)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        await foreach (var page in drive.ListChildrenAsync(loc, folder, ct).ConfigureAwait(false))
        {
            foreach (var (_, f) in page)
            {
                set.Add(f.Name);
            }
        }

        return set;
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

    private static string Unique(string name, HashSet<string> used) => GoogleDriveProvider.Unique(name, used);

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
