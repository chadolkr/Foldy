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
                Foldy.UI.Dialogs.FoldyDialog.Info(owner, "메모", "메모를 열지 못했어요: " + ex.Message, Foldy.UI.Dialogs.DialogKind.Warning);
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
        var w = Open(owner, "새 잠긴 메모");
        w.Say("비밀번호나 꼭 기억해야 할 내용을 적어 두는 잠긴 메모를 만들게요. 메모는 암호를 걸어 한 파일(이름.memo.foldylock)로 저장되고, 열 때마다 암호를 물어봐요. 그림도 붙여 넣을 수 있어요.");
        w.Say("💡 중요한 비밀번호가 아주 많다면 Bitwarden이나 KeePass 같은 비밀번호 관리 프로그램도 좋아요.");
        w.Say(PolicyNote);
        var pw = await w.AskPasswordAsync(ForgetWarning, confirm: true, okLabel: "메모 만들기");
        if (pw is null)
        {
            w.Close();
            return;
        }

        var path = SecureZip.Unique(Path.Combine(folder, "새 메모" + SecureZip.MemoSuffix));
        var note = new LockedNote { Markdown = "# 새 메모\n\n" };
        try
        {
            note.Save(path, pw);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await w.AskAsync("만들지 못했어요: " + ex.Message, C("close", "닫기"));
            w.Close();
            return;
        }

        w.Close();
        new MemoWindow(path, note, pw) { Owner = owner }.Show();
    }

    public static async Task OpenMemoAsync(Window owner, string path)
    {
        var w = Open(owner, "잠긴 메모");
        while (true)
        {
            var pw = await w.AskPasswordAsync($"\"{Path.GetFileName(path)}\"을 열려면 암호를 입력해 주세요.", confirm: false, okLabel: "열기");
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
                w.Say("암호가 맞지 않아요. 다시 입력해 주세요.");
            }
        }
    }

    // ───────────── 파일 잠그기·풀기 ─────────────

    public static async Task LockFileAsync(Window owner, string file, ShellViewModel shell)
    {
        var w = Open(owner, "암호 걸기");
        var name = Path.GetFileName(file);
        w.Say($"\"{name}\"에 암호를 걸게요. 내용은 그대로 암호화되어 \"{name}{SecureZip.LockExtension}\" 파일이 돼요. 열 때는 Foldy에서 암호를 넣으면 돼요.");
        w.Say(PolicyNote);
        var pw = await w.AskPasswordAsync(ForgetWarning, confirm: true, okLabel: "암호 걸기");
        if (pw is null)
        {
            w.Close();
            return;
        }

        w.Busy();
        w.Say("암호를 걸고, 제대로 풀리는지 확인하고 있어요…");
        (bool Ok, string LockPath) r;
        try
        {
            r = await Task.Run(() => FileLocker.Lock(file, pw));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await w.AskAsync("암호를 걸지 못했어요: " + ex.Message, C("close", "닫기"));
            w.Close();
            return;
        }

        if (!r.Ok)
        {
            await w.AskAsync("확인해 보니 내용이 원본과 달라서 잠긴 파일을 지웠어요. 원본은 그대로예요.", C("close", "닫기"));
            w.Close();
            return;
        }

        var c = await w.AskAsync($"다 됐어요! \"{Path.GetFileName(r.LockPath)}\"를 만들고 풀어 보며 확인했어요. 암호가 없는 원본은 어떻게 할까요?",
            C("recycle", "원본은 휴지통으로"), C("delete", "원본 바로 지우기"), C("keep", "원본도 그대로 두기"));
        await DisposeOriginalAsync(w, shell, file, c);

        await w.AskAsync("끝났어요. 잠긴 파일은 자물쇠 아이콘으로 보여요.", C("close", "닫기"));
        w.Close();
    }

    public static async Task OpenLockedFileAsync(Window owner, string path, ShellViewModel shell)
    {
        var w = Open(owner, "잠긴 파일");
        string? pw;
        string inner;
        byte[] bytes;
        while (true)
        {
            pw = await w.AskPasswordAsync($"\"{Path.GetFileName(path)}\"을 열려면 암호를 입력해 주세요.", confirm: false, okLabel: "열기");
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
                w.Say("암호가 맞지 않아요. 다시 입력해 주세요.");
            }
        }

        var head = bytes.AsSpan(0, Math.Min(bytes.Length, 4096));
        if (FileLocker.LooksPreviewable(inner, head) && Preview(inner, bytes) is { } view)
        {
            w.Say($"\"{inner}\" 내용이에요 (메모리에서만 보여 드려요).");
            w.ShowContent(view);
        }
        else
        {
            w.Say("내용을 미리 볼 수 없어요. 원래 프로그램으로 열어 주세요.");
        }

        Array.Clear(bytes);
        var c = await w.AskAsync("어떻게 할까요?", C("unlock", "풀어서 열기"), C("close", "닫기"));
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

                var k = await w.AskAsync($"\"{Path.GetFileName(dst)}\"로 풀어 두고 열었어요. 이제 암호가 없는 파일이에요. 잠긴 파일은 휴지통으로 보낼까요, 그대로 둘까요?",
                    C("keep", "잠긴 파일도 그대로 두기"), C("recycle", "잠긴 파일은 휴지통으로"));
                if (k == "recycle")
                {
                    await shell.RecycleAsync([path]);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                await w.AskAsync("풀지 못했어요: " + ex.Message, C("close", "닫기"));
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
        var w = Open(owner, "이전 버전");
        var name = Path.GetFileName(file);
        var list = Foldy.Core.History.VersionHistory.VersionsOf(file);
        if (list.Count == 0)
        {
            await w.AskAsync($"\"{name}\"의 이전 버전이 없어요. Foldy로 복사·이동하다 덮어쓴 파일만 7일 동안 보관해요.", C("close", "닫기"));
            w.Close();
            return;
        }

        w.Say($"\"{name}\"을 덮어쓰기 전 모습이 {list.Count:N0}개 남아 있어요. 7일이 지나면 저절로 지워져요.");
        var choices = list.Take(6).Select((v, i) => C(i.ToString(System.Globalization.CultureInfo.InvariantCulture), $"{v.SavedUtc.ToLocalTime():MM-dd HH:mm} · {FileItem.FormatBytes(v.Size)}")).ToList();
        choices.Add(C("close", "그만두기"));
        var pick = await w.AskAsync("어느 때로 돌아갈까요?", [.. choices]);
        if (!int.TryParse(pick, out var idx))
        {
            w.Close();
            return;
        }

        var v = list[idx];
        var c = await w.AskAsync("어떻게 할까요? [되돌리기]는 지금 파일도 이전 버전으로 보관한 뒤 바꿔서, 마음이 바뀌면 다시 되돌릴 수 있어요.",
            C("restore", "되돌리기"), C("beside", "옆에 꺼내기"), C("close", "그만두기"));
        if (c == "restore")
        {
            var ok = await Task.Run(() => Foldy.Core.History.VersionHistory.Restore(v));
            await w.AskAsync(ok ? $"\"{name}\"을 {v.SavedUtc.ToLocalTime():MM-dd HH:mm} 모습으로 되돌렸어요." : "되돌리지 못했어요. 파일이 열려 있는지 확인해 주세요.", C("close", "닫기"));
            await shell.RefreshFoldersAsync([Path.GetDirectoryName(file)!]);
        }
        else if (c == "beside")
        {
            var dst = await Task.Run(() => Foldy.Core.History.VersionHistory.ExtractBeside(v));
            await w.AskAsync(dst is null ? "꺼내지 못했어요." : $"\"{Path.GetFileName(dst)}\"로 옆에 꺼냈어요. 지금 파일은 그대로예요.", C("close", "닫기"));
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
                w.Say("원본을 휴지통으로 보냈어요. 휴지통을 비우기 전까지는 암호 없이 볼 수 있으니, 다 확인했으면 휴지통을 비워 주세요.");
            }
        }
        else if (choice == "delete")
        {
            var sure = await w.AskAsync("원본을 휴지통을 거치지 않고 지울게요. 되살릴 수 없어요. 괜찮을까요?", C("yes", "지우기"), C("no", "그만두기"));
            if (sure == "yes" && await shell.DeletePermanentlyAsync([path]))
            {
                w.Say("원본을 지웠어요.");
            }
        }
    }

    // ───────────── 금고 ─────────────

    public static async Task PutInVaultAsync(Window owner, string folder, ShellViewModel shell)
    {
        var w = Open(owner, "금고에 넣기");
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

        w.Say($"\"{name}\" 폴더(파일 {count:N0}개, {FileItem.FormatBytes(size)})를 금고처럼 암호를 걸어 \"{name}{SecureZip.VaultExtension}\" 한 파일에 넣을게요. 열 때는 Foldy에서 암호를 넣으면 안을 볼 수 있어요.");
        w.Say(PolicyNote);
        var pw = await w.AskPasswordAsync(ForgetWarning, confirm: true, okLabel: "금고에 넣기");
        if (pw is null)
        {
            w.Close();
            return;
        }

        w.Busy();
        var status = w.Say("넣고 있어요… 파일이 많으면 조금 걸려요.");
        string vault;
        VaultResult made;
        try
        {
            made = await Task.Run(() => VaultMaker.Create(folder, pw));
            vault = made.VaultPath;
            status.Text = "다 넣었어요. 제대로 열리는지 하나하나 확인하고 있어요…";
            if (!await Task.Run(() => SecureZip.Verify(vault, pw)))
            {
                File.Delete(vault);
                await w.AskAsync("확인해 보니 금고가 제대로 열리지 않아 지웠어요. 원본 폴더는 그대로예요.", C("close", "닫기"));
                w.Close();
                return;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await w.AskAsync("금고를 만들지 못했어요: " + ex.Message, C("close", "닫기"));
            w.Close();
            return;
        }

        status.Text = "확인까지 끝났어요.";
        var inVault = await Task.Run(() => SecureZip.List(vault, pw).Count);
        if (!made.Complete || inVault != made.Files)
        {
            // 빠진 것이 있으면 원본을 지우자고 하지 않는다 (OneDrive라면 클라우드에서도 지워진다)
            var list = string.Join("\n", made.Skipped.Take(5).Select(x => $"· {Path.GetFileName(x.Path)}: {x.Reason}"));
            var more = made.Skipped.Count > 5 ? $"\n…외 {made.Skipped.Count - 5:N0}개" : "";
            await w.AskAsync($"금고는 만들었지만 원본 폴더의 일부를 넣지 못했어요. 그래서 원본 폴더는 그대로 둘게요.\n{list}{more}", C("close", "닫기"));
            w.Close();
            return;
        }

        var c = await w.AskAsync($"\"{Path.GetFileName(vault)}\"({FileItem.FormatBytes(new FileInfo(vault).Length)})를 만들었어요. 암호가 없는 원본 폴더는 어떻게 할까요?",
            C("recycle", "원본은 휴지통으로"), C("delete", "원본 바로 지우기"), C("keep", "원본도 그대로 두기"));
        await DisposeOriginalAsync(w, shell, folder, c);

        await w.AskAsync("끝났어요. 금고는 금고 아이콘으로 보이고, 더블클릭하면 열려요.", C("close", "닫기"));
        w.Close();
    }

    public static async Task OpenVaultAsync(Window owner, string vault, ShellViewModel shell)
    {
        var w = Open(owner, "금고 열기");
        string? pw;
        IReadOnlyList<(string Name, long Size)> entries;
        while (true)
        {
            pw = await w.AskPasswordAsync($"\"{Path.GetFileName(vault)}\" 금고를 열려면 암호를 입력해 주세요.", confirm: false, okLabel: "열기");
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
                w.Say("암호가 맞지 않아요. 다시 입력해 주세요.");
            }
        }

        w.Say($"금고 안에 파일이 {entries.Count:N0}개 있어요. 골라서 미리 보거나 꺼낼 수 있어요. 미리 보기는 메모리에서만 해요.");
        var dest = Path.Combine(Path.GetDirectoryName(vault)!, Path.GetFileNameWithoutExtension(vault));
        while (!w.IsClosed)
        {
            var (choice, picked) = await w.PickAsync("파일 목록",
                entries.Select(e => (e.Name, $"{e.Name}  ·  {FileItem.FormatBytes(e.Size)}", false)),
                C("preview", "고른 것 미리 보기"), C("extract", "고른 것 꺼내기"), C("all", "모두 꺼내기"), C("close", "닫기"));
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
                        w.Say($"\"{n}\"은 미리 볼 수 없어요. 꺼내서 원래 프로그램으로 열어 주세요.");
                    }

                    Array.Clear(bytes);
                }
            }
            else if (choice is "extract" or "all")
            {
                var names = choice == "all" ? entries.Select(e => e.Name).ToList() : [.. picked];
                if (names.Count == 0)
                {
                    w.Say("꺼낼 파일을 골라 주세요.");
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
                var c = await w.AskAsync($"{n:N0}개를 \"{dest}\" 폴더에 꺼냈어요. 꺼낸 파일은 암호가 없어요.", C("open", "폴더 열기"), C("more", "계속 보기"), C("close", "닫기"));
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
