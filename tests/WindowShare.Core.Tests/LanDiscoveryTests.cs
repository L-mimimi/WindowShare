using Xunit;
using System.Text;
using WindowShare.Core.Network;

namespace WindowShare.Core.Tests;

/// <summary>
/// LAN 组播发现协议层测试：报文编解码 roundtrip、抗坏数据、条目过期。
/// 传输层（组播收发）由冒烟测试覆盖。
/// </summary>
public class LanDiscoveryTests
{
    [Fact]
    public void Packet_Encode_Decode_Roundtrip()
    {
        var payload = new DiscoveryPayload("办公室-DESKTOP", 48750);
        var decoded = DiscoveryPacket.Decode(DiscoveryPacket.Encode(payload));
        Assert.NotNull(decoded);
        Assert.Equal("办公室-DESKTOP", decoded!.Name);
        Assert.Equal(48750, decoded.Port);
    }

    [Fact]
    public void Packet_StartsWithMagic()
    {
        var packet = DiscoveryPacket.Encode(new DiscoveryPayload("a", 1));
        Assert.Equal(0x57, packet[0]); // 'W'
        Assert.Equal(0x53, packet[1]); // 'S'
        Assert.Equal(0x48, packet[2]); // 'H'
        Assert.Equal(0x31, packet[3]); // '1'
        Assert.Equal(0x44, packet[4]); // 'D'
        Assert.Equal(1, packet[5]);    // 版本
    }

    [Theory]
    [InlineData("garbage-not-a-packet")]
    [InlineData("")]
    public void Packet_Decode_Garbage_ReturnsNull(string content)
    {
        Assert.Null(DiscoveryPacket.Decode(System.Text.Encoding.UTF8.GetBytes(content)));
    }

    [Fact]
    public void Packet_Decode_WrongMagic_ReturnsNull()
    {
        var packet = DiscoveryPacket.Encode(new DiscoveryPayload("a", 1));
        packet[2] = 0x00;   // 破坏 magic
        Assert.Null(DiscoveryPacket.Decode(packet));
    }

    [Fact]
    public void Packet_Decode_WrongVersion_ReturnsNull()
    {
        var packet = DiscoveryPacket.Encode(new DiscoveryPayload("a", 1));
        packet[5] = 99;
        Assert.Null(DiscoveryPacket.Decode(packet));
    }

    [Fact]
    public void Packet_Decode_Truncated_ReturnsNull()
    {
        var packet = DiscoveryPacket.Encode(new DiscoveryPayload("a", 1));
        Assert.Null(DiscoveryPacket.Decode(packet[..5]));   // 只剩 magic
        Assert.Null(DiscoveryPacket.Decode(packet[..6]));   // magic+版本但无 JSON
    }

    [Fact]
    public void Packet_Decode_Oversize_ReturnsNull()
    {
        // Encode 会先裁剪设备名，正常编码不出超限包 → 手工构造：合法头 + 超大 JSON
        var magic = new byte[] { 0x57, 0x53, 0x48, 0x31, 0x44, 0x01 };
        var huge = System.Text.Encoding.UTF8.GetBytes("{" + new string('x', LanDiscovery.MaxPayloadBytes + 10) + "}");
        var packet = magic.Concat(huge).ToArray();
        Assert.Null(DiscoveryPacket.Decode(packet));
    }

    [Fact]
    public void Payload_OverlongName_Clamped()
    {
        var payload = new DiscoveryPayload(new string('A', 100), 48750);
        Assert.Equal(64, payload.ToJson().Length > 0
            ? DiscoveryPacket.Decode(DiscoveryPacket.Encode(payload))!.Name.Length
            : 0);
    }

    [Fact]
    public void Payload_InvalidPort_FallsBackToDefault()
    {
        Assert.Equal(48750, DiscoveryPacket.Decode(DiscoveryPacket.Encode(new DiscoveryPayload("a", 0)))!.Port);
        Assert.Equal(48750, DiscoveryPacket.Decode(DiscoveryPacket.Encode(new DiscoveryPayload("a", 70000)))!.Port);
        Assert.Equal(1234, DiscoveryPacket.Decode(DiscoveryPacket.Encode(new DiscoveryPayload("a", 1234)))!.Port);
    }

    [Fact]
    public void Payload_FromJson_MissingFields_ReturnsNull()
    {
        Assert.Null(DiscoveryPayload.FromJson("{}"));
        Assert.Null(DiscoveryPayload.FromJson("""{"name":1,"port":1}"""));
        Assert.Null(DiscoveryPayload.FromJson("""{"name":"a","port":"x"}"""));
        Assert.Null(DiscoveryPayload.FromJson("not json"));
        Assert.Null(DiscoveryPayload.FromJson("[1,2]"));
    }

    [Fact]
    public void Listener_Process_Accumulates_And_Prunes_Stale()
    {
        var listener = new DiscoveryListener();
        var now = 1_000_000L;
        Assert.True(listener.Process(DiscoveryPacket.Encode(new DiscoveryPayload("A", 100)), "192.168.1.10", now));
        Assert.True(listener.Process(DiscoveryPacket.Encode(new DiscoveryPayload("B", 200)), "192.168.1.11", now));

        // 6 秒内：两个都在
        Assert.Equal(2, listener.Snapshot(now).Count);

        // 超过 6 秒未刷新 → 过期剔除；刷新过的保留
        var later = now + TimeSpan.FromSeconds(7).Ticks;
        listener.Process(DiscoveryPacket.Encode(new DiscoveryPayload("B", 200)), "192.168.1.11", later);
        var item = Assert.Single(listener.Snapshot(later));
        Assert.Equal("192.168.1.11", item.Address);
    }

    [Fact]
    public void Listener_Process_SameHost_UpdatesEntry()
    {
        var listener = new DiscoveryListener();
        var now = 1_000_000L;
        listener.Process(DiscoveryPacket.Encode(new DiscoveryPayload("A", 100)), "10.0.0.5", now);
        listener.Process(DiscoveryPacket.Encode(new DiscoveryPayload("A-renamed", 200)), "10.0.0.5", now + 1);
        var item = Assert.Single(listener.Snapshot(now + 1));
        Assert.Equal("A-renamed", item.Name);
        Assert.Equal(200, item.Port);
    }

    [Fact]
    public void Listener_Process_Garbage_ReturnsFalse()
    {
        var listener = new DiscoveryListener();
        Assert.False(listener.Process(System.Text.Encoding.UTF8.GetBytes("junk"), "1.2.3.4", 1));
    }
}
