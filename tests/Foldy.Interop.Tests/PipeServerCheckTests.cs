using System.IO.Pipes;
using System.Security.Principal;
using Foldy.Indexer.Client;

namespace Foldy.Interop.Tests;

/// <summary>파이프 서버 확인 (보안 리뷰 L3): SYSTEM 서비스나 나 자신만 믿는다.</summary>
public class PipeServerCheckTests
{
    private static readonly SecurityIdentifier Me = WindowsIdentity.GetCurrent().User!;

    [Fact]
    public void System_and_the_same_user_are_trusted_and_others_are_not()
    {
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var other = new SecurityIdentifier("S-1-5-21-1-2-3-1999");
        Assert.True(PipeServerCheck.IsTrustedUser(system, Me));
        Assert.True(PipeServerCheck.IsTrustedUser(Me, Me));
        Assert.False(PipeServerCheck.IsTrustedUser(other, Me));
        Assert.False(PipeServerCheck.IsTrustedUser(other, null));
    }

    [Fact]
    public async Task A_pipe_served_by_this_process_is_trusted()
    {
        var name = $"foldy-check-{Guid.NewGuid():N}";
        await using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        await using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        var wait = server.WaitForConnectionAsync(TestContext.Current.CancellationToken);
        await client.ConnectAsync(TestContext.Current.CancellationToken);
        await wait;
        Assert.True(PipeServerCheck.IsTrusted(client.SafePipeHandle));
    }
}
