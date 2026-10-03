using Vortice.Direct3D11;
using WindowShare.Core.Audio;
using WindowShare.Core.Capture;
using WindowShare.Core.Encoding;
using WindowShare.Core.Logging;
using WindowShare.Core.Security;
using WindowShare.Core.Utils;

namespace WindowShare.Core.Session;

/// <summary>预览帧（BGRA，≤30fps，仅供 UI 显示，不进传输）</summary>
public sealed class PreviewFrame
{
    public required byte[] Bgra { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
}

/// <summary>共享选项</summary>
public sealed record ShareOptions
{
    public required int Width { get; init; }
    public required int Fps { get; init; }
    public required int BitrateBps { get; init; }
    /// <summary>同时写 H.264 文件（编码验证）</summary>
    public bool RecordForValidation { get; init; }
    /// <summary>录制输出路径（RecordForValidation=true 时有效）</summary>
    public string? RecordFilePath { get; init; }
    /// <summary>捕获引擎偏好（默认自动降级；指定 GDI 可获得与屏幕内容无关的稳定帧率）</summary>
    public CaptureEnginePreference CaptureEngine { get; init; } = CaptureEnginePreference.Auto;
    /// <summary>
    /// 同时共享系统声音（WASAPI loopback 采集「默认播放设备正在播的内容」，不是麦克风）。
    /// 失败会自动降级为纯视频共享，不影响画面。
    /// </summary>
    public bool ShareAudio { get; init; }
}

/// <summary>会话音频参数（Viewer 据此决定是否起播、用什么采样率建解码器）</summary>
public sealed record AudioSessionInfo(
    bool Enabled, int SampleRate, int Channels, string Codec, string EncoderName)
{
    /// <summary>本次会话不共享声音</summary>
    public static AudioSessionInfo Disabled { get; } = new(false, 0, 0, "", "");
}

/// <summary>
/// 共享会话编排器（Host 核心）：
///   捕获引擎 → 编码管线 → 分发到所有 <see cref="IFrameSink"/>（LAN 服务器 / WebRTC 发送端 / 文件写入）。
///   生成会话级房间号与临时密码；一键停止时通知所有 sink 并断流。
/// 线程模型：Start/Stop 需在调用线程；事件在各自工作线程触发，UI 层自行调度。
/// </summary>
public sealed class ShareSession : IDisposable
{
    /// <summary>
    /// 编码帧的接收端（传输层实现此接口接入会话）。
    /// 只读共享：接口中不存在任何输入注入类方法。
    /// </summary>
    public interface IFrameSink
    {
        string Name { get; }
        void OnEncodedFrame(EncodedVideoFrame frame);
        void OnShareStopped(string reason);
    }

    /// <summary>
    /// 可选的音频接收端接口。与 <see cref="IFrameSink"/> 分开而不是往里加方法：
    /// 音频是可选能力，只共享视频的接收端（文件写入、WebRTC 回退）不必被迫实现。
    /// 分发时用 <c>sink is IAudioSink</c> 判定，老接收端零改动。
    /// </summary>
    public interface IAudioSink
    {
        void OnAudioFrame(EncodedAudioFrame frame);
    }

    private readonly object _gate = new();
    private ICaptureEngine? _engine;
    private EncoderPipeline? _pipeline;
    private H264FileWriter? _fileWriter;
    private AudioPipeline? _audio;
    private readonly List<IFrameSink> _sinks = new();
    private ID3D11Texture2D? _previewStaging;
    /// <summary>
    /// 预览缓冲三槽轮换：1080p 一帧 8MB、30fps 即 ~240MB/s 的分配率。
    /// UI 线程通常在下一帧到来前就消费完（WritePixels 同步拷贝）；极端卡顿时
    /// 最坏看到一帧撕裂，仅影响本机预览观感——换算成收益是预览路径零 GC 压力。
    /// </summary>
    private readonly byte[][] _previewBuffers = new byte[3][];
    private int _previewBufferIndex;
    private long _lastPreviewQpc;
    private long _startTicks;
    /// <summary>预览节流间隔（约 30fps）</summary>
    private readonly long _previewIntervalQpc =
        System.Diagnostics.Stopwatch.Frequency * 33 / 1000;

    public bool IsSharing { get; private set; }
    public CaptureSource? Source { get; private set; }
    public ShareOptions? Options { get; private set; }

    /// <summary>会话房间号（6 位）</summary>
    public string RoomCode { get; private set; } = string.Empty;

    /// <summary>会话临时密码（8 位，会话结束即失效）</summary>
    public string Password { get; private set; } = string.Empty;

    /// <summary>编码器显示名</summary>
    public string EncoderName => _pipeline?.EncoderName ?? "未启动";

    /// <summary>是否硬件编码</summary>
    public bool IsHardwareEncoder => _pipeline?.IsHardwareEncoder ?? false;

    /// <summary>是否零拷贝</summary>
    public bool IsZeroCopy => _pipeline?.IsZeroCopy ?? false;

    /// <summary>当前编码输出尺寸（未共享时为 0×0）</summary>
    public (int Width, int Height) OutputSize => _pipeline?.OutputSize ?? (0, 0);

    /// <summary>本次会话的音频参数（未启用/启动失败时为 Disabled）</summary>
    public AudioSessionInfo AudioInfo
    {
        get
        {
            var audio = _audio;
            return audio is { IsRunning: true }
                ? new AudioSessionInfo(true, audio.SampleRate, audio.Channels,
                    AudioStreamInfo.Codec, audio.EncoderName)
                : AudioSessionInfo.Disabled;
        }
    }

    /// <summary>系统声音电平（0..1，Host UI 显示「正在传声音」）</summary>
    public float AudioLevel => _audio?.Level ?? 0f;

    /// <summary>已接入的接收端列表（快照）</summary>
    public IReadOnlyList<string> SinkNames
    {
        get
        {
            lock (_gate)
                return _sinks.Select(s => s.Name).ToList();
        }
    }

    /// <summary>预览帧（≤30fps，工作线程触发）</summary>
    public event Action<PreviewFrame>? PreviewArrived;

    /// <summary>共享已停止（reason 为空表示用户主动停止）</summary>
    public event Action<string>? Stopped;

    /// <summary>发生错误</summary>
    public event Action<string>? Error;

    /// <summary>注册编码帧接收端</summary>
    public void AddSink(IFrameSink sink)
    {
        lock (_gate) _sinks.Add(sink);
    }

    /// <summary>移除接收端</summary>
    public void RemoveSink(IFrameSink sink)
    {
        lock (_gate) _sinks.Remove(sink);
    }

    /// <summary>启动共享</summary>
    public void Start(CaptureSource source, ShareOptions options)
    {
        lock (_gate)
        {
            if (IsSharing)
                throw new InvalidOperationException("共享已在进行中");

            Logger.Info("Session", $"启动共享: {source}, {options.Width}p@{options.Fps}fps, {options.BitrateBps / 1000}kbps");

            // 会话凭据（临时密码随会话失效）
            RoomCode = PasswordGenerator.GenerateRoomCode();
            Password = PasswordGenerator.GeneratePassword();

            var (engine, note) = CaptureEngineFactory.Create(source, options.CaptureEngine);
            if (!string.IsNullOrEmpty(note))
                Logger.Warn("Session", note);
            // 轮询式引擎（GDI）自身按目标帧率节流；推送式引擎（WGC/DXGI）由编码管线统一节流
            if (engine is IFrameRateLimited rateLimited)
                rateLimited.TargetFps = options.Fps;

            var fps = Math.Clamp(options.Fps, 1, 240);
            // 等比缩放到目标宽度：保持源宽高比，源比目标小时不上采样。
            // 最高支持 4K（3840×2160）；实际输出同时受源尺寸与拥塞控制的动态分辨率上限约束。
            var (encWidth, encHeight) = VideoFormatPlanner.FitToWidth(
                source.Bounds.Width, source.Bounds.Height, options.Width);
            if (encWidth < Math.Min(options.Width, VideoFormatPlanner.MaxPresetWidth))
                Logger.Info("Session",
                    $"源 {source.Bounds.Width}x{source.Bounds.Height} 小于目标宽度 {options.Width}，" +
                    $"按源尺寸输出 {encWidth}x{encHeight}（不做上采样）");

            var pipeline = new EncoderPipeline(new EncoderSettings
            {
                Width = encWidth,
                Height = encHeight,
                Fps = fps,
                BitrateBps = options.BitrateBps,
                GopSize = Math.Max(2, fps * 2), // 2 秒一个关键帧
            });

            var recordFile = options.RecordForValidation ? options.RecordFilePath : null;
            if (recordFile != null)
            {
                _fileWriter = new H264FileWriter(recordFile);
                pipeline.Encoded += f => _fileWriter.Write(f);
            }

            pipeline.Encoded += DispatchToSinks;

            _engine = engine;
            _pipeline = pipeline;
            Source = source;
            Options = options;

            engine.StoppedBySystem += reason =>
            {
                Error?.Invoke(reason);
                Stop(reason);
            };
            engine.FrameArrived += OnFrameArrived;

            // 系统声音（可选）：放在视频管线起来之后，失败只降级为纯视频
            if (options.ShareAudio)
                StartAudio();

            // 编码分辨率跟随源（首个帧到达后自动确定，动态分辨率上限由拥塞控制调整）
            _startTicks = DateTime.UtcNow.Ticks;
            engine.Start(source);
            IsSharing = true;
            Logger.Info("Session",
                $"共享已开始: 房间号={RoomCode}, {encWidth}x{encHeight}@{fps}fps {options.BitrateBps / 1000}kbps, " +
                $"编码器={EncoderName}, 硬件={IsHardwareEncoder}, 零拷贝={IsZeroCopy}" +
                $"，系统声音={(AudioInfo.Enabled ? AudioInfo.EncoderName : "未共享")}");
        }
    }

    /// <summary>
    /// 启动系统声音采集与编码。任何失败都只降级为「纯视频共享」：
    /// 没有播放设备、没有 AAC 编码器、音频服务未启动，都不该让整场共享起不来。
    /// </summary>
    private void StartAudio()
    {
        AudioPipeline? audio = null;
        try
        {
            audio = new AudioPipeline();
            audio.Encoded += DispatchAudioToSinks;
            audio.Start();
            _audio = audio;
        }
        catch (Exception ex)
        {
            Logger.Warn("Session", $"系统声音共享启动失败，本次只共享视频: {ex.Message}");
            if (audio != null)
            {
                audio.Encoded -= DispatchAudioToSinks;
                try { audio.Dispose(); } catch { }
            }
        }
    }

    /// <summary>
    /// 轮换会话凭据（房间号 + 临时密码），共享不中断：
    /// 用于信令服务器报告「房间号被占用」时换新号重新注册，已接入的观看者不受影响。
    /// </summary>
    public (string RoomCode, string Password) RotateCredentials()
    {
        lock (_gate)
        {
            RoomCode = PasswordGenerator.GenerateRoomCode();
            Password = PasswordGenerator.GeneratePassword();
            Logger.Info("Session", $"会话凭据已轮换: 房间号={RoomCode}");
            return (RoomCode, Password);
        }
    }

    /// <summary>切换共享源（运行中）</summary>
    public void SwitchSource(CaptureSource newSource)
    {
        lock (_gate)
        {
            if (!IsSharing || _engine == null) return;
            Logger.Info("Session", $"切换共享源 → {newSource}");
            _engine.Start(newSource); // 引擎内部自动重启
            Source = newSource;
        }
    }

    /// <summary>一键停止（UI 悬浮条/主窗口/热键都调用这里）</summary>
    public void Stop(string reason = "")
    {
        ICaptureEngine? engine;
        EncoderPipeline? pipeline;
        AudioPipeline? audio;
        IFrameSink[] sinks;
        lock (_gate)
        {
            if (!IsSharing) return;
            IsSharing = false;
            engine = _engine;
            pipeline = _pipeline;
            audio = _audio;
            sinks = _sinks.ToArray();
            _engine = null;
            _pipeline = null;
            _audio = null;
        }

        if (engine != null) engine.FrameArrived -= OnFrameArrived;
        try { engine?.Stop(); } catch (Exception ex) { Logger.Warn("Session", $"停止捕获异常: {ex.Message}"); }
        // 音频先停并冲刷：尾部几帧要在通知 sink 断开之前发出去，观看端才不会被截断
        if (audio != null)
        {
            try { audio.Stop(); }
            catch (Exception ex) { Logger.Warn("Session", $"停止音频采集异常: {ex.Message}"); }
            audio.Encoded -= DispatchAudioToSinks;
            try { audio.Dispose(); } catch { }
        }
        if (pipeline != null)
        {
            pipeline.Encoded -= DispatchToSinks;
            try { pipeline.Dispose(); } catch { }
        }
        try { engine?.Dispose(); } catch { }
        _previewStaging?.Dispose();
        _previewStaging = null;

        // 通知所有接收端共享已停止
        foreach (var sink in sinks)
        {
            try { sink.OnShareStopped(reason); }
            catch (Exception ex) { Logger.Warn("Session", $"通知 sink 停止失败 ({sink.Name}): {ex.Message}"); }
        }

        _fileWriter?.Dispose();
        _fileWriter = null;

        var elapsed = TimeSpan.FromTicks(DateTime.UtcNow.Ticks - _startTicks);
        Logger.Info("Session", $"共享已停止 ({reason}), 持续 {elapsed:hh\\:mm\\:ss}");
        Stopped?.Invoke(reason);
    }

    /// <summary>释放资源（等效于停止共享）</summary>
    public void Dispose() => Stop();

    /// <summary>请求关键帧（Viewer 重连/新接入时由传输层调用）</summary>
    public void RequestKeyframe()
    {
        lock (_gate) _pipeline?.RequestKeyframe();
    }

    /// <summary>动态码率调整</summary>
    public void SetDynamicBitrate(int bitrateBps)
    {
        lock (_gate) _pipeline?.SetBitrate(bitrateBps);
    }

    /// <summary>动态分辨率调整（动态分辨率上限）</summary>
    public void SetDynamicResolution(int width, int height)
    {
        lock (_gate) _pipeline?.SetDynamicResolution(width, height);
    }

    /// <summary>分发编码帧到所有接收端</summary>
    private void DispatchToSinks(EncodedVideoFrame frame)
    {
        IFrameSink[] sinks;
        lock (_gate) sinks = _sinks.ToArray();
        foreach (var sink in sinks)
        {
            try { sink.OnEncodedFrame(frame); }
            catch (Exception ex)
            {
                Logger.Warn("Session", $"分发帧到 {sink.Name} 失败: {ex.Message}");
            }
        }
    }

    /// <summary>分发音频帧到实现了 <see cref="IAudioSink"/> 的接收端（其余接收端跳过）</summary>
    private void DispatchAudioToSinks(EncodedAudioFrame frame)
    {
        IFrameSink[] sinks;
        lock (_gate) sinks = _sinks.ToArray();
        foreach (var sink in sinks)
        {
            if (sink is not IAudioSink audioSink) continue;
            try { audioSink.OnAudioFrame(frame); }
            catch (Exception ex)
            {
                Logger.Warn("Session", $"分发音频帧到 {sink.Name} 失败: {ex.Message}");
            }
        }
    }

    /// <summary>捕获帧处理：先做低频预览拷贝，再提交编码（Submit 会接管并释放帧）</summary>
    private void OnFrameArrived(CaptureFrame frame)
    {
        var qpc = System.Diagnostics.Stopwatch.GetTimestamp();
        if (qpc - _lastPreviewQpc > _previewIntervalQpc)
        {
            _lastPreviewQpc = qpc;
            try
            {
                var preview = CopyForPreview(frame);
                if (preview != null) PreviewArrived?.Invoke(preview);
            }
            catch (Exception ex)
            {
                Logger.Warn("Session", "预览拷贝失败: " + ex.Message);
            }
        }

        _pipeline?.Submit(frame);
    }

    /// <summary>GPU 纹理 → CPU BGRA（预览用，≤30fps，开销约 1-2ms）</summary>
    private PreviewFrame? CopyForPreview(CaptureFrame frame)
    {
        if (frame.IsGpu)
        {
            var tex = frame.Texture!;
            var dev = tex.Device;
            var desc = tex.Description;
            var w = (int)desc.Width;
            var h = (int)desc.Height;

            if (_previewStaging == null || _previewStaging.Description.Width != desc.Width ||
                _previewStaging.Description.Height != desc.Height)
            {
                _previewStaging?.Dispose();
                _previewStaging = dev.CreateTexture2D(new Vortice.Direct3D11.Texture2DDescription
                {
                    Width = desc.Width,
                    Height = desc.Height,
                    MipLevels = 1,
                    ArraySize = 1,
                    Format = desc.Format,
                    SampleDescription = new Vortice.DXGI.SampleDescription(1, 0),
                    Usage = Vortice.Direct3D11.ResourceUsage.Staging,
                    CPUAccessFlags = Vortice.Direct3D11.CpuAccessFlags.Read,
                });
            }

            var ctx = dev.ImmediateContext;
            ctx.CopyResource(_previewStaging, tex);
            ctx.Map(_previewStaging, 0, Vortice.Direct3D11.MapMode.Read,
                Vortice.Direct3D11.MapFlags.None, out var mapped).CheckError();
            try
            {
                var bgra = _previewBuffers[_previewBufferIndex];
                if (bgra == null || bgra.Length != w * h * 4)
                {
                    bgra = new byte[w * h * 4];
                    _previewBuffers[_previewBufferIndex] = bgra;
                }
                _previewBufferIndex = (_previewBufferIndex + 1) % _previewBuffers.Length;
                unsafe
                {
                    fixed (byte* dst = bgra)
                    {
                        var src = (byte*)mapped.DataPointer;
                        var stride = (int)mapped.RowPitch;
                        if (stride == w * 4)
                        {
                            Buffer.MemoryCopy(src, dst, bgra.Length, bgra.Length);
                        }
                        else
                        {
                            for (var r = 0; r < h; r++)
                                Buffer.MemoryCopy(src + (long)stride * r,
                                    dst + (long)w * 4 * r, (long)w * 4, (long)w * 4);
                        }
                    }
                }
                return new PreviewFrame { Bgra = bgra, Width = w, Height = h };
            }
            finally
            {
                ctx.Unmap(_previewStaging, 0);
            }
        }

        // CPU 帧直接使用像素数组（byte[] 独立持有，不受 Dispose 影响）
        return new PreviewFrame { Bgra = frame.BgraPixels!, Width = frame.Width, Height = frame.Height };
    }
}
