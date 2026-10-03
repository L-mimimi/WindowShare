namespace WindowShare.Core.Utils;

/// <summary>
/// 合并入口（WindowShare.exe）的全局设置（config\app-settings.json）。
/// 只记忆上次使用的模式；Host/Viewer 各自的 UI 设置在 host-settings.json / viewer-settings.json。
/// </summary>
public sealed class AppSettings
{
    /// <summary>上次使用的模式（"host" / "viewer"；空 = 未记住）</summary>
    public string LastMode { get; set; } = "";

    /// <summary>是否记住模式（false = 每次启动都弹模式选择窗）</summary>
    public bool Remember { get; set; } = true;

    public static AppSettings Load() =>
        JsonSettingsStore.Load<AppSettings>(AppPaths.AppSettingsFile);

    public void Save() => JsonSettingsStore.Save(AppPaths.AppSettingsFile, this);
}
