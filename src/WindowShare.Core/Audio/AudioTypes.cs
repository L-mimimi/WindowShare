namespace WindowShare.Core.Audio;

/// <summary>
/// 音频流的固定参数。
/// 采集端一律重混为立体声、重采样到 48 kHz 再编码：AAC 编码器与 ADTS 的
/// sampling_frequency_index 对 48 kHz 支持最稳定，Viewer 端因此不需要从码流里
/// 反推格式，两端参数天然一致。
/// </summary>
public static class AudioStreamInfo
{
    /// <summary>统一采样率（Hz）</summary>
    public const int SampleRate = 48000;

    /// <summary>统一声道数（立体声）</summary>
    public const int Channels = 2;

    /// <summary>AAC-LC 目标码率（bit/s）；实际取编码器支持的最接近档位</summary>
    public const int TargetBitrateBps = 128_000;

    /// <summary>MPEG-4 AAC-LC 一个编码帧的样本数（固定 1024）</summary>
    public const int SamplesPerAacFrame = 1024;

    /// <summary>采集/编码的块长（毫秒）。20ms 在延迟与系统调用次数之间取平衡。</summary>
    public const int ChunkMs = 20;

    /// <summary>一个块的帧数（采样点组）</summary>
    public const int ChunkFrames = SampleRate * ChunkMs / 1000;

    /// <summary>一个块的时长（100ns 单位）</summary>
    public const long ChunkTicks = TimeSpan.TicksPerMillisecond * ChunkMs;

    /// <summary>编码格式标识（认证结果里下发，Viewer 据此选解码器）</summary>
    public const string Codec = "aac-adts";
}

/// <summary>一帧编码后的音频（ADTS 封装的 AAC，可独立解码，无需额外带外信息）</summary>
public sealed class EncodedAudioFrame
{
    /// <summary>ADTS 数据（7 字节头 + 裸 AAC 负载）</summary>
    public required byte[] Data { get; init; }

    /// <summary>
    /// 采集时间戳（DateTime.UtcNow.Ticks）。与视频帧同一时钟，是音画同步的基准：
    /// Viewer 以音频播放时钟为主时钟，视频帧按此时间戳对齐后上屏。
    /// </summary>
    public required long TimestampUtc { get; init; }

    public int SampleRate { get; init; } = AudioStreamInfo.SampleRate;

    public int Channels { get; init; } = AudioStreamInfo.Channels;

    /// <summary>负载字节数（统计用）</summary>
    public int PayloadSize => Data.Length;
}

/// <summary>采集到的一段原始 PCM（int16 交织，立体声）</summary>
public sealed class PcmChunk
{
    /// <summary>交织的 int16 样本，长度 = Frames * Channels</summary>
    public required short[] Data { get; init; }

    /// <summary>帧数（采样点组数）</summary>
    public required int Frames { get; init; }

    /// <summary>本块第一个样本的采集时间戳（DateTime.UtcNow.Ticks）</summary>
    public required long TimestampUtc { get; init; }

    /// <summary>是否为补出来的静音块（系统无声音输出时 WASAPI loopback 不产包，需主动补齐保持流连续）</summary>
    public bool IsSilence { get; init; }

    public int SampleRate { get; init; } = AudioStreamInfo.SampleRate;

    public int Channels { get; init; } = AudioStreamInfo.Channels;
}

/// <summary>解码输出的一段 PCM（int16 交织）</summary>
public sealed class DecodedAudioChunk
{
    public required short[] Data { get; init; }

    public required int Frames { get; init; }

    public required int SampleRate { get; init; }

    public required int Channels { get; init; }

    /// <summary>该块第一个样本对应的采集时间戳（DateTime.UtcNow.Ticks）</summary>
    public required long TimestampUtc { get; init; }
}
