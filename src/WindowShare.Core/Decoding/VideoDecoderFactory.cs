namespace WindowShare.Core.Decoding;

using WindowShare.Core.Encoding;

/// <summary>
/// 解码器选择链：
///   H.264：MF 收件箱解码器（系统组件，稳定可靠，不引入兜底复杂度）；
///   HEVC：MF 解码器（子进程探针通过时；覆盖厂商硬解 MFT 机器）→ FFmpeg 软解兜底 → 抛异常（会话无法观看）。
/// FFmpeg 软解是进程内纯软件路径，永远不缺位（DLL 随应用分发），解码能力不再受系统组件状态支配。
/// 用户可通过 <see cref="DecoderPreference"/> 强制指定解码路径。
/// </summary>
public static class VideoDecoderFactory
{
    /// <summary>
    /// 创建解码器。
    /// </summary>
    /// <param name="codec">编码格式</param>
    /// <param name="preference">用户选择的解码路径</param>
    /// <param name="hevcMfAvailable">HEVC 的 MF 解码探针结论（子进程实测，缓存于设置）；仅 Auto 模式使用</param>
    /// <exception cref="InvalidOperationException">所选路径上没有可用解码器</exception>
    public static IVideoDecoder Create(VideoCodec codec, DecoderPreference preference, bool hevcMfAvailable)
    {
        switch (preference)
        {
            case DecoderPreference.Ffmpeg:
                return CreateFfmpeg(codec);
            case DecoderPreference.MediaFoundation:
                return new MfVideoDecoder(codec);
        }

        // Auto：HEVC 且 MF 探针未通过时直接走 FFmpeg，绝不进程内构造已知会崩的 MFT
        if (codec != VideoCodec.Hevc || hevcMfAvailable)
        {
            try
            {
                return new MfVideoDecoder(codec);
            }
            catch (Exception ex)
            {
                // MF 构造失败（托管层面可捕获的失败）：HEVC 时降级到 FFmpeg 兜底
                if (codec != VideoCodec.Hevc) throw;
                Logging.Logger.Warn("Decoder", "MF HEVC 解码器构造失败，转 FFmpeg 软解兜底: " + ex.Message);
            }
        }

        return CreateFfmpeg(codec);
    }

    private static IVideoDecoder CreateFfmpeg(VideoCodec codec)
    {
        var ffmpeg = FfmpegVideoDecoder.TryCreate(codec);
        if (ffmpeg != null)
            return ffmpeg;

        throw new InvalidOperationException(
            $"FFmpeg 软解不可用（{(FfmpegVideoDecoder.UnavailableReason() ?? "未知原因")}）");
    }
}
