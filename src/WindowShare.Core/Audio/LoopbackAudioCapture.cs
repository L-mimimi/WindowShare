using System.Runtime.InteropServices;
using WindowShare.Core.Logging;

namespace WindowShare.Core.Audio;

/// <summary>
/// 系统声音采集（WASAPI loopback）。
///
/// 采的是「默认渲染端点正在播放的内容」，也就是系统内音频（游戏/视频/音乐），
/// 不是麦克风：loopback 从扬声器这条路上取数据，环境噪声与本机麦克风输入都不会进来。
///
/// 两个必须处理的坑：
///   1) 系统静音时 loopback 一个数据包都不产（音频引擎会停时钟）。若不处理，音频流会
///      断断续续，Viewer 端的音频时钟也跟着停，音画同步直接失效。因此这里主动补静音块，
///      保证输出严格是实时连续流。
///   2) 设备混音格式是 float32、声道数与采样率由系统决定（44.1k/48k、2/6/8 声道都可能），
///      而 AAC 编码统一走 48 kHz 立体声，所以采集侧就地完成重混 + 重采样。
/// </summary>
public sealed class LoopbackAudioCapture : IDisposable
{
    /// <summary>补静音的单次上限：设备异常/时钟跳变时不至于灌进一整段空白</summary>
    private const int MaxGapFrames = AudioStreamInfo.SampleRate;    // 1 秒
    /// <summary>小于这个间隔的缝隙不补（WASAPI 的包边界本来就有零点几毫秒抖动）</summary>
    private const long GapThresholdTicks = TimeSpan.TicksPerMillisecond * 5;
    /// <summary>轮询间隔：比 20ms 的块长小一个量级，保证不会攒出大块延迟</summary>
    private const int PollIntervalMs = 2;

    private Thread? _thread;
    private CancellationTokenSource? _cts;
    private ManualResetEventSlim? _ready;
    private Exception? _initError;
    private volatile bool _running;

    private readonly List<short> _pending = new();
    private long _pendingStartUtc;
    private float[] _floatScratch = new float[0];
    private short[] _remixScratch = new short[0];

    private long _qpcBase;
    private long _utcBase;
    private long _capturedChunks;
    private long _silenceChunks;
    private float _peakLevel;

    /// <summary>一段 20ms/48kHz/立体声 int16 PCM 就绪（采集线程上触发，回调不要阻塞）</summary>
    public event Action<PcmChunk>? ChunkArrived;

    /// <summary>设备混音格式的采样率（诊断/日志用；输出统一是 48 kHz）</summary>
    public int DeviceSampleRate { get; private set; }
    /// <summary>设备混音格式的声道数</summary>
    public int DeviceChannels { get; private set; }
    /// <summary>设备混音格式是否 float32</summary>
    public bool DeviceIsFloat { get; private set; }

    public bool IsRunning => _running;

    /// <summary>真实音频块数（不含补的静音）</summary>
    public long CapturedChunks => Interlocked.Read(ref _capturedChunks);
    /// <summary>补出来的静音块数（系统没在放声音）</summary>
    public long SilenceChunks => Interlocked.Read(ref _silenceChunks);
    /// <summary>最近一块的峰值电平（0..1，Host UI 显示「有声音在传」）</summary>
    public float PeakLevel => _peakLevel;

    /// <summary>
    /// 启动采集。COM 对象在采集线程内创建与释放（WASAPI 对象不保证跨线程释放安全），
    /// 初始化失败会在本方法内抛出，调用方据此降级为「只共享视频」。
    /// </summary>
    public void Start()
    {
        if (_running) return;
        _initError = null;
        _cts = new CancellationTokenSource();
        _ready = new ManualResetEventSlim(false);
        _thread = new Thread(Run) { IsBackground = true, Name = "AudioLoopback", Priority = ThreadPriority.AboveNormal };
        _thread.Start();

        if (!_ready.Wait(TimeSpan.FromSeconds(8)))
        {
            Stop();
            throw new TimeoutException("WASAPI loopback 初始化超时（音频服务无响应）");
        }
        if (_initError != null)
        {
            Stop();
            throw new InvalidOperationException("系统声音采集启动失败: " + _initError.Message, _initError);
        }
        _running = true;
    }

    public void Stop()
    {
        _running = false;
        try { _cts?.Cancel(); } catch { }
        var thread = _thread;
        _thread = null;
        if (thread != null && thread.IsAlive)
        {
            try { thread.Join(1000); } catch { }
        }
        _cts?.Dispose();
        _cts = null;
        try { _ready?.Dispose(); } catch { }
        _ready = null;
        lock (_pending) _pending.Clear();
    }

    public void Dispose() => Stop();

    // ===== 采集线程 =====

    private void Run()
    {
        IMMDevice? device = null;
        IAudioClient? client = null;
        IAudioCaptureClient? capture = null;
        var mixPtr = IntPtr.Zero;
        try
        {
            device = Wasapi.GetDefaultRenderDevice();
            client = Wasapi.ActivateAudioClient(device);

            var hr = client.GetMixFormat(out mixPtr);
            if (hr != 0 || mixPtr == IntPtr.Zero)
                throw new InvalidOperationException($"取混音格式失败 (HRESULT {Wasapi.Describe(hr)})");

            var mix = Marshal.PtrToStructure<WaveFormatEx>(mixPtr);
            var isFloat = mix.IsFloat(mixPtr);
            DeviceSampleRate = (int)mix.SamplesPerSec;
            DeviceChannels = mix.Channels;
            DeviceIsFloat = isFloat;
            if (mix.Channels < 1 || mix.SamplesPerSec < 8000)
                throw new InvalidOperationException($"混音格式异常: {mix.SamplesPerSec}Hz/{mix.Channels}ch");

            // loopback 必须用渲染端点的混音格式初始化，不接受其他格式
            hr = client.Initialize(Wasapi.ShareModeShared, Wasapi.StreamFlagsLoopback,
                TimeSpan.TicksPerMillisecond * 100, 0, mixPtr, IntPtr.Zero);
            if (hr == Wasapi.AudclntEBufferSizeNotAligned)
            {
                // 缓冲区必须按设备周期对齐：按上次请求取整后重试一次（MSDN 推荐做法）
                client.GetBufferSize(out var alignFrames);
                var alignDuration = (long)(alignFrames * TimeSpan.TicksPerSecond / mix.SamplesPerSec);
                client = null;                       // Initialize 失败后该实例不可再用
                device = Wasapi.GetDefaultRenderDevice();
                client = Wasapi.ActivateAudioClient(device);
                hr = client.Initialize(Wasapi.ShareModeShared, Wasapi.StreamFlagsLoopback,
                    alignDuration, 0, mixPtr, IntPtr.Zero);
            }
            if (hr != 0)
                throw new InvalidOperationException($"loopback 初始化失败 (HRESULT {Wasapi.Describe(hr)})");

            var iid = Wasapi.IidIAudioCaptureClient;
            hr = client.GetService(ref iid, out var svc);
            if (hr != 0 || svc == null)
                throw new InvalidOperationException($"取 IAudioCaptureClient 失败 (HRESULT {Wasapi.Describe(hr)})");
            capture = (IAudioCaptureClient)svc;

            // QPC → UTC 基准：数据包自带 QPC 采集时刻，比 DateTime.UtcNow 准得多，
            // 音画同步全靠这个时间戳与视频帧对齐
            _qpcBase = System.Diagnostics.Stopwatch.GetTimestamp();
            _utcBase = DateTime.UtcNow.Ticks;
            lock (_pending)
            {
                _pending.Clear();
                // 时间线从「现在」开始并立刻按实时推进：系统一开始没放声音时也要输出静音块，
                // 保证 Viewer 端的音频时钟从连接起就是连续可用的
                _pendingStartUtc = _utcBase;
            }

            hr = client.Start();
            if (hr != 0)
                throw new InvalidOperationException($"启动 loopback 流失败 (HRESULT {Wasapi.Describe(hr)})");

            Logger.Info("Audio",
                $"系统声音采集已启动: 设备 {DeviceSampleRate}Hz/{DeviceChannels}ch/{(isFloat ? "float32" : "int16")} " +
                $"→ 输出 {AudioStreamInfo.SampleRate}Hz/{AudioStreamInfo.Channels}ch/int16，块长 {AudioStreamInfo.ChunkMs}ms");

            _ready?.Set();
            CaptureLoop(capture, _cts!.Token);
        }
        catch (Exception ex)
        {
            _initError = ex;
            try { _ready?.Set(); } catch { }
        }
        finally
        {
            if (client != null) { try { client.Stop(); } catch { } }
            if (mixPtr != IntPtr.Zero) Marshal.FreeCoTaskMem(mixPtr);
        }
    }

    private void CaptureLoop(IAudioCaptureClient capture, CancellationToken ct)
    {
        var chunkSamples = AudioStreamInfo.ChunkFrames * AudioStreamInfo.Channels;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    ReadPackets(capture);
                }
                catch (Exception ex)
                {
                    Logger.Warn("Audio", $"读取 loopback 数据包异常（继续）: {ex.Message}");
                }

                var emitted = 0;
                lock (_pending)
                {
                    while (_pending.Count >= chunkSamples)
                    {
                        var data = new short[chunkSamples];
                        _pending.CopyTo(0, data, 0, chunkSamples);
                        _pending.RemoveRange(0, chunkSamples);
                        var ts = _pendingStartUtc;
                        _pendingStartUtc += AudioStreamInfo.ChunkTicks;
                        var silence = IsSilent(data);
                        if (silence) Interlocked.Increment(ref _silenceChunks);
                        else Interlocked.Increment(ref _capturedChunks);
                        _peakLevel = silence ? 0f : PcmConvert.RmsLevel(data);
                        SafeEmit(new PcmChunk
                        {
                            Data = data,
                            Frames = AudioStreamInfo.ChunkFrames,
                            TimestampUtc = ts,
                            IsSilence = silence,
                        });
                        emitted++;
                    }

                    // 静音补齐：流的时间线落后于墙钟就说明设备没在产包（系统静音）
                    if (emitted == 0 && _pendingStartUtc != 0)
                    {
                        var streamEndUtc = _pendingStartUtc +
                                           TicksForOutputFrames(_pending.Count / AudioStreamInfo.Channels);
                        if (DateTime.UtcNow.Ticks - streamEndUtc > AudioStreamInfo.ChunkTicks)
                        {
                            for (var i = _pending.Count; i < chunkSamples; i++) _pending.Add(0);
                        }
                    }
                }

                if (emitted == 0) Thread.Sleep(PollIntervalMs);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Logger.Error("Audio", "系统声音采集线程异常退出", ex);
        }
    }

    /// <summary>
    /// 输出侧帧数 → 100ns 时长。必须先乘后除：48kHz 下「每帧 tick 数」是 208.33，
    /// 预先取整成 208 会带来 0.16% 的时间线漂移，一小时就是 5.7 秒，音画同步会越走越偏。
    /// </summary>
    private static long TicksForOutputFrames(long frames) =>
        frames * TimeSpan.TicksPerSecond / AudioStreamInfo.SampleRate;

    private void SafeEmit(PcmChunk chunk)
    {
        var handler = ChunkArrived;
        if (handler == null) return;
        try { handler(chunk); }
        catch (Exception ex) { Logger.Warn("Audio", "音频块回调异常: " + ex.Message); }
    }

    private static bool IsSilent(short[] data)
    {
        for (var i = 0; i < data.Length; i++)
            if (data[i] != 0) return false;
        return true;
    }

    /// <summary>把设备格式的数据转成 48kHz 立体声 int16 并按时间戳拼进待发射队列</summary>
    private void ReadPackets(IAudioCaptureClient capture)
    {
        while (true)
        {
            var hr = capture.GetNextPacketSize(out var packetFrames);
            if (hr != 0) throw new InvalidOperationException($"GetNextPacketSize 失败 (HRESULT {Wasapi.Describe(hr)})");
            if (packetFrames == 0) return;

            hr = capture.GetBuffer(out var dataPtr, out var framesRead, out var flags, out _, out var qpcPos);
            if (hr != 0) throw new InvalidOperationException($"GetBuffer 失败 (HRESULT {Wasapi.Describe(hr)})");
            try
            {
                if (framesRead == 0) return;

                var sampleCount = (int)(framesRead * DeviceChannels);
                if (_floatScratch.Length < sampleCount) _floatScratch = new float[sampleCount];
                if (_remixScratch.Length < (int)framesRead * 2) _remixScratch = new short[(int)framesRead * 2];

                var silent = (flags & Wasapi.BufferFlagsSilent) != 0 || dataPtr == IntPtr.Zero;
                if (!silent)
                {
                    if (DeviceIsFloat)
                    {
                        Marshal.Copy(dataPtr, _floatScratch, 0, sampleCount);
                    }
                    else
                    {
                        // 少数设备（远端桌面/虚拟声卡）的混音格式是 int16
                        var intScratch = new short[sampleCount];
                        Marshal.Copy(dataPtr, intScratch, 0, sampleCount);
                        for (var i = 0; i < sampleCount; i++) _floatScratch[i] = intScratch[i] * (1f / 32768f);
                    }
                }
                else
                {
                    Array.Clear(_floatScratch, 0, sampleCount);
                }

                var frames = PcmConvert.RemixFloat32ToInt16Stereo(
                    _floatScratch.AsSpan(0, sampleCount), DeviceChannels, _remixScratch);
                if (frames <= 0) return;

                var stereo = _remixScratch.AsSpan(0, frames * 2).ToArray();
                if (DeviceSampleRate != AudioStreamInfo.SampleRate)
                    stereo = PcmConvert.ResampleStereoInt16(stereo, DeviceSampleRate, AudioStreamInfo.SampleRate);

                AppendTimeline(stereo, stereo.Length / 2, QpcToUtc(qpcPos));
            }
            finally
            {
                try { capture.ReleaseBuffer(framesRead); } catch { }
            }
        }
    }

    /// <summary>QPC 时间戳 → DateTime.UtcNow.Ticks（与视频帧同一时钟）</summary>
    private long QpcToUtc(ulong qpc)
    {
        if (qpc == 0) return DateTime.UtcNow.Ticks;
        var delta = ((long)qpc - _qpcBase) * TimeSpan.TicksPerSecond / System.Diagnostics.Stopwatch.Frequency;
        return _utcBase + delta;
    }

    /// <summary>
    /// 把一段真实采样按时间线拼接：与已排队数据的末尾比对，出现空洞就补静音，
    /// 保证输出流的相邻样本在时间上严格连续（否则 Viewer 端音画同步会整体漂移）。
    /// </summary>
    private void AppendTimeline(short[] stereo, int frames, long utc)
    {
        lock (_pending)
        {
            if (_pending.Count > 0)
            {
                var expectedEnd = _pendingStartUtc + TicksForOutputFrames(_pending.Count / AudioStreamInfo.Channels);
                var gap = utc - expectedEnd;
                if (gap > GapThresholdTicks)
                {
                    var gapFrames = (int)Math.Min(MaxGapFrames,
                        gap * AudioStreamInfo.SampleRate / TimeSpan.TicksPerSecond);
                    for (var i = 0; i < gapFrames * AudioStreamInfo.Channels; i++) _pending.Add(0);
                }
            }
            _pending.AddRange(stereo);
        }
    }
}
