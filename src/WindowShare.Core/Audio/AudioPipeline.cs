using WindowShare.Core.Logging;

namespace WindowShare.Core.Audio;

/// <summary>
/// Host 端音频管线：系统声音采集（WASAPI loopback）→ AAC-LC 编码 → ADTS 帧输出。
///
/// 与视频管线完全独立：音频出问题时（没有播放设备、没有 AAC 编码器）只关自己，
/// 视频共享照常进行。<see cref="ShareSession"/> 会捕获构造/启动异常并降级为纯视频。
/// </summary>
public sealed class AudioPipeline : IDisposable
{
    private readonly LoopbackAudioCapture _capture;
    private readonly MfAacEncoder _encoder;
    private long _droppedFrames;

    /// <summary>编码后的 ADTS 帧（采集线程上触发，回调不要阻塞）</summary>
    public event Action<EncodedAudioFrame>? Encoded;

    /// <summary>AAC 编码器名</summary>
    public string EncoderName => _encoder.EncoderName;

    /// <summary>实际码率（编码器支持档位）</summary>
    public int BitrateBps => _encoder.AppliedBitrateBps;

    public int SampleRate => _encoder.SampleRate;
    public int Channels => _encoder.Channels;

    public bool IsRunning => _capture.IsRunning;

    /// <summary>已编码帧数 / 字节数</summary>
    public long EncodedFrames => _encoder.EncodedFrames;
    public long EncodedBytes => _encoder.EncodedBytes;

    /// <summary>因没有接收端而丢弃的帧数（还没人接入时不必编码完再扔，但编码本身很便宜，这里只计数）</summary>
    public long DroppedFrames => Interlocked.Read(ref _droppedFrames);

    /// <summary>最近一块的电平（0..1，Host UI 显示「正在传声音」）</summary>
    public float Level => _capture.PeakLevel;

    /// <summary>补出来的静音块数（系统当前没在放声音）</summary>
    public long SilenceChunks => _capture.SilenceChunks;

    public AudioPipeline()
    {
        // 先建编码器：没有 AAC 编码器就没必要再去打开音频设备
        _encoder = new MfAacEncoder();
        _capture = new LoopbackAudioCapture();
        _encoder.Encoded += OnEncoded;
        _capture.ChunkArrived += OnCaptured;
    }

    public void Start()
    {
        _capture.Start();
        Logger.Info("Audio",
            $"系统声音共享已启动: {EncoderName}, {SampleRate}Hz/{Channels}ch, {BitrateBps / 1000}kbps");
    }

    public void Stop()
    {
        _capture.Stop();
        // 冲刷编码器：AAC 有约 2 帧 priming，不冲刷会丢掉结尾（观看端听到最后一句被截断）
        try
        {
            var flushed = _encoder.Drain();
            if (flushed > 0) Logger.Info("Audio", $"停止前冲刷出 {flushed} 帧音频");
        }
        catch (Exception ex)
        {
            Logger.Warn("Audio", "冲刷音频编码器失败: " + ex.Message);
        }
    }

    private void OnCaptured(PcmChunk chunk)
    {
        try
        {
            _encoder.Encode(chunk);
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _droppedFrames);
            if (Interlocked.Read(ref _droppedFrames) % 100 == 1)
                Logger.Warn("Audio", $"音频编码异常（已累计 {DroppedFrames} 块）: {ex.Message}");
        }
    }

    private void OnEncoded(EncodedAudioFrame frame)
    {
        var handler = Encoded;
        if (handler == null)
        {
            Interlocked.Increment(ref _droppedFrames);
            return;
        }
        try { handler(frame); }
        catch (Exception ex) { Logger.Warn("Audio", "音频帧回调异常: " + ex.Message); }
    }

    public void Dispose()
    {
        _capture.ChunkArrived -= OnCaptured;
        _encoder.Encoded -= OnEncoded;
        try { _capture.Dispose(); } catch { }
        try { _encoder.Dispose(); } catch { }
    }
}
