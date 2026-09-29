using SharpGen.Runtime;
using WindowShare.Core.Logging;
using Vortice.DXGI;
using Vortice.Direct3D11;
using WindowShare.Core.Utils;

namespace WindowShare.Core.Capture;

/// <summary>
/// DXGI Desktop Duplication 回退引擎（WGC 不可用时）：
///   - 仅支持整屏捕获（不支持窗口，避免误共享窗口之外的桌面内容）；
///   - 桌面无变化时不产生帧（省带宽）；分辨率变化/全屏切换导致 ACCESS_LOST 时自动重建。
/// </summary>
public sealed class DxgiDuplicationEngine : ICaptureEngine
{
    public string Name => "DXGI Desktop Duplication";
    public bool IsGpuTexture => true;
    public bool IsRunning => _running;

    public event Action<CaptureFrame>? FrameArrived;
    public event Action<string>? StoppedBySystem;

    private readonly object _gate = new();
    private volatile bool _running;
    private long _frameCount;

    private ID3D11Device? _device;
    private IDXGIOutputDuplication? _dupl;
    private CaptureSource _source = null!;
    private Thread? _thread;
    private readonly ManualResetEventSlim _stopEvent = new(false);

    public static bool IsAvailable()
    {
        // Win8+；探测能否拿到 factory 即可
        try
        {
            using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
            return factory != null;
        }
        catch
        {
            return false;
        }
    }

    public void Start(CaptureSource source)
    {
        if (source.Kind != CaptureSourceKind.Monitor)
            throw new NotSupportedException("DXGI Desktop Duplication 仅支持整屏捕获（窗口捕获请使用 WGC 或 GDI）");

        lock (_gate)
        {
            if (_running) StopCore();
            _source = source;
            _device = D3D11DevicePool.GetOrCreate();
            _stopEvent.Reset();
            _running = true;
            _frameCount = 0;
            _thread = new Thread(CaptureLoop)
            {
                Name = "DxgiDuplicationLoop",
                IsBackground = true,
                Priority = ThreadPriority.AboveNormal,
            };
            _thread.Start();
            Logger.Info("DXGI", $"开始捕获: {source}");
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            if (!_running) return;
            _stopEvent.Set();
            _thread?.Join(2000);
            StopCore();
        }
    }

    private void StopCore()
    {
        _running = false;
        try { _dupl?.Dispose(); } catch { }
        _dupl = null;
        Logger.Info("DXGI", $"捕获已停止（共 {_frameCount} 帧）");
    }

    /// <summary>捕获线程主循环：Acquire → 复制 → Release → 派发</summary>
    private void CaptureLoop()
    {
        var recreate = true;
        while (!_stopEvent.IsSet && _running)
        {
            try
            {
                if (recreate)
                {
                    recreate = false;
                    RecreateDuplication();
                }

                var hr = AcquireOnce(out var frame);
                if (hr == ResultCode.Ok) continue;         // 已派发
                if (hr == ResultCode.Timeout) continue;    // 无新帧
                if (hr == ResultCode.AccessLost) { recreate = true; continue; }
            }
            catch (Exception ex)
            {
                if (_stopEvent.IsSet) break;
                Logger.Error("DXGI", "捕获循环异常，1 秒后重试", ex);
                recreate = true;
                if (_stopEvent.Wait(1000)) break;
            }
        }
    }

    private enum ResultCode { Ok, Timeout, AccessLost }

    /// <summary>重建 OutputDuplication（首次或 ACCESS_LOST 后）</summary>
    private void RecreateDuplication()
    {
        _dupl?.Dispose();
        _dupl = null;

        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        // 枚举所有 adapter/output，找到与目标 HMONITOR 匹配的那个
        for (uint a = 0; ; a++)
        {
            if (factory.EnumAdapters1(a, out var adapter).Failure) break;
            using (adapter)
            {
                for (uint o = 0; ; o++)
                {
                    if (adapter.EnumOutputs(o, out var output).Failure) break;
                    using (output)
                    {
                        if (output.Description.Monitor == _source.Handle)
                        {
                            using var output1 = output.QueryInterface<IDXGIOutput1>();
                            _dupl = output1.DuplicateOutput(_device);
                            Logger.Info("DXGI", $"OutputDuplication 已建立: adapter={adapter.Description.Description}");
                            return;
                        }
                    }
                }
            }
        }
        throw new InvalidOperationException("未找到指定显示器对应的 DXGI Output");
    }

    /// <summary>获取一帧并派发；返回分类结果</summary>
    private ResultCode AcquireOnce(out bool dispatched)
    {
        dispatched = false;
        try
        {
            var result = _dupl!.AcquireNextFrame(100, out var info, out var resource);
            if (result.Failure)
            {
                resource?.Dispose();
                if ((uint)result.Code == 0x887A0027) return ResultCode.Timeout;         // DXGI_ERROR_WAIT_TIMEOUT
                if ((uint)result.Code == 0x887A0026) return ResultCode.AccessLost;      // DXGI_ERROR_ACCESS_LOST
                result.CheckError();
                return ResultCode.Ok;
            }

            using (resource)
            using (var srcTexture = resource.QueryInterface<ID3D11Texture2D>())
            {
                var desc = srcTexture.Description;
                using var own = _device!.CreateTexture2D(new Texture2DDescription
                {
                    Width = desc.Width,
                    Height = desc.Height,
                    MipLevels = 1,
                    ArraySize = 1,
                    Format = desc.Format,
                    SampleDescription = new SampleDescription(1, 0),
                    Usage = ResourceUsage.Default,
                    BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget,
                    CPUAccessFlags = CpuAccessFlags.None,
                });
                _device.ImmediateContext.CopyResource(own, srcTexture);

                Interlocked.Increment(ref _frameCount);
                FrameArrived?.Invoke(new CaptureFrame
                {
                    Width = (int)desc.Width,
                    Height = (int)desc.Height,
                    TimestampUtc = DateTime.UtcNow.Ticks,
                    QpcTimestamp = System.Diagnostics.Stopwatch.GetTimestamp(),
                    Texture = own,
                });
                dispatched = true;
            }
            _dupl.ReleaseFrame();
            return ResultCode.Ok;
        }
        catch (Exception ex) when ((uint)ex.HResult == 0x887A0027) // DXGI_ERROR_WAIT_TIMEOUT
        {
            return ResultCode.Timeout;
        }
        catch (Exception ex) when ((uint)ex.HResult == 0x887A0026) // DXGI_ERROR_ACCESS_LOST
        {
            return ResultCode.AccessLost;
        }
    }

    public void Dispose() => Stop();
}
