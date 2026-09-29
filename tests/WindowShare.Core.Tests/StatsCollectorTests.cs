using WindowShare.Core.Stats;
using Xunit;

namespace WindowShare.Core.Tests;

public class StatsCollectorTests
{
    [Fact]
    public void Tick_ComputesBitrateAndFps()
    {
        var s = new StatsCollector();
        for (var i = 0; i < 30; i++)
            s.OnFrame(1000); // 30 帧 × 1000 字节

        var (bitrate, fps, _) = s.Tick();
        // 窗口跨度 < 1 秒（毫秒级完成），码率/帧率按实际跨度折算后 ≥ 名义值
        Assert.True(fps >= 25, $"fps={fps}");
        Assert.True(bitrate >= 25000, $"bitrate={bitrate}");
        Assert.Equal(30, s.TotalFrames);
        Assert.Equal(30000, s.TotalBytes);
    }

    [Fact]
    public void Latency_ConvergesToInput()
    {
        var s = new StatsCollector();
        for (var i = 0; i < 50; i++)
            s.OnLatencySample(40);
        Assert.InRange(s.LatencyMs, 39.9, 40.1);
    }

    [Fact]
    public void Latency_StartsAsNaN()
    {
        var s = new StatsCollector();
        Assert.True(double.IsNaN(s.LatencyMs));
    }
}
