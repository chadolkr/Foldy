using System.Windows;

namespace Foldy.UI.Imaging;

/// <summary>
/// 썸네일 격자 배치 계산 (순수 함수, WPF 객체 없음). 좌표는 내용 좌표(스크롤 전) DIP.
/// 칸 = 썸네일 정사각형 + 아래 이름 한 줄. 칸 사이 간격은 고정, 왼쪽 정렬.
/// </summary>
public readonly record struct GridLayout
{
    public const double Margin = 8;
    public const double Gap = 8;
    public const double CellPadding = 6;

    public GridLayout(double viewportWidth, double thumbnailSize, int count, double labelHeight)
    {
        ThumbnailSize = Math.Max(16, thumbnailSize);
        Count = Math.Max(0, count);
        LabelHeight = Math.Max(0, labelHeight);
        CellWidth = ThumbnailSize + 2 * CellPadding;
        CellHeight = ThumbnailSize + LabelHeight + 3 * CellPadding;
        var usable = Math.Max(0, viewportWidth - 2 * Margin);
        Columns = Math.Max(1, (int)((usable + Gap) / (CellWidth + Gap)));
        Rows = Count == 0 ? 0 : (Count + Columns - 1) / Columns;
    }

    public double ThumbnailSize { get; }

    public int Count { get; }

    public double LabelHeight { get; }

    public double CellWidth { get; }

    public double CellHeight { get; }

    public int Columns { get; }

    public int Rows { get; }

    public double RowPitch => CellHeight + Gap;

    public double ColumnPitch => CellWidth + Gap;

    public double ExtentHeight => Rows == 0 ? 0 : 2 * Margin + Rows * CellHeight + (Rows - 1) * Gap;

    public double ExtentWidth => 2 * Margin + Columns * CellWidth + (Columns - 1) * Gap;

    /// <summary>칸 전체 사각형.</summary>
    public Rect CellRect(int index)
    {
        var r = index / Columns;
        var c = index % Columns;
        return new Rect(Margin + c * ColumnPitch, Margin + r * RowPitch, CellWidth, CellHeight);
    }

    /// <summary>칸 안의 썸네일 정사각형.</summary>
    public Rect ThumbnailRect(int index)
    {
        var cell = CellRect(index);
        return new Rect(cell.X + CellPadding, cell.Y + CellPadding, ThumbnailSize, ThumbnailSize);
    }

    /// <summary>칸 안의 이름 줄.</summary>
    public Rect LabelRect(int index)
    {
        var cell = CellRect(index);
        return new Rect(cell.X + CellPadding, cell.Y + 2 * CellPadding + ThumbnailSize, ThumbnailSize, LabelHeight);
    }

    /// <summary>내용 좌표의 점이 가리키는 항목. 간격·여백·빈 자리면 -1.</summary>
    public int IndexAt(Point p)
    {
        var x = p.X - Margin;
        var y = p.Y - Margin;
        if (x < 0 || y < 0)
        {
            return -1;
        }

        var c = (int)(x / ColumnPitch);
        var r = (int)(y / RowPitch);
        if (c >= Columns || x - c * ColumnPitch > CellWidth || y - r * RowPitch > CellHeight)
        {
            return -1;
        }

        var i = r * Columns + c;
        return i < Count ? i : -1;
    }

    /// <summary>[offsetY, offsetY + height] 구간에 조금이라도 걸치는 항목 범위. 없으면 (0, -1).</summary>
    public (int First, int Last) VisibleRange(double offsetY, double height)
    {
        if (Count == 0 || height <= 0)
        {
            return (0, -1);
        }

        var firstRow = Math.Max(0, (int)Math.Floor((offsetY - Margin - CellHeight) / RowPitch) + 1);
        var lastRow = Math.Min(Rows - 1, (int)Math.Floor((offsetY + height - Margin) / RowPitch));
        if (lastRow < firstRow)
        {
            return (0, -1);
        }

        return (firstRow * Columns, Math.Min(Count - 1, (lastRow + 1) * Columns - 1));
    }

    /// <summary>한 화면에 들어가는 행 수 (PageUp/PageDown 이동량).</summary>
    public int RowsPerPage(double viewportHeight) => Math.Max(1, (int)(viewportHeight / RowPitch));

    /// <summary>방향키 이동. 위·아래는 열 수만큼, 범위 밖이면 끝에 멈춘다.</summary>
    public int Move(int from, int dx, int dy)
    {
        if (Count == 0)
        {
            return -1;
        }

        if (from < 0)
        {
            return 0;
        }

        var target = from + dx + dy * Columns;
        if (dy != 0 && (target < 0 || target >= Count))
        {
            // 마지막 줄이 짧을 때 아래로 가면 마지막 항목, 맨 위에서 위로 가면 그대로
            return dy > 0 ? (from / Columns < Rows - 1 ? Count - 1 : from) : from;
        }

        return Math.Clamp(target, 0, Count - 1);
    }
}
