using WindowShare.Core.Encoding;

namespace WindowShare.Core.Decoding;

/// <summary>
/// HEVC 解码能力探测。
///
/// 为什么必须在子进程里做：部分平台的 HEVC 解码 MFT（如商店扩展 "HEVCVideoExtension" 的
/// 代理实现）在 ProcessMessage 阶段直接原生访问越界（AccessViolationException），
/// .NET 运行时会视为致命错误终止整个进程，无法捕获。子进程崩溃只体现为非零退出码，
/// 主进程零风险——据此把「解码可用」当作能力协商上报给 Host。
/// </summary>
public static class HevcDecodeProbe
{
    /// <summary>命令行参数：以探针模式运行（由应用入口在开窗前调用并按退出码结束进程）</summary>
    public const string ArgProbe = "--probe-hevc";

    /// <summary>
    /// 运行探针（应只在子进程调用）：成功构造 HEVC 解码器并完成流启动 → true。
    /// 任何异常（含原生崩溃导致的进程非零退出）都视为不可用。
    /// </summary>
    public static bool TryProbe()
    {
        try
        {
            using var decoder = new MfVideoDecoder(VideoCodec.Hevc);
            // 构造已完成 BeginStreaming/StartOfStream（与真实使用一致）；
            // 扩展 MFT 在 ProcessMessage 的原生 AV 发生在构造内部，走不到这里。
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>探针退出码（应用入口据此 Environment.Exit）</summary>
    public const int ExitOk = 0;
    public const int ExitUnsupported = 3;
}
