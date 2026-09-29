using Vortice.MediaFoundation;

namespace WindowShare.Core.Encoding;

/// <summary>编码器输出设置（动态码率/动态分辨率运行中可调）</summary>
public sealed record EncoderSettings
{
    /// <summary>编码输出宽（必须为偶数，NV12 要求）</summary>
    public required int Width { get; init; }

    /// <summary>编码输出高（必须为偶数）</summary>
    public required int Height { get; init; }

    /// <summary>目标帧率</summary>
    public int Fps { get; init; } = 30;

    /// <summary>目标平均码率（bit/s）</summary>
    public int BitrateBps { get; init; } = 4_000_000;

    /// <summary>关键帧间隔（帧数）；0 表示仅按需（重连/请求时）出关键帧</summary>
    public int GopSize { get; init; } = 120;

    /// <summary>优先硬件编码器（NVENC/QSV/AMF），失败自动回退软件</summary>
    public bool PreferHardware { get; init; } = true;
}

/// <summary>一帧编码结果（H.264 Annex-B 码流，含起始码；一帧 = 一个访问单元）</summary>
public sealed class EncodedVideoFrame
{
    /// <summary>码流数据（Annex-B，起始码 00 00 00 01）</summary>
    public required byte[] Data { get; init; }

    /// <summary>是否关键帧（IDR，可作为解码起点）</summary>
    public required bool Keyframe { get; init; }

    /// <summary>捕获时间戳（DateTime.UtcNow.Ticks，随帧头发给 Viewer 计算延迟）</summary>
    public required long TimestampUtc { get; init; }

    /// <summary>编码输出宽高（可能小于捕获尺寸：动态分辨率）</summary>
    public required int Width { get; init; }
    public required int Height { get; init; }

    /// <summary>负载大小（统计用）</summary>
    public int PayloadSize => Data.Length;
}
