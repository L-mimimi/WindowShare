namespace WindowShare.Core.WebRtc;

/// <summary>
/// WebRTC 配置：STUN/TURN 服务器。
///   - STUN 默认 Google 公共服务（公网发现）；
///   - TURN 通过环境变量配置（对称 NAT / 企业防火墙时的中继回退）：
///       WINDOWSHARE_TURN_URL      如 turn:turn.example.com:3478
///       WINDOWSHARE_TURN_USER     用户名
///       WINDOWSHARE_TURN_CRED     凭据
///     生产部署建议自建 coturn（见 docs/DEPLOY.md）。
/// </summary>
public static class WebRtcSettings
{
    public const string DefaultStunUrl = "stun:stun.l.google.com:19302";

    public static string? TurnUrl =>
        Environment.GetEnvironmentVariable("WINDOWSHARE_TURN_URL");

    public static string? TurnUsername =>
        Environment.GetEnvironmentVariable("WINDOWSHARE_TURN_USER");

    public static string? TurnCredential =>
        Environment.GetEnvironmentVariable("WINDOWSHARE_TURN_CRED");

    public static bool TurnConfigured =>
        !string.IsNullOrWhiteSpace(TurnUrl);

    /// <summary>是否配置了中继回退（UI 提示用）</summary>
    public static string Describe() => TurnConfigured
        ? $"STUN({DefaultStunUrl}) + TURN({TurnUrl})"
        : $"STUN({DefaultStunUrl})，未配置 TURN";
}
