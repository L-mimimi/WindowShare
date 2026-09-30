using WindowShare.Core.Audio;
using Xunit;

namespace WindowShare.Core.Tests;

/// <summary>
/// ADTS 头封装/解析。裸 AAC 帧没有自描述信息，头写错一个位观看端就整个解不出来，
/// 这里的期望字节是按 ISO/IEC 14496-3 的字段布局手算的。
/// </summary>
public class AdtsTests
{
    [Fact]
    public void Wrap_48k_Stereo_ProducesExpectedBytes()
    {
        var raw = new byte[100];
        var adts = Adts.Wrap(raw, 48000, 2);

        Assert.Equal(Adts.HeaderLength + raw.Length, adts.Length);
        // frame_length = 107 = 0b0000_0110_1011
        Assert.Equal(
            new byte[] { 0xFF, 0xF1, 0x4C, 0x80, 0x0D, 0x7F, 0xFC },
            adts[..Adts.HeaderLength]);
        Assert.Equal(raw, adts[Adts.HeaderLength..]);
    }

    [Fact]
    public void Wrap_44100_UsesFrequencyIndex4()
    {
        var adts = Adts.Wrap(new byte[10], 44100, 2);
        Assert.Equal(4, (adts[2] >> 2) & 0x0F);
    }

    [Fact]
    public void Wrap_5dot1_UsesChannelConfig6()
    {
        var adts = Adts.Wrap(new byte[10], 48000, 6);
        var channelConfig = ((adts[2] & 0x01) << 2) | ((adts[3] >> 6) & 0x03);
        Assert.Equal(6, channelConfig);
    }

    [Theory]
    [InlineData(96000, 0)]
    [InlineData(48000, 3)]
    [InlineData(44100, 4)]
    [InlineData(8000, 11)]
    [InlineData(7350, 12)]
    public void SamplingFrequencyIndex_RoundTrips(int rate, int index)
    {
        Assert.Equal(index, Adts.SamplingFrequencyIndex(rate));
        Assert.Equal(rate, Adts.SampleRateFromIndex(index));
    }

    [Fact]
    public void SamplingFrequencyIndex_RejectsUnknownRate()
    {
        Assert.Equal(-1, Adts.SamplingFrequencyIndex(12345));
        Assert.Equal(0, Adts.SampleRateFromIndex(15));
        Assert.Throws<ArgumentOutOfRangeException>(() => Adts.Wrap(new byte[10], 12345, 2));
    }

    [Fact]
    public void TryReadHeader_ParsesWhatWrapWrote()
    {
        var adts = Adts.Wrap(new byte[300], 48000, 2);
        Assert.True(Adts.TryReadHeader(adts, out var length, out var rate, out var channels));
        Assert.Equal(adts.Length, length);
        Assert.Equal(48000, rate);
        Assert.Equal(2, channels);
    }

    [Theory]
    [InlineData(new byte[] { 0x00, 0xF1, 0x4C, 0x80, 0x00, 0x1F, 0xFC })]  // 同步字错
    [InlineData(new byte[] { 0xFF, 0xF0, 0x4C, 0x80, 0x00, 0x1F, 0xFC })]  // protection_absent=0（带 CRC，本实现不支持）
    [InlineData(new byte[] { 0xFF, 0xF1, 0x4C, 0x80, 0x00 })]              // 太短
    public void TryReadHeader_RejectsInvalid(byte[] data)
    {
        Assert.False(Adts.TryReadHeader(data, out _, out _, out _));
    }

    [Fact]
    public void TryReadHeader_RejectsFrameLongerThanBuffer()
    {
        // 头声明 107 字节，但只给了 20 字节：TCP 是可靠流，短了说明已错位，必须丢
        var adts = Adts.Wrap(new byte[100], 48000, 2);
        Assert.False(Adts.TryReadHeader(adts[..20], out _, out _, out _));
    }

    [Fact]
    public void Wrap_RejectsOversizedRawFrame()
    {
        Assert.Throws<ArgumentException>(() => Adts.Wrap(new byte[Adts.MaxRawLength + 1], 48000, 2));
    }

    [Fact]
    public void Wrap_RejectsInvalidChannelCount()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Adts.Wrap(new byte[10], 48000, 8));
        Assert.Throws<ArgumentOutOfRangeException>(() => Adts.Wrap(new byte[10], 48000, 0));
    }
}

/// <summary>PCM 格式转换：WASAPI 混音格式（float32、任意声道/采样率）与 AAC 要求的 48kHz 立体声 int16 之间的桥。</summary>
public class PcmConvertTests
{
    [Fact]
    public void Float32ToInt16_ClampsInsteadOfWrapping()
    {
        // 溢出翻转会把 +1.5 变成负值，听感是一声爆响；必须饱和削顶
        var src = new float[] { 0f, 1f, -1f, 2.5f, -2.5f, float.NaN, 0.5f };
        var dst = new short[src.Length];
        PcmConvert.Float32ToInt16(src, dst);

        Assert.Equal(0, dst[0]);
        Assert.Equal(short.MaxValue, dst[1]);
        Assert.Equal(short.MinValue, dst[2]);
        Assert.Equal(short.MaxValue, dst[3]);
        Assert.Equal(short.MinValue, dst[4]);
        Assert.Equal(0, dst[5]);
        Assert.Equal(16384, dst[6]);
    }

    [Fact]
    public void Int16ToFloat32_RoundTrips()
    {
        var src = new short[] { 0, 32767, -32768, 16384 };
        var f = new float[src.Length];
        var back = new short[src.Length];
        PcmConvert.Int16ToFloat32(src, f);
        PcmConvert.Float32ToInt16(f, back);

        Assert.Equal(0, back[0]);
        Assert.Equal(32767, back[1]);
        Assert.Equal(-32768, back[2]);
        Assert.Equal(16384, back[3]);
    }

    [Fact]
    public void Remix_MonoToStereo_DuplicatesChannel()
    {
        var src = new float[] { 0.5f, -0.5f };
        var dst = new short[4];
        var frames = PcmConvert.RemixFloat32ToInt16Stereo(src, 1, dst);

        Assert.Equal(2, frames);
        Assert.Equal(dst[0], dst[1]);
        Assert.Equal(dst[2], dst[3]);
        Assert.Equal(16384, dst[0]);
    }

    [Fact]
    public void Remix_SurroundDownmix_StaysInRange()
    {
        // 5.1 全声道满幅直接相加会溢出翻转成负值（听感是一声爆响）；
        // 等功率混音（×0.7071）+ 饱和削顶后必须仍落在 int16 正半区
        var src = new float[6];
        for (var i = 0; i < 6; i++) src[i] = 1f;
        var dst = new short[2];
        var frames = PcmConvert.RemixFloat32ToInt16Stereo(src, 6, dst);

        Assert.Equal(1, frames);
        Assert.InRange(dst[0], (short)1, short.MaxValue);
        Assert.InRange(dst[1], (short)1, short.MaxValue);
    }

    [Fact]
    public void Resample_SameRate_IsIdentity()
    {
        var src = new short[] { 1, 2, 3, 4 };
        Assert.Equal(src, PcmConvert.ResampleStereoInt16(src, 48000, 48000));
    }

    [Fact]
    public void Resample_HalvesFrameCount_WhenHalvingRate()
    {
        // 100 帧立体声 48k → 24k，应得 50 帧
        var src = new short[200];
        var dst = PcmConvert.ResampleStereoInt16(src, 48000, 24000);
        Assert.Equal(100, dst.Length);
    }

    [Fact]
    public void Resample_PreservesDcLevel()
    {
        // 线性插值对直流分量必须无偏：否则重采样会引入偏移（听感是「咔」一声）
        var src = new short[960 * 2];
        Array.Fill(src, (short)10000);
        var dst = PcmConvert.ResampleStereoInt16(src, 48000, 44100);

        Assert.NotEmpty(dst);
        foreach (var s in dst) Assert.InRange(s, (short)9990, (short)10010);
    }

    [Fact]
    public void StereoInt16ToFloat32_MonoDevice_MixesDown()
    {
        var src = new short[] { 16384, -16384 };   // 左 +0.5，右 -0.5
        var dst = new float[1];
        var frames = PcmConvert.StereoInt16ToFloat32(src, 1, dst, 1);

        Assert.Equal(1, frames);
        Assert.Equal(0f, dst[0], 3);
    }

    [Fact]
    public void StereoInt16ToFloat32_SurroundDevice_FillsExtraChannelsWithSilence()
    {
        var src = new short[] { 16384, 16384 };
        var dst = new float[6];
        var frames = PcmConvert.StereoInt16ToFloat32(src, 1, dst, 6);

        Assert.Equal(1, frames);
        Assert.Equal(0.5f, dst[0], 3);
        Assert.Equal(0.5f, dst[1], 3);
        Assert.Equal(0f, dst[2]);
        Assert.Equal(0f, dst[5]);
    }

    [Fact]
    public void RmsLevel_FullScaleSquare_IsOne()
    {
        var samples = new short[1000];
        for (var i = 0; i < samples.Length; i++) samples[i] = (short)(i % 2 == 0 ? 32767 : -32768);
        Assert.InRange(PcmConvert.RmsLevel(samples), 0.99f, 1.01f);
        Assert.Equal(0f, PcmConvert.RmsLevel(new short[100]));
        Assert.Equal(0f, PcmConvert.RmsLevel(ReadOnlySpan<short>.Empty));
    }
}

/// <summary>
/// 「绝对样本序号 → 采集时间戳」映射。AAC 输入 20ms 一块、输出固定 1024 样本一块，
/// 边界不对齐，只能按样本序号定位时间戳，否则音画同步会持续漂移。
/// </summary>
public class SampleTimelineTests
{
    [Fact]
    public void NoMarks_ReturnsFalse()
    {
        var timeline = new SampleTimeline(48000);
        Assert.False(timeline.TryGetUtc(0, out _));
        Assert.Equal(0, timeline.UtcFor(12345));
        Assert.Equal(0, timeline.Count);
    }

    [Fact]
    public void InterpolatesBetweenMarks()
    {
        var timeline = new SampleTimeline(48000);
        timeline.Mark(0, 1_000_000L);
        timeline.Mark(48000, 1_000_000L + TimeSpan.TicksPerSecond);

        // 半秒 = 24000 样本
        Assert.True(timeline.TryGetUtc(24000, out var utc));
        Assert.Equal(1_000_000L + TimeSpan.TicksPerSecond / 2, utc);
    }

    [Fact]
    public void ExtrapolatesBackwardsBeforeFirstMark()
    {
        var timeline = new SampleTimeline(48000);
        timeline.Mark(1024, 5_000_000L);

        // 编解码器的 priming 会先吐出比第一个标记更早的样本，必须能向前外推
        Assert.True(timeline.TryGetUtc(0, out var utc));
        Assert.Equal(5_000_000L - 1024 * TimeSpan.TicksPerSecond / 48000, utc);
    }

    [Fact]
    public void ExtrapolatesForwardAfterLastMark()
    {
        var timeline = new SampleTimeline(48000);
        timeline.Mark(0, 1_000L);
        Assert.True(timeline.TryGetUtc(96000, out var utc));
        Assert.Equal(1_000L + 2 * TimeSpan.TicksPerSecond, utc);
    }

    [Fact]
    public void OutOfOrderMarksAreIgnored()
    {
        var timeline = new SampleTimeline(48000);
        timeline.Mark(1000, 7_000_000L);
        timeline.Mark(500, 1L);       // 回退标记：忽略，否则时间线会被搞乱
        timeline.Mark(1000, 2L);      // 重复标记：忽略

        Assert.Equal(1, timeline.Count);
        Assert.Equal(7_000_000L, timeline.UtcFor(1000));
    }

    [Fact]
    public void LongRunning_TrimKeepsRecentMarksAndStaysAccurate()
    {
        var timeline = new SampleTimeline(48000);
        for (var i = 0; i < 5000; i++)
            timeline.Mark((long)i * 960, 1_000L + i * 960 * TimeSpan.TicksPerSecond / 48000);

        Assert.True(timeline.Count <= 512);
        // 修剪后仍能正确定位（保留的标记足够密）
        var last = 4999L * 960;
        Assert.True(timeline.TryGetUtc(last, out var utc));
        Assert.Equal(1_000L + last * TimeSpan.TicksPerSecond / 48000, utc);
    }

    [Fact]
    public void Reset_ClearsMarks()
    {
        var timeline = new SampleTimeline(48000);
        timeline.Mark(0, 1L);
        timeline.Reset();
        Assert.False(timeline.TryGetUtc(0, out _));
    }

    [Fact]
    public void RejectsInvalidSampleRate()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SampleTimeline(0));
    }
}

/// <summary>
/// 音画同步时钟：视频以音频播放时钟为基准，早了就等、到点就上屏；
/// 没有音频（或音频断流）时必须退回「立即上屏」，不能把画面一起卡死。
/// </summary>
public class AvSyncClockTests
{
    [Fact]
    public void WithoutAudio_AlwaysPresents()
    {
        Assert.Equal(VideoPresentDecision.Present, AvSyncClock.Decide(long.MaxValue / 2, null));
    }

    [Fact]
    public void FrameFarAheadOfAudio_Waits()
    {
        var audio = DateTime.UtcNow.Ticks;
        var frame = audio + TimeSpan.TicksPerMillisecond * 200;
        Assert.Equal(VideoPresentDecision.Wait, AvSyncClock.Decide(frame, audio));
    }

    [Fact]
    public void FrameWithinThreshold_Presents()
    {
        var audio = DateTime.UtcNow.Ticks;
        Assert.Equal(VideoPresentDecision.Present,
            AvSyncClock.Decide(audio + AvSyncClock.MaxEarlyTicks - 1, audio));
    }

    [Fact]
    public void FrameBehindAudio_PresentsImmediately()
    {
        // 画面落后于声音时不再等待（丢弃会让屏幕共享丢内容），立刻上屏
        var audio = DateTime.UtcNow.Ticks;
        Assert.Equal(VideoPresentDecision.Present,
            AvSyncClock.Decide(audio - TimeSpan.TicksPerSecond, audio));
    }

    [Fact]
    public void Clock_IsInvalidBeforeFirstUpdate()
    {
        var clock = new AvSyncClock();
        Assert.Null(clock.GetAudioUtcTicks());
        Assert.False(clock.IsActive);
    }

    [Fact]
    public void Clock_ExtrapolatesBetweenUpdates()
    {
        var clock = new AvSyncClock();
        var baseUtc = DateTime.UtcNow.Ticks;
        clock.Update(baseUtc);
        Thread.Sleep(50);

        var now = clock.GetAudioUtcTicks();
        Assert.NotNull(now);
        // 50ms 后外推值应至少前进 40ms（留一点调度抖动余量）
        Assert.True(now!.Value - baseUtc >= TimeSpan.TicksPerMillisecond * 40,
            $"外推只前进了 {(now.Value - baseUtc) / (double)TimeSpan.TicksPerMillisecond:F1}ms");
    }

    [Fact]
    public void Clock_ResetMakesItInvalid()
    {
        var clock = new AvSyncClock();
        clock.Update(DateTime.UtcNow.Ticks);
        Assert.True(clock.IsActive);
        clock.Reset();
        Assert.False(clock.IsActive);
    }

    [Fact]
    public void OffsetMs_IsPositiveWhenVideoIsEarly()
    {
        var audio = 1_000_000L;
        var frame = audio + TimeSpan.TicksPerMillisecond * 25;
        Assert.Equal(25.0, AvSyncClock.OffsetMs(frame, audio), 3);
        Assert.True(double.IsNaN(AvSyncClock.OffsetMs(frame, null)));
    }
}

/// <summary>音频流参数常量：两端都按这套参数建编解码器，改动会同时影响 Host 与 Viewer。</summary>
public class AudioStreamInfoTests
{
    [Fact]
    public void ChunkSizes_AreSelfConsistent()
    {
        Assert.Equal(AudioStreamInfo.SampleRate * AudioStreamInfo.ChunkMs / 1000, AudioStreamInfo.ChunkFrames);
        Assert.Equal(TimeSpan.TicksPerMillisecond * AudioStreamInfo.ChunkMs, AudioStreamInfo.ChunkTicks);
        Assert.Equal(960, AudioStreamInfo.ChunkFrames);   // 20ms @48kHz
    }

    [Fact]
    public void StreamFormat_IsAdtsFriendly()
    {
        Assert.True(Adts.SamplingFrequencyIndex(AudioStreamInfo.SampleRate) >= 0);
        Assert.InRange(AudioStreamInfo.Channels, 1, 7);
        Assert.Equal("aac-adts", AudioStreamInfo.Codec);
    }
}
