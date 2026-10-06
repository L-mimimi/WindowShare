using Vortice.Direct3D11;

namespace WindowShare.Core.Encoding;

/// <summary>
/// 编码器选择链（to1.5.0 Step 3）：
///   FFmpeg 厂商硬编（nvenc → amf → qsv，逐个 open2 实测驱动，Step 0 验证真实 CBR）
///   → MF 现链（硬件 MFT → DX12 包装器 → 扩展/软件 MFT，行为不变）。
///
/// Host「编码器」下拉三档语义不变（自动=H.264 / H.264 / HEVC 优先），后端选择在此内部化；
/// 全部失败保持现状语义：抛异常（上游按「无编码器」处理）。
/// </summary>
public static class VideoEncoderFactory
{
    /// <summary>按候选链创建编码器；device 传给 MF 路径启用零拷贝</summary>
    public static IVideoEncoder Create(EncoderSettings settings, ID3D11Device? device = null)
    {
        // FFmpeg 厂商硬编优先：收件箱 DX12 编码器拒绝一切 CodecAPI（欠产出根因），
        // 厂商封装经 libavcodec 才有真实 CBR。PreferHardware=false 时跳过（用户显式要软件路径）。
        if (settings.PreferHardware)
        {
            var ff = FfmpegVideoEncoder.TryCreate(settings);
            if (ff != null) return ff;
        }
        return new MfVideoEncoder(settings, device);
    }

    /// <summary>
    /// 编码能力探测（Host 开共享前决策，如 HEVC 会话是否可承诺）：
    /// FFmpeg 厂商链 ∥ MF 链任一可用即可。毫秒到几百 ms 级。
    /// </summary>
    public static bool ProbeAvailable(EncoderSettings settings)
    {
        if (settings.PreferHardware && FfmpegVideoEncoder.ProbeAvailable(settings))
            return true;
        return MfVideoEncoder.ProbeAvailable(settings);
    }
}
