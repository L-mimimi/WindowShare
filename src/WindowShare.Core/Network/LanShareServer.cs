using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using WindowShare.Core.Capture;
using WindowShare.Core.Encoding;
using WindowShare.Core.Logging;
using WindowShare.Core.Protocol;
using WindowShare.Core.Security;
using WindowShare.Core.Session;

namespace WindowShare.Core.Network;

/// <summary>接入的观看者信息</summary>
public sealed record ViewerInfo(string DeviceId, string DeviceName, string RemoteAddress);

/// <summary>
/// LAN 共享服务器（Host 端，TCP 监听）：
///   - 接入认证：AuthRequest → 白名单检查（首次弹批准）→ AuthChallenge → AuthProof 校验 → AuthResult；
///   - 可选 ECDH+AES-GCM 会话加密（认证证明绑定双方公钥）；
///   - 编码帧分发：每连接有界队列（慢消费者丢帧，不拖累其他观看者）；
///   - 新观看者自动请求关键帧；处理 Ping/KeyframeRequest/Bye。
/// 只读共享协议：不存在任何输入/控制消息。
/// </summary>
public sealed class LanShareServer : ShareSession.IFrameSink, IDisposable
{
    public const int DefaultPort = 48750;

    private readonly ShareSession _session;
    private readonly DeviceWhitelist _whitelist;
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private CongestionController? _controller;
    private Timer? _congestionTimer;
    private readonly List<ClientSession> _clients = new();
    private readonly object _clientsGate = new();

    /// <summary>新观看者需要批准（返回 true 允许；Host UI 弹窗实现）</summary>
    public Func<ViewerInfo, Task<bool>>? ApproveRequired;

    /// <summary>观看者数量变化</summary>
    public event Action<int>? ViewerCountChanged;

    public bool IsRunning { get; private set; }
    public int Port { get; }

    public LanShareServer(ShareSession session, DeviceWhitelist whitelist, int port = DefaultPort)
    {
        _session = session;
        _whitelist = whitelist;
        Port = port;
    }

    public void Start()
    {
        if (IsRunning) return;
        _cts = new CancellationTokenSource();
        _listener = new TcpListener(IPAddress.Any, Port);
        try
        {
            _listener.Start();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"TCP 端口 {Port} 监听失败（可能被占用或防火墙拦截）：{ex.Message}");
        }
        IsRunning = true;
        _session.AddSink(this);

        // 拥塞控制器（动态码率/分辨率）
        var initialBitrate = _session.Options?.BitrateBps ?? 2_500_000;
        var initialWidth = _session.Source?.Bounds.Width ?? 1920;
        var initialHeight = _session.Source?.Bounds.Height ?? 1080;
        _controller = new CongestionController(initialBitrate, initialWidth, initialHeight);
        _congestionTimer = new Timer(_ =>
        {
            try
            {
                // 汇总各观看者发送/丢帧统计
                long sent = 0, dropped = 0;
                lock (_clientsGate)
                {
                    foreach (var c in _clients.Where(c => c.Authenticated))
                    {
                        var s = Interlocked.Read(ref c.SentFrames);
                        var d = Interlocked.Read(ref c.DroppedFrames);
                        sent += s - Interlocked.Exchange(ref c.LastSentSnapshot, s);
                        dropped += d - Interlocked.Exchange(ref c.LastDroppedSnapshot, d);
                    }
                }
                _controller.OnSendStats((int)Math.Min(sent, int.MaxValue), (int)Math.Min(dropped, int.MaxValue));

                var decision = _controller.Evaluate();
                if (decision.BitrateBps.HasValue)
                    _session.SetDynamicBitrate(decision.BitrateBps.Value);
                if (decision.Width.HasValue && decision.Height.HasValue)
                    _session.SetDynamicResolution(decision.Width.Value, decision.Height.Value);
            }
            catch { }
        }, null, TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(2));

        _ = Task.Run(() => AcceptLoopAsync(_cts.Token));
        Logging.Logger.Info("LanServer", $"LAN 共享服务已启动，端口 {Port}");
    }

    public void Stop()
    {
        if (!IsRunning) return;
        IsRunning = false;
        _session.RemoveSink(this);
        _cts?.Cancel();
        _congestionTimer?.Dispose();
        _congestionTimer = null;
        _controller = null;
        try { _listener?.Stop(); } catch { }
        lock (_clientsGate)
        {
            foreach (var c in _clients) c.Close();
            _clients.Clear();
        }
        Logging.Logger.Info("LanServer", "LAN 共享服务已停止");
    }

    // ===== 接收端接口 =====

    public string Name => $"LAN:{Port}";

    public void OnEncodedFrame(EncodedVideoFrame frame)
    {
        List<ClientSession> targets;
        lock (_clientsGate)
            targets = _clients.Where(c => c.Authenticated).ToList();

        foreach (var c in targets)
        {
            if (!c.TryEnqueueFrame(frame))
            {
                // 队列满：丢弃该观看者的一帧（关键帧到来前可能有花屏，关键帧恢复）
                if (Interlocked.Increment(ref c.DroppedFrames) % 60 == 1)
                    Logging.Logger.Warn("LanServer", $"观看者 {c.DeviceName} 消费慢，开始丢帧");
            }
        }
    }

    public void OnShareStopped(string reason)
    {
        List<ClientSession> targets;
        lock (_clientsGate)
            targets = _clients.ToList();
        foreach (var c in targets)
        {
            try
            {
                var payload = AuthPayload.Serialize(new AuthResultPayload { Ok = false, Reason = $"共享已停止 {reason}" });
                c.Connection.Send(MessageType.ShareStopped, FrameFlags.None, payload);
            }
            catch { }
            c.Close();
        }
    }

    /// <summary>请求向所有观看者发送关键帧</summary>
    private void RequestKeyframe() => _session.RequestKeyframe();

    // ===== 接入循环 =====

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener!.AcceptTcpClientAsync(ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                if (ct.IsCancellationRequested) break;
                Logging.Logger.Warn("LanServer", $"接受连接失败: {ex.Message}");
                continue;
            }

            _ = Task.Run(() => HandleClientAsync(client, ct), ct);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        var remote = (client.Client.RemoteEndPoint as IPEndPoint)?.Address.ToString() ?? "?";
        Logging.Logger.Info("LanServer", $"新连接: {remote}");
        using var conn = new TcpFrameConnection(client);
        var clientSession = new ClientSession(conn, remote);
        lock (_clientsGate) _clients.Add(clientSession);

        try
        {
            if (!await AuthenticateAsync(conn, clientSession, ct))
            {
                clientSession.Close();
                return;
            }

            // 新观看者接入：请求关键帧，保证快速出画面
            RequestKeyframe();

            ViewerCountChanged?.Invoke(GetViewerCount());

            // 认证后启动读循环（处理 Ping/KeyframeRequest/Bye）
            conn.StartReading();

            // 发送任务 + 接收循环
            var senderTask = Task.Run(() =>
            {
                try { SendLoopAsync(clientSession, ct); }
                catch (Exception ex) { Logging.Logger.Warn("LanServer", $"发送任务异常退出: {ex.GetType().Name}: {ex.Message}"); }
            }, ct);
            var receiveTask = Task.Run(() =>
            {
                try { ReceiveLoopAsync(clientSession, ct); }
                catch (Exception ex) { Logging.Logger.Warn("LanServer", $"接收任务异常退出: {ex.GetType().Name}: {ex.Message}"); }
            }, ct);
            await Task.WhenAny(senderTask, receiveTask);
        }
        catch (Exception ex)
        {
            Logging.Logger.Warn("LanServer", $"观看者处理异常: {ex.Message}");
        }
        finally
        {
            clientSession.Close();
            lock (_clientsGate) _clients.Remove(clientSession);
            ViewerCountChanged?.Invoke(GetViewerCount());
            Logging.Logger.Info("LanServer",
                $"观看者离开: {clientSession.DeviceName} ({remote}), " +
                $"发送 {Interlocked.Read(ref clientSession.SentFrames)} 帧, 丢帧 {Interlocked.Read(ref clientSession.DroppedFrames)}");
        }
    }

    /// <summary>认证握手（同步帧读写，带超时）</summary>
    private async Task<bool> AuthenticateAsync(TcpFrameConnection conn, ClientSession client, CancellationToken ct)
    {
        var handshake = Task.Run(() =>
        {
            // 1) AuthRequest
            var first = conn.ReadFrame();
            if (first == null) return false;
            var (hdr, payload) = first.Value;
            if (hdr.Type != MessageType.AuthRequest) return false;
            var req = AuthPayload.Deserialize<AuthRequestPayload>(payload);
            if (req == null || string.IsNullOrEmpty(req.DeviceId)) return false;

            client.DeviceId = req.DeviceId;
            client.DeviceName = req.DeviceName;

            // 2) 白名单检查 + 必要时请求用户批准
            var approved = _whitelist.IsApproved(req.DeviceId);
            if (!approved)
            {
                var info = new ViewerInfo(req.DeviceId, req.DeviceName, client.RemoteAddress);
                var decision = ApproveRequired?.Invoke(info).GetAwaiter().GetResult() ?? false;
                if (!decision)
                {
                    var deny = AuthPayload.Serialize(new AuthResultPayload { Ok = false, Reason = "设备未获批准" });
                    conn.Send(MessageType.AuthResult, FrameFlags.None, deny);
                    Logging.Logger.Info("LanServer", $"已拒绝未批准设备: {req.DeviceName} ({req.DeviceId})");
                    return false;
                }
            }

            // 3) AuthChallenge（盐 + 能力；Host 生成 ECDH 密钥对用于会话加密）
            var salt = Pbkdf2.NewSalt();
            byte[]? hostPub = null;
            EcdhKeyExchange? ecdh = null;
            if (_session.Password.Length > 0)
            {
                ecdh = new EcdhKeyExchange();
                hostPub = ecdh.ExportPublicKey();
            }
            var challenge = AuthPayload.Serialize(new AuthChallengePayload
            {
                SaltB64 = Convert.ToBase64String(salt),
                SupportsEncryption = hostPub != null,
                HostPubB64 = hostPub != null ? Convert.ToBase64String(hostPub) : "",
            });
            conn.Send(MessageType.AuthChallenge, FrameFlags.None, challenge);

            // 4) AuthProof（带 10 秒超时读取）
            var proofTask = Task.Run(() => conn.ReadFrame());
            if (!proofTask.Wait(TimeSpan.FromSeconds(10))) return false;
            var proofFrame = proofTask.Result;
            if (proofFrame == null || proofFrame.Value.Header.Type != MessageType.AuthProof) return false;
            var proof = AuthPayload.Deserialize<AuthProofPayload>(proofFrame.Value.Payload);
            if (proof == null) return false;

            // 5) 常量时间校验（证明必须绑定客户端公钥，防中间人替换密钥）
            var key = Pbkdf2.Derive(_session.Password, salt);
            byte[]? clientPubActual = !string.IsNullOrEmpty(proof.ClientPubB64)
                ? Convert.FromBase64String(proof.ClientPubB64)
                : null;
            var expected = AuthMath.ComputeProof(key, salt, req.DeviceId, hostPub, clientPubActual);
            var actual = Convert.FromBase64String(proof.ProofB64);
            if (!Pbkdf2.FixedTimeEquals(expected, actual))
            {
                var fail = AuthPayload.Serialize(new AuthResultPayload { Ok = false, Reason = "密码错误" });
                conn.Send(MessageType.AuthResult, FrameFlags.None, fail);
                Logging.Logger.Warn("LanServer", $"密码校验失败: {req.DeviceName} ({client.RemoteAddress})");
                return false;
            }

            // 6) 会话加密（客户端提供公钥 → 派生 AES-256-GCM 密钥）
            var encEnabled = false;
            if (ecdh != null && !string.IsNullOrEmpty(proof.ClientPubB64))
            {
                try
                {
                    var clientPub = Convert.FromBase64String(proof.ClientPubB64);
                    var aesKey = ecdh.DeriveSessionKey(clientPub, salt);
                    conn.EnableEncryption(new AesGcmSession(aesKey));
                    encEnabled = true;
                    Logging.Logger.Info("LanServer", $"会话加密已启用 (AES-256-GCM): {client.DeviceName}");
                }
                catch (Exception ex)
                {
                    Logging.Logger.Warn("LanServer", $"加密协商失败（回退明文，认证已通过）: {ex.Message}");
                }
            }
            ecdh?.Dispose();

            var result = AuthPayload.Serialize(new AuthResultPayload
            {
                Ok = true,
                EncryptionEnabled = encEnabled,
                EncoderName = _session.EncoderName,
                Width = _session.Source?.Bounds.Width ?? 0,
                Height = _session.Source?.Bounds.Height ?? 0,
            });
            conn.Send(MessageType.AuthResult, FrameFlags.None, result);

            client.Authenticated = true;
            Logging.Logger.Info("LanServer", $"观看者接入成功: {client.DeviceName} ({client.RemoteAddress}) 加密={encEnabled}");
            return true;
        }, ct);

        // 整个握手限时 15 秒
        var completed = await Task.WhenAny(handshake, Task.Delay(TimeSpan.FromSeconds(15), ct));
        if (completed != handshake || !handshake.Result)
        {
            Logging.Logger.Warn("LanServer", $"认证握手失败/超时: {client.RemoteAddress}");
            return false;
        }
        return true;
    }

    /// <summary>接收循环：处理观看者控制消息（Ping/KeyframeRequest/Bye）</summary>
    private void ReceiveLoopAsync(ClientSession client, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Connection.FrameReceived += (hdr, payload) =>
        {
            switch (hdr.Type)
            {
                case MessageType.Ping:
                    // 原样回 Pong（payload 含时间戳）
                    try { client.Connection.Send(MessageType.Pong, FrameFlags.None, payload); } catch { }
                    break;
                case MessageType.StatsInfo:
                    // 观看者 RTT 反馈 → 拥塞控制
                    try
                    {
                        var stats = AuthPayload.Deserialize<StatsInfoPayload>(payload);
                        if (stats != null && !double.IsNaN(stats.RttMs) && stats.RttMs > 0)
                            _controller?.OnRttSample(stats.RttMs);
                    }
                    catch { }
                    break;
                case MessageType.KeyframeRequest:
                    RequestKeyframe();
                    break;
                case MessageType.Bye:
                    tcs.TrySetResult();
                    break;
            }
        };
        client.Connection.Disconnected += () => tcs.TrySetResult();
        tcs.Task.Wait(ct);
    }

    /// <summary>发送循环：从队列取帧发送</summary>
    private void SendLoopAsync(ClientSession client, CancellationToken ct)
    {
        var statsInterval = TimeSpan.FromSeconds(1);
        var lastStats = DateTime.MinValue;
        while (IsRunning && !ct.IsCancellationRequested && client.Connection.IsConnected)
        {
            EncodedVideoFrame? frame = null;
            var took = false;
            try
            {
                took = client.Queue.TryTake(out frame, 200, ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Logging.Logger.Warn("LanServer", $"TryTake 异常: {ex.GetType().Name}: {ex.Message}");
                break;
            }
            if (frame == null) continue;

            var flags = frame.Keyframe ? FrameFlags.Keyframe : FrameFlags.None;
            try
            {
                client.Connection.Send(MessageType.VideoFrame, flags, frame.Data);
                Interlocked.Increment(ref client.SentFrames);
            }
            catch (Exception ex)
            {
                Logging.Logger.Warn("LanServer", $"视频帧发送失败: {ex.GetType().Name}: {ex.Message}");
                break;
            }

            if (DateTime.UtcNow - lastStats > statsInterval)
            {
                lastStats = DateTime.UtcNow;
                try
                {
                    var stats = AuthPayload.Serialize(new StatsInfoPayload
                    {
                        EncoderName = _session.EncoderName,
                        Hardware = _session.IsHardwareEncoder,
                        SourceTitle = _session.Source?.Title ?? "",
                    });
                    client.Connection.Send(MessageType.StatsInfo, FrameFlags.None, stats);
                }
                catch (Exception ex)
                {
                    Logging.Logger.Warn("LanServer", $"统计帧发送失败: {ex.GetType().Name}: {ex.Message}");
                    break;
                }
            }
        }
    }

    public int GetViewerCount()
    {
        lock (_clientsGate)
            return _clients.Count(c => c.Authenticated);
    }

    public void Dispose() => Stop();

    /// <summary>单观看者会话（连接 + 发送队列）</summary>
    internal sealed class ClientSession
    {
        public TcpFrameConnection Connection { get; }
        public string RemoteAddress { get; }
        public string DeviceId { get; set; } = "";
        public string DeviceName { get; set; } = "";
        public bool Authenticated { get; set; }
        public long SentFrames;
        public long DroppedFrames;
        /// <summary>拥塞控制统计快照</summary>
        public long LastSentSnapshot;
        public long LastDroppedSnapshot;
        /// <summary>有界发送队列（约 8 秒缓冲；满则丢帧）</summary>
        public BlockingCollection<EncodedVideoFrame> Queue { get; } = new(240);

        public ClientSession(TcpFrameConnection connection, string remote)
        {
            Connection = connection;
            RemoteAddress = remote;
        }

        public bool TryEnqueueFrame(EncodedVideoFrame frame)
        {
            try
            {
                return Queue.TryAdd(frame);
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }

        public void Close()
        {
            try { Queue.CompleteAdding(); } catch { }
            Connection.Dispose();
        }
    }
}
