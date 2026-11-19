using System.Runtime.ExceptionServices;
using System.Windows.Threading;

namespace Foldy.GitView.Tests;

/// <summary>WPF 시험용 STA 스레드 하나를 모든 시험이 공유한다 (Application 은 프로세스에 하나뿐이고 만든 스레드에 묶인다).</summary>
internal static class StaHost
{
    private static readonly Lazy<Dispatcher> Host = new(Start);

    private static Dispatcher Start()
    {
        Dispatcher? d = null;
        using var ready = new ManualResetEventSlim();
        var t = new Thread(() =>
        {
            d = Dispatcher.CurrentDispatcher;
            ready.Set();
            Dispatcher.Run();
        })
        { IsBackground = true, Name = "GitView STA" };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        ready.Wait();
        return d!;
    }

    public static void Run(Func<Task> body)
    {
        Exception? error = null;
        var task = Host.Value.InvokeAsync(async () =>
        {
            try { await body(); }
            catch (Exception e) { error = e; }
        }).Task.Unwrap();
        if (!task.Wait(TimeSpan.FromSeconds(120))) throw new TimeoutException("STA 테스트 시간 초과");
        if (error is not null) ExceptionDispatchInfo.Throw(error);
    }
}
