namespace WindowShare.Core.Decoding;

using WindowShare.Core.Encoding;

/// <summary>
/// 视频解码器契约：输入 Annex-B 码流（一个完整访问单元一次调用），输出 BGRA 帧回调。
///
/// 实现者：
///   - <see cref="MfVideoDecoder"/>：Media Foundation 解码 MFT（收件箱 H.264 / 厂商与商店扩展 HEVC）；
///   - <see cref="FfmpegVideoDecoder"/>：libavcodec 软解兜底（MF 探针失败/被拒时的最后防线）。
/// 由 <see cref="VideoDecoderFactory"/> 按编码格式与平台能力选择，Viewer 只依赖本接口。
/// </summary>
public interface IVideoDecoder : IDisposable
{
    /// <summary>本解码器面向的编码格式</summary>
    VideoCodec Codec { get; }

    /// <summary>解码实现标识（状态栏显示用："MF" / "FFmpeg"）</summary>
    string BackendName { get; }

    /// <summary>解码输出（在调用 Decode 的线程上同步触发）</summary>
    event Action<DecodedVideoFrame>? Decoded;

    /// <summary>解码输出宽高（首个输出后可知）</summary>
    int OutputWidth { get; }

    /// <summary>解码输出宽高（首个输出后可知）</summary>
    int OutputHeight { get; }

    /// <summary>累计解出的 BGRA 帧数（诊断用）</summary>
    long DecodedFrames { get; }

    /// <summary>累计投喂的码流帧数（诊断用）</summary>
    long InputFrames { get; }

    /// <summary>送入一帧 Annex-B 码流并抽干所有可用输出</summary>
    void Decode(byte[] annexB, long timestampUtc);

    /// <summary>
    /// 通知解码器码流结束并抽干内部滞留的输出帧，返回本次新增的解码帧数
    /// （无尾部缓冲语义的实现返回 0）。之后仍可继续 <see cref="Decode"/>。
    /// </summary>
    int Flush();
}
