using WindowShare.Core.Logging;

namespace WindowShare.Core.Audio;

/// <summary>
/// Viewer 端音频管线：ADTS 帧 → AAC 解码 → 抖动缓冲 → WASAPI 渲染，
/// 同时对外提供音画同步用的主时钟（<see cref="AvSyncClock"/>）。
///
/// 音频时钟每 10ms 采样一次播放位置并喂给 <see cref="AvSyncClock"/>，视频侧按
/// 帧的采集时间戳与该时钟比对决定「立即上屏」还是「再等一会儿」。
/// </summary>
public sealed class AudioPlaybackPipeline : IDisposable
{
    /// <summary>音频时钟采样间隔：比视频帧间隔小一个量级，外推误差可忽略</summary>
    private const int ClockUpdateIntervalMs = 10;

    private readonly MfAacDecoder _decoder;
    private readonly AudioRenderer _renderer;
    private Timer? _clockTimer;
    private long _droppedFrames;
    private long _receivedFrames;
    private long _receivedBytes;

    /// <summary>音画同步主时钟（视频侧读取）</summary>
    public AvSyncClock Clock { get; } = new();

    public string DecoderName => _decoder.DecoderName;
    public bool IsPlaying => _renderer.IsPlaying;
    public int BufferedMs => _renderer.BufferedMs;
    public long Underruns => _renderer.Underruns;
    public float Level => _renderer.Level;
    public long ReceivedFrames => Interlocked.Read(ref _receivedFrames);
    public long ReceivedBytes => Interlocked.Read(ref _receivedBytes);
    public long DroppedFrames => Interlocked.Read(ref _droppedFrames);

    public AudioPlaybackPipeline(int sampleRate = AudioStreamInfo.SampleRate)
    {
        _decoder = new MfAacDecoder(sampleRate);
        _renderer = new AudioRenderer(sampleRate);
        _decoder.Decoded += chunk => _renderer.Enqueue(chunk);
    }

    public void Start()
    {
        _renderer.Start();
        _clockTimer = new Timer(_ => UpdateClock(), null,
            ClockUpdateIntervalMs, ClockUpdateIntervalMs);
        Logger.Info("Audio", $"音频播放管线已启动: {_decoder.DecoderName}");
    }

    /// <summary>喂一个网络收到的 ADTS 帧（网络线程调用）</summary>
    public void Feed(EncodedAudioFrame frame)
    {
        Interlocked.Increment(ref _receivedFrames);
        Interlocked.Add(ref _receivedBytes, frame.PayloadSize);
        try
        {
            _decoder.Decode(frame.Data, frame.TimestampUtc);
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _droppedFrames);
            if (Interlocked.Read(ref _droppedFrames) % 100 == 1)
                Logger.Warn("Audio", $"音频解码异常（已累计 {DroppedFrames} 帧）: {ex.Message}");
        }
    }

    private void UpdateClock()
    {
        try
        {
            var utc = _renderer.GetPlaybackUtcTicks();
            if (utc == null) return;
            Clock.Update(utc.Value);
        }
        catch { /* 时钟采样失败不影响播放 */ }
    }

    public void Stop()
    {
        _clockTimer?.Dispose();
        _clockTimer = null;
        Clock.Reset();
        _renderer.Stop();
    }

    public void Dispose()
    {
        Stop();
        try { _renderer.Dispose(); } catch { }
        try { _decoder.Dispose(); } catch { }
    }
}
