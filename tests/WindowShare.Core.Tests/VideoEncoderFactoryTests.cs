using WindowShare.Core.Decoding;
using WindowShare.Core.Encoding;
using Xunit;

namespace WindowShare.Core.Tests;

/// <summary>编码器选择链（to1.5.0 Step 3）：FFmpeg 厂商硬编（nvenc→amf→qsv，真实 CBR）→ MF 现链</summary>
public class VideoEncoderFactoryTests
{
    private static EncoderSettings Settings(VideoCodec codec = VideoCodec.H264) => new()
    {
        Codec = codec,
        Width = 640,
        Height = 360,
        Fps = 30,
        BitrateBps = 2_000_000,
    };

    [Fact]
    public void Create_HardwarePreferred_FfmpegVendorWhenAvailable_ElseMf()
    {
        // 有厂商 GPU 的机器走 FFmpeg 封装（Step 0：h264_nvenc 码率贴合 100%）；
        // 无厂商硬件的机器（CI runner 等）自动回退 MF 现链，行为与 1.4.x 一致
        using var enc = VideoEncoderFactory.Create(Settings());
        if (FfmpegVideoEncoder.ProbeAvailable(Settings()))
            Assert.IsType<FfmpegVideoEncoder>(enc);
        else
            Assert.IsType<MfVideoEncoder>(enc);
    }

    [Fact]
    public void Create_SoftwarePreferred_SkipsFfmpegVendorChain()
    {
        // PreferHardware=false：显式软件/系统路径，不得选厂商封装
        using var enc = VideoEncoderFactory.Create(Settings() with { PreferHardware = false });
        Assert.IsType<MfVideoEncoder>(enc);
        Assert.False(enc.IsD3DAccelerated || enc is FfmpegVideoEncoder);
    }

    [Fact]
    public void Create_HevcChain_CandidateNameMatchesCodec_WhenVendorAvailable()
    {
        var settings = Settings(VideoCodec.Hevc);
        if (!FfmpegVideoEncoder.ProbeAvailable(settings))
        {
            // 本机无厂商 HEVC 硬编：工厂应回退 MF，且 MF 也没有 HEVC 编码器时抛异常（现状语义）
            try
            {
                using var enc = VideoEncoderFactory.Create(settings);
                Assert.IsType<MfVideoEncoder>(enc);
            }
            catch (InvalidOperationException)
            {
                return; // 两链都不可用：允许（与 1.4.x 行为一致）
            }
            return;
        }

        using var ff = VideoEncoderFactory.Create(settings);
        var ffmpeg = Assert.IsType<FfmpegVideoEncoder>(ff);
        Assert.StartsWith("hevc_", ffmpeg.EncoderName);
    }

    [Fact]
    public void ProbeAvailable_SoftwarePreferred_ReturnsMfProbeOnly()
    {
        // 探针与创建链一致：PreferHardware=false 时只看 MF
        Assert.Equal(MfVideoEncoder.ProbeAvailable(Settings() with { PreferHardware = false }),
                     VideoEncoderFactory.ProbeAvailable(Settings() with { PreferHardware = false }));
    }
}
