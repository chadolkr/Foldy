namespace Foldy.Core.Search;

/// <summary>
/// 드라이브를 모을 때의 빠르기: 얼마나 쉬고 몇 개를 같이 읽나 (드라이브 하나에 하나씩, 한 스레드에서만 쓴다).
/// <list type="bullet">
/// <item><b>가까운 드라이브</b>(USB·SD·디스크): 평소처럼 답하면 쉬지 않는다. 다른 프로그램에 비켜 주는 일은 Windows가 한다(<see cref="GiveWay"/>).
/// 답이 평소보다 세 배 넘게 늦어지면 그때부터 쉰다.</item>
/// <item><b>먼 드라이브</b>(네트워크·클라우드): 읽는 시간의 5분의 1쯤, 폴더마다 적어도 최소 시간을 쉰다. 답이 느릴수록 여러 폴더를 같이 읽어 기다리는 시간을 겹친다.</item>
/// </list>
/// 2026-11-24 SD 카드 실측(폴더 22,330개): 폴더마다 10ms를 쉬면 Windows 타이머 때문에 실제로는 16ms씩, 353초 중 거의 전부가 쉬는 시간이었다.
/// 그래서 쉴 시간은 모아 두었다가 한 번에 쉬고, 더 잔 만큼은 다음에서 뺀다.
/// </summary>
internal sealed class CrawlPace(bool near, TimeSpan minPause)
{
    /// <summary>한 번에 쉬는 시간의 최소: 이보다 짧게 부탁해도 Windows는 이쯤 쉰다.</summary>
    public static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(15);

    /// <summary>폴더 하나 읽는 데 평소 이보다 오래 걸리면 멀리 있는 드라이브로 본다 (Windows가 가까운 드라이브라고 해도).</summary>
    private const double RemoteMs = 20;

    private const double SlowAgain = 0.2;
    private const double MostFactor = 3;

    private static readonly TimeSpan MostPaidBack = TimeSpan.FromSeconds(1);

    private double _factor = near ? 0 : 0.5;
    private double _baseline = -1;
    private TimeSpan _owed;
    private TimeSpan _asked;

    /// <summary>같이 읽을 폴더 수.</summary>
    public int Width { get; private set; } = 1;

    private bool Far => !near || _baseline >= RemoteMs;

    /// <summary>폴더 한 묶음을 읽은 뒤 부른다. 지금 쉴 시간을 돌려준다 (0이면 바로 다음).</summary>
    /// <param name="batch">묶음을 읽는 데 걸린 시간.</param>
    /// <param name="folders">묶음의 폴더 수.</param>
    /// <param name="perAsk">목록을 새로 읽은 폴더 하나에 걸린 시간 (새로 읽은 게 없으면 0).</param>
    public TimeSpan After(TimeSpan batch, int folders, TimeSpan perAsk)
    {
        if (perAsk > TimeSpan.Zero)
        {
            var ms = perAsk.TotalMilliseconds;
            var slow = _baseline >= 0 && ms > 3 * Math.Max(_baseline, 1);
            _baseline = _baseline < 0 ? ms : (0.9 * _baseline) + (0.1 * ms);
            if (slow)
            {
                _factor = Math.Min(MostFactor, Math.Max(SlowAgain, _factor * 1.5));
                Width = Math.Max(1, Width / 2);
            }
            else
            {
                _factor *= 0.9;
                if (Far)
                {
                    _factor = Math.Max(SlowAgain, _factor);
                }
                else if (_factor < 0.05)
                {
                    _factor = 0;
                }

                Width = Math.Min(!Far ? 1 : _baseline >= RemoteMs ? SlowCrawler.MaxWidth : 2, Width + 1);
            }
        }

        var least = Far ? minPause * folders : TimeSpan.Zero;
        var want = batch * _factor;
        _owed += want > least ? want : least;
        if (_owed < Tick)
        {
            return TimeSpan.Zero;
        }

        _asked = _owed;
        _owed = TimeSpan.Zero;
        return _asked;
    }

    /// <summary>실제로 쉰 시간을 알린다: 부탁한 것보다 더 쉬었으면 다음에 덜 쉰다.</summary>
    public void Slept(TimeSpan actual)
    {
        var over = actual - _asked;
        if (over > TimeSpan.Zero)
        {
            _owed -= over < MostPaidBack ? over : MostPaidBack;
        }
    }
}
