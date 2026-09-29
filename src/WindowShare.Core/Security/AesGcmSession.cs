using System.Security.Cryptography;

namespace WindowShare.Core.Security;

/// <summary>
/// ECDH P-256 临时密钥协商（LAN 会话加密第一步）。
/// 公钥格式：SubjectPublicKeyInfo；会话密钥：HKDF-SHA256(ECDH共享密钥, salt=认证盐, info=wsh1-aead)。
/// 安全性：认证证明 HMAC 绑定双方公钥，不知道密码的中间人无法替换公钥。
/// </summary>
public sealed class EcdhKeyExchange : IDisposable
{
    private readonly ECDiffieHellman _ecdh;

    public EcdhKeyExchange()
    {
        _ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
    }

    /// <summary>导出本端公钥（SPKI 格式）</summary>
    public byte[] ExportPublicKey() => _ecdh.PublicKey.ExportSubjectPublicKeyInfo();

    /// <summary>派生 32 字节 AES-256 会话密钥</summary>
    public byte[] DeriveSessionKey(byte[] remotePublicKey, byte[] salt)
    {
        using var remote = ECDiffieHellman.Create();
        remote.ImportSubjectPublicKeyInfo(remotePublicKey, out _);
        var shared = _ecdh.DeriveRawSecretAgreement(remote.PublicKey);
        return HKDF.DeriveKey(HashAlgorithmName.SHA256, shared, outputLength: 32,
            salt: salt, info: "wsh1-aead"u8.ToArray());
    }

    public void Dispose() => _ecdh.Dispose();
}

/// <summary>
/// AES-256-GCM 会话加密器。
/// 报文格式：[12B 随机 nonce][密文][16B tag]（GCM 自带完整性认证）。
/// </summary>
public sealed class AesGcmSession : IDisposable
{
    private const int NonceSize = 12;
    private const int TagSize = 16;

    private readonly AesGcm _aes;
    private readonly byte[] _key;

    public AesGcmSession(byte[] key32)
    {
        if (key32.Length != 32)
            throw new ArgumentException("会话密钥必须为 32 字节", nameof(key32));
        _key = key32.ToArray();
        _aes = new AesGcm(_key, TagSize);
    }

    /// <summary>加密：返回 nonce+密文+tag</summary>
    public byte[] Encrypt(ReadOnlySpan<byte> plain)
    {
        var result = new byte[NonceSize + plain.Length + TagSize];
        var nonce = result.AsSpan(0, NonceSize);
        RandomNumberGenerator.Fill(nonce);
        var cipher = result.AsSpan(NonceSize, plain.Length);
        var tag = result.AsSpan(^TagSize);
        _aes.Encrypt(nonce, plain, cipher, tag, associatedData: default);
        return result;
    }

    /// <summary>解密：payload 被篡改时抛出 CryptographicException</summary>
    public byte[] Decrypt(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < NonceSize + TagSize)
            throw new CryptographicException("密文过短");
        var nonce = payload[..NonceSize];
        var tag = payload[^TagSize..];
        var cipher = payload.Slice(NonceSize, payload.Length - NonceSize - TagSize);
        var plain = new byte[cipher.Length];
        _aes.Decrypt(nonce, cipher, tag, plain, associatedData: default);
        return plain;
    }

    public void Dispose() => _aes.Dispose();
}
