using Foldy.Core.Localization;
using Foldy.Core.Space;
using Foldy.Presentation.Files;
using Foldy.Presentation.Services;
using Foldy.Presentation.Shell;

namespace Foldy.App.Services;

/// <summary>
/// 안내 창(수다쟁이 Foldy)에서 쓰는 "시작 전 공간 확인" (사용자 원칙 2026-11-22: 한참 하다가 모자란다고 하지 않는다).
/// 모자라면 얼마 필요하고 얼마 남았는지 알리고, [공간 늘리기]로 그 드라이브 폴더 분석을 연 뒤 [다시 확인]으로 이어 한다.
/// </summary>
internal static class SpaceFlows
{
    private static (string, string) C(string id, string label) => (id, label);

    /// <summary>공간이 되면 true. 모자라면 알리고 사용자가 공간을 늘린 뒤 다시 보게 한다. 그만두면 false.</summary>
    public static async Task<bool> EnsureAsync(GuideWindow w, ShellViewModel shell, Func<SpaceCheck> check)
    {
        var r = await Task.Run(check);
        while (!r.Ok && !w.IsClosed)
        {
            w.Say(SpaceText.Explain(r));
            if (r.Problem == SpaceProblem.Fat32TooBig)
            {
                // 공간을 늘려도 풀리지 않는다 (다른 드라이브를 써야 함)
                await w.AskAsync(null, C("close", Loc.T("cf.close")));
                return false;
            }

            var choices = new List<(string, string)>();
            if (r.Root is { } root && shell.FreeUpSpace is not null)
            {
                choices.Add(C("free", Loc.T("side.space")));
            }

            choices.Add(C("recheck", Loc.T("space.recheck")));
            choices.Add(C("close", Loc.T("dlg.cancel")));
            var c = await w.AskAsync(null, [.. choices]);
            if (c == "free" && r.Root is { } at)
            {
                shell.FreeUpSpace?.Invoke(at);
                w.Say(Loc.T("space.afterFreeUp"));
                c = await w.AskAsync(null, C("recheck", Loc.T("space.recheck")), C("close", Loc.T("dlg.cancel")));
            }

            if (c != "recheck")
            {
                return false;
            }

            r = await Task.Run(check);
        }

        return r.Ok;
    }

    /// <summary>큰 작업이면 시작 전에 필요한 공간을 한 줄로 알린다 ("크기 제한은 없어요 … 약 N 필요 (지금 M 남음)").</summary>
    public static void MentionIfBig(GuideWindow w, string folder, long need)
    {
        if (need < DiskSpace.MentionFrom || DiskSpace.Probe(folder) is not { } room)
        {
            return;
        }

        w.Say(Loc.F("sec.space.note", FileItem.FormatBytes(need), FileItem.FormatBytes(room.Free)));
    }
}
