namespace Foldy.Core.FileSystem;

/// <summary>Provider 식별자. 경로 문자열 대신 (Provider, 정규화된 경로)를 키로 쓴다 (설계서 5.3).</summary>
public readonly record struct ProviderId(string Value)
{
    public static readonly ProviderId Local = new("local");
    public static readonly ProviderId Sftp = new("sftp");
    public override string ToString() => Value;
}

public readonly record struct FsPath(ProviderId Provider, string Value)
{
    public static FsPath Local(string path) => new(ProviderId.Local, path);
    public override string ToString() => Value;
}

public enum FsKind : byte { File, Folder, Link, Drive }

public enum GitFileStatus : byte { None, Modified, Added, Untracked, Conflicted, Ignored }

/// <summary>목록의 항목 하나. 불변이며 UI 스레드 밖에서 만들어진다.</summary>
public sealed record FsEntry(
    string Name,
    FsPath Path,
    FsKind Kind,
    long Size,
    long? AllocSize,
    DateTime Modified,
    DateTime Created,
    FileAttributes Attributes,
    long? SubtreeSize,
    GitFileStatus Git = GitFileStatus.None);

/// <summary>드라이브 응답 등급. 실측값으로 학습한다.</summary>
public enum DriveSpeedClass : byte { Fast, Slow, Offline }

/// <summary>모든 드라이브는 비동기 스트리밍 Provider 뒤에 있다 (DESIGN.md).</summary>
public interface IFileSystemProvider
{
    ProviderId Id { get; }

    /// <summary>폴더 내용을 묶음(기본 200개 또는 100ms) 단위로 스트리밍한다.</summary>
    IAsyncEnumerable<IReadOnlyList<FsEntry>> ListBatchedAsync(FsPath path, CancellationToken ct);
}
