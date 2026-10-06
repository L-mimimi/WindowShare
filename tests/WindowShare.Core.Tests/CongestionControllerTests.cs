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

    /// <summary>
    /// A3-1：分辨率能回到初始值。历史缺陷——分辨率回升嵌在 `_bitrateStep > 0` 分支内，
    /// 到达 `_bitrateStep == 0 && _resolutionStep > 0` 后该分支永不进入，分辨率被永久钉住。
    /// 这里只降到"码率 35% + 分辨率 66%"的中间态，再持续恢复，断言分辨率回到 1920x1080。
    ///
    /// 时间账（每档需 RecoverHoldSeconds=15s，且每升一档后重新计时）：
    ///   降档 60% → 降档（35% + 分辨率 66%）→ 升 35%→60% → 升 60%→100% → 升分辨率回 100%
    ///   共 3 次回升 × 15s ≈ 45s，因此循环窗口要大于 45s（这里 24×3s = 72s）。
    /// </summary>
    [Fact]
    public void Recovery_RestoresResolution_FromMidStepState()
    {
        var (c, advance) = Create(4_000_000, 1920, 1080);

        // 两次拥塞：60% →（35% + 分辨率 66%）
        var d1 = Degrade(c, advance);
        Assert.Equal(4_000_000 * 60 / 100, d1.BitrateBps);
        var d2 = Degrade(c, advance);
        Assert.Equal(1920 * 66 / 100 & ~1, d2.Width);

        // 持续健康恢复。注意「恢复升档」（仅码率）的决策不带 Width/Height，
        // 因此用 CurrentBitrateBps 变化来记录所有回升里程碑，用决策的 Reason 判断分辨率回升。
        int? recoveredWidth = null, recoveredHeight = null;
        var recoveryMilestones = new List<string>();
        var prevBitrate = c.CurrentBitrateBps;
        for (var i = 0; i < 24; i++)
        {
            c.OnRttSample(15);
            c.OnSendStats(60, 0);
            advance();
            var d = c.Evaluate();
            var bitrate = c.CurrentBitrateBps;
            if (bitrate > prevBitrate) recoveryMilestones.Add($"码率→{bitrate / 1000}kbps");
            prevBitrate = bitrate;
            if (d.Width.HasValue)
            {
                recoveredWidth = d.Width;
                recoveredHeight = d.Height;
                recoveryMilestones.Add($"分辨率→{d.Width}x{d.Height}");
            }
        }

        // 先逐档补码率（35%→60%→100%），最后再回升分辨率（66%→100%）——每档各自等满 15s，
        // 因此从 35%+66% 的中间态完整恢复需要 3×15s ≈ 45s。
        // 第一条是降档阶段的最后一次码率变化（评估顺序：先码率后分辨率），
        // 后两条 + 分辨率是恢复阶段，正好锁定「每档一次、顺序正确」。
        Assert.Equal(
            new[] { "码率→1400kbps", "码率→2400kbps", "码率→4000kbps", "分辨率→1920x1080" },
            recoveryMilestones);
        Assert.Equal(1920, recoveredWidth);
        Assert.Equal(1080, recoveredHeight);
        Assert.Equal(4_000_000, c.CurrentBitrateBps);
        Assert.False(c.IsDowngraded, "恢复完成后不应再处于降档状态");
    }

    /// <summary>
    /// A3-2："持续良好 15 秒才回升一档"必须每档重新计时。
    /// 历史缺陷——回升分支从不刷新 `_lastGoodSince`，于是 15 秒一过就每 2 秒升一档，
    /// 约 10 秒从最低档冲回满档并再次拥塞（振荡）。这里断言升档间距不小于 15 秒。
    /// </summary>
    [Fact]
    public void Recovery_PacesOneStepPerHoldWindow()
    {
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var c = new CongestionController(4_000_000, 1920, 1080, () => now);

        // 打到最低档（码率 20%）
        for (var i = 0; i < 4; i++)
        {
            c.OnRttSample(500);
            now = now.AddSeconds(3);
            c.Evaluate();
        }

        // 每 3 秒评估一次，网络持续健康；记录每次"码率回升"的间隔
        var recoverAt = new List<double>();
        var prevBitrate = c.CurrentBitrateBps;
        for (var i = 1; i <= 40; i++)
        {
            c.OnRttSample(15);
            c.OnSendStats(60, 0);
            now = now.AddSeconds(3);
            c.Evaluate();
            var bitrate = c.CurrentBitrateBps;
            if (bitrate > prevBitrate) recoverAt.Add(i * 3.0);
            prevBitrate = bitrate;
        }

        Assert.True(recoverAt.Count >= 2, $"应至少回升两档，实际 {recoverAt.Count} 次");
        for (var i = 1; i < recoverAt.Count; i++)
            Assert.True(recoverAt[i] - recoverAt[i - 1] >= CongestionController.RecoverHoldSeconds,
                $"升档间隔过短（{recoverAt[i] - recoverAt[i - 1]}s < {CongestionController.RecoverHoldSeconds}s）→ 回升节流失效");
    }

    /// <summary>制造一次拥塞评估并推进时钟</summary>
    private static ControlDecision Degrade(CongestionController c, Func<DateTime> advance)
    {
        c.OnRttSample(500);
        advance();
        return c.Evaluate();
    }
}
