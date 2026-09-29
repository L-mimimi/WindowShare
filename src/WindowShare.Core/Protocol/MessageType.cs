namespace WindowShare.Core.Protocol;

/// <summary>
/// TCP/WebRTC 之下、所有传输之上共用的帧消息类型。
/// 只读共享协议：不存在任何输入/控制类消息（协议层面保证不可远程控制）。
/// </summary>
public enum MessageType : byte
{
    // ===== 认证握手 =====
    /// <summary>Viewer → Host：认证请求（deviceId/deviceName/nonce，JSON）</summary>
    AuthRequest = 1,
    /// <summary>Host → Viewer：质询（salt，JSON；不含密码任何派生物）</summary>
    AuthChallenge = 2,
    /// <summary>Viewer → Host：证明（HMAC(PBKDF2(pwd,salt))，含可选 ECDH 公钥，JSON）</summary>
    AuthProof = 3,
    /// <summary>Host → Viewer：认证结果（成功/失败原因/会话信息，JSON）</summary>
    AuthResult = 4,

    // ===== 会话控制 =====
    /// <summary>心跳（payload 含 8 字节发送时间戳，用于 RTT 计算）</summary>
    Ping = 10,
    /// <summary>心跳回包（原样返回时间戳）</summary>
    Pong = 11,
    /// <summary>Viewer 请求关键帧（重连/丢包恢复用）</summary>
    KeyframeRequest = 12,
    /// <summary>任意一方请求断开（优雅关闭）</summary>
    Bye = 13,
    /// <summary>Host → Viewer：共享已停止通知</summary>
    ShareStopped = 14,
    /// <summary>双向：周期性统计（JSON：编码器名/当前码率等）</summary>
    StatsInfo = 15,

    // ===== 媒体 =====
    /// <summary>H.264 Annex-B 码流帧（一个访问单元 = 1 帧，含 SPS/PPS/IDR 或非 IDR NAL）</summary>
    VideoFrame = 20,
    /// <summary>原始视频帧（未编码，用于测试通路；BGRA 像素）</summary>
    RawFrame = 21,
}

/// <summary>帧头标志位</summary>
[Flags]
public enum FrameFlags : byte
{
    None = 0,
    /// <summary>关键帧（IDR，可作为解码起点）</summary>
    Keyframe = 1,
    /// <summary>负载已用会话密钥 AES-256-GCM 加密</summary>
    Encrypted = 2,
}
