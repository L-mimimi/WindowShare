using System.Windows;
using System.Windows.Threading;
using WindowShare.Core.Capture;
using WindowShare.Core.Logging;
using WindowShare.Core.Session;

namespace WindowShare.Host;

/// <summary>
/// 共享指示悬浮条：始终置顶，显示"正在共享 + 时长 + 观看者数 + 实时码率/帧率"，
/// 提供一键停止按钮。可拖动；位于共享屏幕顶部中央。这是共享期间的显著隐私提示。
/// </summary>
public partial class OverlayWindow : Window
{
    private readonly ShareSession _session;
    private readonly CaptureSource _source;
    private readonly Func<int>? _viewerCount;
    private readonly DispatcherTimer _timer;
    private readonly System.Diagnostics.Stopwatch _sw = System.Diagnostics.Stopwatch.StartNew();
    private SessionStatsSnapshot _stats;

    public OverlayWindow(CaptureSource source, ShareSession session, Func<int>? viewerCount = null)
    {
        InitializeComponent();
        _source = source;
        _session = session;
        _viewerCount = viewerCount;

        // 位于共享源所在屏幕顶部中央（物理像素 → WPF DIP 近似换算）
        Loaded += (_, _) =>
        {
            var scaleX = SystemParameters.PrimaryScreenWidth /
                         Math.Max(1, Win32.GetSystemMetrics(Win32.SM_CXSCREEN));
            var left = (source.Bounds.X + source.Bounds.Width / 2.0) * scaleX - Width / 2;
            Left = Math.Max(8, left);
            Top = Math.Max(8, source.Bounds.Y * scaleX + 8);
        };

        // 会话统计在后台线程推送，调度回 UI 线程后再赋值（结构体避免撕裂读）
        _session.StatsTick += OnStatsTick;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => UpdateText();
        _timer.Start();
        UpdateText();
    }

    private void OnStatsTick(SessionStatsSnapshot s) =>
        Dispatcher.BeginInvoke(() => _stats = s);

    private void UpdateText()
    {
        var text = $"正在共享：{Truncate(_source.Title, 20)}  {_sw.Elapsed:hh\\:mm\\:ss}";
        var viewers = _viewerCount?.Invoke() ?? 0;
        if (viewers > 0) text += $" · {viewers} 位观看";
        if (_stats.SendBitrateBps > 0)
            text += $" · {_stats.SendBitrateBps / 1e6:F1} Mbps · {_stats.SendFps:F0}fps";
        TxtInfo.Text = text;
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..(max - 1)] + "…";

    private void Window_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        try { DragMove(); } catch { }
    }

    private void BtnStop_Click(object sender, RoutedEventArgs e)
    {
        Logger.Info("Overlay", "用户通过悬浮条一键停止共享");
        _session.Stop("一键停止");
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        _session.StatsTick -= OnStatsTick;
        _timer.Stop();
        base.OnClosed(e);
    }
}

/// <summary>Win32 辅助</summary>
internal static class Win32
{
    public const int SM_CXSCREEN = 0;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    public static extern int GetSystemMetrics(int index);
}
