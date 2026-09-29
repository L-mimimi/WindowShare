using System.Security.Cryptography;

namespace WindowShare.Core.Security;

/// <summary>
/// 随机房间号 / 临时密码生成器。
/// 使用去混淆字母表（无 0/O/1/I/L），避免口述与手抄出错。
/// 密码为会话级临时密码：共享会话结束后即失效（由 Session 层保证）。
/// </summary>
public static class PasswordGenerator
{
    /// <summary>去混淆字母表（31 个字符）</summary>
    public const string Alphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";

    /// <summary>生成房间号（默认 6 位大写字母数字）</summary>
    public static string GenerateRoomCode(int length = 6) => Generate(length, upper: true);

    /// <summary>生成临时密码（默认 8 位大写字母数字）</summary>
    public static string GeneratePassword(int length = 8) => Generate(length, upper: true);

    private static string Generate(int length, bool upper)
    {
        if (length < 4 || length > 32)
            throw new ArgumentOutOfRangeException(nameof(length), "长度必须在 4..32 之间");
        var data = RandomNumberGenerator.GetBytes(length);
        var chars = new char[length];
        for (var i = 0; i < length; i++)
        {
            var c = Alphabet[data[i] % Alphabet.Length];
            chars[i] = upper ? c : char.ToLowerInvariant(c);
        }
        return new string(chars);
    }
}

/// <summary>
/// PBKDF2-SHA256 密钥派生 + 常量时间比较。
/// 用途：
///   1) 信令服务器存储密码哈希（不存明文）；
///   2) LAN 认证质询（Host 下发 salt，Viewer 回 HMAC 证明知道密码）。
/// </summary>
public static class Pbkdf2
{
    /// <summary>默认迭代次数（安全性与性能折衷，CPU 上约 60ms）</summary>
    public const int DefaultIterations = 100_000;

    public const int SaltSize = 16;
    public const int KeySize = 32;

    /// <summary>生成随机盐</summary>
    public static byte[] NewSalt() => RandomNumberGenerator.GetBytes(SaltSize);

    /// <summary>派生 32 字节密钥</summary>
    public static byte[] Derive(string password, ReadOnlySpan<byte> salt, int iterations = DefaultIterations)
    {
        ArgumentException.ThrowIfNullOrEmpty(password);
        if (iterations < 10_000)
            throw new ArgumentOutOfRangeException(nameof(iterations), "迭代次数过低");
        // Rfc2898DeriveBytes.Pbkdf2 是 .NET 8 推荐的静态入口（OpenSSL 加速）
        return Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, KeySize);
    }

    /// <summary>常量时间比较（防时序侧信道）</summary>
    public static bool FixedTimeEquals(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b) =>
        CryptographicOperations.FixedTimeEquals(a, b);
}

/// <summary>
/// HMAC-SHA256 计算辅助：认证证明与传输完整性绑定使用。
/// </summary>
public static class Hmac
{
    public static byte[] Compute(byte[] key, params byte[][] parts)
    {
        using var h = new HMACSHA256(key);
        foreach (var p in parts)
            h.TransformBlock(p, 0, p.Length, null, 0);
        h.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return h.Hash!;
    }

    /// <summary>把 UTF-8 字符串并入 HMAC 的便捷重载</summary>
    public static byte[] Compute(byte[] key, params string[] utf8Parts)
    {
        var bytes = utf8Parts.Select(p => EncodingUtf8(p)).ToArray();
        return Compute(key, bytes);
    }

    private static byte[] EncodingUtf8(string s) => System.Text.Encoding.UTF8.GetBytes(s);
}
