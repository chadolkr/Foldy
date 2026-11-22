using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
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
    private const string PolicyNote = "회사 PC라면 파일 암호화가 보안 정책에 맞는지 먼저 확인해 주세요.";
    private const string ForgetWarning = "암호를 잊으면 Foldy도 열 수 없어요. 꼭 기억할 수 있는 암호로 정해 주세요.";

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

    public static async Task NewMemoAsync(Window owner, string folder)
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
        new MemoWindow(path, note, pw) { Owner = owner }.Show();
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

        await w.AskAsync(Foldy.Core.Localization.Loc.T("sec.done"), C("close", Foldy.Core.Localization.Loc.T("cf.close")));
        w.Close();
    }

    public static async Task OpenLockedFileAsync(Window owner, string path, ShellViewModel shell)
    {
        var w = Open(owner, Foldy.Core.Localization.Loc.T("sec.lockedFile"));
        string? pw;
        string inner;
        byte[] bytes;
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
                (inner, bytes) = await Task.Run(() =>
                {
                    var n = FileLocker.InnerName(path, p);
                    return (n, SecureZip.ReadEntry(path, p, n));
                });
                break;
            }
            catch (Exception ex) when (ex is WrongPasswordException or ICSharpCode.SharpZipLib.SharpZipBaseException or IOException)
            {
                w.Say(Foldy.Core.Localization.Loc.T("sec.wrongPw"));
            }
        }

        var head = bytes.AsSpan(0, Math.Min(bytes.Length, 4096));
        if (FileLocker.LooksPreviewable(inner, head) && Preview(inner, bytes) is { } view)
        {
            w.Say(Foldy.Core.Localization.Loc.F("sec.memContent", inner));
            w.ShowContent(view);
        }
        else
        {
            w.Say(Foldy.Core.Localization.Loc.T("sec.noPreview"));
        }

        Array.Clear(bytes);
        var c = await w.AskAsync(Foldy.Core.Localization.Loc.T("sec.whatNow"), C("unlock", Foldy.Core.Localization.Loc.T("sec.decryptOpen")), C("close", Foldy.Core.Localization.Loc.T("cf.close")));
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
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                await w.AskAsync(Foldy.Core.Localization.Loc.T("sec.decryptFailed") + ex.Message, C("close", Foldy.Core.Localization.Loc.T("cf.close")));
            }
        }

        pw = null;
        SecureZip.ForgetKeys();
        w.Close();
    }

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
        else if (c == "beside")
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
    private static async Task DisposeOriginalAsync(GuideWindow w, ShellViewModel shell, string path, string? choice)
    {
        if (choice == "recycle")
        {
            if (await shell.RecycleAsync([path]))
            {
                w.Say(Foldy.Core.Localization.Loc.T("sec.origInBin"));
            }
        }
        else if (choice == "delete")
        {
            var sure = await w.AskAsync(Foldy.Core.Localization.Loc.T("sec.origDeleteAsk"), C("yes", Foldy.Core.Localization.Loc.T("sec.delete")), C("no", Foldy.Core.Localization.Loc.T("dlg.cancel")));
            if (sure == "yes" && await shell.DeletePermanentlyAsync([path]))
            {
                w.Say(Foldy.Core.Localization.Loc.T("sec.origDeleted"));
            }
        }
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

        w.Say(Foldy.Core.Localization.Loc.F("sec.vaultList", entries.Count));
        var dest = Path.Combine(Path.GetDirectoryName(vault)!, Path.GetFileNameWithoutExtension(vault));
        while (!w.IsClosed)
        {
            var (choice, picked) = await w.PickAsync(Foldy.Core.Localization.Loc.T("sec.fileList"),
                entries.Select(e => (e.Name, $"{e.Name}  ·  {FileItem.FormatBytes(e.Size)}", false)),
                C("preview", Foldy.Core.Localization.Loc.T("sec.previewPicked")), C("extract", Foldy.Core.Localization.Loc.T("sec.extractPicked")), C("all", Foldy.Core.Localization.Loc.T("sec.extractAll")), C("close", Foldy.Core.Localization.Loc.T("cf.close")));
            if (choice == "preview")
            {
                foreach (var n in picked.Take(5))
                {
                    var p = pw;
                    var bytes = await Task.Run(() => SecureZip.ReadEntry(vault, p, n));
                    var head = bytes.AsSpan(0, Math.Min(bytes.Length, 4096));
                    if (FileLocker.LooksPreviewable(n, head) && Preview(n, bytes) is { } view)
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

                var p = pw;
                var n = await Task.Run(() =>
                {
                    var done = 0;
                    foreach (var name in names)
                    {
                        if (SecureZip.SafeTarget(dest, name) is not { } target)
                        {
                            continue; // 금고 밖으로 나가는 이름은 꺼내지 않는다
                        }

                        var dst = SecureZip.Unique(target);
                        Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                        File.WriteAllBytes(dst, SecureZip.ReadEntry(vault, p, name));
                        done++;
                    }

                    return done;
                });
                var c = await w.AskAsync(Foldy.Core.Localization.Loc.F("sec.extractedN", n, dest), C("open", Foldy.Core.Localization.Loc.T("cf.openFolder")), C("more", Foldy.Core.Localization.Loc.T("sec.keepViewing")), C("close", Foldy.Core.Localization.Loc.T("cf.close")));
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
