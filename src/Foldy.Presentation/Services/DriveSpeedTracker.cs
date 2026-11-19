using Foldy.Core.FileSystem;

namespace Foldy.Presentation.Services;

/// <summary>
/// 드라이브(문자 또는 UNC 공유)별 폴더 열기 속도를 기억해 느린 드라이브를 자동으로 알아낸다 (저장 안 함, 설정 없음).
/// 첫 묶음이 나오기까지 걸린 시간의 지수 이동 평균(EMA)이 250ms를 넘거나(표본 2개 이상), 한 번이라도 2초를 넘으면 느린 드라이브.
/// 스레드 안전.
/// </summary>
public sealed class DriveSpeedTracker
{
    public static readonly TimeSpan SlowEma = TimeSpan.FromMilliseconds(250);
    public static readonly TimeSpan SlowSingle = TimeSpan.FromSeconds(2);
    private const double Alpha = 0.5;

    /// <summary>앱 전체가 공유하는 인스턴스 (미리보기·썸네일·아이콘 판정이 쓴다).</summary>
    public static DriveSpeedTracker Shared { get; } = new();

    private readonly object _gate = new();
    private readonly Dictionary<string, State> _drives = new(StringComparer.OrdinalIgnoreCase);

    private sealed class State
    {
        public double EmaMs;
        public int Samples;
        public bool Slow;
    }

    /// <summary>드라이브가 처음 느려졌을 때 (드라이브 뿌리 문자열).</summary>
    public event Action<string>? BecameSlow;

    /// <summary>경로가 속한 드라이브 뿌리: "K:" 또는 "\\서버\공유". 추적하지 않는 경로(SFTP 등)는 null.</summary>
    public static string? RootOf(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
        {
            path = @"\\" + path[8..];
        }
        else if (path.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            path = path[4..];
        }

        if (path.Length >= 2 && path[1] == ':' && char.IsAsciiLetter(path[0]))
        {
            return char.ToUpperInvariant(path[0]) + ":";
        }

        if (path.StartsWith(@"\\", StringComparison.Ordinal))
        {
            var parts = path[2..].Split('\\', 3, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2)
            {
                return @"\\" + parts[0] + @"\" + parts[1];
            }
        }

        return null;
    }

    public bool IsSlow(string? path)
    {
        if (RootOf(path) is not { } root)
        {
            return false;
        }

        lock (_gate)
        {
            return _drives.TryGetValue(root, out var s) && s.Slow;
        }
    }

    public DriveSpeedClass Classify(string? path) => IsSlow(path) ? DriveSpeedClass.Slow : DriveSpeedClass.Fast;

    /// <summary>폴더를 열 때 첫 묶음까지 걸린 시간을 기록한다. 이 기록으로 처음 느려졌으면 true.</summary>
    /// <summary>느림 판정 대상 드라이브인가 (기본: 모두). 앱은 네트워크·리다이렉터 드라이브만 대상으로 한다
    /// (로컬 디스크는 시작 직후 첫 목록이 느려도 "느린 드라이브"로 표시하지 않는다).</summary>
    public Func<string, bool>? Eligible { get; set; }

    public bool Record(string? path, TimeSpan firstBatch)
    {
        if (RootOf(path) is not { } root || Eligible?.Invoke(root) == false)
        {
            return false;
        }

        bool became;
        lock (_gate)
        {
            if (!_drives.TryGetValue(root, out var s))
            {
                _drives[root] = s = new State();
            }

            var ms = firstBatch.TotalMilliseconds;
            s.EmaMs = s.Samples == 0 ? ms : Alpha * ms + (1 - Alpha) * s.EmaMs;
            s.Samples++;
            var was = s.Slow;
            if (ms > SlowSingle.TotalMilliseconds)
            {
                s.Slow = true;
            }
            else if (s.Samples >= 2)
            {
                s.Slow = s.EmaMs > SlowEma.TotalMilliseconds;
            }

            became = !was && s.Slow;
        }

        if (became)
        {
            BecameSlow?.Invoke(root);
        }

        return became;
    }
}
