using System.Globalization;

namespace Foldy.Core.Diagnostics;

/// <summary>프레임 간격 통계. 설계서 2장 완료 기준: 10만 개 폴더에서 프레임 16ms.</summary>
public sealed record FrameStats(
    int Frames,
    double AvgMs,
    double P50Ms,
    double P95Ms,
    double P99Ms,
    double MaxMs,
    int LateFrames,
    double LateThresholdMs)
{
    /// <summary>60Hz 기준 한 프레임(16.7ms)을 놓치지 않았다고 보는 상한. 지터를 감안해 20ms.</summary>
    public const double DefaultLateThresholdMs = 20.0;

    public double LatePercent => Frames == 0 ? 0 : 100.0 * LateFrames / Frames;

    /// <summary>관문 통과 조건: 95% 이상의 프레임이 제시간(p95 ≤ 20ms).</summary>
    public bool PassesBudget => Frames > 0 && P95Ms <= LateThresholdMs;

    public static FrameStats From(IReadOnlyList<double> deltasMs, double lateThresholdMs = DefaultLateThresholdMs)
    {
        if (deltasMs.Count == 0)
        {
            return new FrameStats(0, 0, 0, 0, 0, 0, 0, lateThresholdMs);
        }

        var sorted = deltasMs.Order().ToArray();
        double Pct(double p) => sorted[Math.Clamp((int)Math.Ceiling(p * sorted.Length) - 1, 0, sorted.Length - 1)];
        return new FrameStats(
            sorted.Length,
            sorted.Average(),
            Pct(0.50),
            Pct(0.95),
            Pct(0.99),
            sorted[^1],
            sorted.Count(d => d > lateThresholdMs),
            lateThresholdMs);
    }

    public override string ToString() => string.Format(
        CultureInfo.InvariantCulture,
        "{0}프레임 · 평균 {1:F1}ms · p95 {2:F1}ms · p99 {3:F1}ms · 최대 {4:F1}ms · 늦은 프레임 {5:F1}% → {6}",
        Frames, AvgMs, P95Ms, P99Ms, MaxMs, LatePercent, PassesBudget ? "통과" : "미달");
}
