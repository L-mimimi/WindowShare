using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WindowShare.Core.Capture;
using WindowShare.Core.Encoding;
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
    private readonly DispatcherTimer _infoTimer;
    private readonly HostSettings _settings = HostSettings.Load();
    /// <summary>信令房间是否已注册成功（与「是否勾选」区分：勾选但未连上时显示重试入口）</summary>
    private bool _signalingReady;
    /// <summary>房间号被占用时的换号重试次数</summary>
    private const int SignalingRegisterRetries = 3;

    /// <summary>
    /// 分辨率档位（只定义目标宽度；高度按源宽高比等比推导，最高 4K）。
    /// 码率不写死，由 <see cref="VideoFormatPlanner.SuggestBitrateBps"/> 按分辨率×帧率自动推算。
    /// </summary>
    private static readonly (string Name, int Width)[] ResolutionPresets =
    {
        ("4K 超高清 (3840)", 3840),
        ("2K (2560)", 2560),
        ("1080p 全高清 (1920)", 1920),
        ("720p 高清 (1280)", 1280),
        ("540p 流畅 (960)", 960),
    };

    public MainWindow()
    {
        InitializeComponent();
        InitializeSources();
        CboResolution.ItemsSource = ResolutionPresets.Select(r => r.Name).ToList();
        CboFps.ItemsSource = VideoFormatPlanner.FpsTiers.Select(f => $"{f} fps").ToList();
        ApplySettings();
        _infoTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _infoTimer.Tick += (_, _) => RefreshOutputInfo();
        ShowModeInfo();
        UpdateBitrateHint();
        Logger.LogEmitted += OnLogEmitted;
        Closing += (_, _) =>
        {
            SaveSettings();
            _infoTimer.Stop();
            Logger.LogEmitted -= OnLogEmitted;
        };
    }

    // ===== 设置持久化（分辨率/帧率/开关/信令地址，重启后恢复上次配置）=====

    private void ApplySettings()
    {
        _restoringSettings = true;
        try
        {
            CboResolution.SelectedIndex = Math.Clamp(_settings.ResolutionIndex, 0, ResolutionPresets.Length - 1);
            CboFps.SelectedIndex = Math.Clamp(_settings.FpsIndex, 0, VideoFormatPlanner.FpsTiers.Length - 1);
            ChkAudio.IsChecked = _settings.ShareAudio;
            ChkRecord.IsChecked = _settings.RecordForValidation;
            ChkSignaling.IsChecked = _settings.EnableSignaling;
            TxtSignalingUrl.Text = string.IsNullOrWhiteSpace(_settings.SignalingUrl)
                ? "http://localhost:5000" : _settings.SignalingUrl;

            // best-effort 恢复上次共享源：显示器重插/窗口句柄失效时自然找不到，静默跳过
            if (_settings.LastSourceKind >= 0)
            {
                var list = (List<CaptureSource>)CboSources.ItemsSource;
                var match = list.FirstOrDefault(s =>
                    (int)s.Kind == _settings.LastSourceKind &&
                    s.Handle.ToInt64() == _settings.LastSourceHandle);
                if (match != null) CboSources.SelectedItem = match;
            }
        }
        finally { _restoringSettings = false; }
    }

    /// <summary>恢复默认值过程中触发的「改动」不回写（避免启动时连写多次文件）</summary>
    private bool _restoringSettings;

    /// <summary>把当前 UI 状态写回设置文件（改动即存 + 退出时存）</summary>
    private void SaveSettings()
    {
        _settings.ResolutionIndex = Math.Max(0, CboResolution.SelectedIndex);
        _settings.FpsIndex = Math.Max(0, CboFps.SelectedIndex);
        _settings.ShareAudio = ChkAudio.IsChecked == true;
        _settings.RecordForValidation = ChkRecord.IsChecked == true;
        _settings.EnableSignaling = ChkSignaling.IsChecked == true;
        _settings.SignalingUrl = TxtSignalingUrl.Text.Trim();
        var src = _selectedSource ?? GetSelectedSource();
        if (src != null)
        {
            _settings.LastSourceKind = (int)src.Kind;
            _settings.LastSourceHandle = src.Handle.ToInt64();
        }
        _settings.Save();
    }

    /// <summary>复选框改动即存（XAML 的 Checked/Unchecked 公用入口）</summary>
    private void Setting_Changed(object sender, RoutedEventArgs e)
    {
        if (_restoringSettings) return;
        SaveSettings();
    }

    // ===== 画质档位（分辨率 / 帧率 / 自动码率）=====

    /// <summary>所选目标宽度（档位值；实际输出还受源尺寸约束，不上采样）</summary>
    private int SelectedWidth =>
        ResolutionPresets[Math.Clamp(CboResolution.SelectedIndex, 0, ResolutionPresets.Length - 1)].Width;

    /// <summary>所选帧率档位（24/30/60/90/120/144）</summary>
    private int SelectedFps =>
        VideoFormatPlanner.FpsTiers[Math.Clamp(CboFps.SelectedIndex, 0, VideoFormatPlanner.FpsTiers.Length - 1)];

    private void Quality_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TxtBitrateHint == null) return;   // InitializeComponent 期间可能早于其他控件
        UpdateBitrateHint();
        if (!_restoringSettings) SaveSettings();
    }

    private void Source_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TxtBitrateHint == null) return;
        UpdateBitrateHint();
        if (!_restoringSettings) SaveSettings();
    }

    /// <summary>按「所选分辨率 × 帧率 × 源宽高比」推算码率并展示</summary>
    private void UpdateBitrateHint()
    {
        var source = GetSelectedSource();
        var (width, height) = VideoFormatPlanner.FitToWidth(
            source?.Bounds.Width ?? SelectedWidth,
            source?.Bounds.Height ?? SelectedWidth * 9 / 16,
            SelectedWidth);
        var bitrate = VideoFormatPlanner.SuggestBitrateBps(width, height, SelectedFps);
        TxtBitrateHint.Text = $"码率 ≈ {bitrate / 1_000_000.0:F1} Mbps（自动）";
        TxtBitrateHint.ToolTip =
            $"按 {width}×{height}@{SelectedFps}fps 自动推算，无需手动设置。\n" +
            "实际输出分辨率不会超过共享源尺寸（不做上采样）；网络拥塞时会自动降码率/降分辨率。";
    }

    /// <summary>每秒刷新实际编码输出（动态降档后会与所选档位不同）</summary>
    private void RefreshOutputInfo()
    {
        var session = _session;
        if (session is not { IsSharing: true })
        {
            TxtOutput.Text = "";
            TxtAudio.Text = "";
            return;
        }
        var (width, height) = session.OutputSize;
        TxtOutput.Text = width > 0
            ? $"输出 {width}×{height}@{session.Options?.Fps ?? 0}fps"
            : "";
        TxtAudio.Text = DescribeAudio(session);
    }

    /// <summary>系统声音共享状态（含实时电平，一眼看出「有没有声音在传」）</summary>
    private static string DescribeAudio(ShareSession session)
    {
        var audio = session.AudioInfo;
        if (audio.Enabled)
            return $"声音 OK {audio.SampleRate / 1000}kHz/{audio.Channels}ch 电平 {session.AudioLevel:P0}";
        return session.Options?.ShareAudio == true
            ? "声音 启动失败（详见日志）"
            : "声音 未共享";
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

        // 分辨率档位只给目标宽度：高度按源宽高比等比推导，源比档位小时不上采样。
        var (plannedWidth, plannedHeight) = VideoFormatPlanner.FitToWidth(
            source.Bounds.Width, source.Bounds.Height, SelectedWidth);
        var fps = SelectedFps;
        var options = new ShareOptions
        {
            Width = SelectedWidth,
            Fps = fps,
            BitrateBps = VideoFormatPlanner.SuggestBitrateBps(plannedWidth, plannedHeight, fps),
            RecordForValidation = ChkRecord.IsChecked == true,
            RecordFilePath = Path.Combine(AppPaths.Recordings, $"share-{DateTime.Now:yyyyMMdd-HHmmss}.h264"),
            ShareAudio = ChkAudio.IsChecked == true,
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
        // 音频参数在会话启动时就固定了，共享期间改勾选没有意义，直接禁掉避免误解
        ChkAudio.IsEnabled = false;
        _infoTimer.Start();
        RefreshOutputInfo();

        // 悬浮共享指示条 + 窗口红框（隐私提示）
        _overlay = new OverlayWindow(source, _session);
        _overlay.Show();
        if (source.Kind == CaptureSourceKind.Window)
        {
            _borderOverlay = new WindowBorderOverlay(source.Handle);
            _borderOverlay.Show();
        }

        // 信令（房间号模式）在共享已起来之后再连：连接失败只降级为「仅局域网直连」，
        // 绝不回滚已经开始的共享（旧行为会把整个共享流程判定为失败并停止）。
        if (ChkSignaling.IsChecked == true)
            await StartSignalingAsync();
    }

    private void BtnStop_Click(object sender, RoutedEventArgs e) => StopSharing("一键停止");

    // ===== 信令服务器（房间号模式）=====

    private enum SignalingUiState { Pending, Ok, Error, Off }

    /// <summary>
    /// 注册信令房间。与共享生命周期解耦：服务器不可达、地址写错、房间号被占用都只影响
    /// 房间号模式，局域网直连共享照常进行；界面给出可照着排查的原因与「重试」入口。
    /// </summary>
    private async System.Threading.Tasks.Task StartSignalingAsync()
    {
        var session = _session;
        if (session is not { IsSharing: true }) return;

        var url = TxtSignalingUrl.Text.Trim();
        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            Logger.Warn("Host", $"信令地址无效: {url}");
            SetSignalingState($"地址需以 http:// 或 https:// 开头（当前：{(url.Length == 0 ? "空" : url)}）",
                SignalingUiState.Error);
            return;
        }

        for (var attempt = 1; attempt <= SignalingRegisterRetries; attempt++)
        {
            if (_session is not { IsSharing: true }) return;   // 期间已停止共享
            if (_signaling == null)
            {
                _signaling = new HostSignalingClient(url);
                _signaling.ViewerJoinRequested += OnSignalingViewerJoin;
                _signaling.RelayFromViewer += OnRelayFromViewer;
            }

            SetSignalingState($"连接中…（{attempt}/{SignalingRegisterRetries}）", SignalingUiState.Pending);
            try
            {
                var registered = await _signaling.RegisterAsync(_session.RoomCode, _session.Password,
                    Environment.MachineName, LocalEndpoints.GetLanEndpoints(LanShareServer.DefaultPort));
                if (registered)
                {
                    SetSignalingState($"已连接 ✓ 房间 {_session.RoomCode}", SignalingUiState.Ok);
                    return;
                }

                // 服务器拒绝（房间号被占用）→ 轮换会话凭据后重试，已接入的观看者不受影响
                var (newRoom, newPassword) = _session.RotateCredentials();
                TxtRoomCode.Text = newRoom;
                TxtPassword.Text = newPassword;
                SetSignalingState($"房间号被占用，已换号 {newRoom}（{attempt}/{SignalingRegisterRetries}）",
                    SignalingUiState.Pending);
            }
            catch (Exception ex)
            {
                // 日志与状态栏用同一份「可照着排查」的文案：只写原始异常消息时，
                // 「由于目标计算机积极拒绝，无法连接」看着像共享失败，实际只是房间号模式没连上。
                Logger.Warn("Host", HostSignalingClient.DescribeConnectFailure(url, ex) +
                                    "｜共享照常进行，仅房间号模式不可用；启动信令服务器后点「重试」即可");
                DisposeSignaling();
                SetSignalingState(HostSignalingClient.DescribeConnectFailure(url, ex) +
                                  "｜局域网直连共享不受影响", SignalingUiState.Error);
                return;
            }

            await System.Threading.Tasks.Task.Delay(200);
        }

        DisposeSignaling();
        SetSignalingState($"房间号连续 {SignalingRegisterRetries} 次被占用，请稍后点「重试」｜局域网直连共享不受影响",
            SignalingUiState.Error);
    }

    /// <summary>更新信令状态文字与配色；出错时露出「重试」按钮</summary>
    private void SetSignalingState(string text, SignalingUiState state)
    {
        TxtSignalingState.Text = text;
        TxtSignalingState.ToolTip = text;
        TxtSignalingState.Foreground = new System.Windows.Media.SolidColorBrush(state switch
        {
            SignalingUiState.Ok => System.Windows.Media.Color.FromRgb(0x43, 0xA0, 0x47),
            SignalingUiState.Error => System.Windows.Media.Color.FromRgb(0xE5, 0x39, 0x35),
            SignalingUiState.Pending => System.Windows.Media.Color.FromRgb(0xE8, 0x8B, 0x00),
            _ => System.Windows.Media.Color.FromRgb(0x88, 0x88, 0x88),
        });
        _signalingReady = state == SignalingUiState.Ok;
        BtnSignalingRetry.Visibility = state == SignalingUiState.Error ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>运行中勾选/取消信令：即时生效，无需重新开始共享</summary>
    private void ChkSignaling_Changed(object sender, RoutedEventArgs e)
    {
        if (TxtSignalingState == null) return;   // InitializeComponent 期间
        if (!_restoringSettings) SaveSettings();
        if (_session is not { IsSharing: true })
        {
            SetSignalingState(ChkSignaling.IsChecked == true ? "将在开始共享时连接" : "未启用",
                SignalingUiState.Off);
            return;
        }
        if (ChkSignaling.IsChecked == true) _ = StartSignalingAsync();
        else
        {
            DisposeSignaling();
            SetSignalingState("未启用", SignalingUiState.Off);
        }
    }

    private void BtnSignalingRetry_Click(object sender, RoutedEventArgs e)
    {
        if (_session is not { IsSharing: true })
        {
            MessageBox.Show("请先「开始共享」，再连接信令服务器。", "提示",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        _ = StartSignalingAsync();
    }

    /// <summary>释放信令客户端（关闭房间 + 断开连接），不触碰共享会话本身</summary>
    private void DisposeSignaling()
    {
        var signaling = _signaling;
        _signaling = null;
        _signalingReady = false;
        if (signaling == null) return;
        _ = System.Threading.Tasks.Task.Run(async () =>
        {
            try { await signaling.StopSharingAsync(); }
            finally { await signaling.DisposeAsync(); }
        });
    }

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
        _infoTimer.Stop();

        if (_webRtcSink != null)
        {
            try { _session?.RemoveSink(_webRtcSink); } catch { }
            _webRtcSink = null;
        }
        var oldSender = _webRtcSender;
        _webRtcSender = null;
        if (oldSender != null) _ = oldSender.DisposeAsync();

        DisposeSignaling();

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
            TxtOutput.Text = "";
            TxtAudio.Text = "";
            ChkAudio.IsEnabled = true;
            SetSignalingState(ChkSignaling.IsChecked == true ? "将在开始共享时连接" : "未启用",
                SignalingUiState.Off);
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
        // 信令已注册成功 → 观看端走「房间号」；否则只能同局域网「直连 IP」
        var invite = _signalingReady
            ? $"【窗享邀请】房间号 {TxtRoomCode.Text}  密码 {TxtPassword.Text}\n" +
              $"信令服务器 {TxtSignalingUrl.Text.Trim()}\n" +
              "打开 窗享 Viewer → 选「房间号」→ 填入房间号 / 密码 / 信令地址即可观看（只读共享）。"
            : $"【窗享邀请】密码 {TxtPassword.Text}\n" +
              "打开 窗享 Viewer → 选「直连 IP」→ 填入本机 IP、端口 48750 与以上密码即可观看（需同一局域网，只读共享）。";
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
