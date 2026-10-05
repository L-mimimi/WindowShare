using System.Collections.Concurrent;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Windows.Controls;
using WindowShare.Core.Audio;
using WindowShare.Core.Decoding;
using WindowShare.Core.Encoding;
using WindowShare.Core.Logging;

using WindowShare.Core.Network;
using WindowShare.Core.Stats;
using WindowShare.Core.WebRtc;
using WindowShare.Core.Signaling;
using WindowShare.Core.Utils;

namespace WindowShare.Viewer;

/// <summary>
/// Viewer 主窗口：
///   连接（直连 IP 或房间号）→ 接收 H.264 → MF 解码 → WriteableBitmap 显示 →
///   状态栏实时显示连接状态/传输方式/码率/帧率/延迟/分辨率/加密状态。
/// </summary>
public partial class MainWindow : Window
{
    private LanShareClient? _client;
    private ViewerSignalingClient? _signaling;
    private WebRtcViewerReceiver? _webRtcReceiver;
    private IVideoDecoder? _decoder;
    private readonly StatsCollector _stats = new();
    private readonly DispatcherTimer _uiTimer;
    private WriteableBitmap? _bitmap;
    private double _lastRttMs = double.NaN;
    private volatile bool _firstKeyframeSeen;
    /// <summary>Host 侧画质参数（StatsInfo 周期携带；0=未知，如旧版 Host 或 WebRTC 路径）</summary>
    private int _hostTargetBitrateBps;
    private bool _hostDowngraded;
    /// <summary>当前解码器对应的编码（Host 协商为 HEVC 时自动换解码器）</summary>
    private VideoCodec _decoderCodec = VideoCodec.H264;
    /// <summary>HEVC 解码能力（子进程探针实测；null=探测中/未知 → 按 H.264 观看端接入）</summary>
    private bool? _hevcSupported;
    /// <summary>FFmpeg 软解兜底可用（进程内实测，DLL 随应用分发，理论上恒可用）</summary>
    private bool _ffmpegHevcOk;
    /// <summary>MF 解码器会话中已炸过（托管异常）→ 强制换 FFmpeg 兜底，一次会话只换一次</summary>
    private bool _mfDecoderBroken;
    /// <summary>解码器创建失败过 → 本会话不再反复尝试（避免每帧异常刷屏），状态栏已说明</summary>
    private bool _decoderCreateFailed;
    /// <summary>当前连接是否处于 Connected（看门狗提示用）</summary>
    private bool _isConnected;
    private DateTime? _connectedAtUtc;

    /// <summary>用户选择的解码路径（设置持久化）</summary>
    private DecoderPreference Preference => _settings.DecoderPreference switch
    {
        "mf" => DecoderPreference.MediaFoundation,
        "ffmpeg" => DecoderPreference.Ffmpeg,
        _ => DecoderPreference.Auto,
    };
    /// <summary>系统声音播放管线（解码 + 抖动缓冲 + 渲染 + 音画同步主时钟）</summary>
    private AudioPlaybackPipeline? _audio;
    /// <summary>待上屏的解码帧队列（音画同步需要「等到点再上屏」，不能在解码回调里直接画）</summary>
    private readonly BlockingCollection<DecodedVideoFrame> _presentQueue = new(16);
    private Thread? _presentThread;
    private CancellationTokenSource? _presentCts;
    private readonly ViewerSettings _settings = ViewerSettings.Load();
    /// <summary>恢复默认值过程中触发的「改动」不回写（避免启动时连写多次文件）</summary>
    private bool _restoringSettings;
    /// <summary>LAN 发现监听器（启动即监听，退出时停）</summary>
    private readonly DiscoveryListener _discovery = new();
    /// <summary>发现列表快照（按 Index 取回完整条目用）</summary>
    private IReadOnlyList<DiscoveredHost> _discovered = new List<DiscoveredHost>();

    public MainWindow()
    {
        InitializeComponent();
        _ = ProbeHevcCapabilityAsync(); // 后台子进程探测，结果缓存进设置
        _uiTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _uiTimer.Tick += (_, _) => UpdateStatsBar();
        _uiTimer.Start();
        TxtMode.Text = AppPaths.ModeDescription;
        TxtMode.ToolTip = AppPaths.Root;
        ApplySettings();
        ApplyConnMode();
        Logger.LogEmitted += OnLogEmitted;
        StartDiscovery();
        Closing += (_, _) =>
        {
            SaveSettings();
            _discovery.Stop();
        };
    }

    // ===== 局域网发现（Host 开了信标就自动列出，双击直连）=====

    private void StartDiscovery()
    {
        try
        {
            _discovery.Start();
            _uiTimer.Tick += (_, _) => RefreshDiscoveryList();
            RefreshDiscoveryList();
        }
        catch (Exception ex)
        {
            Logger.Warn("Viewer", "局域网发现启动失败（不影响手动直连）: " + ex.Message);
        }
    }

    /// <summary>把发现表刷进列表；不可用或无结果时隐藏整块，不占界面</summary>
    private void RefreshDiscoveryList()
    {
        var hosts = _discovery.Snapshot();
        _discovered = hosts;
        Dispatcher.BeginInvoke(() =>
        {
            if (hosts.Count == 0)
            {
                PanelDiscovery.Visibility = Visibility.Collapsed;
                TxtDiscovery.Text = "";
                return;
            }
            PanelDiscovery.Visibility = Visibility.Visible;
            TxtDiscovery.Text = $"已发现 {hosts.Count} 台";
            var selected = LstDiscovered.SelectedIndex;
            LstDiscovered.ItemsSource = hosts
                .Select(h => $"{h.Name}  —  {h.Address}:{h.Port}")
                .ToList();
            if (selected >= 0 && selected < hosts.Count) LstDiscovered.SelectedIndex = selected;
        });
    }

    /// <summary>双击发现的共享端 → 填入直连地址并立即连接（密码仍需手输，绝不走发现通道）</summary>
    private void LstDiscovered_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        var index = LstDiscovered.SelectedIndex;
        if (index < 0 || index >= _discovered.Count) return;
        var host = _discovered[index];
        RbDirect.IsChecked = true;
        TxtHost.Text = host.Address;
        TxtPort.Text = host.Port.ToString();
        TxtPwd.Focus();
    }

    // ===== 设置持久化（连接方式/地址/端口/信令/声音开关，重启后恢复上次配置；密码不落盘）=====

    private void ApplySettings()
    {
        _restoringSettings = true;
        try
        {
            RbRoom.IsChecked = _settings.RoomMode;
            RbDirect.IsChecked = !_settings.RoomMode;
            TxtHost.Text = string.IsNullOrWhiteSpace(_settings.Host) ? "127.0.0.1" : _settings.Host;
            TxtPort.Text = _settings.Port is >= 1 and <= 65535 ? _settings.Port.ToString() : "48750";
            TxtRoom.Text = _settings.Room;
            TxtSignaling.Text = string.IsNullOrWhiteSpace(_settings.SignalingUrl)
                ? "http://localhost:5000" : _settings.SignalingUrl;
            ChkAudioPlay.IsChecked = _settings.PlayAudio;
            CboDecoder.SelectedIndex = _settings.DecoderPreference switch
            {
                "mf" => 1,
                "ffmpeg" => 2,
                _ => 0,
            };
        }
        finally { _restoringSettings = false; }
    }

    private void SaveSettings()
    {
        _settings.RoomMode = RbRoom.IsChecked == true;
        _settings.Host = TxtHost.Text.Trim();
        _settings.Port = int.TryParse(TxtPort.Text.Trim(), out var p) ? p : 48750;
        _settings.Room = TxtRoom.Text.Trim().ToUpperInvariant();
        _settings.SignalingUrl = TxtSignaling.Text.Trim();
        _settings.PlayAudio = ChkAudioPlay.IsChecked == true;
        _settings.DecoderPreference = CboDecoder.SelectedIndex switch
        {
            1 => "mf",
            2 => "ffmpeg",
            _ => "auto",
        };
        _settings.Save();
    }

    private void CboDecoder_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_restoringSettings) return;
        SaveSettings();
        Logger.Info("Viewer", $"解码器选择变更: {Preference}");
    }

    // ===== 连接 =====

    private void BtnConnect_Click(object sender, RoutedEventArgs e)
    {
        SaveSettings();   // 记住本次输入（即使连接失败，地址/端口也保留）
        // 按当前选中的模式分发：两种模式各有独立的密码输入框，互不干扰
        if (RbRoom.IsChecked == true) _ = ConnectRoomFlowAsync();
        else ConnectDirectFlow();
    }

    /// <summary>直连 IP 模式：校验地址/端口/密码后建立 LAN TCP 会话</summary>
    private void ConnectDirectFlow()
    {
        var host = TxtHost.Text.Trim();
        var password = TxtPwd.Password;

        if (host.Length == 0)
        {
            MessageBox.Show("请输入 Host 的 IP 地址或主机名。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (!System.Net.IPAddress.TryParse(host, out _))
        {
            try
            {
                var addrs = System.Net.Dns.GetHostAddresses(host);
                if (addrs.Length == 0) throw new Exception("无解析结果");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"地址无效：{ex.Message}", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
        }
        if (!int.TryParse(TxtPort.Text.Trim(), out var port) || port is < 1 or > 65535)
        {
            MessageBox.Show("端口无效（1-65535，默认 48750）。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (string.IsNullOrEmpty(password))
        {
            MessageBox.Show("请输入密码（Host 界面「会话信息」中显示）。",
                "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        PrepareSession();
        StartSession(host, port, password);
    }

    /// <summary>
    /// 房间号模式：校验输入 → 信令加入房间 → Host 审批 → 取 LAN 端点直连，
    /// 全部失败回退 WebRTC。失败时恢复按钮状态，便于修正输入后重试。
    /// </summary>
    private async System.Threading.Tasks.Task ConnectRoomFlowAsync()
    {
        var room = TxtRoom.Text.Trim().ToUpperInvariant();
        var password = TxtRoomPwd.Password;
        var signalingUrl = TxtSignaling.Text.Trim();

        if (room.Length < 4)
        {
            MessageBox.Show("请输入房间号（Host 界面「会话信息」中显示，6 位字母数字）。",
                "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (string.IsNullOrEmpty(password))
        {
            MessageBox.Show("请输入房间密码（Host 界面「会话信息」中显示）。",
                "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (!signalingUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !signalingUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show($"信令服务器地址需以 http:// 或 https:// 开头。\n当前：{(signalingUrl.Length == 0 ? "（空）" : signalingUrl)}",
                "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        PrepareSession();
        try
        {
            await ConnectViaRoomAsync(signalingUrl, room, password);
        }
        catch (Exception ex)
        {
            Logger.Error("Viewer", "房间号模式连接异常", ex);
            TxtState.Text = $"状态：连接失败（{ex.Message}）";
        }
        finally
        {
            // 既没有 LAN 客户端也没有 WebRTC 接收器 → 本次接入未建立，恢复按钮让用户重试
            if (_client == null && _webRtcReceiver == null)
            {
                BtnConnect.IsEnabled = true;
                BtnDisconnect.IsEnabled = false;
                TxtPlaceholder.Visibility = Visibility.Visible;
            }
        }
    }

    /// <summary>建立新会话前的公共准备（两种模式共用）：清理旧会话、重建解码器、切换按钮状态</summary>
    private void PrepareSession()
    {
        TeardownSession();

        _firstKeyframeSeen = false;
        _mfDecoderBroken = false;
        _decoderCreateFailed = false;
        _decoderCodec = VideoCodec.H264;
        _decoder = VideoDecoderFactory.Create(VideoCodec.H264, Preference, hevcMfAvailable: false);
        _decoder.Decoded += OnDecodedFrame;
        StartPresentThread();

        BtnConnect.IsEnabled = false;
        BtnDisconnect.IsEnabled = true;
        TxtPlaceholder.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// 子进程实测本机 HEVC 解码能力并缓存。探测可能触发部分平台扩展 MFT 的
    /// 原生崩溃（AccessViolation 不可捕获），所以必须在子进程做，主进程零风险。
    /// </summary>
    private async System.Threading.Tasks.Task ProbeHevcCapabilityAsync()
    {
        // FFmpeg 软解兜底：进程内毫秒级实测（纯软件路径，无崩溃风险，不缓存）。
        // 它可用 ⇒ HEVC 观看能力恒成立，不再受系统解码组件状态支配。
        var ffReason = FfmpegVideoDecoder.UnavailableReason();
        _ffmpegHevcOk = ffReason == null;
        Logger.Info("Viewer", $"FFmpeg 软解兜底: {(_ffmpegHevcOk ? "可用" : $"不可用（{ffReason}）")}");

        if (_settings.HevcDecodeSupported is bool cached)
        {
            _hevcSupported = cached;
            Logger.Info("Viewer", $"HEVC MF 解码能力（缓存）: {(cached ? "支持" : "不支持")}");
            return;
        }
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return;
            using var ps = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                exe, HevcDecodeProbe.ArgProbe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (ps == null) return;
            if (!ps.WaitForExit(20000))
            {
                try { ps.Kill(entireProcessTree: true); } catch { }
                return;
            }
            _hevcSupported = ps.ExitCode == HevcDecodeProbe.ExitOk;
            _settings.HevcDecodeSupported = _hevcSupported;
            _settings.Save();
            Logger.Info("Viewer", $"HEVC MF 解码能力探测: {(_hevcSupported == true ? "支持" : "不支持")}" +
                                  $"（exit={ps.ExitCode}）");
        }
        catch (Exception ex)
        {
            Logger.Warn("Viewer", $"HEVC 解码能力探测失败（按不支持处理）: {ex.Message}");
        }
        await System.Threading.Tasks.Task.CompletedTask;
    }

    /// <summary>HEVC 观看能力（随解码器选择联动，上报 Host）：
    /// 自动 = MF 探针 ∥ FFmpeg 兜底；仅 MF = 只看 MF 探针；仅 FFmpeg = 只看 FFmpeg 可用性</summary>
    private bool HevcWatchable => Preference switch
    {
        DecoderPreference.MediaFoundation => _hevcSupported == true,
        DecoderPreference.Ffmpeg => _ffmpegHevcOk,
        _ => _hevcSupported == true || _ffmpegHevcOk,
    };

    /// <summary>直连 IP：建立 LAN TCP 会话（调用前需先 PrepareSession）</summary>
    private void StartSession(string host, int port, string password)
    {
        _client = new LanShareClient(host, port,
            AppPaths.GetOrCreateDeviceId(), AppPaths.GetMachineName(), password)
        {
            HevcSupported = HevcWatchable,
        };
        AttachClient(_client);
        _client.Start();
    }

    /// <summary>
    /// 房间号模式：信令服务器 → 审批 → 取 Host LAN 端点 → 逐个尝试直连。
    /// 全部失败则提示（跨网段观看由 WebRTC 通道提供，见 docs/DEPLOY.md）。
    /// </summary>
    private async System.Threading.Tasks.Task ConnectViaRoomAsync(
        string signalingUrl, string room, string password)
    {
        var signaling = new ViewerSignalingClient(signalingUrl);
        signaling.RelayFromHost += (type, payload) => OnRelayFromHost(signaling, room, type, payload);
        signaling.HostStopped += () => Dispatcher.BeginInvoke(() =>
            TxtState.Text = "状态：Host 已停止共享");
        _signaling = signaling;

        HostInfoPayload? info;
        try
        {
            TxtState.Text = "状态：连接信令服务器…";
            info = await signaling.JoinAsync(room, password,
                AppPaths.GetOrCreateDeviceId(), AppPaths.GetMachineName());
        }
        catch (Exception ex)
        {
            TxtState.Text = $"状态：加入房间失败（{ex.Message}）";
            _signaling = null;
            await signaling.DisposeAsync();
            return;
        }
        if (info == null) return; // 被拒绝，JoinFailed 已提示

        TxtState.Text = $"状态：已批准，尝试直连 {info.LanEndpoints.Count} 个端点…";

        string? hevcBlockReason = null;
        foreach (var endpoint in info.LanEndpoints)
        {
            var parts = endpoint.Split(':');
            if (parts.Length != 2 || !int.TryParse(parts[1], out var port)) continue;

            var client = new LanShareClient(parts[0], port,
                AppPaths.GetOrCreateDeviceId(), AppPaths.GetMachineName(), password)
            {
                HevcSupported = HevcWatchable,
            };
            AttachClient(client);

            var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            string? failure = null;
            client.StateChanged += (state, err) =>
            {
                if (state == ConnectionState.Connected) connected.TrySetResult();
                else if (state == ConnectionState.Failed)
                {
                    failure = err;
                    connected.TrySetException(new Exception(err ?? "认证失败"));
                }
            };
            client.Start();

            // 8 秒内未连上则尝试下一个端点
            var connectedInTime = await System.Threading.Tasks.Task.WhenAny(
                connected.Task, System.Threading.Tasks.Task.Delay(8000));
            if (connectedInTime == connected.Task && connected.Task.IsCompletedSuccessfully)
            {
                _client = client;
                return; // 连接成功
            }
            client.Stop();
            client.Dispose();

            // HEVC 拒接是确定性失败（本端不支持解码），WebRTC 回退同样会被拒——
            // 直接把可操作的提示留给用户，不再空转
            if (failure != null && failure.Contains("HEVC", StringComparison.OrdinalIgnoreCase))
            {
                hevcBlockReason = failure;
                break;
            }
        }

        if (hevcBlockReason != null)
        {
            TxtState.Text = $"状态：{hevcBlockReason}";
            Logger.Warn("Viewer", $"观看被拒: {hevcBlockReason}");
            BtnConnect.IsEnabled = true;
            BtnDisconnect.IsEnabled = false;
            return;
        }

        // LAN 直连全部失败 → WebRTC 回退（SDP/ICE 经信令中继，媒体 DTLS-SRTP 端到端）
        await StartWebRtcFallbackAsync(signaling, room);

        // WebRTC 协商期间保持「断开」可用：用户可随时取消，而不是被迫等待
        TxtState.Text = "状态：局域网直连失败，正在尝试 WebRTC…";
        BtnConnect.IsEnabled = false;
        BtnDisconnect.IsEnabled = true;
    }

    /// <summary>WebRTC 回退：请求 Host 发起 offer</summary>
    private async System.Threading.Tasks.Task StartWebRtcFallbackAsync(
        ViewerSignalingClient signaling, string room)
    {
        TeardownVideoOnly();

        _webRtcReceiver = CreateWebRtcReceiver(signaling, room);

        // 请求 Host 创建 offer
        await signaling.RelayToHostAsync(room, "webrtc-request", "");
        Logger.Info("Viewer", "已请求 WebRTC 接入");
    }

    /// <summary>统一创建 WebRTC 接收器（视频 + 音频回调 + ICE/状态中继）</summary>
    private WebRtcViewerReceiver CreateWebRtcReceiver(ViewerSignalingClient signaling, string room)
    {
        var receiver = new WebRtcViewerReceiver(includeAudio: true);
        receiver.FrameReceived += (data, ts) => OnEncodedBytes(data, ts);
        receiver.AudioFrameReceived += (payload, _) => OnWebRtcAudioFrame(payload);
        receiver.LocalIceCandidate += c =>
            _ = signaling.RelayToHostAsync(room, "ice", c);
        receiver.StateChanged += s => Dispatcher.BeginInvoke(() =>
        {
            TxtTransport.Text = _webRtcReceiver?.UsedRelay == true ? "传输：WebRTC 中继(TURN)" : "传输：WebRTC 直连(P2P)";
            // WebRTC 通路接通且协商到音频轨 → 起播声音（音频参数固定 48kHz/立体声）
            if (_webRtcReceiver is { IsConnected: true, AudioNegotiated: true })
                TryStartAudio();
        });
        return receiver;
    }

    /// <summary>
    /// Host 中继消息处理：webrtc-reject/offer/ice。
    /// 注意：本方法在信令收发线程上触发，任何 UI 访问必须经 Dispatcher 调度。
    /// </summary>
    private async void OnRelayFromHost(ViewerSignalingClient signaling, string room, string type, string payload)
    {
        try
        {
            switch (type)
            {
                case "webrtc-reject":
                    // Host 拒绝 WebRTC（如 HEVC 会话）：给出可操作的原因
                    Logger.Warn("Viewer", $"WebRTC 请求被 Host 拒绝: {payload}");
                    Dispatcher.BeginInvoke(() =>
                        TxtState.Text = $"状态：跨网段观看被拒（{payload}）");
                    break;
                case "offer":
                    if (_webRtcReceiver == null)
                    {
                        _webRtcReceiver = CreateWebRtcReceiver(signaling, room);
                    }
                    var answer = await _webRtcReceiver.AcceptOfferAsync(payload);
                    await signaling.RelayToHostAsync(room, "answer", answer);
                    Dispatcher.BeginInvoke(() => TxtPlaceholder.Visibility = Visibility.Collapsed);
                    break;
                case "ice":
                    _webRtcReceiver?.AddIceCandidate(payload);
                    break;
            }
        }
        catch (Exception ex)
        {
            Logger.Error("Viewer", "WebRTC 中继处理异常", ex);
        }
    }

    /// <summary>WebRTC 收到的编码帧 → 解码（与 LAN 路径共用解码器）</summary>
    private void OnEncodedBytes(byte[] data, long timestamp)
    {
        try
        {
            _stats.OnFrame(data.Length);
            var keyframe = Core.Encoding.AnnexB.IsKeyframe(data);
            if (!_firstKeyframeSeen && !keyframe) return;
            _firstKeyframeSeen = true;
            _decoder?.Decode(data, timestamp);
        }
        catch (Exception ex)
        {
            Logger.Warn("Viewer", "WebRTC 解码输入异常: " + ex.Message);
        }
    }

    /// <summary>仅清理视频相关资源（保留信令连接）</summary>
    private void TeardownVideoOnly()
    {
        var receiver = _webRtcReceiver;
        _webRtcReceiver = null;
        if (receiver != null) _ = receiver.DisposeAsync();
    }

    /// <summary>挂接客户端事件（解码/统计/状态）</summary>
    private void AttachClient(LanShareClient client)
    {
        client.StateChanged += OnStateChanged;
        client.FrameReceived += OnFrameReceived;
        client.AudioFrameReceived += OnAudioFrameReceived;
        client.RttUpdated += rtt => _lastRttMs = rtt;
        client.StatsUpdated += s => Dispatcher.BeginInvoke(() =>
        {
            TxtEncoder.Text = $"编码器：{s.EncoderName}{(s.Hardware ? "(硬)" : "(软)")} · 源:{s.SourceTitle}";
            _hostTargetBitrateBps = s.TargetBitrateBps;
            _hostDowngraded = s.Downgraded;
        });
    }

    private void BtnDisconnect_Click(object sender, RoutedEventArgs e)
    {
        TeardownSession();
        TxtState.Text = "状态：未连接";
        TxtPlaceholder.Visibility = Visibility.Visible;
    }

    private void TeardownSession()
    {
        if (_client != null)
        {
            _client.FrameReceived -= OnFrameReceived;
            _client.AudioFrameReceived -= OnAudioFrameReceived;
            _client.StateChanged -= OnStateChanged;
            _client.Stop("用户断开");
            _client.Dispose();
            _client = null;
        }
        var signaling = _signaling;
        _signaling = null;
        if (signaling != null) _ = signaling.DisposeAsync();
        TeardownVideoOnly();
        StopAudio();
        StopPresentThread();
        if (_decoder != null)
        {
            _decoder.Decoded -= OnDecodedFrame;
            _decoder.Dispose();
            _decoder = null;
        }
        BtnConnect.IsEnabled = true;
        BtnDisconnect.IsEnabled = false;
        TxtEncrypt.Text = "加密：-";
        TxtTransport.Text = "传输：-";
        TxtEncoder.Text = "编码器：-";
        _isConnected = false;
        _connectedAtUtc = null;
        _hostTargetBitrateBps = 0;
        _hostDowngraded = false;
        _decoderCodec = VideoCodec.H264;
    }

    // ===== 数据流 =====

    /// <summary>网络线程：H.264/HEVC 帧 → 解码</summary>
    private void OnFrameReceived(Core.Encoding.EncodedVideoFrame frame)
    {
        try
        {
            _stats.OnFrame(frame.Data.Length);
            var codec = _client?.NegotiatedCodec ?? VideoCodec.H264;

            // MF 零输出自动换 FFmpeg：码流在到、解码器却 8 秒无输出 → 判定该平台 MF 路径失效
            //（探针只能测构造，测不出数据路径的缺陷）。仅 Auto 模式自动换；用户显式选 MF 则尊重。
            if (_decoder != null && codec == VideoCodec.Hevc &&
                _decoder.BackendName == "MF" && _decoder.DecodedFrames == 0 &&
                Preference == DecoderPreference.Auto && _ffmpegHevcOk &&
                _connectedAtUtc is { } at0 && DateTime.UtcNow - at0 > TimeSpan.FromSeconds(8))
            {
                _mfDecoderBroken = true;
                Logger.Warn("Viewer", "MF HEVC 解码器 8 秒零输出（码流在到），判定该平台 MF 路径失效，弃用重建");
                var broken = _decoder;
                _decoder = null;
                broken.Decoded -= OnDecodedFrame;
                try { broken.Dispose(); } catch { }
            }

            // 解码器必须在任何帧解码前就位：换解码器在网络线程同步完成。
            // 之前挂在 Dispatcher 上异步换，Host 的 GOP 补发帧（含 IDR）会先到而被旧的
            // H.264 解码器静默吞掉（不抛异常），换完只能干等下一个关键帧——真实桌面
            // 低帧率下可达几十秒黑屏。同步换轨后补发的 IDR 直接送进正确解码器，接入秒开。
            if (_decoder == null)
            {
                if (_decoderCreateFailed) return; // 创建失败过：本会话不再反复尝试（状态栏已说明）
                EnsureDecoderFor(codec);
                if (_decoder == null) return;
            }
            else if (_decoderCodec != codec)
            {
                EnsureDecoderFor(codec);
            }

            var keyframe = Core.Encoding.AnnexB.IsKeyframe(frame.Data, codec);
            if (!_firstKeyframeSeen && !keyframe)
                return; // 丢弃 IDR 之前的帧（解码器需要从关键帧开始）
            _firstKeyframeSeen = true;
            _decoder.Decode(frame.Data, frame.TimestampUtc);
        }
        catch (Exception ex)
        {
            Logger.Warn("Viewer", $"解码输入异常: {ex.Message}");
            // 运行期兜底：MF 解码器在会话中途抛托管异常 → 丢弃它，下一帧重建
            //（Auto 模式会因此拿到 FFmpeg 软解；换码期间丢帧至下一关键帧/GOP 补发）
            if (_decoder?.BackendName == "MF" && !_mfDecoderBroken)
            {
                _mfDecoderBroken = true;
                Logger.Warn("Viewer", "MF 解码器中途异常，已弃用（下一帧按解码器选择重建）");
                var broken = _decoder;
                broken.Decoded -= OnDecodedFrame;
                _decoder = null;
                try { broken.Dispose(); } catch { }
            }
        }
    }

    /// <summary>
    /// 在网络线程同步创建/更换解码器（调用方保证：同一网络线程串行，无并发 Decode）。
    /// 会话中途的重建（MF 异常/零输出弃用后）会自动向 Host 请求重发 GOP，秒恢复画面。
    /// </summary>
    private void EnsureDecoderFor(VideoCodec codec)
    {
        // 进入时若已在解码（_firstKeyframeSeen=true），说明是会话中途重建——
        // 新解码器从下一个 IDR 起才能解，主动请 Host 补发缓存 GOP，不必干等编码器出 IDR
        var midSession = _firstKeyframeSeen;
        var old = _decoder;
        _decoder = null;
        if (old != null)
        {
            old.Decoded -= OnDecodedFrame;
            try { old.Flush(); } catch { }
            try { old.Dispose(); } catch { }
        }
        try
        {
            var d = VideoDecoderFactory.Create(codec, Preference,
                hevcMfAvailable: _hevcSupported == true && !_mfDecoderBroken);
            d.Decoded += OnDecodedFrame;
            _decoder = d;
            _decoderCodec = codec;
            _firstKeyframeSeen = false;
            _decoderCreateFailed = false;
            Dispatcher.BeginInvoke(UpdateTransportLabel);
            Logger.Info("Viewer", $"会话编码为 {codec.DisplayName()}，解码器已就位（{d.BackendName}）");
            if (midSession)
            {
                Logger.Info("Viewer", "会话中途重建解码器 → 请求 Host 重发关键帧/GOP");
                _client?.RequestKeyframe();
            }
        }
        catch (Exception ex)
        {
            _decoderCreateFailed = true;
            Logger.Error("Viewer", $"{codec.DisplayName()} 解码器创建失败: {ex.Message}", ex);
            Dispatcher.BeginInvoke(() =>
                TxtState.Text = $"状态：无法创建 {codec.DisplayName()} 解码器（{ex.Message}）");
        }
    }

    /// <summary>解码线程：BGRA → 上屏队列（何时真正上屏由 PresentLoop 按音频时钟决定）</summary>
    private void OnDecodedFrame(DecodedVideoFrame frame)
    {
        _stats.OnFrame(0); // 帧率样本（字节数已在收包时计入）
        if (_presentQueue.IsAddingCompleted) return;
        if (!_presentQueue.TryAdd(frame))
        {
            // 队列满（上屏线程可能正卡在同步等待里）：丢最旧的一帧保住实时性
            if (_presentQueue.TryTake(out _)) _presentQueue.TryAdd(frame);
        }
    }

    /// <summary>状态变化</summary>
    private void OnStateChanged(ConnectionState state, string? error)
    {
        Dispatcher.BeginInvoke(() =>
        {
            TxtState.Text = state switch
            {
                ConnectionState.Disconnected => "状态：未连接",
                ConnectionState.Connecting => "状态：连接中…",
                ConnectionState.Authenticating => "状态：认证中…",
                ConnectionState.Connected => "状态：已连接 ✓",
                ConnectionState.Reconnecting => "状态：重连中…",
                ConnectionState.HostStopped => "状态：Host 已停止共享",
                ConnectionState.Failed => $"状态：失败（{error}）",
                _ => "状态：未知",
            };
            if (state == ConnectionState.Connected)
            {
                _isConnected = true;
                _connectedAtUtc = DateTime.UtcNow;
                UpdateTransportLabel();
                TxtEncrypt.Text = _client?.IsEncrypted == true ? "加密：AES-256-GCM ✓" : "加密：未启用";
                TryStartAudio();
            }
            else
            {
                _isConnected = false;
                _connectedAtUtc = null;
            }
        });
    }

    /// <summary>传输/编码状态标签（解码路径随实际创建的解码器显示）</summary>
    private void UpdateTransportLabel()
    {
        TxtTransport.Text = "传输：LAN TCP 直连" +
                            (_client is { NegotiatedCodec: VideoCodec.Hevc }
                                ? $" · HEVC（{_decoder?.BackendName ?? "?"}）"
                                : "");
    }

    // ===== 统计栏 =====

    private void UpdateStatsBar()
    {
        var (bitrate, fps, _) = _stats.Tick();
        // 有 Host 目标码率时显示「实测/目标」+ 差距提示：
        // 降档说明是网络不够；未降档却远低于目标说明编码器没花预算（画面糊的来源）。
        TxtBitrate.Text = _hostTargetBitrateBps > 0
            ? $"码率：{bitrate / 1000:F0} / {_hostTargetBitrateBps / 1000:F0} kbps{GapHint(bitrate)}"
            : $"码率：{bitrate / 1000:F0} kbps";

        // 无画面看门狗：已连接但解码器迟迟没有输出时，把「黑屏」变成可读的原因
        //（等待关键帧 / 解码器无输出），画面恢复后自动撤回
        if (_isConnected && _client != null)
        {
            if (_decoder is { DecodedFrames: > 0 })
            {
                TxtState.Text = "状态：已连接 ✓";
            }
            else if (_connectedAtUtc is { } at && DateTime.UtcNow - at > TimeSpan.FromSeconds(5))
            {
                var wait = (DateTime.UtcNow - at).TotalSeconds;
                TxtState.Text = _decoder == null
                    ? $"状态：已连接 ✓（无画面：解码器创建失败，已 {wait:F0}s）"
                    : $"状态：已连接 ✓（等待首个画面中，已 {wait:F0}s——解码器尚无输出，通常在等下一个关键帧）";
            }
        }
        TxtFps.Text = $"帧率：{fps:F1} fps";
        if (!double.IsNaN(_lastRttMs))
            TxtLatency.Text = $"延迟：≈{_lastRttMs / 2:F0} ms（网络单向）";
        RefreshAudioStatus();
    }

    /// <summary>实测码率与目标码率的差距提示（未降档却远低于目标 = 编码器欠产出）</summary>
    private string GapHint(double measuredBps)
    {
        if (_hostDowngraded) return "（网络降档）";
        if (measuredBps < _hostTargetBitrateBps * 0.7) return "（编码器欠产出）";
        return "";
    }

    /// <summary>切换连接模式：只启用当前模式的输入区，避免往不生效的框里输入</summary>
    private void ConnMode_Checked(object sender, RoutedEventArgs e)
    {
        ApplyConnMode();
        if (IsLoaded && !_restoringSettings) SaveSettings();
    }

    private void ApplyConnMode()
    {
        if (PanelRoom == null || TxtModeHint == null) return;   // InitializeComponent 期间
        var room = RbRoom.IsChecked == true;
        PanelRoom.IsEnabled = room;
        PanelDirect.IsEnabled = !room;
        TxtModeHint.Text = room
            ? "房间号与密码见 Host 界面「会话信息」；需 Host 端已勾选并连上信令服务器"
            : "Host 地址与密码见 Host 界面「会话信息」；需与 Host 处于同一局域网";
        // 窗口尚未显示时不抢焦点（构造期间 ApplyConnMode 也会走到这里）
        if (!IsLoaded) return;
        if (room) TxtRoom.Focus();
        else TxtHost.Focus();
    }

    private void OnLogEmitted(LogLevel level, DateTime time, string message)
    {
        // 摘要级日志走状态栏即可，避免弹窗刷屏
        if (level >= LogLevel.Error)
            Dispatcher.BeginInvoke(() => TxtState.Text = $"状态：{message}");
    }

    // ===== 系统声音播放 + 音画同步 =====

    /// <summary>
    /// 起播系统声音。Host 没共享声音、或本机没有播放设备/没有 AAC 解码器时，
    /// 一律静默降级为「只看画面」，不弹窗打断观看。
    /// </summary>
    private void TryStartAudio()
    {
        var client = _client;
        if (client is { Audio.Enabled: true })
        {
            EnsureAudioPipeline(client.Audio.SampleRate);
            return;
        }
        // LAN 客户端不在（WebRTC 路径）：协商到音频轨就起播
        if (_client == null && _webRtcReceiver is { IsConnected: true, AudioNegotiated: true })
        {
            EnsureAudioPipeline(AudioStreamInfo.SampleRate);
            return;
        }
        UpdateAudioStatus();
    }

    /// <summary>创建并启动播放管线（采样率取决于通路；LAN/WebRTC 现在同为 48kHz）</summary>
    private void EnsureAudioPipeline(int sampleRate)
    {
        if (ChkAudioPlay.IsChecked != true) { UpdateAudioStatus(); return; }
        if (_audio != null) { UpdateAudioStatus(); return; }

        try
        {
            var pipeline = new AudioPlaybackPipeline(sampleRate);
            pipeline.Start();
            _audio = pipeline;
            Logger.Info("Viewer",
                $"系统声音播放已启动: {pipeline.DecoderName}, {sampleRate}Hz/{AudioStreamInfo.Channels}ch " +
                $"(通路: {(_client != null ? "LAN TCP" : "WebRTC")})");
        }
        catch (Exception ex)
        {
            _audio = null;
            Logger.Warn("Viewer", $"音频播放启动失败，本次只看画面: {ex.Message}");
        }
        UpdateAudioStatus();
    }

    /// <summary>WebRTC 音频帧：裸 AAC 包回 ADTS 后进播放管线（时间戳用到达时刻，抖动缓冲消化网络波动）</summary>
    private void OnWebRtcAudioFrame(byte[] rawAac)
    {
        var audio = _audio;
        if (audio == null) return;
        try
        {
            var adts = Adts.Wrap(rawAac, AudioStreamInfo.SampleRate, AudioStreamInfo.Channels);
            audio.Feed(new EncodedAudioFrame { Data = adts, TimestampUtc = DateTime.UtcNow.Ticks });
        }
        catch (Exception ex)
        {
            Logger.Warn("Viewer", "WebRTC 音频处理异常: " + ex.Message);
        }
    }

    /// <summary>停止声音播放（取消勾选 / 断开连接）。同时清空同步时钟，视频退回「解码完立即上屏」。</summary>
    private void StopAudio()
    {
        var audio = _audio;
        _audio = null;
        if (audio != null)
        {
            audio.Clock.Reset();
            try { audio.Dispose(); } catch { }
        }
        UpdateAudioStatus();
    }

    private void OnAudioFrameReceived(EncodedAudioFrame frame) => _audio?.Feed(frame);

    /// <summary>勾选框：随时静音/恢复。取消勾选会一并解除音画同步等待，画面延迟更低。</summary>
    private void ChkAudioPlay_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        if (!_restoringSettings) SaveSettings();
        if (ChkAudioPlay.IsChecked == true) TryStartAudio();
        else StopAudio();
    }

    private void UpdateAudioStatus() => Dispatcher.BeginInvoke(RefreshAudioStatus);

    /// <summary>状态栏的声音一栏（必须在 UI 线程调用）</summary>
    private void RefreshAudioStatus()
    {
        if (TxtAudio == null) return;
        var audio = _audio;
        var audioEnabled = _client?.Audio.Enabled == true ||
                           _webRtcReceiver is { IsConnected: true, AudioNegotiated: true };
        if (!audioEnabled)
        {
            TxtAudio.Text = "声音：Host 未共享";
            TxtAudio.ToolTip = _client != null
                ? "Host 端勾选「共享系统声音」后重新开始共享，这里才会有声音"
                : "Host 端 v1.3+ 且勾选了「共享系统声音」时，WebRTC 观看才有声音";
            return;
        }
        if (audio == null)
        {
            TxtAudio.Text = ChkAudioPlay.IsChecked == true ? "声音：不可用" : "声音：已静音";
            TxtAudio.ToolTip = ChkAudioPlay.IsChecked == true
                ? "本机没有可用的播放设备或 AAC 解码器（详见日志）"
                : "勾选「播放系统声音」即可恢复";
            return;
        }

        TxtAudio.Text = $"声音：{(audio.IsPlaying ? "播放中" : "缓冲中")} {audio.BufferedMs}ms" +
                        (audio.Underruns > 0 ? $" 卡顿{audio.Underruns}" : "");
        TxtAudio.ToolTip =
            $"解码器：{audio.DecoderName}\n" +
            $"抖动缓冲目标：{AudioRenderer.DefaultTargetLatencyMs}ms（当前 {audio.BufferedMs}ms）\n" +
            $"已收 {audio.ReceivedFrames} 帧 / {audio.ReceivedBytes / 1024} KB，解码失败 {audio.DroppedFrames}\n" +
            $"缓冲耗尽 {audio.Underruns} 次\n" +
            "视频以音频播放时钟为主时钟对齐上屏；取消勾选「播放系统声音」则画面不再等待，延迟更低";
    }

    /// <summary>
    /// 视频上屏线程。
    /// 音画同步要求「画面早于声音时等一会儿再画」，解码回调里直接 Dispatcher.BeginInvoke
    /// 做不到——那样画面会永远比声音早一个抖动缓冲的时长。所以解码回调只入队，
    /// 由本线程按音频时钟决定何时上屏。没有音频时时钟为 null，立即上屏（旧行为）。
    /// </summary>
    private void StartPresentThread()
    {
        if (_presentThread != null) return;
        _presentCts = new CancellationTokenSource();
        var ct = _presentCts.Token;
        _presentThread = new Thread(() => PresentLoop(ct)) { IsBackground = true, Name = "VideoPresent" };
        _presentThread.Start();
    }

    private void StopPresentThread()
    {
        try { _presentCts?.Cancel(); } catch { }
        var thread = _presentThread;
        _presentThread = null;
        if (thread != null && thread.IsAlive)
        {
            try { thread.Join(500); } catch { }
        }
        _presentCts?.Dispose();
        _presentCts = null;
        while (_presentQueue.TryTake(out _)) { }
    }

    private void PresentLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            DecodedVideoFrame frame;
            try { frame = _presentQueue.Take(ct); }
            catch (OperationCanceledException) { break; }
            catch (InvalidOperationException) { break; }

            var clock = _audio?.Clock;
            while (clock != null && !ct.IsCancellationRequested &&
                   AvSyncClock.Decide(frame.TimestampUtc, clock.GetAudioUtcTicks()) == VideoPresentDecision.Wait)
            {
                // 等待期间来了更新的帧就直接跳到最新帧：屏幕共享看最新画面比看全每一帧重要，
                // 顺带还能把积压的延迟追平
                if (_presentQueue.TryTake(out var newer)) { frame = newer; continue; }
                Thread.Sleep(2);
            }
            if (ct.IsCancellationRequested) break;
            RenderFrame(frame);
        }
    }

    /// <summary>BGRA → WriteableBitmap（必须在 UI 线程）</summary>
    private void RenderFrame(DecodedVideoFrame frame)
    {
        Dispatcher.BeginInvoke(() =>
        {
            try
            {
                if (_bitmap == null || _bitmap.PixelWidth != frame.Width || _bitmap.PixelHeight != frame.Height)
                {
                    _bitmap = new WriteableBitmap(frame.Width, frame.Height, 96, 96, PixelFormats.Bgra32, null);
                    VideoImage.Source = _bitmap;
                    TxtResolution.Text = $"分辨率：{frame.Width}×{frame.Height}";
                }
                _bitmap.WritePixels(new System.Windows.Int32Rect(0, 0, frame.Width, frame.Height),
                    frame.Bgra, frame.Width * 4, 0);
            }
            catch (Exception ex)
            {
                Logger.Warn("Viewer", "渲染异常: " + ex.Message);
            }
        });
    }

    private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        TeardownSession();
        _uiTimer.Stop();
        Logger.LogEmitted -= OnLogEmitted;
    }

    // ===== 全屏/无边框观看（F11 / 双击画面 / 状态栏按钮；Esc 退出）=====

    /// <summary>窗口内容网格（全屏时直接改行高隐藏面板）</summary>
    private System.Windows.Controls.RowDefinitionCollection RowDefs => ((System.Windows.Controls.Grid)Content).RowDefinitions;

    private bool _fullscreen;
    private GridLength _rowConnection, _rowStatus;
    private Thickness _videoAreaMargin, _videoImageMargin;

    private void BtnFullscreen_Click(object sender, RoutedEventArgs e) => ToggleFullscreen();

    private void VideoArea_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2) ToggleFullscreen();
    }

    /// <summary>窗口按键：F11 切换全屏，Esc 退出全屏</summary>
    protected override void OnKeyDown(System.Windows.Input.KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == System.Windows.Input.Key.F11) ToggleFullscreen();
        else if (e.Key == System.Windows.Input.Key.Escape && _fullscreen) ToggleFullscreen();
    }

    /// <summary>
    /// 全屏 = 无边框 + 铺满 + 隐藏连接面板与状态栏，只留黑底画面。
    /// 不持久化该状态：观看是临时场景，下次启动回到常规窗口更符合直觉。
    /// </summary>
    private void ToggleFullscreen()
    {
        if (_fullscreen)
        {
            WindowStyle = WindowStyle.SingleBorderWindow;
            ResizeMode = ResizeMode.CanResize;
            WindowState = WindowState.Normal;
            RowDefs[0].Height = _rowConnection;
            RowDefs[2].Height = _rowStatus;
            VideoArea.Margin = _videoAreaMargin;
            VideoImage.Margin = _videoImageMargin;
            PanelConnection.Visibility = Visibility.Visible;
            PanelStatusBar.Visibility = Visibility.Visible;
            BtnFullscreen.Content = "全屏";
            _fullscreen = false;
        }
        else
        {
            _rowConnection = RowDefs[0].Height;
            _rowStatus = RowDefs[2].Height;
            _videoAreaMargin = VideoArea.Margin;
            _videoImageMargin = VideoImage.Margin;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            WindowState = WindowState.Maximized;
            RowDefs[0].Height = new GridLength(0);
            RowDefs[2].Height = new GridLength(0);
            PanelConnection.Visibility = Visibility.Collapsed;
            PanelStatusBar.Visibility = Visibility.Collapsed;
            VideoArea.Margin = new Thickness(0);
            VideoImage.Margin = new Thickness(0);
            BtnFullscreen.Content = "退出全屏";
            _fullscreen = true;
        }
    }
}
