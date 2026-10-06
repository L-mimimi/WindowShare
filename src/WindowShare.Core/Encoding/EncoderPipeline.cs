using Vortice.Direct3D11;
using Vortice.DXGI;
using WindowShare.Core.Capture;
using WindowShare.Core.Logging;
using WindowShare.Core.Utils;

namespace WindowShare.Core.Encoding;

/// <summary>
/// 编码管线：CaptureFrame（GPU BGRA 纹理 / CPU BGRA 像素）→ GPU 转换 NV12 → H.264 编码 → Encoded 事件。
/// 支持运行中调整码率与输出分辨率（动态码率/动态分辨率），并按目标帧率节流。
/// </summary>
public sealed class EncoderPipeline : IDisposable
{
    private readonly ID3D11Device _device;
    private readonly GpuVideoProcessor _videoProcessor;
    // 输出尺寸变化时会整体重建（见 UpdateOutputSize），因此不是 readonly。
    // 接口类型：MfVideoEncoder 之外，FFmpeg 厂商硬编等后端共用同一契约（Step 1 抽象）。
    private IVideoEncoder _encoder;
    private readonly object _gate = new();

    private int _outWidth;      // 当前编码输出宽
    private int _outHeight;     // 当前编码输出高
    private int _dynamicMaxW;   // 拥塞控制允许的最大输出宽（动态分辨率上限）
    private int _dynamicMaxH;   // 拥塞控制允许的最大输出高
    /// <summary>会话初始配置尺寸（不随动态降档改写），用于夹取"恢复分辨率"请求</summary>
    private readonly int _configWidth;
    private readonly int _configHeight;
    private ID3D11Texture2D? _uploadTexture;   // CPU BGRA → GPU 上传纹理（复用）
    private int _uploadWidth, _uploadHeight;
    private long _encodedFrames;
    private long _encodedBytes;
    private long _droppedFrames;
    // 帧率节流：捕获源可能高于目标帧率（如 144Hz 显示器 + 30fps 档），
    // 必须丢帧，否则码流实际帧率与声明帧率不符，Viewer 端会持续累积延迟。
    private long _frameIntervalQpc;
    private long _lastAcceptedQpc;
    // 关键帧节奏按「时间」而不是「帧数」触发：真实捕获帧率可能远低于目标
    // （静态桌面下 WGC 只在内容变化时出帧，约 8fps），按帧数计的 GOP 会被拉长到
    // 7 秒以上，新接入的观看者要一直黑屏等到下一个 IDR。
    private const int KeyFrameIntervalSeconds = 2;
    private long _keyFrameIntervalQpc;
    private long _lastKeyFrameQpc;
    private bool _keyFrameRequestWarned;

    /// <summary>编码输出（编码器线程上触发）</summary>
    public event Action<EncodedVideoFrame>? Encoded;

    /// <summary>编码器显示名（UI 显示，如 NVIDIA H.264 Encoder MFT）</summary>
    public string EncoderName => _encoder.EncoderName;

    /// <summary>是否硬件编码</summary>
    public bool IsHardwareEncoder => _encoder.IsHardware;

    /// <summary>是否零拷贝路径</summary>
    public bool IsZeroCopy => _encoder.IsD3DAccelerated;

    /// <summary>当前编码分辨率</summary>
    public (int Width, int Height) OutputSize => (_outWidth, _outHeight);

    public EncoderSettings Settings { get; private set; }

    /// <summary>因帧率节流被丢弃的帧数（诊断用）</summary>
    public long DroppedFrames => Interlocked.Read(ref _droppedFrames);

    /// <summary>
    /// 是否由本管线做帧率节流。默认 true（调用方直接喂帧的场景，如冒烟 Part2b 的定速投喂）。
    /// 会话在捕获引擎自身实现 <see cref="Capture.IFrameRateLimited"/> 时置为 false ——
    /// 让"谁节流"只有一处权威，避免两层独立节流各自带相位与容差互相抢帧
    /// （实测两层串联会把 24fps 目标压到 19.5fps）。启动会话后不再变更。
    /// </summary>
    public bool FrameThrottleEnabled { get; set; } = true;

    public EncoderPipeline(EncoderSettings settings)
    {
        Settings = settings;
        (_outWidth, _outHeight) = EvenSize(settings.Width, settings.Height);
        (_configWidth, _configHeight) = EvenSize(settings.Width, settings.Height);
        _dynamicMaxW = _outWidth;
        _dynamicMaxH = _outHeight;
        _frameIntervalQpc = System.Diagnostics.Stopwatch.Frequency / Math.Max(1, settings.Fps);
        _keyFrameIntervalQpc = System.Diagnostics.Stopwatch.Frequency * KeyFrameIntervalSeconds;

        _device = D3D11DevicePool.GetOrCreate();
        _videoProcessor = new GpuVideoProcessor(_device);
        // 传入共享设备：硬件编码器直接吃 GPU NV12 纹理（零拷贝）
        _encoder = CreateEncoder(Settings);
        _encoder.Encoded += OnEncoderOutput;
        Logger.Info("Pipeline",
            $"编码管线就绪: {_outWidth}x{_outHeight} @ {Settings.Fps}fps, " +
            $"编码器={EncoderName}, 硬件={IsHardwareEncoder}, 零拷贝={IsZeroCopy}");
    }

    /// <summary>
    /// 编码器出帧回调。用命名方法而不是 lambda：分辨率变化重建编码器时需要把旧编码器的
    /// 事件摘掉，否则它的尾帧会混进新码流（观看端会看到尺寸突变的花屏）。
    /// </summary>
    private void OnEncoderOutput(EncodedVideoFrame f)
    {
        Interlocked.Increment(ref _encodedFrames);
        Interlocked.Add(ref _encodedBytes, f.PayloadSize);
        // 以「实际出帧」为准刷新关键帧时刻（从请求发出到 IDR 落地之间有延迟）
        if (f.Keyframe)
            Interlocked.Exchange(ref _lastKeyFrameQpc, System.Diagnostics.Stopwatch.GetTimestamp());
        Encoded?.Invoke(f);
    }

    /// <summary>
    /// 提交一帧捕获（线程安全；GPU/CPU 两种输入形态自动处理）。
    /// </summary>
    public void Submit(CaptureFrame frame)
    {
        lock (_gate)
        {
            try
            {
                // 帧率节流（兜底）：早于目标帧间隔到达的帧直接丢弃（finally 仍会释放帧）。
                // 仅当**捕获引擎自身不做节流**时才启用（见 FrameThrottleEnabled）：两层独立节流各自有
                // 相位与容差，串在一起会互相抢帧——实测把 24fps 目标压到 19.5fps。
                // 保留本层是因为有调用方**直接喂管线**（冒烟 Part2b 的定速投喂，以及将来不实现
                // IFrameRateLimited 的引擎），此时没有上游节流，必须由管线负责。
                var nowQpc = System.Diagnostics.Stopwatch.GetTimestamp();
                if (FrameThrottleEnabled && _lastAcceptedQpc != 0 &&
                    nowQpc - _lastAcceptedQpc < _frameIntervalQpc - _frameIntervalQpc / 8)
                {
                    Interlocked.Increment(ref _droppedFrames);
                    return;
                }
                _lastAcceptedQpc = nowQpc;

                // 关键帧兜底：距上一个 IDR 超过间隔就强制一个（首帧、低实际帧率、新观看者接入）
                if (nowQpc - Interlocked.Read(ref _lastKeyFrameQpc) >= _keyFrameIntervalQpc)
                    RequestKeyframe();

                // 目标尺寸 = 源尺寸等比缩放进 (动态上限) 盒子内，取偶（NV12 要求）。
                // 必须等比：源宽高比可能与编码设置不同（如 16:10 显示器、任意比例窗口），
                // 独立夹取宽高会把画面压扁。
                var (tw, th) = VideoFormatPlanner.FitInto(frame.Width, frame.Height, _dynamicMaxW, _dynamicMaxH);
                if (tw != _outWidth || th != _outHeight)
                    UpdateOutputSize(tw, th);

                var bgra = frame.IsGpu ? frame.Texture! : UploadBgra(frame.BgraPixels!, frame.Width, frame.Height);

                // GPU 内 BGRA → NV12（可缩放）
                var nv12 = _videoProcessor.ConvertBgraToNv12(bgra, _outWidth, _outHeight);
                if (_encoder.IsD3DAccelerated)
                {
                    // 零拷贝：NV12 纹理直接进编码器
                    _encoder.EncodeNv12Texture(nv12, frame.TimestampUtc);
                }
                else
                {
                    // 软件编码器：GPU staging → CPU NV12
                    var bytes = CopyTextureToNv12Bytes(nv12, _outWidth, _outHeight);
                    _encoder.EncodeNv12Bytes(bytes, frame.TimestampUtc);
                }
            }
            catch (Exception ex)
            {
                Logger.Error("Pipeline", "编码管线异常", ex);
            }
            finally
            {
                frame.Dispose();
            }
        }
    }

    /// <summary>
    /// 动态码率：调整编码器目标码率。
    ///
    /// 编码器拒绝时**不改写 Settings.BitrateBps**：该字段是"本会话实际使用的码率"的唯一出处
    /// ——它经 AuthResult.TargetBitrateBps 与周期 StatsInfo 上报给观看端，也用于编码器重建
    /// （分辨率切换）时恢复正确档位。若在设置失败时仍改写它，UI 会显示一个编码器并不遵守的
    /// 目标值（v1.5.2 审计 A4：FFmpeg 厂商硬编路径上 SetBitrate 恒返回 false）。
    /// </summary>
    public bool SetBitrate(int bitrateBps)
    {
        var ok = _encoder.SetBitrate(bitrateBps);
        if (ok)
        {
            Settings = Settings with { BitrateBps = bitrateBps };
            Logger.Info("Pipeline", $"动态码率 → {bitrateBps / 1000} kbps（已生效）");
        }
        else
        {
            Logger.Warn("Pipeline",
                $"动态码率请求 {bitrateBps / 1000} kbps 未被编码器接受，保持 " +
                $"{Settings.BitrateBps / 1000} kbps（本机编码器不支持动态重配；拥塞时仍会降分辨率）");
        }
        return ok;
    }

    /// <summary>请求关键帧（Viewer 接入/重连，或超过关键帧间隔时由管线自动触发）</summary>
    public bool RequestKeyframe()
    {
        var ok = _encoder.ForceKeyFrame();
        if (!ok && !_keyFrameRequestWarned)
        {
            _keyFrameRequestWarned = true;
            Logger.Warn("Pipeline",
                $"编码器不认 CODECAPI_AVEncVideoForceKeyFrame（{EncoderName}）：" +
                "关键帧只按编码器内部 GOP 周期出现；分发端已改为给新观看者补发缓存 GOP，接入即出画面");
        }
        return ok;
    }

    /// <summary>
    /// 编码器构造入口（Step 3 工厂化）：FFmpeg 厂商硬编（探测到 nvenc/amf/qsv）→ MF 现链，
    /// 全部失败抛异常（现状语义）。
    /// </summary>
    private IVideoEncoder CreateEncoder(EncoderSettings settings) => VideoEncoderFactory.Create(settings, _device);

    /// <summary>
    /// 动态分辨率：更新编码输出尺寸上限（等比缩放由 VideoProcessor 完成）。
    /// 实际输出 = min(上限, 源尺寸)，因此恢复时传回原始尺寸即可。
    ///
    /// 夹取基准必须是**会话初始配置尺寸**（`_configWidth/_configHeight`），而不是 `Settings.Width`：
    /// 后者会在 `UpdateOutputSize` 里被改写成降档后的尺寸，用它夹取会让"恢复到原始分辨率"的请求
    /// 被夹回当前降档值并因相等而提前返回 —— 分辨率在本会话内永久回不去（v1.5.2 审计 A3）。
    /// </summary>
    public void SetDynamicResolution(int width, int height)
    {
        lock (_gate)
        {
            var (w, h) = EvenSize(Math.Min(width, _configWidth), Math.Min(height, _configHeight));
            if (w == _dynamicMaxW && h == _dynamicMaxH) return;
            _dynamicMaxW = w;
            _dynamicMaxH = h;
            Logger.Info("Pipeline", $"动态分辨率上限 → {w}x{h}（会话原始 {_configWidth}x{_configHeight}）");
        }
    }

    public (long Frames, long Bytes) GetCounters()
        => (Interlocked.Read(ref _encodedFrames), Interlocked.Read(ref _encodedBytes));

    /// <summary>
    /// 输出尺寸变化（源尺寸变化 / 拥塞降档）：必须重建编码器。
    ///
    /// 只改 GPU 侧的目标尺寸是不够的：编码器的输入媒体类型仍声明旧宽高，喂进去的
    /// NV12 纹理/字节与声明不符会花屏、错位甚至让编码器直接报错；而且每帧的
    /// EncodedVideoFrame.Width/Height 会一直停在旧值，观看端按旧尺寸建解码器同样解不出来。
    /// </summary>
    private void UpdateOutputSize(int w, int h)
    {
        var settings = Settings with { Width = w, Height = h };

        IVideoEncoder next;
        try
        {
            next = CreateEncoder(settings);
        }
        catch (Exception ex)
        {
            // 没有编码器支持新尺寸：保持原尺寸继续跑，断流比降质更糟
            Logger.Error("Pipeline", $"编码分辨率切换到 {w}x{h} 失败，保持 {_outWidth}x{_outHeight}", ex);
            return;
        }

        var old = _encoder;
        old.Encoded -= OnEncoderOutput;   // 先摘事件，再换引用
        next.Encoded += OnEncoderOutput;
        _encoder = next;
        Settings = settings;
        (_outWidth, _outHeight) = (w, h);
        _keyFrameRequestWarned = false;   // 换了编码器，要重新报一次它认不认 ForceKeyFrame
        _lastAcceptedQpc = 0;             // 重置节流基准，避免重建后的第一帧被误丢

        Logger.Info("Pipeline",
            $"编码分辨率: → {w}x{h}（编码器已重建: {next.EncoderName}, " +
            $"硬件={next.IsHardware}, 零拷贝={next.IsD3DAccelerated}）");

        // 旧编码器必须放到后台释放。MfVideoEncoder.Dispose 会 Join 事件泵线程，
        // 而泵线程此刻可能正卡在 ShareSession._gate 上（分发帧给 sink）；本线程又持有
        // 管线 _gate，同步等待就会与 ShareSession.SetDynamicResolution
        // （先持 session._gate、再取 pipeline._gate）形成锁环 → 死锁。
        _ = System.Threading.Tasks.Task.Run(() =>
        {
            try { old.Dispose(); }
            catch (Exception ex) { Logger.Warn("Pipeline", $"旧编码器释放异常: {ex.Message}"); }
        });
    }

    /// <summary>把 CPU BGRA 像素上传到 GPU（复用纹理）</summary>
    private ID3D11Texture2D UploadBgra(byte[] pixels, int width, int height)
    {
        if (_uploadTexture == null || _uploadWidth != width || _uploadHeight != height)
        {
            _uploadTexture?.Dispose();
            _uploadTexture = _device.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)width,
                Height = (uint)height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget,
                CPUAccessFlags = CpuAccessFlags.None,
            });
            _uploadWidth = width;
            _uploadHeight = height;
        }

        unsafe
        {
            fixed (byte* p = pixels)
            {
                _device.ImmediateContext.UpdateSubresource(_uploadTexture, 0,
                    null, (IntPtr)p, (uint)(width * 4), 0);
            }
        }
        return _uploadTexture;
    }

    /// <summary>软件编码器路径的 NV12 staging 纹理（尺寸变化时重建，避免每帧创建）</summary>
    private ID3D11Texture2D? _nv12Staging;
    private int _stagingWidth, _stagingHeight;
    /// <summary>软件编码器路径的 NV12 字节缓冲（_gate 内独占使用，按需增长）</summary>
    private byte[] _nv12Bytes = Array.Empty<byte>();

    /// <summary>把 GPU NV12 纹理拷回 CPU（软件编码器路径；staging 纹理与输出缓冲均复用）</summary>
    private byte[] CopyTextureToNv12Bytes(ID3D11Texture2D nv12, int width, int height)
    {
        if (_nv12Staging == null || _stagingWidth != width || _stagingHeight != height)
        {
            _nv12Staging?.Dispose();
            _nv12Staging = _device.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)width,
                Height = (uint)height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.NV12,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Staging,
                CPUAccessFlags = CpuAccessFlags.Read,
            });
            _stagingWidth = width;
            _stagingHeight = height;
        }
        var staging = _nv12Staging;

        var ctx = _device.ImmediateContext;
        ctx.CopyResource(staging, nv12);
        ctx.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None, out var mapped).CheckError();
        try
        {
            var stride = (int)mapped.RowPitch;
            var total = width * height * 3 / 2;
            if (_nv12Bytes.Length < total) _nv12Bytes = new byte[total];
            var result = _nv12Bytes;
            unsafe
            {
                fixed (byte* dst = result)
                {
                    var src = (byte*)mapped.DataPointer;
                    // Y 平面
                    Buffer.MemoryCopy(src, dst, (long)height * width, (long)height * width);
                    // UV 平面（staging 的 rowPitch 可能大于 width，需按行拷贝）
                    var uvRows = height / 2;
                    for (var r = 0; r < uvRows; r++)
                    {
                        Buffer.MemoryCopy(
                            src + (long)stride * (height + r),
                            dst + (long)width * height + (long)r * width,
                            width, width);
                    }
                }
            }
            return result;
        }
        finally
        {
            ctx.Unmap(staging, 0);
        }
    }

    private static (int, int) EvenSize(int w, int h) => (w & ~1, h & ~1);

    public void Dispose()
    {
        IVideoEncoder encoder;
        lock (_gate) encoder = _encoder;
        encoder.Dispose();
        _videoProcessor.Dispose();
        _uploadTexture?.Dispose();
        _nv12Staging?.Dispose();
    }
}
