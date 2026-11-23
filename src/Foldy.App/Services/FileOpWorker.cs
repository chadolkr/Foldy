using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Foldy.Core.FileSystem;
using Foldy.Platform.Shell;
using Serilog;

namespace Foldy.App.Services;

/// <summary>
/// 휴지통·삭제를 도우미 프로세스(Foldy.exe --file-op)에서 한다 (1.7.1, 사용자 제보 2026-11-23).
/// 큰 node_modules 여러 개를 한꺼번에 휴지통으로 보내다 Windows 파일 작업 안의 남의 코드(탐색기 확장·보안 프로그램)가
/// "Abnormal program termination"으로 프로세스를 끝내 Foldy 창이 같이 꺼졌다. 이제는 도우미만 끝나고
/// Foldy는 작업 패널에 쉬운 말로 알린다. 약속은 <see cref="FileOpWorkerProtocol"/>.
/// </summary>
public static class FileOpWorker
{
    // 휴지통·삭제는 한 번에 하나씩: 같은 디스크에서 겹치면 느리기만 하고, 남의 코드가 겹친 작업에서 넘어질 일도 줄인다
    private static readonly SemaphoreSlim Gate = new(1, 1);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(int processId);

    /// <summary>Foldy 쪽: 도우미를 띄워 작업을 맡기고 결과를 기다린다. 도우미를 못 띄우면 이 프로세스에서 한다.</summary>
    public static async Task<FileOpOutcome> RunAsync(FileOpRequest req, IntPtr owner, FileOpProgressCallback? progress, CancellationToken ct)
    {
        try
        {
            await Gate.WaitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            return new FileOpOutcome(false, true, null, null);
        }

        try
        {
            return await RunIsolatedAsync(req, owner, progress, ct);
        }
        finally
        {
            Gate.Release();
        }
    }

    private static async Task<FileOpOutcome> RunIsolatedAsync(FileOpRequest req, IntPtr owner, FileOpProgressCallback? progress, CancellationToken ct)
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe) || !string.Equals(Path.GetFileNameWithoutExtension(exe), "Foldy", StringComparison.OrdinalIgnoreCase))
        {
            return await FileOperations.RunAsync(req, owner, progress, ct); // 시험 러너 등: 도우미로 쓸 Foldy.exe가 아니다
        }

        var psi = new ProcessStartInfo(exe, FileOpWorkerProtocol.Argument)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false),
        };

        Process? p;
        try
        {
            p = Process.Start(psi);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            Log.Warning(ex, "파일 작업 도우미를 띄우지 못해 Foldy 안에서 합니다");
            p = null;
        }

        if (p is null)
        {
            return await FileOperations.RunAsync(req, owner, progress, ct);
        }

        using (p)
        {
            Log.Information("파일 작업 도우미 시작 (PID {Pid}): {Kind} {Count}개, 첫 항목 {First}", p.Id, req.Kind, req.Sources.Count, req.Sources[0]);
            _ = AllowSetForegroundWindow(p.Id); // 확인·UAC 창이 Foldy 뒤로 숨지 않게
            var cancelRequested = false;
            try
            {
                await p.StandardInput.WriteLineAsync(FileOpWorkerProtocol.WriteRequest(new FileOpWorkerRequest(req.Kind.ToString(), req.Sources, owner.ToInt64())));
                await p.StandardInput.FlushAsync(CancellationToken.None);
            }
            catch (IOException ex)
            {
                Log.Warning(ex, "파일 작업 도우미에 요청을 보내지 못했습니다");
            }

            using var reg = ct.Register(() =>
            {
                cancelRequested = true;
                try
                {
                    p.StandardInput.WriteLine(FileOpWorkerProtocol.CancelLine);
                    p.StandardInput.Flush();
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
                {
                }
            });

            FileOpWorkerResult? result = null;
            try
            {
                while (await p.StandardOutput.ReadLineAsync(CancellationToken.None) is { } line)
                {
                    FileOpWorkerProtocol.ReadLine(line, out var pr, out var r);
                    if (pr is not null)
                    {
                        progress?.Invoke(pr.Done, pr.Total, pr.Current);
                    }
                    else if (r is not null)
                    {
                        result = r;
                    }
                }
            }
            catch (IOException ex)
            {
                Log.Warning(ex, "파일 작업 도우미의 진행을 읽지 못했습니다");
            }

            await p.WaitForExitAsync(CancellationToken.None);
            if (result is null)
            {
                Log.Error("파일 작업 도우미가 결과 없이 끝났습니다 (종료 코드 0x{Code:X8}, 취소 {Cancel}): {Kind} {First}", p.ExitCode, cancelRequested, req.Kind, req.Sources[0]);
            }
            else
            {
                Log.Information("파일 작업 도우미 끝: 성공 {Ok}, 취소 {Aborted}, 오류 {Error}", result.Succeeded, result.Aborted, result.Error);
            }

            var final = FileOpWorkerProtocol.Finish(result, cancelRequested, Foldy.Core.Localization.Loc.T("ops.workerCrashed"));
            return new FileOpOutcome(final.Succeeded, final.Aborted, final.Error, null, final.Code);
        }
    }

    /// <summary>
    /// 도우미 쪽 (Foldy.exe --file-op): 표준 입력 첫 줄의 작업을 하고, 진행과 결과를 표준 출력으로 보낸다.
    /// 창·로그인 확인·단일 실행 검사 없이 돈다. 돌려주는 값은 종료 코드.
    /// </summary>
    public static int Serve()
    {
        using var input = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
        using var output = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
        var req = FileOpWorkerProtocol.ReadRequest(input.ReadLine());
        if (req is null || !Enum.TryParse<FileOpKind>(req.Kind, out var kind) || kind is not (FileOpKind.Recycle or FileOpKind.Delete))
        {
            output.WriteLine(FileOpWorkerProtocol.WriteResult(new FileOpWorkerResult(false, false, "bad request")));
            return 2;
        }

        using var cts = new CancellationTokenSource();
        var listener = new Thread(() =>
        {
            try
            {
                while (input.ReadLine() is { } line)
                {
                    if (line == FileOpWorkerProtocol.CancelLine)
                    {
                        cts.Cancel();
                    }
                }

                cts.Cancel(); // Foldy가 꺼져 입력이 끊겼으면 그만한다
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
            }
        })
        {
            IsBackground = true,
            Name = "FileOp worker input",
        };
        listener.Start();

        var gate = new object();
        var throttle = new ProgressThrottle(TimeSpan.FromMilliseconds(100));
        void Send(string line)
        {
            lock (gate)
            {
                try
                {
                    output.WriteLine(line);
                }
                catch (IOException)
                {
                }
            }
        }

        FileOpProgressCallback progress = (done, total, current) =>
        {
            if (throttle.ShouldSend(force: total > 0 && done >= total))
            {
                Send(FileOpWorkerProtocol.WriteProgress(new FileOpWorkerProgress(done, total, current)));
            }
        };

        var outcome = FileOperations.RunAsync(new FileOpRequest(kind, req.Sources), new IntPtr(req.Owner), progress, cts.Token).GetAwaiter().GetResult();
        Send(FileOpWorkerProtocol.WriteResult(new FileOpWorkerResult(outcome.Succeeded, outcome.Aborted, outcome.Error, outcome.ErrorCode)));
        return outcome.Succeeded ? 0 : 1;
    }
}
