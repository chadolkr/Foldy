namespace Foldy.Providers.Sftp;

public enum SftpAuthKind { Password, KeyFile }

/// <summary>접속에 쓸 자격 증명(메모리 안의 평문). 디스크에는 <see cref="SftpProfile"/>이 DPAPI로 암호화해 저장한다.</summary>
public sealed record SftpAuth(SftpAuthKind Kind, string? Password = null, string? KeyFilePath = null, string? Passphrase = null);

/// <summary>목록 항목 하나(SSH.NET 타입을 Provider 밖으로 노출하지 않기 위한 값).</summary>
public sealed record SftpEntryInfo(
    string Name,
    bool IsDirectory,
    bool IsSymbolicLink,
    long Size,
    DateTime ModifiedUtc);

/// <summary>호스트 키 확인 결과. UI가 Unknown/Changed일 때 사용자에게 묻는다.</summary>
public enum HostKeyStatus { Match, Unknown, Changed }

public enum HostKeyDecision { Reject, AcceptOnce, AcceptAndSave }

/// <summary>UI 콜백에 넘기는 호스트 키 정보.</summary>
public sealed record HostKeyCheck(string Host, int Port, string KeyType, string FingerprintSha256, HostKeyStatus Status, byte[] KeyBlob);

/// <summary>연결 생성 요청. 호스트 키 핸들러가 null이면 알 수 없거나 바뀐 키는 거부한다.</summary>
public sealed record SftpConnectionRequest(
    SftpLocation Location,
    SftpAuth Auth,
    Func<HostKeyCheck, HostKeyDecision>? HostKeyHandler);

/// <summary>연결이 끊긴 것을 나타낸다. 재연결 후 재시도 대상.</summary>
public sealed class SftpConnectionLostException : IOException
{
    public SftpConnectionLostException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>SSH.NET 위의 얇은 추상화. 테스트에서는 가짜 구현을 쓴다. 호출은 모두 동기·블로킹이며 큐 스레드에서 실행된다.</summary>
public interface ISftpClient : IDisposable
{
    bool IsConnected { get; }

    void Connect(CancellationToken ct);

    /// <summary>폴더 내용을 지연 열거한다("."/".." 포함 가능, Provider가 걸러낸다).</summary>
    IEnumerable<SftpEntryInfo> ListDirectory(string path, CancellationToken ct);

    Stream OpenRead(string path);

    void Download(string path, Stream destination, Action<long> progress, CancellationToken ct);

    void Upload(Stream source, string path, Action<long> progress, CancellationToken ct);

    /// <summary>경로의 정보. 없으면 null.</summary>
    SftpEntryInfo? Stat(string path);

    void CreateDirectory(string path);

    void DeleteFile(string path);

    /// <summary>빈 폴더만 지운다(재귀는 호출자가 한다).</summary>
    void DeleteDirectory(string path);

    /// <summary>같은 서버 안에서 이름·위치를 바꾼다(대상이 있으면 실패).</summary>
    void Rename(string source, string target);
}

public interface ISftpClientFactory
{
    ISftpClient Create(SftpConnectionRequest request);
}
