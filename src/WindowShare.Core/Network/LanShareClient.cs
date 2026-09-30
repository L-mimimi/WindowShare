using System.Net.Sockets;
using WindowShare.Core.Audio;
using WindowShare.Core.Encoding;
using WindowShare.Core.Logging;
using WindowShare.Core.Protocol;
using WindowShare.Core.Security;
using WindowShare.Core.Session;

namespace WindowShare.Core.Network;

/// <summary>连接状态</summary>
public enum ConnectionState
{
    Disconnected,
    Connecting,
    Authenticating,
    Connected,
    Reconnecting,
    HostStopped,
    Failed,
}

/// <summary>
/// LAN 观看客户端（Viewer 端）：
///   - 连接 + 三步认证（设备 ID/密码，支持 ECDH+AES-GCM 会话加密）；
///   - 接收 H.264 帧并回调（配合 MfH264Decoder 解码显示）；
///   - Ping/Pong RTT 统计、断线自动重连（指数退避 + 重连后请求关键帧）；
///   - 显示连接状态/码率/帧率/延迟所需的全部数据源。
/// </summary>
public sealed class LanShareClient : IDisposable
{
    private TcpFrameConnection? _connection;
    private CancellationTokenSource? _cts;
    private readonly object _gate = new();
    private long _lastPingSent;
    private double _lastRttMs = double.NaN; // 最近 RTT（毫秒；Pong 回包时更新）

    public string Host { get; }
    public int Port { get; }
    public string DeviceId { get; }
    public string DeviceName { get; }
    public string Password { get; }

    public ConnectionState State { get; private set; } = ConnectionState.Disconnected;
    public string? LastError { get; private set; }
    public string HostInfo { get; private set; } = "";
    public bool IsEncrypted { get; private set; }

    /// <summary>本次会话的系统声音参数（Host 未共享时为 Disabled）</summary>
    public AudioSessionInfo Audio { get; private set; } = AudioSessionInfo.Disabled;

    /// <summary>状态变化（UI 调度）</summary>
    public event Action<ConnectionState, string?>? StateChanged;

    /// <summary>收到一帧 H.264（Annex-B）</summary>
    public event Action<EncodedVideoFrame>? FrameReceived;

    /// <summary>收到一帧系统声音（ADTS 封装的 AAC）</summary>
    public event Action<EncodedAudioFrame>? AudioFrameReceived;

    /// <summary>RTT 更新（毫秒）</summary>
    public event Action<double>? RttUpdated;

    /// <summary>Host 端统计（编码器/画质等）</summary>
    public event Action<StatsInfoPayload>? StatsUpdated;

    public LanShareClient(string host, int port, string deviceId, string deviceName, string password)
    {
        Host = host;
        Port = port;
        DeviceId = deviceId;
        DeviceName = deviceName;
        Password = password;
    }

    /// <summary>启动连接（内部自动重连）</summary>
    public void Start()
    {
        _cts = new CancellationTokenSource();
        _ = Task.Run(() => SessionLoopAsync(_cts.Token));
    }

    /// <summary>用户主动断开</summary>
    public void Stop(string reason = "")
    {
        State = ConnectionState.Disconnected;
        StateChanged?.Invoke(State, reason);
        _cts?.Cancel();
        lock (_gate) _connection?.Dispose();
    }

    private void SetState(ConnectionState state, string? error = null)
    {
        State = state;
        LastError = error;
        StateChanged?.Invoke(state, error);
    }

    /// <summary>会话主循环：连接 → 认证 → 收流；断开后指数退避重连</summary>
    private async Task SessionLoopAsync(CancellationToken ct)
    {
        var backoffMs = 500;
        var first = true;
        while (!ct.IsCancellationRequested)
        {
            if (!first) SetState(ConnectionState.Reconnecting, null);
            first = false;

            TcpFrameConnection? conn = null;
            try
            {
                SetState(ConnectionState.Connecting, null);
                var client = new TcpClient();
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(5));
                await client.ConnectAsync(Host, Port, timeoutCts.Token);
                conn = new TcpFrameConnection(client);
                lock (_gate) _connection = conn;

                SetState(ConnectionState.Authenticating, null);
                var result = await Task.Run(() => Authenticate(conn), ct);
                if (!result.Ok)
                {
                    SetState(ConnectionState.Failed, result.Reason);
                    conn.Dispose();
                    return; // 认证失败不重连（密码错误等）
                }

                IsEncrypted = result.Encrypted;
                HostInfo = result.Info;
                Audio = result.Audio ?? AudioSessionInfo.Disabled;
                backoffMs = 500; // 重置退避

                // 认证成功 → 请求关键帧快速出画面
                conn.Send(MessageType.KeyframeRequest, FrameFlags.None, ReadOnlySpan<byte>.Empty);

                var connectedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                conn.FrameReceived += (hdr, payload) => OnFrame(hdr, payload, conn);
                conn.Disconnected += () => connectedTcs.TrySetResult();

                conn.StartReading();
                SetState(ConnectionState.Connected, null);

                // 心跳 + RTT 反馈（供 Host 拥塞控制）
                var pingTimer = Task.Run(async () =>
                {
                    while (!ct.IsCancellationRequested && conn.IsConnected)
                    {
                        _lastPingSent = Environment.TickCount64;
                        try
                        {
                            var ts = BitConverter.GetBytes(DateTime.UtcNow.Ticks);
                            conn.Send(MessageType.Ping, FrameFlags.None, ts);
                            // RTT 反馈（拥塞控制输入）
                            var rtt = _lastRttMs;
                            if (!double.IsNaN(rtt) && rtt > 0)
                                conn.Send(MessageType.StatsInfo, FrameFlags.None,
                                    AuthPayload.Serialize(new StatsInfoPayload { RttMs = rtt }));
                        }
                        catch { break; }
                        await Task.Delay(2000, ct);
                    }
                }, ct);

                await Task.WhenAny(connectedTcs.Task, pingTimer, Task.Delay(Timeout.Infinite, ct));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Logging.Logger.Warn("LanClient", $"连接失败: {ex.Message}");
                SetState(ConnectionState.Reconnecting, ex.Message);
            }
            finally
            {
                conn?.Dispose();
                lock (_gate) if (_connection == conn) _connection = null;
            }

            // 认证被拒（Failed）则退出循环；其他情况指数退避重连
            if (State == ConnectionState.Failed) break;

            Logging.Logger.Info("LanClient", $"{backoffMs}ms 后重连…");
            try { await Task.Delay(backoffMs, ct); }
            catch (OperationCanceledException) { break; }
            backoffMs = Math.Min(backoffMs * 2, 8000);
        }
    }

    private sealed record AuthOutcome(bool Ok, string Reason, bool Encrypted, string Info,
        AudioSessionInfo? Audio = null);

    /// <summary>三步认证（同步帧 IO，在专用任务上执行）</summary>
    private AuthOutcome Authenticate(TcpFrameConnection conn)
    {
        try
        {
            // 1) AuthRequest
            var req = AuthPayload.Serialize(new AuthRequestPayload
            {
                DeviceId = DeviceId,
                DeviceName = DeviceName,
            });
            conn.Send(MessageType.AuthRequest, FrameFlags.None, req);

            // 2) AuthChallenge
            var challengeFrame = conn.ReadFrame();
            if (challengeFrame == null || challengeFrame.Value.Header.Type != MessageType.AuthChallenge)
                return new AuthOutcome(false, "Host 未响应认证质询", false, "");
            var challenge = AuthPayload.Deserialize<AuthChallengePayload>(challengeFrame.Value.Payload);
            if (challenge == null)
                return new AuthOutcome(false, "质询格式错误", false, "");
            var salt = Convert.FromBase64String(challenge.SaltB64);

            // 3) AuthProof（若有 Host 公钥则生成 ECDH 公钥并绑定）
            var key = Pbkdf2.Derive(Password, salt);
            byte[]? clientPub = null;
            EcdhKeyExchange? ecdh = null;
            byte[]? hostPub = null;
            if (challenge.SupportsEncryption && !string.IsNullOrEmpty(challenge.HostPubB64))
            {
                hostPub = Convert.FromBase64String(challenge.HostPubB64);
                ecdh = new EcdhKeyExchange();
                clientPub = ecdh.ExportPublicKey();
            }
            var proof = AuthMath.ComputeProof(key, salt, DeviceId, hostPub, clientPub);
            conn.Send(MessageType.AuthProof, FrameFlags.None, AuthPayload.Serialize(new AuthProofPayload
            {
                ProofB64 = Convert.ToBase64String(proof),
                ClientPubB64 = clientPub != null ? Convert.ToBase64String(clientPub) : "",
            }));

            // 4) AuthResult
            var resultFrame = conn.ReadFrame();
            if (resultFrame == null || resultFrame.Value.Header.Type != MessageType.AuthResult)
                return new AuthOutcome(false, "认证结果未收到", false, "");
            var result = AuthPayload.Deserialize<AuthResultPayload>(resultFrame.Value.Payload);
            if (result == null) return new AuthOutcome(false, "认证结果格式错误", false, "");
            if (!result.Ok)
                return new AuthOutcome(false, string.IsNullOrEmpty(result.Reason) ? "认证被拒绝" : result.Reason, false, "");

            // 5) 启用会话加密
            if (result.EncryptionEnabled && ecdh != null && clientPub != null)
            {
                var aesKey = ecdh.DeriveSessionKey(hostPub! /*本地生成方: 派生用对方公钥=Host 公钥*/, salt);
                conn.EnableEncryption(new AesGcmSession(aesKey));
                Logging.Logger.Info("LanClient", "会话加密已启用 (AES-256-GCM)");
            }
            ecdh?.Dispose();

            var info = $"编码器: {result.EncoderName}" +
                       (result.Width > 0 ? $"  源: {result.Width}x{result.Height}" : "");
            var audio = result.AudioEnabled
                ? new AudioSessionInfo(true,
                    result.AudioSampleRate > 0 ? result.AudioSampleRate : AudioStreamInfo.SampleRate,
                    result.AudioChannels > 0 ? result.AudioChannels : AudioStreamInfo.Channels,
                    string.IsNullOrEmpty(result.AudioCodec) ? AudioStreamInfo.Codec : result.AudioCodec,
                    result.AudioEncoderName)
                : AudioSessionInfo.Disabled;
            if (audio.Enabled)
                Logging.Logger.Info("LanClient",
                    $"Host 正在共享系统声音: {audio.SampleRate}Hz/{audio.Channels}ch {audio.Codec}");
            return new AuthOutcome(true, "", result.EncryptionEnabled, info, audio);
        }
        catch (Exception ex)
        {
            return new AuthOutcome(false, $"认证异常: {ex.Message}", false, "");
        }
    }

    /// <summary>帧分发 + RTT 计算</summary>
    private void OnFrame(FrameHeader hdr, byte[] payload, TcpFrameConnection conn)
    {
        switch (hdr.Type)
        {
            case MessageType.VideoFrame:
            {
                var isKeyframe = (hdr.Flags & FrameFlags.Keyframe) != 0;
                FrameReceived?.Invoke(new EncodedVideoFrame
                {
                    Data = payload,
                    Keyframe = isKeyframe,
                    TimestampUtc = hdr.TimestampUtc,
                    Width = 0,
                    Height = 0,
                });
                break;
            }
            case MessageType.AudioFrame:
            {
                AudioFrameReceived?.Invoke(new EncodedAudioFrame
                {
                    Data = payload,
                    TimestampUtc = hdr.TimestampUtc,
                    SampleRate = Audio.SampleRate,
                    Channels = Audio.Channels,
                });
                break;
            }
            case MessageType.Pong:
            {
                // RTT = 收到 Pong 的时间 - 发出 Ping 的时间
                var rtt = Environment.TickCount64 - _lastPingSent;
                _lastRttMs = rtt;
                RttUpdated?.Invoke(rtt);
                break;
            }
            case MessageType.StatsInfo:
            {
                var stats = AuthPayload.Deserialize<StatsInfoPayload>(payload);
                if (stats != null) StatsUpdated?.Invoke(stats);
                break;
            }
            case MessageType.ShareStopped:
                SetState(ConnectionState.HostStopped, "Host 已停止共享");
                _cts?.Cancel();
                break;
            case MessageType.Bye:
                SetState(ConnectionState.Disconnected, "Host 断开");
                _cts?.Cancel();
                break;
        }
    }

    public void Dispose()
    {
        Stop();
        _cts?.Dispose();
    }
}
