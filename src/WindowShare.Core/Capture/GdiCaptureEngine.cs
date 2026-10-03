using WindowShare.Core.Logging;
using System.Runtime.InteropServices;

namespace WindowShare.Core.Capture;

/// <summary>
/// GDI 捕获引擎（最终兜底，任何 Windows 都可用）：
///   - 整屏：BitBlt 自屏幕 DC；
///   - 窗口：PrintWindow(PW_RENDERFULLCONTENT)（支持硬件加速窗口），失败退 BitBlt；
///   - CPU 帧率限制 30fps；BGRA 自上而下像素。
/// </summary>
public sealed class GdiCaptureEngine : ICaptureEngine, IFrameRateLimited
{
    public string Name => "GDI (BitBlt/PrintWindow)";
    public bool IsGpuTexture => false;
    public bool IsRunning => _running;

    public event Action<CaptureFrame>? FrameArrived;
    // GDI 路径不检测系统级停止（接口事件保留，永远不触发）
    #pragma warning disable CS0067
    public event Action<string>? StoppedBySystem;
    #pragma warning restore CS0067

    private const int PW_CLIENTONLY = 0x01;
    private const int PW_RENDERFULLCONTENT = 0x02;
    private const int SRCCOPY = 0x00CC0020;
    private const int CAPTUREBLT = 0x40000000;
    private const int BI_RGB = 0;
    private const int DIB_RGB_COLORS = 0;

    private volatile bool _running;
    private long _frameCount;
    private Thread? _thread;
    private readonly ManualResetEventSlim _stopEvent = new(false);
    private CaptureSource _source = null!;
    private volatile int _targetFps = 30;

    /// <summary>目标捕获帧率（轮询节流；由会话在 Start 前下发）</summary>
    public int TargetFps
    {
        get => _targetFps;
        set => _targetFps = Math.Clamp(value, 1, 240);
    }

    public static bool IsAvailable() => true;

    public void Start(CaptureSource source)
    {
        if (_running) Stop();
        _source = source;
        _stopEvent.Reset();
        _running = true;
        _frameCount = 0;
        _thread = new Thread(CaptureLoop) { Name = "GdiCaptureLoop", IsBackground = true };
        _thread.Start();
        Logger.Info("GDI", $"开始捕获: {source} @ {_targetFps}fps");
    }

    public void Stop()
    {
        _running = false;
        _stopEvent.Set();
        _thread?.Join(2000);
        Logger.Info("GDI", $"捕获已停止（共 {_frameCount} 帧）");
    }

    private void CaptureLoop()
    {
        while (!_stopEvent.IsSet)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            // 每轮重读目标帧率：允许运行中调整（与编码管线节流保持一致）
            var interval = TimeSpan.FromMilliseconds(1000.0 / Math.Max(1, _targetFps));
            try
            {
                CaptureOnce();
            }
            catch (Exception ex)
            {
                Logger.Error("GDI", "捕获异常", ex);
            }
            var remain = interval - sw.Elapsed;
            if (remain > TimeSpan.Zero) _stopEvent.Wait(remain);
        }
    }

    private void CaptureOnce()
    {
        if (_source.Kind == CaptureSourceKind.Window)
            CaptureWindow(_source.Handle);
        else
            CaptureMonitor(_source.Handle, _source.Bounds);
    }

    /// <summary>捕获窗口（PrintWindow 优先）</summary>
    private void CaptureWindow(IntPtr hwnd)
    {
        if (!GetWindowRect(hwnd, out var rect)) return;
        var w = rect.Right - rect.Left;
        var h = rect.Bottom - rect.Top;
        if (w <= 0 || h <= 0) return;

        // 窗口已最小化时跳过（内容不可见，保留上一帧）
        if (IsIconic(hwnd)) return;

        var hdcWindow = GetWindowDC(hwnd);
        if (hdcWindow == IntPtr.Zero) return;
        try
        {
            using var dib = new DibSection(w, h);
            // PW_RENDERFULLCONTENT：Win8.1+，可捕获 DirectX 内容
            if (!PrintWindow(hwnd, dib.Hdc, PW_RENDERFULLCONTENT) &&
                !PrintWindow(hwnd, dib.Hdc, 0))
            {
                BitBlt(dib.Hdc, 0, 0, w, h, hdcWindow, 0, 0, SRCCOPY | CAPTUREBLT);
            }
            DispatchPixels(w, h, dib);
        }
        finally
        {
            ReleaseDC(hwnd, hdcWindow);
        }
    }

    /// <summary>捕获显示器指定区域</summary>
    private void CaptureMonitor(IntPtr hmon, PixelRect bounds)
    {
        var hdcScreen = GetDC(IntPtr.Zero);
        if (hdcScreen == IntPtr.Zero) return;
        try
        {
            using var dib = new DibSection(bounds.Width, bounds.Height);
            BitBlt(dib.Hdc, 0, 0, bounds.Width, bounds.Height,
                hdcScreen, bounds.X, bounds.Y, SRCCOPY | CAPTUREBLT);
            DispatchPixels(bounds.Width, bounds.Height, dib);
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, hdcScreen);
        }
    }

    /// <summary>把 DIB 像素转成托管数组并派发</summary>
    private void DispatchPixels(int w, int h, DibSection dib)
    {
        var pixels = new byte[w * h * 4];
        Marshal.Copy(dib.Bits, pixels, 0, pixels.Length);
        Interlocked.Increment(ref _frameCount);
        FrameArrived?.Invoke(new CaptureFrame
        {
            Width = w,
            Height = h,
            TimestampUtc = DateTime.UtcNow.Ticks,
            QpcTimestamp = System.Diagnostics.Stopwatch.GetTimestamp(),
            BgraPixels = pixels,
        });
    }

    /// <summary>32bpp 自上而下 DIB 节（GDI 像素缓冲）</summary>
    private sealed class DibSection : IDisposable
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct BitmapInfoHeader
        {
            public uint Size;
            public int Width;
            public int Height;   // 负数 = 自上而下
            public ushort Planes;
            public ushort BitCount;
            public uint Compression;
            public uint SizeImage;
            public int XPelsPerMeter;
            public int YPelsPerMeter;
            public uint ClrUsed;
            public uint ClrImportant;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BitmapInfo
        {
            public BitmapInfoHeader Header;
            public uint Colors; // 占位（24bpp 不用调色板）
        }

        public IntPtr Hdc { get; }
        public IntPtr Bits { get; }
        private readonly IntPtr _bitmap;
        private readonly IntPtr _oldBitmap;
        private readonly IntPtr _dc;

        public DibSection(int width, int height)
        {
            var info = new BitmapInfo();
            info.Header.Size = (uint)Marshal.SizeOf<BitmapInfoHeader>();
            info.Header.Width = width;
            info.Header.Height = -height; // 自上而下
            info.Header.Planes = 1;
            info.Header.BitCount = 32;
            info.Header.Compression = BI_RGB;
            var ptr = Marshal.AllocHGlobal(Marshal.SizeOf<BitmapInfo>());
            Marshal.StructureToPtr(info, ptr, false);
            try
            {
                _dc = CreateCompatibleDC(IntPtr.Zero);
                Bits = IntPtr.Zero;
                _bitmap = CreateDIBSection(_dc, ptr, DIB_RGB_COLORS, out var bits, IntPtr.Zero, 0);
                Bits = bits;
                _oldBitmap = SelectObject(_dc, _bitmap);
                Hdc = _dc;
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }
        }

        public void Dispose()
        {
            if (_dc != IntPtr.Zero && _bitmap != IntPtr.Zero)
                SelectObject(_dc, _oldBitmap);
            if (_bitmap != IntPtr.Zero) DeleteObject(_bitmap);
            if (_dc != IntPtr.Zero) DeleteDC(_dc);
        }
    }

    // ===== GDI P/Invoke =====
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
    [DllImport("user32.dll")] private static extern IntPtr GetWindowDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")]
    private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, int flags);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr hdcDst, int x, int y, int w, int h, IntPtr hdcSrc, int sx, int sy, int rop);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateDIBSection(IntPtr hdc, IntPtr bmi, int usage,
        out IntPtr bits, IntPtr hSection, int offset);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    public void Dispose() => Stop();
}
