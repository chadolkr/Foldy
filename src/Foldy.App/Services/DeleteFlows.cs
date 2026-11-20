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

    public const string Title = "많은 파일 지우기";

    /// <summary>대화 내용 (문서 캡처 장면도 같은 흐름을 쓴다).</summary>
    internal static async Task<BigDeleteChoice> RunAsync(GuideWindow w, BigDeleteQuestion q)
    {
        {
            var n = q.Count.Capped ? $"{q.Count.Files:N0}개 넘게" : $"{q.Count.Files:N0}개";
            var what = q.Paths.Count == 1 ? $"'{System.IO.Path.GetFileName(System.IO.Path.TrimEndingDirectorySeparator(q.Paths[0]))}'" : $"고른 {q.Paths.Count:N0}개 항목";
            w.Say($"{what} 안에 파일이 {n} 있어요 ({FileItem.FormatBytes(q.Count.Bytes)}{(q.Count.Capped ? " 이상" : "")}).");
            w.Say("휴지통으로 보내면 나중에 되살릴 수 있어요. 다만 이만큼 많으면 몇 분에서 몇십 분 걸릴 수 있어요.");
            if (!q.CanFast)
            {
                w.Say("이 위치는 Windows나 사용자 폴더처럼 중요한 곳이라 빨리 지우기는 쓸 수 없어요.");
                var only = await w.AskAsync(null, ("recycle", "휴지통으로"), ("cancel", "그만두기"));
                return only == "recycle" ? BigDeleteChoice.Recycle : BigDeleteChoice.Cancel;
            }

            w.Say("빨리 지우기는 휴지통을 거치지 않고 한꺼번에 지워서 훨씬 빨라요. 대신 되살릴 수 없어요.");
            var c = await w.AskAsync(null, ("recycle", "휴지통으로 (되살릴 수 있어요)"), ("fast", "빨리 지우기"), ("cancel", "그만두기"));
            if (c == "recycle")
            {
                return BigDeleteChoice.Recycle;
            }

            if (c != "fast")
            {
                return BigDeleteChoice.Cancel;
            }

            w.Say("⚠ 한 번만 더 확인할게요. 빨리 지우기로 지운 파일은 휴지통에도 남지 않아서 되살릴 수 없어요. 중간에 멈춰도 이미 지운 것은 돌아오지 않아요.");
            w.ShowList(q.Paths, max: 5);
            var sure = await w.AskAsync(null, ("cancel", "그만두기"), ("fast", "되살릴 수 없어도 지울게요"));
            return sure == "fast" ? BigDeleteChoice.Fast : BigDeleteChoice.Cancel;
        }
    }
}
