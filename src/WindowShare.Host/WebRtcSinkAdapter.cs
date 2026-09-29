using WindowShare.Core.Encoding;
using WindowShare.Core.Session;
using WindowShare.Core.WebRtc;

namespace WindowShare.Host;

/// <summary>
/// WebRTC 发送器的会话 sink 适配器：共享会话 → WebRTC 发送（仅连接后发送）。
/// Host 关闭共享时 OnShareStopped 会关闭发送器。
/// </summary>
public sealed class WebRtcSinkAdapter : ShareSession.IFrameSink
{
    private readonly WebRtcHostSender _sender;
    private readonly int _fps;

    public WebRtcSinkAdapter(WebRtcHostSender sender, int fps)
    {
        _sender = sender;
        _fps = fps;
    }

    public string Name => "WebRTC";

    public void OnEncodedFrame(EncodedVideoFrame frame)
    {
        if (_sender.IsConnected)
            _sender.SendEncodedFrame(frame, _fps);
    }

    public void OnShareStopped(string reason)
    {
        _ = _sender.DisposeAsync();
    }
}
