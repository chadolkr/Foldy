using System.Windows;
using Foldy.Presentation.Files;
using Foldy.Presentation.Services;

namespace Foldy.App.Services;

/// <summary>
/// 파일이 아주 많은 곳을 지울 때의 대화. 기본은 휴지통(되살릴 수 있음). 빨리 지우기는 되살릴 수 없다는 걸
/// 두 번 알리고, 마지막 확인의 기본 단추는 [그만두기]다 (Enter로 실수해도 지워지지 않게).
/// </summary>
internal static class DeleteFlows
{
    public static async Task<BigDeleteChoice> AskBigDeleteAsync(Window owner, BigDeleteQuestion q)
    {
        var w = new GuideWindow(Title) { Owner = owner };
        w.Show();
        try
        {
            return await RunAsync(w, q);
        }
        finally
        {
            if (!w.IsClosed)
            {
                w.Close();
            }
        }
    }

    public static string Title => Foldy.Core.Localization.Loc.T("del.title");

    /// <summary>대화 내용 (문서 캡처 장면도 같은 흐름을 쓴다).</summary>
    internal static async Task<BigDeleteChoice> RunAsync(GuideWindow w, BigDeleteQuestion q)
    {
        {
            var n = q.Count.Capped ? Foldy.Core.Localization.Loc.F("del.moreThan", q.Count.Files) : Foldy.Core.Localization.Loc.F("del.nFiles", q.Count.Files);
            var what = q.Paths.Count == 1 ? $"'{System.IO.Path.GetFileName(System.IO.Path.TrimEndingDirectorySeparator(q.Paths[0]))}'" : Foldy.Core.Localization.Loc.F("del.nItems", q.Paths.Count);
            w.Say(Foldy.Core.Localization.Loc.F("del.summary", what, n, FileItem.FormatBytes(q.Count.Bytes), (q.Count.Capped ? Foldy.Core.Localization.Loc.T("del.orMore") : "")));
            w.Say(Foldy.Core.Localization.Loc.T("del.bulkRecycle"));
            if (!q.CanFast)
            {
                w.Say(Foldy.Core.Localization.Loc.T("del.protected"));
                var only = await w.AskAsync(null, ("recycle", Foldy.Core.Localization.Loc.T("ops.toRecycle")), ("cancel", Foldy.Core.Localization.Loc.T("dlg.cancel")));
                return only == "recycle" ? BigDeleteChoice.Recycle : BigDeleteChoice.Cancel;
            }

            w.Say(Foldy.Core.Localization.Loc.T("del.fastWhy"));
            var c = await w.AskAsync(null, ("recycle", Foldy.Core.Localization.Loc.T("del.recycleSafe")), ("fast", Foldy.Core.Localization.Loc.T("del.fast")), ("cancel", Foldy.Core.Localization.Loc.T("dlg.cancel")));
            if (c == "recycle")
            {
                return BigDeleteChoice.Recycle;
            }

            if (c != "fast")
            {
                return BigDeleteChoice.Cancel;
            }

            w.Say(Foldy.Core.Localization.Loc.T("del.fastConfirm"));
            w.ShowList(q.Paths, max: 5);
            var sure = await w.AskAsync(null, ("cancel", Foldy.Core.Localization.Loc.T("dlg.cancel")), ("fast", Foldy.Core.Localization.Loc.T("del.fastYes")));
            return sure == "fast" ? BigDeleteChoice.Fast : BigDeleteChoice.Cancel;
        }
    }
}
