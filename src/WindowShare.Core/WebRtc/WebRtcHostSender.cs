using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;
using WindowShare.Core.Audio;
using WindowShare.Core.Encoding;
using WindowShare.Core.Logging;

namespace WindowShare.Core.WebRtc;

/// <summary>ICE 连接信息（直连/中继判定 + 状态展示）</summary>
public sealed record IceConnectionReport(bool Connected, bool Relay, string Detail);

/// <summary>
/// WebRTC Host 发送端（SIPSorcery，DTLS-SRTP 加密）：
///   - sendonly H.264 轨道，直接发送 Media Foundation 编码出的 Annex-B 帧；
///   - 可选 sendonly 音频轨道：AAC-LC 走自定义动态 PT（两端都是 WindowShare，无需浏览器互通）；
///   - ICE：STUN 穿透，失败走 TURN（relay 候选）；
///   - 信令（offer/answer/ice）由调用方经信令服务器中继。
/// </summary>
public sealed class WebRtcHostSender : IAsyncDisposable
{
    /// <summary>
    /// AAC 音频格式（动态 PT 97，48kHz/立体声）：与 LAN 通路共用 MF AAC 编码器的输出。
    /// RTP 时钟率与采样率一致（48kHz），1 帧 = 1024 样本 ≈ 21.3ms。
    /// </summary>
    internal static readonly AudioFormat AacFormat = new(97, "AAC", 48000, 48000, 2, "");

    /// <summary>AAC 单帧样本数（RTP 时间戳增量）</summary>
    internal const uint AacSamplesPerFrame = 1024;

    private readonly RTCPeerConnection _pc;
    private uint _rtpTimestamp;

    /// <summary>本地 ICE 候选（需中继给观看者）</summary>
    public event Action<string>? LocalIceCandidate;

    /// <summary>连接状态变化（detail：直连/中继）</summary>
    public event Action<string>? StateChanged;

    public bool IsConnected { get; private set; }
    public bool UsedRelay { get; private set; }

    /// <summary>本次会话是否带音频轨（旧版观看端协商失败降级后为 false）</summary>
    public bool AudioIncluded { get; private set; }

    public WebRtcHostSender(bool includeAudio = true)
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

        // 可选 AAC 音频轨道（v1.3+）
        AudioIncluded = includeAudio;
        if (includeAudio)
            _pc.addTrack(new MediaStreamTrack(AacFormat, MediaStreamStatusEnum.SendOnly));

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

    /// <summary>发送一帧 AAC 音频（ADTS 头会被剥掉，只发裸 AAC 负载；无音频轨时静默丢弃）</summary>
    public void SendAudioFrame(EncodedAudioFrame frame)
    {
        if (!IsConnected || !AudioIncluded) return;
        try
        {
            // RTP 层只认裸 AAC 负载，ADTS 头交给接收端从协商参数恢复
            var payload = Audio.Adts.Strip(frame.Data);
            _pc.SendAudio(AacSamplesPerFrame, payload);
        }
        catch (Exception ex)
        {
            Logging.Logger.Warn("WebRTC", $"发送音频帧失败: {ex.Message}");
        }
    }

    public ValueTask DisposeAsync()
    {
        try { _pc.Close("disposed"); } catch { }
        _pc.Dispose();
        return ValueTask.CompletedTask;
    }
}
