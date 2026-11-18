using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Foldy.Analysis;

public sealed record ScanInfo(string Volume, DateTime StartedUtc, DateTime FinishedUtc, long TotalFiles, long TotalDirs, long TotalAlloc);

/// <param name="OwnAlloc">하위 폴더를 뺀 자신의 파일 크기 ("자신 제외 크기"를 고른 경우만 채움).</param>
public sealed record FolderRow(
    long Id, long? ParentId, string Name, int Depth, long Size, long Alloc, long OwnFiles, long TotalFiles, long TotalDirs,
    long NewestMtime, long OldestMtime, string? Tag, string Path, long OwnAlloc);

public sealed record FileRow(long Id, long DirId, string Name, string Ext, long Size, long Alloc, long Mtime, string Path);

public sealed record ExtStat(string Ext, long Files, long Size, long Alloc);

public sealed record GroupStat(string Group, long Files, long Alloc, IReadOnlyList<string> Extensions);

/// <summary>트리맵 칸 하나 (폴더 또는 파일).</summary>
public sealed record TreemapChild(long Id, bool IsDir, string Name, long Alloc, long Size, string? Tag);

public sealed record CandidateRow(long Id, string Kind, long DirId, long? FileId, long Reclaim, string Rule, string Reason, string Path, string? Tag);

/// <param name="PreviousAlloc">지난 스냅샷의 크기 (새로 생긴 폴더는 0).</param>
public sealed record GrowthRow(long DirId, string Name, string Path, long Alloc, long PreviousAlloc, bool IsNew)
{
    public long Delta => Alloc - PreviousAlloc;
}

/// <summary>
/// 분석 스냅샷 DB 하나를 읽기 전용으로 연 것 (설계서 7.3 "같은 DB로 답하는 것"). 스레드 안전하지 않다 — 호출자가 한 번에 하나씩 부른다.
/// 경로 문자열은 DB에 없고 parent_id 사슬을 재귀 CTE로 올라가 만든다 (결과는 캐시).
/// </summary>
public sealed class AnalysisDb : IDisposable
{
    private readonly SqliteConnection _c;
    private readonly Dictionary<long, string> _paths = [];
    private string? _attached;

    private AnalysisDb(SqliteConnection c, string path)
    {
        _c = c;
        FilePath = path;
    }

    public string FilePath { get; }

    /// <summary>스냅샷을 연다 (mode=ro). 스키마 버전이 다르면 <see cref="InvalidDataException"/>.</summary>
    public static AnalysisDb Open(string path)
    {
        var c = AnalysisSqlite.OpenReadOnly(path);
        try
        {
            var v = Convert.ToInt32(Scalar(c, "PRAGMA user_version"), CultureInfo.InvariantCulture);
            if (v != AnalysisSchema.Version)
            {
                throw new InvalidDataException($"분석 DB 스키마 버전 {v} (기대 {AnalysisSchema.Version}): {path}");
            }

            var db = new AnalysisDb(c, path);
            db.RootId = Convert.ToInt64(Scalar(c, "SELECT id FROM dir WHERE parent_id IS NULL LIMIT 1") ?? throw new InvalidDataException("루트 폴더 없음"), CultureInfo.InvariantCulture);
            return db;
        }
        catch
        {
            c.Dispose();
            throw;
        }
    }

    public long RootId { get; private set; }

    public ScanInfo Scan()
    {
        using var cmd = Cmd("SELECT volume, started_at, finished_at, total_files, total_dirs, total_alloc FROM scan WHERE id = 1");
        using var r = cmd.ExecuteReader();
        if (!r.Read())
        {
            throw new InvalidDataException("scan 행 없음");
        }

        return new ScanInfo(r.GetString(0), AnalysisText.FromFileTime(r.GetInt64(1)), AnalysisText.FromFileTime(r.GetInt64(2)), r.GetInt64(3), r.GetInt64(4), r.GetInt64(5));
    }

    private const string DirCols = "d.id, d.parent_id, d.name, d.depth, d.size, d.alloc, d.own_files, d.total_files, d.total_dirs, d.newest_mtime, d.oldest_mtime, d.tag";

    private static FolderRow ReadFolder(SqliteDataReader r, long own = 0)
    {
        var id = r.GetInt64(0);
        return new FolderRow(id, r.IsDBNull(1) ? null : r.GetInt64(1), r.GetString(2), r.GetInt32(3), r.GetInt64(4), r.GetInt64(5), r.GetInt64(6),
            r.GetInt64(7), r.GetInt64(8), r.GetInt64(9), r.GetInt64(10), r.IsDBNull(11) ? null : r.GetString(11), "", own);
    }

    private List<FolderRow> WithPaths(List<FolderRow> rows) => [.. rows.Select(x => x with { Path = PathOf(x.Id) })];

    public FolderRow? Folder(long id)
    {
        using var cmd = Cmd($"SELECT {DirCols} FROM dir d WHERE d.id = $id", ("$id", id));
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadFolder(r) with { Path = PathOf(id) } : null;
    }

    /// <summary>큰 폴더 Top N (루트 제외). <paramref name="ownSizeOnly"/>면 하위 폴더 크기를 뺀 "자신 제외 크기"로 정렬해 중첩 중복을 없앤다.</summary>
    public IReadOnlyList<FolderRow> TopFolders(int n, bool ownSizeOnly = false)
    {
        var sql = ownSizeOnly
            ? $"SELECT {DirCols}, d.alloc - COALESCE((SELECT SUM(c.alloc) FROM dir c WHERE c.parent_id = d.id), 0) AS own FROM dir d WHERE d.parent_id IS NOT NULL ORDER BY own DESC LIMIT $n"
            : $"SELECT {DirCols} FROM dir d WHERE d.parent_id IS NOT NULL ORDER BY d.alloc DESC LIMIT $n";
        using var cmd = Cmd(sql, ("$n", n));
        using var r = cmd.ExecuteReader();
        var rows = new List<FolderRow>();
        while (r.Read())
        {
            rows.Add(ReadFolder(r, ownSizeOnly ? r.GetInt64(12) : 0));
        }

        return WithPaths(rows);
    }

    /// <summary>파일 수가 많은 폴더. <paramref name="ownFilesOnly"/>면 한 폴더에 바로 몰린 파일 수(own_files)로.</summary>
    public IReadOnlyList<FolderRow> MostFilesFolders(int n, bool ownFilesOnly = false)
    {
        var order = ownFilesOnly ? "d.own_files" : "d.total_files";
        using var cmd = Cmd($"SELECT {DirCols} FROM dir d WHERE d.parent_id IS NOT NULL ORDER BY {order} DESC LIMIT $n", ("$n", n));
        using var r = cmd.ExecuteReader();
        var rows = new List<FolderRow>();
        while (r.Read())
        {
            rows.Add(ReadFolder(r));
        }

        return WithPaths(rows);
    }

    private List<FileRow> ReadFiles(SqliteCommand cmd)
    {
        using var r = cmd.ExecuteReader();
        var rows = new List<FileRow>();
        while (r.Read())
        {
            rows.Add(new FileRow(r.GetInt64(0), r.GetInt64(1), r.GetString(2), r.GetString(3), r.GetInt64(4), r.GetInt64(5), r.GetInt64(6), ""));
        }

        return [.. rows.Select(f => f with { Path = Join(PathOf(f.DirId), f.Name) })];
    }

    public IReadOnlyList<FileRow> TopFiles(int n)
    {
        using var cmd = Cmd("SELECT id, dir_id, name, ext, size, alloc, mtime FROM file ORDER BY alloc DESC LIMIT $n", ("$n", n));
        return ReadFiles(cmd);
    }

    /// <summary>오래된 파일: 수정 시각이 <paramref name="olderThanUtc"/>보다 이른 파일을 큰 순으로.</summary>
    public IReadOnlyList<FileRow> OldFiles(DateTime olderThanUtc, int n)
    {
        using var cmd = Cmd("SELECT id, dir_id, name, ext, size, alloc, mtime FROM file WHERE mtime > 0 AND mtime < $t ORDER BY alloc DESC LIMIT $n",
            ("$t", AnalysisText.ToFileTime(olderThanUtc)), ("$n", n));
        return ReadFiles(cmd);
    }

    public IReadOnlyList<FileRow> FilesWithExtension(string ext, int n)
    {
        using var cmd = Cmd("SELECT id, dir_id, name, ext, size, alloc, mtime FROM file WHERE ext = $e ORDER BY alloc DESC LIMIT $n", ("$e", ext), ("$n", n));
        return ReadFiles(cmd);
    }

    /// <summary>확장자별 용량 (큰 순).</summary>
    public IReadOnlyList<ExtStat> ExtensionStats()
    {
        using var cmd = Cmd("SELECT ext, files, size, alloc FROM ext_stat ORDER BY alloc DESC");
        using var r = cmd.ExecuteReader();
        var rows = new List<ExtStat>();
        while (r.Read())
        {
            rows.Add(new ExtStat(r.GetString(0), r.GetInt64(1), r.GetInt64(2), r.GetInt64(3)));
        }

        return rows;
    }

    /// <summary>종류(동영상·압축·설치 파일…)별 용량 (큰 순).</summary>
    public IReadOnlyList<GroupStat> GroupStats() =>
        [.. ExtensionStats()
            .GroupBy(e => ExtensionGroups.GroupOf(e.Ext))
            .Select(g => new GroupStat(g.Key, g.Sum(x => x.Files), g.Sum(x => x.Alloc), [.. g.OrderByDescending(x => x.Alloc).Take(5).Select(x => x.Ext)]))
            .OrderByDescending(g => g.Alloc)];

    /// <summary>트리맵: 폴더 바로 아래 폴더와 파일, 큰 순. 파일은 큰 것 <paramref name="maxFiles"/>개만 (나머지는 부모 크기와의 차이로 "기타").</summary>
    public IReadOnlyList<TreemapChild> Children(long dirId, int maxFiles = 400)
    {
        var list = new List<TreemapChild>();
        using (var cmd = Cmd("SELECT id, name, alloc, size, tag FROM dir WHERE parent_id = $id", ("$id", dirId)))
        using (var r = cmd.ExecuteReader())
        {
            while (r.Read())
            {
                list.Add(new TreemapChild(r.GetInt64(0), true, r.GetString(1), r.GetInt64(2), r.GetInt64(3), r.IsDBNull(4) ? null : r.GetString(4)));
            }
        }

        using (var cmd = Cmd("SELECT id, name, alloc, size FROM file WHERE dir_id = $id ORDER BY alloc DESC LIMIT $n", ("$id", dirId), ("$n", maxFiles)))
        using (var r = cmd.ExecuteReader())
        {
            while (r.Read())
            {
                list.Add(new TreemapChild(r.GetInt64(0), false, r.GetString(1), r.GetInt64(2), r.GetInt64(3), null));
            }
        }

        list.Sort((a, b) => b.Alloc.CompareTo(a.Alloc));
        return list;
    }

    /// <summary>루트 → 폴더 순서의 (id, 이름) 사슬 (브레드크럼).</summary>
    public IReadOnlyList<(long Id, string Name)> Chain(long dirId)
    {
        using var cmd = Cmd("""
            WITH RECURSIVE up(id, parent_id, name, lvl) AS (
              SELECT id, parent_id, name, 0 FROM dir WHERE id = $id
              UNION ALL SELECT d.id, d.parent_id, d.name, up.lvl + 1 FROM dir d JOIN up ON d.id = up.parent_id WHERE up.lvl < 4096)
            SELECT id, name FROM up ORDER BY lvl DESC
            """, ("$id", dirId));
        using var r = cmd.ExecuteReader();
        var v = new List<(long, string)>();
        while (r.Read())
        {
            v.Add((r.GetInt64(0), r.GetString(1)));
        }

        return v;
    }

    /// <summary>폴더 전체 경로 (재귀 CTE, 캐시).</summary>
    public string PathOf(long dirId)
    {
        if (_paths.TryGetValue(dirId, out var p))
        {
            return p;
        }

        var chain = Chain(dirId);
        if (chain.Count == 0)
        {
            return "";
        }

        p = chain[0].Name;
        for (var i = 1; i < chain.Count; i++)
        {
            p = Join(p, chain[i].Name);
        }

        _paths[dirId] = p;
        return p;
    }

    /// <summary>전체 경로 → 폴더 id (대소문자 무시). 스냅샷 루트 밖이거나 없으면 null.</summary>
    public long? FindDir(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var root = PathOf(RootId).TrimEnd('\\');
        var norm = path.Replace('/', '\\').TrimEnd('\\');
        if (!norm.StartsWith(root, StringComparison.OrdinalIgnoreCase) || (norm.Length > root.Length && norm[root.Length] != '\\'))
        {
            return null;
        }

        var cur = RootId;
        foreach (var comp in norm[root.Length..].Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            using var cmd = Cmd("SELECT id FROM dir WHERE parent_id = $p AND name = $n COLLATE NOCASE LIMIT 1", ("$p", cur), ("$n", comp));
            if (cmd.ExecuteScalar() is not long id)
            {
                return null;
            }

            cur = id;
        }

        return cur;
    }

    /// <summary>정리 제안 (회수 가능한 크기 큰 순).</summary>
    public IReadOnlyList<CandidateRow> Candidates()
    {
        using var cmd = Cmd("""
            SELECT c.id, c.kind, c.dir_id, c.file_id, c.reclaim, c.rule, c.reason, f.name, d.tag
            FROM candidate c LEFT JOIN file f ON f.id = c.file_id LEFT JOIN dir d ON d.id = c.dir_id
            ORDER BY c.reclaim DESC
            """);
        using var r = cmd.ExecuteReader();
        var rows = new List<(long, string, long, long?, long, string, string, string?, string?)>();
        while (r.Read())
        {
            rows.Add((r.GetInt64(0), r.GetString(1), r.GetInt64(2), r.IsDBNull(3) ? null : r.GetInt64(3), r.GetInt64(4), r.GetString(5),
                r.IsDBNull(6) ? "" : r.GetString(6), r.IsDBNull(7) ? null : r.GetString(7), r.IsDBNull(8) ? null : r.GetString(8)));
        }

        return [.. rows.Select(x =>
        {
            var dirPath = PathOf(x.Item3);
            var path = x.Item8 is { } fileName ? Join(dirPath, fileName) : dirPath;
            return new CandidateRow(x.Item1, x.Item2, x.Item3, x.Item4, x.Item5, x.Item6, x.Item7, path, x.Item2 == "dir" ? x.Item9 : null);
        })];
    }

    /// <summary>지난 스냅샷의 분석 시각.</summary>
    public DateTime PreviousScanTime(string previousDbPath)
    {
        Attach(previousDbPath);
        return AnalysisText.FromFileTime(Convert.ToInt64(Scalar(_c, "SELECT started_at FROM prev.scan WHERE id = 1") ?? 0L, CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// 지난 분석 대비 폴더별 증감 (이전 DB를 ATTACH해 루트부터 이름으로 짝을 맞춘다). 새로 생긴 폴더는 그 맨 위만 보이고
    /// 그 아래는 내려가지 않는다. <paramref name="shrunk"/>면 줄어든 순.
    /// </summary>
    public IReadOnlyList<GrowthRow> Growth(string previousDbPath, int n, bool shrunk = false)
    {
        Attach(previousDbPath);
        using var cmd = Cmd($"""
            WITH RECURSIVE m(cur, prev, depth) AS (
              SELECT c.id, p.id, 0 FROM main.dir c, prev.dir p WHERE c.parent_id IS NULL AND p.parent_id IS NULL
              UNION ALL
              SELECT c.id, p.id, m.depth + 1 FROM m JOIN main.dir c ON c.parent_id = m.cur
                LEFT JOIN prev.dir p ON p.parent_id = m.prev AND p.name = c.name
              WHERE m.prev IS NOT NULL)
            SELECT m.cur, c.name, c.alloc, COALESCE(p.alloc, 0), m.prev IS NULL
            FROM m JOIN main.dir c ON c.id = m.cur LEFT JOIN prev.dir p ON p.id = m.prev
            WHERE m.depth > 0
            ORDER BY c.alloc - COALESCE(p.alloc, 0) {(shrunk ? "ASC" : "DESC")} LIMIT $n
            """, ("$n", n));
        using var r = cmd.ExecuteReader();
        var rows = new List<GrowthRow>();
        while (r.Read())
        {
            rows.Add(new GrowthRow(r.GetInt64(0), r.GetString(1), "", r.GetInt64(2), r.GetInt64(3), r.GetBoolean(4)));
        }

        return [.. rows.Select(g => g with { Path = PathOf(g.DirId) })];
    }

    /// <summary>지난 스냅샷 전체 크기 (루트).</summary>
    public long PreviousTotal(string previousDbPath)
    {
        Attach(previousDbPath);
        return Convert.ToInt64(Scalar(_c, "SELECT alloc FROM prev.dir WHERE parent_id IS NULL LIMIT 1") ?? 0L, CultureInfo.InvariantCulture);
    }

    private void Attach(string path)
    {
        if (string.Equals(_attached, path, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (_attached is not null)
        {
            using var d = Cmd("DETACH DATABASE prev");
            d.ExecuteNonQuery();
            _attached = null;
        }

        using var cmd = Cmd("ATTACH DATABASE $p AS prev", ("$p", path));
        cmd.ExecuteNonQuery();
        _attached = path;
    }

    public static string Join(string dir, string name) => dir.EndsWith('\\') ? dir + name : dir + "\\" + name;

    private SqliteCommand Cmd(string sql, params (string Name, object Value)[] ps)
    {
        var cmd = _c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in ps)
        {
            cmd.Parameters.AddWithValue(name, value);
        }

        return cmd;
    }

    private static object? Scalar(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        var v = cmd.ExecuteScalar();
        return v is DBNull ? null : v;
    }

    public void Dispose() => _c.Dispose();
}
