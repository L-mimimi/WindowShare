namespace WindowShare.Core.Capture;

/// <summary>捕获源类型</summary>
public enum CaptureSourceKind
{
    /// <summary>整个显示器</summary>
    Monitor,
    /// <summary>单个顶级窗口（只共享该窗口内容，窗口外的桌面内容不会进入视频流）</summary>
    Window,
}

/// <summary>捕获源描述（显示器或窗口）</summary>
public sealed record CaptureSource(
    CaptureSourceKind Kind,
    IntPtr Handle,
    string Title,
    PixelRect Bounds,
    bool IsPrimary)
{
    public override string ToString() =>
        Kind == CaptureSourceKind.Monitor
            ? $"[屏幕] {Title} ({Bounds.Width}×{Bounds.Height})"
            : $"[窗口] {Title}";
}

/// <summary>像素矩形（虚拟屏幕坐标）</summary>
public readonly record struct PixelRect(int X, int Y, int Width, int Height);
