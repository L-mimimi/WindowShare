using System.Buffers.Binary;
using System.Buffers.Text;

namespace WindowShare.Core.Protocol;

/// <summary>
/// 帧头（定长 24 字节，小端序）：
/// [4] Magic 'WSH1' | [2] HeaderSize | [1] Type | [1] Flags | [4] Sequence
/// | [8] TimestampUtc (DateTime.UtcNow.Ticks, 100ns) | [4] PayloadLength
/// 所有传输（TCP/回环测试）共用此格式；TCP 上前缀帧头，WebRTC 上走 DataChannel 时同样可用。
/// </summary>
public readonly record struct FrameHeader(
    MessageType Type,
    FrameFlags Flags,
    uint Sequence,
    long TimestampUtc,
    int PayloadLength)
{
    /// <summary>魔数 'WSH1'（字节序 W,S,H,1）</summary>
    public const uint Magic = 0x31485357u;

    /// <summary>帧头大小（字节）</summary>
    public const int HeaderSize = 24;

    /// <summary>单帧负载上限 8MB（1080p 关键帧远小于此值；防御异常输入）</summary>
    public const int MaxPayloadLength = 8 * 1024 * 1024;

    /// <summary>序列化为帧头字节（buffer 长度须 >= 24）</summary>
    public void Write(Span<byte> buffer)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(buffer[..4], Magic);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.Slice(4, 2), HeaderSize);
        buffer[6] = (byte)Type;
        buffer[7] = (byte)Flags;
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.Slice(8, 4), Sequence);
        BinaryPrimitives.WriteInt64LittleEndian(buffer.Slice(12, 8), TimestampUtc);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.Slice(20, 4), PayloadLength);
    }

    /// <summary>
    /// 从字节流解析帧头。返回 false 表示魔数/长度非法（流已错位或被攻击），调用方必须断开连接。
    /// </summary>
    public static bool TryParse(ReadOnlySpan<byte> buffer, out FrameHeader header)
    {
        header = default;
        if (buffer.Length < HeaderSize) return false;
        if (BinaryPrimitives.ReadUInt32LittleEndian(buffer[..4]) != Magic) return false;
        var headerSize = BinaryPrimitives.ReadUInt16LittleEndian(buffer.Slice(4, 2));
        if (headerSize != HeaderSize) return false;

        var payloadLen = BinaryPrimitives.ReadInt32LittleEndian(buffer.Slice(20, 4));
        if (payloadLen < 0 || payloadLen > MaxPayloadLength) return false;

        header = new FrameHeader(
            (MessageType)buffer[6],
            (FrameFlags)buffer[7],
            BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(8, 4)),
            BinaryPrimitives.ReadInt64LittleEndian(buffer.Slice(12, 8)),
            payloadLen);
        // 类型必须是已知值，防止未来版本错位
        return Enum.IsDefined(typeof(MessageType), header.Type);
    }

    /// <summary>构造一个完整帧（帧头 + 负载）</summary>
    public static byte[] BuildFrame(MessageType type, FrameFlags flags, uint seq,
        long timestampUtc, ReadOnlySpan<byte> payload)
    {
        var frame = new byte[HeaderSize + payload.Length];
        new FrameHeader(type, flags, seq, timestampUtc, payload.Length).Write(frame);
        payload.CopyTo(frame.AsSpan(HeaderSize));
        return frame;
    }

    /// <summary>构造仅含帧头时间的控制帧</summary>
    public static byte[] BuildControl(MessageType type, uint seq, long timestampUtc) =>
        BuildFrame(type, FrameFlags.None, seq, timestampUtc, ReadOnlySpan<byte>.Empty);
}

/// <summary>
/// 基于 Memory{byte} 的帧写入/读取辅助：TCP 发送时先写 4 字节长度前缀再写帧？
/// —— 不需要：帧头自带 PayloadLength，读取方按 HeaderSize → PayloadLength 两段读取即可。
/// 本类提供把多个字节块粘成一次发送的缓冲（减少小包）。
/// </summary>
public static class FrameWriter
{
    /// <summary>组装帧并返回可发送的连续缓冲</summary>
    public static byte[] Create(MessageType type, FrameFlags flags, uint seq, long ts, ReadOnlySpan<byte> payload)
        => FrameHeader.BuildFrame(type, flags, seq, ts, payload);

    /// <summary>解析一个完整帧（头部 + 负载都已收到）</summary>
    public static bool TryParseFrame(ReadOnlySpan<byte> frame, out FrameHeader header, out ReadOnlySpan<byte> payload)
    {
        if (!FrameHeader.TryParse(frame, out header)) { payload = default; return false; }
        payload = frame.Slice(FrameHeader.HeaderSize, header.PayloadLength);
        return true;
    }
}
