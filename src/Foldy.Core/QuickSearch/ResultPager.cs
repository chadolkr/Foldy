namespace Foldy.Core.QuickSearch;

/// <summary>
/// 무엇이든 찾기의 파일 결과를 끝까지 보기 (2026-11-23 사용자: "더 보기는 아래까지 스크롤하면 안 보인다.
/// 인피니티 스크롤이나 페이지 번호로 직관적으로, 전체가 몇 건인지 알 수 있게").
/// 찾은 것은 모두 세고(<see cref="Total"/>), 화면에는 처음 몇 개만 그리다가 아래로 내리면 한 묶음씩 더 그린다.
/// 메모리를 지키려고 <see cref="ResultPaging.MaxKept"/>개까지만 들고 있고, 그 뒤는 세기만 한다.
/// </summary>
/// <typeparam name="T">결과 하나 (파일·폴더).</typeparam>
public sealed class ResultPager<T>
{
    private readonly List<T> _kept = [];
    private int _want;

    /// <param name="first">처음 그릴 수 (모두 보기 30, 한 묶음만 볼 때 50).</param>
    public ResultPager(int first) => _want = Math.Max(1, first);

    /// <summary>찾은 수 전부.</summary>
    public long Total { get; private set; }

    /// <summary>화면에 그린 수.</summary>
    public int Shown { get; private set; }

    /// <summary>찾기가 끝났는지.</summary>
    public bool Done { get; private set; }

    /// <summary>들고 있는 수 (<see cref="ResultPaging.MaxKept"/>까지).</summary>
    public int Kept => _kept.Count;

    /// <summary>아직 그리지 않았지만 들고 있는 것이 있는지 (아래로 내리면 더 보임).</summary>
    public bool HasMore => Shown < _kept.Count;

    /// <summary>찾은 것이 너무 많아 끝까지는 못 보는지 (낱말을 더 넣으라고 알림).</summary>
    public bool Capped => Total > _kept.Count;

    /// <summary>새로 찾은 묶음을 더한다.</summary>
    public void Add(IReadOnlyList<T> batch)
    {
        Total += batch.Count;
        var room = ResultPaging.MaxKept - _kept.Count;
        if (room > 0)
        {
            _kept.AddRange(batch.Count <= room ? batch : batch.Take(room));
        }
    }

    /// <summary>찾기가 끝났다.</summary>
    public void Finish() => Done = true;

    /// <summary>한 묶음 더 보고 싶다 (아래 끝에 닿음). 실제로 그릴 것은 <see cref="TakeNew"/>.</summary>
    public void WantMore()
    {
        if (Shown >= _want)
        {
            _want = Shown + ResultPaging.Page;
        }
    }

    /// <summary>지금 그려야 할 새 결과 (그린 것으로 센다).</summary>
    public IReadOnlyList<T> TakeNew()
    {
        var upTo = Math.Min(_want, _kept.Count);
        if (upTo <= Shown)
        {
            return [];
        }

        var fresh = _kept.GetRange(Shown, upTo - Shown);
        Shown = upTo;
        return fresh;
    }
}

/// <summary>끝까지 보기의 수와 스크롤 판단.</summary>
public static class ResultPaging
{
    /// <summary>아래로 내릴 때마다 더 그리는 수.</summary>
    public const int Page = 50;

    /// <summary>들고 있는 최대 수 (그 뒤는 세기만).</summary>
    public const int MaxKept = 10_000;

    /// <summary>스크롤이 아래 끝 가까이 왔는지 (남은 높이가 <paramref name="margin"/>보다 작음).</summary>
    public static bool NearEnd(double offset, double viewport, double extent, double margin = 160) =>
        extent > 0 && offset + viewport >= extent - margin;
}
