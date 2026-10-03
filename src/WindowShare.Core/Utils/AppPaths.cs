using System.Text.Json;

namespace WindowShare.Core.Utils;

/// <summary>
/// 应用目录与路径约定。
///
/// 数据根目录按以下优先级确定：
///   1) 环境变量 WINDOWSHARE_DATA_DIR（显式指定，最高优先级）；
///   2) 便携模式：程序目录下存在 portable.marker 文件或 data 目录，且该目录可写
///      → 数据放在程序目录的 data\ 下（解压即用，可放 U 盘随身携带）；
///   3) 默认：%APPDATA%\WindowShare（安装版行为）。
///
/// 便携模式下设备 ID、白名单、日志、录制文件都跟随程序目录，换机器插入 U 盘即为同一设备。
/// </summary>
public static class AppPaths
{
    private static readonly object Gate = new();

    /// <summary>便携模式标记文件名</summary>
    public const string PortableMarkerName = "portable.marker";

    /// <summary>便携模式数据目录名</summary>
    public const string PortableDataDirName = "data";

    /// <summary>环境变量：显式指定数据目录</summary>
    public const string DataDirEnvVar = "WINDOWSHARE_DATA_DIR";

    /// <summary>是否运行在便携模式</summary>
    public static bool IsPortable { get; }

    /// <summary>数据根目录</summary>
    public static string Root { get; }

    /// <summary>便携模式判定说明（UI/日志展示用）</summary>
    public static string ModeDescription { get; }

    /// <summary>日志目录</summary>
    public static string Logs => Path.Combine(Root, "logs");

    /// <summary>配置文件目录</summary>
    public static string Config => Path.Combine(Root, "config");

    /// <summary>录制验证输出目录（H.264 文件）</summary>
    public static string Recordings => Path.Combine(Root, "recordings");

    /// <summary>设备白名单文件</summary>
    public static string WhitelistFile => Path.Combine(Config, "whitelist.json");

    /// <summary>本端设备设置文件（设备 ID 等）</summary>
    public static string DeviceFile => Path.Combine(Config, "device.json");

    /// <summary>Host 端设置文件</summary>
    public static string HostSettingsFile => Path.Combine(Config, "host-settings.json");

    /// <summary>Viewer 端设置文件</summary>
    public static string ViewerSettingsFile => Path.Combine(Config, "viewer-settings.json");

    /// <summary>合并入口的全局设置文件（模式选择）</summary>
    public static string AppSettingsFile => Path.Combine(Config, "app-settings.json");

    static AppPaths()
    {
        var resolved = Resolve(
            envDir: Environment.GetEnvironmentVariable(DataDirEnvName),
            exeDir: GetExeDirectory(),
            isWritable: TryPrepareWritable);

        Root = resolved.Root;
        IsPortable = resolved.IsPortable;
        ModeDescription = resolved.Description;
    }

    private const string DataDirEnvName = DataDirEnvVar;

    /// <summary>解析结果</summary>
    internal readonly record struct ResolvedPaths(string Root, bool IsPortable, string Description);

    /// <summary>
    /// 数据根目录决策（纯逻辑，可单测）：
    ///   1) 环境变量指定 → 便携
    ///   2) 程序目录有便携标记/data 目录且可写 → 便携（程序目录\data）
    ///   3) 程序目录有便携标记但不可写 → 回退 %APPDATA%（避免启动失败）
    ///   4) 无标记 → 安装模式（%APPDATA%\WindowShare）
    /// </summary>
    internal static ResolvedPaths Resolve(string? envDir, string exeDir, Func<string, bool> isWritable)
    {
        var appDataRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WindowShare");

        // 1) 环境变量显式指定
        if (!string.IsNullOrWhiteSpace(envDir))
        {
            return new ResolvedPaths(Path.GetFullPath(envDir),
                true, $"便携模式（环境变量 {DataDirEnvName}）");
        }

        // 2) 便携标记检测
        var hasMarker = false;
        try
        {
            hasMarker = File.Exists(Path.Combine(exeDir, PortableMarkerName))
                        || Directory.Exists(Path.Combine(exeDir, PortableDataDirName));
        }
        catch { /* 路径异常按非便携处理 */ }

        if (!hasMarker)
            return new ResolvedPaths(appDataRoot, false, "安装模式（数据存于 %APPDATA%\\WindowShare）");

        var dataDir = Path.Combine(exeDir, PortableDataDirName);
        if (isWritable(dataDir))
            return new ResolvedPaths(dataDir, true, "便携模式（数据存于程序目录 data\\）");

        // 3) 便携目录不可写（程序放在只读位置/U 盘写保护）→ 安全回退
        return new ResolvedPaths(appDataRoot, false,
            "安装模式（程序目录不可写，已回退到 %APPDATA%）");
    }

    /// <summary>程序所在目录（兼容单文件发布与普通发布）</summary>
    public static string GetExeDirectory()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exe))
            {
                var dir = Path.GetDirectoryName(exe);
                if (!string.IsNullOrEmpty(dir)) return dir;
            }
        }
        catch { }
        return AppContext.BaseDirectory;
    }

    /// <summary>确保所有目录存在（线程安全）</summary>
    public static void EnsureDirectories()
    {
        lock (Gate)
        {
            foreach (var dir in new[] { Root, Logs, Config, Recordings })
            {
                try
                {
                    if (!Directory.Exists(dir))
                        Directory.CreateDirectory(dir);
                }
                catch
                {
                    // 单目录创建失败不影响其余目录；日志层会记录后续写入失败
                }
            }
        }
    }

    /// <summary>
    /// 本机设备 ID（首次运行生成，之后固定），用于设备白名单识别。
    /// 便携模式下随程序目录存放，U 盘即设备身份。
    /// </summary>
    public static string GetOrCreateDeviceId()
    {
        EnsureDirectories();
        if (File.Exists(DeviceFile))
        {
            try
            {
                var json = File.ReadAllText(DeviceFile);
                var node = JsonDocument.Parse(json).RootElement;
                var id = node.GetProperty("deviceId").GetString();
                if (!string.IsNullOrEmpty(id)) return id!;
            }
            catch (Exception ex)
            {
                // 设备文件损坏时重新生成，不影响安全（白名单会重新走审批流程）
                Logging.Logger.Warn("Utils", $"设备文件损坏，将重新生成: {ex.Message}");
            }
        }

        var newId = Guid.NewGuid().ToString("N");
        var newName = GetMachineName();
        try
        {
            File.WriteAllText(DeviceFile,
                JsonSerializer.Serialize(new { deviceId = newId, deviceName = newName }));
        }
        catch (Exception ex)
        {
            Logging.Logger.Warn("Utils", $"设备文件写入失败（本次会话仍可用）: {ex.Message}");
        }
        return newId;
    }

    /// <summary>获取当前机器名（作为展示名）</summary>
    public static string GetMachineName() =>
        string.IsNullOrWhiteSpace(Environment.MachineName) ? "Windows-PC" : Environment.MachineName;

    /// <summary>尝试创建目录并验证可写（便携盘可能只读）</summary>
    private static bool TryPrepareWritable(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            var probe = Path.Combine(dir, $".write-probe-{Guid.NewGuid():N}");
            File.WriteAllText(probe, "probe");
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
