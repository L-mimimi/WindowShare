namespace WindowShare.Core.Utils;

/// <summary>
/// Viewer 端 UI 设置（config\viewer-settings.json）。密码不落盘（会话级临时凭据）。
/// </summary>
public sealed class ViewerSettings
{
    /// <summary>true=房间号模式，false=直连 IP 模式</summary>
    public bool RoomMode { get; set; }

    public string Host { get; set; } = "127.0.0.1";

    public int Port { get; set; } = 48750;

    public string Room { get; set; } = "";

    public string SignalingUrl { get; set; } = "http://localhost:5000";

    public bool PlayAudio { get; set; } = true;

    /// <summary>
    /// 音频抖动缓冲目标延迟（毫秒）。默认 120ms；「低延迟」档 40ms 适合有线/强信号局域网，
    /// 代价是网络抖动大时更容易欠载重缓冲。v1.5.0 Q6。
    /// </summary>
    public int AudioTargetLatencyMs { get; set; } = Audio.AudioRenderer.DefaultTargetLatencyMs;

    /// <summary>
    /// HEVC 解码能力（子进程探针实测后缓存；null=尚未探测，下次启动后台补测）。
    /// 探测可能触发部分平台扩展 MFT 的原生崩溃，因此永远在子进程进行并缓存结论。
    /// </summary>
    public bool? HevcDecodeSupported { get; set; }

    /// <summary>
    /// 视频解码器选择："auto"（默认）/ "mf"（仅 Media Foundation）/ "ffmpeg"（仅 FFmpeg 软解）。
    /// 未知值按 auto 处理。
    /// </summary>
    public string DecoderPreference { get; set; } = "auto";

    public static ViewerSettings Load() =>
        JsonSettingsStore.Load<ViewerSettings>(AppPaths.ViewerSettingsFile);

    public void Save() => JsonSettingsStore.Save(AppPaths.ViewerSettingsFile, this);
}
