namespace Foldy.Analysis;

/// <summary>
/// 분석 DB 스키마 (설계서 7.3). Rust engine/foldy-index/src/analysis/mod.rs 의 SCHEMA/INDEXES와 글자까지 같아야 한다.
/// 시각 열(started_at, finished_at, mtime, newest_mtime, oldest_mtime)은 모두 FILETIME(UTC 100ns).
/// </summary>
public static class AnalysisSchema
{
    /// <summary>PRAGMA user_version.</summary>
    public const int Version = 1;

    /// <summary>PRAGMA application_id = "Fold".</summary>
    public const int ApplicationId = 0x466F_6C64;

    public const string Tables = """
        CREATE TABLE scan   (id INTEGER PRIMARY KEY, volume TEXT, started_at INTEGER, finished_at INTEGER,
                             usn INTEGER, total_files INTEGER, total_dirs INTEGER, total_alloc INTEGER);
        CREATE TABLE dir    (id INTEGER PRIMARY KEY, parent_id INTEGER, name TEXT, depth INTEGER,
                             size INTEGER, alloc INTEGER,
                             own_files INTEGER, total_files INTEGER, total_dirs INTEGER,
                             newest_mtime INTEGER, oldest_mtime INTEGER, tag TEXT);
        CREATE TABLE file   (id INTEGER PRIMARY KEY, dir_id INTEGER, name TEXT, ext TEXT,
                             size INTEGER, alloc INTEGER, mtime INTEGER, atime INTEGER, attrs INTEGER);
        CREATE TABLE ext_stat (ext TEXT PRIMARY KEY, files INTEGER, size INTEGER, alloc INTEGER);
        CREATE TABLE candidate (id INTEGER PRIMARY KEY, kind TEXT, dir_id INTEGER, file_id INTEGER,
                             reclaim INTEGER, rule TEXT, reason TEXT);
        """;

    /// <summary>삽입이 끝난 뒤 만든다. dir_parent는 (parent_id, name): 트리맵 자식 조회 + 이전 스냅샷과 이름으로 짝 맞추기.</summary>
    public const string Indexes = """
        CREATE INDEX dir_alloc  ON dir(alloc DESC);
        CREATE INDEX dir_files  ON dir(total_files DESC);
        CREATE INDEX dir_parent ON dir(parent_id, name);
        CREATE INDEX file_alloc ON file(alloc DESC);
        CREATE INDEX file_dir   ON file(dir_id);
        CREATE INDEX file_ext   ON file(ext, alloc DESC);
        CREATE INDEX file_mtime ON file(mtime);
        """;
}
