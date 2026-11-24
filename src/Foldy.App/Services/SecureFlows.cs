using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using Foldy.Core.Space;
using Foldy.Core.Vault;
using Foldy.Presentation.Files;
using Foldy.Presentation.Shell;

namespace Foldy.App.Services;

/// <summary>
/// 🔒지키기: 금고(.foldyvault)와 잠긴 파일·메모(.foldylock)의 대화 흐름. 암호는 저장하지도 기록하지도 않는다.
/// 내용은 메모리에서만 풀어 보여 주고, 사용자가 [꺼내기]/[풀어서 열기]를 고를 때만 디스크에 쓴다.
/// </summary>
public static class SecureFlows
{
    private static string PolicyNote => Foldy.Core.Localization.Loc.T("sec.policyNote");
    private static string ForgetWarning => Foldy.Core.Localization.Loc.T("sec.forgetWarning");

    private static (string, string) C(string id, string label) => (id, label);

    private static GuideWindow Open(Window owner, string title)
    {
        var w = new GuideWindow(title) { Owner = owner };
        w.Show();
        return w;
    }

    public static bool IsLocked(string path) => path.EndsWith(SecureZip.LockExtension, StringComparison.OrdinalIgnoreCase);

    public static bool IsVault(string path) => path.EndsWith(SecureZip.VaultExtension, StringComparison.OrdinalIgnoreCase);

    /// <summary>더블클릭·Enter·탐색기에서 연 파일이 Foldy가 다룰 잠긴 파일이면 열고 true.</summary>
    /// <summary>Foldy가 자기 창으로 여는 파일인가 (그림 든 메모·금고·잠긴 메모·잠긴 파일). <see cref="TryOpen"/>과 같은 기준.</summary>
    public static bool Handles(string path) =>
        TextPack.Is(path) || IsVault(path) || path.EndsWith(SecureZip.MemoSuffix, StringComparison.OrdinalIgnoreCase) || IsLocked(path);

    public static bool TryOpen(Window owner, string path, ShellViewModel shell)
    {
        if (TextPack.Is(path))
        {
            try
            {
                var w = MemoWindow.OpenMarkdown(path, startInView: true); // 그림이 든 메모: 보기로 열고 [쓰기로]
                w.Owner = owner;
                w.Show();
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                Foldy.UI.Dialogs.FoldyDialog.Info(owner, Foldy.Core.Localization.Loc.T("sec.memo"), Foldy.Core.Localization.Loc.T("sec.memoCantOpen") + ex.Message, Foldy.UI.Dialogs.DialogKind.Warning);
            }

            return true;
        }

        if (IsVault(path))
        {
            _ = OpenVaultAsync(owner, path, shell);
            return true;
        }

        if (path.EndsWith(SecureZip.MemoSuffix, StringComparison.OrdinalIgnoreCase))
        {
            _ = OpenMemoAsync(owner, path);
            return true;
        }

        if (IsLocked(path))
        {
            _ = OpenLockedFileAsync(owner, path, shell);
            return true;
        }

        return false;
    }

    // ───────────── 잠긴 메모 ─────────────

    /// <summary>
    /// 새 잠긴 메모. 사용자 2026-11-22 "어떻게 써야 할지 모르겠다": 무엇을 하는지 한 줄로 말하고, 암호를 정하게 하고,
    /// 만든 뒤에는 파일 목록에서 그 파일을 골라 보여 주며 어디에 저장됐는지·다시 여는 법을 메모 창에 적는다.
    /// </summary>
    public static async Task NewMemoAsync(Window owner, string folder, ShellViewModel shell)
    {
        var w = Open(owner, Foldy.Core.Localization.Loc.T("sec.newLocked"));
        w.Say(Foldy.Core.Localization.Loc.T("sec.newLockedHello"));
        w.Say(Foldy.Core.Localization.Loc.T("sec.pwManagerTip"));
        w.Say(PolicyNote);
        var pw = await w.AskPasswordAsync(ForgetWarning, confirm: true, okLabel: Foldy.Core.Localization.Loc.T("sec.makeMemo"));
        if (pw is null)
        {
            w.Close();
            return;
        }

        var path = SecureZip.Unique(Path.Combine(folder, Foldy.Core.Localization.Loc.T("sec.newMemoName") + SecureZip.MemoSuffix));
        var note = new LockedNote { Markdown = Foldy.Core.Localization.Loc.T("sw.memo.body") };
        try
        {
            note.Save(path, pw);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await w.AskAsync(Foldy.Core.Localization.Loc.T("sec.cantCreate") + ex.Message, C("close", Foldy.Core.Localization.Loc.T("cf.close")));
            w.Close();
            return;
        }

        w.Close();
        var m = new MemoWindow(path, note, pw) { Owner = owner };
        m.Show();
        m.Tell(Foldy.Core.Localization.Loc.F("sec.memoWhere", Path.GetFileName(path), folder));
        await ShowInListAsync(shell, path);
    }

    /// <summary>새로 만든 잠긴 파일·메모·금고를 지금 보고 있는 목록에서 골라 보여 준다 (어디에 생겼는지 눈으로 보이게).</summary>
    private static Task ShowInListAsync(ShellViewModel shell, string path) =>
        shell.RefreshFoldersAsync([Path.GetDirectoryName(path)!], Path.GetFileName(path));

    /// <summary>
    /// 사이드바 [지키기]·Ctrl+Space에서 고른 것 없이 [파일 잠그기]를 누른 경우: 상태 줄에 작게 쓰는 대신 Windows 파일 고르기 창을 띄운다.
    /// </summary>
    public static void PickFileToLock(Window owner, ShellViewModel shell, string? startFolder)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = Foldy.Core.Localization.Loc.T("sw.c.encryptNeed"), InitialDirectory = startFolder ?? "", CheckFileExists = true };
        if (dlg.ShowDialog(owner) == true && !IsLocked(dlg.FileName) && !IsVault(dlg.FileName))
        {
            _ = LockFileAsync(owner, dlg.FileName, shell);
        }
    }

    /// <summary>고른 것 없이 [폴더를 금고에 넣기]: Windows 폴더 고르기 창.</summary>
    public static void PickFolderForVault(Window owner, ShellViewModel shell, string? startFolder)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = Foldy.Core.Localization.Loc.T("sw.c.vaultNeed"), InitialDirectory = startFolder ?? "" };
        if (dlg.ShowDialog(owner) == true && Directory.Exists(dlg.FolderName) && Path.GetPathRoot(dlg.FolderName) != dlg.FolderName)
        {
            _ = PutInVaultAsync(owner, dlg.FolderName, shell);
        }
    }

    public static async Task OpenMemoAsync(Window owner, string path)
    {
        var w = Open(owner, Foldy.Core.Localization.Loc.T("sec.lockedMemo"));
        while (true)
        {
            var pw = await w.AskPasswordAsync(Foldy.Core.Localization.Loc.F("sec.pwFor", Path.GetFileName(path)), confirm: false, okLabel: Foldy.Core.Localization.Loc.T("sec.open"));
            if (pw is null)
            {
                w.Close();
                return;
            }

            try
            {
                var note = await Task.Run(() => LockedNote.Load(path, pw));
                w.Close();
                new MemoWindow(path, note, pw) { Owner = owner }.Show();
                return;
            }
            catch (Exception ex) when (ex is WrongPasswordException or ICSharpCode.SharpZipLib.SharpZipBaseException or IOException)
            {
                w.Say(Foldy.Core.Localization.Loc.T("sec.wrongPw"));
            }
        }
    }

    // ───────────── 파일 잠그기·풀기 ─────────────

    public static async Task LockFileAsync(Window owner, string file, ShellViewModel shell)
    {
        var w = Open(owner, Foldy.Core.Localization.Loc.T("sec.encrypt"));
        var name = Path.GetFileName(file);
        w.Say(Foldy.Core.Localization.Loc.F("sec.encryptAsk", name, name, SecureZip.LockExtension));

        // 암호를 묻기 전에 공간부터 본다: 잠긴 파일은 옆에 새로 생기고 원본은 확인이 끝날 때까지 그대로다 (사용자 원칙 2026-11-22)
        var dir = Path.GetDirectoryName(file)!;
        long length;
        try
        {
            length = new FileInfo(file).Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            length = 0;
        }

        var need = DiskSpace.VaultEstimate(length, 1);
        if (!await SpaceFlows.EnsureAsync(w, shell, () => DiskSpace.Check(dir, need, need)))
        {
            w.Close();
            return;
        }

        SpaceFlows.MentionIfBig(w, dir, need);
        w.Say(PolicyNote);
        var pw = await w.AskPasswordAsync(ForgetWarning, confirm: true, okLabel: Foldy.Core.Localization.Loc.T("sec.encrypt"));
        if (pw is null)
        {
            w.Close();
            return;
        }

        w.Busy();
        w.Say(Foldy.Core.Localization.Loc.T("sec.encrypting"));
        (bool Ok, string LockPath) r;
        try
        {
            r = await Task.Run(() => FileLocker.Lock(file, pw));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await w.AskAsync(Foldy.Core.Localization.Loc.T("sec.encryptFailed") + ex.Message, C("close", Foldy.Core.Localization.Loc.T("cf.close")));
            w.Close();
            return;
        }

        if (!r.Ok)
        {
            await w.AskAsync(Foldy.Core.Localization.Loc.T("sec.verifyFailed"), C("close", Foldy.Core.Localization.Loc.T("cf.close")));
            w.Close();
            return;
        }

        var c = await w.AskAsync(Foldy.Core.Localization.Loc.F("sec.encryptDone", Path.GetFileName(r.LockPath)),
            C("recycle", Foldy.Core.Localization.Loc.T("sec.origToBin")), C("delete", Foldy.Core.Localization.Loc.T("sec.origDelete")), C("keep", Foldy.Core.Localization.Loc.T("sec.origKeep")));
        await DisposeOriginalAsync(w, shell, file, c);
        await ShowInListAsync(shell, r.LockPath);

        await w.AskAsync(Foldy.Core.Localization.Loc.T("sec.done"), C("close", Foldy.Core.Localization.Loc.T("cf.close")));
        w.Close();
    }

    public static async Task OpenLockedFileAsync(Window owner, string path, ShellViewModel shell)
    {
        var w = Open(owner, Foldy.Core.Localization.Loc.T("sec.lockedFile"));
        string? pw;
        string inner;
        byte[] bytes;
        long size;
        while (true)
        {
            pw = await w.AskPasswordAsync(Foldy.Core.Localization.Loc.F("sec.pwFor", Path.GetFileName(path)), confirm: false, okLabel: Foldy.Core.Localization.Loc.T("sec.open"));
            if (pw is null)
            {
                w.Close();
                return;
            }

            try
            {
                var p = pw;
                // 미리보기에 쓸 만큼만 메모리로 (큰 동영상 등은 앞부분만 읽어 암호를 확인하고, 풀 때 파일로 바로 푼다)
                (inner, bytes, size) = await Task.Run(() =>
                {
                    var n = FileLocker.InnerName(path, p);
                    var (h, sz) = SecureZip.ReadEntryHead(path, p, n, PreviewLimit);
                    return (n, h, sz);
                });
                break;
            }
            catch (Exception ex) when (ex is WrongPasswordException or ICSharpCode.SharpZipLib.SharpZipBaseException)
            {
                w.Say(Foldy.Core.Localization.Loc.T("sec.wrongPw"));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                await w.AskAsync(Foldy.Core.Localization.Loc.T("sec.decryptFailed") + ex.Message, C("close", Foldy.Core.Localization.Loc.T("cf.close")));
                w.Close();
                return;
            }
        }

        var head = bytes.AsSpan(0, Math.Min(bytes.Length, 4096));
        if (size <= PreviewLimit && FileLocker.LooksPreviewable(inner, head) && Preview(inner, bytes) is { } view)
        {
            w.Say(Foldy.Core.Localization.Loc.F("sec.memContent", inner));
            w.ShowContent(view);
        }
        else
        {
            w.Say(Foldy.Core.Localization.Loc.T("sec.noPreview"));
        }

        Array.Clear(bytes);
        var c = await w.AskAsync(Foldy.Core.Localization.Loc.T("sec.lockedWhatNow"), C("unlock", Foldy.Core.Localization.Loc.T("sec.decryptOpen")), C("close", Foldy.Core.Localization.Loc.T("cf.close")));
        if (c == "unlock" && !await SpaceFlows.EnsureAsync(w, shell, () => DiskSpace.CheckFiles(Path.GetDirectoryName(path)!, [size])))
        {
            c = "close"; // 시작 전에 공간이 모자라 그만둠 (암호 걸린 파일은 그대로)
        }

        if (c == "unlock")
        {
            try
            {
                var p = pw;
                var dst = await Task.Run(() => FileLocker.Unlock(path, p));
                try
                {
                    using var _ = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(dst) { UseShellExecute = true });
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
                {
                }

                var k = await w.AskAsync(Foldy.Core.Localization.Loc.F("sec.decrypted", Path.GetFileName(dst)),
                    C("keep", Foldy.Core.Localization.Loc.T("sec.lockedKeep")), C("recycle", Foldy.Core.Localization.Loc.T("sec.lockedToBin")));
                if (k == "recycle")
                {
                    await shell.RecycleAsync([path]);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or WrongPasswordException or InvalidDataException or ICSharpCode.SharpZipLib.SharpZipBaseException)
            {
                await w.AskAsync(Foldy.Core.Localization.Loc.T("sec.decryptFailed") + ex.Message, C("close", Foldy.Core.Localization.Loc.T("cf.close")));
            }
        }

        pw = null;
        SecureZip.ForgetKeys();
        w.Close();
    }

    /// <summary>미리보기로 메모리에 읽는 최대 크기. 더 큰 파일은 미리 보지 않고 풀어서 연다.</summary>
    private const int PreviewLimit = 32 << 20;

    private static FrameworkElement? Preview(string name, byte[] bytes)
    {
        var ext = Path.GetExtension(name).ToLowerInvariant();
        try
        {
            if (ext is ".png" or ".jpg" or ".jpeg" or ".gif")
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.StreamSource = new MemoryStream(bytes);
                bmp.EndInit();
                bmp.Freeze();
                return new Image { Source = bmp, MaxHeight = 320, Stretch = System.Windows.Media.Stretch.Uniform, StretchDirection = StretchDirection.DownOnly };
            }

            if (ext is ".txt" or ".md" or ".csv" or ".log" or ".json")
            {
                var text = System.Text.Encoding.UTF8.GetString(bytes);
                return new ScrollViewer
                {
                    MaxHeight = 320,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    Content = new TextBox { Text = text.Length > 20000 ? text[..20000] + "\n…" : text, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, BorderThickness = new Thickness(0), Background = System.Windows.Media.Brushes.Transparent },
                };
            }
        }
        catch (Exception ex) when (ex is NotSupportedException or IOException or ArgumentException)
        {
        }

        return null;
    }

    // ───────────── 이전 버전 (덮어쓰기 복구) ─────────────

    public static async Task VersionsAsync(Window owner, string file, ShellViewModel shell)
    {
        var w = Open(owner, Foldy.Core.Localization.Loc.T("sec.versions"));
        var name = Path.GetFileName(file);
        var list = Foldy.Core.History.VersionHistory.VersionsOf(file);
        if (list.Count == 0)
        {
            await w.AskAsync(Foldy.Core.Localization.Loc.F("sec.noVersions", name), C("close", Foldy.Core.Localization.Loc.T("cf.close")));
            w.Close();
            return;
        }

        w.Say(Foldy.Core.Localization.Loc.F("sec.versionsFound", name, list.Count));
        var choices = list.Take(6).Select((v, i) => C(i.ToString(System.Globalization.CultureInfo.InvariantCulture), $"{v.SavedUtc.ToLocalTime():MM-dd HH:mm} · {FileItem.FormatBytes(v.Size)}")).ToList();
        choices.Add(C("close", Foldy.Core.Localization.Loc.T("dlg.cancel")));
        var pick = await w.AskAsync(Foldy.Core.Localization.Loc.T("sec.whichTime"), [.. choices]);
        if (!int.TryParse(pick, out var idx))
        {
            w.Close();
            return;
        }

        var v = list[idx];
        var c = await w.AskAsync(Foldy.Core.Localization.Loc.T("sec.restoreHow"),
            C("restore", Foldy.Core.Localization.Loc.T("cf.undo")), C("beside", Foldy.Core.Localization.Loc.T("sec.extractBeside")), C("close", Foldy.Core.Localization.Loc.T("dlg.cancel")));
        if (c == "restore")
        {
            var ok = await Task.Run(() => Foldy.Core.History.VersionHistory.Restore(v));
            await w.AskAsync(ok ? Foldy.Core.Localization.Loc.F("sec.restored", name, v.SavedUtc.ToLocalTime()) : Foldy.Core.Localization.Loc.T("sec.restoreFailed"), C("close", Foldy.Core.Localization.Loc.T("cf.close")));
            await shell.RefreshFoldersAsync([Path.GetDirectoryName(file)!]);
        }
        else if (c == "beside" && await SpaceFlows.EnsureAsync(w, shell, () => DiskSpace.CheckFiles(Path.GetDirectoryName(file)!, [v.Size])))
        {
            var dst = await Task.Run(() => Foldy.Core.History.VersionHistory.ExtractBeside(v));
            await w.AskAsync(dst is null ? Foldy.Core.Localization.Loc.T("sec.extractFailed") : Foldy.Core.Localization.Loc.F("sec.extractedBeside", Path.GetFileName(dst)), C("close", Foldy.Core.Localization.Loc.T("cf.close")));
            await shell.RefreshFoldersAsync([Path.GetDirectoryName(file)!]);
        }

        w.Close();
    }

    /// <summary>
    /// 잠그거나 금고에 넣은 뒤 암호 없는 원본 처리. 휴지통은 비우기 전까지 원본이 그대로 남는다는 것을 알려 준다.
    /// "바로 지우기"는 휴지통을 거치지 않는다 (디스크 복구 도구까지 막는 완전 삭제는 아니다).
    /// </summary>
    private static async Task DisposeOriginalAsync(GuideWindow w, ShellViewModel shell, string path, string? choice) =>
        await DisposeOriginalAsync(w, shell, [path], choice);

    private static async Task DisposeOriginalAsync(GuideWindow w, ShellViewModel shell, IReadOnlyList<string> paths, string? choice)
    {
        if (choice == "recycle")
        {
            if (await shell.RecycleAsync(paths))
            {
                w.Say(Foldy.Core.Localization.Loc.T("sec.origInBin"));
            }
        }
        else if (choice == "delete")
        {
            var sure = await w.AskAsync(Foldy.Core.Localization.Loc.T("sec.origDeleteAsk"), C("yes", Foldy.Core.Localization.Loc.T("sec.delete")), C("no", Foldy.Core.Localization.Loc.T("dlg.cancel")));
            if (sure == "yes" && await shell.DeletePermanentlyAsync(paths))
            {
                w.Say(Foldy.Core.Localization.Loc.T("sec.origDeleted"));
            }
        }
    }

    /// <summary>
    /// 열어 둔 금고에 파일을 더 넣는다 (사용자 결정 2026-11-22: 이번에 같이). 고르기 → 같은 이름이면 어떻게 할지 → 공간 확인 →
    /// 넣고 확인 → 원래 파일 처리. 다 되면 새 목록, 그만두거나 실패하면 null (금고는 그대로).
    /// </summary>
    private static async Task<IReadOnlyList<(string Name, long Size)>?> AddToVaultAsync(GuideWindow w, ShellViewModel shell, string vault, string pw, IReadOnlyList<(string Name, long Size)> entries)
    {
        var dir = Path.GetDirectoryName(vault)!;
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = Foldy.Core.Localization.Loc.T("sec.addPick"), Multiselect = true, CheckFileExists = true, InitialDirectory = dir };
        if (dlg.ShowDialog(w) != true)
        {
            return null;
        }

        // 금고 자신이나 다른 잠긴 파일은 넣지 않는다
        var files = dlg.FileNames.Where(f => !IsVault(f) && !IsLocked(f)).ToList();
        if (files.Count == 0)
        {
            w.Say(Foldy.Core.Localization.Loc.T("sec.addNone"));
            return null;
        }

        var have = new HashSet<string>(entries.Select(e => e.Name), StringComparer.OrdinalIgnoreCase);
        var same = files.Select(f => Path.GetFileName(f)).Where(n => n is not null && have.Contains(n)).Select(n => n!).ToList();
        var replace = false;
        if (same.Count > 0)
        {
            var how = await w.AskAsync(Foldy.Core.Localization.Loc.F("sec.addSame", same.Count, string.Join(", ", same.Take(3))),
                C("both", Foldy.Core.Localization.Loc.T("sec.addKeepBoth")), C("replace", Foldy.Core.Localization.Loc.T("sec.addReplace")), C("cancel", Foldy.Core.Localization.Loc.T("dlg.cancel")));
            if (how is not ("both" or "replace"))
            {
                return null;
            }

            replace = how == "replace";
        }

        // 시작 전에 공간부터 (금고를 새로 써서 바꿔 끼우므로 금고 크기 + 넣는 크기만큼 더 든다)
        long add = 0;
        try
        {
            add = files.Sum(f => new FileInfo(f).Length) + new FileInfo(vault).Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        var need = DiskSpace.VaultEstimate(add, entries.Count + files.Count);
        if (!await SpaceFlows.EnsureAsync(w, shell, () => DiskSpace.Check(dir, need, need)))
        {
            return null;
        }

        w.Busy();
        var status = w.Say(Foldy.Core.Localization.Loc.F("sec.adding", files.Count));
        VaultAddResult? r;
        IReadOnlyList<(string Name, long Size)> after;
        try
        {
            r = await Task.Run(() => SecureZip.AddFiles(vault, pw, files, replace));
            after = r is null ? entries : await Task.Run(() => SecureZip.List(vault, pw));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or WrongPasswordException or ICSharpCode.SharpZipLib.SharpZipBaseException)
        {
            status.Text = Foldy.Core.Localization.Loc.T("sec.addFailed") + ex.Message;
            return null;
        }

        if (r is null)
        {
            status.Text = Foldy.Core.Localization.Loc.T("sec.addBad");
            return null;
        }

        status.Text = Foldy.Core.Localization.Loc.F("sec.added", r.Added + r.Replaced, after.Count);
        var c = await w.AskAsync(Foldy.Core.Localization.Loc.T("sec.addOrig"),
            C("recycle", Foldy.Core.Localization.Loc.T("sec.origToBin")), C("delete", Foldy.Core.Localization.Loc.T("sec.origDelete")), C("keep", Foldy.Core.Localization.Loc.T("sec.origKeep")));
        await DisposeOriginalAsync(w, shell, files, c);
        await shell.RefreshFoldersAsync(files.Select(f => Path.GetDirectoryName(f)!).Append(dir).Distinct(StringComparer.OrdinalIgnoreCase));
        return after;
    }

    // ───────────── 금고 ─────────────

    public static async Task PutInVaultAsync(Window owner, string folder, ShellViewModel shell)
    {
        var w = Open(owner, Foldy.Core.Localization.Loc.T("sec.vault"));
        var name = Path.GetFileName(folder.TrimEnd('\\'));
        long size = 0;
        var count = 0;
        try
        {
            // 금고에 실제로 넣을 것과 같은 기준으로 센다 (숨김 파일 포함, 폴더 링크는 따라가지 않음)
            var (files, _) = await Task.Run(() => VaultMaker.Scan(folder.TrimEnd('\\', '/')));
            foreach (var f in files)
            {
                size += new FileInfo(f).Length;
                count++;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        w.Say(Foldy.Core.Localization.Loc.F("sec.vaultAsk", name, count, FileItem.FormatBytes(size), name, SecureZip.VaultExtension));

        // 암호를 묻고 오래 암호화하기 전에 공간부터 본다 (사용자 요청 2026-11-22: 시간 들인 뒤에 넘었다고 하지 말 것).
        // 금고는 폴더 옆에 생기고, 원본은 확인이 끝날 때까지 그대로 두므로 넣는 크기만큼 더 든다. 크기 자체의 제한은 없다.
        var parent = Path.GetDirectoryName(folder.TrimEnd('\\', '/')) ?? folder;
        var need = DiskSpace.VaultEstimate(size, count);
        if (!await SpaceFlows.EnsureAsync(w, shell, () => DiskSpace.Check(parent, need, need)))
        {
            w.Close();
            return;
        }

        SpaceFlows.MentionIfBig(w, parent, need);
        w.Say(PolicyNote);
        var pw = await w.AskPasswordAsync(ForgetWarning, confirm: true, okLabel: Foldy.Core.Localization.Loc.T("sec.vault"));
        if (pw is null)
        {
            w.Close();
            return;
        }

        w.Busy();
        var status = w.Say(Foldy.Core.Localization.Loc.T("sec.vaulting"));
        string vault;
        VaultResult made;
        try
        {
            made = await Task.Run(() => VaultMaker.Create(folder, pw));
            vault = made.VaultPath;
            status.Text = Foldy.Core.Localization.Loc.T("sec.vaultChecking");
            if (!await Task.Run(() => SecureZip.Verify(vault, pw)))
            {
                File.Delete(vault);
                await w.AskAsync(Foldy.Core.Localization.Loc.T("sec.vaultBad"), C("close", Foldy.Core.Localization.Loc.T("cf.close")));
                w.Close();
                return;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await w.AskAsync(Foldy.Core.Localization.Loc.T("sec.vaultFailed") + ex.Message, C("close", Foldy.Core.Localization.Loc.T("cf.close")));
            w.Close();
            return;
        }

        status.Text = Foldy.Core.Localization.Loc.T("sec.verified");
        var inVault = await Task.Run(() => SecureZip.List(vault, pw).Count);
        if (!made.Complete || inVault != made.Files)
        {
            // 빠진 것이 있으면 원본을 지우자고 하지 않는다 (OneDrive라면 클라우드에서도 지워진다)
            var list = string.Join("\n", made.Skipped.Take(5).Select(x => $"· {Path.GetFileName(x.Path)}: {x.Reason}"));
            var more = made.Skipped.Count > 5 ? Foldy.Core.Localization.Loc.F("sec.andMore", made.Skipped.Count - 5) : "";
            await w.AskAsync(Foldy.Core.Localization.Loc.F("sec.vaultPartial", list, more), C("close", Foldy.Core.Localization.Loc.T("cf.close")));
            w.Close();
            return;
        }

        var c = await w.AskAsync(Foldy.Core.Localization.Loc.F("sec.vaultCreated", Path.GetFileName(vault), FileItem.FormatBytes(new FileInfo(vault).Length)),
            C("recycle", Foldy.Core.Localization.Loc.T("sec.origToBin")), C("delete", Foldy.Core.Localization.Loc.T("sec.origDelete")), C("keep", Foldy.Core.Localization.Loc.T("sec.origKeep")));
        await DisposeOriginalAsync(w, shell, folder, c);
        await ShowInListAsync(shell, vault);

        await w.AskAsync(Foldy.Core.Localization.Loc.T("sec.vaultDone"), C("close", Foldy.Core.Localization.Loc.T("cf.close")));
        w.Close();
    }

    public static async Task OpenVaultAsync(Window owner, string vault, ShellViewModel shell)
    {
        var w = Open(owner, Foldy.Core.Localization.Loc.T("sec.vaultOpen"));
        string? pw;
        IReadOnlyList<(string Name, long Size)> entries;
        while (true)
        {
            pw = await w.AskPasswordAsync(Foldy.Core.Localization.Loc.F("sec.vaultPw", Path.GetFileName(vault)), confirm: false, okLabel: Foldy.Core.Localization.Loc.T("sec.open"));
            if (pw is null)
            {
                w.Close();
                return;
            }

            try
            {
                var p = pw;
                entries = await Task.Run(() => SecureZip.List(vault, p));
                break;
            }
            catch (Exception ex) when (ex is WrongPasswordException or ICSharpCode.SharpZipLib.SharpZipBaseException or IOException)
            {
                w.Say(Foldy.Core.Localization.Loc.T("sec.wrongPw"));
            }
        }

        var dest = Path.Combine(Path.GetDirectoryName(vault)!, Path.GetFileNameWithoutExtension(vault));
        // 어디로 꺼내지는지는 꺼내기 전에 말한다 (사용자 원칙: 미리 알리기)
        w.Say(Foldy.Core.Localization.Loc.F("sec.vaultList", entries.Count, Path.GetFileName(dest)));
        while (!w.IsClosed)
        {
            var (choice, picked) = await w.PickAsync(Foldy.Core.Localization.Loc.T("sec.fileList"),
                entries.Select(e => (e.Name, $"{e.Name}  ·  {FileItem.FormatBytes(e.Size)}", false)),
                C("preview", Foldy.Core.Localization.Loc.T("sec.previewPicked")), C("extract", Foldy.Core.Localization.Loc.T("sec.extractPicked")), C("all", Foldy.Core.Localization.Loc.T("sec.extractAll")),
                C("add", Foldy.Core.Localization.Loc.T("sec.addFiles")), C("close", Foldy.Core.Localization.Loc.T("cf.close")));
            if (choice == "add")
            {
                if (await AddToVaultAsync(w, shell, vault, pw, entries) is { } after)
                {
                    entries = after;
                }
            }
            else if (choice == "preview")
            {
                foreach (var n in picked.Take(5))
                {
                    var p = pw;
                    byte[] bytes;
                    long size;
                    try
                    {
                        (bytes, size) = await Task.Run(() => SecureZip.ReadEntryHead(vault, p, n, PreviewLimit));
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or WrongPasswordException or ICSharpCode.SharpZipLib.SharpZipBaseException)
                    {
                        w.Say(Foldy.Core.Localization.Loc.F("sec.cantPreviewN", n));
                        continue;
                    }

                    var head = bytes.AsSpan(0, Math.Min(bytes.Length, 4096));
                    if (size <= PreviewLimit && FileLocker.LooksPreviewable(n, head) && Preview(n, bytes) is { } view)
                    {
                        w.Say(n);
                        w.ShowContent(view);
                    }
                    else
                    {
                        w.Say(Foldy.Core.Localization.Loc.F("sec.cantPreviewN", n));
                    }

                    Array.Clear(bytes);
                }
            }
            else if (choice is "extract" or "all")
            {
                var names = choice == "all" ? entries.Select(e => e.Name).ToList() : [.. picked];
                if (names.Count == 0)
                {
                    w.Say(Foldy.Core.Localization.Loc.T("sec.pickExtract"));
                    continue;
                }

                // 꺼내기 전에 공간부터 본다 (사용자 원칙 2026-11-22)
                var chosen = new HashSet<string>(names, StringComparer.Ordinal);
                var sizes = entries.Where(e => chosen.Contains(e.Name)).Select(e => e.Size).ToList();
                if (!await SpaceFlows.EnsureAsync(w, shell, () => DiskSpace.CheckFiles(dest, sizes)))
                {
                    continue;
                }

                var p = pw;
                int n;
                IReadOnlyList<(string Name, string Reason)> failed;
                try
                {
                    // 항목마다 파일로 바로 푼다 (2GB 넘는 파일도). 하나가 실패해도 나머지는 계속 (금고 밖으로 나가는 이름은 건너뜀)
                    (n, failed) = await Task.Run(() => SecureZip.Extract(vault, p, names, dest));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or WrongPasswordException or ICSharpCode.SharpZipLib.SharpZipBaseException)
                {
                    (n, failed) = (0, [(Path.GetFileName(vault), ex.Message)]);
                }

                var msg = Foldy.Core.Localization.Loc.F("sec.extractedN", n, dest);
                if (failed.Count > 0)
                {
                    msg += "\n\n" + Foldy.Core.Localization.Loc.F("sec.extractFailedN", failed.Count,
                        string.Join("\n", failed.Take(5).Select(f => $"{f.Name} — {f.Reason}")));
                }

                var c = await w.AskAsync(msg, C("open", Foldy.Core.Localization.Loc.T("cf.openFolder")), C("more", Foldy.Core.Localization.Loc.T("sec.keepViewing")), C("close", Foldy.Core.Localization.Loc.T("cf.close")));
                if (c == "open")
                {
                    shell.ActivePane.NewFolderTab(dest);
                }

                if (c != "more")
                {
                    break;
                }
            }
            else
            {
                break;
            }
        }

        pw = null;
        SecureZip.ForgetKeys();
        if (!w.IsClosed)
        {
            w.Close();
        }
    }
}
