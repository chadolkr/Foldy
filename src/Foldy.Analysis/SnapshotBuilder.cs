using System.Diagnostics;
using Microsoft.Data.Sqlite;

namespace Foldy.Analysis;

/// <summary>스냅샷에 넣을 폴더. Parent는 <see cref="SnapshotInput.Dirs"/> 위치 (루트 = -1).</summary>
public readonly record struct InputDir(long Id, int Parent, string Name, long Mtime, int Attributes);

/// <summary>스냅샷에 넣을 파일. Dir는 <see cref="SnapshotInput.Dirs"/> 위치.</summary>
public readonly record struct InputFile(long Id, int Dir, string Name, long Size, long Alloc, long Mtime, int Attributes);

/// <summary>폴백 스캐너(또는 시험)가 모은 트리. Dirs[0]이 루트이고 이름은 루트 전체 경로.</summary>
public sealed class SnapshotInput
{
    public required string RootPath { get; init; }

    /// <summary>scan.volume ("E:" 또는 폴더 경로).</summary>
    public required string Volume { get; init; }

    /// <summary>분석 시작 시각 (FILETIME). 규칙의 "N일 넘게" 기준.</summary>
    public long StartedAt { get; init; }

    public List<InputDir> Dirs { get; } = [];

    public List<InputFile> Files { get; } = [];
}

/// <summary>만든 스냅샷 통계.</summary>
public sealed record SnapshotStats(int Dirs, int Files, int Candidates, long TotalAlloc, long DbBytes, TimeSpan Elapsed);

/// <summary>
/// 분석 스냅샷 DB 쓰기 (C# 쪽). Rust foldy-index analysis 모듈과 같은 스키마·집계·규칙·파일 이름을 쓴다.
/// 서비스가 없는 드라이브(USB, 네트워크, FAT)를 폴백 스캐너로 분석할 때와 시험에서 쓴다.
/// 폴더 size/alloc은 파일 합으로 직접 계산한다 (Rust는 색인의 누적값을 쓴다).
/// </summary>
public static class SnapshotBuilder
{
    private const int MaxFileCandidatesPerRule = 5000;
    private const int FileAttributeHidden = 0x2;
    private const int FileAttributeSystem = 0x4;
    private const int FileAttributeReparse = 0x400;

    /// <summary>임시 파일에 쓰고 <c>&lt;키&gt;.&lt;시각&gt;.db</c>로 이름을 바꾼 뒤 보관 정책을 적용한다. 만든 파일 경로를 돌려준다.</summary>
    public static (string Path, SnapshotStats Stats) BuildIntoDirectory(
        SnapshotInput input, string directory, string key, CleanupRules rules, IProgress<AnalysisProgress>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(rules);
        Directory.CreateDirectory(directory);
        foreach (var stale in Directory.EnumerateFiles(directory, key + ".*.tmp"))
        {
            TryDelete(stale);
        }

        var tmp = Path.Combine(directory, $"{key}.{Environment.ProcessId}.{Environment.CurrentManagedThreadId}.tmp");
        SnapshotStats stats;
        try
        {
            stats = Write(tmp, input, rules, progress, ct);
        }
        catch
        {
            TryDelete(tmp);
            throw;
        }

        var t = AnalysisText.FromFileTime(input.StartedAt);
        string target;
        while (File.Exists(target = Path.Combine(directory, AnalysisPaths.SnapshotFileName(key, t))))
        {
            t = t.AddSeconds(1);
        }

        File.Move(tmp, target);
        foreach (var old in AnalysisPaths.RetentionPlan(AnalysisPaths.ListSnapshots(directory, key)))
        {
            TryDelete(old.DbPath); // 열려 있는 파일은 다음 분석 때 다시 시도
        }

        progress?.Report(new AnalysisProgress(1, "완료"));
        return (target, stats);
    }

    /// <summary><paramref name="path"/>에 스냅샷 DB를 새로 쓴다.</summary>
    public static SnapshotStats Write(string path, SnapshotInput input, CleanupRules rules, IProgress<AnalysisProgress>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(rules);
        var sw = Stopwatch.StartNew();
        var dirs = input.Dirs;
        var files = input.Files;
        var nd = dirs.Count;
        if (nd == 0)
        {
            throw new ArgumentException("루트 폴더가 없음", nameof(input));
        }

        progress?.Report(new AnalysisProgress(0.55, "폴더 집계"));
        var agg = Aggregate(input);
        var (tags, candidates) = Classify(input, agg, rules, input.StartedAt);
        ct.ThrowIfCancellationRequested();

        TryDelete(path);
        AnalysisSqlite.EnsureInitialized();
        var cs = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString();
        using (var conn = new SqliteConnection(cs))
        {
            conn.Open();
            Exec(conn, "PRAGMA journal_mode=OFF; PRAGMA synchronous=OFF; PRAGMA locking_mode=EXCLUSIVE; PRAGMA temp_store=MEMORY; PRAGMA cache_size=-65536;");
            Exec(conn, AnalysisSchema.Tables);
            Exec(conn, $"PRAGMA user_version={AnalysisSchema.Version}; PRAGMA application_id={AnalysisSchema.ApplicationId};");
            using (var tx = conn.BeginTransaction())
            {
                progress?.Report(new AnalysisProgress(0.6, "폴더 쓰는 중"));
                using (var cmd = Prepare(conn, tx, "INSERT INTO dir (id,parent_id,name,depth,size,alloc,own_files,total_files,total_dirs,newest_mtime,oldest_mtime,tag) VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12)", 12))
                {
                    for (var k = 0; k < nd; k++)
                    {
                        var d = dirs[k];
                        Set(cmd, d.Id, d.Parent < 0 ? null : dirs[d.Parent].Id, d.Name, agg.Depth[k], agg.Size[k], agg.Alloc[k],
                            agg.OwnFiles[k], agg.TotalFiles[k], agg.TotalDirs[k], agg.Newest[k], agg.Oldest[k], tags[k]);
                        cmd.ExecuteNonQuery();
                    }
                }

                progress?.Report(new AnalysisProgress(0.65, "파일 쓰는 중"));
                var ext = new Dictionary<string, (long Files, long Size, long Alloc)>(StringComparer.Ordinal);
                using (var cmd = Prepare(conn, tx, "INSERT INTO file (id,dir_id,name,ext,size,alloc,mtime,atime,attrs) VALUES ($1,$2,$3,$4,$5,$6,$7,NULL,$8)", 8))
                {
                    for (var k = 0; k < files.Count; k++)
                    {
                        var f = files[k];
                        var e = AnalysisText.Extension(f.Name);
                        Set(cmd, f.Id, dirs[f.Dir].Id, f.Name, e, f.Size, f.Alloc, f.Mtime, f.Attributes & (FileAttributeHidden | FileAttributeSystem | FileAttributeReparse));
                        cmd.ExecuteNonQuery();
                        ext.TryGetValue(e, out var s);
                        ext[e] = (s.Files + 1, s.Size + f.Size, s.Alloc + f.Alloc);
                        if ((k & 0xFFFF) == 0)
                        {
                            ct.ThrowIfCancellationRequested();
                            progress?.Report(new AnalysisProgress(0.65 + (0.25 * k / Math.Max(1, files.Count)), "파일 쓰는 중"));
                        }
                    }
                }

                using (var cmd = Prepare(conn, tx, "INSERT INTO ext_stat (ext,files,size,alloc) VALUES ($1,$2,$3,$4)", 4))
                {
                    foreach (var (e, s) in ext)
                    {
                        Set(cmd, e, s.Files, s.Size, s.Alloc);
                        cmd.ExecuteNonQuery();
                    }
                }

                using (var cmd = Prepare(conn, tx, "INSERT INTO candidate (kind,dir_id,file_id,reclaim,rule,reason) VALUES ($1,$2,$3,$4,$5,$6)", 6))
                {
                    foreach (var c in candidates)
                    {
                        Set(cmd, c.Kind, c.DirId, c.FileId, c.Reclaim, c.Rule, c.Reason);
                        cmd.ExecuteNonQuery();
                    }
                }

                using (var cmd = Prepare(conn, tx, "INSERT INTO scan (id,volume,started_at,finished_at,usn,total_files,total_dirs,total_alloc) VALUES (1,$1,$2,$3,0,$4,$5,$6)", 6))
                {
                    Set(cmd, input.Volume, input.StartedAt, DateTime.UtcNow.ToFileTimeUtc(), (long)files.Count, (long)nd, agg.Alloc[0]);
                    cmd.ExecuteNonQuery();
                }

                tx.Commit();
            }

            ct.ThrowIfCancellationRequested();
            progress?.Report(new AnalysisProgress(0.92, "인덱스 만드는 중"));
            Exec(conn, "PRAGMA threads=4;");
            Exec(conn, AnalysisSchema.Indexes);
            Exec(conn, "PRAGMA analysis_limit=1000; ANALYZE;");
        }

        return new SnapshotStats(nd, files.Count, candidates.Count, agg.Alloc[0], new FileInfo(path).Length, sw.Elapsed);
    }

    internal sealed class Aggregates(int n)
    {
        public int[] Depth { get; } = new int[n];

        /// <summary>깊이 오름차순 폴더 순번 (부모가 항상 앞).</summary>
        public int[] Order { get; } = new int[n];

        public long[] Size { get; } = new long[n];

        public long[] Alloc { get; } = new long[n];

        public long[] OwnFiles { get; } = new long[n];

        public long[] TotalFiles { get; } = new long[n];

        public long[] TotalDirs { get; } = new long[n];

        public long[] Newest { get; } = new long[n];

        public long[] Oldest { get; } = new long[n];
    }

    internal static Aggregates Aggregate(SnapshotInput input)
    {
        var dirs = input.Dirs;
        var n = dirs.Count;
        var a = new Aggregates(n);
        Array.Fill(a.Depth, -1);
        a.Depth[0] = 0;
        var stack = new Stack<int>();
        for (var s = 0; s < n; s++)
        {
            var cur = s;
            while (a.Depth[cur] < 0)
            {
                stack.Push(cur);
                cur = dirs[cur].Parent;
            }

            var d = a.Depth[cur];
            while (stack.Count > 0)
            {
                a.Depth[stack.Pop()] = ++d;
            }
        }

        var order = Enumerable.Range(0, n).ToArray();
        Array.Sort(a.Depth.ToArray(), order);
        order.CopyTo(a.Order, 0);
        Array.Fill(a.Newest, long.MinValue);
        Array.Fill(a.Oldest, long.MaxValue);
        foreach (var f in input.Files)
        {
            var d = f.Dir;
            a.OwnFiles[d]++;
            a.Size[d] += f.Size;
            a.Alloc[d] += f.Alloc;
            if (f.Mtime > 0)
            {
                a.Newest[d] = Math.Max(a.Newest[d], f.Mtime);
                a.Oldest[d] = Math.Min(a.Oldest[d], f.Mtime);
            }
        }

        Array.Copy(a.OwnFiles, a.TotalFiles, n);
        for (var i = n - 1; i >= 0; i--)
        {
            var d = a.Order[i];
            var p = dirs[d].Parent;
            if (p < 0)
            {
                continue;
            }

            a.TotalFiles[p] += a.TotalFiles[d];
            a.TotalDirs[p] += a.TotalDirs[d] + 1;
            a.Size[p] += a.Size[d];
            a.Alloc[p] += a.Alloc[d];
            a.Newest[p] = Math.Max(a.Newest[p], a.Newest[d]);
            a.Oldest[p] = Math.Min(a.Oldest[p], a.Oldest[d]);
        }

        for (var d = 0; d < n; d++)
        {
            if (a.Newest[d] == long.MinValue)
            {
                a.Newest[d] = dirs[d].Mtime;
                a.Oldest[d] = dirs[d].Mtime;
            }
        }

        return a;
    }

    internal sealed record Candidate(string Kind, long DirId, long? FileId, long Reclaim, string Rule, string Reason);

    internal static (string?[] Tags, List<Candidate> Candidates) Classify(SnapshotInput input, Aggregates agg, CleanupRules rules, long now)
    {
        var dirs = input.Dirs;
        var files = input.Files;
        var n = dirs.Count;
        var tags = new string?[n];
        var cands = new List<Candidate>();
        // 규칙의 paths는 볼륨 루트 기준이므로 "C:\Users\me"를 분석해도 상대 경로는 "\Users\me\..."로 만든다.
        // 드라이브가 없는 경로(UNC)는 분석 루트 기준. Rust classify와 같다.
        var trimmed = input.RootPath.TrimEnd('\\');
        var hasDrive = trimmed.Length >= 2 && trimmed[1] == ':';
        var baseRoot = hasDrive ? trimmed[..2] : trimmed;
        var rel = new string[n];
        rel[0] = hasDrive ? trimmed[2..] : "";
        var childDirs = new List<int>?[n];
        var childFiles = new List<int>?[n];
        for (var i = 1; i < n; i++)
        {
            (childDirs[dirs[i].Parent] ??= []).Add(i);
        }

        for (var i = 0; i < files.Count; i++)
        {
            (childFiles[files[i].Dir] ??= []).Add(i);
        }

        IReadOnlyList<string> Siblings(int p)
        {
            var v = new List<string>();
            foreach (var c in childDirs[p] ?? [])
            {
                v.Add(dirs[c].Name.ToLowerInvariant());
            }

            foreach (var f in childFiles[p] ?? [])
            {
                v.Add(files[f].Name.ToLowerInvariant());
            }

            return v;
        }

        var covered = new bool[n];
        for (var i = 1; i < n; i++)
        {
            var d = agg.Order[i];
            var x = dirs[d];
            var p = x.Parent;
            rel[d] = rel[p] + "\\" + x.Name;
            var sys = (x.Attributes & (FileAttributeSystem | FileAttributeReparse)) != 0;
            var v = rules.MatchDir(x.Name, rel[d], baseRoot + rel[d], agg.Alloc[d], agg.Newest[d], sys, now, () => Siblings(p));
            var here = false;
            if (v is not null)
            {
                tags[d] = v.Rule.Tag;
                if (v.Suggest && !covered[p])
                {
                    here = true;
                    cands.Add(new Candidate("dir", x.Id, null, agg.Alloc[d], v.Rule.Id, v.Rule.ReasonText));
                }
            }

            covered[d] = covered[p] || here;
        }

        if (rules.HasFileRules)
        {
            var fileRules = rules.Rules.Where(r => r.Kind == CleanupRuleKind.File && r.Suggest).ToList();
            var anyExt = fileRules.Any(r => r.Extensions.Count == 0);
            var exts = fileRules.SelectMany(r => r.Extensions).ToHashSet(StringComparer.Ordinal);
            var perRule = new SortedDictionary<string, List<Candidate>>(StringComparer.Ordinal);
            foreach (var f in files)
            {
                if (covered[f.Dir])
                {
                    continue;
                }

                var e = AnalysisText.Extension(f.Name);
                if (!anyExt && !exts.Contains(e))
                {
                    continue;
                }

                var relPath = rel[f.Dir] + "\\" + f.Name;
                var sys = (f.Attributes & (FileAttributeSystem | FileAttributeReparse)) != 0;
                if (rules.MatchFile(f.Name, e, relPath, baseRoot + relPath, f.Alloc, f.Mtime, sys, now) is { } r)
                {
                    if (!perRule.TryGetValue(r.Id, out var list))
                    {
                        perRule[r.Id] = list = [];
                    }

                    list.Add(new Candidate("file", dirs[f.Dir].Id, f.Id, f.Alloc, r.Id, r.ReasonText));
                }
            }

            foreach (var list in perRule.Values)
            {
                cands.AddRange(list.OrderByDescending(c => c.Reclaim).Take(MaxFileCandidatesPerRule));
            }
        }

        return (tags, cands);
    }

    private static void Exec(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static SqliteCommand Prepare(SqliteConnection c, SqliteTransaction tx, string sql, int n)
    {
        var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        for (var i = 1; i <= n; i++)
        {
            cmd.Parameters.Add(new SqliteParameter("$" + i.ToString(System.Globalization.CultureInfo.InvariantCulture), null));
        }

        cmd.Prepare();
        return cmd;
    }

    private static void Set(SqliteCommand cmd, params object?[] values)
    {
        for (var i = 0; i < values.Length; i++)
        {
            cmd.Parameters[i].Value = values[i] ?? DBNull.Value;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 열려 있으면 다음에
        }
    }
}
