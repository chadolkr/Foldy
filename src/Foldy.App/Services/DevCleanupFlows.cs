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
        var w = Open(owner, Foldy.Core.Localization.Loc.T("dev.rb.title"));
        w.Say(Foldy.Core.Localization.Loc.F("dev.rb.hello", root));
        w.Busy();
        IReadOnlyList<RebuildableFolder> found;
        try
        {
            var kinds = RebuildableFolders.With(extraNames);
            found = await Task.Run(() => RebuildableFolders.Scan(root, kinds, DateTime.UtcNow, indexSize));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await w.AskAsync(Foldy.Core.Localization.Loc.T("cf.m.cantRead") + ex.Message, C("close", Foldy.Core.Localization.Loc.T("cf.close")));
            w.Close();
            return;
        }

        if (found.Count == 0)
        {
            await w.AskAsync(Foldy.Core.Localization.Loc.T("dev.rb.none"), C("close", Foldy.Core.Localization.Loc.T("cf.close")));
            w.Close();
            return;
        }

        var total = found.Where(f => f.Size > 0).Sum(f => f.Size);
        w.Say(Foldy.Core.Localization.Loc.F("dev.rb.found", found.Count, FileItem.FormatBytes(total)));
        string Label(RebuildableFolder f) =>
            $"{FileItem.FormatBytes(Math.Max(0, f.Size))}  ·  {Rel(root, f.Path)}  ·  {f.Kind.Why}"
            + (f.LastWriteUtc > DateTime.MinValue ? $"  ·  {f.LastWriteUtc.ToLocalTime():yyyy-MM-dd}" : "");
        var (choice, picked) = await w.PickAsync(Foldy.Core.Localization.Loc.T("dev.rb.pick"),
            found.Select(f => (f, Label(f), f.Selected)), f => f.Warning,
            C("recycle", Foldy.Core.Localization.Loc.T("ops.toRecycle")), C("skip", Foldy.Core.Localization.Loc.T("cf.keep")));
        if (choice is "skip" or GuideWindow.ClosedChoice || picked.Count == 0)
        {
            if (!w.IsClosed)
            {
                await w.AskAsync(Foldy.Core.Localization.Loc.T("cf.keepOk"), C("close", Foldy.Core.Localization.Loc.T("cf.close")));
                w.Close();
            }

            return;
        }

        // .git을 골랐으면 한 번 더 (되돌릴 수 없는 기록)
        if (picked.Any(p => !p.Kind.CanRebuild))
        {
            var git = picked.Count(p => !p.Kind.CanRebuild);
            var sure = await w.AskAsync(Foldy.Core.Localization.Loc.F("dev.rb.gitWarn", git),
                C("yes", Foldy.Core.Localization.Loc.T("dev.rb.withGit")), C("nogit", Foldy.Core.Localization.Loc.T("dev.rb.noGit")), C("cancel", Foldy.Core.Localization.Loc.T("dlg.cancel")));
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
            ? Foldy.Core.Localization.Loc.F("dev.rb.done", picked.Count, FileItem.FormatBytes(size))
            : Foldy.Core.Localization.Loc.T("dev.rb.partial"), C("close", Foldy.Core.Localization.Loc.T("cf.close")));
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
        var w = Open(owner, Foldy.Core.Localization.Loc.T("dev.dc.title"));
        w.Say(Foldy.Core.Localization.Loc.T("dev.dc.hello"));
        w.Busy();
        var found = await Task.Run(() => DevCaches.Find(Environment.GetEnvironmentVariable("PATH"), Environment.ExpandEnvironmentVariables));
        if (found.Count == 0)
        {
            await w.AskAsync(Foldy.Core.Localization.Loc.T("dev.dc.none"), C("close", Foldy.Core.Localization.Loc.T("cf.close")));
            w.Close();
            return;
        }

        string Label(DevCache c) => $"{c.Kind.Name}  ·  {(c.Size >= 0 ? FileItem.FormatBytes(c.Size) : Foldy.Core.Localization.Loc.T("dev.dc.dockerSize"))}  ·  {c.Kind.Why}";
        var (choice, picked) = await w.PickAsync(Foldy.Core.Localization.Loc.T("dev.dc.pick"), found.Select(c => (c, Label(c), c.Kind.Id != "docker")), c => c.Kind.Note,
            C("clean", Foldy.Core.Localization.Loc.T("dev.dc.go")), C("skip", Foldy.Core.Localization.Loc.T("cf.keep")));
        if (choice is "skip" or GuideWindow.ClosedChoice || picked.Count == 0)
        {
            if (!w.IsClosed)
            {
                await w.AskAsync(Foldy.Core.Localization.Loc.T("cf.keepOk"), C("close", Foldy.Core.Localization.Loc.T("cf.close")));
                w.Close();
            }

            return;
        }

        var size = picked.Where(c => c.Size > 0).Sum(c => c.Size);
        var go = await w.AskAsync(Foldy.Core.Localization.Loc.F("dev.dc.ask", string.Join(", ", picked.Select(c => c.Kind.Name)), (size > 0 ? Foldy.Core.Localization.Loc.F("dev.dc.about", FileItem.FormatBytes(size)) : "")),
            C("go", Foldy.Core.Localization.Loc.T("dev.dc.go")), C("cancel", Foldy.Core.Localization.Loc.T("dlg.cancel")));
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

            w.Say(Foldy.Core.Localization.Loc.F("dev.dc.running", c.Kind.Name, string.Join(' ', c.Kind.Args)));
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

                w.Say(Foldy.Core.Localization.Loc.F("dev.dc.oneDone", c.Kind.Name, (c.Size > 0 && after >= 0 ? Foldy.Core.Localization.Loc.F("dev.dc.freed", FileItem.FormatBytes(Math.Max(0, c.Size - after))) : "")));
            }
            else
            {
                w.Say(Foldy.Core.Localization.Loc.F("dev.dc.failed", c.Kind.Name, code, Last(output)));
            }
        }

        await w.AskAsync(freed > 0 ? Foldy.Core.Localization.Loc.F("dev.dc.allDone", FileItem.FormatBytes(freed)) : Foldy.Core.Localization.Loc.T("cf.done"), C("close", Foldy.Core.Localization.Loc.T("cf.close")));
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

                return (-2, Foldy.Core.Localization.Loc.T("dev.dc.timeout"));
            }

            return (p.ExitCode, outTask.Result + "\n" + errTask.Result);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return (-1, ex.Message);
        }
    }
}
