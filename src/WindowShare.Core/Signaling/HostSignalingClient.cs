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
    private string _deviceName = "";
    private List<string> _lanEndpoints = new();

    public bool IsConnected => _hub?.State == HubConnectionState.Connected;

    /// <summary>最近一次注册失败的原因（连接异常/被拒绝时填写；成功时清空）</summary>
    public string? LastError { get; private set; }

    /// <summary>观看者请求接入（需要 UI 审批）</summary>
    public event Action<ViewerJoinRequest>? ViewerJoinRequested;

    /// <summary>观看者中继消息（type: offer/answer/ice 等）</summary>
    public event Action<string?, string, string>? RelayFromViewer;

    public HostSignalingClient(string url)
    {
        _url = url.TrimEnd('/');
    }

    /// <summary>
    /// 连接并注册房间（阻塞直至注册完成）。
    /// 可重复调用：换房间号重试时会先释放上一条连接，避免旧连接在服务端继续占着房间。
    /// 返回 false 表示服务器拒绝注册（房间号被占用）；连接类失败直接抛异常，由调用方决定降级策略。
    /// </summary>
    public async Task<bool> RegisterAsync(string roomCode, string password, string deviceName,
        List<string> lanEndpoints, CancellationToken ct = default)
    {
        _roomCode = roomCode;
        _passwordHash = SignalingHubCompat.HashPasswordClient(password);
        _deviceName = deviceName;
        _lanEndpoints = lanEndpoints ?? new List<string>();
        LastError = null;

        // 重入保护：丢弃上一条连接，否则旧连接会在服务端继续持有房间
        if (_hub != null)
        {
            _heartbeat?.Dispose();
            _heartbeat = null;
            var previous = _hub;
            _hub = null;
            try { await previous.DisposeAsync(); } catch { }
        }

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
            await _hub!.InvokeAsync("RegisterHost", _roomCode, _passwordHash, _deviceName, _lanEndpoints);
        };

        try
        {
            await _hub.StartAsync(ct);
        }
        catch (Exception ex)
        {
            LastError = DescribeConnectFailure(_url, ex);
            var failed = _hub;
            _hub = null;
            try { if (failed != null) await failed.DisposeAsync(); } catch { }
            throw;
        }

        var registered = await _hub.InvokeAsync<bool>("RegisterHost",
            _roomCode, _passwordHash, _deviceName, _lanEndpoints, ct);
        if (!registered)
        {
            Logging.Logger.Warn("Signaling", $"房间 {_roomCode} 注册被拒绝（房间号已被占用）");
            LastError = "房间号已被其他 Host 占用";
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

    /// <summary>把底层连接异常翻译成用户能照着排查的原因（UI 直接展示）</summary>
    public static string DescribeConnectFailure(string url, Exception ex)
    {
        var inner = ex is AggregateException aggregate ? aggregate.GetBaseException() : ex;
        return inner switch
        {
            UriFormatException => $"地址格式无效：{url}（需形如 http://主机:5000）",
            System.Net.Sockets.SocketException socket =>
                $"无法连接 {url}（{socket.SocketErrorCode}）：请确认信令服务器已启动、地址与端口正确",
            System.Net.Http.HttpRequestException http =>
                $"无法连接 {url}：{http.Message}（请确认信令服务器已启动、防火墙已放行）",
            TimeoutException or OperationCanceledException => $"连接 {url} 超时",
            _ => $"连接 {url} 失败：{inner.Message}",
        };
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
