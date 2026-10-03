using WindowShare.Core.Encoding;
using WindowShare.Core.Logging;
using WindowShare.Core.Session;
using WindowShare.Core.WebRtc;

namespace WindowShare.Host;

/// <summary>
/// WebRTC 发送器的会话 sink 适配器：共享会话 → WebRTC 发送（仅连接后发送）。
/// Host 关闭共享时 OnShareStopped 会关闭发送器。
/// 连接刚建立时先补发缓存的 GOP（自上一个 IDR 起），观看者不必黑屏等下一个关键帧。
/// 同时实现 <see cref="ShareSession.IAudioSink"/>：Host 带音频轨时系统声音随 WebRTC 一起送出。
/// </summary>
public sealed class WebRtcSinkAdapter : ShareSession.IFrameSink, ShareSession.IAudioSink
{
    private readonly WebRtcHostSender _sender;
    private readonly int _fps;
    // 与 LanShareServer 同一套思路：编码器可能不认 ForceKeyFrame，靠补发缓存实现秒开。
    private readonly GopCache _gopCache = new();
    private readonly object _gate = new();
    private bool _replayed;

    public WebRtcSinkAdapter(WebRtcHostSender sender, int fps)
    {
        _sender = sender;
        _fps = fps;
    }

    public string Name => "WebRTC";

    public void OnEncodedFrame(EncodedVideoFrame frame)
    {
        // 缓存与「是否已补发」的判定在同一把锁内，保证补发序列与后续直发帧不重不漏
        EncodedVideoFrame[]? replay = null;
        bool connected;
        lock (_gate)
        {
            _gopCache.Add(frame);
            connected = _sender.IsConnected;
            if (connected && !_replayed)
            {
                _replayed = true;
                replay = _gopCache.GetReplayFrames();
            }
        }

        if (!connected) return;

        if (replay is { Length: > 0 })
        {
            // 补发序列已包含当前帧，发完直接返回，避免当前帧被发两次
            Logger.Info("WebRTC", $"连接就绪，补发缓存 GOP {replay.Length} 帧（观看者无需等待下一个关键帧）");
            foreach (var cached in replay)
                _sender.SendEncodedFrame(cached, _fps);
            return;
        }

        _sender.SendEncodedFrame(frame, _fps);
    }

    public void OnShareStopped(string reason)
    {
        _ = _sender.DisposeAsync();
    }

    /// <summary>音频帧直发（发送端内部处理连接状态与降级判定）</summary>
    public void OnAudioFrame(Core.Audio.EncodedAudioFrame frame) => _sender.SendAudioFrame(frame);
}
