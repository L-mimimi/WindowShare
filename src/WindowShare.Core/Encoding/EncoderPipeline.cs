using Vortice.Direct3D11;
using Vortice.DXGI;
using WindowShare.Core.Capture;
using WindowShare.Core.Logging;
using WindowShare.Core.Utils;

namespace WindowShare.Core.Encoding;

/// <summary>
/// 编码管线：CaptureFrame（GPU BGRA 纹理 / CPU BGRA 像素）→ GPU 转换 NV12 → H.264 编码 → Encoded 事件。
/// 支持运行中调整码率与输出分辨率（动态码率/动态分辨率）。
/// </summary>
public sealed class EncoderPipeline : IDisposable
{
    private readonly ID3D11Device _device;
    private readonly GpuVideoProcessor _videoProcessor;
    private readonly MfH264Encoder _encoder;
    private readonly object _gate = new();

    private int _outWidth;      // 当前编码输出宽
    private int _outHeight;     // 当前编码输出高
    private int _dynamicMaxW;   // 拥塞控制允许的最大输出宽（动态分辨率上限）
    private int _dynamicMaxH;   // 拥塞控制允许的最大输出高
    private ID3D11Texture2D? _uploadTexture;   // CPU BGRA → GPU 上传纹理（复用）
    private int _uploadWidth, _uploadHeight;
    private long _encodedFrames;
    private long _encodedBytes;

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

    public EncoderPipeline(EncoderSettings settings)
    {
        Settings = settings;
        (_outWidth, _outHeight) = EvenSize(settings.Width, settings.Height);
        _dynamicMaxW = _outWidth;
        _dynamicMaxH = _outHeight;

        _device = D3D11DevicePool.GetOrCreate();
        _videoProcessor = new GpuVideoProcessor(_device);
        // 传入共享设备：硬件编码器直接吃 GPU NV12 纹理（零拷贝）
        _encoder = new MfH264Encoder(Settings, _device);
        _encoder.Encoded += f =>
        {
            Interlocked.Increment(ref _encodedFrames);
            Interlocked.Add(ref _encodedBytes, f.PayloadSize);
            Encoded?.Invoke(f);
        };
        Logger.Info("Pipeline",
            $"编码管线就绪: {_outWidth}x{_outHeight} @ {Settings.Fps}fps, " +
            $"编码器={EncoderName}, 硬件={IsHardwareEncoder}, 零拷贝={IsZeroCopy}");
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
                // 目标尺寸 = min(动态上限, 源尺寸)，并取偶（NV12 要求）
                var (tw, th) = EvenSize(Math.Min(_dynamicMaxW, frame.Width), Math.Min(_dynamicMaxH, frame.Height));
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

    /// <summary>动态码率：调整编码器目标码率</summary>
    public bool SetBitrate(int bitrateBps)
    {
        Settings = Settings with { BitrateBps = bitrateBps };
        var ok = _encoder.SetBitrate(bitrateBps);
        Logger.Info("Pipeline", $"动态码率 → {bitrateBps / 1000} kbps ({(ok ? "已生效" : "设置失败")})");
        return ok;
    }

    /// <summary>请求关键帧（Viewer 重连/接入时）</summary>
    public void RequestKeyframe() => _encoder.ForceKeyFrame();

    /// <summary>
    /// 动态分辨率：更新编码输出尺寸上限（等比缩放由 VideoProcessor 完成）。
    /// 实际输出 = min(上限, 源尺寸)，因此恢复时传回原始尺寸即可。
    /// </summary>
    public void SetDynamicResolution(int width, int height)
    {
        lock (_gate)
        {
            var (w, h) = EvenSize(Math.Min(width, Settings.Width), Math.Min(height, Settings.Height));
            if (w == _dynamicMaxW && h == _dynamicMaxH) return;
            _dynamicMaxW = w;
            _dynamicMaxH = h;
            Logger.Info("Pipeline", $"动态分辨率上限 → {w}x{h}");
        }
    }

    public (long Frames, long Bytes) GetCounters()
        => (Interlocked.Read(ref _encodedFrames), Interlocked.Read(ref _encodedBytes));

    private void UpdateOutputSize(int w, int h)
    {
        Logger.Info("Pipeline", $"编码分辨率: {_outWidth}x{_outHeight} → {w}x{h}");
        (_outWidth, _outHeight) = (w, h);
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

    /// <summary>把 GPU NV12 纹理拷回 CPU（软件编码器路径）</summary>
    private byte[] CopyTextureToNv12Bytes(ID3D11Texture2D nv12, int width, int height)
    {
        var staging = _device.CreateTexture2D(new Texture2DDescription
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
        using (staging)
        {
            var ctx = _device.ImmediateContext;
            ctx.CopyResource(staging, nv12);
            ctx.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None, out var mapped).CheckError();
            try
            {
                var stride = (int)mapped.RowPitch;
                var result = new byte[width * height * 3 / 2];
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
    }

    private static (int, int) EvenSize(int w, int h) => (w & ~1, h & ~1);

    public void Dispose()
    {
        _encoder.Dispose();
        _videoProcessor.Dispose();
        _uploadTexture?.Dispose();
    }
}
