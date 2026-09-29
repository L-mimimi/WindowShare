using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WindowShare.Core.Decoding;
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
    private MfH264Decoder? _decoder;
    private readonly StatsCollector _stats = new();
    private readonly DispatcherTimer _uiTimer;
    private WriteableBitmap? _bitmap;
    private double _lastRttMs = double.NaN;
    private volatile bool _firstKeyframeSeen;

    public MainWindow()
    {
        InitializeComponent();
        _uiTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _uiTimer.Tick += (_, _) => UpdateStatsBar();
        _uiTimer.Start();
        TxtMode.Text = AppPaths.ModeDescription;
        TxtMode.ToolTip = AppPaths.Root;
        Logger.LogEmitted += OnLogEmitted;
    }

    // ===== 连接 =====

    private void BtnConnect_Click(object sender, RoutedEventArgs e)
    {
        var password = TxtPwd.Password;
        string host;
        int port;

        if (RbRoom.IsChecked == true)
        {
            // 房间号模式：经信令服务器（批6 接入）；此处先提示
            MessageBox.Show("房间号模式需要信令服务器（详见 docs/DEPLOY.md）。\n" +
                            "局域网内请使用「直连 IP」模式。",
                "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!System.Net.IPAddress.TryParse(TxtHost.Text.Trim(), out _))
        {
            try
            {
                var addrs = System.Net.Dns.GetHostAddresses(TxtHost.Text.Trim());
                if (addrs.Length == 0) throw new Exception("无解析结果");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"地址无效：{ex.Message}", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
        }
        host = TxtHost.Text.Trim();
        if (!int.TryParse(TxtPort.Text.Trim(), out port) || port is < 1 or > 65535)
        {
            MessageBox.Show("端口无效", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (string.IsNullOrEmpty(password))
        {
            MessageBox.Show("请输入密码", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        StartSession(host, port, password);
    }

    private void StartSession(string host, int port, string password)
    {
        // 清理旧会话
        TeardownSession();

        _firstKeyframeSeen = false;
        _decoder = new MfH264Decoder();
        _decoder.Decoded += OnDecodedFrame;

        _client = new LanShareClient(host, port,
            AppPaths.GetOrCreateDeviceId(), AppPaths.GetMachineName(), password);
        AttachClient(_client);
        _client.Start();

        BtnConnect.IsEnabled = false;
        BtnDisconnect.IsEnabled = true;
        TxtPlaceholder.Visibility = Visibility.Collapsed;
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

        foreach (var endpoint in info.LanEndpoints)
        {
            var parts = endpoint.Split(':');
            if (parts.Length != 2 || !int.TryParse(parts[1], out var port)) continue;

            var client = new LanShareClient(parts[0], port,
                AppPaths.GetOrCreateDeviceId(), AppPaths.GetMachineName(), password);
            AttachClient(client);

            var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            client.StateChanged += (state, err) =>
            {
                if (state == ConnectionState.Connected) connected.TrySetResult();
                else if (state == ConnectionState.Failed) connected.TrySetException(new Exception(err ?? "认证失败"));
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
        }

        // LAN 直连全部失败 → WebRTC 回退（SDP/ICE 经信令中继，媒体 DTLS-SRTP 端到端）
        await StartWebRtcFallbackAsync(signaling, room);

        TxtState.Text = "状态：正在尝试 WebRTC 连接…";
        BtnConnect.IsEnabled = true;
        BtnDisconnect.IsEnabled = false;
    }

    /// <summary>WebRTC 回退：请求 Host 发起 offer</summary>
    private async System.Threading.Tasks.Task StartWebRtcFallbackAsync(
        ViewerSignalingClient signaling, string room)
    {
        TeardownVideoOnly();

        _webRtcReceiver = new WebRtcViewerReceiver();
        _webRtcReceiver.FrameReceived += (data, ts) => OnEncodedBytes(data, ts);
        _webRtcReceiver.LocalIceCandidate += c =>
            _ = signaling.RelayToHostAsync(room, "ice", c);
        _webRtcReceiver.StateChanged += s => Dispatcher.BeginInvoke(() =>
        {
            TxtTransport.Text = _webRtcReceiver?.UsedRelay == true ? "传输：WebRTC 中继(TURN)" : "传输：WebRTC 直连(P2P)";
        });

        // 请求 Host 创建 offer
        await signaling.RelayToHostAsync(room, "webrtc-request", "");
        Logger.Info("Viewer", "已请求 WebRTC 接入");
    }

    /// <summary>Host 中继消息处理：offer/ice</summary>
    private async void OnRelayFromHost(ViewerSignalingClient signaling, string room, string type, string payload)
    {
        try
        {
            switch (type)
            {
                case "offer":
                    if (_webRtcReceiver == null)
                    {
                        _webRtcReceiver = new WebRtcViewerReceiver();
                        _webRtcReceiver.FrameReceived += (data, ts) => OnEncodedBytes(data, ts);
                        _webRtcReceiver.LocalIceCandidate += c =>
                            _ = signaling.RelayToHostAsync(room, "ice", c);
                    }
                    var answer = await _webRtcReceiver.AcceptOfferAsync(payload);
                    await signaling.RelayToHostAsync(room, "answer", answer);
                    TxtPlaceholder.Visibility = Visibility.Collapsed;
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
        client.RttUpdated += rtt => _lastRttMs = rtt;
        client.StatsUpdated += s => Dispatcher.BeginInvoke(() =>
            TxtEncoder.Text = $"编码器：{s.EncoderName}{(s.Hardware ? "(硬)" : "(软)")} · 源:{s.SourceTitle}");
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
            _client.StateChanged -= OnStateChanged;
            _client.Stop("用户断开");
            _client.Dispose();
            _client = null;
        }
        var signaling = _signaling;
        _signaling = null;
        if (signaling != null) _ = signaling.DisposeAsync();
        TeardownVideoOnly();
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
    }

    // ===== 数据流 =====

    /// <summary>网络线程：H.264 帧 → 解码</summary>
    private void OnFrameReceived(Core.Encoding.EncodedVideoFrame frame)
    {
        try
        {
            _stats.OnFrame(frame.Data.Length);
            if (!_firstKeyframeSeen && !frame.Keyframe)
                return; // 丢弃 IDR 之前的帧（解码器需要从关键帧开始）
            _firstKeyframeSeen = true;
            _decoder?.Decode(frame.Data, frame.TimestampUtc);
        }
        catch (Exception ex)
        {
            Logger.Warn("Viewer", $"解码输入异常: {ex.Message}");
        }
    }

    /// <summary>解码线程：BGRA → 位图</summary>
    private void OnDecodedFrame(DecodedVideoFrame frame)
    {
        _stats.OnFrame(0); // 帧率样本（字节数已在收包时计入）
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
                TxtTransport.Text = "传输：LAN TCP 直连";
                TxtEncrypt.Text = _client?.IsEncrypted == true ? "加密：AES-256-GCM ✓" : "加密：未启用";
            }
        });
    }

    // ===== 统计栏 =====

    private void UpdateStatsBar()
    {
        var (bitrate, fps, _) = _stats.Tick();
        TxtBitrate.Text = $"码率：{bitrate / 1000:F0} kbps";
        TxtFps.Text = $"帧率：{fps:F1} fps";
        if (!double.IsNaN(_lastRttMs))
            TxtLatency.Text = $"延迟：≈{_lastRttMs / 2:F0} ms（网络单向）";
    }

    private void ModeChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        // 房间号/直连输入互斥提示（简单处理）
    }

    private void OnLogEmitted(LogLevel level, DateTime time, string message)
    {
        // 摘要级日志走状态栏即可，避免弹窗刷屏
        if (level >= LogLevel.Error)
            Dispatcher.BeginInvoke(() => TxtState.Text = $"状态：{message}");
    }

    private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        TeardownSession();
        _uiTimer.Stop();
        Logger.LogEmitted -= OnLogEmitted;
    }
}
