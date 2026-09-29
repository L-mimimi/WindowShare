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

    public bool IsConnected => _client.Connected && !_closed;
    private volatile bool _closed;

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
        var enc = _encryption;
        byte[] buffer;
        if (enc != null && (type == MessageType.VideoFrame || type == MessageType.StatsInfo))
        {
            var cipher = enc.Encrypt(payload.Span);
            var seq = Interlocked.Increment(ref _sendSeq);
            buffer = new byte[FrameHeader.HeaderSize + cipher.Length];
            new FrameHeader(type, flags | FrameFlags.Encrypted, seq, DateTime.UtcNow.Ticks, cipher.Length)
                .Write(buffer);
            cipher.CopyTo(buffer.AsMemory(FrameHeader.HeaderSize));
        }
        else
        {
            var seq = Interlocked.Increment(ref _sendSeq);
            buffer = FrameHeader.BuildFrame(type, flags, seq, DateTime.UtcNow.Ticks, payload.Span);
        }

        await _sendLock.WaitAsync(ct);
        try
        {
            await _stream.WriteAsync(buffer, ct);
            await _stream.FlushAsync(ct);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <summary>同步发送（认证握手阶段用，简单可靠）</summary>
    public void Send(MessageType type, FrameFlags flags, ReadOnlySpan<byte> payload)
    {
        var seq = Interlocked.Increment(ref _sendSeq);
        var buffer = FrameHeader.BuildFrame(type, flags, seq, DateTime.UtcNow.Ticks, payload);
        _sendLock.Wait();
        try
        {
            _stream.Write(buffer, 0, buffer.Length);
            _stream.Flush();
        }
        finally
        {
            _sendLock.Release();
        }
    }

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
                    try
                    {
                        payload = enc.Decrypt(payload);
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
