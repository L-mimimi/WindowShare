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

    /// <summary>共享进行中关闭窗口 → 隐藏到托盘继续共享（关=直接停止共享退出）</summary>
    public bool MinimizeToTray { get; set; } = true;

    /// <summary>共享期间广播 LAN 发现信标（Viewer 可自动看到本机；不含任何密钥）</summary>
    public bool Discoverable { get; set; } = true;

    /// <summary>
    /// HEVC 优先（1.4.0 起）：本机有可用 HEVC 编码器时以 HEVC 开启会话，同画质约省 30–50% 码率。
    /// 仅 1.4.0+ 且实测支持 HEVC 解码的观看端能接入；关 = 永远 H.264（兼容所有版本）。
    /// </summary>
    public bool PreferHevc { get; set; }

    /// <summary>
    /// LAN 共享监听地址（高级项，一般不改配置文件）：填本机某个 IPv4（如 192.168.1.10）
    /// 则只在该网卡监听；空 = 所有网卡。
    /// </summary>
    public string BindAddress { get; set; } = "";

    public string SignalingUrl { get; set; } = "http://localhost:5000";

    /// <summary>上次共享源（best-effort 恢复：源列表里找不到同类型同句柄就不恢复）</summary>
    public int LastSourceKind { get; set; } = -1;

    public long LastSourceHandle { get; set; }

    public static HostSettings Load() =>
        JsonSettingsStore.Load<HostSettings>(AppPaths.HostSettingsFile);

    public void Save() => JsonSettingsStore.Save(AppPaths.HostSettingsFile, this);
}
