using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using WindowShare.Core.Audio;
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
///   - 新观看者接入时补发缓存的 GOP（自上一个 IDR 起的全部帧），编码器不认关键帧请求也能秒开；
///   - 同时仍请求关键帧；处理 Ping/KeyframeRequest/Bye。
/// 只读共享协议：不存在任何输入/控制消息。
/// </summary>
public sealed class LanShareServer : ShareSession.IFrameSink, ShareSession.IAudioSink, IDisposable
{
    public const int DefaultPort = 48750;

    /// <summary>未认证并发连接上限：握手中的连接最多这么多，超出直接断开（防连接洪泛）</summary>
    internal const int MaxConcurrentUnauthenticated = 4;

    /// <summary>已认证观看者上限（超出拒绝接入；正常使用远达不到）</summary>
    internal const int MaxAuthenticatedViewers = 16;

    private readonly ShareSession _session;
    private readonly DeviceWhitelist _whitelist;
    private readonly string? _bindAddress;
    private readonly AuthRateLimiter _rateLimiter = new();
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private CongestionController? _controller;
    private Timer? _congestionTimer;
    private readonly List<ClientSession> _clients = new();
    private readonly object _clientsGate = new();
    // GOP 缓存 + 专用锁：观看者接入时把「自上一个 IDR 起的帧」整段补发过去。
    // 锁的用途见 OnEncodedFrame / MarkAuthenticated 的注释（保证补发与直发严格有序）。
    private readonly GopCache _gopCache = new();
    private readonly object _gopGate = new();

    /// <summary>新观看者需要批准（返回 true 允许；Host UI 弹窗实现）</summary>
    public Func<ViewerInfo, Task<bool>>? ApproveRequired;

    /// <summary>观看者数量变化</summary>
    public event Action<int>? ViewerCountChanged;

    public bool IsRunning { get; private set; }
    public int Port { get; }

    /// <summary>
    /// <paramref name="bindAddress"/>：监听地址（如 "192.168.1.10" 只在内网网卡监听）；
    /// null/空/解析失败 = 所有 IPv4 网卡（默认行为）。
    /// </summary>
    public LanShareServer(ShareSession session, DeviceWhitelist whitelist, int port = DefaultPort,
        string? bindAddress = null)
    {
        _session = session;
        _whitelist = whitelist;
        _bindAddress = string.IsNullOrWhiteSpace(bindAddress) ? null : bindAddress.Trim();
        Port = port;
    }

    public void Start()
    {
        if (IsRunning) return;
        IPAddress bindIp = IPAddress.Any;
        if (_bindAddress != null && (!IPAddress.TryParse(_bindAddress, out bindIp!) || bindIp.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork))
        {
            Logging.Logger.Warn("LanServer", $"监听地址 {_bindAddress} 无效，回退到所有网卡");
            bindIp = IPAddress.Any;
        }
        _cts = new CancellationTokenSource();
        _listener = new TcpListener(bindIp, Port);
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
        Logging.Logger.Info("LanServer",
            $"LAN 共享服务已启动，端口 {Port}，监听 {(bindIp == IPAddress.Any ? "所有网卡" : bindIp)}");
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
        lock (_gopGate) _gopCache.Clear();
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
        // 追加缓存与分发必须在同一把锁内：否则「给新观看者补发的最后一帧」与
        // 「给老观看者直发的同一帧」会交错，新观看者可能收到重复帧或缺参考帧而花屏。
        // 入队是 BlockingCollection.TryAdd（非阻塞），持锁时间可忽略；日志挪到锁外。
        var slowClients = new List<ClientSession>();
        lock (_gopGate)
        {
            _gopCache.Add(frame);

            List<ClientSession> targets;
            lock (_clientsGate)
                targets = _clients.Where(c => c.Authenticated).ToList();

            foreach (var c in targets)
            {
                if (!c.TryEnqueueFrame(frame))
                {
                    // 队列满：丢弃该观看者的一帧（下一个 IDR 到来前可能有花屏，之后恢复）
                    if (Interlocked.Increment(ref c.DroppedFrames) % 60 == 1)
                        slowClients.Add(c);
                }
            }
        }

        foreach (var c in slowClients)
            Logging.Logger.Warn("LanServer", $"观看者 {c.DeviceName} 消费慢，开始丢帧");
    }

    /// <summary>
    /// 分发一帧系统声音。音频与视频走各自独立的队列和发送循环：
    /// 视频队列积压时不该把声音一起拖住（人耳对卡顿远比眼睛敏感），
    /// 反之音频积压也不该挤占视频的发送时机。TCP 层对发送加了锁，两个循环并发写是安全的。
    /// </summary>
    public void OnAudioFrame(EncodedAudioFrame frame)
    {
        List<ClientSession> targets;
        lock (_clientsGate)
            targets = _clients.Where(c => c.Authenticated).ToList();
        if (targets.Count == 0) return;

        foreach (var c in targets)
        {
            if (!c.TryEnqueueAudio(frame) && Interlocked.Increment(ref c.DroppedAudioFrames) % 100 == 1)
                Logging.Logger.Warn("LanServer", $"观看者 {c.DeviceName} 音频队列已满，开始丢音频帧");
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

    /// <summary>当前 GOP 缓存的帧数（诊断 / 测试用）</summary>
    public int CachedGopFrames
    {
        get { lock (_gopGate) return _gopCache.Count; }
    }

    /// <summary>
    /// 标记观看者已认证，并立刻把缓存的 GOP（自上一个 IDR 起的全部帧）补发给它。
    /// 编码器可能不认 CODECAPI_AVEncVideoForceKeyFrame（本机 Microsoft AVC DX12 Encoder 实测如此），
    /// 此时 IDR 只按编码器内部 GOP 周期出现，静态桌面下可能要黑屏等好几秒；补发缓存即可秒开。
    /// 必须与 OnEncodedFrame 共用 _gopGate，保证补发的最后一帧与随后直发的第一帧不重不漏。
    /// </summary>
    private void MarkAuthenticated(ClientSession client)
    {
        EncodedVideoFrame[] replay;
        lock (_gopGate)
        {
            client.Authenticated = true;
            replay = _gopCache.GetReplayFrames();
            foreach (var f in replay)
                client.TryEnqueueFrame(f);
        }

        Logging.Logger.Info("LanServer", replay.Length > 0
            ? $"已为 {client.DeviceName} 补发缓存 GOP {replay.Length} 帧（接入即出画面，无需等待下一个关键帧）"
            : $"GOP 缓存为空，{client.DeviceName} 需等待下一个关键帧");
    }

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

            // 连接数上限：不认证直接断开，不给洪泛连接消耗线程与内存的机会
            lock (_clientsGate)
            {
                var total = _clients.Count;
                var unauthenticated = _clients.Count(c => !c.Authenticated);
                if (unauthenticated >= MaxConcurrentUnauthenticated || total >= MaxAuthenticatedViewers)
                {
                    var remote = (client.Client.RemoteEndPoint as IPEndPoint)?.Address;
                    Logging.Logger.Warn("LanServer",
                        $"连接数超限（总 {total}/未认证 {unauthenticated}），拒绝来自 {remote} 的新连接");
                    client.Dispose();
                    continue;
                }
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
                catch (Exception ex) { LogTaskExit("发送", ex); }
            }, ct);
            // 音频独立发送循环（与视频队列互不阻塞）
            var audioSenderTask = Task.Run(() =>
            {
                try { AudioSendLoopAsync(clientSession, ct); }
                catch (Exception ex) { LogTaskExit("音频发送", ex); }
            }, ct);
            var receiveTask = Task.Run(() =>
            {
                try { ReceiveLoopAsync(clientSession, ct); }
                catch (Exception ex) { LogTaskExit("接收", ex); }
            }, ct);
            await Task.WhenAny(senderTask, audioSenderTask, receiveTask);
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
            // 0) 限流：冷却期内的 IP 直接拒绝（放在最前面，避免触发白名单弹窗干扰用户）
            if (_rateLimiter.IsBlocked(client.RemoteAddress))
            {
                var denyMsg = AuthPayload.Serialize(new AuthResultPayload
                { Ok = false, Reason = "认证失败次数过多，请稍后再试" });
                try { conn.Send(MessageType.AuthResult, FrameFlags.None, denyMsg); } catch { }
                Logging.Logger.Warn("LanServer", $"IP {client.RemoteAddress} 认证尝试过于频繁，已拒绝");
                return false;
            }

            // 1) AuthRequest
            var first = conn.ReadFrame();
            if (first == null) return false;
            var (hdr, payload) = first.Value;
            if (hdr.Type != MessageType.AuthRequest) return false;
            var req = AuthPayload.Deserialize<AuthRequestPayload>(payload);
            if (req == null || string.IsNullOrEmpty(req.DeviceId)) return false;

            client.DeviceId = req.DeviceId;
            client.DeviceName = req.DeviceName;
            // 帧头 AAD 绑定 + 防重放：仅对表明 ≥1.3 的观看端启用（旧版本维持旧加密格式）
            var aadBinding = PeerCapability.SupportsAadBinding(req.AppVersion);

            // 2) 会话编码协商：HEVC 会话仅放行声明支持 HEVC 解码的观看端（老版本不带字段 = 不支持）
            var sessionCodec = _session.Options?.Codec ?? VideoCodec.H264;
            if (sessionCodec == VideoCodec.Hevc && !req.HevcSupported)
            {
                var incompatible = AuthPayload.Serialize(new AuthResultPayload
                {
                    Ok = false,
                    Reason = "Host 正在以 HEVC 编码共享，观看端不支持或版本过旧；请升级观看端或在 Host 取消「HEVC 优先」",
                });
                conn.Send(MessageType.AuthResult, FrameFlags.None, incompatible);
                Logging.Logger.Info("LanServer",
                    $"已拒绝不支持 HEVC 的观看端: {req.DeviceName} ({req.DeviceId})");
                return false;
            }

            // 3) 白名单检查 + 必要时请求用户批准
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
                _rateLimiter.RecordFailure(client.RemoteAddress);
                Logging.Logger.Warn("LanServer",
                    $"密码校验失败: {req.DeviceName} ({client.RemoteAddress})" +
                    (_rateLimiter.IsBlocked(client.RemoteAddress) ? "，该 IP 已进入冷却期" : ""));
                return false;
            }

            // 6) 会话加密（强制：质询提供了加密能力而观看端不配合 → 拒绝，绝不回退明文。
            //    否则「已认证」的连接会以明文跑完整个会话，加密形同虚设）
            var encEnabled = false;
            if (ecdh != null)
            {
                if (string.IsNullOrEmpty(proof.ClientPubB64))
                {
                    var reject = AuthPayload.Serialize(new AuthResultPayload
                    { Ok = false, Reason = "观看端未提供加密公钥（Host 已强制加密）" });
                    conn.Send(MessageType.AuthResult, FrameFlags.None, reject);
                    Logging.Logger.Warn("LanServer",
                        $"观看端 {req.DeviceName} 认证通过但拒绝加密，已断开（不回退明文）");
                    return false;
                }
                try
                {
                    var clientPub = Convert.FromBase64String(proof.ClientPubB64);
                    var aesKey = ecdh.DeriveSessionKey(clientPub, salt);
                    conn.EnableEncryption(new AesGcmSession(aesKey));
                    conn.UseAadBinding = aadBinding;
                    encEnabled = true;
                    Logging.Logger.Info("LanServer",
                        $"会话加密已启用 (AES-256-GCM{(aadBinding ? "+AAD" : "")}): {client.DeviceName}");
                }
                catch (Exception ex)
                {
                    var fail2 = AuthPayload.Serialize(new AuthResultPayload
                    { Ok = false, Reason = "会话加密协商失败" });
                    conn.Send(MessageType.AuthResult, FrameFlags.None, fail2);
                    Logging.Logger.Warn("LanServer",
                        $"加密协商失败（拒绝该连接，不回退明文）: {ex.Message}");
                    return false;
                }
            }
            ecdh?.Dispose();

            var audioInfo = _session.AudioInfo;
            var result = AuthPayload.Serialize(new AuthResultPayload
            {
                Ok = true,
                EncryptionEnabled = encEnabled,
                AadBindingEnabled = encEnabled && aadBinding,
                EncoderName = _session.EncoderName,
                Width = _session.Source?.Bounds.Width ?? 0,
                Height = _session.Source?.Bounds.Height ?? 0,
                TargetBitrateBps = _session.Options?.BitrateBps ?? 0,
                Fps = _session.Options?.Fps ?? 0,
                VideoCodecWireName = sessionCodec.ToWireName(),
                AudioEnabled = audioInfo.Enabled,
                AudioSampleRate = audioInfo.SampleRate,
                AudioChannels = audioInfo.Channels,
                AudioCodec = audioInfo.Codec,
                AudioEncoderName = audioInfo.EncoderName,
            });
            conn.Send(MessageType.AuthResult, FrameFlags.None, result);

            _rateLimiter.RecordSuccess(client.RemoteAddress);
            MarkAuthenticated(client);
            Logging.Logger.Info("LanServer",
                $"观看者接入成功: {client.DeviceName} ({client.RemoteAddress}) 加密={encEnabled} " +
                $"系统声音={(audioInfo.Enabled ? $"{audioInfo.SampleRate}Hz/{audioInfo.Channels}ch {audioInfo.Codec}" : "未共享")}");
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
                        TargetBitrateBps = _controller.CurrentBitrateBps,
                        Fps = _session.Options?.Fps ?? 0,
                        Downgraded = _controller.IsDowngraded,
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

    /// <summary>收发任务退出：随会话停止的取消是正常路径（Debug），真正的异常才 Warn</summary>
    private static void LogTaskExit(string role, Exception ex)
    {
        if (ex is OperationCanceledException)
            Logging.Logger.Debug("LanServer", $"{role}任务随会话停止而结束");
        else
            Logging.Logger.Warn("LanServer", $"{role}任务异常退出: {ex.GetType().Name}: {ex.Message}");
    }

    public int GetViewerCount()
    {
        lock (_clientsGate)
            return _clients.Count(c => c.Authenticated);
    }

    /// <summary>
    /// 音频发送循环：独立于视频，只服务音频队列。
    /// 帧头里的 TimestampUtc 由 TcpFrameConnection.Send 统一填 DateTime.UtcNow，
    /// 而音画同步需要的是「采集时刻」，所以这里走带时间戳的重载把采集时间戳原样带过去。
    /// </summary>
    private void AudioSendLoopAsync(ClientSession client, CancellationToken ct)
    {
        while (IsRunning && !ct.IsCancellationRequested && client.Connection.IsConnected)
        {
            EncodedAudioFrame? frame = null;
            try
            {
                if (!client.AudioQueue.TryTake(out frame, 200, ct)) continue;
            }
            catch (OperationCanceledException) { break; }
            catch (InvalidOperationException) { break; }   // 队列已 CompleteAdding
            catch (Exception ex)
            {
                Logging.Logger.Warn("LanServer", $"音频 TryTake 异常: {ex.GetType().Name}: {ex.Message}");
                break;
            }
            if (frame == null) continue;

            try
            {
                client.Connection.Send(MessageType.AudioFrame, FrameFlags.None, frame.TimestampUtc, frame.Data);
                Interlocked.Increment(ref client.SentAudioFrames);
            }
            catch (Exception ex)
            {
                Logging.Logger.Warn("LanServer", $"音频帧发送失败: {ex.GetType().Name}: {ex.Message}");
                break;
            }
        }
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
        /// <summary>
        /// 音频专用有界队列。50 帧 ≈ 1 秒（20ms 一帧）：音频积压到这个量已经明显听得出延迟，
        /// 与其继续攒不如丢掉，让 Viewer 端重新蓄水后对齐。
        /// </summary>
        public BlockingCollection<EncodedAudioFrame> AudioQueue { get; } = new(50);
        public long SentAudioFrames;
        public long DroppedAudioFrames;

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

        public bool TryEnqueueAudio(EncodedAudioFrame frame)
        {
            try
            {
                return AudioQueue.TryAdd(frame);
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }

        public void Close()
        {
            try { Queue.CompleteAdding(); } catch { }
            try { AudioQueue.CompleteAdding(); } catch { }
            Connection.Dispose();
        }
    }
}
