using System.Runtime.InteropServices;
using System.Text;

namespace WindowShare.Core.Capture;

/// <summary>
/// 枚举可捕获源：所有显示器 + 可见顶级窗口。
/// 枚举线程设置为 Per-Monitor-V2 DPI 感知，保证坐标是真实像素。
/// </summary>
public static class CaptureSourceList
{
    // ===== Win32 显示器枚举 =====
    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clipRect, MonitorEnumProc callback, IntPtr data);

    private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr hdc, ref RECT rect, IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int maxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    private const int GWL_STYLE = -16;
    private const int GWL_EXSTYLE = -20;
    private const long WS_CHILD = 0x40000000L;
    private const long WS_EX_TOOLWINDOW = 0x00000080L;

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr data);

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr data);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);

    private const int DWMWA_CLOAKED = 14;

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    private const uint MONITOR_DEFAULTTONEAREST = 2;

    [DllImport("user32.dll")]
    private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);

    private static readonly IntPtr PerMonitorAwareV2 = new(-4);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    /// <summary>当前进程 ID（排除自身窗口）</summary>
    private static readonly uint CurrentPid = (uint)Environment.ProcessId;

    /// <summary>
    /// 列出全部显示器（含主屏标记），每线程设置 DPI 感知上下文后恢复。
    /// </summary>
    public static List<CaptureSource> GetMonitors()
    {
        var list = new List<CaptureSource>();
        var oldCtx = SetThreadDpiAwarenessContext(PerMonitorAwareV2);
        try
        {
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero,
                (IntPtr hmon, IntPtr hdc, ref RECT rect, IntPtr data) =>
            {
                var bounds = new PixelRect(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
                // 主显示器 = 包含原点 (0,0)
                var isPrimary = bounds.X == 0 && bounds.Y == 0;
                list.Add(new CaptureSource(CaptureSourceKind.Monitor, hmon,
                    $"显示器 {list.Count + 1} ({bounds.Width}×{bounds.Height})", bounds, isPrimary));
                return true;
            }, IntPtr.Zero);
        }
        finally
        {
            SetThreadDpiAwarenessContext(oldCtx);
        }
        return list;
    }

    /// <summary>
    /// 列出可共享窗口：可见、有标题、非工具窗口、非被遮罩(Cloaked)、非本进程、非最小化。
    /// </summary>
    public static List<CaptureSource> GetWindows()
    {
        var list = new List<CaptureSource>();
        var oldCtx = SetThreadDpiAwarenessContext(PerMonitorAwareV2);
        try
        {
            EnumWindows((hwnd, data) =>
            {
                try
                {
                    if (!IsWindowVisible(hwnd)) return true;
                    if (IsIconic(hwnd)) return true;                    // 最小化窗口内容不可见
                    if (GetWindowTextLength(hwnd) == 0) return true;

                    var style = GetWindowLong(hwnd, GWL_STYLE);
                    var exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
                    if ((style & WS_CHILD) == WS_CHILD) return true;    // 子窗口（如控件）
                    if ((exStyle & WS_EX_TOOLWINDOW) == WS_EX_TOOLWINDOW) return true;

                    if (DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out var cloaked, 4) == 0 && cloaked != 0)
                        return true;                                    // UWP 挂起/虚拟桌面隐藏窗口

                    GetWindowThreadProcessId(hwnd, out var pid);
                    if (pid == CurrentPid) return true;                 // 不共享自己的界面

                    if (!GetWindowRect(hwnd, out var rect)) return true;
                    var bounds = new PixelRect(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
                    if (bounds.Width < 80 || bounds.Height < 80) return true; // 排除通知气泡等

                    var title = GetWindowTitle(hwnd);
                    if (string.IsNullOrWhiteSpace(title)) return true;

                    list.Add(new CaptureSource(CaptureSourceKind.Window, hwnd, title, bounds, false));
                }
                catch { /* 单个窗口枚举失败不影响整体 */ }
                return true;
            }, IntPtr.Zero);
        }
        finally
        {
            SetThreadDpiAwarenessContext(oldCtx);
        }

        // 按 Z 序（EnumWindows 顺序即 Z 序）排序，标题去重加后缀
        for (var i = 0; i < list.Count; i++)
        {
            var dup = list.FindIndex(j => j != list[i] && j.Title == list[i].Title);
            if (dup >= 0) list[i] = list[i] with { Title = $"{list[i].Title} (0x{list[i].Handle:X})" };
        }
        return list;
    }

    /// <summary>获取窗口标题</summary>
    public static string GetWindowTitle(IntPtr hwnd)
    {
        var len = GetWindowTextLength(hwnd);
        if (len <= 0) return string.Empty;
        var sb = new StringBuilder(len + 1);
        GetWindowText(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    /// <summary>查找窗口所在的显示器源（窗口捕获 → 系统级回退时使用）</summary>
    public static CaptureSource? FindMonitorOfWindow(IntPtr hwnd)
    {
        var hmon = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        return GetMonitors().FirstOrDefault(m => m.Handle == hmon);
    }
}
