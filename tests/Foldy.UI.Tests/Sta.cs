using System.Runtime.ExceptionServices;
using System.Windows.Threading;

namespace Foldy.UI.Tests;

/// <summary>WPF 객체는 STA 스레드 + Dispatcher가 필요하다. 테스트 본문을 새 STA 스레드에서 돌린다.</summary>
internal static class Sta
{
    public static void Run(Action body) => Run(() =>
    {
        body();
        return Task.CompletedTask;
    });

    public static void Run(Func<Task> body)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            var frame = new DispatcherFrame();
            dispatcher.InvokeAsync(async () =>
            {
                try
                {
                    await body();
                }
                catch (Exception e)
                {
                    error = e;
                }
                finally
                {
                    frame.Continue = false;
                }
            });
            Dispatcher.PushFrame(frame);
            dispatcher.InvokeShutdown();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(60)))
        {
            throw new TimeoutException("STA 테스트가 60초 안에 끝나지 않았습니다.");
        }

        if (error is not null)
        {
            ExceptionDispatchInfo.Throw(error);
        }
    }
}
