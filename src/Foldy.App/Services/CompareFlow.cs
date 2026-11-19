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
        var w = new GuideWindow("두 창 비교") { Owner = owner };
        w.Show();
        var ln = Name(left);
        var rn = Name(right);
        if (string.Equals(left.TrimEnd('\\'), right.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
        {
            await w.AskAsync("두 창이 같은 폴더를 보고 있어요. 한쪽 창에서 비교할 다른 폴더를 열어 주세요.", C("close", "닫기"));
            w.Close();
            return;
        }

        w.Say($"왼쪽 \"{ln}\"과 오른쪽 \"{rn}\"을 비교할게요. 파일 크기와 고친 시각으로 봐요.");
        w.Busy();
        var lastSync = LastSync(left, right);
        IReadOnlyList<DiffItem> diff;
        try
        {
            diff = await Task.Run(() => FolderCompare.Compare(left, right, lastSync));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await w.AskAsync("비교하지 못했어요: " + ex.Message, C("close", "닫기"));
            w.Close();
            return;
        }

        if (w.IsClosed)
        {
            return;
        }

        if (diff.Count == 0)
        {
            await w.AskAsync("두 폴더가 똑같아요. 맞출 게 없어요! 🙂", C("close", "닫기"));
            w.Close();
            return;
        }

        int Count(DiffKind k) => diff.Count(d => d.Kind == k);
        var leftNewer = Count(DiffKind.LeftNewer);
        var rightNewer = Count(DiffKind.RightNewer);
        var conflicts = diff.Where(d => d.Kind == DiffKind.Conflict).ToList();
        var parts = new List<string>();
        if (Count(DiffKind.LeftOnly) > 0) parts.Add($"왼쪽에만 {Count(DiffKind.LeftOnly):N0}개");
        if (Count(DiffKind.RightOnly) > 0) parts.Add($"오른쪽에만 {Count(DiffKind.RightOnly):N0}개");
        if (leftNewer > 0) parts.Add($"왼쪽이 더 최신 {leftNewer:N0}개");
        if (rightNewer > 0) parts.Add($"오른쪽이 더 최신 {rightNewer:N0}개");
        if (conflicts.Count > 0) parts.Add($"양쪽 다 고친 파일 {conflicts.Count:N0}개");
        w.Say("다른 곳: " + string.Join(", ", parts) + ".");
        var verdict = leftNewer > rightNewer ? $"왼쪽(\"{ln}\")이 더 최신이에요. {leftNewer:N0}개 파일이 왼쪽에서 고쳐졌어요."
            : rightNewer > leftNewer ? $"오른쪽(\"{rn}\")이 더 최신이에요. {rightNewer:N0}개 파일이 오른쪽에서 고쳐졌어요."
            : "어느 한쪽이 더 최신이라고 하기 어려워요.";
        w.Say(verdict);

        var choice = await w.AskAsync(
            "어느 쪽으로 맞출까요? 한쪽에만 있는 파일은 반대쪽에 복사만 하고 지우지 않아요. 덮어쓰는 파일은 이전 버전으로 남겨서 [되돌리기]로 돌릴 수 있어요.",
            C("l2r", "왼쪽 → 오른쪽"), C("r2l", "오른쪽 → 왼쪽"), C("both", "양쪽 최신으로 맞추기"), C("detail", "자세히 보기"), C("later", "나중에"));
        if (choice == "detail")
        {
            w.ShowList(diff.Select(d => $"{Label(d.Kind)}  ·  {d.RelPath}"));
            choice = await w.AskAsync("어느 쪽으로 맞출까요?", C("l2r", "왼쪽 → 오른쪽"), C("r2l", "오른쪽 → 왼쪽"), C("both", "양쪽 최신으로 맞추기"), C("later", "나중에"));
        }

        var dir = choice switch { "l2r" => MergeDirection.LeftToRight, "r2l" => MergeDirection.RightToLeft, "both" => MergeDirection.Both, _ => (MergeDirection?)null };
        if (dir is null)
        {
            if (!w.IsClosed)
            {
                await w.AskAsync("알겠어요. 아무것도 바꾸지 않았어요.", C("close", "닫기"));
                w.Close();
            }

            return;
        }

        // 양쪽 다 고친 파일은 저절로 고르지 않는다: 하나씩 묻는다 (10개까지, 나머지는 그대로 둠)
        var keep = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in conflicts.Take(10))
        {
            var k = await w.AskAsync($"\"{c.RelPath}\"은 지난번 맞춘 뒤 양쪽 다 고쳐졌어요. (왼쪽 {c.LeftUtc.ToLocalTime():MM-dd HH:mm}, 오른쪽 {c.RightUtc.ToLocalTime():MM-dd HH:mm}) 어느 쪽을 남길까요?",
                C("left", "왼쪽 것"), C("right", "오른쪽 것"), C("skip", "그대로 두기"));
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
            await w.AskAsync("이 방향으로는 복사할 게 없어요. 더 최신 파일을 오래된 것으로 덮어쓰지 않거든요.", C("close", "닫기"));
            w.Close();
            return;
        }

        w.ShowList(steps.Select(s => $"{(s.Dst.StartsWith(right, StringComparison.OrdinalIgnoreCase) ? "→ 오른쪽" : "← 왼쪽")}  {s.RelPath}"));
        if (await w.AskAsync($"이렇게 {steps.Count:N0}개를 복사할게요. 괜찮을까요?", C("go", "이대로 맞추기"), C("later", "나중에")) != "go")
        {
            if (!w.IsClosed)
            {
                w.Close();
            }

            return;
        }

        w.Busy();
        var status = w.Say("맞추고 있어요…");
        var (done, skipped) = await Task.Run(() => FolderCompare.Apply(steps, Journal));
        SaveSync(left, right);
        await shell.RefreshFoldersAsync([left, right]);
        status.Text = "다 했어요!";
        var msg = $"{done.Count:N0}개를 맞췄어요." + (skipped.Count > 0 ? $" {skipped.Count:N0}개는 건너뛰었어요 ({string.Join(", ", skipped.Select(s => s.Reason).Distinct())})." : "")
            + "\n마음에 안 들면 [되돌리기] 한 번이면 원래대로예요.";
        if (await w.AskAsync(msg, C("undo", "되돌리기"), C("close", "닫기")) == "undo")
        {
            var n = await Task.Run(() => FolderCompare.Undo(Journal));
            await shell.RefreshFoldersAsync([left, right]);
            await w.AskAsync($"{n:N0}개를 원래대로 돌려놨어요.", C("close", "닫기"));
        }

        if (!w.IsClosed)
        {
            w.Close();
        }
    }

    private static string Label(DiffKind k) => k switch
    {
        DiffKind.LeftOnly => "왼쪽에만",
        DiffKind.RightOnly => "오른쪽에만",
        DiffKind.LeftNewer => "왼쪽이 최신",
        DiffKind.RightNewer => "오른쪽이 최신",
        DiffKind.Conflict => "양쪽 다 고침",
        _ => "같음",
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
