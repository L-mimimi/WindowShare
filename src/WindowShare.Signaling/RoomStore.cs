using System.Collections.Concurrent;
namespace WindowShare.Signaling.Models;

/// <summary>房间中的观看者</summary>
public sealed class RoomViewer
{
    public string ViewerId { get; set; } = "";
    public string DeviceId { get; set; } = "";
    public string DeviceName { get; set; } = "";
    public string ConnectionId { get; set; } = "";
    public DateTime JoinedAt { get; set; }
}

/// <summary>房间（内存态；进程重启即失效，符合临时房间语义）</summary>
public sealed class Room
{
    public string Code { get; set; } = "";
    /// <summary>PBKDF2(password,"wsh1-signaling") 的 SHA-256 哈希（服务器不存明文密码）</summary>
    public string PasswordHash { get; set; } = "";
    public string HostConnectionId { get; set; } = "";
    public string HostDeviceName { get; set; } = "";
    /// <summary>Host 上报的本机 LAN 端点（ip:port 列表，供同网段观看者直连）</summary>
    public List<string> LanEndpoints { get; set; } = new();
    public DateTime LastSeen { get; set; }
    public Dictionary<string, RoomViewer> Viewers { get; } = new();
}

/// <summary>房间存储（线程安全 + 过期清理）</summary>
public sealed class RoomStore
{
    /// <summary>房间无心跳过期时间</summary>
    private static readonly TimeSpan RoomTtl = TimeSpan.FromMinutes(2);

    private readonly ConcurrentDictionary<string, Room> _rooms = new();

    public bool Register(string roomCode, string passwordHash, string connectionId,
        string deviceName, List<string> lanEndpoints)
    {
        var room = new Room
        {
            Code = roomCode,
            PasswordHash = passwordHash,
            HostConnectionId = connectionId,
            HostDeviceName = deviceName,
            LanEndpoints = lanEndpoints,
            LastSeen = DateTime.UtcNow,
        };
        // 同一房间号被占用时覆盖（新会话生效；旧连接会被心跳失败淘汰）
        return _rooms.AddOrUpdate(roomCode, room, (_, _) => room) == room;
    }

    public void Unregister(string connectionId)
    {
        var room = _rooms.Values.FirstOrDefault(r => r.HostConnectionId == connectionId);
        if (room != null) _rooms.TryRemove(room.Code, out _);
    }

    public void UnregisterByCode(string roomCode) => _rooms.TryRemove(roomCode, out _);

    public Room? Find(string roomCode) =>
        _rooms.TryGetValue(roomCode, out var room) ? room : null;

    public void Heartbeat(string connectionId)
    {
        var room = _rooms.Values.FirstOrDefault(r => r.HostConnectionId == connectionId);
        if (room != null) room.LastSeen = DateTime.UtcNow;
    }

    /// <summary>移除过期房间</summary>
    public int Cleanup()
    {
        var removed = 0;
        foreach (var (code, room) in _rooms)
        {
            if (DateTime.UtcNow - room.LastSeen > RoomTtl && _rooms.TryRemove(code, out _))
                removed++;
        }
        return removed;
    }

    public int Count => _rooms.Count;

    /// <summary>全部房间（Hub 遍历用）</summary>
    public IEnumerable<Room> Values => _rooms.Values;
}
