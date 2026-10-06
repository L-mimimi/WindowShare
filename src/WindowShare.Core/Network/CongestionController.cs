using WindowShare.Core.Logging;

namespace WindowShare.Core.Network;

/// <summary>控制决策（null 表示维持不变）</summary>
public sealed record ControlDecision(int? BitrateBps, int? Width, int? Height, string Reason);

/// <summary>
/// 拥塞控制器（Host 侧，每 2 秒评估一次）：
///   - 输入：观看者 RTT 样本、发送丢帧率；
///   - 动作：码率阶梯（100%/60%/35%/20%），连续两次降码率后降分辨率（100%/66%）；
///   - 恢复：RTT 良好且无丢帧持续 15 秒 → 逐级回升，永不超初始值。
/// 纯逻辑无 IO，可完全单测。
/// </summary>
public sealed class CongestionController
{
    // RTT 阈值（毫秒）
    private const double DegradeRttMs = 200;
    private const double RecoverRttMs = 80;

    // 丢帧率阈值
    private const double DegradeDropRatio = 0.10;

    /// <summary>每档回升所需的"持续良好"时长（秒）——每升一档都要重新计满</summary>
    internal const int RecoverHoldSeconds = 15;

    private static readonly int[] BitrateSteps = { 100, 60, 35, 20 }; // 百分比
    private const int MaxResolutionStep = 2;                          // 分辨率阶梯档位 0..2

    private readonly int _initialBitrateBps;
    private readonly int _initialWidth;
    private readonly int _initialHeight;

    private readonly object _gate = new();
    private double _rttEwma = double.NaN;
    private long _sentWindow;
    private long _droppedWindow;
    private DateTime _lastGoodSince;
    private DateTime _lastEvaluate;

    private int _bitrateStep;
    private int _resolutionStep;
    private int _consecutiveDegrades;
    private readonly Func<DateTime> _now;

    public CongestionController(int initialBitrateBps, int initialWidth, int initialHeight)
        : this(initialBitrateBps, initialWidth, initialHeight, () => DateTime.UtcNow)
    {
    }

    /// <summary>测试用构造：可注入时钟</summary>
    public CongestionController(int initialBitrateBps, int initialWidth, int initialHeight,
        Func<DateTime> clock)
    {
        _initialBitrateBps = initialBitrateBps;
        _initialWidth = initialWidth;
        _initialHeight = initialHeight;
        _now = clock;
        _lastEvaluate = _now() - TimeSpan.FromSeconds(3); // 允许首次评估立即执行
        _lastGoodSince = _now();
    }

    /// <summary>记录观看者 RTT 样本（毫秒）</summary>
    public void OnRttSample(double rttMs)
    {
        lock (_gate)
        {
            _rttEwma = double.IsNaN(_rttEwma) ? rttMs : _rttEwma * 0.7 + rttMs * 0.3;
        }
    }

    /// <summary>记录发送统计（周期性累计）</summary>
    public void OnSendStats(int framesSent, int framesDropped)
    {
        lock (_gate)
        {
            _sentWindow += framesSent;
            _droppedWindow += framesDropped;
        }
    }

    /// <summary>当前生效的目标码率（bit/s；初始值 × 当前档位百分比，供状态栏显示）</summary>
    public int CurrentBitrateBps
    {
        get { lock (_gate) return _initialBitrateBps * BitrateSteps[_bitrateStep] / 100; }
    }

    /// <summary>是否处于降档状态（码率或分辨率任一低于初始档）</summary>
    public bool IsDowngraded
    {
        get { lock (_gate) return _bitrateStep > 0 || _resolutionStep > 0; }
    }

    /// <summary>周期评估（每 2 秒调用一次）</summary>
    public ControlDecision Evaluate()
    {
        lock (_gate)
        {
            var now = _now();
            if ((now - _lastEvaluate).TotalSeconds < 2)
                return new ControlDecision(null, null, null, "间隔未到");
            _lastEvaluate = now;

            var dropRatio = _sentWindow + _droppedWindow > 0
                ? _droppedWindow / (double)(_sentWindow + _droppedWindow)
                : 0.0;
            _sentWindow = 0;
            _droppedWindow = 0;

            var congested = (!double.IsNaN(_rttEwma) && _rttEwma > DegradeRttMs) || dropRatio > DegradeDropRatio;
            var healthy = (double.IsNaN(_rttEwma) || _rttEwma < RecoverRttMs) && dropRatio < 0.01;

            if (congested)
            {
                _lastGoodSince = now;
                _consecutiveDegrades++;
                var newBitrateStep = Math.Min(_bitrateStep + 1, BitrateSteps.Length - 1);
                if (newBitrateStep == _bitrateStep)
                    return new ControlDecision(null, null, null, "已在最低档");

                _bitrateStep = newBitrateStep;
                int? newW = null, newH = null;
                // 连续两次降码率后仍拥塞 → 降分辨率
                if (_consecutiveDegrades >= 2 && _resolutionStep < MaxResolutionStep)
                {
                    _resolutionStep++;
                    var scale = ResolutionScale(_resolutionStep);
                    newW = _initialWidth * scale / 100 & ~1;
                    newH = _initialHeight * scale / 100 & ~1;
                    _consecutiveDegrades = 0;
                }
                var target = _initialBitrateBps * BitrateSteps[_bitrateStep] / 100;
                Logging.Logger.Warn("Congestion",
                    $"拥塞 (RTT={(double.IsNaN(_rttEwma) ? "-" : _rttEwma.ToString("F0"))}ms, 丢帧={dropRatio:P0}) → " +
                    $"码率 {target / 1000}kbps{(newW.HasValue ? $", 分辨率 {newW}x{newH}" : "")}");
                return new ControlDecision(target, newW, newH, "拥塞降档");
            }

            // 恢复：需要持续良好 RecoverHoldSeconds 秒才允许回升**一档**。
            // 每成功回升一档都要刷新 _lastGoodSince —— 否则"持续良好 15 秒才升一档"只对第一档成立，
            // 之后每次评估（2s）都会再升一档，从最低档约 10s 冲回满档并再次拥塞（振荡）。
            if (healthy && (_bitrateStep > 0 || _resolutionStep > 0))
            {
                if ((now - _lastGoodSince).TotalSeconds < RecoverHoldSeconds)
                    return new ControlDecision(null, null, null, "观察恢复中");
                _consecutiveDegrades = 0;

                if (_bitrateStep > 0)
                {
                    _bitrateStep--;
                    _lastGoodSince = now;   // 本档已经行动过，下一档再等一个观察窗口
                    var target = _initialBitrateBps * BitrateSteps[_bitrateStep] / 100;
                    Logger.Info("Congestion",
                        $"网络恢复 → 码率 {target / 1000}kbps（档位 {_bitrateStep}/{BitrateSteps.Length - 1}）");
                    return new ControlDecision(target, null, null, "恢复升档");
                }

                // 码率已在满档、分辨率仍在降档档位 → 单独立回升分辨率。
                // 历史上这一步嵌在 `_bitrateStep > 0` 分支内，导致到达
                // `_bitrateStep == 0 && _resolutionStep > 0` 这一自然中间态后永远回不去
                // （本会话分辨率被永久钉在 66%/45%，v1.5.2 审计 A3）。
                _resolutionStep--;
                _lastGoodSince = now;
                var scale = ResolutionScale(_resolutionStep);
                var w = _initialWidth * scale / 100 & ~1;
                var h = _initialHeight * scale / 100 & ~1;
                var target2 = _initialBitrateBps * BitrateSteps[_bitrateStep] / 100;
                Logger.Info("Congestion", $"网络恢复 → 分辨率 {w}x{h}（档位 {_resolutionStep}/{MaxResolutionStep}）");
                return new ControlDecision(target2, w, h, "恢复分辨率");
            }

            if (healthy)
                _lastGoodSince = now; // 持续良好计时

            return new ControlDecision(null, null, null, "维持");
        }
    }

    private static int ResolutionScale(int step) => step switch
    {
        1 => 66,
        2 => 45,
        _ => 100,
    };
}
