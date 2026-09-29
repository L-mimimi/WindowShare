using Vortice.Direct3D11;
using Vortice.DXGI;
using WindowShare.Core.Logging;

namespace WindowShare.Core.Capture;

/// <summary>
/// GPU 视频处理器（D3D11 VideoProcessor）：
///   - BGRA(全范围 709) → NV12(窄范围 709)：H.264 标准输入格式；
///   - 同时支持降采样（动态分辨率阶梯使用）；
///   - 全程 GPU 内完成，CPU 零拷贝。
/// </summary>
public sealed class GpuVideoProcessor : IDisposable
{
    // DXGI_COLOR_SPACE_TYPE 枚举数值：
    //   0 = RGB_FULL_G22_NONE_P709（BGRA 输入）
    //   6 = YCBCR_STUDIO_G22_LEFT_P709（NV12 输出，Studio 范围）
    private const int ColorSpaceRgbFullG22NoneP709 = 0;
    private const int ColorSpaceYcbcrStudioG22LeftP709 = 6;

    private readonly ID3D11Device _device;
    private readonly ID3D11VideoDevice _videoDevice;
    private readonly ID3D11VideoContext _videoContext;
    private readonly ID3D11VideoContext1 _videoContext1;
    private ID3D11VideoProcessorEnumerator? _enumerator;
    private ID3D11VideoProcessor? _processor;
    private ID3D11Texture2D? _nv12Output;
    private int _outWidth, _outHeight;
    // VideoProcessor 实例当前对应的输入/输出尺寸（任一变化都必须重建，见 EnsureProcessor）
    private int _procInWidth, _procInHeight, _procOutWidth, _procOutHeight;

    public GpuVideoProcessor(ID3D11Device device)
    {
        _device = device;
        _videoDevice = device.QueryInterface<ID3D11VideoDevice>();
        _videoContext = device.ImmediateContext.QueryInterface<ID3D11VideoContext>();
        _videoContext1 = _videoContext.QueryInterface<ID3D11VideoContext1>();
        Logger.Info("VideoProc", "GPU 视频处理器已创建");
    }

    /// <summary>
    /// 把 BGRA 纹理转换为 NV12 纹理（可缩放到 outWidth×outHeight）。
    /// 返回的纹理由本类持有并在下次调用时复用；如需跨帧保存请 CopyResource。
    /// </summary>
    public ID3D11Texture2D ConvertBgraToNv12(ID3D11Texture2D bgraInput, int outWidth, int outHeight)
    {
        EnsureOutput(outWidth, outHeight);
        var inW = (int)bgraInput.Description.Width;
        var inH = (int)bgraInput.Description.Height;
        EnsureProcessor(inW, inH, outWidth, outHeight);

        var result = _videoDevice.CreateVideoProcessorInputView(
            bgraInput, _enumerator,
            new VideoProcessorInputViewDescription
            {
                FourCC = 0,
                ViewDimension = VideoProcessorInputViewDimension.Texture2D,
                Texture2D = new Texture2DVideoProcessorInputView { MipSlice = 0, ArraySlice = 0 },
            }, out var inputView);
        result.CheckError();
        using (inputView)
        {
            result = _videoDevice.CreateVideoProcessorOutputView(
                _nv12Output, _enumerator,
                new VideoProcessorOutputViewDescription
                {
                    ViewDimension = VideoProcessorOutputViewDimension.Texture2D,
                    Texture2D = new Texture2DVideoProcessorOutputView { MipSlice = 0 },
                }, out var outputView);
            result.CheckError();
            using (outputView)
            {
                // 流配置：逐行扫描、全画面、禁用自动处理（保证性能与确定性）
                _videoContext.VideoProcessorSetStreamFrameFormat(_processor!, 0, VideoFrameFormat.Progressive);
                _videoContext.VideoProcessorSetStreamAutoProcessingMode(_processor!, 0, false);
                _videoContext.VideoProcessorSetStreamSourceRect(_processor!, 0, true,
                    new Vortice.RawRect(0, 0, inW, inH));
                _videoContext.VideoProcessorSetStreamDestRect(_processor!, 0, true,
                    new Vortice.RawRect(0, 0, outWidth, outHeight));
                _videoContext1.VideoProcessorSetStreamColorSpace1(_processor!, 0,
                    (ColorSpaceType)ColorSpaceRgbFullG22NoneP709);
                _videoContext1.VideoProcessorSetOutputColorSpace1(_processor!,
                    (ColorSpaceType)ColorSpaceYcbcrStudioG22LeftP709);

                var streams = new[]
                {
                    new VideoProcessorStream
                    {
                        Enable = true,
                        InputSurface = inputView,
                    },
                };
                _videoContext.VideoProcessorBlt(_processor!, outputView, 0, 1, streams);
            }
        }
        return _nv12Output!;
    }

    /// <summary>确保输出 NV12 纹理尺寸匹配</summary>
    private void EnsureOutput(int width, int height)
    {
        if (_nv12Output != null && _outWidth == width && _outHeight == height) return;
        _nv12Output?.Dispose();
        _nv12Output = _device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.NV12,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget,   // VideoProcessor 输出需要 RT bind
            CPUAccessFlags = CpuAccessFlags.None,
        });
        _outWidth = width;
        _outHeight = height;
        Logger.Debug("VideoProc", $"NV12 输出纹理: {width}x{height}");
    }

    /// <summary>确保 VideoProcessor 实例匹配输入/输出尺寸（尺寸变化时重建）</summary>
    private void EnsureProcessor(int inW, int inH, int outW, int outH)
    {
        // 输入尺寸同样参与判定：切换共享源（如 1080p 显示器 → 4K 显示器）时
        // 输出尺寸可能不变，但 ContentDescription 的输入尺寸必须更新，否则缩放结果错误。
        if (_processor != null &&
            _procInWidth == inW && _procInHeight == inH &&
            _procOutWidth == outW && _procOutHeight == outH) return;
        _processor?.Dispose();
        _enumerator?.Dispose();
        var desc = new VideoProcessorContentDescription
        {
            InputWidth = (uint)inW,
            InputHeight = (uint)inH,
            OutputWidth = (uint)outW,
            OutputHeight = (uint)outH,
            Usage = (VideoUsage)0, // D3D11_VIDEO_USAGE_UNKNOWN
        };
        var result = _videoDevice.CreateVideoProcessorEnumerator(desc, out var enumerator);
        result.CheckError();
        _enumerator = enumerator;
        result = _videoDevice.CreateVideoProcessor(_enumerator, 0, out var processor);
        result.CheckError();
        _processor = processor;
        (_procInWidth, _procInHeight, _procOutWidth, _procOutHeight) = (inW, inH, outW, outH);
        Logger.Debug("VideoProc", $"VideoProcessor 已重建: {inW}x{inH} → {outW}x{outH}");
    }

    public void Dispose()
    {
        _processor?.Dispose();
        _enumerator?.Dispose();
        _nv12Output?.Dispose();
        _videoContext1.Dispose();
        _videoContext.Dispose();
        _videoDevice.Dispose();
    }
}
