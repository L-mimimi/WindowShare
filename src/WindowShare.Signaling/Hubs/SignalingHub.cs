using System.Security.Cryptography;
using Microsoft.AspNetCore.SignalR;
using WindowShare.Signaling.Models;

namespace WindowShare.Signaling.Hubs;

/// <summary>
/// 信令 Hub：房间号/临时密码验证、设备上线、SDP/ICE 中继。
///   - 服务器只转发信号，媒体走 LAN TCP 或 WebRTC（DTLS-SRTP 端到端加密）；
///   - 密码以 PBKDF2 派生哈希比较，服务器不接触明文；
///   - 生产环境请使用 HTTPS/WSS（见 docs/DEPLOY.md）。
/// </summary>
public class SignalingHub : Hub
{
    private readonly RoomStore _rooms;
    private readonly ILogger<SignalingHub> _logger;

    public SignalingHub(RoomStore rooms, ILogger<SignalingHub> logger)
    {
        _rooms = rooms;
        _logger = logger;
    }

    /// <summary>房间码规范化（大写、去空格）</summary>
    private static string Normalize(string code) =>
        (code ?? "").Trim().ToUpperInvariant().Replace(" ", "");

    /// <summary>双方约定的密码哈希算法：PBKDF2(password, "wsh1-signaling", 50k) → SHA-256</summary>
    public static string HashPassword(string password)
    {
        var derived = Rfc2898DeriveBytes.Pbkdf2(password, "wsh1-signaling"u8.ToArray(),
            50_000, HashAlgorithmName.SHA256, 32);
        return Convert.ToHexString(SHA256.HashData(derived));
    }

    // ===== Host 端方法 =====

    /// <summary>Host 注册房间（共享开始时调用）</summary>
    public async Task<bool> RegisterHost(string roomCode, string passwordHash,
        string deviceName, List<string> lanEndpoints)
    {
        var code = Normalize(roomCode);
        if (code.Length < 4 || string.IsNullOrEmpty(passwordHash))
        {
            _logger.LogWarning("RegisterHost 参数无效");
            return false;
        }

        var room = _rooms.Find(code);
        if (room != null && room.HostConnectionId != Context.ConnectionId)
        {
            // 房间号已被其他 Host 占用 → 拒绝，Host 端重新生成
            return false;
        }

        var endpoints = lanEndpoints ?? new List<string>();
        _rooms.Register(code, passwordHash, Context.ConnectionId, deviceName, endpoints);
        await Groups.AddToGroupAsync(Context.ConnectionId, Group(code));
        _logger.LogInformation("Host 已注册房间 {Code} ({Device})，LAN 端点 {Count} 个", code, deviceName, endpoints.Count);
        return true;
    }

    /// <summary>心跳（保持房间存活）</summary>
    public Task Heartbeat()
    {
        _rooms.Heartbeat(Context.ConnectionId);
        return Task.CompletedTask;
    }

    /// <summary>观看者审批结果（Host UI 决策后回调）</summary>
    public async Task ApproveViewer(string viewerId, bool approved, string reason)
    {
        var room = FindRoomByConnection();
        if (room == null) return;

        var viewerIdNorm = (viewerId ?? "").Trim();
        if (approved)
        {
            await Clients.Group(Group(room.Code)).SendAsync("ViewerApproved", viewerIdNorm);
            // 向观看者推送 Host 信息（LAN 端点供直连；WebRTC 走中继消息）
            if (room.Viewers.TryGetValue(viewerIdNorm, out var viewer))
            {
                var viewerConn = viewer.ConnectionId;
                await Clients.Client(viewerConn).SendAsync("HostInfo", new
                {
                    hostDeviceName = room.HostDeviceName,
                    lanEndpoints = room.LanEndpoints,
                    shareActive = true,
                });
            }
            _logger.LogInformation("观看者 {ViewerId} 已批准接入房间 {Code}", viewerIdNorm, room.Code);
        }
        else
        {
            if (room.Viewers.TryGetValue(viewerIdNorm, out var viewer))
            {
                await Clients.Client(viewer.ConnectionId).SendAsync("JoinFailed", reason ?? "设备未获批准");
                room.Viewers.Remove(viewerIdNorm);
            }
            _logger.LogInformation("观看者 {ViewerId} 被拒绝（{Reason}）", viewerIdNorm, reason);
        }
    }

    /// <summary>Host 主动停止共享 → 通知所有观看者</summary>
    public async Task StopSharing()
    {
        var room = FindRoomByConnection();
        if (room == null) return;
        await Clients.Group(Group(room.Code)).SendAsync("HostStopped");
        _rooms.UnregisterByCode(room.Code);
        _logger.LogInformation("房间 {Code} 已由 Host 关闭", room.Code);
    }

    /// <summary>Host → 观看者 中继（WebRTC SDP/ICE；服务器不解析内容）</summary>
    public Task RelayToViewer(string viewerId, string type, string payload)
    {
        var room = FindRoomByConnection();
        if (room == null) return Task.CompletedTask;
        if (room.Viewers.TryGetValue(viewerId ?? "", out var viewer))
            return Clients.Client(viewer.ConnectionId).SendAsync("RelayFromHost", type, payload);
        return Task.CompletedTask;
    }

    // ===== Viewer 端方法 =====

    /// <summary>
    /// 观看者加入房间：验证房间存在 + 密码哈希 → 请求 Host 审批 → HostInfo/JoinFailed 回调。
    /// </summary>
    public async Task<string> JoinRoom(string roomCode, string passwordHash,
        string deviceId, string deviceName)
    {
        var code = Normalize(roomCode);
        var room = _rooms.Find(code);
        if (room == null)
            throw new HubException("房间不存在或已过期");

        // 常量时间比较密码哈希
        if (!CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(room.PasswordHash),
                Convert.FromHexString(HashPasswordMatch(passwordHash))))
        {
            _logger.LogWarning("房间 {Code} 密码校验失败", code);
            throw new HubException("密码错误");
        }

        var viewerId = $"v-{Context.ConnectionId[..8]}";
        room.Viewers[viewerId] = new RoomViewer
        {
            ViewerId = viewerId,
            DeviceId = deviceId ?? "",
            DeviceName = deviceName ?? "未知设备",
            ConnectionId = Context.ConnectionId,
            JoinedAt = DateTime.UtcNow,
        };
        await Groups.AddToGroupAsync(Context.ConnectionId, Group(code));

        // 请求 Host 审批（Host UI 弹窗）
        await Clients.Client(room.HostConnectionId).SendAsync("ViewerJoinRequest",
            viewerId, deviceName ?? "", deviceId ?? "");
        _logger.LogInformation("观看者 {Device} 请求加入房间 {Code}", deviceName, code);
        return viewerId;
    }

    /// <summary>兼容空哈希的规范化（防御 null）</summary>
    private static string HashPasswordMatch(string hash) =>
        string.IsNullOrEmpty(hash) ? "0000000000000000000000000000000000000000000000000000000000000000" : hash.Trim();

    /// <summary>观看者 → Host 中继（WebRTC SDP/ICE）</summary>
    public Task RelayToHost(string roomCode, string type, string payload)
    {
        var room = _rooms.Find(Normalize(roomCode));
        if (room == null) return Task.CompletedTask;
        return Clients.Client(room.HostConnectionId).SendAsync("RelayFromViewer",
            FindViewerIdByConnection(), type, payload);
    }

    // ===== 生命周期 =====

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        _rooms.Unregister(Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }

    // ===== 辅助 =====

    private Room? FindRoomByConnection()
    {
        var room = _rooms.Values.FirstOrDefault(r => r.HostConnectionId == Context.ConnectionId);
        if (room == null)
        {
            // 也可能是观看者 → 找其所在房间
            foreach (var r in _rooms.Values)
                if (r.Viewers.Values.Any(v => v.ConnectionId == Context.ConnectionId))
                    return r;
        }
        return room;
    }

    private string? FindViewerIdByConnection()
    {
        foreach (var r in _rooms.Values)
            foreach (var v in r.Viewers.Values)
                if (v.ConnectionId == Context.ConnectionId)
                    return v.ViewerId;
        return null;
    }

    private static string Group(string code) => $"room:{code}";
}
