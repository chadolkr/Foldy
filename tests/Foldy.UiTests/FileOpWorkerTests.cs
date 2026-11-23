using System.Diagnostics;
using System.Text;
using Foldy.Core.FileSystem;

namespace Foldy.UiTests;

/// <summary>
/// 휴지통·삭제 도우미 (Foldy.exe --file-op, 사용자 제보 2026-11-23). 창 없이 뜨고, 결과 한 줄을 돌려주고 끝나야 한다.
/// Windows 파일 작업이 도우미 안에서 넘어져도 Foldy 창은 남는다.
/// </summary>
[Collection("ui")]
public sealed class FileOpWorkerTests
{
    private static (int Code, List<string> Lines) Run(string? request)
    {
        Assert.SkipWhen(FoldyApp.Exe is null, "Foldy.exe가 없습니다 (dotnet build Foldy.slnx -c Release 먼저)");
        var data = Path.Combine(Path.GetTempPath(), "foldy-worker-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(data);
        var psi = new ProcessStartInfo(FoldyApp.Exe!, FileOpWorkerProtocol.Argument)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false),
        };
        psi.Environment["FOLDY_DATA_DIR"] = data;
        using var p = Process.Start(psi)!;
        try
        {
            if (request is not null)
            {
                p.StandardInput.WriteLine(request);
            }

            var lines = new List<string>();
            var reader = Task.Run(() =>
            {
                while (p.StandardOutput.ReadLine() is { } l)
                {
                    lines.Add(l);
                }
            });
            Assert.True(p.WaitForExit(60_000), "도우미가 60초 안에 끝나지 않음");
            reader.Wait(5_000);
            return (p.ExitCode, lines);
        }
        finally
        {
            if (!p.HasExited)
            {
                p.Kill();
            }

            try
            {
                Directory.Delete(data, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private static FileOpWorkerResult? LastResult(List<string> lines)
    {
        FileOpWorkerResult? result = null;
        foreach (var l in lines)
        {
            FileOpWorkerProtocol.ReadLine(l, out _, out var r);
            result = r ?? result;
        }

        return result;
    }

    [Fact]
    public void Bad_request_gets_a_failure_line_and_no_window()
    {
        var (code, lines) = Run("not a request");
        Assert.Equal(2, code);
        var r = LastResult(lines);
        Assert.NotNull(r);
        Assert.False(r.Succeeded);
    }

    [Fact]
    public void Recycling_a_missing_folder_fails_softly()
    {
        var missing = Path.Combine(Path.GetTempPath(), "foldy-no-such-" + Guid.NewGuid().ToString("N"), "node_modules");
        var (code, lines) = Run(FileOpWorkerProtocol.WriteRequest(new FileOpWorkerRequest("Recycle", [missing], 0)));
        Assert.Equal(1, code);
        var r = LastResult(lines);
        Assert.NotNull(r);
        Assert.False(r.Succeeded);
        Assert.False(string.IsNullOrEmpty(r.Error));
    }
}
