using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Foldy.Core.Organize;

namespace Foldy.Core.Managed;

/// <summary>폴더 관리 방법 (폴더의 .foldy.md에 적는다).</summary>
public sealed record FolderMethod(string Id, string Name, string Badge, string Explanation, string? TemplateId)
{
    public static readonly FolderMethod Para = new("para", "PARA", "PARA",
        "끝나는 날이 있는 일은 1_Projects, 계속 관리하는 영역은 2_Areas, 참고 자료는 3_Resources, 끝난 것은 4_Archives에 둡니다. 오래 손대지 않은 프로젝트는 Archives로 옮기자고 알려 드려요.", "para");

    public static readonly FolderMethod JohnnyDecimal = new("johnny-decimal", "Johnny.Decimal", "JD",
        "영역(10-19)과 카테고리(11) 번호로 자리를 정합니다. 번호 없는 폴더가 생기면 알려 드려요.", "johnny-decimal");

    public static readonly FolderMethod NumberPrefix = new("number-prefix", "번호·날짜 붙이기", "01_",
        "00_받은 파일에 일단 모으고 01_진행중, 02_자료, 03_완료, 99_보관으로 나눕니다. 오래 손대지 않은 진행중 폴더는 완료로 옮기자고 알려 드려요.", "number-prefix");

    public static readonly FolderMethod ByDate = new("date", "날짜별", "날짜",
        "파일을 연도\\연-월 폴더에 둡니다. 맨 위에 흩어진 파일은 바꾼 날짜에 맞는 폴더로 넣자고 알려 드려요.", null);

    public static readonly IReadOnlyList<FolderMethod> All = [Para, JohnnyDecimal, NumberPrefix, ByDate];

    public static FolderMethod? Find(string? idOrName) =>
        All.FirstOrDefault(m => string.Equals(m.Id, idOrName?.Trim(), StringComparison.OrdinalIgnoreCase) || string.Equals(m.Name, idOrName?.Trim(), StringComparison.OrdinalIgnoreCase));
}

/// <summary>제안 하나: 설명 + 옮길 목록 (옮길 게 없으면 알림만).</summary>
public sealed record ManagedMove(string Src, string Dst, bool IsFolder);

public sealed record ManagedSuggestion(string Text, IReadOnlyList<ManagedMove> Moves);

/// <summary>
/// 폴더별 관리 방법. 폴더에 숨김 파일 .foldy.md("정리 방법: PARA")를 두고, 하위 폴더는 가장 가까운 .foldy.md를 따른다.
/// 저절로 옮기지 않는다: 제안을 만들고, 사용자가 확인하면 <see cref="Apply"/>로 옮기고 <see cref="Undo"/>로 되돌린다.
/// Git 저장소·개발 프로젝트 폴더에는 정할 수 없고, 관리 폴더 안의 저장소는 통째로만 다룬다(안은 건드리지 않음).
/// </summary>
public static class ManagedFolders
{
    public const string FileName = ".foldy.md";
    public const int StaleDays = 90;
    public const string Inbox = "00_받은 파일";

    private static readonly Regex MethodLine = new(@"^\s*정리\s*방법\s*:\s*(.+?)\s*$", RegexOptions.Multiline);

    // 옮기면 다른 프로그램·바로 가기가 깨질 수 있는 파일은 제안하지 않는다
    private static readonly HashSet<string> NeverMove = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".dll", ".lnk", ".url", ".ini", ".config", ".json", ".xml", ".yaml", ".yml", ".bat", ".cmd", ".ps1", ".sys", ".db", ".sqlite",
    };

    /// <summary>관리 폴더(.foldy.md가 있는 폴더) 목록. 목록·트리 아이콘 표시와 백그라운드 확인에 쓴다 (앱이 파일로 저장).</summary>
    private static readonly HashSet<string> Roots = new(StringComparer.OrdinalIgnoreCase);
    private static string? _registryPath;

    public static event Action? Changed;

    public static void LoadRegistry(string path)
    {
        _registryPath = path;
        try
        {
            if (File.Exists(path))
            {
                lock (Roots)
                {
                    foreach (var r in JsonSerializer.Deserialize<List<string>>(File.ReadAllText(path)) ?? [])
                    {
                        Roots.Add(Norm(r));
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
        }
    }

    public static IReadOnlyList<string> KnownRoots()
    {
        lock (Roots)
        {
            return [.. Roots];
        }
    }

    public static bool IsRoot(string path)
    {
        lock (Roots)
        {
            return Roots.Count > 0 && Roots.Contains(Norm(path));
        }
    }

    private static void SaveRegistry()
    {
        if (_registryPath is null)
        {
            return;
        }

        try
        {
            lock (Roots)
            {
                File.WriteAllText(_registryPath, JsonSerializer.Serialize(Roots.ToList()));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        Changed?.Invoke();
    }

    private static string Norm(string p) => p.TrimEnd('\\', '/');

    /// <summary>이 폴더에 방법을 정할 수 없는 이유 (Git·개발 프로젝트 폴더이거나 그 안). 정할 수 있으면 null.</summary>
    public static string? CannotManage(string folder)
    {
        for (var d = new DirectoryInfo(folder); d is not null; d = d.Parent)
        {
            if (OrganizeScanner.IsProjectFolder(d.FullName))
            {
                return "Git 저장소·개발 프로젝트 폴더는 구조를 바꾸지 않아요. 파일을 옮기면 빌드나 Git 기록이 깨질 수 있어서요. 저장소들을 담은 위 폴더는 관리할 수 있어요.";
            }
        }

        return null;
    }

    /// <summary>가장 가까운 관리 폴더와 방법. 없으면 null.</summary>
    public static (string Root, FolderMethod Method)? Resolve(string folder)
    {
        for (var d = new DirectoryInfo(folder); d is not null; d = d.Parent)
        {
            var f = Path.Combine(d.FullName, FileName);
            try
            {
                if (File.Exists(f) && MethodLine.Match(File.ReadAllText(f)) is { Success: true } m && FolderMethod.Find(m.Groups[1].Value) is { } method)
                {
                    return (d.FullName, method);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        return null;
    }

    /// <summary>.foldy.md를 쓴다 (숨김). 사람이 읽을 수 있고, 지워도 된다고 적어 둔다.</summary>
    public static void SetMethod(string folder, FolderMethod method)
    {
        var path = Path.Combine(folder, FileName);
        var keptTags = File.Exists(path) ? Foldy.Core.Tags.FileTags.Parse(File.ReadAllText(path)) : [];
        if (File.Exists(path))
        {
            File.SetAttributes(path, FileAttributes.Normal);
        }

        var text = new StringBuilder()
            .AppendLine("# Foldy 폴더 관리 방법")
            .AppendLine()
            .Append("정리 방법: ").AppendLine(method.Name)
            .AppendLine()
            .AppendLine(method.Explanation)
            .AppendLine()
            .AppendLine("이 파일은 Foldy가 이 폴더를 어떤 방법으로 관리하는지 기억하는 메모예요. 지워도 괜찮아요 (관리가 꺼질 뿐 파일은 그대로예요).")
            .AppendLine("Foldy는 저절로 옮기지 않고, 제안을 보여 드린 뒤 확인을 받고 옮겨요. 옮긴 것은 [되돌리기]로 돌릴 수 있어요.")
            .ToString();
        text = Foldy.Core.Tags.FileTags.Render(text, keptTags); // 파일 상태 태그 칸은 그대로
        File.WriteAllText(path, text, new UTF8Encoding(true));
        File.SetAttributes(path, FileAttributes.Hidden);
        lock (Roots)
        {
            Roots.Add(Norm(folder));
        }

        SaveRegistry();
    }

    public static void ClearMethod(string folder)
    {
        var path = Path.Combine(folder, FileName);
        if (File.Exists(path))
        {
            var tags = Foldy.Core.Tags.FileTags.Parse(File.ReadAllText(path));
            File.SetAttributes(path, FileAttributes.Normal);
            File.Delete(path);
            if (tags.Count > 0)
            {
                // 관리 방법만 끄고 태그는 남긴다
                File.WriteAllText(path, Foldy.Core.Tags.FileTags.Render("", tags), new UTF8Encoding(true));
                File.SetAttributes(path, FileAttributes.Hidden);
            }
        }

        lock (Roots)
        {
            Roots.Remove(Norm(folder));
        }

        SaveRegistry();
    }

    /// <summary>방법에 따른 정리 제안. 옮길 것이 있는 제안만 Moves를 갖는다.</summary>
    public static IReadOnlyList<ManagedSuggestion> Suggest(string root, FolderMethod method, DateTime nowUtc)
    {
        var list = new List<ManagedSuggestion>();
        var dir = new DirectoryInfo(root);
        var loose = dir.EnumerateFiles("*", new EnumerationOptions { AttributesToSkip = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint })
            .Where(f => !NeverMove.Contains(f.Extension) && !f.Name.Equals(FileName, StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (loose.Count > 0)
        {
            if (method == FolderMethod.ByDate)
            {
                var moves = loose.Select(f => new ManagedMove(f.FullName,
                    Path.Combine(root, f.LastWriteTime.ToString("yyyy", System.Globalization.CultureInfo.InvariantCulture), f.LastWriteTime.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture), f.Name), false)).ToList();
                list.Add(new ManagedSuggestion($"맨 위에 흩어진 파일이 {loose.Count:N0}개 있어요. 바꾼 날짜에 맞는 연도\\연-월 폴더로 넣을게요.", moves));
            }
            else
            {
                var inbox = method == FolderMethod.JohnnyDecimal ? Path.Combine(root, "00-09 시스템", "01 받은 파일") : Path.Combine(root, Inbox);
                var moves = loose.Select(f => new ManagedMove(f.FullName, Path.Combine(inbox, f.Name), false)).ToList();
                list.Add(new ManagedSuggestion($"맨 위에 아직 나누지 않은 파일이 {loose.Count:N0}개 있어요. 일단 \"{Path.GetFileName(inbox)}\"에 모아 둘게요. 나중에 천천히 나누면 돼요.", moves));
            }
        }

        if (method == FolderMethod.Para)
        {
            list.AddRange(Stale(root, "1_Projects", "4_Archives", nowUtc, "Projects", "Archives"));
        }
        else if (method == FolderMethod.NumberPrefix)
        {
            list.AddRange(Stale(root, "01_진행중", "03_완료", nowUtc, "01_진행중", "03_완료"));
        }
        else if (method == FolderMethod.JohnnyDecimal)
        {
            var unnumbered = dir.EnumerateDirectories().Where(d => (d.Attributes & FileAttributes.Hidden) == 0 && !Regex.IsMatch(d.Name, @"^\d\d-\d\d\s")).Select(d => d.Name).ToList();
            if (unnumbered.Count > 0)
            {
                list.Add(new ManagedSuggestion($"번호 없는 폴더가 {unnumbered.Count:N0}개 있어요 ({string.Join(", ", unnumbered.Take(3))}{(unnumbered.Count > 3 ? " …" : "")}). 이름 앞에 \"40-49 \"처럼 영역 번호를 붙여 주세요.", []));
            }
        }

        return list;
    }

    private static IEnumerable<ManagedSuggestion> Stale(string root, string from, string to, DateTime nowUtc, string fromLabel, string toLabel)
    {
        var src = new DirectoryInfo(Path.Combine(root, from));
        if (!src.Exists)
        {
            yield break;
        }

        foreach (var d in src.EnumerateDirectories().Where(d => (d.Attributes & FileAttributes.Hidden) == 0))
        {
            var last = LastTouched(d);
            var days = (int)(nowUtc - last).TotalDays;
            if (days >= StaleDays)
            {
                var note = OrganizeScanner.IsProjectFolder(d.FullName) ? " (저장소라서 안은 건드리지 않고 폴더째 옮겨요)" : "";
                yield return new ManagedSuggestion($"{fromLabel}의 \"{d.Name}\"을 {days:N0}일째 손대지 않았어요. {toLabel}로 옮길까요?{note}",
                    [new ManagedMove(d.FullName, Path.Combine(root, to, d.Name), true)]);
            }
        }
    }

    // 폴더와 바로 아래 항목 중 가장 최근 수정 시각 (깊이 훑지 않는다: 느린 드라이브에서도 가볍게)
    private static DateTime LastTouched(DirectoryInfo d)
    {
        var last = d.LastWriteTimeUtc;
        try
        {
            foreach (var e in d.EnumerateFileSystemInfos())
            {
                if (e.LastWriteTimeUtc > last)
                {
                    last = e.LastWriteTimeUtc;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return last;
    }

    /// <summary>옮긴다. 열려 있거나 이름이 겹치면 건너뛰고 이유를 돌려준다. 옮긴 것은 되돌리기 기록에 남긴다.</summary>
    public static (IReadOnlyList<ManagedMove> Done, IReadOnlyList<(string Path, string Reason)> Skipped) Apply(IEnumerable<ManagedMove> moves, string journalPath)
    {
        var done = new List<ManagedMove>();
        var skipped = new List<(string, string)>();
        foreach (var m in moves)
        {
            try
            {
                if (File.Exists(m.Dst) || Directory.Exists(m.Dst))
                {
                    skipped.Add((m.Src, "같은 이름이 이미 있어서 건너뛰었어요"));
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(m.Dst)!);
                if (m.IsFolder)
                {
                    Directory.Move(m.Src, m.Dst);
                }
                else
                {
                    File.Move(m.Src, m.Dst);
                }

                done.Add(m);
            }
            catch (IOException)
            {
                skipped.Add((m.Src, "열려 있어서 건너뛰었어요"));
            }
            catch (UnauthorizedAccessException)
            {
                skipped.Add((m.Src, "권한이 없어서 건너뛰었어요"));
            }
        }

        if (done.Count > 0)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(journalPath)!);
            File.WriteAllText(journalPath, JsonSerializer.Serialize(done));
        }

        return (done, skipped);
    }

    public static bool CanUndo(string journalPath) => File.Exists(journalPath);

    /// <summary>지난번 적용을 되돌린다. 되돌린 개수.</summary>
    public static int Undo(string journalPath)
    {
        if (!File.Exists(journalPath))
        {
            return 0;
        }

        var n = 0;
        List<ManagedMove> moves;
        try
        {
            moves = JsonSerializer.Deserialize<List<ManagedMove>>(File.ReadAllText(journalPath)) ?? [];
        }
        catch (JsonException)
        {
            moves = [];
        }

        foreach (var m in Enumerable.Reverse(moves))
        {
            try
            {
                if (m.IsFolder && Directory.Exists(m.Dst) && !Directory.Exists(m.Src))
                {
                    Directory.Move(m.Dst, m.Src);
                    n++;
                }
                else if (!m.IsFolder && File.Exists(m.Dst) && !File.Exists(m.Src))
                {
                    File.Move(m.Dst, m.Src);
                    n++;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        File.Delete(journalPath);
        return n;
    }
}
