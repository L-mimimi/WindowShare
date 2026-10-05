namespace WindowShare.Core.Audio;

/// <summary>视频帧的上屏决策</summary>
public enum VideoPresentDecision
{
    /// <summary>立即上屏</summary>
    Present,
    /// <summary>还没到点，先等一下（避免画面跑到声音前面）</summary>
    Wait,
}

/// <summary>
/// 音画同步时钟（纯逻辑，可单元测试）。
///
/// 以音频播放时钟为主时钟：声音的连续性人耳极其敏感，卡顿/跳跃立刻能听出来，
/// 而视频晚一两帧上屏几乎察觉不到。因此视频帧按自己的采集时间戳与「当前正在
/// 播放的音频采样点的采集时间戳」比较，早了就等、到点就上屏。
///
/// 音频时钟每 10ms 左右更新一次，两次更新之间用 QPC 外推，保证视频判定的分辨率
/// 不受更新频率限制。没有音频（Host 未共享声音 / 尚未收到第一帧音频）时同步自动
/// 失效，视频退回「解码完立即上屏」的旧行为，不会因为等音频而卡住。
/// </summary>
public sealed class AvSyncClock
{
    /// <summary>视频帧最多比音频时钟提前多久就上屏（小于此差值视为已对齐）</summary>
    public static readonly long MaxEarlyTicks = TimeSpan.TicksPerMillisecond * 40;

    /// <summary>音频时钟超过这个时长没更新就认为失效（音频断流时不要把视频一起卡死）</summary>
    public static readonly long StaleTicks = TimeSpan.TicksPerMillisecond * 400;

    private static readonly long QpcFrequency = System.Diagnostics.Stopwatch.Frequency;

    private readonly object _gate = new();
    private long _audioUtcTicks;   // 最近一次更新对应的音频采集时间戳
    private long _audioSetQpc;     // 更新那一刻的 QPC（外推基准）
    private bool _valid;

    // 冻结检测：音频欠载时 GetPlaybackUtcTicks 返回不再前进的时间戳，而时钟定时器
    // 仍在周期性刷新——仅凭「有没有 Update 调用」的失效判断会被骗过（视频会以
    // 「画面比声音早几分钟」的理由永久等待）。这里额外跟踪「值不前进」的持续时长。
    private long _lastUpdatedUtc;
    private long _frozenSinceQpc;

    /// <summary>音频播放位置推进（渲染线程周期调用；utcTicks 是当前正在播放的采样的采集时间戳）</summary>
    public void Update(long utcTicks)
    {
        lock (_gate)
        {
            var nowQpc = System.Diagnostics.Stopwatch.GetTimestamp();
            if (utcTicks <= _lastUpdatedUtc && _valid)
            {
                // 值没有前进：开始（或继续）冻结计时
                _frozenSinceQpc = _frozenSinceQpc == 0 ? nowQpc : _frozenSinceQpc;
            }
            else
            {
                _frozenSinceQpc = 0;
            }
            _lastUpdatedUtc = utcTicks;
            _audioUtcTicks = utcTicks;
            _audioSetQpc = nowQpc;
            _valid = true;
        }
    }

    /// <summary>清空时钟（断开连接 / 音频断流后重新起播时调用）</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _audioUtcTicks = 0;
            _audioSetQpc = 0;
            _lastUpdatedUtc = 0;
            _frozenSinceQpc = 0;
            _valid = false;
        }
    }

    /// <summary>当前音频时钟对应的采集时间戳（含 QPC 外推）；音频不可用时返回 null</summary>
    public long? GetAudioUtcTicks()
    {
        lock (_gate)
        {
            if (!_valid) return null;
            var nowQpc = System.Diagnostics.Stopwatch.GetTimestamp();
            var elapsedTicks = (nowQpc - _audioSetQpc) * TimeSpan.TicksPerSecond / QpcFrequency;
            if (elapsedTicks > StaleTicks || elapsedTicks < 0)
            {
                _valid = false;
                return null;
            }
            // 冻结检测：值持续不前进超过 StaleTicks → 视为音频时钟失效（与无更新同等对待）
            if (_frozenSinceQpc != 0 &&
                (nowQpc - _frozenSinceQpc) * TimeSpan.TicksPerSecond / QpcFrequency > StaleTicks)
            {
                _valid = false;
                return null;
            }
            return _audioUtcTicks + elapsedTicks;
        }
    }

    /// <summary>同步是否生效（false 时视频应立即上屏）</summary>
    public bool IsActive => GetAudioUtcTicks() != null;

    /// <summary>
    /// 视频帧上屏判定。audioUtcTicks 为 null（无音频）时永远立即上屏。
    /// </summary>
    public static VideoPresentDecision Decide(long frameUtcTicks, long? audioUtcTicks)
    {
        if (audioUtcTicks == null) return VideoPresentDecision.Present;
        var early = frameUtcTicks - audioUtcTicks.Value;
        return early > MaxEarlyTicks ? VideoPresentDecision.Wait : VideoPresentDecision.Present;
    }

    /// <summary>帧与音频时钟的偏差（毫秒，正数=画面早于声音；无音频时返回 double.NaN）</summary>
    public static double OffsetMs(long frameUtcTicks, long? audioUtcTicks) =>
        audioUtcTicks == null
            ? double.NaN
            : (frameUtcTicks - audioUtcTicks.Value) / (double)TimeSpan.TicksPerMillisecond;
}
