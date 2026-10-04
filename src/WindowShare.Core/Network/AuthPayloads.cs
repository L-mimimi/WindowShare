using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WindowShare.Core.Network;

/// <summary>Viewer → Host：认证请求（明文 JSON）</summary>
public sealed record AuthRequestPayload
{
    [JsonPropertyName("deviceId")] public string DeviceId { get; init; } = "";
    [JsonPropertyName("deviceName")] public string DeviceName { get; init; } = "";
    [JsonPropertyName("proto")] public int ProtocolVersion { get; init; } = 1;
    [JsonPropertyName("roomCode")] public string RoomCode { get; init; } = "";
    /// <summary>
    /// 观看端应用版本（"1.3.0"；1.2.0 及更早版本不带此字段）。
    /// Host 据此决定是否对本次连接启用「帧头 AAD 绑定 + 防重放」加密增强。
    /// </summary>
    [JsonPropertyName("ver")] public string AppVersion { get; init; } = "";
    /// <summary>
    /// Viewer 是否支持 HEVC 解码（1.4.0 起，子进程探针实测；老版本不带此字段 = false）。
    /// Host 会话为 HEVC 时仅对接入能力为 true 的观看端放行。
    /// </summary>
    [JsonPropertyName("hevc")] public bool HevcSupported { get; init; }
}

/// <summary>Host → Viewer：认证质询（salt + 服务端能力）</summary>
public sealed record AuthChallengePayload
{
    [JsonPropertyName("salt")] public string SaltB64 { get; init; } = "";
    /// <summary>是否支持会话加密（ECDH+AES-GCM）</summary>
    [JsonPropertyName("enc")] public bool SupportsEncryption { get; init; }
    /// <summary>Host ECDH P-256 公钥（SubjectPublicKeyInfo，Base64；启用加密时提供）</summary>
    [JsonPropertyName("hostPub")] public string HostPubB64 { get; init; } = "";
}

/// <summary>Viewer → Host：认证证明（证明知道密码；绑定 ECDH 公钥防中间人）</summary>
public sealed record AuthProofPayload
{
    /// <summary>HMAC-SHA256(PBKDF2(password,salt), salt||deviceId||hostPub||clientPub)</summary>
    [JsonPropertyName("proof")] public string ProofB64 { get; init; } = "";
    /// <summary>Viewer ECDH 公钥（启用加密时提供）</summary>
    [JsonPropertyName("clientPub")] public string ClientPubB64 { get; init; } = "";
}

/// <summary>Host → Viewer：认证结果</summary>
public sealed record AuthResultPayload
{
    [JsonPropertyName("ok")] public bool Ok { get; init; }
    [JsonPropertyName("reason")] public string Reason { get; init; } = "";
    /// <summary>是否启用会话加密</summary>
    [JsonPropertyName("enc")] public bool EncryptionEnabled { get; init; }
    /// <summary>
    /// 加密增强：24 字节帧头作为 AAD 绑定 + 加密帧序号防重放（1.3.0 起）。
    /// 仅对表明支持该能力（ver ≥ 1.3）的观看端置 true；老版本忽略此字段继续用旧加密格式。
    /// </summary>
    [JsonPropertyName("aad")] public bool AadBindingEnabled { get; init; }
    /// <summary>会话信息：编码器名/分辨率（展示用）</summary>
    [JsonPropertyName("encoder")] public string EncoderName { get; init; } = "";
    [JsonPropertyName("width")] public int Width { get; init; }
    [JsonPropertyName("height")] public int Height { get; init; }

    // ===== 画质透明化（1.3.3 起；老版本 Host 不带这些字段，Viewer 按「未知」显示）=====
    /// <summary>本次会话的目标码率（bit/s；拥塞降档后以 StatsInfo 的实时值为准）</summary>
    [JsonPropertyName("bitrate")] public int TargetBitrateBps { get; init; }
    /// <summary>配置帧率</summary>
    [JsonPropertyName("fps")] public int Fps { get; init; }
    /// <summary>
    /// 会话视频编码（1.4.0 起；"h264"/"hevc"。老版本 Host 不带此字段，Viewer 按 H.264 处理）
    /// </summary>
    [JsonPropertyName("vcodec")] public string VideoCodecWireName { get; init; } = "h264";

    // ===== 系统声音（1.2.0 起；老版本 Host 不带这些字段，Viewer 按「无音频」处理）=====
    /// <summary>本次会话是否共享系统声音</summary>
    [JsonPropertyName("audio")] public bool AudioEnabled { get; init; }
    /// <summary>音频采样率（固定 48000）</summary>
    [JsonPropertyName("audioRate")] public int AudioSampleRate { get; init; }
    /// <summary>音频声道数（固定 2）</summary>
    [JsonPropertyName("audioCh")] public int AudioChannels { get; init; }
    /// <summary>音频编码标识（"aac-adts"）</summary>
    [JsonPropertyName("audioCodec")] public string AudioCodec { get; init; } = "";
    /// <summary>音频编码器名（诊断显示用；与视频编码器名是两回事，不要混用）</summary>
    [JsonPropertyName("audioEnc")] public string AudioEncoderName { get; init; } = "";
}

/// <summary>双向：周期统计（Host→Viewer：编码器信息；Viewer→Host：RTT 反馈）</summary>
public sealed record StatsInfoPayload
{
    [JsonPropertyName("encoder")] public string EncoderName { get; init; } = "";
    [JsonPropertyName("hw")] public bool Hardware { get; init; }
    [JsonPropertyName("source")] public string SourceTitle { get; init; } = "";
    /// <summary>Viewer → Host：最近 RTT（毫秒），用于拥塞控制（0=未知）</summary>
    [JsonPropertyName("rttMs")] public double RttMs { get; init; } = 0;

    // ===== 画质透明化（1.3.3 起；老版本 Host 不带这些字段，Viewer 按「未知」显示）=====
    /// <summary>Host 当前目标码率（bit/s，含拥塞降档后的实时值；0=未知/老版本）</summary>
    [JsonPropertyName("bitrate")] public int TargetBitrateBps { get; init; }
    /// <summary>配置帧率（0=未知/老版本）</summary>
    [JsonPropertyName("fps")] public int Fps { get; init; }
    /// <summary>Host 是否处于拥塞降档状态（true 时状态栏提示「已降档」）</summary>
    [JsonPropertyName("dg")] public bool Downgraded { get; init; }
}

/// <summary>认证载荷的 JSON 序列化辅助</summary>
public static class AuthPayload
{
    private static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // 防御 NaN/Infinity 序列化异常
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    public static byte[] Serialize<T>(T payload) => JsonSerializer.SerializeToUtf8Bytes(payload, Options);

    public static T? Deserialize<T>(ReadOnlySpan<byte> utf8) where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<T>(utf8, Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>认证证明的计算（双方共用同一公式，防中间人绑定）</summary>
public static class AuthMath
{
    /// <summary>proof = HMAC-SHA256(pbkdf2Key, salt || deviceId || hostPub || clientPub)</summary>
    public static byte[] ComputeProof(byte[] pbkdf2Key, byte[] salt, string deviceId,
        byte[]? hostPub, byte[]? clientPub)
    {
        var parts = new List<byte[]>
        {
            salt,
            System.Text.Encoding.UTF8.GetBytes(deviceId),
        };
        if (hostPub != null) parts.Add(hostPub);
        if (clientPub != null) parts.Add(clientPub);
        return Security.Hmac.Compute(pbkdf2Key, parts.ToArray());
    }
}
