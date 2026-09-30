namespace WindowShare.Core.Audio;

/// <summary>
/// 「绝对样本序号 → 采集时间戳」的时间线映射（纯逻辑，可单元测试）。
///
/// AAC 编解码都是「进 N 个样本、延后出 N 个样本」的延迟线：编码器输入 20ms 块，
/// 输出却是固定 1024 样本（21.33ms）一帧，两边的块边界不对齐。要给每个编码/解码
/// 输出块打上正确的采集时间戳（音画同步全靠它），就必须按「第几个样本」而不是
/// 「第几次调用」来定位时间戳。
/// </summary>
public sealed class SampleTimeline
{
    /// <summary>最多保留的标记数（超出后丢弃最旧的一半，防止长时间共享内存增长）</summary>
    private const int MaxMarks = 512;
    private const int TrimTo = 256;

    private readonly List<(long Index, long Utc)> _marks = new();
    private readonly int _sampleRate;

    public SampleTimeline(int sampleRate)
    {
        if (sampleRate < 1) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        _sampleRate = sampleRate;
    }

    /// <summary>
    /// 记录「绝对样本序号 index 对应采集时间戳 utcTicks」。
    /// 序号必须单调前进；重复或回退的标记直接忽略（乱序标记只会把时间线搞乱）。
    /// </summary>
    public void Mark(long index, long utcTicks)
    {
        if (_marks.Count > 0 && index <= _marks[^1].Index) return;
        _marks.Add((index, utcTicks));
        if (_marks.Count > MaxMarks) _marks.RemoveRange(0, _marks.Count - TrimTo);
    }

    /// <summary>当前已记录的标记数</summary>
    public int Count => _marks.Count;

    /// <summary>
    /// 取某个绝对样本序号对应的采集时间戳（在相邻标记之间按采样率线性推算）。
    /// 还没有任何标记时返回 false。
    /// </summary>
    public bool TryGetUtc(long index, out long utcTicks)
    {
        utcTicks = 0;
        if (_marks.Count == 0) return false;

        var lo = 0;
        var hi = _marks.Count - 1;
        var best = -1;
        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            if (_marks[mid].Index <= index) { best = mid; lo = mid + 1; }
            else hi = mid - 1;
        }

        if (best < 0)
        {
            // 早于第一个标记：向前外推（编解码器的 priming 会先吐出几个「更早」的样本）
            var first = _marks[0];
            utcTicks = first.Utc - TicksForSamples(first.Index - index);
            return true;
        }

        var mark = _marks[best];
        utcTicks = mark.Utc + TicksForSamples(index - mark.Index);
        return true;
    }

    /// <summary>
    /// 样本数 → 100ns 时长。必须先乘后除：预先算出「每样本多少 tick」会截断
    /// （48kHz 下 208.33 → 208），一小时累计漂移 5.7 秒，音画同步会越走越偏。
    /// </summary>
    private long TicksForSamples(long samples) => samples * TimeSpan.TicksPerSecond / _sampleRate;

    /// <summary>取时间戳（无标记时退化为 0，调用方自行判断）</summary>
    public long UtcFor(long index) => TryGetUtc(index, out var utc) ? utc : 0;

    /// <summary>清空（重新开始一段流时调用）</summary>
    public void Reset() => _marks.Clear();
}
