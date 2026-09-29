using System.Collections.Concurrent;

namespace WindowShare.Core.Stats;

/// <summary>
/// 实时统计采集器（线程安全）：
///   - 码率：滑动 1 秒窗口内收/发字节数
///   - 帧率：滑动 1 秒窗口内帧数
///   - 延迟：EWMA 平滑的单向近似延迟（capture→display 年龄，扣去 RTT/2）
/// Viewer 与 Host 各自持有一个实例；网络层通过 OnSecondTick 汇报给 UI。
/// </summary>
public sealed class StatsCollector : IDisposable
{
    private readonly ConcurrentQueue<(long Ticks, int Bytes)> _bytesWindow = new();
    private readonly ConcurrentQueue<long> _frameWindow = new();
    private readonly object _latencyGate = new();
    private double _latencyEwmaMs = double.NaN; // 无样本时为 NaN

    private long _totalBytes;
    private long _totalFrames;

    /// <summary>当前码率（字节/秒，基于最近 1 秒窗口）</summary>
    public double BitrateBps { get; private set; }

    /// <summary>当前帧率（帧/秒，基于最近 1 秒窗口）</summary>
    public double Fps { get; private set; }

    /// <summary>平滑后的端到端近似延迟（毫秒；无样本时为 NaN）</summary>
    public double LatencyMs
    {
        get { lock (_latencyGate) return _latencyEwmaMs; }
    }

    /// <summary>累计收/发字节数</summary>
    public long TotalBytes => Interlocked.Read(ref _totalBytes);

    /// <summary>累计帧数</summary>
    public long TotalFrames => Interlocked.Read(ref _totalFrames);

    /// <summary>记录一帧（携带字节数）</summary>
    public void OnFrame(int bytes)
    {
        var now = DateTime.UtcNow.Ticks;
        Interlocked.Add(ref _totalBytes, bytes);
        Interlocked.Increment(ref _totalFrames);
        _bytesWindow.Enqueue((now, bytes));
        _frameWindow.Enqueue(now);
        TrimWindow(now);
    }

    /// <summary>记录一个延迟样本（毫秒）</summary>
    public void OnLatencySample(double ms)
    {
        lock (_latencyGate)
        {
            // EWMA：α=0.2，兼顾平滑与响应
            _latencyEwmaMs = double.IsNaN(_latencyEwmaMs) ? ms : _latencyEwmaMs * 0.8 + ms * 0.2;
        }
    }

    /// <summary>合并另一个采集器（Host 汇总多个 Viewer 时使用）</summary>
    public void Merge(StatsCollector other)
    {
        Interlocked.Add(ref _totalBytes, other.TotalBytes);
        Interlocked.Add(ref _totalFrames, other.TotalFrames);
        var now = DateTime.UtcNow.Ticks;
        foreach (var (t, b) in other._bytesWindow) _bytesWindow.Enqueue((t, b));
        foreach (var t in other._frameWindow) _frameWindow.Enqueue(t);
        TrimWindow(now);
        var otherLat = other.LatencyMs;
        if (!double.IsNaN(otherLat)) OnLatencySample(otherLat);
    }

    /// <summary>每秒调用一次：重算码率/帧率。返回快照供 UI 显示。</summary>
    public (double BitrateBps, double Fps, double LatencyMs) Tick()
    {
        var now = DateTime.UtcNow.Ticks;
        TrimWindow(now);

        // 窗口实际跨度（首样本到 now），避免启动初期除以 1 秒高估
        var spanSec = 1.0;
        if (_bytesWindow.TryPeek(out var oldest))
        {
            var s = (now - oldest.Ticks) / (double)TimeSpan.TicksPerSecond;
            if (s > 0.2 && s < 1.0) spanSec = s;
        }
        var bytes = _bytesWindow.Sum(x => (double)x.Bytes);
        var frames = (double)_frameWindow.Count;

        BitrateBps = spanSec > 0 ? bytes / spanSec : 0;
        Fps = spanSec > 0 ? frames / spanSec : 0;
        return (BitrateBps, Fps, LatencyMs);
    }

    /// <summary>清理超出 1 秒窗口的旧样本</summary>
    private void TrimWindow(long now)
    {
        var cutoff = now - TimeSpan.TicksPerSecond * 2; // 保留 2 秒窗口，容忍抖动
        while (_bytesWindow.TryPeek(out var b) && b.Ticks < cutoff)
            _bytesWindow.TryDequeue(out _);
        while (_frameWindow.TryPeek(out var f) && f < cutoff)
            _frameWindow.TryDequeue(out _);
    }

    public void Dispose()
    {
        _bytesWindow.Clear();
        _frameWindow.Clear();
    }
}
