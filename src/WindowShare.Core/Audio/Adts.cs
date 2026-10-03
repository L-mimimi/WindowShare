namespace WindowShare.Core.Audio;

/// <summary>AAC 音频对象类型（ADTS 头里的 profile 字段 = 该值）</summary>
public enum AudioObjectType : byte
{
    /// <summary>AAC Main</summary>
    AacMain = 0,
    /// <summary>AAC-LC（低复杂度，Media Foundation AAC 编码器的默认输出，也是兼容性最好的一档）</summary>
    AacLowComplexity = 1,
    /// <summary>AAC SSR</summary>
    AacScalableSampleRate = 2,
}

/// <summary>
/// ADTS（Audio Data Transport Stream）头封装与解析。
/// Media Foundation 的 AAC 编码器输出裸 AAC 帧（既无起始码也无容器），
/// 直接喂给解码器需要自己补 7 字节 ADTS 头（protection_absent=1，不带 CRC）。
/// 带上 ADTS 后每一帧都能独立解析出采样率/声道数，传输层无需额外的带外协商。
/// </summary>
public static class Adts
{
    /// <summary>ADTS 头长度（protection_absent=1 时为 7 字节；带 CRC 是 9 字节，本实现不使用）</summary>
    public const int HeaderLength = 7;

    /// <summary>frame_length 是 13 位字段，含头部本身，因此单帧负载上限为 8191-7</summary>
    public const int MaxRawLength = 0x1FFF - HeaderLength;

    /// <summary>采样率 → MPEG-4 sampling_frequency_index；不支持时返回 -1</summary>
    public static int SamplingFrequencyIndex(int sampleRate) => sampleRate switch
    {
        96000 => 0,
        88200 => 1,
        64000 => 2,
        48000 => 3,
        44100 => 4,
        32000 => 5,
        24000 => 6,
        22050 => 7,
        16000 => 8,
        12000 => 9,
        11025 => 10,
        8000 => 11,
        7350 => 12,
        _ => -1,
    };

    /// <summary>sampling_frequency_index → 采样率；非法索引返回 0</summary>
    public static int SampleRateFromIndex(int index) => index switch
    {
        0 => 96000,
        1 => 88200,
        2 => 64000,
        3 => 48000,
        4 => 44100,
        5 => 32000,
        6 => 24000,
        7 => 22050,
        8 => 16000,
        9 => 12000,
        10 => 11025,
        11 => 8000,
        12 => 7350,
        _ => 0,
    };

    /// <summary>
    /// 写 7 字节 ADTS 头。frameLength 为「头 + 负载」的总字节数（13 位字段）。
    /// 布局：syncword(12) | ID(1) | layer(2) | protection_absent(1) | profile(2)
    ///       | sampling_frequency_index(4) | private(1) | channel_configuration(3)
    ///       | original(1) | home(1) | copyright_id_bit(1) | copyright_id_start(1)
    ///       | frame_length(13) | adts_buffer_fullness(11) | number_of_aac_frames-1(2)
    /// </summary>
    public static void WriteHeader(Span<byte> header, int frameLength, int sampleRate, int channels,
        AudioObjectType objectType = AudioObjectType.AacLowComplexity)
    {
        if (header.Length < HeaderLength)
            throw new ArgumentException($"ADTS 头需要 {HeaderLength} 字节", nameof(header));
        var freqIndex = SamplingFrequencyIndex(sampleRate);
        if (freqIndex < 0)
            throw new ArgumentOutOfRangeException(nameof(sampleRate), $"采样率 {sampleRate} 没有对应的 ADTS 索引");
        if (channels is < 1 or > 7)
            throw new ArgumentOutOfRangeException(nameof(channels), $"声道数 {channels} 超出 ADTS 支持范围 1-7");
        if (frameLength < HeaderLength || frameLength > 0x1FFF)
            throw new ArgumentOutOfRangeException(nameof(frameLength), $"ADTS 帧总长 {frameLength} 超出 13 位字段范围");

        var profile = (int)objectType;
        header[0] = 0xFF;                                   // syncword 高 8 位
        header[1] = 0xF1;                                   // syncword 低 4 位 | ID=0(MPEG-4) | layer=00 | protection_absent=1
        header[2] = (byte)((profile << 6) | (freqIndex << 2) | (channels >> 2));
        header[3] = (byte)(((channels & 0x03) << 6) | ((frameLength >> 11) & 0x03));
        header[4] = (byte)((frameLength >> 3) & 0xFF);
        header[5] = (byte)(((frameLength & 0x07) << 5) | 0x1F);   // adts_buffer_fullness 高 5 位（0x7FF = VBR）
        header[6] = 0xFC;                                   // buffer_fullness 低 6 位 | 单帧（number_of_aac_frames-1 = 0）
    }

    /// <summary>把一个裸 AAC 帧封成完整的 ADTS 帧（返回新数组）</summary>
    public static byte[] Wrap(ReadOnlySpan<byte> rawAac, int sampleRate, int channels,
        AudioObjectType objectType = AudioObjectType.AacLowComplexity)
    {
        if (rawAac.Length > MaxRawLength)
            throw new ArgumentException($"裸 AAC 帧 {rawAac.Length} 字节超出 ADTS 单帧上限 {MaxRawLength}", nameof(rawAac));

        var result = new byte[HeaderLength + rawAac.Length];
        WriteHeader(result, result.Length, sampleRate, channels, objectType);
        rawAac.CopyTo(result.AsSpan(HeaderLength));
        return result;
    }

    /// <summary>
    /// 剥掉 ADTS 头，返回裸 AAC 负载（RTP 传输不需要头；无有效头时原样返回，容错处理）。
    /// </summary>
    public static byte[] Strip(ReadOnlySpan<byte> adts)
    {
        if (TryReadHeader(adts, out var frameLength, out _, out _) && frameLength > HeaderLength)
            return adts.Slice(HeaderLength, frameLength - HeaderLength).ToArray();
        return adts.ToArray();
    }

    /// <summary>
    /// 解析 ADTS 头。返回 false 表示同步字/字段非法（数据损坏或不是 ADTS），调用方应丢弃该包。
    /// </summary>
    public static bool TryReadHeader(ReadOnlySpan<byte> data, out int frameLength, out int sampleRate, out int channels)
    {
        frameLength = 0;
        sampleRate = 0;
        channels = 0;
        if (data.Length < HeaderLength) return false;
        if (data[0] != 0xFF) return false;
        // 低 4 位同步字必须是 1111，layer 必须是 00，protection_absent 必须是 1（本实现不发 CRC）
        if ((data[1] & 0xF6) != 0xF0) return false;

        sampleRate = SampleRateFromIndex((data[2] >> 2) & 0x0F);
        if (sampleRate <= 0) return false;

        channels = ((data[2] & 0x01) << 2) | ((data[3] >> 6) & 0x03);
        if (channels is < 1 or > 7) return false;

        frameLength = ((data[3] & 0x03) << 11) | (data[4] << 3) | ((data[5] >> 5) & 0x07);
        // 帧长必须自洽：不能小于头部，也不能超过实际收到的字节数（TCP 是可靠流，短了说明已错位）
        return frameLength >= HeaderLength && frameLength <= data.Length;
    }
}
