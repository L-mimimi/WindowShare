namespace WindowShare.Core.Encoding;

/// <summary>
/// 视频格式计算（纯函数，无 IO，可完全单测）：
///   - 等比缩放并取偶（NV12 要求宽高为偶数），绝不做非等比拉伸；
///   - 依据分辨率与帧率推算目标码率（屏幕共享内容经验值）；
///   - 提供 UI 使用的帧率档位。
/// 4K（3840×2160）为当前支持的最高分辨率档位。
/// </summary>
public static class VideoFormatPlanner
{
    /// <summary>4K 超高清宽（最高档位）</summary>
    public const int MaxPresetWidth = 3840;

    /// <summary>编码输出最小宽/高（低于此值按比例放大，避免编码器拒绝）</summary>
    public const int MinWidth = 320;
    public const int MinHeight = 240;

    /// <summary>码率上下限：4K 高帧率最高 120 Mbps，再低不低于 1.5 Mbps</summary>
    public const int MaxBitrateBps = 120_000_000;
    public const int MinBitrateBps = 1_500_000;

    /// <summary>UI 帧率档位（帧率不宜过高，144 为上限）</summary>
    public static readonly int[] FpsTiers = { 24, 30, 60, 90, 120, 144 };

    /// <summary>向下取偶（NV12 要求）</summary>
    public static int EvenDown(int value) => value & ~1;

    /// <summary>四舍五入到偶数</summary>
    public static int EvenRound(double value)
    {
        var v = (int)Math.Round(value);
        return v < 2 ? 2 : v & ~1;
    }

    /// <summary>
    /// 等比缩放到不超过 (maxWidth, maxHeight)：保持源宽高比、结果取偶、不上采样。
    /// 每帧调用（捕获尺寸可能因窗口缩放而变化），因此不做最小尺寸放大。
    /// </summary>
    public static (int Width, int Height) FitInto(int sourceWidth, int sourceHeight,
        int maxWidth, int maxHeight)
    {
        var srcW = EvenDown(sourceWidth);
        var srcH = EvenDown(sourceHeight);
        if (srcW < 2 || srcH < 2) return (Math.Max(2, srcW), Math.Max(2, srcH));

        var scale = Math.Min(1.0, Math.Min(maxWidth / (double)srcW, maxHeight / (double)srcH));
        if (scale <= 0) return (srcW, srcH);
        return (Math.Max(2, EvenRound(srcW * scale)), Math.Max(2, EvenRound(srcH * scale)));
    }

    /// <summary>
    /// 按目标宽度等比缩放源尺寸（初始编码尺寸）：保持宽高比、不上采样、结果取偶。
    /// 源尺寸非法时退回 16:9。结果小于 <see cref="MinWidth"/>×<see cref="MinHeight"/> 时等比放大。
    /// </summary>
    public static (int Width, int Height) FitToWidth(int sourceWidth, int sourceHeight, int targetWidth)
    {
        if (sourceWidth < 2 || sourceHeight < 2)
        {
            var w = Math.Max(MinWidth, EvenDown(targetWidth));
            return (w, Math.Max(MinHeight, EvenDown(w * 9 / 16)));
        }

        var (width, height) = FitInto(sourceWidth, sourceHeight, EvenDown(targetWidth), int.MaxValue);

        // 源本身很小（如小窗口）→ 等比放大到最小可用尺寸，不破坏宽高比
        if (width < MinWidth || height < MinHeight)
        {
            var up = Math.Max(MinWidth / (double)width, MinHeight / (double)height);
            width = EvenRound(width * up);
            height = EvenRound(height * up);
        }
        return (width, height);
    }

    /// <summary>
    /// 依据分辨率与帧率推算目标码率（bit/s）。
    /// 以每像素比特数（bpp）为基准，分辨率越高单位像素开销越低（边际递减），
    /// 帧率按 0.8 次幂增长（高帧率的码率需求非线性上升）。
    /// 结果对齐到 100 kbps，并夹在 <see cref="MinBitrateBps"/>..<see cref="MaxBitrateBps"/>。
    /// </summary>
    public static int SuggestBitrateBps(int width, int height, int fps)
    {
        if (width < 2 || height < 2) return MinBitrateBps;
        var safeFps = Math.Clamp(fps, 1, 240);

        const double bitsPerPixel = 0.14;       // 屏幕内容经验值（文本/图形为主；0.085 偏糊，1.3.2 提高约 65%）
        const double referencePixels = 1920.0 * 1080.0;

        var pixels = (double)width * height;
        var fpsFactor = Math.Pow(safeFps / 30.0, 0.8);                    // 帧率次线性
        var resolutionFactor = Math.Pow(referencePixels / pixels, 0.15);  // 高分辨率边际递减
        var bps = pixels * 30.0 * bitsPerPixel * fpsFactor * resolutionFactor;

        var aligned = (int)(Math.Round(bps / 100_000.0) * 100_000);
        return Math.Clamp(aligned, MinBitrateBps, MaxBitrateBps);
    }

    /// <summary>分辨率档位的展示名（4K / 2K / 1080p / 720p / 540p / 原始）</summary>
    public static string PresetLabel(int width) => width switch
    {
        >= 3840 => "4K",
        >= 2560 => "2K",
        >= 1920 => "1080p",
        >= 1280 => "720p",
        >= 960 => "540p",
        _ => $"{width}p",
    };

    /// <summary>
    /// H.264 Level 约束表（eAVEncH264VLevel 值 / 单帧最大宏块数 MaxFS / 每秒最大宏块数 MaxMBPS）。
    /// 来源：ITU-T H.264 Table A-1。码率上限 <see cref="MaxBitrateBps"/>（120Mbps）远低于
    /// 各 level 的 MaxBR，因此 level 只需按「分辨率 × 帧率」推导。
    /// </summary>
    private static readonly (int Level, int MaxFs, int MaxMbPerSec)[] H264Levels =
    {
        (40, 8192, 245_760),
        (41, 8192, 245_760),
        (42, 8704, 522_240),
        (50, 22_080, 589_824),
        (51, 36_864, 983_040),
        (52, 36_864, 2_073_600),
        (60, 139_264, 4_177_920),
        (61, 139_264, 8_355_840),
        (62, 139_264, 16_711_680),
    };

    /// <summary>
    /// 依据分辨率与帧率推导所需的最低 H.264 Level（eAVEncH264VLevel 值，如 51 = Level 5.1）。
    /// 必须显式下发给编码器：Windows 的 "Microsoft AVC DX12 Encoder" 默认锁在 Level 5.0，
    /// 4K（32400 宏块/帧）或 1080p144（1175040 宏块/秒）会被直接拒绝（E_INVALIDARG）。
    /// 尺寸/帧率非法时返回最低档；超出 6.2 时返回 6.2（尽力而为，最终由编码器裁决）。
    /// </summary>
    public static int SuggestH264Level(int width, int height, int fps)
    {
        if (width < 2 || height < 2 || fps < 1) return H264Levels[0].Level;

        var macroblocks = ((width + 15) / 16) * ((height + 15) / 16);
        var macroblocksPerSecond = (long)macroblocks * Math.Clamp(fps, 1, 240);
        foreach (var (level, maxFs, maxMbPerSec) in H264Levels)
        {
            if (macroblocks <= maxFs && macroblocksPerSecond <= maxMbPerSec)
                return level;
        }
        return H264Levels[^1].Level;
    }

    /// <summary>Level 的展示名（51 → "5.1"）；0 表示未下发</summary>
    public static string H264LevelName(int level) =>
        level <= 0 ? "未设置" : $"{level / 10}.{level % 10}";
}
