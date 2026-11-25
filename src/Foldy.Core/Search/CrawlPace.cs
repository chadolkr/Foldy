namespace Foldy.Core.Search;

/// <summary>
/// 드라이브를 모을 때의 빠르기: 얼마나 쉬고 몇 개를 같이 읽나 (드라이브 하나에 하나씩, 한 스레드에서만 쓴다).
/// <list type="bullet">
/// <item><b>가까운 드라이브</b>(USB·SD·디스크): 평소처럼 답하면 쉬지 않는다. 다른 프로그램에 비켜 주는 일은 Windows가 한다(<see cref="GiveWay"/>).
/// 답이 평소보다 세 배 넘게 늦어지면 그때부터 쉰다.</item>
/// <item><b>먼 드라이브</b>(네트워크·클라우드): 읽는 시간의 5분의 1쯤, 폴더마다 적어도 최소 시간을 쉰다. 답이 느릴수록 여러 폴더를 같이 읽어 기다리는 시간을 겹친다.
/// 같이 읽어서 실제로 빨라질 때만 늘리고, 빨라지지 않으면 줄인다 (요청을 하나씩만 받는 드라이브에 짐만 주지 않게).</item>
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
    private double _wallBase = -1;
    private readonly double[] _wallAt = Enumerable.Repeat(-1.0, SlowCrawler.MaxWidth + 1).ToArray();
    private int _steady;

    /// <summary>한 칸 더 같이 읽어 폴더 하나에 드는 시간이 이만큼(90%)보다 줄어야 늘린 보람이 있다고 본다.</summary>
    private const double Helped = 0.9;

    /// <summary>한 칸 늘리기 전에 지금 넓이로 하는 판단 수 (재 볼 시간).</summary>
    private const int Steady = 2;

    /// <summary>넓혀도 보람이 없던 넓이를 다시 재 보기까지의 판단 수.</summary>
    private const int Retry = 20;

    /// <summary>이만큼 폴더를 읽었거나 <see cref="JudgeTime"/>만큼 지나면 빠르기를 다시 고른다.</summary>
    private const int JudgeFolders = 48;

    private static readonly TimeSpan JudgeTime = TimeSpan.FromSeconds(1);

    private TimeSpan _winTime;
    private int _winFolders;
    private int _winCalls;
    private int _slowRuns;
    private TimeSpan _owed;
    private TimeSpan _asked;

    /// <summary>폴더 하나 목록을 묻는 데 평소 걸리는 시간(ms, 아직 모르면 0).</summary>
    public double AskMs => Math.Max(0, _baseline);

    /// <summary>같이 읽을 폴더 수.</summary>
    public int Width { get; private set; } = 1;

    private bool Far => !near || _baseline >= RemoteMs;

    /// <summary>폴더 한 묶음을 읽은 뒤 부른다. 지금 쉴 시간을 돌려준다 (0이면 바로 다음).</summary>
    /// <param name="batch">묶음을 읽는 데 걸린 시간.</param>
    /// <param name="folders">묶음의 폴더 수.</param>
    /// <param name="perAsk">목록을 새로 읽은 폴더 하나에 걸린 시간 (새로 읽은 게 없으면 0).</param>
    public TimeSpan After(TimeSpan batch, int folders, TimeSpan perAsk)
    {
        if (perAsk > TimeSpan.Zero && folders > 0)
        {
            var ms = perAsk.TotalMilliseconds;
            _baseline = _baseline < 0 ? ms : (0.9 * _baseline) + (0.1 * ms);

            // 폴더마다 걸리는 시간은 들쭉날쭉하다(큰 폴더, 서버 사정). 몇 개만 보고 "느려졌다"·"같이 읽어도 소용없다"고 정하면
            // 덜 읽고 더 쉬게 된다 (2026-11-25 모의 실험: 들쭉날쭉한 드라이브에서 시간의 절반 넘게 쉬고 평균 1.3개만 같이 읽음).
            // 그래서 폴더 여럿(또는 1초어치)을 모아 한 번에 판단한다
            _winTime += batch;
            _winFolders += folders;
            _winCalls++;
            if (_winFolders >= JudgeFolders || _winTime >= JudgeTime)
            {
                Judge();
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

    /// <summary>모은 폴더들로 빠르기를 고른다. 느려짐은 두 번 이어질 때만 믿는다.</summary>
    private void Judge()
    {
        // 폴더 하나에 실제로 든 시간 (같이 읽으면 요청 하나하나는 줄을 서서 늦어도 전체는 빨라진다)
        var wall = _winTime.TotalMilliseconds / _winFolders;
        var calls = _winCalls;
        (_winTime, _winFolders, _winCalls) = (TimeSpan.Zero, 0, 0);
        var w = Width;
        if (_wallBase >= 0 && wall > 3 * Math.Max(_wallBase, 1))
        {
            if (++_slowRuns >= 2)
            {
                _slowRuns = 0;
                _factor = Math.Min(MostFactor, Math.Max(SlowAgain, _factor * 1.5));
                Width = Math.Max(1, Width / 2);
            }

            return;
        }

        _slowRuns = 0;
        _wallBase = _wallBase < 0 ? wall : (0.7 * _wallBase) + (0.3 * wall);
        _wallAt[w] = _wallAt[w] < 0 ? wall : (0.5 * _wallAt[w]) + (0.5 * wall);
        _factor *= Math.Pow(0.9, calls);
        if (Far)
        {
            _factor = Math.Max(SlowAgain, _factor);
        }
        else if (_factor < 0.05)
        {
            _factor = 0;
        }

        var most = !Far ? 1 : _baseline >= RemoteMs ? SlowCrawler.MaxWidth : 2;
        var helped = w == 1 || _wallAt[w - 1] < 0 || _wallAt[w] < Helped * _wallAt[w - 1];
        if (w > most)
        {
            Width = most;
        }
        else if (!helped)
        {
            // 같이 읽어도 빨라지지 않는 드라이브(요청을 하나씩만 받는 드라이브): 더 늘려도 짐만 되니 한 칸 줄인다
            Width = w - 1;
        }
        else if (w < most && ++_steady >= Steady)
        {
            // 한 칸 더 넓혀 봤는데 보람이 없었으면 한동안은 그대로 둔다 (가끔 다시 재 본다: 드라이브 사정은 바뀐다)
            if (_wallAt[w + 1] < 0 || _wallAt[w + 1] < Helped * _wallAt[w] || _steady >= Retry)
            {
                _steady = 0;
                Width = w + 1;
            }
        }
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
