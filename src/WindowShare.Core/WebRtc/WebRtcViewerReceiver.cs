using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;
using WindowShare.Core.Logging;

namespace WindowShare.Core.WebRtc;

/// <summary>
/// WebRTC 观看端接收器（SIPSorcery，DTLS-SRTP 加密）：
///   - recvonly H.264 轨道；ICE/STUN/TURN 与发送端对称；
///   - 收到重构后的 H.264 帧（Annex-B）经 <see cref="FrameReceived"/> 回调给解码器。
/// </summary>
public sealed class WebRtcViewerReceiver : IAsyncDisposable
{
    private readonly RTCPeerConnection _pc;

    /// <summary>本地 ICE 候选（中继给 Host）</summary>
    public event Action<string>? LocalIceCandidate;

    /// <summary>收到一帧 H.264</summary>
    public event Action<byte[], long>? FrameReceived;

    /// <summary>连接状态变化（detail：直连/中继）</summary>
    public event Action<string>? StateChanged;

    public event Action<double>? RttUpdated;

    public bool IsConnected { get; private set; }
    public bool UsedRelay { get; private set; }

    public WebRtcViewerReceiver()
    {
        var config = new RTCConfiguration { iceServers = new List<RTCIceServer>() };
        config.iceServers.Add(new RTCIceServer { urls = WebRtcSettings.DefaultStunUrl });
        if (WebRtcSettings.TurnConfigured)
        {
            config.iceServers.Add(new RTCIceServer
            {
                urls = WebRtcSettings.TurnUrl,
                username = WebRtcSettings.TurnUsername,
                credential = WebRtcSettings.TurnCredential,
            });
        }

        _pc = new RTCPeerConnection(config);

        var h264Format = new VideoFormat(VideoCodecsEnum.H264, 96, 90000,
            "packetization-mode=1;profile-level-id=42e01f");
        _pc.addTrack(new MediaStreamTrack(new List<VideoFormat> { h264Format },
            MediaStreamStatusEnum.RecvOnly));

        // 收到重构的 H.264 帧（SIPSorcery 内部完成 RTP 去包/重组）
        _pc.OnVideoFrameReceived += (remoteEndPoint, timestamp, payload, format) =>
        {
            if (payload is { Length: > 0 })
                FrameReceived?.Invoke(payload, DateTime.UtcNow.Ticks);
        };

        _pc.onicecandidate += candidate =>
        {
            if (candidate?.candidate != null)
            {
                if (candidate.type == RTCIceCandidateType.relay) UsedRelay = true;
                LocalIceCandidate?.Invoke(candidate.candidate);
            }
        };
        _pc.onconnectionstatechange += state =>
        {
            Logging.Logger.Info("WebRTC", $"Viewer 连接状态: {state}");
            IsConnected = state == RTCPeerConnectionState.connected;
            StateChanged?.Invoke(DescribeState(state));
        };
    }

    private static string DescribeState(RTCPeerConnectionState state) => state switch
    {
        RTCPeerConnectionState.connected => "已连接（WebRTC · DTLS-SRTP）",
        RTCPeerConnectionState.connecting => "WebRTC 连接中…",
        RTCPeerConnectionState.failed => "WebRTC 连接失败",
        RTCPeerConnectionState.closed => "WebRTC 已关闭",
        _ => state.ToString(),
    };

    /// <summary>接收 Host 的 offer 并生成 answer</summary>
    public async Task<string> AcceptOfferAsync(string offerSdp)
    {
        var offer = new RTCSessionDescriptionInit { type = RTCSdpType.offer, sdp = offerSdp };
        var setResult = _pc.setRemoteDescription(offer);
        if (setResult != SetDescriptionResultEnum.OK)
            throw new InvalidOperationException($"setRemoteDescription(offer) 失败: {setResult}");
        var answer = _pc.createAnswer();
        await _pc.setLocalDescription(answer);
        return answer.sdp ?? throw new InvalidOperationException("answer 为空");
    }

    /// <summary>加入 Host 的 ICE 候选</summary>
    public void AddIceCandidate(string candidateSdp)
    {
        var init = new RTCIceCandidateInit { candidate = candidateSdp };
        _pc.addIceCandidate(init);
    }

    public ValueTask DisposeAsync()
    {
        try { _pc.Close("disposed"); } catch { }
        _pc.Dispose();
        return ValueTask.CompletedTask;
    }
}
