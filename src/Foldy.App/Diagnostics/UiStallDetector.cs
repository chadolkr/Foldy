using System.Diagnostics;
using System.Windows.Threading;
using Serilog;

namespace Foldy.App.Diagnostics;

/// <summary>
/// UI 멈춤 감지 (설계서 8.1). 별도 스레드가 100ms마다 Dispatcher에 핑을 보내고,
/// 임계값보다 오래 응답이 없으면 경고 로그를 남긴다. (스택 캡처는 M1에서 ETW와 함께)
/// </summary>
public sealed class UiStallDetector : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly TimeSpan _threshold;
    private readonly Thread _thread;
    private readonly CancellationTokenSource _cts = new();
    private long _lastPong = Stopwatch.GetTimestamp();

    public UiStallDetector(Dispatcher dispatcher, TimeSpan threshold)
    {
        _dispatcher = dispatcher;
        _threshold = threshold;
        _thread = new Thread(Loop) { IsBackground = true, Name = "UiStallDetector", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    public int StallCount { get; private set; }

    private void Loop()
    {
        var reported = false;
        var lastLoop = Stopwatch.GetTimestamp();
        while (!_cts.IsCancellationRequested)
        {
            _dispatcher.BeginInvoke(DispatcherPriority.Send, () => Interlocked.Exchange(ref _lastPong, Stopwatch.GetTimestamp()));
            if (_cts.Token.WaitHandle.WaitOne(100))
            {
                return;
            }

            // 이 감시 스레드도 0.1초 기다렸는데 몇 초가 지났다면 PC가 잠자기·최대 절전에서 깨어난 것: UI 멈춤이 아니다
            // (깨어난 뒤 "1487865ms 응답 없음" 같은 잘못된 경고가 남던 문제)
            var now = Stopwatch.GetTimestamp();
            if (Stopwatch.GetElapsedTime(lastLoop, now) > TimeSpan.FromSeconds(5))
            {
                Interlocked.Exchange(ref _lastPong, now);
                lastLoop = now;
                reported = false;
                continue;
            }

            lastLoop = now;

            var since = Stopwatch.GetElapsedTime(Interlocked.Read(ref _lastPong));
            if (since > _threshold + TimeSpan.FromMilliseconds(100))
            {
                if (!reported)
                {
                    StallCount++;
                    Log.Warning("UI 스레드가 {Ms}ms 이상 응답하지 않음", (int)since.TotalMilliseconds);
                    reported = true;
                }
            }
            else
            {
                reported = false;
            }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _thread.Join(500);
        _cts.Dispose();
    }
}
