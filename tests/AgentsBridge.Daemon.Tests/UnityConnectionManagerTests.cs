using AgentsBridge.Daemon;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentsBridge.Daemon.Tests;

public sealed class UnityConnectionManagerTests
{
    [Fact]
    public async Task SendAsync_WhenUnityIsDisconnected_ReturnsServiceUnavailable()
    {
        UnityConnectionManager manager = new(NullLogger<UnityConnectionManager>.Instance);

        BridgeReply reply = await manager.SendAsync(
            "/status",
            TimeSpan.FromSeconds(1),
            CancellationToken.None);

        Assert.False(reply.Succeeded);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, reply.StatusCode);
        Assert.Equal("unity_disconnected", reply.ErrorCode);
    }

    [Fact]
    public void Snapshot_WhenNoEditorConnected_IsExplicitlyDisconnected()
    {
        UnityConnectionManager manager = new(NullLogger<UnityConnectionManager>.Instance);

        AgentsBridge.Contracts.UnityConnectionSnapshot snapshot = manager.GetSnapshot();

        Assert.False(snapshot.Connected);
        Assert.Null(snapshot.ProjectPath);
        Assert.Null(snapshot.ConnectedAtUtc);
    }
}
