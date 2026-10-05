using WindowShare.Core.Decoding;
using WindowShare.Core.Encoding;
using Xunit;

namespace WindowShare.Core.Tests;

/// <summary>解码器选择链：H.264 → MF；HEVC → MF 探针通过走 MF，否则 FFmpeg 兜底</summary>
public class VideoDecoderFactoryTests
{
    [Fact]
    public void CreateH264_AlwaysMf()
    {
        using var d = VideoDecoderFactory.Create(VideoCodec.H264, DecoderPreference.Auto, hevcMfAvailable: false);
        Assert.Equal(VideoCodec.H264, d.Codec);
        Assert.Equal("MF", d.BackendName);
    }

    [Fact]
    public void CreateHevc_MfProbeFailed_FallsBackToFfmpeg()
    {
        // hevcMfAvailable=false 时 Auto 模式跳过 MF 分支，必然走 FFmpeg（DLL 随测试输出分发）
        // FFmpeg 兜底也不可用的平台（avcodec 缺失/版本不符）才允许抛 InvalidOperationException
        try
        {
            using var d = VideoDecoderFactory.Create(VideoCodec.Hevc, DecoderPreference.Auto, hevcMfAvailable: false);
            Assert.Equal(VideoCodec.Hevc, d.Codec);
            Assert.Equal("FFmpeg", d.BackendName);
        }
        catch (InvalidOperationException)
        {
            Assert.True(FfmpegVideoDecoder.UnavailableReason() != null,
                "抛异常的前提是 FFmpeg 兜底确实不可用");
        }
    }

    [Fact]
    public void CreateHevc_ForcedFfmpeg_AlwaysFfmpeg()
    {
        // 用户显式选择 FFmpeg：即使 MF 探针可用也走软解（用户绕开有缺陷的扩展解码器）
        try
        {
            using var d = VideoDecoderFactory.Create(VideoCodec.Hevc, DecoderPreference.Ffmpeg, hevcMfAvailable: true);
            Assert.Equal("FFmpeg", d.BackendName);
        }
        catch (InvalidOperationException)
        {
            Assert.True(FfmpegVideoDecoder.UnavailableReason() != null);
        }
    }

    [Fact]
    public void CreateH264_ForcedFfmpeg_AlwaysFfmpeg()
    {
        // FFmpeg 也能解 H.264：显式选择时对两种编码都生效
        try
        {
            using var d = VideoDecoderFactory.Create(VideoCodec.H264, DecoderPreference.Ffmpeg, hevcMfAvailable: false);
            Assert.Equal("FFmpeg", d.BackendName);
        }
        catch (InvalidOperationException)
        {
            Assert.True(FfmpegVideoDecoder.UnavailableReason() != null);
        }
    }

    // 注意：刻意不测 Create(Hevc, hevcMfAvailable: true)——该参数的语义是「子进程探针确实通过」，
    // 探针通过 ⇒ 同一构造路径已安全走通；在探针失败的机器上强测会触发扩展 MFT 的原生 AV
    //（进程死亡，不可捕获，测试主机直接崩）。该组合由冒烟 Part4b 在真实应用层覆盖。
}
