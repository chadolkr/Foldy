using System.Diagnostics;
using System.IO;
using Foldy.Core.Mcp;
using Serilog;

namespace Foldy.App.Services;

/// <summary>
/// [Claude 다시 시작] (사용자 2026-11-22): AI 연결 뒤 Claude Desktop이 설정을 다시 읽게 끄고 다시 켠다.
/// 창의 X로는 알림 영역에 남아 꺼지지 않으므로 프로그램을 끝낸다. 묻고 나서만 부른다 (Claude에서 하던 일이 멈춘다).
/// </summary>
internal static class ClaudeRestart
{
    public enum Result
    {
        Restarted,
        NotRunning,
        Failed,
    }

    /// <summary>실행 중인 Claude Desktop의 실행 파일. 없으면 null.</summary>
    public static string? RunningExe() => Running().Select(p => p.Path).FirstOrDefault();

    public static async Task<Result> RestartAsync()
    {
        var running = Running();
        if (running.Count == 0)
        {
            return Result.NotRunning;
        }

        var exe = running[0].Path;
        string? manifest = null;
        if (ClaudeDesktopApp.PackageRoot(exe) is { } root)
        {
            try
            {
                manifest = await File.ReadAllTextAsync(Path.Combine(root, "AppxManifest.xml"));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        var target = ClaudeDesktopApp.LaunchTarget(exe, manifest);
        try
        {
            foreach (var (p, _) in running)
            {
                try
                {
                    p.Kill();
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                }
            }

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            foreach (var (p, _) in running)
            {
                try
                {
                    await p.WaitForExitAsync(cts.Token);
                }
                catch (OperationCanceledException)
                {
                    Log.Warning("Claude가 10초 안에 끝나지 않음");
                    break;
                }
            }

            await Task.Delay(800);
            if (target.StartsWith("shell:", StringComparison.Ordinal))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", target) { UseShellExecute = false })?.Dispose();
            }
            else
            {
                Process.Start(new ProcessStartInfo(target) { UseShellExecute = true })?.Dispose();
            }

            return Result.Restarted;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            Log.Warning(ex, "Claude 다시 시작 실패");
            return Result.Failed;
        }
        finally
        {
            foreach (var (p, _) in running)
            {
                p.Dispose();
            }
        }
    }

    /// <summary>Claude Desktop 프로세스들 (Claude Code 명령줄 claude.exe는 뺀다).</summary>
    private static List<(Process Process, string Path)> Running()
    {
        var found = new List<(Process, string)>();
        foreach (var p in Process.GetProcessesByName("claude"))
        {
            string? path = null;
            try
            {
                path = p.MainModule?.FileName;
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
            }

            if (ClaudeDesktopApp.IsDesktopExe(path))
            {
                found.Add((p, path!));
            }
            else
            {
                p.Dispose();
            }
        }

        return found;
    }
}
