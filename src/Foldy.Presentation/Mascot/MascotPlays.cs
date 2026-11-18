namespace Foldy.Presentation.Mascot;

/// <summary>동작 라이브러리. 복사·이동 중에는 이 중에서 겹치지 않게 골라 이어 붙인다.</summary>
public enum PlayKind
{
    /// <summary>크로스오버 드리블.</summary>
    Crossover,
    /// <summary>비하인드 백 드리블.</summary>
    BehindBack,
    /// <summary>다리 사이 드리블.</summary>
    BetweenLegs,
    /// <summary>스핀 무브.</summary>
    SpinMove,
    /// <summary>패스하고 치고 나가 다시 받기.</summary>
    GiveAndGo,
    /// <summary>바운드 패스.</summary>
    BouncePass,
    /// <summary>앨리웁 로브 패스와 점프 캐치.</summary>
    AlleyOop,
    /// <summary>스크린 앤 롤.</summary>
    ScreenRoll,
    /// <summary>커트 인 (달려 들어가 패스 받기).</summary>
    CutToBasket,
    /// <summary>슛 페이크.</summary>
    FakeShot,
    /// <summary>방향 전환.</summary>
    ChangeDirection,
}

/// <summary>어떤 장면이 쓸 동작 묶음.</summary>
public enum PlaySet
{
    All,
    /// <summary>3점 라인 밖 준비 단계(100% 근처): 슛 없이 드리블·패스·페이크.</summary>
    Setup,
    /// <summary>패스 위주.</summary>
    Passing,
}

/// <summary>동작 한 토막. From/To는 0~1 정규화된 국소 위치(그룹 중심의 좌우 왕복 범위 안).</summary>
public readonly record struct PlaySegment(
    PlayKind Play,
    double From,
    double To,
    int Holder,
    int HolderEnd,
    double Start,
    double Duration,
    double DistStart);

/// <summary>시드로 결정되는 동작 순서. 같은 시드 = 같은 순서이고, 이웃한 동작(처음·끝 포함)은 항상 다르다.</summary>
public sealed class PlayPlan
{
    private PlayPlan(IReadOnlyList<PlaySegment> segments, double total, double totalDistance)
    {
        Segments = segments;
        TotalDuration = total;
        TotalDistance = totalDistance;
    }

    public IReadOnlyList<PlaySegment> Segments { get; }

    /// <summary>한 바퀴 시간(초). 끝나면 처음으로 되돌아가며 이음새도 자연스럽다.</summary>
    public double TotalDuration { get; }

    public double TotalDistance { get; }

    /// <summary>공이 상대에게 넘어가는 동작인가 (드리블 담당이 바뀐다).</summary>
    public static bool Transfers(PlayKind k) => k is PlayKind.BouncePass or PlayKind.AlleyOop or PlayKind.CutToBasket;

    public static IReadOnlyList<PlayKind> Library(PlaySet set) => set switch
    {
        PlaySet.Setup =>
        [
            PlayKind.Crossover, PlayKind.BehindBack, PlayKind.BetweenLegs, PlayKind.SpinMove,
            PlayKind.ChangeDirection, PlayKind.FakeShot, PlayKind.GiveAndGo, PlayKind.BouncePass,
        ],
        PlaySet.Passing =>
        [
            PlayKind.GiveAndGo, PlayKind.BouncePass, PlayKind.AlleyOop, PlayKind.CutToBasket, PlayKind.ScreenRoll,
        ],
        _ => Enum.GetValues<PlayKind>(),
    };

    public static PlayPlan Create(int seed, PlaySet set = PlaySet.All, int count = 20)
    {
        var rng = new Rng((ulong)(uint)seed ^ 0x9E3779B97F4A7C15UL);
        var lib = Library(set);
        var n = Math.Max(4, count) & ~1; // 짝수 → 처음과 끝의 위치 쪽이 어긋나지 않는다

        // 국소 위치: 낮은 쪽(0.02~0.32)과 높은 쪽(0.68~0.98)을 번갈아 오가며 범위 전체를 쓴다
        var pos = new double[n + 1];
        var low = rng.NextDouble() < 0.5;
        for (var i = 0; i < n; i++)
        {
            pos[i] = low ? 0.02 + (0.30 * rng.NextDouble()) : 0.68 + (0.30 * rng.NextDouble());
            low = !low;
        }

        pos[n] = pos[0];

        var plays = new PlayKind[n];
        var holders = new int[n + 1];
        holders[0] = (int)(rng.Next() & 1);
        for (var i = 0; i < n; i++)
        {
            IEnumerable<PlayKind> cand = lib.Where(k => i == 0 || k != plays[i - 1]);
            if (i == n - 1)
            {
                // 마지막은 처음 동작과 달라야 하고, 공의 주인이 처음 주인으로 돌아오도록 맞춘다
                var needTransfer = holders[i] != holders[0];
                var filtered = cand.Where(k => k != plays[0] && Transfers(k) == needTransfer).ToList();
                if (filtered.Count == 0)
                {
                    filtered = cand.Where(k => k != plays[0]).ToList();
                }

                cand = filtered;
            }

            var list = cand.ToList();
            plays[i] = list[(int)(rng.Next() % (ulong)list.Count)];
            holders[i + 1] = Transfers(plays[i]) ? 1 - holders[i] : holders[i];
        }

        var segs = new List<PlaySegment>(n);
        double t = 0;
        double dist = 0;
        for (var i = 0; i < n; i++)
        {
            var delta = Math.Abs(pos[i + 1] - pos[i]);
            var dur = 1.1 + (1.3 * delta);
            segs.Add(new PlaySegment(plays[i], pos[i], pos[i + 1], holders[i], holders[i + 1], t, dur, dist));
            t += dur;
            dist += delta;
        }

        return new PlayPlan(segs, t, dist);
    }

    /// <summary>시각 t(초)의 동작과 그 안의 진행 s(0~1). 한 바퀴를 넘으면 처음부터 반복한다.</summary>
    public (PlaySegment Segment, double S, int Index) At(double seconds)
    {
        var t = seconds - (Math.Floor(seconds / TotalDuration) * TotalDuration);
        for (var i = 0; i < Segments.Count; i++)
        {
            var s = Segments[i];
            if (t < s.Start + s.Duration || i == Segments.Count - 1)
            {
                return (s, Math.Clamp((t - s.Start) / s.Duration, 0, 1), i);
            }
        }

        return (Segments[0], 0, 0);
    }

    private struct Rng
    {
        private ulong _s;

        public Rng(ulong seed) => _s = seed;

        public ulong Next()
        {
            _s += 0x9E3779B97F4A7C15UL;
            var z = _s;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }

        public double NextDouble() => (Next() >> 11) / (double)(1UL << 53);
    }
}

/// <summary>
/// 코트 배치. 진행 막대 역할: 0%는 맨 왼쪽, 100%는 3점 라인 밖(골대에서 띠 폭의 약 1/3)이다.
/// 렌더러와 테스트가 같은 값을 쓴다.
/// </summary>
public static class MascotCourt
{
    public const double RimInset = 27;
    public const double TrackStart = 36;
    public const double MaxWorld = 640;

    public static double World(double width) => Math.Min(width, MaxWorld);

    public static double RimX(double world) => world - RimInset;

    /// <summary>골대에서 3점 슈터까지의 거리: 띠 폭의 1/3 (최소 90px).</summary>
    public static double ShotDistance(double world) => Math.Max(90, world / 3);

    /// <summary>100% 지점의 슛 위치(3점 라인 바깥).</summary>
    public static double ShotX(double world) => RimX(world) - ShotDistance(world);

    /// <summary>바닥에 그린 3점 라인의 가장 왼쪽 점. 슈터는 이보다 왼쪽에 선다.</summary>
    public static double ArcX(double world) => ShotX(world) + 10;

    /// <summary>사이드라인 x. 삭제 마무리에서는 이 선 너머(오른쪽)에 휴지통이 있다.</summary>
    public static double SideX(double world) => world - 70;

    public static double BinX(double world) => world - 26;

    /// <summary>삭제·영구 삭제의 던지는 자리(사이드라인 앞쪽).</summary>
    public static double ThrowX(double world) => world - 150;

    /// <summary>마무리 종류별 100% 슈팅/던지기 위치.</summary>
    public static double FinishX(double world, MascotFinish finish) =>
        finish is MascotFinish.BinToss or MascotFinish.Punt ? ThrowX(world) : ShotX(world);

    public static double TrackEnd(double world, MascotFinish finish = MascotFinish.ThreePointer) => FinishX(world, finish) - 22;

    /// <summary>플레이가 넘어가면 안 되는 오른쪽 한계 (3점 라인 / 사이드라인 바깥).</summary>
    public static double MaxPlayX(double world, MascotFinish finish) =>
        finish is MascotFinish.BinToss or MascotFinish.Punt ? SideX(world) - 14 : ArcX(world) - 12;

    /// <summary>진행률 → 플레이 중심 x. 진행률을 모르면(-1) 중간에서 움직인다.</summary>
    public static double PlayCenter(double progress, double world, MascotFinish finish = MascotFinish.ThreePointer)
    {
        var a = TrackStart;
        var b = TrackEnd(world, finish);
        return progress < 0 ? (a + b) / 2 : a + ((b - a) * Math.Clamp(progress, 0, 1));
    }

    /// <summary>중심 주위 좌우 왕복 반폭. 진행률을 모르면 더 넓게 돈다.</summary>
    public static double Excursion(double progress, double world) =>
        progress < 0 ? Math.Clamp(world * 0.18, 40, 110) : Math.Clamp(world * 0.09, 28, 60);
}
