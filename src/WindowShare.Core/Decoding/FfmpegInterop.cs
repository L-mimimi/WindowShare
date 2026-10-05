using System.Runtime.InteropServices;
using FFmpeg.AutoGen;

namespace WindowShare.Core.Decoding;

/// <summary>
/// libavcodec/libavutil 的最小 P/Invoke（解码方向，约 13 个函数）。
///
/// 为什么不直接用 FFmpeg.AutoGen 的 ffmpeg.avcodec_* 函数：AutoGen（5.x 起）的函数指针在
/// 静态初始化时按「裸名」动态加载（LoadLibrary("avcodec")），而社区发行版的 DLL 都带版本
/// 后缀（avcodec-61.dll），裸名永远找不到 → 函数调用统一抛 NotSupportedException。
/// 这里改用带后缀的显式导入，与打包分发的 Sdcb 构建DLL（avcodec-61/avutil-59，FFmpeg n7.1）
/// 逐名对齐；结构体类型复用 AutoGen 的定义（纯托管数据，无加载副作用）。
///
/// 因此升级 FFmpeg 大版本时必须同步核对：① DLL 文件名后缀；② AutoGen 包版本（结构体布局）。
/// </summary>
internal static unsafe class FfmpegInterop
{
    /// <summary>FFmpeg n7.1 的库名（与 csproj 拷出的 DLL 文件名一致）</summary>
    private const string LibAvcodec = "avcodec-61";
    private const string LibAvutil = "avutil-59";

    static FfmpegInterop()
    {
        // 预加载：apphost 直启时 P/Invoke 默认搜索路径含 exe 目录，但 `dotnet run`/单文件宿主
        // 的搜索起点是 dotnet.exe 所在目录。按完整路径从托管程序集目录先加载，
        // 之后同名 DllImport 会直接命中内核已载模块（依赖链按序：avutil → swresample → avcodec）。
        TryPreload(LibAvutil);
        TryPreload("swresample-5");
        TryPreload(LibAvcodec);
    }

    private static void TryPreload(string name)
    {
        try
        {
            var dir = Path.GetDirectoryName(typeof(FfmpegInterop).Assembly.Location);
            if (string.IsNullOrEmpty(dir))
                return; // 单文件发布：exe 目录在默认搜索路径上，交给 DllImport
            NativeLibrary.TryLoad(Path.Combine(dir, name + ".dll"), out _);
        }
        catch
        {
            // 加载失败不在此处报错，交给 DllImportNotFoundException 统一进入探针结论
        }
    }

    // ---- 常量（FFmpeg n7.1）----
    /// <summary>AV_CODEC_ID_HEVC</summary>
    internal const AVCodecID CodecIdHevc = (AVCodecID)173;
    /// <summary>AV_PIX_FMT_YUV420P（8-bit 平面 YUV）</summary>
    internal const int PixFmtYuv420p = 0;
    /// <summary>AV_CODEC_FLAG_LOW_DELAY（1 &lt;&lt; 19）</summary>
    internal const int FlagLowDelay = 524288;
    /// <summary>AV_LOG_ERROR</summary>
    internal const int LogError = 16;
    /// <summary>AVERROR(EAGAIN)：需要更多输入/输出未就绪</summary>
    internal const int ErrorAgain = -11;
    /// <summary>AVERROR_EOF</summary>
    internal const int ErrorEof = -541478725;

    // ---- avcodec ----
    [DllImport(LibAvcodec, CallingConvention = CallingConvention.Cdecl)]
    internal static extern uint avcodec_version();

    [DllImport(LibAvcodec, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr avcodec_find_decoder(AVCodecID id);

    [DllImport(LibAvcodec, CallingConvention = CallingConvention.Cdecl)]
    internal static extern AVCodecContext* avcodec_alloc_context3(IntPtr codec);

    [DllImport(LibAvcodec, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int avcodec_open2(AVCodecContext* ctx, IntPtr codec, IntPtr options);

    [DllImport(LibAvcodec, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void avcodec_free_context(AVCodecContext** ctx);

    [DllImport(LibAvcodec, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void avcodec_flush_buffers(AVCodecContext* ctx);

    [DllImport(LibAvcodec, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int avcodec_send_packet(AVCodecContext* ctx, AVPacket* pkt);

    [DllImport(LibAvcodec, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int avcodec_receive_frame(AVCodecContext* ctx, AVFrame* frame);

    // packet.h 在 libavcodec 实现（不是 libavutil）
    [DllImport(LibAvcodec, CallingConvention = CallingConvention.Cdecl)]
    internal static extern AVPacket* av_packet_alloc();

    [DllImport(LibAvcodec, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void av_packet_free(AVPacket** pkt);

    [DllImport(LibAvcodec, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void av_packet_unref(AVPacket* pkt);

    // ---- avutil（frame.h / log.h）----
    [DllImport(LibAvutil, CallingConvention = CallingConvention.Cdecl)]
    internal static extern AVFrame* av_frame_alloc();

    [DllImport(LibAvutil, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void av_frame_free(AVFrame** frame);

    [DllImport(LibAvutil, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void av_frame_unref(AVFrame* frame);

    [DllImport(LibAvutil, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void av_log_set_level(int level);

    /// <summary>
    /// 原生库是否可用（探针：毫秒级、纯软件路径、无崩溃风险，进程内直接实测）。
    /// 找不到 DLL、版本异常或 HEVC 解码器缺失都算不可用，返回失败原因。
    /// </summary>
    internal static string? ProbeAvailability()
    {
        try
        {
            av_log_set_level(LogError);
            var ver = avcodec_version();
            var major = (int)(ver >> 16);
            if (major != 61)
                return $"avcodec 大版本不匹配（{major}，需 61）——与打包的 avcodec-61.dll 不一致";
            if (avcodec_find_decoder(CodecIdHevc) == IntPtr.Zero)
                return "avcodec 中未编译 HEVC 解码器";
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }
}
