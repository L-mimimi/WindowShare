using WindowShare.Core.Encoding;
using Xunit;

namespace WindowShare.Core.Tests;

/// <summary>
/// 分辨率/帧率/码率推算：等比缩放（不拉伸）、不上采样、4K 档位、帧率档位、码率单调性与 H.264 Level 推导。
/// </summary>
public class VideoFormatPlannerTests
{
    [Fact]
    public void FpsTiers_AreExactly24_30_60_90_120_144()
    {
        Assert.Equal(new[] { 24, 30, 60, 90, 120, 144 }, VideoFormatPlanner.FpsTiers);
    }

    [Fact]
    public void MaxPreset_Is4K()
    {
        Assert.Equal(3840, VideoFormatPlanner.MaxPresetWidth);
    }

    [Theory]
    [InlineData(3840, 2160, 3840, 3840, 2160)]   // 4K 源 + 4K 档 → 原生输出
    [InlineData(3840, 2160, 1920, 1920, 1080)]   // 4K 源降到 1080p
    [InlineData(1920, 1080, 1920, 1920, 1080)]   // 1080p 源 + 1080p 档
    [InlineData(2560, 1440, 3840, 2560, 1440)]   // 2K 源选 4K 档 → 不上采样
    [InlineData(1280, 720, 1920, 1280, 720)]     // 720p 源选 1080p 档 → 不上采样
    public void FitToWidth_ScalesWithoutUpscaling(int srcW, int srcH, int target, int expW, int expH)
    {
        var (w, h) = VideoFormatPlanner.FitToWidth(srcW, srcH, target);
        Assert.Equal(expW, w);
        Assert.Equal(expH, h);
    }

    [Fact]
    public void FitToWidth_PreservesAspectRatio_On16x10Source()
    {
        // 回归：旧实现把高度写成 targetWidth*9/16，16:10 源会被压扁成 16:9
        var (w, h) = VideoFormatPlanner.FitToWidth(1920, 1200, 1920);
        Assert.Equal(1920, w);
        Assert.Equal(1200, h);
    }

    [Fact]
    public void FitToWidth_PreservesAspectRatio_OnUltrawideSource()
    {
        var (w, h) = VideoFormatPlanner.FitToWidth(3440, 1440, 1920);
        Assert.Equal(1920, w);
        // 21:9 → 高度约 804，绝不是 16:9 的 1080
        Assert.InRange(h, 800, 808);
        AssertRatio(3440.0 / 1440.0, w / (double)h);
    }

    [Fact]
    public void FitToWidth_OddSourceSize_ProducesEvenOutput()
    {
        var (w, h) = VideoFormatPlanner.FitToWidth(1367, 769, 1367);
        Assert.Equal(0, w % 2);
        Assert.Equal(0, h % 2);
    }

    [Fact]
    public void FitToWidth_SmallWindow_ScalesUpToMinimumKeepingAspect()
    {
        var (w, h) = VideoFormatPlanner.FitToWidth(200, 100, 1920);
        Assert.True(w >= VideoFormatPlanner.MinWidth);
        Assert.True(h >= VideoFormatPlanner.MinHeight);
        AssertRatio(2.0, w / (double)h);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(-1, 720)]
    [InlineData(1920, -5)]
    public void FitToWidth_DegenerateSource_DoesNotThrow(int srcW, int srcH)
    {
        var (w, h) = VideoFormatPlanner.FitToWidth(srcW, srcH, 1920);
        Assert.True(w >= 2 && h >= 2);
        Assert.Equal(0, w % 2);
        Assert.Equal(0, h % 2);
    }

    [Fact]
    public void FitInto_NeverUpscales()
    {
        var (w, h) = VideoFormatPlanner.FitInto(1280, 720, 3840, 2160);
        Assert.Equal(1280, w);
        Assert.Equal(720, h);
    }

    [Fact]
    public void FitInto_AspectMismatch_FitsBoxWithoutDistortion()
    {
        // 16:10 的帧塞进 16:9 的盒子：必须等比缩小，而不是各自夹取宽高
        var (w, h) = VideoFormatPlanner.FitInto(1920, 1200, 1920, 1080);
        Assert.True(w <= 1920 && h <= 1080);
        AssertRatio(1920.0 / 1200.0, w / (double)h);
    }

    [Fact]
    public void FitInto_4KSourceInto1080pBox()
    {
        var (w, h) = VideoFormatPlanner.FitInto(3840, 2160, 1920, 1080);
        Assert.Equal(1920, w);
        Assert.Equal(1080, h);
    }

    [Theory]
    [InlineData(960, 540, 30)]
    [InlineData(1280, 720, 30)]
    [InlineData(1920, 1080, 30)]
    [InlineData(2560, 1440, 60)]
    [InlineData(3840, 2160, 60)]
    [InlineData(3840, 2160, 144)]
    public void SuggestBitrate_StaysWithinBounds(int w, int h, int fps)
    {
        var bps = VideoFormatPlanner.SuggestBitrateBps(w, h, fps);
        Assert.InRange(bps, VideoFormatPlanner.MinBitrateBps, VideoFormatPlanner.MaxBitrateBps);
    }

    [Fact]
    public void SuggestBitrate_IncreasesWithResolution()
    {
        var b540 = VideoFormatPlanner.SuggestBitrateBps(960, 540, 30);
        var b720 = VideoFormatPlanner.SuggestBitrateBps(1280, 720, 30);
        var b1080 = VideoFormatPlanner.SuggestBitrateBps(1920, 1080, 30);
        var b2k = VideoFormatPlanner.SuggestBitrateBps(2560, 1440, 30);
        var b4k = VideoFormatPlanner.SuggestBitrateBps(3840, 2160, 30);
        Assert.True(b540 < b720, $"{b540} < {b720}");
        Assert.True(b720 < b1080, $"{b720} < {b1080}");
        Assert.True(b1080 < b2k, $"{b1080} < {b2k}");
        Assert.True(b2k < b4k, $"{b2k} < {b4k}");
    }

    [Fact]
    public void SuggestBitrate_IncreasesWithFps()
    {
        var previous = 0;
        foreach (var fps in VideoFormatPlanner.FpsTiers)
        {
            var bps = VideoFormatPlanner.SuggestBitrateBps(1920, 1080, fps);
            Assert.True(bps > previous, $"fps={fps}: {bps} > {previous}");
            previous = bps;
        }
    }

    [Fact]
    public void SuggestBitrate_HighFpsGrowsSublinearly()
    {
        var b30 = VideoFormatPlanner.SuggestBitrateBps(1920, 1080, 30);
        var b144 = VideoFormatPlanner.SuggestBitrateBps(1920, 1080, 144);
        // 144fps 是 30fps 的 4.8 倍，但码率不应线性放大到 4.8 倍
        Assert.True(b144 < b30 * 4.8, $"{b144} < {b30 * 4.8}");
        Assert.True(b144 > b30, $"{b144} > {b30}");
    }

    [Theory]
    [InlineData(1920, 1080, 30, 7_000_000, 10_500_000)]    // 1080p30 约 8.7 Mbps（bpp 0.14）
    [InlineData(3840, 2160, 30, 22_000_000, 36_000_000)]   // 4K30 约 28 Mbps
    [InlineData(3840, 2160, 60, 40_000_000, 60_000_000)]   // 4K60 约 49 Mbps
    public void SuggestBitrate_InExpectedRange(int w, int h, int fps, int min, int max)
    {
        Assert.InRange(VideoFormatPlanner.SuggestBitrateBps(w, h, fps), min, max);
    }

    [Fact]
    public void SuggestBitrate_AlignsTo100Kbps()
    {
        var bps = VideoFormatPlanner.SuggestBitrateBps(1920, 1080, 30);
        Assert.Equal(0, bps % 100_000);
    }

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(1920, 1080, -5)]
    [InlineData(1920, 1080, 1000)]
    public void SuggestBitrate_InvalidInput_DoesNotThrow(int w, int h, int fps)
    {
        var bps = VideoFormatPlanner.SuggestBitrateBps(w, h, fps);
        Assert.InRange(bps, VideoFormatPlanner.MinBitrateBps, VideoFormatPlanner.MaxBitrateBps);
    }

    [Theory]
    [InlineData(3840, "4K")]
    [InlineData(2560, "2K")]
    [InlineData(1920, "1080p")]
    [InlineData(1280, "720p")]
    [InlineData(960, "540p")]
    public void PresetLabel_MapsWidthToName(int width, string expected)
    {
        Assert.Equal(expected, VideoFormatPlanner.PresetLabel(width));
    }

    // 回归：Microsoft AVC DX12 Encoder 默认锁在 Level 5.0，不显式下发 level 时
    // 4K（32400 宏块/帧）与 1080p144（1175040 宏块/秒）都会被拒绝（E_INVALIDARG）。
    [Theory]
    [InlineData(1920, 1080, 30, 40)]   // 8160 宏块、244800/秒 → 刚好落在 4.0
    [InlineData(1920, 1080, 60, 42)]   // 489600/秒 超出 4.0/4.1 → 4.2
    [InlineData(1280, 720, 120, 42)]   // 432000/秒 → 4.2
    [InlineData(2560, 1440, 60, 51)]   // 14400 宏块、864000/秒 → 5.1
    [InlineData(3840, 2160, 30, 51)]   // 32400 宏块超出 5.0 的 22080 → 5.1
    [InlineData(1920, 1080, 144, 52)]  // 1175040/秒 超出 5.1 的 983040 → 5.2
    [InlineData(3840, 2160, 60, 52)]   // 1944000/秒 → 5.2
    [InlineData(3840, 2160, 90, 60)]   // 2916000/秒 → 6.0
    [InlineData(3840, 2160, 120, 60)]  // 3888000/秒 → 6.0
    [InlineData(3840, 2160, 144, 61)]  // 4665600/秒 超出 6.0 的 4177920 → 6.1
    public void SuggestH264Level_PicksLowestLevelThatFits(int width, int height, int fps, int expected)
    {
        Assert.Equal(expected, VideoFormatPlanner.SuggestH264Level(width, height, fps));
    }

    [Fact]
    public void SuggestH264Level_ClampsTo62_AndFallsBackOnInvalidInput()
    {
        // 远超 6.2（262144 宏块/帧）→ 返回最高档，最终由编码器裁决
        Assert.Equal(62, VideoFormatPlanner.SuggestH264Level(8192, 8192, 240));
        // 非法输入 → 最低档，不抛异常
        Assert.Equal(40, VideoFormatPlanner.SuggestH264Level(0, 0, 0));
        Assert.Equal(40, VideoFormatPlanner.SuggestH264Level(-1, 1080, 30));
    }

    [Fact]
    public void SuggestH264Level_AllFpsTiersFitWithinSpecAt4K()
    {
        foreach (var fps in VideoFormatPlanner.FpsTiers)
            Assert.InRange(VideoFormatPlanner.SuggestH264Level(3840, 2160, fps), 40, 62);
    }

    [Theory]
    [InlineData(40, "4.0")]
    [InlineData(51, "5.1")]
    [InlineData(62, "6.2")]
    [InlineData(0, "未设置")]
    public void H264LevelName_FormatsLevel(int level, string expected)
    {
        Assert.Equal(expected, VideoFormatPlanner.H264LevelName(level));
    }

    /// <summary>宽高比一致（容忍偶数取整带来的 1% 误差）</summary>
    private static void AssertRatio(double expected, double actual)
    {
        Assert.True(Math.Abs(expected - actual) / expected < 0.01,
            $"宽高比不符：期望 {expected:F3}，实际 {actual:F3}");
    }
}
