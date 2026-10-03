namespace WindowShare.Core.Network;

/// <summary>
/// 认证尝试速率限制（纯逻辑，可注入时钟单测）：
///   - 每 IP 在滑动窗口内最多 <see cref="_maxFailures"/> 次认证失败；
///   - 超限后该 IP 进入冷却期，期间所有认证请求直接拒绝（连白名单弹窗都不触发）；
///   - 认证成功清空该 IP 的失败记录。
///
/// 防的是局域网内对 8 位临时密码的持续暴力尝试：白名单只挡「看」，限流挡「试」。
/// 仅内存态、按会话重置——重启 Host 即清零，不引入持久化黑名单的复杂度。
/// </summary>
public sealed class AuthRateLimiter
{
    private readonly int _maxFailures;
    private readonly TimeSpan _window;
    private readonly TimeSpan _cooldown;
    private readonly object _gate = new();
    private readonly Dictionary<string, (List<long> Failures, long BlockedUntilTicks)> _records = new();
    private readonly Func<long> _utcNowTicks;

    public AuthRateLimiter(
        int maxFailures = 5,
        TimeSpan? window = null,
        TimeSpan? cooldown = null,
        Func<long>? utcNowTicks = null)
    {
        if (maxFailures < 1) throw new ArgumentOutOfRangeException(nameof(maxFailures));
        _maxFailures = maxFailures;
        _window = window ?? TimeSpan.FromSeconds(60);
        _cooldown = cooldown ?? TimeSpan.FromMinutes(5);
        _utcNowTicks = utcNowTicks ?? (() => DateTime.UtcNow.Ticks);
    }

    /// <summary>该 IP 当前是否处于冷却期（被拒绝）</summary>
    public bool IsBlocked(string ip)
    {
        var now = _utcNowTicks();
        lock (_gate)
        {
            if (!_records.TryGetValue(ip, out var rec)) return false;
            if (rec.BlockedUntilTicks > now) return true;
            // 冷却已过：清掉失败历史，给改错密码的正常人重新开始的机会
            if (rec.BlockedUntilTicks > 0) _records.Remove(ip);
            return false;
        }
    }

    /// <summary>记录一次认证失败；返回该 IP 是否因此进入冷却期</summary>
    public bool RecordFailure(string ip)
    {
        var now = _utcNowTicks();
        lock (_gate)
        {
            var rec = _records.TryGetValue(ip, out var r) ? r : (new List<long>(), 0);
            if (rec.BlockedUntilTicks > now) return true;   // 已在冷却中，无需再记

            var windowStart = now - _window.Ticks;
            rec.Failures.RemoveAll(t => t < windowStart);
            rec.Failures.Add(now);
            if (rec.Failures.Count >= _maxFailures)
            {
                _records[ip] = (rec.Failures, now + _cooldown.Ticks);
                return true;
            }
            _records[ip] = (rec.Failures, 0);
            return false;
        }
    }

    /// <summary>认证成功：清空该 IP 的失败记录（成功意味着密码是对的，无需再防）</summary>
    public void RecordSuccess(string ip)
    {
        lock (_gate) _records.Remove(ip);
    }

    /// <summary>当前处于冷却期的 IP 数（诊断/测试用）</summary>
    public int BlockedCount
    {
        get
        {
            var now = _utcNowTicks();
            lock (_gate) return _records.Values.Count(r => r.BlockedUntilTicks > now);
        }
    }
}

/// <summary>对端能力判定（认证握手的版本协商）</summary>
public static class PeerCapability
{
    /// <summary>
    /// 帧头 AAD 绑定 + 防重放需要两端 ≥ 1.3.0。
    /// 对端版本号为空（1.2.0 及更早的 AuthRequest 不带 ver 字段）→ 不启用。
    /// </summary>
    public static bool SupportsAadBinding(string? peerAppVersion)
    {
        if (string.IsNullOrWhiteSpace(peerAppVersion)) return false;
        // 容忍 "1.3.0-beta" 这类后缀：Version.Parse 接受它并忽略构建号后的内容
        return System.Version.TryParse(peerAppVersion.Split('-')[0], out var v)
               && (v.Major > 1 || (v.Major == 1 && v.Minor >= 3));
    }
}
