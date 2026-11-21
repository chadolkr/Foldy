using System.Diagnostics;
using System.IO;
using System.Windows;
using Foldy.Core.Cleanup;
using Foldy.Presentation.Files;
using Foldy.Presentation.Shell;

namespace Foldy.App.Services;

/// <summary>
/// 개발자용 정리 두 가지 (2026-11-21 사용자 승인, 조사 문서의 추천 1·3):
/// 지워도 되는 폴더 찾기(node_modules·bin/obj·__history·.git 등)와 개발 도구 캐시 정리(공식 명령만).
/// 둘 다 대화형 창(GuideWindow)으로, 저절로 지우는 것은 없다.
/// </summary>
public static class DevCleanupFlows
{
    private static (string, string) C(string id, string label) => (id, label);

    private static GuideWindow Open(Window owner, string title)
    {
        var w = new GuideWindow(title) { Owner = owner };
        w.Show();
        return w;
    }

    /// <summary>지워도 되는 폴더 찾기: 고른 폴더 안에서만, 크기 순. 고른 것은 휴지통으로(되살릴 수 있음).</summary>
    public static async Task RebuildableAsync(Window owner, string root, ShellViewModel shell, IEnumerable<string>? extraNames, Func<string, long?>? indexSize)
    {
        var w = Open(owner, "지워도 되는 폴더 찾기");
        w.Say($"{root} 안에서 다시 만들 수 있는 폴더를 찾을게요 (node_modules, bin·obj, __pycache__, __history 같은 것). 프로젝트 파일이 옆에 있을 때만 골라요. 저절로 지우는 건 없어요.");
        w.Busy();
        IReadOnlyList<RebuildableFolder> found;
        try
        {
            var kinds = RebuildableFolders.With(extraNames);
            found = await Task.Run(() => RebuildableFolders.Scan(root, kinds, DateTime.UtcNow, indexSize));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await w.AskAsync("폴더를 읽지 못했어요: " + ex.Message, C("close", "닫기"));
            w.Close();
            return;
        }

        if (found.Count == 0)
        {
            await w.AskAsync("지워도 되는 폴더가 없어요. 깔끔해요! 🙂", C("close", "닫기"));
            w.Close();
            return;
        }

        var total = found.Where(f => f.Size > 0).Sum(f => f.Size);
        w.Say($"{found.Count:N0}개({FileItem.FormatBytes(total)})를 찾았어요. 큰 것부터 보여 드릴게요. 최근 30일 안에 쓴 것과 .git은 골라 두지 않았어요.");
        string Label(RebuildableFolder f) =>
            $"{FileItem.FormatBytes(Math.Max(0, f.Size))}  ·  {Rel(root, f.Path)}  ·  {f.Kind.Why}"
            + (f.LastWriteUtc > DateTime.MinValue ? $"  ·  {f.LastWriteUtc.ToLocalTime():yyyy-MM-dd}" : "");
        var (choice, picked) = await w.PickAsync("휴지통으로 보낼 폴더를 골라 주세요 (휴지통에서 되살릴 수 있어요).",
            found.Select(f => (f, Label(f), f.Selected)), f => f.Warning,
            C("recycle", "휴지통으로"), C("skip", "그대로 두기"));
        if (choice is "skip" or GuideWindow.ClosedChoice || picked.Count == 0)
        {
            if (!w.IsClosed)
            {
                await w.AskAsync("네, 그대로 둘게요.", C("close", "닫기"));
                w.Close();
            }

            return;
        }

        // .git을 골랐으면 한 번 더 (되돌릴 수 없는 기록)
        if (picked.Any(p => !p.Kind.CanRebuild))
        {
            var git = picked.Count(p => !p.Kind.CanRebuild);
            var sure = await w.AskAsync($".git 폴더 {git}개가 들어 있어요. Git 기록(커밋·브랜치)이 없어져요. 원본 저장소가 따로 있는 백업 사본일 때만 지우세요.",
                C("yes", ".git도 휴지통으로"), C("nogit", ".git은 빼고"), C("cancel", "그만두기"));
            if (sure is "cancel" or GuideWindow.ClosedChoice)
            {
                w.Close();
                return;
            }

            if (sure == "nogit")
            {
                picked = [.. picked.Where(p => p.Kind.CanRebuild)];
            }
        }

        var size = picked.Where(p => p.Size > 0).Sum(p => p.Size);
        var ok = await shell.RecycleAsync([.. picked.Select(p => p.Path)]);
        await w.AskAsync(ok
            ? $"{picked.Count:N0}개({FileItem.FormatBytes(size)})를 휴지통으로 옮겼어요. 휴지통에서 되살릴 수 있고, 휴지통을 비워야 공간이 늘어나요."
            : "일부를 옮기지 못했어요. 작업 창에서 이유를 볼 수 있어요(쓰고 있는 프로그램이 있으면 이름을 알려 드려요).", C("close", "닫기"));
        w.Close();
    }

    private static string Rel(string root, string path)
    {
        var r = Path.GetRelativePath(root, path);
        return r.StartsWith("..", StringComparison.Ordinal) ? path : r;
    }

    /// <summary>개발 도구 캐시 정리: 깔린 도구만, 공식 명령으로. 되살릴 수 없으니 크기를 보여 주고 한 번 확인.</summary>
    public static async Task DevCachesAsync(Window owner)
    {
        var w = Open(owner, "개발 캐시 정리");
        w.Say("npm·NuGet·pip·Docker 같은 개발 도구가 받아 둔 캐시를 살펴볼게요. 다시 받을 수 있는 것만 다루고, 각 도구의 공식 정리 명령만 써요.");
        w.Busy();
        var found = await Task.Run(() => DevCaches.Find(Environment.GetEnvironmentVariable("PATH"), Environment.ExpandEnvironmentVariables));
        if (found.Count == 0)
        {
            await w.AskAsync("정리할 개발 캐시가 없어요 (npm·NuGet·pip·Yarn·pnpm·Docker를 못 찾았거나 캐시가 비어 있어요).", C("close", "닫기"));
            w.Close();
            return;
        }

        string Label(DevCache c) => $"{c.Kind.Name}  ·  {(c.Size >= 0 ? FileItem.FormatBytes(c.Size) : "크기는 Docker가 계산")}  ·  {c.Kind.Why}";
        var (choice, picked) = await w.PickAsync("정리할 캐시를 골라 주세요.", found.Select(c => (c, Label(c), c.Kind.Id != "docker")), c => c.Kind.Note,
            C("clean", "정리하기"), C("skip", "그대로 두기"));
        if (choice is "skip" or GuideWindow.ClosedChoice || picked.Count == 0)
        {
            if (!w.IsClosed)
            {
                await w.AskAsync("네, 그대로 둘게요.", C("close", "닫기"));
                w.Close();
            }

            return;
        }

        var size = picked.Where(c => c.Size > 0).Sum(c => c.Size);
        var go = await w.AskAsync($"{string.Join(", ", picked.Select(c => c.Kind.Name))}를 정리해요{(size > 0 ? $" (약 {FileItem.FormatBytes(size)})" : "")}. 휴지통을 거치지 않아 되살릴 수 없어요(필요하면 다시 받아요).",
            C("go", "정리하기"), C("cancel", "그만두기"));
        if (go != "go")
        {
            w.Close();
            return;
        }

        var cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        long freed = 0;
        foreach (var c in picked)
        {
            if (w.IsClosed)
            {
                return;
            }

            w.Say($"{c.Kind.Name} 정리 중… ({string.Join(' ', c.Kind.Args)})");
            w.Busy();
            var (file, args) = DevCaches.Command(c, cmd);
            var (code, output) = await Task.Run(() => Run(file, args, TimeSpan.FromMinutes(10)));
            if (code == 0)
            {
                var after = c.Size >= 0 ? DevCaches.Find(Environment.GetEnvironmentVariable("PATH"), Environment.ExpandEnvironmentVariables).FirstOrDefault(x => x.Kind.Id == c.Kind.Id)?.Size ?? 0 : -1;
                if (c.Size > 0 && after >= 0)
                {
                    freed += Math.Max(0, c.Size - after);
                }

                w.Say($"{c.Kind.Name}: 끝났어요.{(c.Size > 0 && after >= 0 ? $" {FileItem.FormatBytes(Math.Max(0, c.Size - after))} 늘었어요." : "")}");
            }
            else
            {
                w.Say($"{c.Kind.Name}: 하지 못했어요 (종료 코드 {code}). {Last(output)}");
            }
        }

        await w.AskAsync(freed > 0 ? $"다 했어요! 모두 {FileItem.FormatBytes(freed)}를 비웠어요." : "다 했어요!", C("close", "닫기"));
        w.Close();
    }

    private static string Last(string output)
    {
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return lines.Length == 0 ? "" : lines[^1].Length > 200 ? lines[^1][..200] : lines[^1];
    }

    private static (int Code, string Output) Run(string file, IReadOnlyList<string> args, TimeSpan timeout)
    {
        var psi = new ProcessStartInfo(file) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        try
        {
            using var p = Process.Start(psi);
            if (p is null)
            {
                return (-1, "");
            }

            var outTask = p.StandardOutput.ReadToEndAsync();
            var errTask = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(timeout))
            {
                try
                {
                    p.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                }

                return (-2, "시간이 너무 오래 걸려 멈췄어요");
            }

            return (p.ExitCode, outTask.Result + "\n" + errTask.Result);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return (-1, ex.Message);
        }
    }
}
