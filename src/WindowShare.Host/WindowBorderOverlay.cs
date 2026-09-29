using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace WindowShare.Host;

/// <summary>
/// 窗口共享红框标记：透明点击穿透窗口，紧贴被共享窗口边框，
/// 随窗口移动/缩放实时跟随 —— 直观提示"此窗口正在被共享"。
/// </summary>
public sealed class WindowBorderOverlay : Window
{
    private const int GwlExstyle = -20;
    private const int WsExTransparent = 0x20;
    private const int WsExToolwindow = 0x80;
    private const int WsExNoactivate = 0x08000000;

    private readonly IntPtr _targetHwnd;
    private readonly DispatcherTimer _timer;
    private IntPtr _selfHwnd;

    public WindowBorderOverlay(IntPtr targetHwnd)
    {
        _targetHwnd = targetHwnd;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        Width = 100;
        Height = 100;

        var border = new System.Windows.Controls.Border
        {
            BorderBrush = new SolidColorBrush(Color.FromArgb(0xE0, 0xE5, 0x39, 0x35)),
            BorderThickness = new Thickness(4),
            CornerRadius = new CornerRadius(2),
        };
        Content = border;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _timer.Tick += (_, _) => TrackTarget();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _selfHwnd = new WindowInteropHelper(this).Handle;

        // 点击穿透 + 不进 Alt-Tab
        var exStyle = GetWindowLong(_selfHwnd, GwlExstyle);
        SetWindowLong(_selfHwnd, GwlExstyle, exStyle | WsExTransparent | WsExToolwindow | WsExNoactivate);

        _timer.Start();
        TrackTarget();
    }

    /// <summary>跟随目标窗口位置（物理像素坐标直接 SetWindowPos，避开 DPI 换算误差）</summary>
    private void TrackTarget()
    {
        if (!IsWindow(_targetHwnd) || !IsWindowVisible(_targetHwnd))
        {
            Close();
            return;
        }
        if (!GetWindowRect(_targetHwnd, out var rect)) return;

        SetWindowPos(_selfHwnd, IntPtr.Zero,
            rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top,
            SwpNoactivate | SwpNoOwnerZorder);
    }

    protected override void OnClosed(EventArgs e)
    {
        _timer.Stop();
        base.OnClosed(e);
    }

    // ===== Win32 =====
    private const uint SwpNoactivate = 0x0010;
    private const uint SwpNoOwnerZorder = 0x0200;

    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hwnd, int index, int newStyle);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after,
        int x, int y, int cx, int cy, uint flags);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }
}
