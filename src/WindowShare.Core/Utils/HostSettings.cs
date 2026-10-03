namespace WindowShare.Core.Utils;

/// <summary>
/// Host 端 UI 设置（config\host-settings.json，随便携模式落程序目录）。
/// 会话房间号/密码是一次性临时凭据，保持会话级、绝不落盘。
/// </summary>
public sealed class HostSettings
{
    /// <summary>分辨率档位索引（与 Host 分辨率下拉一致，默认 1080p）</summary>
    public int ResolutionIndex { get; set; } = 2;

    /// <summary>帧率档位索引（VideoFormatPlanner.FpsTiers 索引，默认 30fps）</summary>
    public int FpsIndex { get; set; } = 1;

    public bool ShareAudio { get; set; } = true;

    public bool RecordForValidation { get; set; }

    public bool EnableSignaling { get; set; }

    public string SignalingUrl { get; set; } = "http://localhost:5000";

    /// <summary>上次共享源（best-effort 恢复：源列表里找不到同类型同句柄就不恢复）</summary>
    public int LastSourceKind { get; set; } = -1;

    public long LastSourceHandle { get; set; }

    public static HostSettings Load() =>
        JsonSettingsStore.Load<HostSettings>(AppPaths.HostSettingsFile);

    public void Save() => JsonSettingsStore.Save(AppPaths.HostSettingsFile, this);
}
