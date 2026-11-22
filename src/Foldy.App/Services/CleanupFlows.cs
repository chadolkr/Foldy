using System.Diagnostics;
using System.IO;
using System.Windows;
using Foldy.Core.Desktop;
using Foldy.Core.Downloads;
using Foldy.Core.Logs;
using Foldy.Core.Managed;
using Foldy.Presentation.Files;
using Foldy.Presentation.Shell;

namespace Foldy.App.Services;

/// <summary>정리 도구가 쓰는 위치.</summary>
public static class CleanupPaths
{
    public static string? Downloads()
    {
        var p = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        return Directory.Exists(p) ? p : null;
    }

    public static string Desktop => Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);

    /// <summary>바탕화면 안의 "바탕화면 보관" (눈에 보여야 안심이 된다).</summary>
    public static string DesktopArchive => DesktopTidy.ArchiveFolderIn(Desktop);

    public static string DesktopJournal => Path.Combine(App.DataDir, "desktop-tidy-last.json");

    public static string ManagedJournal => Path.Combine(App.DataDir, "managed-last.json");
}

/// <summary>
/// 정리 도구들의 대화 흐름. 찾은 것 → 왜 중요한지 → 무엇을 할지(어떻게 되는지) → 고르기 → 한 일과 둔 곳 + [되돌리기].
/// 화면은 <see cref="GuideWindow"/> 하나를 같이 쓴다.
/// </summary>
public static class CleanupFlows
{
    private static (string, string) C(string id, string label) => (id, label);

    private static GuideWindow Open(Window owner, string title)
    {
        var w = new GuideWindow(title) { Owner = owner };
        w.Show();
        return w;
    }

    private static void OpenFolder(string folder)
    {
        try
        {
            using var _ = Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
        }
    }

    // ───────────── 로그 정리 ─────────────

    public static async Task LogsAsync(Window owner, string drive, ShellViewModel shell)
    {
        var w = Open(owner, Foldy.Core.Localization.Loc.T("sw.logs.title"));
        var d = drive.TrimEnd('\\');
        w.Say(Foldy.Core.Localization.Loc.F("cf.log.hello", d));
        w.Busy();
        List<LogFile> files;
        try
        {
            files = await LogFinder.FromIndexAsync(drive, CancellationToken.None) ?? [];
            if (files.Count == 0 && !w.IsClosed)
            {
                w.Say(Foldy.Core.Localization.Loc.T("cf.log.crawl"));
                files = await LogFinder.ScanAsync(drive, CancellationToken.None);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            files = [];
        }

        if (w.IsClosed)
        {
            return;
        }

        var groups = LogPlanner.Plan(files, DateTime.UtcNow);
        if (groups.Count == 0)
        {
            await w.AskAsync(Foldy.Core.Localization.Loc.T("cf.log.none"), C("close", Foldy.Core.Localization.Loc.T("cf.close")));
            w.Close();
            return;
        }

        var size = groups.Sum(g => g.Size);
        var saving = groups.Sum(g => g.EstimatedSaving);
        w.Say(Foldy.Core.Localization.Loc.F("cf.log.found", groups.Sum(g => g.Files.Count), FileItem.FormatBytes(size), groups.Select(g => g.Folder).Distinct(StringComparer.OrdinalIgnoreCase).Count()));
        w.Say(Foldy.Core.Localization.Loc.T("cf.log.what"));
        var choice = await w.AskAsync(
            Foldy.Core.Localization.Loc.F("cf.log.how", FileItem.FormatBytes(saving)),
            C("all", Foldy.Core.Localization.Loc.T("cf.log.go")), C("pick", Foldy.Core.Localization.Loc.T("cf.log.pick")), C("later", Foldy.Core.Localization.Loc.T("sw.later")));
        var picked = groups;
        if (choice == "pick")
        {
            var r = await w.PickAsync(Foldy.Core.Localization.Loc.T("cf.log.pickHint"),
                groups.Select(g => ((LogGroup)g, Foldy.Core.Localization.Loc.F("cf.log.row", g.Folder, g.Month, g.Files.Count, FileItem.FormatBytes(g.Size)), true)),
                C("go", Foldy.Core.Localization.Loc.T("cf.log.goPicked")), C("later", Foldy.Core.Localization.Loc.T("sw.later")));
            choice = r.Choice == "go" ? "all" : r.Choice;
            picked = [.. r.Picked];
        }

        if (choice != "all" || picked.Count == 0)
        {
            Later(w);
            return;
        }

        w.Busy();
        var status = w.Say(Foldy.Core.Localization.Loc.T("cf.log.zipping"));
        long freed = 0;
        var skipped = 0;
        var done = 0;
        foreach (var g in picked)
        {
            if (w.IsClosed)
            {
                return;
            }

            status.Text = Foldy.Core.Localization.Loc.F("cf.log.zippingN", ++done, picked.Count, g.Folder);
            try
            {
                var r = await Task.Run(() => LogArchiver.Archive(g, CancellationToken.None));
                skipped += r.Skipped.Count;
                if (r.Archived.Count > 0 && await shell.RecycleAsync(r.Archived))
                {
                    freed += g.Files.Where(f => r.Archived.Contains(f.Path)).Sum(f => f.Size) - r.ZipBytesAdded;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                skipped += g.Files.Count;
            }
        }

        status.Text = Foldy.Core.Localization.Loc.T("cf.done");
        await w.AskAsync(
            Foldy.Core.Localization.Loc.F("cf.log.done", FileItem.FormatBytes(Math.Max(0, freed)))
            + (skipped > 0 ? Foldy.Core.Localization.Loc.F("cf.log.inUse", skipped) : ""),
            C("close", Foldy.Core.Localization.Loc.T("cf.close")));
        w.Close();
    }

    // ───────────── 다운로드 정리 ─────────────

    public static async Task DownloadsAsync(Window owner, string folder, ShellViewModel shell)
    {
        var w = Open(owner, Foldy.Core.Localization.Loc.T("sw.c.downloads"));
        w.Say(Foldy.Core.Localization.Loc.T("cf.dl.hello"));
        w.Busy();
        IReadOnlyList<DownloadItem> items;
        try
        {
            items = await Task.Run(() =>
            {
                var installed = InstalledPrograms.Names();
                return DownloadsPlanner.Plan(folder, p => DownloadsPlanner.MatchInstalled(p, installed), DateTime.UtcNow, findDuplicates: true);
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await w.AskAsync(Foldy.Core.Localization.Loc.T("cf.dl.cantRead") + ex.Message, C("close", Foldy.Core.Localization.Loc.T("cf.close")));
            w.Close();
            return;
        }

        if (items.Count == 0)
        {
            await w.AskAsync(Foldy.Core.Localization.Loc.T("cf.dl.clean"), C("close", Foldy.Core.Localization.Loc.T("cf.close")));
            w.Close();
            return;
        }

        w.Say(Foldy.Core.Localization.Loc.F("cf.dl.found", items.Count, FileItem.FormatBytes(items.Sum(i => i.Size))));
        var archive = DownloadsPlanner.ArchiveFolder(folder, DateTime.Now);
        int recycled = 0, archived = 0;
        foreach (var kind in Enum.GetValues<DownloadKind>())
        {
            var group = items.Where(i => i.Kind == kind).ToList();
            if (group.Count == 0 || w.IsClosed)
            {
                continue;
            }

            var (found, why) = kind switch
            {
                DownloadKind.Installer => (Foldy.Core.Localization.Loc.F("cf.dl.installers", group.Count, FileItem.FormatBytes(group.Sum(i => i.Size))), Foldy.Core.Localization.Loc.T("cf.dl.installersWhy")),
                DownloadKind.Duplicate => (Foldy.Core.Localization.Loc.F("cf.dl.dups", group.Count, Path.GetFileName(group[0].Path)), Foldy.Core.Localization.Loc.T("cf.dl.dupWhy")),
                DownloadKind.Unpacked => (Foldy.Core.Localization.Loc.F("cf.dl.zips", group.Count), Foldy.Core.Localization.Loc.T("cf.dl.zipWhy")),
                _ => (Foldy.Core.Localization.Loc.F("cf.dl.old", DownloadsPlanner.OldDays, group.Count, FileItem.FormatBytes(group.Sum(i => i.Size))), Foldy.Core.Localization.Loc.T("cf.dl.oldWhy")),
            };
            w.Say(found + " " + why);
            var choice = await w.AskAsync(
                Foldy.Core.Localization.Loc.F("cf.dl.oldHint", Path.GetFileName(archive)),
                kind == DownloadKind.Old
                    ? [C("archive", Foldy.Core.Localization.Loc.T("cf.archive")), C("pick", Foldy.Core.Localization.Loc.T("cf.log.pick")), C("skip", Foldy.Core.Localization.Loc.T("cf.keep"))]
                    : [C("recycle", Foldy.Core.Localization.Loc.T("ops.toRecycle")), C("archive", Foldy.Core.Localization.Loc.T("cf.archive")), C("pick", Foldy.Core.Localization.Loc.T("cf.log.pick")), C("skip", Foldy.Core.Localization.Loc.T("cf.keep"))]);
            var chosen = group;
            if (choice == "pick")
            {
                var r = await w.PickAsync(Foldy.Core.Localization.Loc.T("cf.dl.pickHint"),
                    group.Select(i => (i, $"{Path.GetFileName(i.Path)}  ·  {FileItem.FormatBytes(i.Size)}  ·  {i.Reason}", kind != DownloadKind.Old)),
                    C("recycle", Foldy.Core.Localization.Loc.T("ops.toRecycle")), C("archive", Foldy.Core.Localization.Loc.T("cf.archive")), C("skip", Foldy.Core.Localization.Loc.T("cf.keep")));
                choice = r.Choice;
                chosen = [.. r.Picked];
            }

            if (choice == GuideWindow.ClosedChoice)
            {
                return;
            }

            if (chosen.Count == 0 || choice == "skip")
            {
                w.Say(Foldy.Core.Localization.Loc.T("cf.keepOk"));
                continue;
            }

            if (choice == "recycle")
            {
                if (await shell.RecycleAsync(chosen.Select(i => i.Path).ToList()))
                {
                    var n = chosen.Count(i => !File.Exists(i.Path));
                    recycled += n;
                    w.Say(Foldy.Core.Localization.Loc.F("cf.movedToBin", n));
                }
            }
            else if (choice == "archive")
            {
                var n = await Task.Run(() => DownloadsPlanner.MoveToArchive(chosen.Select(i => i.Path), archive));
                archived += n;
                w.Say(Foldy.Core.Localization.Loc.F("cf.movedToArchive", n));
            }
        }

        if (w.IsClosed)
        {
            return;
        }

        if (recycled + archived == 0)
        {
            await w.AskAsync(Foldy.Core.Localization.Loc.T("cf.nothingMoved"), C("close", Foldy.Core.Localization.Loc.T("cf.close")));
            w.Close();
            return;
        }

        var summary = Foldy.Core.Localization.Loc.F("cf.dl.done", recycled, archived)
            + (archived > 0 ? Foldy.Core.Localization.Loc.F("cf.dl.archiveAt", archive) : "")
            + (recycled > 0 ? Foldy.Core.Localization.Loc.T("cf.recycleNote") : "");
        while (true)
        {
            var end = await w.AskAsync(summary, archived > 0 ? [C("open", Foldy.Core.Localization.Loc.T("cf.openArchive")), C("close", Foldy.Core.Localization.Loc.T("cf.close"))] : [C("close", Foldy.Core.Localization.Loc.T("cf.close"))]);
            if (end != "open")
            {
                break;
            }

            OpenFolder(archive);
            summary = Foldy.Core.Localization.Loc.T("cf.archiveOpened");
        }

        w.Close();
    }

    // ───────────── 바탕화면 정리 ─────────────

    public static async Task DesktopAsync(Window owner)
    {
        var w = Open(owner, Foldy.Core.Localization.Loc.T("sw.c.desktop"));
        if (DesktopTidy.LastMoves(CleanupPaths.DesktopJournal).Count is var last and > 0)
        {
            var c = await w.AskAsync(Foldy.Core.Localization.Loc.F("cf.dt.last", last),
                C("new", Foldy.Core.Localization.Loc.T("cf.dt.again")), C("undo", Foldy.Core.Localization.Loc.T("cf.dt.restore")), C("close", Foldy.Core.Localization.Loc.T("cf.close")));
            if (c == "undo")
            {
                var n = await Task.Run(() => DesktopTidy.Undo(CleanupPaths.DesktopJournal));
                await w.AskAsync(Foldy.Core.Localization.Loc.F("cf.dt.restored", n), C("close", Foldy.Core.Localization.Loc.T("cf.close")));
                w.Close();
                return;
            }

            if (c != "new")
            {
                w.Close();
                return;
            }
        }

        w.Say(Foldy.Core.Localization.Loc.T("cf.dt.hello"));
        IReadOnlyList<DesktopMove> plan;
        try
        {
            plan = DesktopTidy.Plan(CleanupPaths.Desktop, CleanupPaths.DesktopArchive, DateTime.UtcNow);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await w.AskAsync(Foldy.Core.Localization.Loc.T("cf.dt.cantRead") + ex.Message, C("close", Foldy.Core.Localization.Loc.T("cf.close")));
            w.Close();
            return;
        }

        if (plan.Count == 0)
        {
            await w.AskAsync(Foldy.Core.Localization.Loc.T("cf.dt.clean"), C("close", Foldy.Core.Localization.Loc.T("cf.close")));
            w.Close();
            return;
        }

        var byType = string.Join(", ", plan.GroupBy(m => m.Group).OrderByDescending(g => g.Count()).Select(g => Foldy.Core.Localization.Loc.F("cf.dt.typeCount", g.Key, g.Count())));
        w.Say(Foldy.Core.Localization.Loc.F("cf.dt.found", plan.Count, byType, DesktopTidy.RecentDays));
        var choice = await w.AskAsync(
            Foldy.Core.Localization.Loc.T("cf.dt.plan"),
            C("go", Foldy.Core.Localization.Loc.T("cf.log.go")), C("detail", Foldy.Core.Localization.Loc.T("cf.details")), C("later", Foldy.Core.Localization.Loc.T("sw.later")));
        if (choice == "detail")
        {
            w.ShowList(plan.Select(m => $"{Path.GetFileName(m.Src)}  →  {Path.GetRelativePath(CleanupPaths.Desktop, Path.GetDirectoryName(m.Dst)!)}"));
            choice = await w.AskAsync(Foldy.Core.Localization.Loc.T("cf.okToMove"), C("go", Foldy.Core.Localization.Loc.T("cf.log.go")), C("later", Foldy.Core.Localization.Loc.T("sw.later")));
        }

        if (choice != "go")
        {
            Later(w);
            return;
        }

        w.Busy();
        var done = await Task.Run(() => DesktopTidy.Execute(plan, CleanupPaths.DesktopJournal));
        var msg = Foldy.Core.Localization.Loc.F("cf.dt.done", done.Count)
            + (done.Count < plan.Count ? Foldy.Core.Localization.Loc.F("cf.dt.openSkipped", plan.Count - done.Count) : "");
        while (!w.IsClosed)
        {
            var end = await w.AskAsync(msg, C("open", Foldy.Core.Localization.Loc.T("cf.openArchive")), C("undo", Foldy.Core.Localization.Loc.T("cf.undo")), C("close", Foldy.Core.Localization.Loc.T("cf.close")));
            if (end == "open")
            {
                OpenFolder(CleanupPaths.DesktopArchive);
                msg = Foldy.Core.Localization.Loc.T("cf.archiveOpened");
                continue;
            }

            if (end == "undo")
            {
                var n = await Task.Run(() => DesktopTidy.Undo(CleanupPaths.DesktopJournal));
                await w.AskAsync(Foldy.Core.Localization.Loc.F("cf.undone", n), C("close", Foldy.Core.Localization.Loc.T("cf.close")));
            }

            break;
        }

        w.Close();
    }

    // ───────────── 폴더 관리 방식 ─────────────

    /// <summary>"이 폴더 관리 방식 정하기": 방법을 설명하고 고르게 한 뒤, 바로 정리 제안으로 이어 간다.</summary>
    public static async Task SetMethodAsync(Window owner, string folder)
    {
        var w = Open(owner, Foldy.Core.Localization.Loc.T("cf.m.title"));
        var name = Path.GetFileName(folder.TrimEnd('\\'));
        if (ManagedFolders.CannotManage(folder) is { } why)
        {
            await w.AskAsync(why, C("close", Foldy.Core.Localization.Loc.T("cf.close")));
            w.Close();
            return;
        }

        var current = ManagedFolders.Resolve(folder);
        if (current is { } cur && string.Equals(cur.Root.TrimEnd('\\'), folder.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
        {
            var c = await w.AskAsync(Foldy.Core.Localization.Loc.F("cf.m.current", name, cur.Method.Name),
                C("suggest", Foldy.Core.Localization.Loc.T("cf.m.viewNow")), C("change", Foldy.Core.Localization.Loc.T("cf.m.change")), C("off", Foldy.Core.Localization.Loc.T("cf.m.off")), C("close", Foldy.Core.Localization.Loc.T("cf.close")));
            if (c == "suggest")
            {
                w.Close();
                await SuggestAsync(owner, cur.Root, cur.Method);
                return;
            }

            if (c == "off")
            {
                ManagedFolders.ClearMethod(folder);
                ExplorerFolderMark.Clear(folder);
                await w.AskAsync(Foldy.Core.Localization.Loc.T("cf.m.offDone"), C("close", Foldy.Core.Localization.Loc.T("cf.close")));
                w.Close();
                return;
            }

            if (c != "change")
            {
                w.Close();
                return;
            }
        }

        w.Say(Foldy.Core.Localization.Loc.F("cf.m.ask", name));
        foreach (var m in FolderMethod.All)
        {
            w.Say($"• {m.Name}: {m.Explanation}");
        }

        var pick = await w.AskAsync(Foldy.Core.Localization.Loc.T("cf.m.which"), [.. FolderMethod.All.Select(m => C(m.Id, m.Name)), C("close", Foldy.Core.Localization.Loc.T("sw.later"))]);
        if (FolderMethod.Find(pick) is not { } method)
        {
            Later(w);
            return;
        }

        if (ExplorerFolderMark.SyncWarning(folder) is { } sync)
        {
            w.Say("⚠ " + sync);
        }

        var create = method.TemplateId is not null
            ? await w.AskAsync(Foldy.Core.Localization.Loc.F("cf.m.makeAsk", method.Name), C("yes", Foldy.Core.Localization.Loc.T("cf.m.make")), C("no", Foldy.Core.Localization.Loc.T("cf.m.self")))
            : "no";
        try
        {
            ManagedFolders.SetMethod(folder, method);
            if (create == "yes" && method.TemplateId is { } tid)
            {
                CreateMethodFolders(folder, tid);
            }

            ExplorerFolderMark.Set(folder, method);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await w.AskAsync(Foldy.Core.Localization.Loc.T("cf.m.saveFailed") + ex.Message, C("close", Foldy.Core.Localization.Loc.T("cf.close")));
            w.Close();
            return;
        }

        var next = await w.AskAsync(Foldy.Core.Localization.Loc.F("cf.m.set", name, method.Name),
            C("suggest", Foldy.Core.Localization.Loc.T("cf.m.lookNow")), C("close", Foldy.Core.Localization.Loc.T("sw.later")));
        w.Close();
        if (next == "suggest")
        {
            await SuggestAsync(owner, folder, method);
        }
    }

    private static void CreateMethodFolders(string folder, string templateId)
    {
        var t = Foldy.Core.Templates.TemplateLibrary.LoadBuiltIn().FirstOrDefault(x => x.Id == templateId);
        if (t is null)
        {
            return;
        }

        foreach (var n in t.Tree.Where(n => !n.IsFile && !n.Name.Contains("{{", StringComparison.Ordinal)))
        {
            Directory.CreateDirectory(Path.Combine(folder, n.Name));
        }
    }

    /// <summary>관리 방법에 따른 제안을 하나씩 설명하고, 확인을 받아 한 번에 옮긴다. 끝나면 [되돌리기].</summary>
    public static async Task SuggestAsync(Window owner, string root, FolderMethod method)
    {
        var w = Open(owner, Foldy.Core.Localization.Loc.F("cf.m.tidyWith", method.Name));
        var name = Path.GetFileName(root.TrimEnd('\\'));
        IReadOnlyList<ManagedSuggestion> list;
        try
        {
            list = await Task.Run(() => ManagedFolders.Suggest(root, method, DateTime.UtcNow));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await w.AskAsync(Foldy.Core.Localization.Loc.T("cf.m.cantRead") + ex.Message, C("close", Foldy.Core.Localization.Loc.T("cf.close")));
            w.Close();
            return;
        }

        if (list.Count == 0)
        {
            await w.AskAsync(Foldy.Core.Localization.Loc.F("cf.m.allGood", name, method.Name), C("close", Foldy.Core.Localization.Loc.T("cf.close")));
            w.Close();
            return;
        }

        w.Say(Foldy.Core.Localization.Loc.F("cf.m.found", name, method.Name, list.Count));
        if (ExplorerFolderMark.SyncWarning(root) is { } sync)
        {
            w.Say("⚠ " + sync);
        }

        var accepted = new List<ManagedMove>();
        foreach (var s in list)
        {
            if (w.IsClosed)
            {
                return;
            }

            if (s.Moves.Count == 0)
            {
                await w.AskAsync(s.Text, C("ok", Foldy.Core.Localization.Loc.T("cf.gotIt")));
                continue;
            }

            var c = await w.AskAsync(s.Text, C("yes", Foldy.Core.Localization.Loc.T("cf.log.go")), C("detail", Foldy.Core.Localization.Loc.T("cf.details")), C("skip", Foldy.Core.Localization.Loc.T("sw.later")));
            if (c == "detail")
            {
                w.ShowList(s.Moves.Select(m => $"{Path.GetRelativePath(root, m.Src)}  →  {Path.GetRelativePath(root, m.Dst)}"));
                c = await w.AskAsync(Foldy.Core.Localization.Loc.T("cf.okToMove"), C("yes", Foldy.Core.Localization.Loc.T("cf.log.go")), C("skip", Foldy.Core.Localization.Loc.T("sw.later")));
            }

            if (c == "yes")
            {
                accepted.AddRange(s.Moves);
            }
            else if (c == GuideWindow.ClosedChoice)
            {
                return;
            }
        }

        if (accepted.Count == 0)
        {
            Later(w);
            return;
        }

        w.Busy();
        var (done, skipped) = await Task.Run(() => ManagedFolders.Apply(accepted, CleanupPaths.ManagedJournal));
        var msg = Foldy.Core.Localization.Loc.F("cf.m.done", done.Count)
            + (skipped.Count > 0 ? Foldy.Core.Localization.Loc.F("cf.m.skipped", skipped.Count, string.Join(", ", skipped.Select(x => x.Reason).Distinct())) : "")
            + Foldy.Core.Localization.Loc.T("cf.undoNote");
        var end = await w.AskAsync(msg, C("open", Foldy.Core.Localization.Loc.T("cf.openFolder")), C("undo", Foldy.Core.Localization.Loc.T("cf.undo")), C("close", Foldy.Core.Localization.Loc.T("cf.close")));
        if (end == "open")
        {
            OpenFolder(root);
        }
        else if (end == "undo")
        {
            var n = await Task.Run(() => ManagedFolders.Undo(CleanupPaths.ManagedJournal));
            await w.AskAsync(Foldy.Core.Localization.Loc.F("cf.undone", n), C("close", Foldy.Core.Localization.Loc.T("cf.close")));
        }

        w.Close();
    }

    private static async void Later(GuideWindow w)
    {
        if (w.IsClosed)
        {
            return;
        }

        await w.AskAsync(Foldy.Core.Localization.Loc.T("cf.nothingChanged"), C("close", Foldy.Core.Localization.Loc.T("cf.close")));
        w.Close();
    }
}

/// <summary>처음 실행 때 30초 둘러보기: 두 창과 네 묶음(찾기·정리하기·지키기·연결하기)을 한 단계씩 소개한다.</summary>
public static class FirstRunTour
{
    private static string Stamp => Path.Combine(App.DataDir, "tour-shown.txt");

    public static bool Shown => File.Exists(Stamp);

    private static string CurrentVersion => typeof(FirstRunTour).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    /// <summary>
    /// 이 버전에서 짧은 소개를 이미 봤나. 사용자 요청(2026-11-21): 다시 설치하거나 업데이트한 뒤 처음 켤 때도 보인다.
    /// 기록은 "버전|시각". 버전이 다르면(업데이트) 다시, 같은 버전이라도 Foldy.exe가 기록보다 나중에 설치됐으면(다시 설치) 다시.
    /// "always"는 시험용(늘 본 것으로).
    /// </summary>
    public static bool ShownForThisVersion => ShownFor(ReadStamp(), CurrentVersion, ExeInstalledUtc());

    public static bool ShownFor(string? stamp, string version, DateTime? exeInstalledUtc)
    {
        if (stamp is null)
        {
            return false;
        }

        if (stamp.Trim() == "always")
        {
            return true;
        }

        var parts = stamp.Trim().Split('|');
        if (parts.Length < 2 || parts[0] != version)
        {
            return false; // 예전 기록(날짜만)이거나 다른 버전 → 업데이트 뒤 처음
        }

        return !(exeInstalledUtc is { } t
            && DateTime.TryParse(parts[1], System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var seen)
            && t > seen.ToUniversalTime().AddMinutes(1)); // 본 뒤에 다시 설치됨
    }

    private static string? ReadStamp()
    {
        try
        {
            return File.Exists(Stamp) ? File.ReadAllText(Stamp) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static DateTime? ExeInstalledUtc()
    {
        try
        {
            return Environment.ProcessPath is { } exe ? File.GetCreationTimeUtc(exe) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static void MarkShown()
    {
        try
        {
            File.WriteAllText(Stamp, $"{CurrentVersion}|{DateTime.UtcNow:O}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// 처음 실행: 먼저 보기 편하게(글자 크기·밝기)를 한 장씩 고르고 — 고르는 즉시 화면에 반영돼 바로 보인다 — 그다음 둘러보기.
    /// 업데이트로 다시 켤 때는 설정이 이미 있으므로 묻지 않는다.
    /// </summary>
    public static async Task FirstRunAsync(Window owner, Action<double> setScale, Action<string> setTheme, Func<GuideWindow, Task>? hotkeyStep = null)
    {
        var w = new GuideWindow(Foldy.Core.Localization.Loc.T("cf.fr.title")) { Owner = owner, Width = 720, Height = 640, FontSize = 18 }; // 처음 안내는 글씨를 크게
        w.Show();
        w.EchoChoices = false;
        w.Say(Foldy.Core.Localization.Loc.T("cf.fr.hello"));
        // 글자 크기는 실제 파일 목록 모양으로 보여 주고 고르게 한다
        foreach (var (label, scale) in new[] { (Foldy.Core.Localization.Loc.T("cf.fr.normal"), 1.0), (Foldy.Core.Localization.Loc.T("cf.fr.large"), 1.15), (Foldy.Core.Localization.Loc.T("cf.fr.larger"), 1.3), (Foldy.Core.Localization.Loc.T("cf.fr.largest"), 1.5) })
        {
            w.ShowContent(SampleList(label, scale));
        }
        while (true)
        {
            var c = await w.AskAsync(Foldy.Core.Localization.Loc.T("cf.fr.size"),
                ("1.0", Foldy.Core.Localization.Loc.T("cf.fr.normal")), ("1.15", Foldy.Core.Localization.Loc.T("cf.fr.large")), ("1.3", Foldy.Core.Localization.Loc.T("cf.fr.larger")), ("1.5", Foldy.Core.Localization.Loc.T("cf.fr.largest")), ("next", Foldy.Core.Localization.Loc.T("cf.fr.ok")));
            if (c == GuideWindow.ClosedChoice)
            {
                return;
            }

            if (c == "next")
            {
                break;
            }

            setScale(double.Parse(c, System.Globalization.CultureInfo.InvariantCulture));
            w.Say(Foldy.Core.Localization.Loc.F("cf.fr.changedSize", (c switch { "1.0" => Foldy.Core.Localization.Loc.T("cf.fr.normal"), "1.15" => Foldy.Core.Localization.Loc.T("cf.fr.large"), "1.3" => Foldy.Core.Localization.Loc.T("cf.fr.larger"), _ => Foldy.Core.Localization.Loc.T("cf.fr.largest") })));
        }

        while (true)
        {
            var c = await w.AskAsync(Foldy.Core.Localization.Loc.T("cf.fr.theme"), ("light", Foldy.Core.Localization.Loc.T("cf.fr.light")), ("dark", Foldy.Core.Localization.Loc.T("cf.fr.dark")), ("system", Foldy.Core.Localization.Loc.T("cf.fr.system")), ("next", Foldy.Core.Localization.Loc.T("cf.fr.ok")));
            if (c == GuideWindow.ClosedChoice)
            {
                return;
            }

            if (c == "next")
            {
                break;
            }

            setTheme(c);
            w.Say(Foldy.Core.Localization.Loc.T("cf.fr.changedTheme"));
        }

        // 빠른 검색 단축키(Ctrl+Space)가 다른 프로그램과 겹치는지 여기서 확인하고, 겹치면 이 자리에서 고른다
        if (hotkeyStep is not null)
        {
            await hotkeyStep(w);
            if (w.IsClosed)
            {
                return;
            }
        }

        w.Close();
        await RunAsync(owner);
    }

    /// <summary>파일 목록 세 줄짜리 견본 (그 크기에서 실제로 어떻게 보이는지).</summary>
    internal static System.Windows.Controls.Border SampleList(string label, double scale)
    {
        var panel = new System.Windows.Controls.StackPanel();
        panel.Children.Add(new System.Windows.Controls.TextBlock { Text = label, FontWeight = FontWeights.SemiBold, FontSize = 14, Margin = new Thickness(0, 0, 0, 4) });
        foreach (var (glyph, name, size) in new[] { ("\uE8B7", Foldy.Core.Localization.Loc.T("cf.demo.photos"), ""), ("\uE8A5", Foldy.Core.Localization.Loc.T("cf.demo.plan"), "8.2 MB"), ("\uE8A5", Foldy.Core.Localization.Loc.T("cf.demo.minutes"), "1.2 MB") })
        {
            var row = new System.Windows.Controls.DockPanel { LastChildFill = true, Width = 300, Margin = new Thickness(0, 1, 0, 1) };
            var sz = new System.Windows.Controls.TextBlock { Text = size, FontSize = 12.5, Opacity = 0.7 };
            System.Windows.Controls.DockPanel.SetDock(sz, System.Windows.Controls.Dock.Right);
            row.Children.Add(sz);
            var icon = new System.Windows.Controls.TextBlock { Text = glyph, FontSize = 13, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
            icon.SetResourceReference(System.Windows.Controls.TextBlock.FontFamilyProperty, "Font.Icon");
            row.Children.Add(icon);
            row.Children.Add(new System.Windows.Controls.TextBlock { Text = name, FontSize = 12.5 });
            panel.Children.Add(row);
        }

        var box = new System.Windows.Controls.Border { Child = panel, LayoutTransform = new System.Windows.Media.ScaleTransform(scale, scale) };
        box.SetResourceReference(System.Windows.Controls.TextBlock.FontFamilyProperty, "Font.UI");
        return box;
    }

    public static async Task RunAsync(Window owner)
    {
        MarkShown();

        // 쇼츠처럼 보는 짧은 소개 (글 대신 실제 화면이 움직이며 한 줄씩, 사용자 요청 2026-11-21)
        var w = new ShortsWindow();
        if (owner.IsLoaded)
        {
            w.Owner = owner;
        }

        var done = new TaskCompletionSource();
        w.Closed += (_, _) => done.TrySetResult();
        w.Show();
        await done.Task;
    }
}
