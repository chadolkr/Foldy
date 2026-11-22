using System.IO;
using System.Text.Json;
using System.Windows;
using Foldy.Core.Compare;
using Foldy.Presentation.Shell;

namespace Foldy.App.Services;

/// <summary>
/// 🔍 찾기 → 두 창 비교·맞추기 (대화 창). 무엇이 다른지, 어느 쪽이 더 최신인지 알려 주고, 방향을 고르면 복사할 목록을 먼저 보여 준 뒤 맞춘다.
/// 한쪽에만 있는 파일은 지우지 않는다. 덮어쓴 파일은 이전 버전으로 남겨 [되돌리기]로 돌린다. 양쪽 다 바뀐 파일은 하나씩 고르게 한다.
/// </summary>
public static class CompareFlow
{
    private static string Journal => Path.Combine(App.DataDir, "compare-last.json");

    private static string SyncStore => Path.Combine(App.DataDir, "compare-sync.json");

    private static (string, string) C(string id, string label) => (id, label);

    public static async Task RunAsync(Window owner, string left, string right, ShellViewModel shell)
    {
        var w = new GuideWindow(Foldy.Core.Localization.Loc.T("cmp.title")) { Owner = owner };
        w.Show();
        var ln = Name(left);
        var rn = Name(right);
        if (string.Equals(left.TrimEnd('\\'), right.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
        {
            await w.AskAsync(Foldy.Core.Localization.Loc.T("cmp.same"), C("close", Foldy.Core.Localization.Loc.T("cf.close")));
            w.Close();
            return;
        }

        w.Say(Foldy.Core.Localization.Loc.F("cmp.hello", ln, rn));
        w.Busy();
        var lastSync = LastSync(left, right);
        IReadOnlyList<DiffItem> diff;
        try
        {
            diff = await Task.Run(() => FolderCompare.Compare(left, right, lastSync));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await w.AskAsync(Foldy.Core.Localization.Loc.T("cmp.failed") + ex.Message, C("close", Foldy.Core.Localization.Loc.T("cf.close")));
            w.Close();
            return;
        }

        if (w.IsClosed)
        {
            return;
        }

        if (diff.Count == 0)
        {
            await w.AskAsync(Foldy.Core.Localization.Loc.T("cmp.identical"), C("close", Foldy.Core.Localization.Loc.T("cf.close")));
            w.Close();
            return;
        }

        int Count(DiffKind k) => diff.Count(d => d.Kind == k);
        var leftNewer = Count(DiffKind.LeftNewer);
        var rightNewer = Count(DiffKind.RightNewer);
        var conflicts = diff.Where(d => d.Kind == DiffKind.Conflict).ToList();
        var parts = new List<string>();
        if (Count(DiffKind.LeftOnly) > 0) parts.Add(Foldy.Core.Localization.Loc.F("cmp.nLeftOnly", Count(DiffKind.LeftOnly)));
        if (Count(DiffKind.RightOnly) > 0) parts.Add(Foldy.Core.Localization.Loc.F("cmp.nRightOnly", Count(DiffKind.RightOnly)));
        if (leftNewer > 0) parts.Add(Foldy.Core.Localization.Loc.F("cmp.nLeftNewer", leftNewer));
        if (rightNewer > 0) parts.Add(Foldy.Core.Localization.Loc.F("cmp.nRightNewer", rightNewer));
        if (conflicts.Count > 0) parts.Add(Foldy.Core.Localization.Loc.F("cmp.nBoth", conflicts.Count));
        w.Say(Foldy.Core.Localization.Loc.T("cmp.diffs") + string.Join(", ", parts) + ".");
        var verdict = leftNewer > rightNewer ? Foldy.Core.Localization.Loc.F("cmp.leftIsNewer", ln, leftNewer)
            : rightNewer > leftNewer ? Foldy.Core.Localization.Loc.F("cmp.rightIsNewer", rn, rightNewer)
            : Foldy.Core.Localization.Loc.T("cmp.unclear");
        w.Say(verdict);

        var choice = await w.AskAsync(
            Foldy.Core.Localization.Loc.T("cmp.howAsk"),
            C("l2r", Foldy.Core.Localization.Loc.T("cmp.l2r")), C("r2l", Foldy.Core.Localization.Loc.T("cmp.r2l")), C("both", Foldy.Core.Localization.Loc.T("cmp.newest")), C("detail", Foldy.Core.Localization.Loc.T("cf.details")), C("later", Foldy.Core.Localization.Loc.T("sw.later")));
        if (choice == "detail")
        {
            w.ShowList(diff.Select(d => $"{Label(d.Kind)}  ·  {d.RelPath}"));
            choice = await w.AskAsync(Foldy.Core.Localization.Loc.T("cmp.which"), C("l2r", Foldy.Core.Localization.Loc.T("cmp.l2r")), C("r2l", Foldy.Core.Localization.Loc.T("cmp.r2l")), C("both", Foldy.Core.Localization.Loc.T("cmp.newest")), C("later", Foldy.Core.Localization.Loc.T("sw.later")));
        }

        var dir = choice switch { "l2r" => MergeDirection.LeftToRight, "r2l" => MergeDirection.RightToLeft, "both" => MergeDirection.Both, _ => (MergeDirection?)null };
        if (dir is null)
        {
            if (!w.IsClosed)
            {
                await w.AskAsync(Foldy.Core.Localization.Loc.T("cmp.nothingChanged"), C("close", Foldy.Core.Localization.Loc.T("cf.close")));
                w.Close();
            }

            return;
        }

        // 양쪽 다 고친 파일은 저절로 고르지 않는다: 하나씩 묻는다 (10개까지, 나머지는 그대로 둠)
        var keep = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in conflicts.Take(10))
        {
            var k = await w.AskAsync(Foldy.Core.Localization.Loc.F("cmp.conflict", c.RelPath, c.LeftUtc.ToLocalTime(), c.RightUtc.ToLocalTime()),
                C("left", Foldy.Core.Localization.Loc.T("cmp.leftOne")), C("right", Foldy.Core.Localization.Loc.T("cmp.rightOne")), C("skip", Foldy.Core.Localization.Loc.T("cf.keep")));
            if (k == "left" || k == "right")
            {
                keep[c.RelPath] = k == "left";
            }
            else if (k == GuideWindow.ClosedChoice)
            {
                return;
            }
        }

        var steps = FolderCompare.Plan(left, right, diff, dir.Value, keep);
        if (steps.Count == 0)
        {
            await w.AskAsync(Foldy.Core.Localization.Loc.T("cmp.noCopy"), C("close", Foldy.Core.Localization.Loc.T("cf.close")));
            w.Close();
            return;
        }

        w.ShowList(steps.Select(s => $"{(s.Dst.StartsWith(right, StringComparison.OrdinalIgnoreCase) ? Foldy.Core.Localization.Loc.T("cmp.toRight") : Foldy.Core.Localization.Loc.T("cmp.toLeft"))}  {s.RelPath}"));
        if (await w.AskAsync(Foldy.Core.Localization.Loc.F("cmp.planAsk", steps.Count), C("go", Foldy.Core.Localization.Loc.T("cmp.go")), C("later", Foldy.Core.Localization.Loc.T("sw.later"))) != "go")
        {
            if (!w.IsClosed)
            {
                w.Close();
            }

            return;
        }

        w.Busy();
        var status = w.Say(Foldy.Core.Localization.Loc.T("cmp.syncing"));
        var (done, skipped) = await Task.Run(() => FolderCompare.Apply(steps, Journal));
        SaveSync(left, right);
        await shell.RefreshFoldersAsync([left, right]);
        status.Text = Foldy.Core.Localization.Loc.T("cf.done");
        var msg = Foldy.Core.Localization.Loc.F("cmp.synced", done.Count) + (skipped.Count > 0 ? Foldy.Core.Localization.Loc.F("cmp.skipped", skipped.Count, string.Join(", ", skipped.Select(s => s.Reason).Distinct())) : "")
            + Foldy.Core.Localization.Loc.T("cf.undoNote");
        if (await w.AskAsync(msg, C("undo", Foldy.Core.Localization.Loc.T("cf.undo")), C("close", Foldy.Core.Localization.Loc.T("cf.close"))) == "undo")
        {
            var n = await Task.Run(() => FolderCompare.Undo(Journal));
            await shell.RefreshFoldersAsync([left, right]);
            await w.AskAsync(Foldy.Core.Localization.Loc.F("cmp.undone", n), C("close", Foldy.Core.Localization.Loc.T("cf.close")));
        }

        if (!w.IsClosed)
        {
            w.Close();
        }
    }

    private static string Label(DiffKind k) => k switch
    {
        DiffKind.LeftOnly => Foldy.Core.Localization.Loc.T("cmp.leftOnly"),
        DiffKind.RightOnly => Foldy.Core.Localization.Loc.T("cmp.rightOnly"),
        DiffKind.LeftNewer => Foldy.Core.Localization.Loc.T("cmp.leftNewer"),
        DiffKind.RightNewer => Foldy.Core.Localization.Loc.T("cmp.rightNewer"),
        DiffKind.Conflict => Foldy.Core.Localization.Loc.T("cmp.both"),
        _ => Foldy.Core.Localization.Loc.T("cmp.equal"),
    };

    private static string Name(string p) => Path.GetFileName(p.TrimEnd('\\')) is { Length: > 0 } n ? n : p;

    private static string Key(string a, string b) => (a.TrimEnd('\\') + "|" + b.TrimEnd('\\')).ToUpperInvariant();

    private static DateTime? LastSync(string a, string b)
    {
        try
        {
            if (File.Exists(SyncStore) && JsonSerializer.Deserialize<Dictionary<string, DateTime>>(File.ReadAllText(SyncStore)) is { } d)
            {
                return d.TryGetValue(Key(a, b), out var t) ? t : d.TryGetValue(Key(b, a), out t) ? t : null;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
        }

        return null;
    }

    private static void SaveSync(string a, string b)
    {
        try
        {
            var d = File.Exists(SyncStore) ? JsonSerializer.Deserialize<Dictionary<string, DateTime>>(File.ReadAllText(SyncStore)) ?? [] : [];
            d.Remove(Key(b, a));
            d[Key(a, b)] = DateTime.UtcNow;
            File.WriteAllText(SyncStore, JsonSerializer.Serialize(d));
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
        }
    }
}
