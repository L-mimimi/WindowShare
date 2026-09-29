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
    /// <summary>会话信息：编码器名/分辨率（展示用）</summary>
    [JsonPropertyName("encoder")] public string EncoderName { get; init; } = "";
    [JsonPropertyName("width")] public int Width { get; init; }
    [JsonPropertyName("height")] public int Height { get; init; }
}

/// <summary>双向：周期统计（Host→Viewer：编码器信息；Viewer→Host：RTT 反馈）</summary>
public sealed record StatsInfoPayload
{
    [JsonPropertyName("encoder")] public string EncoderName { get; init; } = "";
    [JsonPropertyName("hw")] public bool Hardware { get; init; }
    [JsonPropertyName("source")] public string SourceTitle { get; init; } = "";
    /// <summary>Viewer → Host：最近 RTT（毫秒），用于拥塞控制（0=未知）</summary>
    [JsonPropertyName("rttMs")] public double RttMs { get; init; } = 0;
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
