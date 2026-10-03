using System.Buffers.Binary;
using System.Net.Sockets;
using WindowShare.Core.Logging;
using WindowShare.Core.Protocol;
using WindowShare.Core.Security;

namespace WindowShare.Core.Network;

/// <summary>
/// TCP 帧连接：二进制协议读写（24 字节帧头 + 负载），支持会话加密。
/// 读循环在后台任务运行；发送加锁串行化。一端一行代码即可收发，两端共用。
/// </summary>
public sealed class TcpFrameConnection : IDisposable
{
    private readonly TcpClient _client;
    private readonly NetworkStream _stream;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private CancellationTokenSource? _readCts;
    private AesGcmSession? _encryption;
    private uint _sendSeq;
    /// <summary>接收侧已见的最大序号（防重放：加密帧序号必须严格递增）</summary>
    private long _lastRecvSeq;

    public bool IsConnected => _client.Connected && !_closed;
    private volatile bool _closed;

    /// <summary>
    /// 是否把 24 字节帧头作为 AAD 绑定进加密（1.3.0 起经认证握手协商；
    /// 与旧版本对端互通时保持 false——密文只保护负载本身）。
    /// </summary>
    public bool UseAadBinding { get; set; }

    /// <summary>诊断用：底层关闭标志</summary>
    internal bool IsClosedForTest => _closed;

    /// <summary>收到完整帧（读循环任务上触发；payload 可能为空）</summary>
    public event Action<FrameHeader, byte[]>? FrameReceived;

    /// <summary>连接断开（读循环任务上触发，仅触发一次）</summary>
    public event Action? Disconnected;

    public TcpFrameConnection(TcpClient client)
    {
        _client = client;
        _stream = client.GetStream();
        _stream.ReadTimeout = Timeout.Infinite;
    }

    /// <summary>启用会话加密（后续发送帧自动加密；接收方按 flags 解密）</summary>
    public void EnableEncryption(AesGcmSession session) => _encryption = session;

    /// <summary>启动读循环</summary>
    public void StartReading()
    {
        _readCts = new CancellationTokenSource();
        _ = Task.Run(() => ReadLoopAsync(_readCts.Token));
    }

    /// <summary>发送一帧（自动加密、自动帧头）</summary>
    public async Task SendAsync(MessageType type, FrameFlags flags, ReadOnlyMemory<byte> payload,
        CancellationToken ct = default)
    {
        await _sendLock.WaitAsync(ct);
        try
        {
            // 组帧与发送同锁：发送缓冲是本连接独占的复用缓冲，串行填充避免撕裂
            var total = SerializeFrame(type, flags, DateTime.UtcNow.Ticks, payload.Span, out var buffer);
            await _stream.WriteAsync(buffer.AsMemory(0, total), ct);
            await _stream.FlushAsync(ct);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <summary>同步发送（时间戳取发送时刻）</summary>
    public void Send(MessageType type, FrameFlags flags, ReadOnlySpan<byte> payload) =>
        Send(type, flags, DateTime.UtcNow.Ticks, payload);

    /// <summary>
    /// 同步发送并显式指定帧头时间戳（自动加密）。
    /// 音画同步要的是「采集时刻」，而默认的发送时刻中间隔着编码与队列等待（几十毫秒起），
    /// 音频帧必须把采集时间戳原样带过去，观看端才对得齐。
    /// </summary>
    public void Send(MessageType type, FrameFlags flags, long timestampUtc, ReadOnlySpan<byte> payload)
    {
        _sendLock.Wait();
        try
        {
            var total = SerializeFrame(type, flags, timestampUtc, payload, out var buffer);
            _stream.Write(buffer, 0, total);
            _stream.Flush();
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <summary>每连接复用的发送缓冲（发送锁内独占使用；增长留旧）</summary>
    private byte[] _sendBuffer = new byte[FrameHeader.HeaderSize + AesGcmSession.OverheadSize + 64 * 1024];

    /// <summary>确保发送缓冲容量（只在发送锁内调用）</summary>
    private byte[] EnsureSendBufferLocked(int total)
    {
        if (_sendBuffer.Length >= total) return _sendBuffer;
        var grown = new byte[Math.Max(total, _sendBuffer.Length * 2)];
        _sendBuffer = grown;
        return grown;
    }

    /// <summary>
    /// 把一帧组进复用发送缓冲（必须在发送锁内调用），返回总长度。
    /// 布局：明文帧 = [头 24][负载]；加密帧 = [头 24][nonce 12][密文 N][tag 16]。
    /// 组帧与发送同锁消除了每帧两次的大数组分配——这是每秒上百次的帧级 GC 热点。
    /// 认证握手（Auth*）必须保持明文——密钥本身就是在握手过程中协商出来的；
    /// Ping/Pong/Bye 不含内容，也走明文。
    /// </summary>
    private int SerializeFrame(MessageType type, FrameFlags flags, long timestampUtc,
        ReadOnlySpan<byte> payload, out byte[] buffer)
    {
        var seq = Interlocked.Increment(ref _sendSeq);
        var enc = _encryption;
        var encrypted = enc != null && ShouldEncrypt(type);
        var payloadLen = encrypted ? AesGcmSession.OverheadSize + payload.Length : payload.Length;
        var total = FrameHeader.HeaderSize + payloadLen;
        buffer = EnsureSendBufferLocked(total);

        // 帧头先写入：AAD 绑定模式下整段帧头参与加密认证
        new FrameHeader(type, encrypted ? flags | FrameFlags.Encrypted : flags, seq, timestampUtc, payloadLen)
            .Write(buffer);
        if (encrypted)
        {
            payload.CopyTo(buffer.AsSpan(FrameHeader.HeaderSize + AesGcmSession.NonceSize, payload.Length));
            enc!.EncryptInPlace(buffer.AsSpan(FrameHeader.HeaderSize), payload.Length,
                UseAadBinding ? buffer.AsSpan(0, FrameHeader.HeaderSize) : default);
        }
        else
        {
            payload.CopyTo(buffer.AsSpan(FrameHeader.HeaderSize, payload.Length));
        }
        return total;
    }

    /// <summary>该类型的负载是否应当加密（内容类消息）</summary>
    private static bool ShouldEncrypt(MessageType type) => type is MessageType.VideoFrame
        or MessageType.AudioFrame
        or MessageType.RawFrame
        or MessageType.StatsInfo;

    /// <summary>读一帧（同步，阻塞；用于认证握手）</summary>
    public (FrameHeader Header, byte[] Payload)? ReadFrame()
    {
        var header = new byte[FrameHeader.HeaderSize];
        if (!ReadExact(header)) return null;
        if (!FrameHeader.TryParse(header, out var hdr)) return null;
        var payload = new byte[hdr.PayloadLength];
        if (hdr.PayloadLength > 0 && !ReadExact(payload)) return null;
        return (hdr, payload);
    }

    private bool ReadExact(byte[] buffer)
    {
        try
        {
            var offset = 0;
            while (offset < buffer.Length)
            {
                var n = _stream.Read(buffer, offset, buffer.Length - offset);
                if (n <= 0) return false;
                offset += n;
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or SocketException)
        {
            return false;
        }
    }

    /// <summary>读循环：解析帧头 → 读负载 → 解密 → 回调</summary>
    private async Task ReadLoopAsync(CancellationToken ct)
    {
        var header = new byte[FrameHeader.HeaderSize];
        try
        {
            while (!ct.IsCancellationRequested && _client.Connected)
            {
                if (!await ReadExactAsync(header, ct)) break;
                if (!FrameHeader.TryParse(header, out var hdr))
                {
                    Logging.Logger.Warn("TCP", "帧头非法，断开连接（可能被攻击或流错位）");
                    break;
                }

                var payload = new byte[hdr.PayloadLength];
                if (hdr.PayloadLength > 0 && !await ReadExactAsync(payload, ct)) break;

                // 解密
                if ((hdr.Flags & FrameFlags.Encrypted) != 0)
                {
                    var enc = _encryption;
                    if (enc == null)
                    {
                        Logging.Logger.Warn("TCP", "收到加密帧但未启用加密，断开");
                        break;
                    }
                    // 防重放：加密帧序号必须严格递增（TCP 有序，乱序/重放即异常）
                    if (UseAadBinding)
                    {
                        var prev = Interlocked.Read(ref _lastRecvSeq);
                        if (hdr.Sequence <= prev)
                        {
                            Logging.Logger.Warn("TCP", $"加密帧序号回退（{hdr.Sequence} ≤ {prev}），疑似重放，断开");
                            break;
                        }
                        Interlocked.Exchange(ref _lastRecvSeq, hdr.Sequence);
                    }
                    try
                    {
                        payload = enc.Decrypt(payload,
                            UseAadBinding ? header.AsSpan(0, FrameHeader.HeaderSize) : default);
                    }
                    catch (System.Security.Cryptography.CryptographicException)
                    {
                        Logging.Logger.Warn("TCP", "解密失败（数据被篡改或密钥不一致），断开");
                        break;
                    }
                }

                FrameReceived?.Invoke(hdr, payload);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Logging.Logger.Warn("TCP", $"读循环异常: {ex.Message}");
        }
        finally
        {
            if (!_closed)
            {
                _closed = true;
                Disconnected?.Invoke();
            }
        }
    }

    private async Task<bool> ReadExactAsync(byte[] buffer, CancellationToken ct)
    {
        try
        {
            var offset = 0;
            while (offset < buffer.Length)
            {
                var n = await _stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), ct);
                if (n <= 0)
                {
                    Logging.Logger.Warn("TCP", $"对端关闭连接 (已读 {offset}/{buffer.Length} 字节)");
                    return false;
                }
                offset += n;
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or SocketException or ObjectDisposedException)
        {
            Logging.Logger.Warn("TCP", $"读流中断: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    public void Dispose()
    {
        if (_closed) return;
        _closed = true;
        _readCts?.Cancel();
        _sendLock.Dispose();
        try { _stream.Dispose(); } catch { }
        try { _client.Dispose(); } catch { }
        (_encryption as IDisposable)?.Dispose();
    }
}
