namespace Foldy.Providers.Sftp;

/// <summary>
/// SSH.NET의 HostKeyReceived 이벤트에서 쓰는 판정 로직. known_hosts 확인 후
/// Match면 통과, Unknown/Changed면 UI 콜백(<see cref="HostKeyDecision"/>)에 맡긴다.
/// 콜백이 없으면 거부한다. 키가 Changed일 때 AcceptAndSave는 기존 항목을 지우고 새 키를 기록한다.
/// </summary>
public sealed class HostKeyVerifier(KnownHostsFile knownHosts, Func<HostKeyCheck, HostKeyDecision>? handler)
{
    public bool Verify(string host, int port, byte[] keyBlob)
    {
        HostKeyStatus status;
        try
        {
            status = knownHosts.Check(host, port, keyBlob);
        }
        catch (IOException)
        {
            status = HostKeyStatus.Unknown; // 읽을 수 없으면 새 호스트로 취급해 사용자에게 묻는다.
        }

        if (status == HostKeyStatus.Match)
        {
            return true;
        }

        if (handler is null)
        {
            return false;
        }

        var check = new HostKeyCheck(host, port, KnownHostsFile.KeyTypeOf(keyBlob), KnownHostsFile.FingerprintSha256(keyBlob), status, keyBlob);
        switch (handler(check))
        {
            case HostKeyDecision.AcceptOnce:
                return true;
            case HostKeyDecision.AcceptAndSave:
                if (status == HostKeyStatus.Changed)
                {
                    knownHosts.Remove(host, port, check.KeyType);
                }

                knownHosts.Add(host, port, keyBlob);
                return true;
            default:
                return false;
        }
    }
}
