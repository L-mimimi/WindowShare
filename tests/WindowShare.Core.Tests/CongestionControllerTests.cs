using WindowShare.Core.Network;
using Xunit;

namespace WindowShare.Core.Tests;

public class CongestionControllerTests
{
    /// <summary>可控时钟：每次 Evaluate 间隔 3 秒，越过 2 秒最小评估间隔</summary>
    private static (CongestionController C, Func<DateTime> Advance) Create(int bitrate, int w, int h)
    {
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var c = new CongestionController(bitrate, w, h, () => now);
        return (c, () => now = now.AddSeconds(3));
    }

    [Fact]
    public void HealthyNetwork_MaintainsBitrate()
    {
        var (c, advance) = Create(4_000_000, 1920, 1080);
        c.OnRttSample(30);
        c.OnSendStats(60, 0);
        advance();
        var d = c.Evaluate();
        Assert.Null(d.BitrateBps);
        Assert.Null(d.Width);
        Assert.Equal("维持", d.Reason);
    }

    [Fact]
    public void HighRtt_DegradesBitrateStep()
    {
        var (c, advance) = Create(4_000_000, 1920, 1080);
        c.OnRttSample(500);
        advance();
        var d1 = c.Evaluate();
        Assert.Equal(4_000_000 * 60 / 100, d1.BitrateBps); // 60%
        Assert.Null(d1.Width); // 首次降档不降分辨率

        // 连续第二次降码率 → 同时降分辨率（66%）
        c.OnRttSample(500);
        advance();
        var d2 = c.Evaluate();
        Assert.Equal(4_000_000 * 35 / 100, d2.BitrateBps); // 35%
        Assert.Equal(1920 * 66 / 100 & ~1, d2.Width);
        Assert.Equal(1080 * 66 / 100 & ~1, d2.Height);

        // 已到最低码率档 → 不再变化
        c.OnRttSample(500);
        advance();
        var d3 = c.Evaluate();
        Assert.Equal(4_000_000 * 20 / 100, d3.BitrateBps);
        Assert.Null(d3.Width);
    }

    [Fact]
    public void BottomStep_StaysAtMinimum()
    {
        var (c, advance) = Create(2_000_000, 1280, 720);
        // 打到最低档（3 次降档后已在最底）
        for (var i = 0; i < 6; i++)
        {
            c.OnRttSample(800);
            advance();
            c.Evaluate();
        }
        c.OnRttSample(800);
        advance();
        var d = c.Evaluate();
        Assert.Null(d.BitrateBps);
        Assert.Equal("已在最低档", d.Reason);
    }

    [Fact]
    public void HighDropRatio_TriggersDegrade()
    {
        var (c, advance) = Create(4_000_000, 1920, 1080);
        c.OnSendStats(60, 20); // 丢帧率 25% > 10%
        advance();
        var d = c.Evaluate();
        Assert.NotNull(d.BitrateBps);
        Assert.True(d.BitrateBps < 4_000_000);
    }

    [Fact]
    public void Evaluate_HasMinInterval()
    {
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var c = new CongestionController(4_000_000, 1920, 1080, () => now);
        c.OnRttSample(500);
        var d1 = c.Evaluate();
        Assert.NotNull(d1.BitrateBps);
        // 时钟未推进 → 间隔不足 → 不动作
        var d2 = c.Evaluate();
        Assert.Null(d2.BitrateBps);
        Assert.Equal("间隔未到", d2.Reason);
    }

    [Fact]
    public void Recovery_NeverExceedsInitial()
    {
        var (c, advance) = Create(2_000_000, 1280, 720);
        // 先打到最低档
        for (var i = 0; i < 6; i++)
        {
            c.OnRttSample(800);
            advance();
            c.Evaluate();
        }
        // 恢复网络：连续 6 次评估（每次间隔 3 秒，覆盖 15 秒观察期）
        for (var i = 0; i < 8; i++)
        {
            c.OnRttSample(15);
            c.OnSendStats(60, 0);
            advance();
            var d = c.Evaluate();
            if (d.BitrateBps.HasValue)
                Assert.InRange(d.BitrateBps.Value, 400_000, 2_000_000);
            if (d.Width.HasValue)
                Assert.InRange(d.Width.Value, 576, 1280);
        }
    }
}
