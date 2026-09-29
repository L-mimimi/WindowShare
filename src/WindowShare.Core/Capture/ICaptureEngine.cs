using Vortice.Direct3D11;

namespace WindowShare.Core.Capture;

/// <summary>
/// 一帧捕获结果。两种形态互斥：
///   - GPU 路径：<see cref="Texture"/>（BGRA8 D3D11 纹理，引擎内部已复制，归消费方所有，用完必须 Dispose）
///   - CPU 路径：<see cref="BgraPixels"/>（自上而下 BGRA，stride = Width*4，GDI 兜底引擎）
/// 事件在线程池/捕获线程上触发，消费方不得长时间阻塞，应尽快拷走并返回。
/// </summary>
public sealed class CaptureFrame : IDisposable
{
    public required int Width { get; init; }
    public required int Height { get; init; }

    /// <summary>UTC 时间戳（DateTime.UtcNow.Ticks，100ns；随帧头发往 Viewer 计算延迟）</summary>
    public required long TimestampUtc { get; init; }

    /// <summary>高精度性能计数（本机排序/测延迟用）</summary>
    public required long QpcTimestamp { get; init; }

    /// <summary>GPU 纹理（BGRA8；WGC/DXGI 引擎）</summary>
    public ID3D11Texture2D? Texture { get; init; }

    /// <summary>CPU 像素（BGRA；GDI 引擎）</summary>
    public byte[]? BgraPixels { get; init; }

    /// <summary>是否 GPU 纹理帧</summary>
    public bool IsGpu => Texture != null;

    public void Dispose()
    {
        Texture?.Dispose();
    }
}

/// <summary>
/// 捕获引擎统一接口。实现：GraphicsCaptureEngine(WGC) → DxgiDuplicationEngine → GdiCaptureEngine。
/// </summary>
public interface ICaptureEngine : IDisposable
{
    /// <summary>引擎显示名（用于日志与 UI 提示）</summary>
    string Name { get; }

    /// <summary>是否输出 GPU 纹理（决定是否需要额外 CPU 拷贝）</summary>
    bool IsGpuTexture { get; }

    /// <summary>是否处于运行状态</summary>
    bool IsRunning { get; }

    /// <summary>新帧到达（非 UI 线程）</summary>
    event Action<CaptureFrame>? FrameArrived;

    /// <summary>共享被系统终止（窗口关闭/显示器变化等），UI 层据此提示用户</summary>
    event Action<string>? StoppedBySystem;

    /// <summary>启动捕获指定源（可在运行中切换源：内部自动重启）</summary>
    void Start(CaptureSource source);

    /// <summary>停止捕获</summary>
    void Stop();
}
