using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;
using WindowShare.Core.Encoding;
using WindowShare.Core.Logging;

namespace WindowShare.Core.WebRtc;

/// <summary>ICE 连接信息（直连/中继判定 + 状态展示）</summary>
public sealed record IceConnectionReport(bool Connected, bool Relay, string Detail);

/// <summary>
/// WebRTC Host 发送端（SIPSorcery，DTLS-SRTP 加密）：
///   - sendonly H.264 轨道，直接发送 Media Foundation 编码出的 Annex-B 帧；
///   - ICE：STUN 穿透，失败走 TURN（relay 候选）；
///   - 信令（offer/answer/ice）由调用方经信令服务器中继。
/// </summary>
public sealed class WebRtcHostSender : IAsyncDisposable
{
    private readonly RTCPeerConnection _pc;
    private uint _rtpTimestamp;

    /// <summary>本地 ICE 候选（需中继给观看者）</summary>
    public event Action<string>? LocalIceCandidate;

    /// <summary>连接状态变化（detail：直连/中继）</summary>
    public event Action<string>? StateChanged;

    public bool IsConnected { get; private set; }
    public bool UsedRelay { get; private set; }

    public WebRtcHostSender()
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

        // sendonly H.264 轨道（packetization-mode=1）
        var h264Format = new VideoFormat(VideoCodecsEnum.H264, 96, 90000,
            "packetization-mode=1;profile-level-id=42e01f");
        _pc.addTrack(new MediaStreamTrack(h264Format, MediaStreamStatusEnum.SendOnly));

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
            Logging.Logger.Info("WebRTC", $"Host 连接状态: {state}");
            IsConnected = state == RTCPeerConnectionState.connected;
            StateChanged?.Invoke(DescribeState(state));
        };
        _pc.oniceconnectionstatechange += state =>
        {
            if (state == RTCIceConnectionState.connected)
                IsConnected = true;
        };
    }

    private static string DescribeState(RTCPeerConnectionState state) => state switch
    {
        RTCPeerConnectionState.connected => "已连接（DTLS-SRTP 加密）",
        RTCPeerConnectionState.connecting => "连接中…",
        RTCPeerConnectionState.failed => "连接失败",
        RTCPeerConnectionState.closed => "已关闭",
        _ => state.ToString(),
    };

    /// <summary>创建 offer 并设置本地描述（调用方把 offer sdp 中继给观看者）</summary>
    public async Task<string> CreateOfferAsync()
    {
        var offer = _pc.createOffer();
        await _pc.setLocalDescription(offer);
        return offer.sdp ?? throw new InvalidOperationException("offer 为空");
    }

    /// <summary>设置观看者的 answer（同步：setRemoteDescription 返回枚举）</summary>
    public void SetAnswer(string answerSdp)
    {
        var init = new RTCSessionDescriptionInit { type = RTCSdpType.answer, sdp = answerSdp };
        var result = _pc.setRemoteDescription(init);
        if (result != SetDescriptionResultEnum.OK)
            throw new InvalidOperationException($"setRemoteDescription(answer) 失败: {result}");
    }

    /// <summary>加入观看者的 ICE 候选</summary>
    public void AddIceCandidate(string candidateSdp)
    {
        var init = new RTCIceCandidateInit { candidate = candidateSdp };
        _pc.addIceCandidate(init);
    }

    /// <summary>发送一帧编码后的 H.264（Annex-B）</summary>
    public void SendEncodedFrame(EncodedVideoFrame frame, int fps)
    {
        if (!IsConnected) return;
        // RTP 时间戳增量：90000/fps（H.264 时钟率 90kHz）
        var duration = (uint)(90000 / Math.Max(1, fps));
        _rtpTimestamp += duration;
        try
        {
            _pc.SendVideo(duration, frame.Data);
        }
        catch (Exception ex)
        {
            Logging.Logger.Warn("WebRTC", $"发送帧失败: {ex.Message}");
        }
    }

    public ValueTask DisposeAsync()
    {
        try { _pc.Close("disposed"); } catch { }
        _pc.Dispose();
        return ValueTask.CompletedTask;
    }
}
