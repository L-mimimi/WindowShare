using Microsoft.AspNetCore.SignalR.Client;
using WindowShare.Core.Logging;

namespace WindowShare.Core.Signaling;

/// <summary>
/// 观看者信令客户端：加入房间（房间号+密码哈希）→ 等待审批 → 接收 Host 信息
/// （LAN 端点供直连；WebRTC 走中继，批7 使用）。
/// </summary>
public sealed class ViewerSignalingClient : IAsyncDisposable
{
    private readonly string _url;
    private HubConnection? _hub;
    private readonly TaskCompletionSource<JoinOutcome> _joinTcs =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool IsConnected => _hub?.State == HubConnectionState.Connected;

    /// <summary>Host 中继消息（type: offer/answer/ice 等）</summary>
    public event Action<string, string>? RelayFromHost;

    /// <summary>Host 停止共享</summary>
    public event Action? HostStopped;

    private sealed record JoinOutcome(bool Ok, string Reason, string ViewerId, HostInfoPayload? HostInfo);

    public ViewerSignalingClient(string url)
    {
        _url = url.TrimEnd('/');
    }

    /// <summary>加入房间（返回 Host 信息；失败抛 HubException/返回 null 由调用方区分）</summary>
    public async Task<HostInfoPayload?> JoinAsync(string roomCode, string password,
        string deviceId, string deviceName, CancellationToken ct = default)
    {
        _hub = new HubConnectionBuilder()
            .WithUrl($"{_url}/signalr")
            .WithAutomaticReconnect()
            .Build();

        _hub.On<string>("ViewerApproved", _ =>
        {
            // 审批通过（HostInfo 随后单独推送）
        });
        _hub.On<string>("JoinFailed", reason =>
        {
            _joinTcs.TrySetResult(new JoinOutcome(false, reason, "", null));
        });
        _hub.On<HostInfoPayload>("HostInfo", info =>
        {
            _joinTcs.TrySetResult(new JoinOutcome(true, "", "", info));
        });
        _hub.On<string, string>("RelayFromHost", (type, payload) => RelayFromHost?.Invoke(type, payload));
        _hub.On("HostStopped", () => HostStopped?.Invoke());

        await _hub.StartAsync(ct);
        var viewerId = await _hub.InvokeAsync<string>("JoinRoom",
            roomCode, SignalingHubCompat.HashPasswordClient(password), deviceId, deviceName, ct);

        // 等待审批结果（最长 60 秒：等用户点批准弹窗）
        var outcome = await _joinTcs.Task.WaitAsync(TimeSpan.FromSeconds(60), ct);
        if (!outcome.Ok)
        {
            Logging.Logger.Warn("Signaling", $"加入房间失败: {outcome.Reason}");
            return null;
        }
        return outcome.HostInfo;
    }

    /// <summary>观看者 → Host 中继</summary>
    public Task RelayToHostAsync(string roomCode, string type, string payload) =>
        _hub?.InvokeAsync("RelayToHost", roomCode, type, payload) ?? Task.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        if (_hub != null) await _hub.DisposeAsync();
    }
}
