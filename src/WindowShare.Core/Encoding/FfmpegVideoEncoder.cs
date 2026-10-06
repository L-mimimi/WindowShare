using System.Runtime.InteropServices;
using FFmpeg.AutoGen;
using WindowShare.Core.Decoding;
using WindowShare.Core.Utils;

namespace WindowShare.Core.Encoding;

/// <summary>
/// libavcodec 厂商硬件编码封装（to1.5.0 Step 2）：hevc_nvenc / h264_nvenc / hevc_amf / h264_amf /
/// hevc_qsv / h264_qsv 候选链，OBS/Sunshine/Moonlight 同款路线。
///
/// 为什么走 libavcodec：收件箱 "Microsoft AVC DX12 Encoder" 拒绝一切 CodecAPI 码率控制（诊断已固化，
/// 见 ROADMAP），实测码率只有目标的 5–10%（「糊」的根因）；厂商编码器经 libavcodec 的私有选项
/// 下发真实 CBR（Step 0 实测 h264_nvenc 码率贴合 100%）。
///
/// 许可与分发：LGPL 兼容——运行时 dlopen 驱动库（nvEncodeAPI64/amfrt64/libmfx64，来自用户已装驱动），
/// 不随应用分发任何厂商 DLL；不引入 libx264/x265（GPL）。
///
/// 喂数模型：CPU 紧排 NV12 → AVFrame → send_frame → receive_packet → Annex-B EncodedVideoFrame。
/// 管线经现有 staging 回读（IsD3DAccelerated=false 自动走 CPU 路径），零拷贝（hw_frames_ctx）列为后续项。
/// 低延迟 + 真码率控制：time_base={1,fps}、bit_rate=rc_max_rate=目标、1 帧 VBV、gop 2×fps、无 B 帧；
/// NVENC tune=ull / preset=p4 / rc=cbr / delay=0（Step 0 实测组合），AMF/QSV 等价项设失败仅记日志。
/// </summary>
public sealed unsafe class FfmpegVideoEncoder : IVideoEncoder
{
    private readonly AVCodecContext* _ctx;
    private readonly AVFrame* _frame;
    private readonly AVPacket* _packet;
    private readonly EncoderSettings _settings;
    private readonly object _gate = new();
    private bool _disposed;
    private long _framesIn;
    private long _framesOut;

    public string EncoderName { get; }
    public bool IsHardware { get; }
    public bool IsD3DAccelerated => false; // CPU NV12 喂入；零拷贝（hw_frames_ctx）留作后续项

    /// <summary>编码输出（在调用 EncodeNv12Bytes 的线程上同步触发）</summary>
    public event Action<EncodedVideoFrame>? Encoded;

    private FfmpegVideoEncoder(EncoderSettings settings, string backendName, bool hardware)
    {
        _settings = settings;
        EncoderName = backendName;
        IsHardware = hardware;
        FfmpegInterop.av_log_set_level(FfmpegInterop.LogError);

        var codec = FfmpegInterop.avcodec_find_encoder_by_name(backendName);
        if (codec == IntPtr.Zero)
            throw new InvalidOperationException($"libavcodec 中没有 {backendName} 编码器");

        _ctx = FfmpegInterop.avcodec_alloc_context3(codec);
        if (_ctx == null)
            throw new InvalidOperationException("avcodec_alloc_context3 失败");

        var fps = Math.Max(1, settings.Fps);
        _ctx->width = settings.Width;
        _ctx->height = settings.Height;
        _ctx->pix_fmt = (AVPixelFormat)FfmpegInterop.PixFmtNv12;
        _ctx->time_base = new AVRational { num = 1, den = fps };
        _ctx->framerate = new AVRational { num = fps, den = 1 };
        _ctx->bit_rate = settings.BitrateBps;
        _ctx->rc_max_rate = settings.BitrateBps;
        // 1 帧 VBV：贴近实时 CBR（Step 0 实测该组合下 h264_nvenc 码率贴合 100%）
        _ctx->rc_buffer_size = Math.Max(1, settings.BitrateBps / fps);
        _ctx->gop_size = settings.GopSize > 0 ? settings.GopSize : 2 * fps;
        _ctx->max_b_frames = 0;
        _ctx->flags |= FfmpegInterop.FlagLowDelay;

        ApplyBackendOptions(backendName);

        var ret = FfmpegInterop.avcodec_open2(_ctx, codec, IntPtr.Zero);
        if (ret < 0)
        {
            var ctx = _ctx;
            FfmpegInterop.avcodec_free_context(&ctx);
            throw new InvalidOperationException($"avcodec_open2 失败: {ret}（驱动侧不可用或选项被拒）");
        }

        _frame = FfmpegInterop.av_frame_alloc();
        _frame->format = FfmpegInterop.PixFmtNv12;
        _frame->width = settings.Width;
        _frame->height = settings.Height;
        ret = FfmpegInterop.av_frame_get_buffer(_frame, 0);
        if (ret < 0)
        {
            Dispose();
            throw new InvalidOperationException($"av_frame_get_buffer 失败: {ret}");
        }

        _packet = FfmpegInterop.av_packet_alloc();
        if (_packet == null)
        {
            Dispose();
            throw new InvalidOperationException("av_packet_alloc 失败");
        }

        Logging.Logger.Info("Encoder",
            $"{backendName} 就绪: {settings.Width}x{settings.Height}@{fps}, " +
            $"目标 {settings.BitrateBps / 1000} kbps（真实 CBR）, GOP {_ctx->gop_size} 帧, 硬件={IsHardware}");
    }

    /// <summary>后端私有选项（低延迟 + CBR）；设置失败仅记日志，open 后以实际出流为准</summary>
    private void ApplyBackendOptions(string backendName)
    {
        if (_ctx->priv_data == null) return;
        var opts = backendName.Contains("nvenc")
            ? new[] { ("tune", "ull"), ("preset", "p4"), ("rc", "cbr"), ("delay", "0") }
            : backendName.Contains("amf")
                ? new[] { ("usage", "lowlatency"), ("rc", "cbr") }
                : backendName.Contains("qsv")
                    ? new[] { ("preset", "veryfast"), ("async_depth", "1") }
                    : Array.Empty<(string, string)>();

        foreach (var (k, v) in opts)
        {
            if (FfmpegInterop.av_opt_set(_ctx->priv_data, k, v, 0) != 0)
                Logging.Logger.Warn("Encoder", $"{backendName} 私有选项 {k}={v} 被拒（继续，按后端默认运行）");
        }
    }

    /// <summary>该编码格式的厂商编码器候选链（顺序即优先级：NVENC → AMF → QSV）</summary>
    public static IReadOnlyList<string> Candidates(VideoCodec codec) => codec switch
    {
        VideoCodec.Hevc => new[] { "hevc_nvenc", "hevc_amf", "hevc_qsv" },
        _ => new[] { "h264_nvenc", "h264_amf", "h264_qsv" },
    };

    /// <summary>
    /// 按候选链创建编码器：逐个 avcodec_open2 实测驱动可用性（会话开始时调用，几百 ms 可接受）。
    /// 全部失败返回 null（工厂回退 MF 链）。
    /// </summary>
    public static FfmpegVideoEncoder? TryCreate(EncoderSettings settings)
    {
        foreach (var name in Candidates(settings.Codec))
        {
            try
            {
                // 名字含 nvenc/qsv/amf 的都是厂商硬件封装
                return new FfmpegVideoEncoder(settings, name, hardware: true);
            }
            catch (Exception ex)
            {
                Logging.Logger.Info("Encoder", $"{name} 不可用: {ex.Message}（尝试下一候选）");
            }
        }
        return null;
    }

    /// <summary>厂商编码器是否至少有一个候选可打开（工厂探测用；副作用：开完立即释放）</summary>
    public static bool ProbeAvailable(EncoderSettings settings)
    {
        var enc = TryCreate(settings);
        if (enc == null) return false;
        enc.Dispose();
        return true;
    }

    public void EncodeNv12Texture(Vortice.Direct3D11.ID3D11Texture2D nv12Texture, long timestampUtc)
        => throw new NotSupportedException(
            "FFmpeg 编码器走 CPU NV12 路径（管线在 IsD3DAccelerated=false 时自动使用 EncodeNv12Bytes）");

    public void EncodeNv12Bytes(byte[] nv12, long timestampUtc)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_gate)
        {
            Interlocked.Increment(ref _framesIn);
            var w = _settings.Width;
            var h = _settings.Height;
            var expected = w * h * 3 / 2;
            if (nv12.Length < expected)
            {
                Logging.Logger.Warn("Encoder", $"NV12 输入不足（{nv12.Length} < {expected}），丢弃该帧");
                return;
            }

            unsafe
            {
                fixed (byte* pSrc = nv12)
                {
                    // AVFrame 缓冲由 av_frame_get_buffer 分配一次；逐行拷入（linesize 可能含对齐填充）
                    byte** dst = (byte**)&_frame->data;
                    int* dstStride = (int*)&_frame->linesize;

                    var yRow = Math.Min(w, dstStride[0]);
                    for (var r = 0; r < h; r++)
                        Buffer.MemoryCopy(pSrc + (long)r * w, dst[0] + (long)r * dstStride[0], yRow, yRow);

                    var uvRows = h / 2;
                    var uvRow = Math.Min(w, dstStride[1]);
                    var uvSrc = pSrc + (long)w * h;
                    for (var r = 0; r < uvRows; r++)
                        Buffer.MemoryCopy(uvSrc + (long)r * w, dst[1] + (long)r * dstStride[1], uvRow, uvRow);
                }
            }

            _frame->pts = Interlocked.Read(ref _framesIn) - 1;
            var ret = FfmpegInterop.avcodec_send_frame(_ctx, _frame);
            if (ret < 0 && ret != FfmpegInterop.ErrorAgain)
            {
                Logging.Logger.Warn("Encoder", $"avcodec_send_frame 失败: {ret}（丢弃该帧）");
                return;
            }

            Drain(timestampUtc);
        }
    }

    /// <summary>抽干编码输出；每个 packet 复制成托管 Annex-B 帧后触发 Encoded</summary>
    private void Drain(long timestampUtc)
    {
        while (true)
        {
            var ret = FfmpegInterop.avcodec_receive_packet(_ctx, _packet);
            if (ret == FfmpegInterop.ErrorAgain || ret == FfmpegInterop.ErrorEof)
                return;
            if (ret < 0)
            {
                Logging.Logger.Warn("Encoder", $"avcodec_receive_packet 失败: {ret}");
                return;
            }

            try
            {
                if (_packet->size > 0 && _packet->data != null)
                {
                    var data = new byte[_packet->size];
                    Marshal.Copy((IntPtr)_packet->data, data, 0, _packet->size);
                    Interlocked.Increment(ref _framesOut);
                    Encoded?.Invoke(new EncodedVideoFrame
                    {
                        Data = data,
                        Keyframe = AnnexB.IsKeyframe(data, _settings.Codec),
                        TimestampUtc = timestampUtc,
                        Width = _settings.Width,
                        Height = _settings.Height,
                    });
                }
            }
            finally
            {
                FfmpegInterop.av_packet_unref(_packet);
            }
        }
    }

    /// <summary>v1 记日志不生效（NVENC 动态重配不在 v1 范围，to1.5.0.md）；Host 已有 GOP 补发与码率梯子兜底</summary>
    public bool SetBitrate(int bitrateBps)
    {
        Logging.Logger.Info("Encoder", $"{EncoderName} 动态码率 v1 暂不生效（请求 {bitrateBps / 1000} kbps，保持初始值）");
        return false;
    }

    /// <summary>v1 不支持（同 to1.5.0.md）；管线超时补发 GOP 的兜底不受影响</summary>
    public bool ForceKeyFrame() => false;

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;

            // 冲刷尾部（正常停止时有 0–2 帧滞留）
            try
            {
                if (_ctx != null)
                {
                    FfmpegInterop.avcodec_send_frame(_ctx, null);
                    Drain(DateTime.UtcNow.Ticks);
                }
            }
            catch { /* 释放路径尽力而为 */ }

            var frame = _frame;
            if (frame != null) FfmpegInterop.av_frame_free(&frame);
            var packet = _packet;
            if (packet != null) FfmpegInterop.av_packet_free(&packet);
            var ctx = _ctx;
            if (ctx != null) FfmpegInterop.avcodec_free_context(&ctx);
        }
    }
}
