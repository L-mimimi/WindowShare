using System.Text.Json;
using System.Text.Json.Serialization;

namespace WindowShare.Core.Utils;

/// <summary>
/// UI 设置的 JSON 持久化（路径显式传入，AppPaths 只提供默认位置，便于单测注入临时目录）。
/// 读取宽松：文件缺失/损坏/字段缺失一律回退默认值——设置损坏绝不阻断启动。
/// 写入原子：先写 .tmp 再替换，断电/崩溃不会留下半个 JSON。
/// 保存失败只记日志不抛出：设置是便利功能，不是关键数据。
/// </summary>
public static class JsonSettingsStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static T Load<T>(string path) where T : class, new()
    {
        try
        {
            if (!File.Exists(path)) return new T();
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<T>(json, Options) ?? new T();
        }
        catch (Exception ex)
        {
            Logging.Logger.Warn("Settings", $"设置读取失败，使用默认值（{path}）: {ex.Message}");
            return new T();
        }
    }

    public static void Save<T>(string path, T value) where T : class
    {
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(value, Options));
            try
            {
                if (File.Exists(path)) File.Replace(tmp, path, null);
                else File.Move(tmp, path);
            }
            catch (PlatformNotSupportedException)
            {
                // 个别文件系统（FAT U 盘等）不支持 ReplaceFile API → 覆盖移动兜底
                File.Move(tmp, path, overwrite: true);
            }
        }
        catch (Exception ex)
        {
            Logging.Logger.Warn("Settings", $"设置保存失败（{path}）: {ex.Message}");
        }
    }
}
