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

    public static ViewerSettings Load() =>
        JsonSettingsStore.Load<ViewerSettings>(AppPaths.ViewerSettingsFile);

    public void Save() => JsonSettingsStore.Save(AppPaths.ViewerSettingsFile, this);
}
