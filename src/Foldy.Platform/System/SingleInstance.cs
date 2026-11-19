using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;

namespace Foldy.Platform.SystemInfo;

/// <summary>
/// 단일 인스턴스: 같은 사용자·같은 로그온 세션에서 Foldy는 하나만 뜬다.
/// 처음 뜬 Foldy가 이름 있는 이벤트 개체를 만들고, 파이프로 "앞으로 와 달라 + 인자" 요청을 받는다.
/// 나중에 뜬 Foldy는 요청을 보내고 바로 끝난다. 앞의 Foldy가 응답하지 않으면(멈춤 등) 그냥 새로 뜬다 — 창이 하나도 없는 것보다 낫다.
/// 프로토콜: 서버가 자기 PID(int32)를 쓰고, 클라이언트는 AllowSetForegroundWindow(PID) 뒤 인자 개수(int32)와 UTF-8 문자열들을 쓴다.
/// </summary>
public sealed partial class SingleInstance : IDisposable
{
    private const int MaxArgs = 64;
    private const int MaxArgBytes = 32 * 1024;

    private readonly EventWaitHandle _marker;
    private readonly string _pipeName;
    private readonly CancellationTokenSource _cts = new();
    private bool _owns;
    private Task? _server;

    private SingleInstance(EventWaitHandle marker, bool owns, string pipeName)
    {
        _marker = marker;
        _owns = owns;
        _pipeName = pipeName;
    }

    /// <summary>이 프로세스가 첫 Foldy인가.</summary>
    public bool IsFirst => _owns;

    public string PipeName => _pipeName;

    /// <summary>
    /// 이름 있는 커널 개체를 처음 만든 프로세스가 첫 Foldy다. key는 앱 이름 (시험에서는 고유 이름).
    /// 세션 ID를 붙여 원격 데스크톱의 다른 세션과 섞이지 않게 한다.
    /// 뮤텍스 대신 이벤트를 쓴다: 스레드에 묶이지 않고(같은 스레드가 다시 잡아도 "처음"이 아님), 첫 Foldy가 죽으면 핸들이 닫혀 개체가 사라진다.
    /// </summary>
    public static SingleInstance Acquire(string key = "Foldy")
    {
        var session = System.Diagnostics.Process.GetCurrentProcess().SessionId;
        var user = Environment.UserName.GetHashCode(StringComparison.OrdinalIgnoreCase).ToString("x8", System.Globalization.CultureInfo.InvariantCulture);
        var name = $"{key}.{session}.{user}";
        var marker = new EventWaitHandle(false, EventResetMode.ManualReset, @"Local\" + name + ".instance", out var createdNew);
        return new SingleInstance(marker, createdNew, name + ".activate");
    }

    /// <summary>첫 Foldy: 다른 Foldy의 요청을 받기 시작한다. onActivate는 스레드 풀에서 불린다 (UI로 넘기는 건 호출자 몫).</summary>
    public void StartServer(Action<string[]> onActivate)
    {
        ArgumentNullException.ThrowIfNull(onActivate);
        if (!_owns || _server is not null)
        {
            return;
        }

        var ct = _cts.Token;
        _server = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    using var pipe = new NamedPipeServerStream(_pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeout.CancelAfter(TimeSpan.FromSeconds(5));
                    await WriteInt(pipe, Environment.ProcessId, timeout.Token).ConfigureAwait(false);
                    var count = await ReadInt(pipe, timeout.Token).ConfigureAwait(false);
                    if (count is < 0 or > MaxArgs)
                    {
                        continue;
                    }

                    var args = new string[count];
                    for (var i = 0; i < count; i++)
                    {
                        var len = await ReadInt(pipe, timeout.Token).ConfigureAwait(false);
                        if (len is < 0 or > MaxArgBytes)
                        {
                            throw new InvalidDataException("인자가 너무 깁니다");
                        }

                        var buf = new byte[len];
                        await pipe.ReadExactlyAsync(buf, timeout.Token).ConfigureAwait(false);
                        args[i] = Encoding.UTF8.GetString(buf);
                    }

                    pipe.WriteByte(1); // 받았음
                    onActivate(args);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex) when (ex is IOException or OperationCanceledException or InvalidDataException or UnauthorizedAccessException)
                {
                    // 상대가 중간에 끊었거나 잘못된 요청: 다음 요청을 기다린다.
                    await Task.Delay(50, CancellationToken.None).ConfigureAwait(false);
                }
            }
        }, CancellationToken.None);
    }

    /// <summary>
    /// 나중 Foldy: 첫 Foldy에 인자를 넘기고 앞으로 오게 한다. 성공하면 true (이 프로세스는 끝내면 된다).
    /// 첫 Foldy가 timeout 안에 받지 않으면 false.
    /// </summary>
    public bool TrySendToFirst(IReadOnlyList<string> args, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(args);
        try
        {
            using var cts = new CancellationTokenSource(timeout);
            using var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.CurrentUserOnly);
            pipe.ConnectAsync(cts.Token).GetAwaiter().GetResult();
            var pid = ReadInt(pipe, cts.Token).GetAwaiter().GetResult();
            if (pid > 0 && OperatingSystem.IsWindows())
            {
                _ = AllowSetForegroundWindow(pid); // 지금 앞에 있는 건 이 프로세스라, 첫 Foldy에 앞으로 나올 권한을 넘긴다
            }

            var n = Math.Min(args.Count, MaxArgs);
            WriteInt(pipe, n, cts.Token).GetAwaiter().GetResult();
            for (var i = 0; i < n; i++)
            {
                var bytes = Encoding.UTF8.GetBytes(args[i]);
                if (bytes.Length > MaxArgBytes)
                {
                    bytes = [];
                }

                WriteInt(pipe, bytes.Length, cts.Token).GetAwaiter().GetResult();
                pipe.Write(bytes);
            }

            pipe.Flush();
            var ack = new byte[1];
            pipe.ReadExactlyAsync(ack, cts.Token).AsTask().GetAwaiter().GetResult();
            return ack[0] == 1;
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or TimeoutException or UnauthorizedAccessException or EndOfStreamException)
        {
            return false;
        }
    }

    private static async Task WriteInt(Stream s, int value, CancellationToken ct)
    {
        var b = BitConverter.GetBytes(value);
        await s.WriteAsync(b, ct).ConfigureAwait(false);
        await s.FlushAsync(ct).ConfigureAwait(false);
    }

    private static async Task<int> ReadInt(Stream s, CancellationToken ct)
    {
        var b = new byte[4];
        await s.ReadExactlyAsync(b, ct).ConfigureAwait(false);
        return BitConverter.ToInt32(b);
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AllowSetForegroundWindow(int processId);

    public void Dispose()
    {
        _cts.Cancel();
        try
        {
            _server?.Wait(500);
        }
        catch (AggregateException)
        {
        }

        _owns = false;
        _marker.Dispose();
        _cts.Dispose();
    }
}
