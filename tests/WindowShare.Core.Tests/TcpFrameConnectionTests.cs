using System.Net;
using System.Net.Sockets;
using WindowShare.Core.Network;
using WindowShare.Core.Protocol;
using Xunit;

namespace WindowShare.Core.Tests;

/// <summary>
/// TCP 帧连接的资源生命周期：对端断开（最常见路径）之后 Dispose 仍必须真正释放资源。
/// 回归 v1.5.2 审计 S6——读循环的 finally 会置 _closed，若拿它当"已释放"的门闩，
/// Dispose 会直接 return，socket / SemaphoreSlim / 发送缓冲全部只等 GC 终结器。
/// </summary>
public class TcpFrameConnectionTests
{
    /// <summary>建立一对已连接的 TCP 客户端（listener 持服务端一端）</summary>
    private static (TcpClient Client, TcpClient Server, TcpListener Listener) ConnectPair()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var client = new TcpClient();
        var connect = client.ConnectAsync(IPAddress.Loopback, port);
        var server = listener.AcceptTcpClient();
        Assert.True(connect.Wait(TimeSpan.FromSeconds(5)), "本机回环连接超时");

        return (client, server, listener);
    }

    [Fact]
    public void Dispose_AfterPeerDisconnect_StillClosesConnection()
    {
        var (client, server, listener) = ConnectPair();
        try
        {
            var conn = new TcpFrameConnection(client);
            var disconnected = new ManualResetEventSlim(false);
            conn.Disconnected += () => disconnected.Set();
            conn.StartReading();

            // 对端断开 → 读循环退出 → _closed = true（历史实现里 Dispose 从此变成空操作）
            server.Close();
            Assert.True(disconnected.Wait(TimeSpan.FromSeconds(5)), "未观察到断开事件");
            Assert.True(conn.IsClosedForTest, "读循环退出后应置位关闭标志");

            // 关键断言：Dispose 必须仍然真正释放（不能抛、且底层客户端被关闭）
            conn.Dispose();
            Assert.False(client.Connected, "Dispose 后底层 TcpClient 应已关闭");
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task Dispose_IsIdempotent_AndMakesSendSafe()
    {
        var (client, _, listener) = ConnectPair();
        try
        {
            var conn = new TcpFrameConnection(client);

            conn.Dispose();
            conn.Dispose();   // 二次释放不得抛异常

            // 释放后发送必须静默丢弃，而不是在已释放的信号量上抛 ObjectDisposedException
            conn.Send(MessageType.Ping, FrameFlags.None, ReadOnlySpan<byte>.Empty);
            await conn.SendAsync(MessageType.Ping, FrameFlags.None, ReadOnlyMemory<byte>.Empty)
                .WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public void IsConnected_BecomesFalseAfterDispose()
    {
        var (client, _, listener) = ConnectPair();
        try
        {
            var conn = new TcpFrameConnection(client);
            conn.Dispose();
            Assert.False(conn.IsConnected);
        }
        finally
        {
            listener.Stop();
        }
    }
}
