using System.Text.Json;
using WindowShare.Core.Logging;
using WindowShare.Core.Utils;

namespace WindowShare.Core.Security;

/// <summary>白名单条目（已批准的设备）</summary>
public sealed record WhitelistEntry
{
    public string DeviceId { get; init; } = "";
    public string DeviceName { get; init; } = "";
    public DateTime AddedAt { get; init; }
}

/// <summary>
/// 设备白名单（持久化 %APPDATA%\WindowShare\whitelist.json）：
///   - 首次连接的设备必须经 Host 用户批准（批准时可勾选"记住此设备"）；
///   - 已在白名单中的设备自动放行（密码仍需校验）；
///   - 防止未授权连接的第一道防线。
/// </summary>
public sealed class DeviceWhitelist
{
    private readonly object _gate = new();
    private List<WhitelistEntry> _entries = new();

    public DeviceWhitelist()
    {
        AppPaths.EnsureDirectories();
        Load();
    }

    /// <summary>设备是否已批准</summary>
    public bool IsApproved(string deviceId)
    {
        if (string.IsNullOrEmpty(deviceId)) return false;
        lock (_gate)
            return _entries.Any(e => e.DeviceId == deviceId);
    }

    /// <summary>查询设备名（审批弹窗展示用）</summary>
    public string? GetName(string deviceId)
    {
        lock (_gate)
            return _entries.FirstOrDefault(e => e.DeviceId == deviceId)?.DeviceName;
    }

    /// <summary>添加并持久化</summary>
    public void Approve(string deviceId, string deviceName)
    {
        if (string.IsNullOrEmpty(deviceId)) return;
        lock (_gate)
        {
            if (_entries.Any(e => e.DeviceId == deviceId)) return;
            _entries.Add(new WhitelistEntry
            {
                DeviceId = deviceId,
                DeviceName = deviceName,
                AddedAt = DateTime.Now,
            });
            Save();
        }
        Logger.Info("Whitelist", $"设备已加入白名单: {deviceName} ({deviceId[..Math.Min(8, deviceId.Length)]}…)");
    }

    /// <summary>移除设备</summary>
    public void Remove(string deviceId)
    {
        lock (_gate)
        {
            _entries.RemoveAll(e => e.DeviceId == deviceId);
            Save();
        }
    }

    public IReadOnlyList<WhitelistEntry> GetAll()
    {
        lock (_gate) return _entries.ToList();
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(AppPaths.WhitelistFile)) return;
            var json = File.ReadAllText(AppPaths.WhitelistFile);
            var list = JsonSerializer.Deserialize<List<WhitelistEntry>>(json);
            if (list != null) _entries = list;
            Logger.Info("Whitelist", $"已加载 {_entries.Count} 个已批准设备");
        }
        catch (Exception ex)
        {
            Logger.Warn("Whitelist", $"白名单加载失败: {ex.Message}");
        }
    }

    private void Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(_entries, new JsonSerializerOptions
            {
                WriteIndented = true,
            });
            File.WriteAllText(AppPaths.WhitelistFile, json);
        }
        catch (Exception ex)
        {
            Logger.Warn("Whitelist", $"白名单保存失败: {ex.Message}");
        }
    }
}
