using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using static WindowShare.Host.NativeTray;

namespace WindowShare.Host;

/// <summary>
/// 通知栏托盘图标（纯 P/Invoke Shell_NotifyIcon，不引 WinForms/System.Drawing 依赖）。
/// 生命周期挂宿主窗口的 HwndSource：回调消息经窗口钩子接收，事件统一在 UI 线程触发。
/// 图标从程序集内嵌的 .ico 中解析（CreateIconFromResourceEx 支持 PNG 压缩条目）。
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private const uint WM_TRAY = 0x8000 /*WM_APP*/ + 0x51;
    private const uint MenuOpen = 1, MenuStop = 2, MenuExit = 3;

    private readonly IntPtr _hwnd;
    private readonly HwndSource _source;
    private readonly IntPtr _iconSmall;
    private string _tip = "窗享 Host";
    private bool _sharing;
    private bool _added;

    /// <summary>左键单击 / 双击 / 菜单「打开主窗口」</summary>
    public event Action? OpenRequested;
    /// <summary>菜单「停止共享」</summary>
    public event Action? StopShareRequested;
    /// <summary>菜单「退出」</summary>
    public event Action? ExitRequested;

    public TrayIcon(Window owner)
    {
        _hwnd = new WindowInteropHelper(owner).EnsureHandle();
        _source = HwndSource.FromHwnd(_hwnd)!;
        _source.AddHook(WndProc);
        _iconSmall = LoadIcoHandle(16);

        var nid = NewNotifyIcon();
        nid.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP;
        nid.hIcon = _iconSmall;
        nid.szTip = _tip;
        Shell_NotifyIcon(NIM_ADD, ref nid);
        _added = true;
    }

    /// <summary>更新悬浮提示与菜单可用态（共享中禁止无意义的「停止共享」灰项歧义）</summary>
    public void Update(string tip, bool sharing)
    {
        _tip = tip;
        _sharing = sharing;
        if (!_added) return;
        var nid = NewNotifyIcon();
        nid.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP;
        nid.hIcon = _iconSmall;
        nid.szTip = tip;
        Shell_NotifyIcon(NIM_MODIFY, ref nid);
    }

    /// <summary>气泡提示（首图说明「关闭≠停止共享」）</summary>
    public void ShowBalloon(string title, string text)
    {
        if (!_added) return;
        var nid = NewNotifyIcon();
        nid.uFlags = NIF_INFO;
        nid.szInfoTitle = title;
        nid.szInfo = text;
        Shell_NotifyIcon(NIM_MODIFY, ref nid);
    }

    private static NOTIFYICONDATA NewNotifyIcon() => new()
    {
        cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
        hWnd = 0, uID = 0x5753, uCallbackMessage = WM_TRAY,
        szTip = "", szInfo = "", szInfoTitle = "",
    };

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WM_TRAY) return IntPtr.Zero;
        var mouse = (uint)(lParam.ToInt64() & 0xFFFF);
        switch (mouse)
        {
            case WM_LBUTTONUP:
            case WM_LBUTTONDBLCLK:
                OpenRequested?.Invoke();
                handled = true;
                break;
            case WM_RBUTTONUP:
            case WM_CONTEXTMENU:
                ShowMenu();
                handled = true;
                break;
        }
        return IntPtr.Zero;
    }

    /// <summary>托盘右键菜单（原生 TrackPopupMenu：窗口隐藏时也能弹出）</summary>
    private void ShowMenu()
    {
        var menu = CreatePopupMenu();
        try
        {
            AppendMenu(menu, MF_STRING, MenuOpen, "打开主窗口");
            AppendMenu(menu, MF_STRING | MF_SEPARATOR, 0, null);
            AppendMenu(menu, MF_STRING | (_sharing ? MF_ENABLED : MF_GRAYED), MenuStop, "停止共享");
            AppendMenu(menu, MF_STRING | MF_SEPARATOR, 0, null);
            AppendMenu(menu, MF_STRING, MenuExit, "退出");
            SetForegroundWindow(_hwnd);   // KB135788：菜单收不到 WM_LBUTTONUP 的官方解法
            var cmd = TrackPopupMenu(menu, TPM_RETURNCMD | TPM_RIGHTBUTTON | TPM_NONOTIFY,
                GetCursorX(), GetCursorY(), 0, _hwnd, IntPtr.Zero);
            PostMessage(_hwnd, WM_NULL, IntPtr.Zero, IntPtr.Zero);
            switch ((uint)cmd)
            {
                case MenuOpen: OpenRequested?.Invoke(); break;
                case MenuStop: StopShareRequested?.Invoke(); break;
                case MenuExit: ExitRequested?.Invoke(); break;
            }
        }
        finally { DestroyMenu(menu); }
    }

    /// <summary>从内嵌 .ico 中挑最接近的条目建 HICON（条目是 PNG 数据，Vista+ 原生支持）</summary>
    private static IntPtr LoadIcoHandle(int preferSize)
    {
        var asmName = typeof(TrayIcon).Assembly.GetName().Name;
        using var s = typeof(TrayIcon).Assembly.GetManifestResourceStream($"{asmName}.tray.ico")
                      ?? throw new InvalidOperationException("未找到内嵌托盘图标资源");
        var bytes = new byte[s.Length];
        s.ReadExactly(bytes);

        ushort count = BitConverter.ToUInt16(bytes, 4);
        int bestOff = -1, bestLen = 0, bestDiff = int.MaxValue;
        for (var i = 0; i < count; i++)
        {
            var e = 6 + 16 * i;
            var w = bytes[e] == 0 ? 256 : bytes[e];
            var len = (int)BitConverter.ToUInt32(bytes, e + 8);
            var off = (int)BitConverter.ToUInt32(bytes, e + 12);
            if (Math.Abs(w - preferSize) < bestDiff)
            {
                bestDiff = Math.Abs(w - preferSize);
                bestOff = off;
                bestLen = len;
            }
        }
        if (bestOff < 0) throw new InvalidOperationException(".ico 无有效条目");

        var data = new byte[bestLen];
        Array.Copy(bytes, bestOff, data, 0, bestLen);
        var hIcon = CreateIconFromResourceEx(data, (uint)bestLen, true, 0x00030000,
            preferSize, preferSize, LR_DEFAULTCOLOR);
        if (hIcon == IntPtr.Zero) throw new InvalidOperationException("托盘图标创建失败");
        return hIcon;
    }

    public void Dispose()
    {
        if (_added)
        {
            var nid = NewNotifyIcon();
            Shell_NotifyIcon(NIM_DELETE, ref nid);
            _added = false;
        }
        if (_iconSmall != IntPtr.Zero) DestroyIcon(_iconSmall);
        _source.RemoveHook(WndProc);
    }
}

/// <summary>shell32/user32 托盘所需的最小互操作集</summary>
internal static class NativeTray
{
    public const uint NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2;
    public const uint NIF_MESSAGE = 1, NIF_ICON = 2, NIF_TIP = 4, NIF_INFO = 0x10;
    public const uint WM_LBUTTONUP = 0x0202, WM_LBUTTONDBLCLK = 0x0203,
                      WM_RBUTTONUP = 0x0205, WM_CONTEXTMENU = 0x007B;
    public const uint WM_NULL = 0;
    public const uint MF_STRING = 0, MF_ENABLED = 0, MF_GRAYED = 1, MF_SEPARATOR = 0x800;
    public const uint TPM_RETURNCMD = 0x100, TPM_RIGHTBUTTON = 2, TPM_NONOTIFY = 0x80;
    public const uint LR_DEFAULTCOLOR = 0;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct NOTIFYICONDATA
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool Shell_NotifyIcon(uint message, ref NOTIFYICONDATA data);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool AppendMenu(IntPtr menu, uint flags, uint id, string? text);

    [DllImport("user32.dll")]
    public static extern bool DestroyMenu(IntPtr menu);

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hwnd);

    /// <summary>TPM_RETURNCMD 下返回值是所选菜单项 ID（0=取消），而非成功标志</summary>
    [DllImport("user32.dll")]
    public static extern int TrackPopupMenu(IntPtr menu, uint flags, int x, int y,
        int reserved, IntPtr hwnd, IntPtr rect);

    [DllImport("user32.dll")]
    public static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool GetCursorPos(out POINT point);

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

    public static int GetCursorX() { GetCursorPos(out var p); return p.X; }
    public static int GetCursorY() { GetCursorPos(out var p); return p.Y; }

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr CreateIconFromResourceEx(byte[] presbits, uint dwResSize,
        bool fIcon, uint dwVer, int cxDesired, int cyDesired, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool DestroyIcon(IntPtr hIcon);
}
