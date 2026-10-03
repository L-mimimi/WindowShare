using Xunit;
using WindowShare.Core.Network;
using WindowShare.Core.Security;

namespace WindowShare.Core.Tests;

/// <summary>
/// B 安全包：认证限流（注入时钟）+ 版本能力判定 + AES-GCM AAD 绑定。
/// </summary>
public class SecurityHardeningTests
{
    // ===== AuthRateLimiter =====

    private sealed class FakeClock
    {
        public long Now { get; set; } = 1_000_000;
        public long Ticks() => Now;
        public void AdvanceMinutes(double m) => Now += (long)(TimeSpan.FromMinutes(m).Ticks);
    }

    [Fact]
    public void Limiter_AllowsBelowMaxFailures_ThenBlocks()
    {
        var clock = new FakeClock();
        var limiter = new AuthRateLimiter(maxFailures: 3, utcNowTicks: clock.Ticks);

        Assert.False(limiter.RecordFailure("10.0.0.1"));
        Assert.False(limiter.RecordFailure("10.0.0.1"));
        Assert.False(limiter.IsBlocked("10.0.0.1"));
        Assert.True(limiter.RecordFailure("10.0.0.1"));     // 第 3 次失败 → 冷却
        Assert.True(limiter.IsBlocked("10.0.0.1"));
        Assert.Equal(1, limiter.BlockedCount);
    }

    [Fact]
    public void Limiter_OldFailuresFallOutOfWindow()
    {
        var clock = new FakeClock();
        var limiter = new AuthRateLimiter(maxFailures: 3, window: TimeSpan.FromMinutes(1),
            utcNowTicks: clock.Ticks);

        limiter.RecordFailure("ip");
        limiter.RecordFailure("ip");
        clock.AdvanceMinutes(2);                            // 两条失败都滑出窗口
        Assert.False(limiter.RecordFailure("ip"));
        Assert.False(limiter.IsBlocked("ip"));
    }

    [Fact]
    public void Limiter_CooldownExpiresAndClears()
    {
        var clock = new FakeClock();
        var limiter = new AuthRateLimiter(maxFailures: 2, cooldown: TimeSpan.FromMinutes(5),
            utcNowTicks: clock.Ticks);

        limiter.RecordFailure("ip");
        Assert.True(limiter.RecordFailure("ip"));
        Assert.True(limiter.IsBlocked("ip"));

        clock.AdvanceMinutes(6);                            // 冷却结束 → 解封且历史清零
        Assert.False(limiter.IsBlocked("ip"));
        Assert.False(limiter.RecordFailure("ip"));
    }

    [Fact]
    public void Limiter_SuccessClearsFailures()
    {
        var clock = new FakeClock();
        var limiter = new AuthRateLimiter(maxFailures: 2, utcNowTicks: clock.Ticks);

        limiter.RecordFailure("ip");
        limiter.RecordSuccess("ip");
        Assert.False(limiter.RecordFailure("ip"));
        Assert.False(limiter.IsBlocked("ip"));
    }

    [Fact]
    public void Limiter_IpsAreIsolated()
    {
        var clock = new FakeClock();
        var limiter = new AuthRateLimiter(maxFailures: 2, utcNowTicks: clock.Ticks);

        limiter.RecordFailure("a");
        Assert.True(limiter.RecordFailure("a"));
        Assert.False(limiter.IsBlocked("b"));               // 别的 IP 不受牵连
    }

    // ===== PeerCapability（AAD 能力协商）=====

    [Theory]
    [InlineData("1.3.0", true)]
    [InlineData("1.3.5", true)]
    [InlineData("2.0.0", true)]
    [InlineData("1.2.0", false)]
    [InlineData("1.0.0", false)]
    [InlineData("1.3.0-beta", true)]
    [InlineData("", false)]
    [InlineData("garbage", false)]
    public void PeerCapability_AadBindingGate(string version, bool expected)
    {
        Assert.Equal(expected, PeerCapability.SupportsAadBinding(version));
    }

    [Fact]
    public void PeerCapability_NullVersion_Disabled()
    {
        Assert.False(PeerCapability.SupportsAadBinding(null));
    }

    // ===== AES-GCM AAD 绑定 =====

    [Fact]
    public void AesGcm_Aad_Roundtrip()
    {
        using var enc = new AesGcmSession(new byte[32]);
        using var dec = new AesGcmSession(new byte[32]);
        var aad = new byte[] { 1, 2, 3, 0xFF };
        var cipher = enc.Encrypt(new byte[] { 9, 9, 9 }, aad);

        var plain = dec.Decrypt(cipher, aad);
        Assert.Equal(new byte[] { 9, 9, 9 }, plain);
    }

    [Fact]
    public void AesGcm_AadMismatch_FailsDecryption()
    {
        using var enc = new AesGcmSession(new byte[32]);
        using var dec = new AesGcmSession(new byte[32]);
        var cipher = enc.Encrypt(new byte[] { 9, 9, 9 }, new byte[] { 1, 2, 3 });

        // 篡改 AAD（等价于篡改帧头）→ 认证标签不匹配
        Assert.ThrowsAny<System.Security.Cryptography.CryptographicException>(
            () => dec.Decrypt(cipher, new byte[] { 1, 2, 4 }));
        // 不带 AAD 解密同样失败（发送时绑定了 AAD）
        Assert.ThrowsAny<System.Security.Cryptography.CryptographicException>(
            () => dec.Decrypt(cipher));
    }

    [Fact]
    public void AesGcm_InPlaceWithAad_Roundtrip()
    {
        using var enc = new AesGcmSession(new byte[32]);
        using var dec = new AesGcmSession(new byte[32]);

        // 模拟发送侧布局：[nonce 12][密文 N][tag 16]，帧头作为 AAD
        const int payloadLen = 64;
        var header = new byte[24];
        new Random(42).NextBytes(header);
        var workspace = new byte[AesGcmSession.OverheadSize + payloadLen];
        var payload = new byte[payloadLen];
        new Random(7).NextBytes(payload);
        payload.CopyTo(workspace.AsSpan(AesGcmSession.NonceSize));

        enc.EncryptInPlace(workspace, payloadLen, header);

        var full = new byte[24 + workspace.Length];
        header.CopyTo(full, 0);
        workspace.CopyTo(full, 24);
        var plain = dec.Decrypt(full.AsSpan(24), full.AsSpan(0, 24));
        Assert.Equal(payload, plain);
    }
}
