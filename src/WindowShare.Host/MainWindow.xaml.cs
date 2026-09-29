using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using WindowShare.Core.Capture;
using WindowShare.Core.Logging;
using WindowShare.Core.Network;
using WindowShare.Core.Security;
using WindowShare.Core.Session;
using WindowShare.Core.Signaling;
using WindowShare.Core.Utils;
using WindowShare.Core.WebRtc;

namespace WindowShare.Host;

/// <summary>
/// Host 主窗口：选择捕获源 → 开始共享 → 预览 + 悬浮提示 + 一键停止 + LAN 服务器。
/// 本应用为只读共享：不包含任何远程控制/输入注入功能。
/// </summary>
public partial class MainWindow : Window
{
    private ShareSession? _session;
    private OverlayWindow? _overlay;
    private WindowBorderOverlay? _borderOverlay;
    private LanShareServer? _server;
    private DeviceWhitelist? _whitelist;
    private HostSignalingClient? _signaling;
    private WebRtcHostSender? _webRtcSender;
    private WebRtcSinkAdapter? _webRtcSink;
    private string? _webRtcViewerId;
    /// <summary>经信令审批过的设备（LAN 认证时免二次弹窗）</summary>
    private readonly HashSet<string> _preApprovedDevices = new();
    private CaptureSource? _selectedSource;
    private WriteableBitmap? _previewBitmap;

    private static readonly (string Name, int Width, int Bitrate)[] QualityPresets =
    {
        ("高清 1080p / 4 Mbps", 1920, 4_000_000),
        ("均衡 720p / 2.5 Mbps", 1280, 2_500_000),
        ("流畅 540p / 1.2 Mbps", 960, 1_200_000),
    };

    public MainWindow()
    {
        InitializeComponent();
        InitializeSources();
        CboQuality.ItemsSource = QualityPresets.Select(q => q.Name).ToList();
        CboQuality.SelectedIndex = 1;
        ShowModeInfo();
        Logger.LogEmitted += OnLogEmitted;
        Closing += (_, _) => Logger.LogEmitted -= OnLogEmitted;
    }

    /// <summary>显示运行模式（便携/安装）与数据目录</summary>
    private void ShowModeInfo()
    {
        TxtMode.Text = AppPaths.IsPortable ? "便携模式" : "安装模式";
        TxtMode.ToolTip = $"{AppPaths.ModeDescription}\n{AppPaths.Root}";
    }

    /// <summary>打开数据目录（资源管理器）</summary>
    private void BtnDataDir_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            AppPaths.EnsureDirectories();
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = AppPaths.Root,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Logger.Warn("Host", $"打开数据目录失败: {ex.Message}");
            MessageBox.Show($"打开数据目录失败：{ex.Message}\n\n路径：{AppPaths.Root}",
                "提示", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    // ===== 初始化 =====

    private void InitializeSources()
    {
        var sources = new List<CaptureSource>();
        sources.AddRange(CaptureSourceList.GetMonitors());
        sources.AddRange(CaptureSourceList.GetWindows());
        CboSources.ItemsSource = sources;
        CboSources.DisplayMemberPath = null; // 使用 ToString
        CboSources.SelectedIndex = sources.Count > 0 ? 0 : -1;
    }

    private CaptureSource? GetSelectedSource() => CboSources.SelectedItem as CaptureSource;

    private void BtnRefresh_Click(object sender, RoutedEventArgs e)
    {
        var prev = GetSelectedSource();
        InitializeSources();
        // 尽量恢复上次选择
        if (prev != null)
        {
            var list = (List<CaptureSource>)CboSources.ItemsSource;
            var match = list.FirstOrDefault(s => s.Handle == prev.Handle && s.Kind == prev.Kind);
            if (match != null) CboSources.SelectedItem = match;
        }
    }

    // ===== 开始/停止共享 =====

    private void BtnToggleShare_Click(object sender, RoutedEventArgs e)
    {
        if (_session is { IsSharing: true })
        {
            StopSharing("用户停止");
            return;
        }
        _ = StartSharingFlow();
    }

    /// <summary>启动共享的异步主流程（含信令注册）</summary>
    private async System.Threading.Tasks.Task StartSharingFlow()
    {

        var source = GetSelectedSource();
        if (source == null)
        {
            MessageBox.Show("请先选择要共享的屏幕或窗口", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        _selectedSource = source;

        var preset = QualityPresets[Math.Max(0, CboQuality.SelectedIndex)];
        var options = new ShareOptions
        {
            Width = preset.Width,
            Fps = 30,
            BitrateBps = preset.Bitrate,
            RecordForValidation = ChkRecord.IsChecked == true,
            RecordFilePath = Path.Combine(AppPaths.Recordings, $"share-{DateTime.Now:yyyyMMdd-HHmmss}.h264"),
        };

        _session = new ShareSession();
        _session.PreviewArrived += OnPreviewArrived;
        _session.Stopped += OnSessionStopped;
        _session.Error += OnSessionError;

        // LAN 共享服务器：观看者经密码+白名单审批接入
        _whitelist = new DeviceWhitelist();
        _server = new LanShareServer(_session, _whitelist, LanShareServer.DefaultPort);
        _server.ApproveRequired = info => System.Threading.Tasks.Task.FromResult(Dispatcher.Invoke(() => ApproveDevice(info)));
        _server.ViewerCountChanged += count => Dispatcher.BeginInvoke(() =>
            TxtStats.Text = $"观看者：{count}");

        try
        {
            _session.Start(source, options);
            _server.Start();

            // 信令服务器（房间号模式）：注册房间，观看者凭房间号+密码加入
            if (ChkSignaling.IsChecked == true && !string.IsNullOrWhiteSpace(TxtSignalingUrl.Text))
            {
                _signaling = new HostSignalingClient(TxtSignalingUrl.Text.Trim());
                _signaling.ViewerJoinRequested += OnSignalingViewerJoin;
                _signaling.RelayFromViewer += OnRelayFromViewer;
                var registered = await _signaling.RegisterAsync(
                    _session.RoomCode, _session.Password,
                    Environment.MachineName,
                    LocalEndpoints.GetLanEndpoints(LanShareServer.DefaultPort));
                if (registered)
                {
                    TxtSignalingState.Text = "已连接 ✓";
                    TxtSignalingState.Foreground =
                        new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x43, 0xA0, 0x47));
                }
                else
                {
                    TxtSignalingState.Text = "房间号被占用";
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Error("Host", "启动共享失败", ex);
            MessageBox.Show($"启动共享失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            CleanupSession();
            return;
        }

        // 更新 UI
        TxtRoomCode.Text = _session.RoomCode;
        TxtPassword.Text = _session.Password;
        TxtEncoder.Text = $"编码器：{_session.EncoderName}" +
                          (_session.IsHardwareEncoder ? " (硬件)" : " (软件)") +
                          (_session.IsZeroCopy ? " 零拷贝" : "");
        BtnToggleShare.Content = "停止共享";
        BtnToggleShare.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE5, 0x39, 0x35));
        BtnStop.Visibility = Visibility.Visible;
        TxtPreviewHint.Visibility = Visibility.Collapsed;

        // 悬浮共享指示条 + 窗口红框（隐私提示）
        _overlay = new OverlayWindow(source, _session);
        _overlay.Show();
        if (source.Kind == CaptureSourceKind.Window)
        {
            _borderOverlay = new WindowBorderOverlay(source.Handle);
            _borderOverlay.Show();
        }
    }

    private void BtnStop_Click(object sender, RoutedEventArgs e) => StopSharing("一键停止");

    /// <summary>信令中继消息处理：观看者跨网段时发起 WebRTC（SDP/ICE 中继）</summary>
    private async void OnRelayFromViewer(string? viewerId, string type, string payload)
    {
        try
        {
            switch (type)
            {
                case "webrtc-request":
                    _webRtcViewerId = viewerId;
                    if (_webRtcSink != null) _session?.RemoveSink(_webRtcSink);
                    _webRtcSink = null;
                    if (_webRtcSender != null) await _webRtcSender.DisposeAsync();

                    _webRtcSender = new WebRtcHostSender();
                    _webRtcSender.LocalIceCandidate += c =>
                        _ = _signaling?.RelayToViewerAsync(viewerId ?? "", "ice", c);
                    _webRtcSender.StateChanged += s => Logger.Info("Host", $"WebRTC: {s}");

                    // 发送器作为会话 sink 接入（仅连接后真正发送）
                    _webRtcSink = new WebRtcSinkAdapter(_webRtcSender, _session?.Options?.Fps ?? 30);
                    _session?.AddSink(_webRtcSink);

                    var offer = await _webRtcSender.CreateOfferAsync();
                    await _signaling?.RelayToViewerAsync(viewerId ?? "", "offer", offer)!;
                    Logger.Info("Host", "WebRTC offer 已中继给观看者");
                    break;

                case "answer":
                    _webRtcSender?.SetAnswer(payload);
                    break;

                case "ice":
                    _webRtcSender?.AddIceCandidate(payload);
                    break;
            }
        }
        catch (Exception ex)
        {
            Logger.Error("Host", "WebRTC 中继处理异常", ex);
        }
    }

    /// <summary>信令观看者审批（UI 线程弹窗，结果回传真令）</summary>
    private async void OnSignalingViewerJoin(ViewerJoinRequest request)
    {
        var approved = await Dispatcher.InvokeAsync(() =>
        {
            var dialog = new ApprovalDialog(request.DeviceName, request.DeviceId, "经信令服务器") { Owner = this };
            dialog.ShowDialog();
            if (dialog.Decision == ApprovalDecision.ApprovedAndRemembered)
                _whitelist?.Approve(request.DeviceId, request.DeviceName);
            if (dialog.Decision != ApprovalDecision.Denied)
                _preApprovedDevices.Add(request.DeviceId);
            return dialog.Decision != ApprovalDecision.Denied;
        });
        try
        {
            if (_signaling != null)
                await _signaling.ApproveViewerAsync(request.ViewerId, approved);
        }
        catch (Exception ex)
        {
            Logger.Warn("Host", "审批结果回传失败: " + ex.Message);
        }
    }

    private void StopSharing(string reason)
    {
        try
        {
            _session?.Stop(reason);
        }
        catch (Exception ex)
        {
            Logger.Error("Host", "停止共享异常", ex);
        }
        CleanupSession();
        TxtPreviewHint.Visibility = Visibility.Visible;
    }

    private void OnSessionStopped(string reason)
    {
        Dispatcher.BeginInvoke(() =>
        {
            CleanupSession();
            TxtPreviewHint.Visibility = Visibility.Visible;
            if (!string.IsNullOrEmpty(reason))
                MessageBox.Show(this, $"共享已结束：{reason}", "窗享", MessageBoxButton.OK, MessageBoxImage.Warning);
        });
    }

    private void OnSessionError(string message)
    {
        Dispatcher.BeginInvoke(() => TxtStats.Text = $"⚠ {message}");
    }

    /// <summary>设备审批（在 UI 线程执行，阻塞接入线程直至用户决定）</summary>
    private bool ApproveDevice(ViewerInfo info)
    {
        // 经信令审批过的设备免二次弹窗
        if (_preApprovedDevices.Contains(info.DeviceId))
        {
            Logger.Info("Host", $"设备 {info.DeviceName} 已经信令审批，直接放行");
            return true;
        }
        var dialog = new ApprovalDialog(info.DeviceName, info.DeviceId, info.RemoteAddress) { Owner = this };
        dialog.ShowDialog();
        switch (dialog.Decision)
        {
            case ApprovalDecision.ApprovedAndRemembered:
                _whitelist?.Approve(info.DeviceId, info.DeviceName);
                return true;
            case ApprovalDecision.ApprovedOnce:
                return true;
            default:
                return false;
        }
    }

    private void CleanupSession()
    {
        _overlay?.Close();
        _overlay = null;
        _borderOverlay?.Close();
        _borderOverlay = null;

        _server?.Stop();
        _server = null;
        _whitelist = null;
        _preApprovedDevices.Clear();

        if (_webRtcSink != null)
        {
            try { _session?.RemoveSink(_webRtcSink); } catch { }
            _webRtcSink = null;
        }
        var oldSender = _webRtcSender;
        _webRtcSender = null;
        if (oldSender != null) _ = oldSender.DisposeAsync();

        var signaling = _signaling;
        _signaling = null;
        if (signaling != null)
        {
            Dispatcher.BeginInvoke(() => TxtSignalingState.Text = "未启用");
            _ = Task.Run(async () =>
            {
                try { await signaling.StopSharingAsync(); }
                finally { await signaling.DisposeAsync(); }
            });
        }

        if (_session != null)
        {
            _session.PreviewArrived -= OnPreviewArrived;
            _session.Stopped -= OnSessionStopped;
            _session.Error -= OnSessionError;
        }
        _session = null;

        Dispatcher.BeginInvoke(() =>
        {
            BtnToggleShare.Content = "开始共享";
            BtnToggleShare.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1E, 0x88, 0xE5));
            BtnStop.Visibility = Visibility.Collapsed;
            TxtRoomCode.Text = "";
            TxtPassword.Text = "";
            TxtEncoder.Text = "编码器：未启动";
            TxtStats.Text = "观看者：0";
        });
    }

    // ===== 预览渲染 =====

    private void OnPreviewArrived(PreviewFrame frame)
    {
        Dispatcher.BeginInvoke(() =>
        {
            try
            {
                if (frame.Width <= 0 || frame.Height <= 0) return;
                if (_previewBitmap == null ||
                    _previewBitmap.PixelWidth != frame.Width ||
                    _previewBitmap.PixelHeight != frame.Height)
                {
                    _previewBitmap = new WriteableBitmap(frame.Width, frame.Height, 96, 96,
                        System.Windows.Media.PixelFormats.Bgra32, null);
                    PreviewImage.Source = _previewBitmap;
                }
                var rect = new System.Windows.Int32Rect(0, 0, frame.Width, frame.Height);
                _previewBitmap.WritePixels(rect, frame.Bgra, frame.Width * 4, 0);
            }
            catch (Exception ex)
            {
                Logger.Warn("Host", "预览渲染失败: " + ex.Message);
            }
        });
    }

    // ===== 其他 UI 事件 =====

    private void BtnCopy_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(TxtRoomCode.Text)) return;
        var invite = $"【窗享邀请】房间号 {TxtRoomCode.Text}  密码 {TxtPassword.Text}\n" +
                     "打开 窗享 Viewer，输入房间号和密码即可观看（只读共享）。";
        try
        {
            Clipboard.SetText(invite);
            Logger.Info("Host", "邀请已复制到剪贴板");
        }
        catch (Exception ex)
        {
            Logger.Warn("Host", "复制失败: " + ex.Message);
        }
    }

    private void CboQuality_SelectionChanged(object sender, SelectionChangedEventArgs e) { }

    private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_session is { IsSharing: true })
            StopSharing("窗口关闭");
        _overlay?.Close();
        _borderOverlay?.Close();
    }

    // ===== 日志 =====

    private void OnLogEmitted(LogLevel level, DateTime time, string message)
    {
        // 只显示 Info 以上，避免刷屏
        if (level < LogLevel.Info) return;
        Dispatcher.BeginInvoke(() =>
        {
            TxtLog.AppendText($"{time:HH:mm:ss} {message}{Environment.NewLine}");
            TxtLog.ScrollToEnd();
        });
    }
}
