using Vortice.Direct3D11;

namespace WindowShare.Core.Encoding;

/// <summary>
/// 视频编码器抽象（对齐 Decoding 侧 IVideoDecoder 的模式）。
///
/// 实现约定：
/// • Encoded 事件在编码器内部线程上触发，订阅方自行负责线程切换；Dispose 之后不得再触发。
/// • IsD3DAccelerated=true 时 EncodeNv12Texture 为零拷贝路径（管线喂 GPU NV12 纹理）；
///   false 时管线自动走 staging 回读并改喂 EncodeNv12Bytes——实现方两者只须保证其一有效，
///   但两个方法都应实现（另一个抛 NotSupportedException 亦不触发）。
/// • EncodeNv12Bytes 接收紧排 NV12（Y 平面 + UV 平面，stride=width）。
/// • SetBitrate/ForceKeyFrame 返回 false 表示后端不支持（不抛异常），管线已有降级与告警逻辑。
/// </summary>
public interface IVideoEncoder : IDisposable
{
    /// <summary>编码器显示名（UI 显示，如 NVIDIA H.264 Encoder MFT / h264_nvenc）</summary>
    string EncoderName { get; }

    /// <summary>是否硬件编码</summary>
    bool IsHardware { get; }

    /// <summary>是否 D3D 加速（true=零拷贝纹理路径；false=CPU 字节路径）</summary>
    bool IsD3DAccelerated { get; }

    /// <summary>编码输出（编码器内部线程触发）</summary>
    event Action<EncodedVideoFrame>? Encoded;

    /// <summary>零拷贝路径：GPU NV12 纹理直接进编码器（仅 IsD3DAccelerated=true 有效）</summary>
    void EncodeNv12Texture(ID3D11Texture2D nv12Texture, long timestampUtc);

    /// <summary>CPU 路径：紧排 NV12 字节流进编码器</summary>
    void EncodeNv12Bytes(byte[] nv12, long timestampUtc);

    /// <summary>动态调整目标码率；返回是否实际生效</summary>
    bool SetBitrate(int bitrateBps);

    /// <summary>请求立即出一个 IDR；返回编码器是否支持</summary>
    bool ForceKeyFrame();
}
