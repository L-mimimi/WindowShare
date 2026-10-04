namespace WindowShare.Core.Encoding;

/// <summary>
/// 会话视频编码（1.4.0 起 HEVC 可选）。
/// LAN 帧格式不变（均为 Annex-B 裸流），解码端按协商结果选择解码器。
/// </summary>
public enum VideoCodec
{
    /// <summary>H.264/AVC（所有版本默认，兼容旧观看端）</summary>
    H264,

    /// <summary>H.265/HEVC（同画质约省 30–50% 码率；需要两端 1.4.0+ 且平台有 HEVC 编解码器）</summary>
    Hevc,
}

/// <summary>VideoCodec 的协议表示与通用辅助</summary>
public static class VideoCodecs
{
    /// <summary>协议字段取值（AuthResult.vcodec / AuthRequest.hevc 的语义来源）</summary>
    public static string ToWireName(this VideoCodec codec) =>
        codec == VideoCodec.Hevc ? "hevc" : "h264";

    /// <summary>解析协议字段；未知值回退 H.264（旧 Host 不带该字段时的行为）</summary>
    public static VideoCodec FromWireName(string? wireName) =>
        string.Equals(wireName, "hevc", StringComparison.OrdinalIgnoreCase)
            ? VideoCodec.Hevc
            : VideoCodec.H264;

    /// <summary>显示名（日志/UI）</summary>
    public static string DisplayName(this VideoCodec codec) =>
        codec == VideoCodec.Hevc ? "HEVC" : "H.264";
}
