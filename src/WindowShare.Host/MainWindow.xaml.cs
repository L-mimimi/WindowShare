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
    /// <summary>状态栏统计（观看者数 + 会话每秒快照，UpdateStatsLine 组合显示）</summary>
    private int _viewerCount;
    private SessionStatsSnapshot _lastStats;
    /// <summary>信令房间是否已注册成功（与「是否勾选」区分：勾选但未连上时显示重试入口）</summary>
    private bool _signalingReady;
    /// <summary>房间号被占用时的换号重试次数</summary>
    private const int SignalingRegisterRetries = 3;
    /// <summary>托盘图标（SourceInitialized 后创建，窗口关闭时销毁）</summary>
    private TrayIcon? _tray;
    /// <summary>托盘菜单「退出」置位：跳过隐藏到托盘，真正关闭窗口</summary>
    private bool _forceExit;
    /// <summary>「关闭窗口但共享继续」的气泡只提示第一次</summary>
    private bool _trayBalloonShown;
    /// <summary>LAN 发现信标（共享期间广播本机，随会话启停）</summary>
    private DiscoveryBeacon? _beacon;
    /// <summary>本机信令服务器子进程（一键启动功能）</summary>
    private System.Diagnostics.Process? _localSignaling;
    private static readonly System.Net.Http.HttpClient SignalingHealthClient = new()
    {
        Timeout = TimeSpan.FromSeconds(2),
    };

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
        // 构造期间禁止写设置：InitializeSources/CboResolution.ItemsSource 等会设置 SelectedIndex，
        // 从而触发 SelectionChanged → SaveSettings()，而此时多数控件还是 XAML 默认值。
        // 后果是"已保存的用户设置被启动瞬间的控件默认值覆盖"（实测：MinimizeToTray true→false、
        // Discoveryable true→false、ResolutionIndex 2→0），与 1.3.1 的「构造期写设置」
        // 是同一类缺陷——当时只修了崩溃（控件为 null），没有修数据覆盖。
        // ApplySettings 内部自带 try/finally，会把它置回 false，因此这里先把整段构造护住，
        // 构造结束时再统一放行（见方法末尾的 _uiReady = true）。
        _restoringSettings = true;
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
        SourceInitialized += (_, _) => InitializeTray();
        Closing += (_, _) =>
        {
            SaveSettings();
            _infoTimer.Stop();
            Logger.LogEmitted -= OnLogEmitted;
        };
        _restoringSettings = false;   // 构造完成，控件事件恢复写设置
        _uiReady = true;              // XAML 加载完毕，之后控件事件才允许写设置
    }

    /// <summary>窗口句柄就绪后挂托盘图标；菜单动作统一回 UI 线程</summary>
    private void InitializeTray()
    {
        try
        {
            _tray = new TrayIcon(this);
            _tray.OpenRequested += () => Dispatcher.BeginInvoke(() =>
            {
                Show();
                WindowState = WindowState.Normal;
                Activate();
            });
            _tray.StopShareRequested += () => Dispatcher.BeginInvoke(() =>
            {
                if (_session is not { IsSharing: true }) return;
                StopSharing("托盘停止");
                Show();
                WindowState = WindowState.Normal;
            });
            _tray.ExitRequested += () => Dispatcher.BeginInvoke(() =>
            {
                _forceExit = true;
                Close();
            });
            _tray.Update("窗享 Host — 空闲", sharing: false);
        }
        catch (Exception ex)
        {
            Logger.Warn("Host", "托盘图标初始化失败（不影响共享功能）: " + ex.Message);
        }
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
            CboEncoder.SelectedIndex = EffectiveEncoderPreference switch
            {
                "hevc" => 2,
                "h264" => 1,
                _ => 0,
            };
            ChkSignaling.IsChecked = _settings.EnableSignaling;
            ChkMinToTray.IsChecked = _settings.MinimizeToTray;
            ChkDiscoverable.IsChecked = _settings.Discoverable;
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
    /// <summary>
    /// XAML 解析期间控件默认值（如 ChkAudio IsChecked="True"）就会触发 Checked 事件，
    /// 此时后声明的控件（TxtSignalingUrl 等）还是 null——构造完成前一律不保存设置。
    /// </summary>
    private bool _uiReady;

    /// <summary>把当前 UI 状态写回设置文件（改动即存 + 退出时存）</summary>
    private void SaveSettings()
    {
        _settings.ResolutionIndex = Math.Max(0, CboResolution.SelectedIndex);
        _settings.FpsIndex = Math.Max(0, CboFps.SelectedIndex);
        _settings.ShareAudio = ChkAudio.IsChecked == true;
        _settings.RecordForValidation = ChkRecord.IsChecked == true;
        var pref = CboEncoder.SelectedIndex switch
        {
            2 => "hevc",
            1 => "h264",
            _ => "auto",
        };
        _settings.EncoderPreference = pref;
        _settings.PreferHevc = pref == "hevc"; // 兼容 1.4.2 及以前的读取方
        _settings.EnableSignaling = ChkSignaling.IsChecked == true;
        _settings.MinimizeToTray = ChkMinToTray.IsChecked == true;
        _settings.Discoverable = ChkDiscoverable.IsChecked == true;
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
        if (!_uiReady || _restoringSettings) return;
        SaveSettings();
    }

    /// <summary>编码器选择（含旧版 PreferHevc 迁移）：auto/h264/hevc</summary>
    private string EffectiveEncoderPreference =>
        _settings.EncoderPreference ?? (_settings.PreferHevc ? "hevc" : "auto");

    private void CboEncoder_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_uiReady || _restoringSettings) return;
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
            ? $"输出 {width}×{height}@{session.Options?.Fps ?? 0}fps{CaptureHint(session)}"
            : "";
        TxtAudio.Text = DescribeAudio(session);
    }

    /// <summary>
    /// 帧率错配提示：配置档位高但内容更新率低时（如 144fps 档只捕到 56fps），
    /// 在输出信息里如实显示，避免「选了高帧率却没变流畅」的困惑。
    /// </summary>
    private static string CaptureHint(ShareSession session)
    {
        var target = session.Options?.Fps ?? 0;
        var capture = session.CaptureStats.Fps; // OnStatsTick 每秒刷新
        return target > 0 && capture > 0.5 && capture < target * 0.7
            ? $"（实际捕获 {capture:F0}fps）"
            : "";
    }

    /// <summary>组合显示观看者数与实时输出码率/帧率（ViewerCountChanged 与 StatsTick 驱动）</summary>
    private void UpdateStatsLine()
    {
        TxtStats.Text = $"观看者：{_viewerCount}" +
                        (_lastStats.SendBitrateBps > 0
                            ? $" · {_lastStats.SendBitrateBps / 1e6:F1} Mbps · {_lastStats.SendFps:F0}fps"
                            : "");
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

        // 会话编码决策：选「HEVC 优先」且本机有可用 HEVC 编码器 → HEVC（同画质省 30–50% 码率，
        // 仅 1.4.0+ 观看端可接入）；否则 H.264（兼容一切版本）。探针失败自动回退，不阻断共享。
        var codec = VideoCodec.H264;
        if (EffectiveEncoderPreference == "hevc")
        {
            var hevcProbe = new EncoderSettings
            {
                Codec = VideoCodec.Hevc,
                Width = plannedWidth,
                Height = plannedHeight,
                Fps = fps,
                BitrateBps = VideoFormatPlanner.SuggestBitrateBps(plannedWidth, plannedHeight, fps),
            };
            if (VideoEncoderFactory.ProbeAvailable(hevcProbe))
                codec = VideoCodec.Hevc;
            else
                Logger.Warn("Host", "本机无可用 HEVC 编码器，本次共享回退 H.264（观看端兼容性最好）");
        }

        var options = new ShareOptions
        {
            Width = SelectedWidth,
            Fps = fps,
            BitrateBps = VideoFormatPlanner.SuggestBitrateBps(plannedWidth, plannedHeight, fps),
            Codec = codec,
            RecordForValidation = ChkRecord.IsChecked == true,
            RecordFilePath = Path.Combine(AppPaths.Recordings, $"share-{DateTime.Now:yyyyMMdd-HHmmss}.h264"),
            ShareAudio = ChkAudio.IsChecked == true,
        };

        _session = new ShareSession();
        _session.PreviewArrived += OnPreviewArrived;
        _session.Stopped += OnSessionStopped;
        _session.Error += OnSessionError;
        _session.StatsTick += s => Dispatcher.BeginInvoke(() =>
        {
            _lastStats = s;
            UpdateStatsLine();
        });

        // LAN 共享服务器：观看者经密码+白名单审批接入
        _whitelist = new DeviceWhitelist();
        _server = new LanShareServer(_session, _whitelist, LanShareServer.DefaultPort,
            _settings.BindAddress);
        _server.ApproveRequired = info => System.Threading.Tasks.Task.FromResult(Dispatcher.Invoke(() => ApproveDevice(info)));
        _server.ViewerCountChanged += count => Dispatcher.BeginInvoke(() =>
        {
            _viewerCount = count;
            UpdateStatsLine();
        });

        try
        {
            _session.Start(source, options);
            _server.Start();
            if (_settings.Discoverable)
            {
                _beacon = new DiscoveryBeacon(AppPaths.GetMachineName(), LanShareServer.DefaultPort);
                _beacon.Start();
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
        // 音频参数在会话启动时就固定了，共享期间改勾选没有意义，直接禁掉避免误解
        ChkAudio.IsEnabled = false;
        _infoTimer.Start();
        RefreshOutputInfo();
        _tray?.Update("窗享 Host — 共享中", sharing: true);

        // 悬浮共享指示条 + 窗口红框（隐私提示）
        _overlay = new OverlayWindow(source, _session, () => _server?.GetViewerCount() ?? 0);
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

        // 信令地址指向本机默认端口而服务器没在跑 → 自动拉起程序目录自带的信令服务器，
        // 这是「勾上信令就能用」零操作流程的关键一步；远程地址不代劳（那台机器不归我们管）。
        if (IsLocalSignalingUrl(url) && !await IsOurSignalingUpAsync())
        {
            SetSignalingState("本机信令未运行，正在自动启动…", SignalingUiState.Pending);
            Logger.Info("Host", "连接信令前发现本机服务器未运行，自动拉起");
            if (!await EnsureLocalSignalingRunningAsync())
            {
                Logger.Warn("Host", "本机信令服务器自动启动失败，房间号模式不可用（局域网直连共享不受影响）");
                SetSignalingState("本机信令自动启动失败（5000 端口可能被占用）｜局域网直连共享不受影响",
                    SignalingUiState.Error);
                return;
            }
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
                                    "｜共享照常进行，仅房间号模式不可用；可点「重试」再试（本机信令会自动拉起）");
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

    // ===== 本机信令服务器（一键启动/停止） =====

    private void BtnSignalingLocal_Click(object sender, RoutedEventArgs e)
    {
        if (_localSignaling is { HasExited: false })
        {
            StopLocalSignaling("手动停止");
            return;
        }
        _ = StartLocalSignalingAsync();
    }

    /// <summary>定位程序目录自带的信令服务器 exe（便携版/安装版均为 signaling\ 子目录布局）</summary>
    private static string? FindSignalingExe()
    {
        foreach (var root in new[] { AppPaths.GetExeDirectory(), AppContext.BaseDirectory })
        {
            var p = Path.Combine(root, "signaling", "WindowShare.Signaling.exe");
            if (File.Exists(p)) return p;
        }
        return null;
    }

    /// <summary>探测本机 5000 端口是否已在跑「我们的」信令服务器</summary>
    private static async System.Threading.Tasks.Task<bool> IsOurSignalingUpAsync()
    {
        try
        {
            var body = await SignalingHealthClient.GetStringAsync("http://localhost:5000/");
            return body.Contains("WindowShare Signaling");
        }
        catch
        {
            return false;
        }
    }

    private async System.Threading.Tasks.Task StartLocalSignalingAsync()
    {
        if (await EnsureLocalSignalingRunningAsync())
            return;

        MessageBox.Show(this,
            "本机信令服务器未能启动：可能未找到 signaling\\WindowShare.Signaling.exe，或 5000 端口被其他程序占用。\n" +
            "详见日志，或手动运行 signaling\\WindowShare.Signaling.exe 查看报错。",
            "启动失败", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    /// <summary>是否指向本机默认信令地址（自动拉起只对它生效；自定义端口/远程地址不代劳）</summary>
    private static bool IsLocalSignalingUrl(string url)
    {
        var trimmed = url.TrimEnd('/');
        return trimmed.Equals("http://localhost:5000", StringComparison.OrdinalIgnoreCase) ||
               trimmed.Equals("http://127.0.0.1:5000", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 确保本机信令服务器在运行：已在跑直接复用；否则启动程序目录自带的信令进程并等待就绪（≤12 秒）。
    /// 不弹窗（自动拉起与手动按钮共用，失败只写日志返回 false）；按钮路径由 <see cref="StartLocalSignalingAsync"/> 补提示。
    /// </summary>
    private async System.Threading.Tasks.Task<bool> EnsureLocalSignalingRunningAsync()
    {
        try
        {
            // 已经有我们的信令在跑（上次未关 / 另一窗口启动的）→ 直接接管状态
            if (await IsOurSignalingUpAsync())
            {
                MarkLocalSignalingRunning(null);
                return true;
            }

            var exe = FindSignalingExe();
            if (exe == null)
            {
                Logger.Warn("Host", "未找到信令服务器程序（signaling\\WindowShare.Signaling.exe），无法启动");
                return false;
            }

            BtnSignalingLocal.Content = "启动中…";
            BtnSignalingLocal.IsEnabled = false;
            var psi = new System.Diagnostics.ProcessStartInfo(exe, "--urls http://0.0.0.0:5000")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(exe)!,
            };
            var proc = System.Diagnostics.Process.Start(psi);

            for (var i = 0; i < 24 && (proc == null || !proc.HasExited); i++)
            {
                await System.Threading.Tasks.Task.Delay(500);
                if (await IsOurSignalingUpAsync())
                {
                    MarkLocalSignalingRunning(proc);
                    return true;
                }
            }

            ResetLocalSignalingButton();
            Logger.Warn("Host", "本机信令服务器 12 秒内未就绪（5000 端口可能被其他程序占用）");
            return false;
        }
        catch (Exception ex)
        {
            ResetLocalSignalingButton();
            Logger.Error("Host", "本机信令服务器启动失败", ex);
            return false;
        }
    }

    private void MarkLocalSignalingRunning(System.Diagnostics.Process? proc)
    {
        // 双击/重入时可能走到两次：只记一次「已就绪」日志；已跟踪的进程不被
        // 「检测到已有实例」路径（proc=null）清掉句柄，否则窗口关闭时无法回收它。
        var alreadyTracked = _localSignaling != null;
        _localSignaling ??= proc;
        if (proc != null)
        {
            proc.EnableRaisingEvents = true;
            proc.Exited += (_, _) => Dispatcher.BeginInvoke(() =>
            {
                _localSignaling = null;
                ResetLocalSignalingButton();
                Logger.Info("Host", "本机信令服务器进程已退出");
            });
        }
        BtnSignalingLocal.Content = "本机信令：停止";
        BtnSignalingLocal.IsEnabled = true;
        TxtSignalingUrl.Text = "http://localhost:5000";
        if (ChkSignaling.IsChecked != true)
            ChkSignaling.IsChecked = true;   // 触发既有流程：共享中立即连接，否则提示开始共享后连接
        if (!alreadyTracked)
            Logger.Info("Host", "本机信令服务器已就绪（http://localhost:5000）");
    }

    private void StopLocalSignaling(string reason)
    {
        var proc = _localSignaling;
        _localSignaling = null;
        if (proc is { HasExited: false })
        {
            try { proc.Kill(entireProcessTree: true); }
            catch (Exception ex) { Logger.Warn("Host", $"停止信令进程失败: {ex.Message}"); }
        }
        ResetLocalSignalingButton();
        Logger.Info("Host", $"本机信令服务器已停止（{reason}）");
    }

    private void ResetLocalSignalingButton()
    {
        BtnSignalingLocal.Content = "本机信令：启动";
        BtnSignalingLocal.IsEnabled = true;
    }

    /// <summary>信令中继消息处理：观看者跨网段时发起 WebRTC（SDP/ICE 中继）</summary>
    private async void OnRelayFromViewer(string? viewerId, string type, string payload)
    {
        try
        {
            switch (type)
            {
                case "webrtc-request":
                    // HEVC 会话不走 WebRTC（v1.4：RTP 打包器按 H.264 语义处理 NAL，
                    // 载 HEVC 会损坏码流）；引导观看端改用局域网直连或 Host 关闭 HEVC 优先。
                    if (_session?.Options?.Codec == VideoCodec.Hevc)
                    {
                        Logger.Warn("Host", "观看端请求 WebRTC，但当前会话为 HEVC 编码（WebRTC 暂不支持 HEVC），已拒绝");
                        await _signaling?.RelayToViewerAsync(viewerId ?? "", "webrtc-reject",
                            "Host 正在以 HEVC 编码共享，跨网段观看暂不支持；请在局域网内直连，或 Host 取消「HEVC 优先」")!;
                        break;
                    }
                    _webRtcViewerId = viewerId;
                    if (_webRtcSink != null) _session?.RemoveSink(_webRtcSink);
                    _webRtcSink = null;
                    if (_webRtcSender != null) await _webRtcSender.DisposeAsync();

                    _webRtcSender = new WebRtcHostSender(includeAudio: true);
                    _webRtcSender.LocalIceCandidate += c =>
                        _ = _signaling?.RelayToViewerAsync(viewerId ?? "", "ice", c);
                    _webRtcSender.StateChanged += s => Logger.Info("Host", $"WebRTC: {s}");

                    // 发送器作为会话 sink 接入（仅连接后真正发送）
                    _webRtcSink = new WebRtcSinkAdapter(_webRtcSender, _session?.Options?.Fps ?? 30);
                    _session?.AddSink(_webRtcSink);

                    var offer = await _webRtcSender.CreateOfferAsync();
                    await _signaling?.RelayToViewerAsync(viewerId ?? "", "offer", offer)!;
                    Logger.Info("Host", "WebRTC offer（含音频轨）已中继给观看者");
                    break;

                case "answer":
                    try
                    {
                        _webRtcSender?.SetAnswer(payload);
                    }
                    catch (Exception ex)
                    {
                        // 旧版观看端（v1.2，无音频能力）对带音频轨的 offer 可能协商失败：
                        // 降级为纯视频 offer 重试一次，旧观看端按 v1.2 流程应答
                        Logger.Warn("Host", $"WebRTC answer 协商失败，尝试纯视频降级: {ex.Message}");
                        await RestartWebRtcWithoutAudioAsync(viewerId);
                    }
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

    /// <summary>WebRTC 音频协商失败 → 重建纯视频发送端并重发 offer（旧观看端兼容）</summary>
    private async System.Threading.Tasks.Task RestartWebRtcWithoutAudioAsync(string? viewerId)
    {
        if (_session is not { IsSharing: true }) return;
        if (_webRtcSink != null) _session.RemoveSink(_webRtcSink);
        _webRtcSink = null;
        var oldSender = _webRtcSender;
        if (oldSender != null) await oldSender.DisposeAsync();

        _webRtcSender = new WebRtcHostSender(includeAudio: false);
        _webRtcSender.LocalIceCandidate += c =>
            _ = _signaling?.RelayToViewerAsync(viewerId ?? "", "ice", c);
        _webRtcSender.StateChanged += s => Logger.Info("Host", $"WebRTC(纯视频): {s}");
        _webRtcSink = new WebRtcSinkAdapter(_webRtcSender, _session.Options?.Fps ?? 30);
        _session.AddSink(_webRtcSink);

        var offer = await _webRtcSender.CreateOfferAsync();
        await _signaling?.RelayToViewerAsync(viewerId ?? "", "offer", offer)!;
        Logger.Info("Host", "纯视频 offer 已重发");
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
        _beacon?.Stop();
        _beacon = null;

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
            _viewerCount = 0;
            _lastStats = default;
            UpdateStatsLine();
            TxtOutput.Text = "";
            TxtAudio.Text = "";
            ChkAudio.IsEnabled = true;
            _tray?.Update("窗享 Host — 空闲", sharing: false);
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
        // 共享进行中关窗 → 隐藏到托盘继续共享（托盘菜单「退出」才真正关闭）
        if (_session is { IsSharing: true } && !_forceExit && _settings.MinimizeToTray)
        {
            e.Cancel = true;
            Hide();
            if (!_trayBalloonShown)
            {
                _tray?.ShowBalloon("窗享仍在共享",
                    "已最小化到通知栏，观看者仍可接入。右键托盘图标可停止共享或退出。");
                _trayBalloonShown = true;
            }
            return;
        }

        _tray?.Dispose();
        _tray = null;
        StopLocalSignaling("窗口关闭");
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
