namespace WindowShare.Core.Encoding;

/// <summary>
/// GOP 缓存：保存「自上一个 IDR 关键帧以来的全部编码帧」。
///
/// 为什么需要它：部分编码器（本机实测 Microsoft AVC DX12 Encoder 与软件 H264 Encoder MFT）
/// 对 CODECAPI_AVEncVideoForceKeyFrame 一律返回 E_NOTIMPL，「请求关键帧」根本不被认账，
/// IDR 只按编码器内部 GOP 周期出现。静态桌面下实际帧率很低（WGC 约 8fps），
/// 一个 GOP 可能横跨数秒，新接入的观看者就得黑屏干等到下一个 IDR。
///
/// 有了缓存，观看者接入后立刻把整段 GOP 补发过去（自带 SPS/PPS 与完整参考链），
/// 画面当场可解；不需要动编码器，也不牺牲压缩率。
///
/// 线程模型：本类不加锁，由调用方串行化（见 LanShareServer._gopGate / WebRtcSinkAdapter._gate）。
/// 「追加帧 + 分发给老观看者」与「标记就绪 + 补发给新观看者」必须在同一把锁内完成，
/// 否则补发的最后一帧与随后直发的第一帧可能乱序或重复，解码器会花屏。
/// </summary>
public sealed class GopCache
{
    /// <summary>默认字节上限：4K 高码率下 2 秒 GOP 也远小于此值</summary>
    public const int DefaultMaxBytes = 24 * 1024 * 1024;

    /// <summary>默认帧数上限：144fps × 2 秒关键帧间隔仍有富余</summary>
    public const int DefaultMaxFrames = 600;

    private readonly List<EncodedVideoFrame> _frames = new();

    // 缓存被上限截断后，剩下的半截 GOP 没有参考帧，补发也解不出来：
    // 置位后不再收帧，等到下一个 IDR 重新积累。
    private bool _awaitKeyframe;

    public GopCache(int maxBytes = DefaultMaxBytes, int maxFrames = DefaultMaxFrames)
    {
        MaxBytes = maxBytes > 0 ? maxBytes : DefaultMaxBytes;
        MaxFrames = maxFrames > 0 ? maxFrames : DefaultMaxFrames;
    }

    /// <summary>字节上限</summary>
    public int MaxBytes { get; }

    /// <summary>帧数上限</summary>
    public int MaxFrames { get; }

    /// <summary>当前缓存帧数</summary>
    public int Count => _frames.Count;

    /// <summary>当前缓存总字节数</summary>
    public long TotalBytes { get; private set; }

    /// <summary>缓存是否以 IDR 开头（只有这种情况补发出来的码流才可解）</summary>
    public bool StartsWithKeyframe => _frames.Count > 0 && _frames[0].Keyframe;

    /// <summary>追加一帧；遇到 IDR 自动开启新一轮缓存</summary>
    public void Add(EncodedVideoFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);

        // 分辨率切换（动态降档 / 源尺寸变化）后，旧尺寸的帧对新尺寸没有参考价值
        if (_frames.Count > 0 &&
            (_frames[0].Width != frame.Width || _frames[0].Height != frame.Height))
        {
            Clear();
            _awaitKeyframe = true;
        }

        if (frame.Keyframe)
        {
            Clear();
        }
        else if (_awaitKeyframe)
        {
            return;
        }

        _frames.Add(frame);
        TotalBytes += frame.PayloadSize;

        if (TotalBytes > MaxBytes || _frames.Count > MaxFrames)
        {
            Clear();
            _awaitKeyframe = true;
        }
    }

    /// <summary>清空缓存（共享停止 / 重新开始积累时调用）</summary>
    public void Clear()
    {
        _frames.Clear();
        TotalBytes = 0;
        _awaitKeyframe = false;
    }

    /// <summary>
    /// 取补发序列：缓存以 IDR 开头时返回全部帧的快照，否则返回空数组。
    /// 返回值是副本，调用方可在锁外安全遍历。
    /// </summary>
    public EncodedVideoFrame[] GetReplayFrames() =>
        StartsWithKeyframe ? _frames.ToArray() : Array.Empty<EncodedVideoFrame>();
}
