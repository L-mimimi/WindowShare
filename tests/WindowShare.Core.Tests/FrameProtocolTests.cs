using WindowShare.Core.Protocol;
using Xunit;

namespace WindowShare.Core.Tests;

public class FrameProtocolTests
{
    [Fact]
    public void Header_Roundtrip_PreservesAllFields()
    {
        var h = new FrameHeader(MessageType.VideoFrame, FrameFlags.Keyframe, 42, 1234567890123L, 777);
        var buf = new byte[FrameHeader.HeaderSize];
        h.Write(buf);

        Assert.True(FrameHeader.TryParse(buf, out var parsed));
        Assert.Equal(MessageType.VideoFrame, parsed.Type);
        Assert.Equal(FrameFlags.Keyframe, parsed.Flags);
        Assert.Equal(42u, parsed.Sequence);
        Assert.Equal(1234567890123L, parsed.TimestampUtc);
        Assert.Equal(777, parsed.PayloadLength);
    }

    [Fact]
    public void Header_RejectsBadMagic()
    {
        var h = new FrameHeader(MessageType.Ping, FrameFlags.None, 1, 1, 0);
        var buf = new byte[FrameHeader.HeaderSize];
        h.Write(buf);
        buf[0] = 0x00; // 破坏魔数
        Assert.False(FrameHeader.TryParse(buf, out _));
    }

    [Fact]
    public void Header_RejectsOversizedPayload()
    {
        var buf = new byte[FrameHeader.HeaderSize];
        new FrameHeader(MessageType.VideoFrame, FrameFlags.None, 1, 1, FrameHeader.MaxPayloadLength + 1)
            .Write(buf);
        Assert.False(FrameHeader.TryParse(buf, out _));
    }

    [Fact]
    public void Header_RejectsUnknownType()
    {
        var buf = new byte[FrameHeader.HeaderSize];
        new FrameHeader((MessageType)0xFF, FrameFlags.None, 1, 1, 0).Write(buf);
        Assert.False(FrameHeader.TryParse(buf, out _));
    }

    [Fact]
    public void BuildFrame_WithPayload_ParsesBack()
    {
        var payload = new byte[] { 1, 2, 3, 4, 5 };
        var frame = FrameHeader.BuildFrame(MessageType.StatsInfo, FrameFlags.None, 7, 999, payload);

        Assert.Equal(FrameHeader.HeaderSize + payload.Length, frame.Length);
        Assert.True(FrameWriter.TryParseFrame(frame, out var header, out var parsedPayload));
        Assert.Equal(MessageType.StatsInfo, header.Type);
        Assert.Equal(999, header.TimestampUtc);
        Assert.True(payload.AsSpan().SequenceEqual(parsedPayload));
    }
}
