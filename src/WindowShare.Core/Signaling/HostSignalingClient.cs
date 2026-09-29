using Microsoft.AspNetCore.SignalR.Client;
using WindowShare.Core.Logging;

namespace WindowShare.Core.Signaling;

/// <summary>信令服务器下发给观看者的 Host 信息</summary>
public sealed record HostInfoPayload
{
    public string HostDeviceName { get; init; } = "";
    public List<string> LanEndpoints { get; init; } = new();
    public bool ShareActive { get; init; }
}

/// <summary>观看者接入请求（Host 侧收到的审批信息）</summary>
public sealed record ViewerJoinRequest
{
    public string ViewerId { get; init; } = "";
    public string DeviceName { get; init; } = "";
    public string DeviceId { get; init; } = "";
}

/// <summary>
/// Host 信令客户端：把房间号/密码哈希注册到信令服务器，接收审批请求并回传结果，
/// 同时接收观看者的中继消息（WebRTC SDP/ICE，批7 使用）。
/// </summary>
public sealed class HostSignalingClient : IAsyncDisposable
{
    private readonly string _url;
    private HubConnection? _hub;
    private Timer? _heartbeat;
    private string _roomCode = "";
    private string _passwordHash = "";

    public bool IsConnected => _hub?.State == HubConnectionState.Connected;

    /// <summary>观看者请求接入（需要 UI 审批）</summary>
    public event Action<ViewerJoinRequest>? ViewerJoinRequested;

    /// <summary>观看者中继消息（type: offer/answer/ice 等）</summary>
    public event Action<string?, string, string>? RelayFromViewer;

    public HostSignalingClient(string url)
    {
        _url = url.TrimEnd('/');
    }

    /// <summary>连接并注册房间（阻塞直至注册完成）</summary>
    public async Task<bool> RegisterAsync(string roomCode, string password, string deviceName,
        List<string> lanEndpoints, CancellationToken ct = default)
    {
        _roomCode = roomCode;
        _passwordHash = SignalingHubCompat.HashPasswordClient(password);

        _hub = new HubConnectionBuilder()
            .WithUrl($"{_url}/signalr")
            .WithAutomaticReconnect(new[] { TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5) })
            .Build();

        _hub.On<string, string, string>("ViewerJoinRequest", (viewerId, devName, devId) =>
            ViewerJoinRequested?.Invoke(new ViewerJoinRequest
            {
                ViewerId = viewerId,
                DeviceName = devName,
                DeviceId = devId,
            }));
        _hub.On<string?, string, string>("RelayFromViewer", (viewerId, type, payload) =>
            RelayFromViewer?.Invoke(viewerId, type, payload));
        _hub.Reconnected += async _ =>
        {
            // 重连后重新注册房间
            await _hub!.InvokeAsync("RegisterHost", _roomCode, _passwordHash, deviceName, lanEndpoints, ct);
        };

        await _hub.StartAsync(ct);
        var ok = await _hub.InvokeAsync<bool>("RegisterHost",
            _roomCode, _passwordHash, deviceName, lanEndpoints, ct);
        if (!ok)
        {
            Logging.Logger.Warn("Signaling", "房间号已被占用，注册失败");
            await _hub.DisposeAsync();
            _hub = null;
            return false;
        }

        // 心跳保持房间存活
        _heartbeat = new Timer(async _ =>
        {
            try { if (_hub != null && _hub.State == HubConnectionState.Connected) await _hub.InvokeAsync("Heartbeat"); }
            catch { }
        }, null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));

        Logging.Logger.Info("Signaling", $"房间 {roomCode} 已注册到信令服务器 ({_url})");
        return true;
    }

    /// <summary>审批结果回传</summary>
    public Task ApproveViewerAsync(string viewerId, bool approved, string reason = "") =>
        _hub?.InvokeAsync("ApproveViewer", viewerId, approved, reason) ?? Task.CompletedTask;

    /// <summary>共享结束 → 关闭房间</summary>
    public async Task StopSharingAsync()
    {
        try
        {
            if (_hub is { State: HubConnectionState.Connected })
                await _hub.InvokeAsync("StopSharing");
        }
        catch { }
        _heartbeat?.Dispose();
        _heartbeat = null;
    }

    /// <summary>Host → 观看者 中继</summary>
    public Task RelayToViewerAsync(string viewerId, string type, string payload) =>
        _hub?.InvokeAsync("RelayToViewer", viewerId, type, payload) ?? Task.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        _heartbeat?.Dispose();
        if (_hub != null) await _hub.DisposeAsync();
    }
}

/// <summary>
/// 与服务端 HashPassword 保持一致（PBKDF2 + SHA-256，见 SignalingHub.HashPassword）。
/// </summary>
public static class SignalingHubCompat
{
    public static string HashPasswordClient(string password)
    {
        var derived = System.Security.Cryptography.Rfc2898DeriveBytes.Pbkdf2(
            password, "wsh1-signaling"u8.ToArray(), 50_000,
            System.Security.Cryptography.HashAlgorithmName.SHA256, 32);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(derived));
    }
}
