namespace Foldy.Core.Organize;

/// <summary>정리에 쓰는 파일 작업. 같은 드라이브는 이름 변경(MoveFileEx)만 한다.</summary>
public interface IMoveOperations
{
    bool FileExists(string path);

    bool DirectoryExists(string path);

    void CreateDirectory(string path);

    void MoveFile(string source, string destination);

    void MoveDirectory(string source, string destination);

    /// <summary>비어 있을 때만 지운다.</summary>
    bool DeleteDirectoryIfEmpty(string path);

    /// <summary>되돌릴 때 "그사이 수정됐는지" 판단용 (크기, 수정 시각). 없으면 null.</summary>
    (long Size, DateTime LastWriteUtc)? GetStamp(string path, bool isFolder);

    bool IsSameVolume(string a, string b);
}

/// <summary>로컬 구현: File.Move/Directory.Move (같은 볼륨은 MoveFileEx 이름 변경과 같음). 덮어쓰지 않는다.</summary>
public sealed class LocalMoveOperations : IMoveOperations
{
    public static readonly LocalMoveOperations Instance = new();

    public bool FileExists(string path) => File.Exists(path);

    public bool DirectoryExists(string path) => Directory.Exists(path);

    public void CreateDirectory(string path) => Directory.CreateDirectory(path);

    public void MoveFile(string source, string destination) => File.Move(source, destination, overwrite: false);

    public void MoveDirectory(string source, string destination) => Directory.Move(source, destination);

    public bool DeleteDirectoryIfEmpty(string path)
    {
        if (!Directory.Exists(path) || Directory.EnumerateFileSystemEntries(path).Any())
        {
            return false;
        }

        Directory.Delete(path);
        return true;
    }

    public (long Size, DateTime LastWriteUtc)? GetStamp(string path, bool isFolder)
    {
        if (isFolder)
        {
            return Directory.Exists(path) ? (0, Directory.GetLastWriteTimeUtc(path)) : null;
        }

        var fi = new FileInfo(path);
        return fi.Exists ? (fi.Length, fi.LastWriteTimeUtc) : null;
    }

    public bool IsSameVolume(string a, string b) =>
        string.Equals(Path.GetPathRoot(Path.GetFullPath(a)), Path.GetPathRoot(Path.GetFullPath(b)), StringComparison.OrdinalIgnoreCase);
}

public sealed record OrganizeProgress(int Done, int Total, string? Current);

public sealed class OrganizeResult
{
    public required string JournalPath { get; init; }

    public int Moved { get; set; }

    public List<(string Path, string Error)> Failed { get; } = [];

    public bool Canceled { get; set; }
}

public sealed class UndoResult
{
    public int Restored { get; set; }

    /// <summary>그사이 수정돼 현재 위치에 둔 파일.</summary>
    public List<string> LeftModified { get; } = [];

    /// <summary>찾을 수 없는 파일 (사용자가 옮기거나 지움).</summary>
    public List<string> Missing { get; } = [];

    /// <summary>원래 자리에 같은 이름이 생겨 "이름 (2)"로 돌려놓은 파일.</summary>
    public List<string> Renamed { get; } = [];

    public List<(string Path, string Error)> Failed { get; } = [];

    public int RemovedFolders { get; set; }

    public bool AlreadyUndone { get; set; }
}

/// <summary>정리 실행·되돌리기 (설계서 5.11). 모든 이동은 저널에 기록하고, 삭제는 하지 않는다 (빈 폴더 정리만).</summary>
public static class OrganizeExecutor
{
    public static string DefaultJournalFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Foldy", "jobs");

    public static string NewJournalPath(string? folder = null) =>
        Path.Combine(folder ?? DefaultJournalFolder, $"organize-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}.journal");

    public static OrganizeResult Execute(OrganizePlan plan, string journalPath, IMoveOperations ops,
        IProgress<OrganizeProgress>? progress = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(ops);
        if (plan.Blocked is not null)
        {
            throw new InvalidOperationException(plan.Blocked);
        }

        var result = new OrganizeResult { JournalPath = journalPath };
        var moves = plan.IncludedMoves.ToList();
        using var journal = new OrganizeJournalWriter(journalPath);
        journal.Append(new JournalRecord { Type = JournalRecordType.Header, Job = Path.GetFileNameWithoutExtension(journalPath), Root = plan.Root });
        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < moves.Count; i++)
        {
            if (ct.IsCancellationRequested)
            {
                result.Canceled = true;
                break;
            }

            var m = moves[i];
            progress?.Report(new OrganizeProgress(i, moves.Count, m.Source));
            try
            {
                if (m.IsFolder ? !ops.DirectoryExists(m.Source) : !ops.FileExists(m.Source))
                {
                    throw new FileNotFoundException(Foldy.Core.Localization.Loc.T("oe.noSource"), m.Source);
                }

                if (m.IsFolder && !ops.IsSameVolume(m.Source, m.Destination))
                {
                    throw new IOException(Foldy.Core.Localization.Loc.T("oe.crossDrive"));
                }

                EnsureDirectory(Path.GetDirectoryName(m.Destination)!, plan.Root, ops, journal);

                // 계획 뒤에 같은 이름이 생겼으면 다시 피한다 (덮어쓰지 않음)
                var dest = OrganizePlanner.Unique(m.Destination, p => ops.FileExists(p) || ops.DirectoryExists(p), claimed);
                if (m.IsFolder)
                {
                    ops.MoveDirectory(m.Source, dest);
                }
                else
                {
                    ops.MoveFile(m.Source, dest);
                }

                claimed.Add(dest);
                var stamp = ops.GetStamp(dest, m.IsFolder);
                journal.Append(new JournalRecord
                {
                    Type = JournalRecordType.Move, Src = m.Source, Dst = dest, IsFolder = m.IsFolder,
                    Size = stamp?.Size ?? 0, LastWriteUtc = stamp?.LastWriteUtc,
                });
                result.Moved++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 사용 중인 파일 등: 건너뛰고 목록으로 알린다
                result.Failed.Add((m.Source, ex.Message));
                journal.Append(new JournalRecord { Type = JournalRecordType.Fail, Src = m.Source, Dst = m.Destination, Error = ex.Message });
            }
        }

        progress?.Report(new OrganizeProgress(result.Moved + result.Failed.Count, moves.Count, null));
        journal.Append(new JournalRecord { Type = JournalRecordType.Done });
        return result;
    }

    private static void EnsureDirectory(string dir, string root, IMoveOperations ops, OrganizeJournalWriter journal)
    {
        if (ops.DirectoryExists(dir))
        {
            return;
        }

        // 위에서부터 없는 단계만 만들고 하나씩 기록 (되돌릴 때 이것만 지운다)
        var missing = new Stack<string>();
        for (var d = dir; !string.IsNullOrEmpty(d) && !ops.DirectoryExists(d); d = Path.GetDirectoryName(d))
        {
            missing.Push(d);
            if (string.Equals(Path.TrimEndingDirectorySeparator(d), Path.TrimEndingDirectorySeparator(root), StringComparison.OrdinalIgnoreCase))
            {
                break;
            }
        }

        while (missing.Count > 0)
        {
            var d = missing.Pop();
            journal.Append(new JournalRecord { Type = JournalRecordType.Mkdir, Dst = d });
            ops.CreateDirectory(d);
        }
    }

    /// <summary>
    /// 되돌리기: 저널의 이동을 거꾸로 원래 자리에 돌려놓고, 정리로 만든 빈 폴더만 지운다.
    /// 그사이 수정된 파일은 현재 위치에 두고 알려준다.
    /// </summary>
    public static UndoResult Undo(string journalPath, IMoveOperations ops)
    {
        ArgumentNullException.ThrowIfNull(ops);
        var records = OrganizeJournalReader.Read(journalPath, out _);
        var result = new UndoResult();
        if (records.Any(r => r.Type == JournalRecordType.Undone))
        {
            result.AlreadyUndone = true;
            return result;
        }

        using var journal = new OrganizeJournalWriter(journalPath);
        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in records.Where(r => r.Type == JournalRecordType.Move).Reverse())
        {
            var src = r.Src!;
            var dst = r.Dst!;
            try
            {
                var stamp = ops.GetStamp(dst, r.IsFolder);
                if (stamp is null)
                {
                    result.Missing.Add(dst);
                    continue;
                }

                if (!r.IsFolder && (stamp.Value.Size != r.Size || (r.LastWriteUtc is { } lw && stamp.Value.LastWriteUtc != lw)))
                {
                    result.LeftModified.Add(dst);
                    continue;
                }

                var parent = Path.GetDirectoryName(src)!;
                if (!ops.DirectoryExists(parent))
                {
                    ops.CreateDirectory(parent);
                }

                var target = OrganizePlanner.Unique(src, p => ops.FileExists(p) || ops.DirectoryExists(p), claimed);
                if (r.IsFolder)
                {
                    ops.MoveDirectory(dst, target);
                }
                else
                {
                    ops.MoveFile(dst, target);
                }

                claimed.Add(target);
                if (!string.Equals(target, src, StringComparison.OrdinalIgnoreCase))
                {
                    result.Renamed.Add(target);
                }

                result.Restored++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                result.Failed.Add((dst, ex.Message));
            }
        }

        foreach (var r in records.Where(r => r.Type == JournalRecordType.Mkdir).Reverse())
        {
            try
            {
                if (ops.DeleteDirectoryIfEmpty(r.Dst!))
                {
                    result.RemovedFolders++;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                result.Failed.Add((r.Dst!, ex.Message));
            }
        }

        journal.Append(new JournalRecord { Type = JournalRecordType.Undone });
        return result;
    }
}
