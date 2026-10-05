namespace WindowShare.Core.Decoding;

/// <summary>
/// 视频解码器选择（Viewer 端用户可选）。
/// </summary>
public enum DecoderPreference
{
    /// <summary>自动（推荐）：HEVC 时 MF 子进程探针通过走系统解码（硬解优先），否则 FFmpeg 软解兜底</summary>
    Auto,

    /// <summary>仅 Media Foundation：系统解码 MFT（硬件加速优先；个别平台的 HEVC 扩展解码器有缺陷，用户可据此绕开 FFmpeg）</summary>
    MediaFoundation,

    /// <summary>仅 FFmpeg：纯软件解码，与系统解码组件状态完全无关，兼容性最好（CPU 占用略高）</summary>
    Ffmpeg,
}
