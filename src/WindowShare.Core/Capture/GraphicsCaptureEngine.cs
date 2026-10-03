using WindowShare.Core.Logging;
using Vortice.Direct3D11;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using WindowShare.Core.Utils;

namespace WindowShare.Core.Capture;

/// <summary>
/// Windows Graphics Capture 捕获引擎（首选）：
///   - 支持 Windows 10 1903+；窗口/显示器均可；
///   - GPU 纹理零拷贝输出，编码器可直接消费；
///   - 尝试去掉系统黄色边框（IsBorderRequired，Win11/Server 2022+）。
/// </summary>
public sealed class GraphicsCaptureEngine : ICaptureEngine
{
    public string Name => "Windows Graphics Capture";
    public bool IsGpuTexture => true;
    public bool IsRunning => _running;

    /// <summary>新帧（引擎自复制纹理，归消费方所有）</summary>
    public event Action<CaptureFrame>? FrameArrived;
    public event Action<string>? StoppedBySystem;

    private readonly object _gate = new();
    private volatile bool _running;
    private long _frameCount;

    private ID3D11Device? _device;
    private Windows.Graphics.DirectX.Direct3D11.IDirect3DDevice? _winrtDevice;
    private GraphicsCaptureItem? _item;
    private Direct3D11CaptureFramePool? _pool;
    private GraphicsCaptureSession? _session;
    private DirectXPixelFormat _format = DirectXPixelFormat.B8G8R8A8UIntNormalized;

    /// <summary>回收待复用的纹理（消费方 Dispose 帧时归还；尺寸变化/停止时清空销毁）</summary>
    private readonly Queue<ID3D11Texture2D> _texturePool = new();
    /// <summary>池上限：捕获→编码→归还的在途窗口通常 ≤2 帧，3 个留余量</summary>
    private const int MaxPooledTextures = 3;

    /// <summary>系统是否支持 WGC</summary>
    public static bool IsAvailable()
    {
        if (!WgcInterop.IsSupported()) return false;
        try
        {
            return GraphicsCaptureSession.IsSupported();
        }
        catch
        {
            return false;
        }
    }

    public void Start(CaptureSource source)
    {
        lock (_gate)
        {
            if (_running) StopCore();
            _device = D3D11DevicePool.GetOrCreate();
            _winrtDevice = D3D11DevicePool.GetOrCreateWinRTDevice();

            // 从 HWND/HMONITOR 创建捕获项（通过互操作接口，兼容 1903）
            _item = source.Kind == CaptureSourceKind.Window
                ? WgcInterop.CreateItemForWindow(source.Handle)
                : WgcInterop.CreateItemForMonitor(source.Handle);

            _item.Closed += OnItemClosed;
            // CreateFreeThreaded：帧回调在线程池线程触发，不要求调用线程持有 DispatcherQueue
            _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(
                _winrtDevice, _format, 2, _item.Size);
            _session = _pool.CreateCaptureSession(_item);

            // 保留鼠标指针（观看端需要看到操作位置）；这不是远程控制，仅画面呈现
            _session.IsCursorCaptureEnabled = true;

            // 尝试去掉系统捕获边框（需要 Win11/Server 2022+；失败则保留默认边框）
            try
            {
                var prop = typeof(GraphicsCaptureSession).GetProperty("IsBorderRequired");
                if (prop != null)
                    prop.SetValue(_session, false);
            }
            catch (Exception ex)
            {
                Logger.Warn("WGC", $"去除捕获边框不可用: {ex.Message}");
            }

            _pool.FrameArrived += OnFrameArrived;
            _session.StartCapture();
            _running = true;
            _frameCount = 0;
            Logger.Info("WGC", $"开始捕获: {source} ({_item.Size.Width}x{_item.Size.Height})");
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            if (_running) StopCore();
        }
    }

    private void StopCore()
    {
        try
        {
            if (_pool != null) _pool.FrameArrived -= OnFrameArrived;
            if (_item != null) _item.Closed -= OnItemClosed;
            _session?.Dispose();
            _pool?.Dispose();
            _item = null;
            _session = null;
            _pool = null;
        }
        catch (Exception ex)
        {
            Logger.Warn("WGC", $"停止捕获异常: {ex.Message}");
        }
        _running = false;
        // 在途帧归还时会发现 _running=false 而自毁，这里清掉已在池中的
        DrainTexturePool();
        Logger.Info("WGC", $"捕获已停止（共 {_frameCount} 帧）");
    }

    private void OnItemClosed(GraphicsCaptureItem sender, object? args)
    {
        Logger.Warn("WGC", "捕获目标已关闭（窗口被关闭或显示器断开）");
        StoppedBySystem?.Invoke("共享目标已关闭");
        Stop();
    }

    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object? args)
    {
        if (!_running) return;
        // Stop 置空后事件泵可能还排着一帧，局部快照兜底空引用
        var item = _item;
        var pool = _pool;
        if (item == null || pool == null) return;
        try
        {
            using var frame = sender.TryGetNextFrame();
            if (frame == null) return;

            // 帧内容尺寸可能变化（窗口/显示器分辨率改变）→ 重建 FramePool
            var size = frame.ContentSize;
            if (size.Width != item.Size.Width || size.Height != item.Size.Height)
            {
                Logger.Info("WGC", $"捕获尺寸变化: {item.Size.Width}x{item.Size.Height} → {size.Width}x{size.Height}");
                pool.Recreate(_winrtDevice, _format, 2, size);
            }

            // 零拷贝取出源纹理，再复制到私有纹理（FramePool 需要立即回收其缓冲区）
            using var srcTexture = WgcInterop.GetTextureFromSurface(frame.Surface);
            var ownTexture = TakePooledTexture(srcTexture, size.Width, size.Height);
            var ctx = _device!.ImmediateContext;
            ctx.CopyResource(ownTexture, srcTexture);

            Interlocked.Increment(ref _frameCount);
            var arrived = new CaptureFrame
            {
                Width = size.Width,
                Height = size.Height,
                TimestampUtc = DateTime.UtcNow.Ticks,
                QpcTimestamp = System.Diagnostics.Stopwatch.GetTimestamp(),
                Texture = ownTexture, // 所有权移交消费方
            };
            // 消费方 Dispose 整帧时纹理归还池中（池化对消费方透明）
            arrived.TextureRelease = () => ReturnPooledTexture(ownTexture);
            FrameArrived?.Invoke(arrived);
        }
        catch (Exception ex)
        {
            // 单帧失败不中断捕获；连续失败由上层超时机制处理
            Logger.Error("WGC", "处理捕获帧异常", ex);
        }
    }

    /// <summary>从池里取同尺寸纹理；没有就新建（回调线程 + 归还线程并发访问，锁保护）</summary>
    private ID3D11Texture2D TakePooledTexture(ID3D11Texture2D src, int width, int height)
    {
        lock (_gate)
        {
            while (_texturePool.Count > 0)
            {
                var pooled = _texturePool.Dequeue();
                var d = pooled.Description;
                if ((int)d.Width == width && (int)d.Height == height) return pooled;
                pooled.Dispose();   // 尺寸不匹配（刚发生分辨率切换），丢弃
            }
        }
        return CreateSameTexture(src, width, height);
    }

    /// <summary>归还纹理；池满则销毁（引擎停止后归还也会走这里）</summary>
    private void ReturnPooledTexture(ID3D11Texture2D texture)
    {
        lock (_gate)
        {
            if (!_running || _texturePool.Count >= MaxPooledTextures || _device == null)
            {
                texture.Dispose();
                return;
            }
            _texturePool.Enqueue(texture);
        }
    }

    /// <summary>清空纹理池（持 _gate 调用或停止流程调用）</summary>
    private void DrainTexturePool()
    {
        lock (_gate)
        {
            while (_texturePool.Count > 0) _texturePool.Dequeue().Dispose();
        }
    }

    /// <summary>创建与源纹理同尺寸/同用法的 BGRA 纹理</summary>
    private static ID3D11Texture2D CreateSameTexture(ID3D11Texture2D src, int width, int height)
    {
        var desc = new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Vortice.DXGI.Format.B8G8R8A8_UNorm,
            SampleDescription = new Vortice.DXGI.SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget,
            CPUAccessFlags = CpuAccessFlags.None,
            MiscFlags = ResourceOptionFlags.None,
        };
        return src.Device.CreateTexture2D(desc);
    }

    public void Dispose() => Stop();
}
