namespace Foldy.Analysis;

public readonly record struct TreemapRect(double X, double Y, double Width, double Height)
{
    public double Area => Width * Height;

    public double Right => X + Width;

    public double Bottom => Y + Height;

    public bool Contains(double x, double y) => x >= X && x < Right && y >= Y && y < Bottom;
}

/// <summary>
/// 정사각형에 가까운 트리맵 배치 (Bruls, Huizing, van Wijk, "Squarified Treemaps", 2000).
/// 화면(WPF)과 무관한 순수 계산이라 시험할 수 있다. 크기는 큰 순으로 주는 것이 좋다 (그래야 비율이 좋다).
/// </summary>
public static class SquarifiedLayout
{
    /// <summary>각 크기에 비례하는 넓이의 직사각형들을 <paramref name="bounds"/> 안에 겹치지 않게 놓는다. 결과는 입력 순서.</summary>
    public static TreemapRect[] Layout(IReadOnlyList<double> sizes, TreemapRect bounds)
    {
        ArgumentNullException.ThrowIfNull(sizes);
        var result = new TreemapRect[sizes.Count];
        var total = 0.0;
        foreach (var s in sizes)
        {
            total += s > 0 ? s : 0;
        }

        if (total <= 0 || bounds.Width <= 0 || bounds.Height <= 0)
        {
            for (var i = 0; i < result.Length; i++)
            {
                result[i] = new TreemapRect(bounds.X, bounds.Y, 0, 0);
            }

            return result;
        }

        var scale = bounds.Area / total;
        // 0 이하 크기는 빈 칸으로 두고 배치에서 뺀다.
        var idx = new List<int>(sizes.Count);
        for (var i = 0; i < sizes.Count; i++)
        {
            if (sizes[i] > 0)
            {
                idx.Add(i);
            }
            else
            {
                result[i] = new TreemapRect(bounds.X, bounds.Y, 0, 0);
            }
        }

        var free = bounds;
        var start = 0;
        while (start < idx.Count)
        {
            var side = Math.Min(free.Width, free.Height);
            // 줄에 하나씩 더해 보며 가장 나쁜 가로세로 비율이 나빠지기 직전까지
            var end = start + 1;
            var rowSum = sizes[idx[start]] * scale;
            var worst = Worst(idx, sizes, start, end, rowSum, side, scale);
            while (end < idx.Count)
            {
                var nextSum = rowSum + (sizes[idx[end]] * scale);
                var w = Worst(idx, sizes, start, end + 1, nextSum, side, scale);
                if (w > worst)
                {
                    break;
                }

                worst = w;
                rowSum = nextSum;
                end++;
            }

            // 마지막 줄은 남은 칸을 정확히 채운다 (부동소수 오차 흡수)
            var last = end == idx.Count;
            if (free.Width >= free.Height)
            {
                // 왼쪽에 세로 줄
                var thick = last ? free.Width : Math.Min(free.Width, rowSum / free.Height);
                var y = free.Y;
                for (var k = start; k < end; k++)
                {
                    var h = k == end - 1 ? free.Bottom - y : sizes[idx[k]] * scale / thick;
                    result[idx[k]] = new TreemapRect(free.X, y, thick, Math.Max(0, h));
                    y += h;
                }

                free = new TreemapRect(free.X + thick, free.Y, Math.Max(0, free.Width - thick), free.Height);
            }
            else
            {
                // 위쪽에 가로 줄
                var thick = last ? free.Height : Math.Min(free.Height, rowSum / free.Width);
                var x = free.X;
                for (var k = start; k < end; k++)
                {
                    var w = k == end - 1 ? free.Right - x : sizes[idx[k]] * scale / thick;
                    result[idx[k]] = new TreemapRect(x, free.Y, Math.Max(0, w), thick);
                    x += w;
                }

                free = new TreemapRect(free.X, free.Y + thick, free.Width, Math.Max(0, free.Height - thick));
            }

            start = end;
        }

        return result;
    }

    /// <summary>줄 [start, end)를 길이 side인 변에 놓을 때 가장 나쁜 가로세로 비율 (1이 정사각형).</summary>
    private static double Worst(List<int> idx, IReadOnlyList<double> sizes, int start, int end, double sum, double side, double scale)
    {
        double max = 0, min = double.MaxValue;
        for (var k = start; k < end; k++)
        {
            var a = sizes[idx[k]] * scale;
            max = Math.Max(max, a);
            min = Math.Min(min, a);
        }

        var s2 = side * side;
        var sum2 = sum * sum;
        return Math.Max(s2 * max / sum2, sum2 / (s2 * min));
    }

    /// <summary>점이 들어 있는 칸의 위치 (없으면 -1).</summary>
    public static int HitTest(IReadOnlyList<TreemapRect> rects, double x, double y)
    {
        ArgumentNullException.ThrowIfNull(rects);
        for (var i = 0; i < rects.Count; i++)
        {
            if (rects[i].Area > 0 && rects[i].Contains(x, y))
            {
                return i;
            }
        }

        return -1;
    }
}
