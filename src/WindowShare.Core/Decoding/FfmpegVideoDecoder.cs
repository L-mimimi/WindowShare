using System.Runtime.InteropServices;
using FFmpeg.AutoGen;
using WindowShare.Core.Encoding;
using WindowShare.Core.Utils;

namespace WindowShare.Core.Decoding;

/// <summary>
/// libavcodec 软解兜底（HEVC）：MF 解码器探针失败/被拒时的最后防线。
///
/// 为什么可靠：纯软件实现、不碰任何系统解码 MFT/DXVA 路径——
/// 扩展 MFT 的原生崩溃（本机实测 BeginStreaming 必崩）与驱动状态都影响不到它。
///
/// 喂数模型：现有管线「一个完整 Annex-B 访问单元 = 一帧」与
/// avcodec_send_packet / avcodec_receive_frame 天然匹配，无需容器与 parser。
/// 低延迟配置（借 Moonlight 经验）：thread_count=1（避免帧线程缓冲延迟）+ LOW_DELAY 标志。
/// 输出：YUV420P 三平面 → <see cref="Yuv420pToBgra"/> → 复用现有 BGRA 渲染路径。
/// </summary>
public sealed unsafe class FfmpegVideoDecoder : IVideoDecoder
{
    private readonly AVCodecContext* _ctx;
    private readonly AVPacket* _packet;
    private readonly AVFrame* _frame;
    private readonly object _gate = new();
    private bool _disposed;
    private long _decodedFrames;
    private long _inputFrames;

    public VideoCodec Codec { get; }
    public string BackendName => "FFmpeg";

    /// <summary>解码输出（在调用 Decode 的线程上同步触发）</summary>
    public event Action<DecodedVideoFrame>? Decoded;

    public int OutputWidth { get; private set; }
    public int OutputHeight { get; private set; }
    public long DecodedFrames => Interlocked.Read(ref _decodedFrames);
    public long InputFrames => Interlocked.Read(ref _inputFrames);

    private FfmpegVideoDecoder(VideoCodec codec)
    {
        Codec = codec;
        FfmpegInterop.av_log_set_level(FfmpegInterop.LogError);

        var avcodec = FfmpegInterop.avcodec_find_decoder(CodecId(codec));
        if (avcodec == IntPtr.Zero)
            throw new InvalidOperationException($"libavcodec 中没有 {codec.DisplayName()} 解码器");

        _ctx = FfmpegInterop.avcodec_alloc_context3(avcodec);
        if (_ctx == null)
            throw new InvalidOperationException("avcodec_alloc_context3 失败");

        // 低延迟：单线程（多线程解码器会缓冲 thread_count-1 帧）+ LOW_DELAY。
        _ctx->thread_count = 1;
        _ctx->flags |= FfmpegInterop.FlagLowDelay;

        var ret = FfmpegInterop.avcodec_open2(_ctx, avcodec, IntPtr.Zero);
        if (ret < 0)
        {
            var ctx = _ctx;
            FfmpegInterop.avcodec_free_context(&ctx);
            throw new InvalidOperationException($"avcodec_open2 失败: {ret}");
        }

        _packet = FfmpegInterop.av_packet_alloc();
        _frame = FfmpegInterop.av_frame_alloc();
        if (_packet == null || _frame == null)
            throw new InvalidOperationException("av_packet_alloc/av_frame_alloc 失败");

        Logging.Logger.Info("Decoder", $"{codec.DisplayName()} FFmpeg 软解就绪（avcodec n7.1，进程内纯软件路径）");
    }

    private static AVCodecID CodecId(VideoCodec codec) => codec switch
    {
        VideoCodec.Hevc => FfmpegInterop.CodecIdHevc,
        _ => (AVCodecID)27, // AV_CODEC_ID_H264
    };

    /// <summary>
    /// 原生库是否可用（毫秒级进程内探测：加载 DLL + 版本对齐 + HEVC 解码器存在）。
    /// 返回失败原因；null = 可用。
    /// </summary>
    public static string? UnavailableReason() => FfmpegInterop.ProbeAvailability();

    /// <summary>创建软解器；任何失败（DLL 缺失/版本不符）返回 null 并记日志</summary>
    public static FfmpegVideoDecoder? TryCreate(VideoCodec codec)
    {
        try
        {
            return new FfmpegVideoDecoder(codec);
        }
        catch (Exception ex)
        {
            Logging.Logger.Warn("Decoder", $"FFmpeg 软解兜底不可用（{codec.DisplayName()}）: " + ex.Message);
            return null;
        }
    }

    /// <summary>创建 HEVC 软解器（<see cref="TryCreate"/> 的便捷形式）</summary>
    public static FfmpegVideoDecoder? TryCreateHevc() => TryCreate(VideoCodec.Hevc);

    public void Decode(byte[] annexB, long timestampUtc)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_gate)
        {
            Interlocked.Increment(ref _inputFrames);
            fixed (byte* pData = annexB)
            {
                var ret = SendPacket(pData, annexB.Length, timestampUtc);
                if (ret == FfmpegInterop.ErrorAgain)
                {
                    // 输出未抽干（理论上我们每次都抽干，防御性处理）：抽干后重投一次
                    Drain(timestampUtc);
                    ret = SendPacket(pData, annexB.Length, timestampUtc);
                }
                if (ret < 0 && ret != FfmpegInterop.ErrorAgain)
                    Logging.Logger.Warn("Decoder", $"avcodec_send_packet 失败: {ret}（帧 {annexB.Length}B，继续后续帧）");
            }

            Drain(timestampUtc);
        }
    }

    /// <summary>投喂一帧；data 指针仅在调用内有效（send_packet 不取得所有权，立即抽干即安全）</summary>
    private int SendPacket(byte* pData, int size, long timestampUtc)
    {
        _packet->data = pData;
        _packet->size = size;
        _packet->pts = timestampUtc;
        var ret = FfmpegInterop.avcodec_send_packet(_ctx, _packet);
        FfmpegInterop.av_packet_unref(_packet); // 清零 packet（无 buf，仅复位字段）
        return ret;
    }

    private void Drain(long timestampUtc)
    {
        while (true)
        {
            var ret = FfmpegInterop.avcodec_receive_frame(_ctx, _frame);
            if (ret == FfmpegInterop.ErrorAgain || ret == FfmpegInterop.ErrorEof)
                return;
            if (ret < 0)
            {
                Logging.Logger.Warn("Decoder", $"avcodec_receive_frame 失败: {ret}");
                return;
            }

            try
            {
                Emit(timestampUtc);
            }
            finally
            {
                FfmpegInterop.av_frame_unref(_frame);
            }
        }
    }

    private void Emit(long timestampUtc)
    {
        var frame = _frame;
        var w = frame->width;
        var h = frame->height;
        if (w <= 0 || h <= 0)
            return;

        if (OutputWidth != w || OutputHeight != h)
        {
            OutputWidth = w;
            OutputHeight = h;
            Logging.Logger.Info("Decoder", $"FFmpeg 解码输出分辨率: {w}x{h}");
        }

        if (frame->format != FfmpegInterop.PixFmtYuv420p)
        {
            // 我们只编码 8-bit Main Profile；10-bit（P010/P010LE）等不应出现，防御性丢帧
            Logging.Logger.Warn("Decoder", $"FFmpeg 输出了未处理的像素格式 {frame->format}，丢弃该帧");
            return;
        }

        var yPlane = AsSpan(frame->data[0], frame->linesize[0], h);
        var uPlane = AsSpan(frame->data[1], frame->linesize[1], h / 2);
        var vPlane = AsSpan(frame->data[2], frame->linesize[2], h / 2);
        if (yPlane.IsEmpty || uPlane.IsEmpty || vPlane.IsEmpty)
        {
            Logging.Logger.Warn("Decoder", "FFmpeg 输出平面指针/行距异常（负行距？），丢弃该帧");
            return;
        }

        var bgra = new byte[w * h * 4];
        Yuv420pToBgra.Convert(
            yPlane, frame->linesize[0],
            uPlane, frame->linesize[1],
            vPlane,
            w, h, bgra);

        Interlocked.Increment(ref _decodedFrames);
        Decoded?.Invoke(new DecodedVideoFrame
        {
            Bgra = bgra,
            Width = w,
            Height = h,
            TimestampUtc = timestampUtc,
        });
    }

    /// <summary>把 AVFrame 平面缓冲映射为托管只读视图（按行距考虑实际所需长度）</summary>
    private static ReadOnlySpan<byte> AsSpan(byte* ptr, int stride, int rows)
    {
        if (ptr == null || stride <= 0 || rows <= 0)
            return ReadOnlySpan<byte>.Empty;
        return new ReadOnlySpan<byte>(ptr, stride * rows);
    }

    public int Flush()
    {
        lock (_gate)
        {
            if (_disposed) return 0;
            var before = Interlocked.Read(ref _decodedFrames);
            // 空 packet 进入排空模式 → 抽干尾部 → flush_buffers 复位（之后可继续 Decode）
            var ret = FfmpegInterop.avcodec_send_packet(_ctx, null);
            if (ret == 0 || ret == FfmpegInterop.ErrorEof)
                Drain(DateTime.UtcNow.Ticks);
            FfmpegInterop.avcodec_flush_buffers(_ctx);
            return (int)(Interlocked.Read(ref _decodedFrames) - before);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;

            var frame = _frame;
            FfmpegInterop.av_frame_free(&frame);
            var packet = _packet;
            FfmpegInterop.av_packet_free(&packet);
            var ctx = _ctx;
            FfmpegInterop.avcodec_free_context(&ctx);
        }
    }
}
