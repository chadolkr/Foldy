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
    public static string DesktopArchive => Path.Combine(Desktop, DesktopTidy.ArchiveFolderName);

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
        var w = Open(owner, "로그 정리");
        var d = drive.TrimEnd('\\');
        w.Say($"안녕하세요! {d} 드라이브의 로그 파일을 찾아볼게요. 잠깐만요…");
        w.Busy();
        List<LogFile> files;
        try
        {
            files = await LogFinder.FromIndexAsync(drive, CancellationToken.None) ?? [];
            if (files.Count == 0 && !w.IsClosed)
            {
                w.Say("색인 서비스가 없어서 드라이브를 직접 훑고 있어요. 조금 걸려요.");
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
            await w.AskAsync("정리할 오래된 로그가 없어요. 깔끔하네요! 🙂", C("close", "닫기"));
            w.Close();
            return;
        }

        var size = groups.Sum(g => g.Size);
        var saving = groups.Sum(g => g.EstimatedSaving);
        w.Say($"7일 넘게 바뀌지 않은 로그가 {groups.Sum(g => g.Files.Count):N0}개, 모두 {FileItem.FormatBytes(size)} 있어요 (폴더 {groups.Select(g => g.Folder).Distinct(StringComparer.OrdinalIgnoreCase).Count():N0}곳).");
        w.Say("로그는 프로그램이 남기는 기록이에요. 문제가 생겼을 때 말고는 다시 볼 일이 거의 없는데, 모르는 사이에 몇 GB씩 쌓이곤 해요.");
        var choice = await w.AskAsync(
            $"로그가 있던 폴더에 달마다 zip 하나씩(logs-연-월.zip) 꼭꼭 눌러 담으면 약 {FileItem.FormatBytes(saving)} 줄어요. zip을 다시 열어 원본과 똑같은지 확인한 뒤에만 원본을 휴지통으로 보내니까 걱정 마세요. 지금 쓰고 있는 로그는 건드리지 않아요.",
            C("all", "이대로 정리"), C("pick", "자세히 보고 고르기"), C("later", "나중에"));
        var picked = groups;
        if (choice == "pick")
        {
            var r = await w.PickAsync("정리할 묶음을 골라 주세요. (폴더 · 달 · 개수 · 크기)",
                groups.Select(g => ((LogGroup)g, $"{g.Folder}  ·  {g.Month}  ·  {g.Files.Count:N0}개  ·  {FileItem.FormatBytes(g.Size)}", true)),
                C("go", "고른 것 정리"), C("later", "나중에"));
            choice = r.Choice == "go" ? "all" : r.Choice;
            picked = [.. r.Picked];
        }

        if (choice != "all" || picked.Count == 0)
        {
            Later(w);
            return;
        }

        w.Busy();
        var status = w.Say("압축하고 있어요…");
        long freed = 0;
        var skipped = 0;
        var done = 0;
        foreach (var g in picked)
        {
            if (w.IsClosed)
            {
                return;
            }

            status.Text = $"압축하고 있어요… ({++done}/{picked.Count}) {g.Folder}";
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

        status.Text = "다 했어요!";
        await w.AskAsync(
            $"약 {FileItem.FormatBytes(Math.Max(0, freed))}를 줄였어요. 로그는 각 폴더의 logs-연-월.zip 안에 있고, 원본은 휴지통에 있어요. 필요하면 휴지통에서 언제든 되살릴 수 있어요."
            + (skipped > 0 ? $"\n프로그램이 쓰고 있던 로그 {skipped:N0}개는 그대로 뒀어요." : ""),
            C("close", "닫기"));
        w.Close();
    }

    // ───────────── 다운로드 정리 ─────────────

    public static async Task DownloadsAsync(Window owner, string folder, ShellViewModel shell)
    {
        var w = Open(owner, "다운로드 정리");
        w.Say("다운로드 폴더를 살펴볼게요. 저절로 지우는 건 없으니 편하게 보세요.");
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
            await w.AskAsync("다운로드 폴더를 읽지 못했어요: " + ex.Message, C("close", "닫기"));
            w.Close();
            return;
        }

        if (items.Count == 0)
        {
            await w.AskAsync("다운로드 폴더가 깔끔해요. 정리할 게 없어요! 🙂", C("close", "닫기"));
            w.Close();
            return;
        }

        w.Say($"정리할 만한 파일을 {items.Count:N0}개({FileItem.FormatBytes(items.Sum(i => i.Size))}) 찾았어요. 종류별로 하나씩 여쭤볼게요.");
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
                DownloadKind.Installer => ($"이미 설치한 프로그램의 설치 파일이 {group.Count:N0}개({FileItem.FormatBytes(group.Sum(i => i.Size))}) 있어요.", "설치가 끝난 설치 파일은 보통 다시 쓸 일이 없어요. 필요하면 언제든 다시 받을 수 있어요."),
                DownloadKind.Duplicate => ($"같은 파일을 여러 번 받은 게 {group.Count:N0}개 있어요 (예: \"{Path.GetFileName(group[0].Path)}\").", "내용까지 똑같은 파일이에요. 하나는 남겨 두고 나머지만 골랐어요."),
                DownloadKind.Unpacked => ($"이미 푼 압축 파일이 {group.Count:N0}개 있어요.", "옆에 같은 이름의 폴더가 있어서 압축 파일은 없어도 돼요."),
                _ => ($"{DownloadsPlanner.OldDays}일 넘게 열지 않은 파일이 {group.Count:N0}개({FileItem.FormatBytes(group.Sum(i => i.Size))}) 있어요.", "오래 안 쓴 파일이라도 중요한 게 섞여 있을 수 있어요. 버리기보다 보관을 추천해요."),
            };
            w.Say(found + " " + why);
            var choice = await w.AskAsync(
                "휴지통에 버리거나(휴지통에서 언제든 꺼낼 수 있어요) 보관하세요(다운로드\\보관\\" + Path.GetFileName(archive) + " 폴더로 옮겨요).",
                kind == DownloadKind.Old
                    ? [C("archive", "보관하기"), C("pick", "자세히 보고 고르기"), C("skip", "그대로 두기")]
                    : [C("recycle", "휴지통으로"), C("archive", "보관하기"), C("pick", "자세히 보고 고르기"), C("skip", "그대로 두기")]);
            var chosen = group;
            if (choice == "pick")
            {
                var r = await w.PickAsync("어떤 걸 정리할지 골라 주세요.",
                    group.Select(i => (i, $"{Path.GetFileName(i.Path)}  ·  {FileItem.FormatBytes(i.Size)}  ·  {i.Reason}", kind != DownloadKind.Old)),
                    C("recycle", "휴지통으로"), C("archive", "보관하기"), C("skip", "그대로 두기"));
                choice = r.Choice;
                chosen = [.. r.Picked];
            }

            if (choice == GuideWindow.ClosedChoice)
            {
                return;
            }

            if (chosen.Count == 0 || choice == "skip")
            {
                w.Say("네, 그대로 둘게요.");
                continue;
            }

            if (choice == "recycle")
            {
                if (await shell.RecycleAsync(chosen.Select(i => i.Path).ToList()))
                {
                    var n = chosen.Count(i => !File.Exists(i.Path));
                    recycled += n;
                    w.Say($"{n:N0}개를 휴지통으로 옮겼어요.");
                }
            }
            else if (choice == "archive")
            {
                var n = await Task.Run(() => DownloadsPlanner.MoveToArchive(chosen.Select(i => i.Path), archive));
                archived += n;
                w.Say($"{n:N0}개를 보관 폴더로 옮겼어요.");
            }
        }

        if (w.IsClosed)
        {
            return;
        }

        if (recycled + archived == 0)
        {
            await w.AskAsync("이번에는 아무것도 옮기지 않았어요. 생각나면 언제든 다시 불러 주세요!", C("close", "닫기"));
            w.Close();
            return;
        }

        var summary = $"다 했어요! 휴지통으로 {recycled:N0}개, 보관 폴더로 {archived:N0}개를 옮겼어요."
            + (archived > 0 ? $"\n보관한 파일은 {archive}에 있어요." : "")
            + (recycled > 0 ? "\n휴지통으로 간 파일은 휴지통에서 되살릴 수 있어요." : "");
        while (true)
        {
            var end = await w.AskAsync(summary, archived > 0 ? [C("open", "보관 폴더 열기"), C("close", "닫기")] : [C("close", "닫기")]);
            if (end != "open")
            {
                break;
            }

            OpenFolder(archive);
            summary = "보관 폴더를 열었어요.";
        }

        w.Close();
    }

    // ───────────── 바탕화면 정리 ─────────────

    public static async Task DesktopAsync(Window owner)
    {
        var w = Open(owner, "바탕화면 정리");
        if (DesktopTidy.LastMoves(CleanupPaths.DesktopJournal).Count is var last and > 0)
        {
            var c = await w.AskAsync($"지난번에 바탕화면에서 {last:N0}개를 \"바탕화면 보관\" 폴더로 옮겼어요. 원래대로 돌려 드릴까요, 아니면 새로 정리할까요?",
                C("new", "새로 정리"), C("undo", "원래대로 돌리기"), C("close", "닫기"));
            if (c == "undo")
            {
                var n = await Task.Run(() => DesktopTidy.Undo(CleanupPaths.DesktopJournal));
                await w.AskAsync($"{n:N0}개를 바탕화면 제자리로 돌려놨어요.", C("close", "닫기"));
                w.Close();
                return;
            }

            if (c != "new")
            {
                w.Close();
                return;
            }
        }

        w.Say("방 청소해 주듯 바탕화면을 정리해 드릴게요. 버리는 건 하나도 없어요.");
        IReadOnlyList<DesktopMove> plan;
        try
        {
            plan = DesktopTidy.Plan(CleanupPaths.Desktop, CleanupPaths.DesktopArchive, DateTime.UtcNow);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await w.AskAsync("바탕화면을 읽지 못했어요: " + ex.Message, C("close", "닫기"));
            w.Close();
            return;
        }

        if (plan.Count == 0)
        {
            await w.AskAsync("바탕화면이 깔끔해요. 보관할 파일이 없어요! 🙂", C("close", "닫기"));
            w.Close();
            return;
        }

        var byType = string.Join(", ", plan.GroupBy(m => m.Group).OrderByDescending(g => g.Count()).Select(g => $"{g.Key} {g.Count():N0}개"));
        w.Say($"한동안 쓰지 않은 파일이 {plan.Count:N0}개 있어요 ({byType}). 바로 가기와 최근 {DesktopTidy.RecentDays}일 안에 쓴 파일은 그대로 둘게요.");
        var choice = await w.AskAsync(
            "바탕화면에 \"바탕화면 보관\" 폴더를 만들고, 그 안에 달별·종류별(문서, 이미지, 압축 …)로 넣어 둘게요. 폴더가 바탕화면에 보이니까 찾기 쉬워요. 마음에 안 들면 [되돌리기] 한 번이면 원래대로예요.",
            C("go", "이대로 정리"), C("detail", "자세히 보기"), C("later", "나중에"));
        if (choice == "detail")
        {
            w.ShowList(plan.Select(m => $"{Path.GetFileName(m.Src)}  →  {Path.GetRelativePath(CleanupPaths.Desktop, Path.GetDirectoryName(m.Dst)!)}"));
            choice = await w.AskAsync("이렇게 옮길게요. 괜찮을까요?", C("go", "이대로 정리"), C("later", "나중에"));
        }

        if (choice != "go")
        {
            Later(w);
            return;
        }

        w.Busy();
        var done = await Task.Run(() => DesktopTidy.Execute(plan, CleanupPaths.DesktopJournal));
        var msg = $"다 했어요! {done.Count:N0}개를 바탕화면의 \"바탕화면 보관\" 폴더에 넣어 뒀어요."
            + (done.Count < plan.Count ? $"\n열려 있던 {plan.Count - done.Count:N0}개는 그대로 뒀어요." : "");
        while (!w.IsClosed)
        {
            var end = await w.AskAsync(msg, C("open", "보관 폴더 열기"), C("undo", "되돌리기"), C("close", "닫기"));
            if (end == "open")
            {
                OpenFolder(CleanupPaths.DesktopArchive);
                msg = "보관 폴더를 열었어요.";
                continue;
            }

            if (end == "undo")
            {
                var n = await Task.Run(() => DesktopTidy.Undo(CleanupPaths.DesktopJournal));
                await w.AskAsync($"{n:N0}개를 원래 자리로 돌려놨어요.", C("close", "닫기"));
            }

            break;
        }

        w.Close();
    }

    // ───────────── 폴더 관리 방식 ─────────────

    /// <summary>"이 폴더 관리 방식 정하기": 방법을 설명하고 고르게 한 뒤, 바로 정리 제안으로 이어 간다.</summary>
    public static async Task SetMethodAsync(Window owner, string folder)
    {
        var w = Open(owner, "폴더 관리 방식");
        var name = Path.GetFileName(folder.TrimEnd('\\'));
        if (ManagedFolders.CannotManage(folder) is { } why)
        {
            await w.AskAsync(why, C("close", "닫기"));
            w.Close();
            return;
        }

        var current = ManagedFolders.Resolve(folder);
        if (current is { } cur && string.Equals(cur.Root.TrimEnd('\\'), folder.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
        {
            var c = await w.AskAsync($"\"{name}\" 폴더는 지금 {cur.Method.Name} 방법으로 관리하고 있어요. 어떻게 할까요?",
                C("suggest", "지금 정리할 것 보기"), C("change", "방법 바꾸기"), C("off", "관리 끄기"), C("close", "닫기"));
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
                await w.AskAsync("관리를 껐어요. 폴더와 파일은 그대로예요.", C("close", "닫기"));
                w.Close();
                return;
            }

            if (c != "change")
            {
                w.Close();
                return;
            }
        }

        w.Say($"\"{name}\" 폴더를 어떤 방법으로 관리할지 정해 볼까요? 정해 두면 Foldy가 가끔 살펴보고, 정리할 게 생기면 어떻게 옮길지 먼저 보여 드려요. 확인을 누를 때만 옮겨요.");
        foreach (var m in FolderMethod.All)
        {
            w.Say($"• {m.Name}: {m.Explanation}");
        }

        var pick = await w.AskAsync("어떤 방법이 좋을까요?", [.. FolderMethod.All.Select(m => C(m.Id, m.Name)), C("close", "나중에")]);
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
            ? await w.AskAsync($"{method.Name}에 필요한 폴더를 만들까요? 이미 있는 폴더는 그대로 두고, 없는 것만 만들어요.", C("yes", "만들어 줘"), C("no", "폴더는 내가 만들게"))
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
            await w.AskAsync("설정을 저장하지 못했어요: " + ex.Message, C("close", "닫기"));
            w.Close();
            return;
        }

        var next = await w.AskAsync($"좋아요! 이제 \"{name}\"은 {method.Name} 방법으로 관리해요. 폴더 아이콘에 작은 표시가 붙고, 폴더에 들어가면 위쪽에 한 줄로 알려 드려요. 지금 정리할 게 있는지 볼까요?",
            C("suggest", "지금 볼래요"), C("close", "나중에"));
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
        var w = Open(owner, $"{method.Name}로 정리");
        var name = Path.GetFileName(root.TrimEnd('\\'));
        IReadOnlyList<ManagedSuggestion> list;
        try
        {
            list = await Task.Run(() => ManagedFolders.Suggest(root, method, DateTime.UtcNow));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await w.AskAsync("폴더를 읽지 못했어요: " + ex.Message, C("close", "닫기"));
            w.Close();
            return;
        }

        if (list.Count == 0)
        {
            await w.AskAsync($"\"{name}\"은 {method.Name} 방법대로 잘 정리돼 있어요! 🙂", C("close", "닫기"));
            w.Close();
            return;
        }

        w.Say($"\"{name}\"을 {method.Name} 방법대로 살펴봤어요. 정리할 게 {list.Count:N0}가지 있어요. 하나씩 보여 드릴게요.");
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
                await w.AskAsync(s.Text, C("ok", "알겠어요"));
                continue;
            }

            var c = await w.AskAsync(s.Text, C("yes", "이대로 정리"), C("detail", "자세히 보기"), C("skip", "나중에"));
            if (c == "detail")
            {
                w.ShowList(s.Moves.Select(m => $"{Path.GetRelativePath(root, m.Src)}  →  {Path.GetRelativePath(root, m.Dst)}"));
                c = await w.AskAsync("이렇게 옮길게요. 괜찮을까요?", C("yes", "이대로 정리"), C("skip", "나중에"));
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
        var msg = $"다 했어요! {done.Count:N0}개를 옮겼어요."
            + (skipped.Count > 0 ? $"\n{skipped.Count:N0}개는 건너뛰었어요 ({string.Join(", ", skipped.Select(x => x.Reason).Distinct())})." : "")
            + "\n마음에 안 들면 [되돌리기] 한 번이면 원래대로예요.";
        var end = await w.AskAsync(msg, C("open", "폴더 열기"), C("undo", "되돌리기"), C("close", "닫기"));
        if (end == "open")
        {
            OpenFolder(root);
        }
        else if (end == "undo")
        {
            var n = await Task.Run(() => ManagedFolders.Undo(CleanupPaths.ManagedJournal));
            await w.AskAsync($"{n:N0}개를 원래 자리로 돌려놨어요.", C("close", "닫기"));
        }

        w.Close();
    }

    private static async void Later(GuideWindow w)
    {
        if (w.IsClosed)
        {
            return;
        }

        await w.AskAsync("알겠어요. 아무것도 바꾸지 않았어요. 생각나면 언제든 다시 불러 주세요!", C("close", "닫기"));
        w.Close();
    }
}

/// <summary>처음 실행 때 30초 둘러보기: 두 창과 네 묶음(찾기·정리하기·지키기·연결하기)을 한 단계씩 소개한다.</summary>
public static class FirstRunTour
{
    private static string Stamp => Path.Combine(App.DataDir, "tour-shown.txt");

    public static bool Shown => File.Exists(Stamp);

    public static async Task RunAsync(Window owner)
    {
        try
        {
            File.WriteAllText(Stamp, DateTime.UtcNow.ToString("O"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        var w = new GuideWindow("Foldy 둘러보기") { Owner = owner, Height = 480 };
        w.Show();
        var c = await w.AskAsync("안녕하세요! Foldy예요 👋 30초만 같이 둘러볼까요?", ("go", "둘러보기"), ("skip", "건너뛰기"));
        if (c != "go")
        {
            w.Close();
            return;
        }

        (string Text, string Next)[] steps =
        [
            ("가운데에는 폴더 창이 두 개 있어요. 왼쪽에서 오른쪽으로 끌어 놓거나 F5를 누르면 복사, F6이면 이동이에요. Tab 키로 두 창을 오가요.", "다음"),
            ("왼쪽 사이드바의 도구는 네 묶음이에요. 🔍 찾기: 파일 이름 검색(Ctrl+F), 무엇이 공간을 차지하는지 보는 폴더 분석.", "다음"),
            ("🧹 정리하기: 바탕화면·다운로드·로그 정리, 폴더 구조 템플릿, 폴더마다 정리 방법 정해 두기. 저절로 버리지 않고, 먼저 보여 드리고, 되돌릴 수 있어요.", "다음"),
            ("🔒 지키기: 잠긴 메모, 파일 암호 걸기, 금고, 휴지통. 🔗 연결하기: SFTP 서버, Git, 터미널.", "다음"),
            ("모르는 게 있으면 F1(도움말), 모든 명령은 Ctrl+Shift+P로 이름만 쳐서 찾을 수 있어요. 이 안내는 명령 목록의 \"둘러보기\"로 다시 볼 수 있어요.", "시작하기"),
        ];
        foreach (var (text, next) in steps)
        {
            if (await w.AskAsync(text, ("next", next), ("skip", "그만 보기")) != "next")
            {
                break;
            }
        }

        if (!w.IsClosed)
        {
            w.Close();
        }
    }
}
