using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Foldy.Presentation.Mascot;

namespace Foldy.UI.Mascot;

/// <summary>
/// 장면을 비트맵으로 렌더링한다. 레트로 모드(저해상도 렌더 + 팔레트 축소)와 PNG 확인용 하네스가 함께 쓴다.
/// UI 스레드(또는 STA 스레드) 전용.
/// </summary>
public static class MascotBitmap
{
    /// <summary>벡터 그대로 렌더 (배율 scale). 배경은 투명.</summary>
    public static BitmapSource RenderVector(MascotPainter painter, double width, MascotFrame frame, double u, double t, double scale = 1, int cycle = 0)
    {
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.PushTransform(new ScaleTransform(scale, scale));
            painter.Paint(dc, width, frame, u, t, cycle);
            dc.Pop();
        }

        var rtb = new RenderTargetBitmap(
            Math.Max(1, (int)Math.Ceiling(width * scale)),
            Math.Max(1, (int)Math.Ceiling(MascotPainter.DesignHeight * scale)),
            96,
            96,
            PixelFormats.Pbgra32);
        rtb.Render(dv);
        rtb.Freeze();
        return rtb;
    }

    /// <summary>
    /// 레트로: 1/pixel 크기로 안티앨리어싱 없이 그리고, 알파를 이진화하고 색을 6단계로 줄인 WriteableBitmap을 돌려준다.
    /// 화면에는 NearestNeighbor로 pixel배 확대해서 그린다.
    /// </summary>
    public static WriteableBitmap RenderRetro(MascotPainter painter, double width, MascotFrame frame, double u, double t, int pixel = 3, int cycle = 0)
    {
        pixel = Math.Max(1, pixel);
        var dv = new DrawingVisual();
        RenderOptions.SetEdgeMode(dv, EdgeMode.Aliased);
        RenderOptions.SetBitmapScalingMode(dv, BitmapScalingMode.NearestNeighbor);
        // 이름표 글자는 1/pixel로 줄이면 읽을 수 없으므로 기록만 하고, 확대한 뒤 painter.PaintLabels로 얹는다.
        var inline = painter.DrawLabelsInline;
        painter.DrawLabelsInline = false;
        try
        {
            using var dc = dv.RenderOpen();
            dc.PushTransform(new ScaleTransform(1.0 / pixel, 1.0 / pixel));
            painter.Paint(dc, width, frame, u, t, cycle);
            dc.Pop();
        }
        finally
        {
            painter.DrawLabelsInline = inline;
        }

        var w = Math.Max(1, (int)Math.Ceiling(width / pixel));
        var h = Math.Max(1, (int)Math.Ceiling(MascotPainter.DesignHeight / pixel));
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);

        var stride = w * 4;
        var px = new byte[stride * h];
        rtb.CopyPixels(px, stride, 0);
        Posterize(px);

        var wb = new WriteableBitmap(w, h, 96, 96, PixelFormats.Pbgra32, null);
        wb.WritePixels(new Int32Rect(0, 0, w, h), px, stride, 0);
        wb.Freeze();
        return wb;
    }

    /// <summary>알파 이진화(프리멀티플라이 해제) + 채널당 6단계. 레트로 도트 느낌.</summary>
    internal static void Posterize(byte[] bgra)
    {
        for (var i = 0; i < bgra.Length; i += 4)
        {
            var a = bgra[i + 3];
            if (a < 96)
            {
                bgra[i] = bgra[i + 1] = bgra[i + 2] = bgra[i + 3] = 0;
                continue;
            }

            for (var c = 0; c < 3; c++)
            {
                var straight = Math.Min(255, bgra[i + c] * 255 / a);
                bgra[i + c] = (byte)(((straight + 25) / 51) * 51);
            }

            bgra[i + 3] = 255;
        }
    }
}
