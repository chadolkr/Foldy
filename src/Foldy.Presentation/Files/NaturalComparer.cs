namespace Foldy.Presentation.Files;

/// <summary>
/// 탐색기(StrCmpLogicalW)와 같은 자연 정렬: 숫자 덩어리는 수 크기로, 나머지는 대소문자 무시 문화권 비교.
/// "file2" &lt; "file10". 순수 관리 코드라 백그라운드 스레드에서 안전하고 테스트하기 쉽다.
/// </summary>
public sealed class NaturalComparer : IComparer<string>
{
    public static readonly NaturalComparer Instance = new();

    private static readonly StringComparer Text = StringComparer.Create(System.Globalization.CultureInfo.CurrentCulture, ignoreCase: true);

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y))
        {
            return 0;
        }

        if (x is null)
        {
            return -1;
        }

        if (y is null)
        {
            return 1;
        }

        int i = 0, j = 0;
        while (i < x.Length && j < y.Length)
        {
            var dx = char.IsAsciiDigit(x[i]);
            var dy = char.IsAsciiDigit(y[j]);
            if (dx && dy)
            {
                var si = i;
                var sj = j;
                while (i < x.Length && x[i] == '0') i++;
                while (j < y.Length && y[j] == '0') j++;
                var ni = i;
                var nj = j;
                while (i < x.Length && char.IsAsciiDigit(x[i])) i++;
                while (j < y.Length && char.IsAsciiDigit(y[j])) j++;
                var lenX = i - ni;
                var lenY = j - nj;
                if (lenX != lenY)
                {
                    return lenX < lenY ? -1 : 1;
                }

                var c = string.CompareOrdinal(x, ni, y, nj, lenX);
                if (c != 0)
                {
                    return c;
                }

                // 같은 값이면 앞의 0이 적은 쪽이 먼저 ("1" < "01")
                var zx = ni - si;
                var zy = nj - sj;
                if (zx != zy)
                {
                    return zx < zy ? -1 : 1;
                }
            }
            else
            {
                var ei = i;
                var ej = j;
                while (ei < x.Length && !char.IsAsciiDigit(x[ei])) ei++;
                while (ej < y.Length && !char.IsAsciiDigit(y[ej])) ej++;
                var c = Text.Compare(x[i..ei], y[j..ej]);
                if (c != 0)
                {
                    // 덩어리 길이가 다르면 짧은 쪽 접두어만 비교했을 때 같은 경우를 처리
                    return c;
                }

                i = ei;
                j = ej;
            }
        }

        return (x.Length - i).CompareTo(y.Length - j);
    }
}
