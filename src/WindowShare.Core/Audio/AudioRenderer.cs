using System.Runtime.InteropServices;
using WindowShare.Core.Logging;

namespace WindowShare.Core.Audio;

/// <summary>
/// Viewer 端音频播放（WASAPI 共享模式渲染 + 抖动缓冲）。
///
/// 三个职责：
///   1) 把解码出的 48 kHz 立体声 int16 转成设备混音格式并连续播放（网络抖动由缓冲吸收）；
///   2) 对外提供「音频播放时钟」——当前正在从扬声器出来的采样点对应的采集时间戳，
///      这是整个音画同步的主时钟（见 <see cref="AvSyncClock"/>）；
///   3) 缓冲耗尽时重新蓄水（rebuffer）而不是硬撑，避免以几毫秒为单位反复卡顿把声音打成马达声。
///
/// 用设备混音格式渲染而不是自己指定格式：共享模式下 WASAPI 只接受混音格式，
/// 指定别的会直接返回 AUDCLNT_E_UNSUPPORTED_FORMAT。
/// </summary>
public sealed class AudioRenderer : IDisposable
{
    /// <summary>抖动缓冲目标（毫秒）。LAN 直连抖动很小，120ms 足够吸收，延迟也可接受。</summary>
    public const int DefaultTargetLatencyMs = 120;

    /// <summary>缓冲上限（毫秒）：超过就丢最旧的，防止网络积压让声音越拖越后</summary>
    public const int MaxBufferMs = 800;

    private readonly int _sourceSampleRate;
    private readonly int _targetLatencyFrames;
    private readonly int _maxBufferFrames;

    private readonly object _gate = new();
    private readonly Queue<RenderChunk> _queue = new();
    private readonly List<(long FrameIdx, long Utc)> _marks = new();
    private int _queuedFrames;
    private long _writtenFrames;      // 已交给设备的总帧数
    private long _lastPadding;        // 最近一次读到的设备内待播帧数
    private bool _rebuffering = true;
    private bool _deviceStarted;
    private long _underruns;
    private bool _inUnderrun;
    private float _level;

    private IAudioClient? _client;
    private IAudioRenderClient? _render;
    private IntPtr _mixPtr;
    private WaveFormatEx _mixFormat;
    private bool _mixIsFloat;
    private uint _bufferSize;
    private Thread? _thread;
    private CancellationTokenSource? _cts;
    private volatile bool _running;
    private float[] _floatScratch = new float[0];
    private short[] _intScratch = new short[0];

    // 自适应抖动缓冲（v1.5.1）：慢性欠载说明「到达间隔 > 当前缓冲」，按 40ms 步进自动加深（上限 +200ms），
    // 连续 30s 健康后按 20ms 步进回落到用户设定值。低延迟档（40ms）遇到突发到达不再反复耗尽。
    private long _adaptiveExtraFrames;
    private long _lastUnderrunTicks;

    private int EffectiveTargetFrames =>
        _targetLatencyFrames + (int)Interlocked.Read(ref _adaptiveExtraFrames);

    public AudioRenderer(int sourceSampleRate = AudioStreamInfo.SampleRate,
                         int targetLatencyMs = DefaultTargetLatencyMs)
    {
        _sourceSampleRate = sourceSampleRate;
        _targetLatencyFrames = Math.Max(1, sourceSampleRate * targetLatencyMs / 1000);
        _maxBufferFrames = Math.Max(_targetLatencyFrames * 2, sourceSampleRate * MaxBufferMs / 1000);
    }

    /// <summary>设备混音格式的采样率（可能与流采样率不同，内部自动重采样）</summary>
    public int DeviceSampleRate { get; private set; }
    /// <summary>设备混音格式的声道数</summary>
    public int DeviceChannels { get; private set; }
    /// <summary>是否正在正常播放（蓄满水且未处于重灌状态）</summary>
    public bool IsPlaying => _deviceStarted && !_rebuffering;
    /// <summary>当前缓冲深度（毫秒）</summary>
    public int BufferedMs
    {
        get { lock (_gate) return (int)((long)_queuedFrames * 1000 / Math.Max(1, _sourceSampleRate)); }
    }
    /// <summary>缓冲耗尽次数</summary>
    public long Underruns => Interlocked.Read(ref _underruns);
    /// <summary>最近一块的 RMS 电平（0..1，UI 显示音量）</summary>
    public float Level => _level;

    /// <summary>
    /// 打开默认播放设备并启动渲染线程。失败抛异常，调用方据此降级为「只播视频」。
    /// </summary>
    public void Start()
    {
        if (_running) return;

        // IMMDevice 只在激活 IAudioClient 时需要，用局部变量即可（RCW 由 GC 释放）
        var device = Wasapi.GetDefaultRenderDevice();
        _client = Wasapi.ActivateAudioClient(device);
        var hr = _client.GetMixFormat(out _mixPtr);
        if (hr != 0 || _mixPtr == IntPtr.Zero)
            throw new InvalidOperationException($"取播放设备混音格式失败 (HRESULT {Wasapi.Describe(hr)})");

        _mixFormat = Marshal.PtrToStructure<WaveFormatEx>(_mixPtr);
        _mixIsFloat = _mixFormat.IsFloat(_mixPtr);
        DeviceSampleRate = (int)_mixFormat.SamplesPerSec;
        DeviceChannels = _mixFormat.Channels;
        if (DeviceChannels < 1 || DeviceSampleRate < 8000)
            throw new InvalidOperationException($"播放设备格式异常: {DeviceSampleRate}Hz/{DeviceChannels}ch");
        if (!_mixIsFloat && _mixFormat.BitsPerSample != 16)
            throw new InvalidOperationException($"播放设备混音格式不支持: {_mixFormat.BitsPerSample}bit 整型");

        _client.GetDevicePeriod(out var defaultPeriod, out _);
        var bufferDuration = Math.Max(defaultPeriod * 4, TimeSpan.TicksPerMillisecond * 100);
        hr = _client.Initialize(Wasapi.ShareModeShared, 0, bufferDuration, 0, _mixPtr, IntPtr.Zero);
        if (hr == Wasapi.AudclntEBufferSizeNotAligned)
        {
            // 缓冲区必须按设备周期对齐：用上次请求取到的帧数换算后重试一次（MSDN 推荐做法）
            _client.GetBufferSize(out var aligned);
            bufferDuration = (long)(aligned * TimeSpan.TicksPerSecond / DeviceSampleRate);
            hr = _client.Initialize(Wasapi.ShareModeShared, 0, bufferDuration, 0, _mixPtr, IntPtr.Zero);
        }
        if (hr != 0)
            throw new InvalidOperationException($"初始化播放设备失败 (HRESULT {Wasapi.Describe(hr)})");

        _client.GetBufferSize(out _bufferSize);
        var iid = Wasapi.IidIAudioRenderClient;
        hr = _client.GetService(ref iid, out var svc);
        if (hr != 0 || svc == null)
            throw new InvalidOperationException($"取 IAudioRenderClient 失败 (HRESULT {Wasapi.Describe(hr)})");
        _render = (IAudioRenderClient)svc;

        _cts = new CancellationTokenSource();
        _running = true;
        _thread = new Thread(RenderLoop) { IsBackground = true, Name = "AudioRender", Priority = ThreadPriority.AboveNormal };
        _thread.Start();

        Logger.Info("Audio",
            $"音频播放已启动: 设备 {DeviceSampleRate}Hz/{DeviceChannels}ch/{(_mixIsFloat ? "float32" : "int16")}, " +
            $"流 {_sourceSampleRate}Hz/{AudioStreamInfo.Channels}ch, " +
            $"抖动缓冲 {_targetLatencyFrames * 1000 / _sourceSampleRate}ms, 设备缓冲 {_bufferSize} 帧");
    }

    /// <summary>入队一段解码后的 PCM（网络线程调用，非阻塞）</summary>
    public void Enqueue(DecodedAudioChunk chunk)
    {
        if (!_running || chunk.Frames <= 0 || chunk.Data.Length < chunk.Frames * chunk.Channels) return;

        // 声道数不是立体声时先重混（正常不会走到：流格式固定为 48kHz/2ch）
        var stereo = chunk.Channels == AudioStreamInfo.Channels
            ? chunk.Data
            : DownmixToStereo(chunk);

        var atDeviceRate = chunk.SampleRate == DeviceSampleRate
            ? stereo
            : PcmConvert.ResampleStereoInt16(stereo, chunk.SampleRate, DeviceSampleRate);
        var frames = atDeviceRate.Length / 2;
        if (frames <= 0) return;

        RenderChunk render;
        if (_mixIsFloat)
        {
            var data = new float[frames * DeviceChannels];
            PcmConvert.StereoInt16ToFloat32(atDeviceRate, frames, data, DeviceChannels);
            render = RenderChunk.FromFloat(data, frames, chunk.TimestampUtc, DeviceSampleRate);
        }
        else
        {
            var data = new short[frames * DeviceChannels];
            PcmConvert.StereoInt16ToInt16(atDeviceRate, frames, data, DeviceChannels);
            render = RenderChunk.FromInt16(data, frames, chunk.TimestampUtc, DeviceSampleRate);
        }

        _level = PcmConvert.RmsLevel(stereo.AsSpan(0, Math.Min(stereo.Length, frames * 2)));

        lock (_gate)
        {
            _queue.Enqueue(render);
            _queuedFrames += frames;
            // 缓冲超上限：丢最旧的，保住实时性（宁可少听一段，也不能越拖越后）
            while (_queuedFrames > _maxBufferFrames && _queue.Count > 1)
            {
                var dropped = _queue.Dequeue();
                _queuedFrames -= dropped.Frames;
            }
        }
    }

    private static short[] DownmixToStereo(DecodedAudioChunk chunk)
    {
        var frames = chunk.Frames;
        var result = new short[frames * 2];
        if (chunk.Channels == 1)
        {
            for (var i = 0; i < frames; i++)
            {
                result[i * 2] = chunk.Data[i];
                result[i * 2 + 1] = chunk.Data[i];
            }
        }
        else
        {
            for (var i = 0; i < frames; i++)
            {
                var b = i * chunk.Channels;
                result[i * 2] = chunk.Data[b];
                result[i * 2 + 1] = chunk.Data.Length > b + 1 ? chunk.Data[b + 1] : chunk.Data[b];
            }
        }
        return result;
    }

    /// <summary>
    /// 音频播放时钟：当前正在播放的采样点对应的采集时间戳。
    /// 返回 null 表示还没起播（此时 Viewer 应立即上屏视频，不做同步）。
    /// </summary>
    public long? GetPlaybackUtcTicks()
    {
        lock (_gate)
        {
            if (!_deviceStarted || _marks.Count == 0) return null;
            var played = Math.Max(0, _writtenFrames - _lastPadding);

            for (var i = _marks.Count - 1; i >= 0; i--)
            {
                if (_marks[i].FrameIdx <= played)
                    return _marks[i].Utc + TicksForDeviceFrames(played - _marks[i].FrameIdx);
            }
            return _marks[0].Utc;
        }
    }

    /// <summary>
    /// 设备帧数 → 100ns 时长。必须先乘后除：预先算「每帧 tick 数」会截断
    /// （44.1kHz 下 226.76 → 226，误差 0.33%），播放时钟会持续走慢，
    /// 音画同步跟着越拖越偏。
    /// </summary>
    private long TicksForDeviceFrames(long frames) =>
        frames * TimeSpan.TicksPerSecond / Math.Max(1, DeviceSampleRate);

    /// <summary>清空缓冲（重连/Host 重启音频时调用，避免用陈旧数据起播）</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _queue.Clear();
            _marks.Clear();
            _queuedFrames = 0;
            _writtenFrames = 0;
            _lastPadding = 0;
            _rebuffering = true;
            _inUnderrun = false;
        }
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
        try { _client?.Stop(); } catch { }
        try { _client?.Reset(); } catch { }
        lock (_gate)
        {
            _queue.Clear();
            _marks.Clear();
            _queuedFrames = 0;
            _deviceStarted = false;
            _rebuffering = true;
        }
        _cts?.Dispose();
        _cts = null;
    }

    public void Dispose()
    {
        Stop();
        if (_mixPtr != IntPtr.Zero)
        {
            Marshal.FreeCoTaskMem(_mixPtr);
            _mixPtr = IntPtr.Zero;
        }
    }

    // ===== 渲染线程 =====

    private void RenderLoop()
    {
        var ct = _cts!.Token;
        try
        {
            while (!ct.IsCancellationRequested && _running)
            {
                var hr = _client!.GetCurrentPadding(out var pad);
                if (hr != 0)
                {
                    if (hr == Wasapi.AudclntEDeviceInvalidated)
                    {
                        Logger.Warn("Audio", "播放设备失效（被拔出/切换），音频停止");
                        break;
                    }
                    Thread.Sleep(5);
                    continue;
                }
                lock (_gate) _lastPadding = pad;

                if (!_deviceStarted || _rebuffering)
                {
                    int queued;
                    lock (_gate) queued = _queuedFrames;
                    if (queued < EffectiveTargetFrames) { Thread.Sleep(3); continue; }

                    if (!_deviceStarted)
                    {
                        hr = _client.Start();
                        if (hr != 0)
                        {
                            Logger.Warn("Audio", $"启动播放设备失败 (HRESULT {Wasapi.Describe(hr)})");
                            Thread.Sleep(20);
                            continue;
                        }
                        _deviceStarted = true;
                        Logger.Info("Audio", $"音频起播（蓄水 {queued * 1000 / _sourceSampleRate}ms）");
                    }
                    lock (_gate) _rebuffering = false;
                }

                var available = (int)_bufferSize - (int)pad;
                if (available <= 0) { Thread.Sleep(2); continue; }

                int queuedNow;
                lock (_gate) queuedNow = _queuedFrames;
                if (queuedNow <= 0)
                {
                    // 缓冲耗尽：重新蓄水，而不是以几毫秒为单位反复卡顿
                    if (!_inUnderrun)
                    {
                        _inUnderrun = true;
                        Interlocked.Increment(ref _underruns);
                        _lastUnderrunTicks = Environment.TickCount64;
                        // 自适应加深：步进 40ms，上限 +200ms（v1.5.1）
                        var extra = Interlocked.Read(ref _adaptiveExtraFrames);
                        var step = _sourceSampleRate * 40 / 1000;
                        var cap = _sourceSampleRate * 200 / 1000;
                        if (extra < cap)
                            Interlocked.Exchange(ref _adaptiveExtraFrames, Math.Min(extra + step, cap));
                        Logger.Warn("Audio",
                            $"音频缓冲耗尽（第 {Underruns} 次），重新蓄水 {EffectiveTargetFrames * 1000 / _sourceSampleRate}ms" +
                            $"（自适应缓冲 +{Interlocked.Read(ref _adaptiveExtraFrames) * 1000 / _sourceSampleRate}ms）");
                    }
                    lock (_gate) _rebuffering = true;
                    Thread.Sleep(3);
                    continue;
                }
                _inUnderrun = false;

                if (!WriteFrames(Math.Min(available, queuedNow))) Thread.Sleep(3);
                else
                {
                    // 健康回落：连续 30s 无欠载就逐步回落到用户设定的目标缓冲（步进 20ms）
                    var extra = Interlocked.Read(ref _adaptiveExtraFrames);
                    if (extra > 0 && Environment.TickCount64 - _lastUnderrunTicks > 30_000)
                    {
                        var step = _sourceSampleRate * 20 / 1000;
                        Interlocked.Exchange(ref _adaptiveExtraFrames, Math.Max(0, extra - step));
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Logger.Error("Audio", "音频渲染线程异常退出", ex);
        }
    }

    /// <summary>从抖动缓冲取 want 帧写进设备；返回是否成功写入</summary>
    private bool WriteFrames(int want)
    {
        // 先在锁内把数据拼进复用的 scratch，再在锁外调 GetBuffer/ReleaseBuffer（COM 调用不宜持锁）
        long markFrameIdx;
        long markUtc;
        int actual;

        lock (_gate)
        {
            var sampleCount = want * DeviceChannels;
            var assembledFloat = _mixIsFloat ? EnsureFloat(sampleCount) : null;
            var assembledInt = _mixIsFloat ? null : EnsureInt(sampleCount);

            var written = 0;
            var firstUtc = 0L;
            while (written < want && _queue.Count > 0)
            {
                var head = _queue.Peek();
                var take = Math.Min(want - written, head.Frames - head.Offset);
                if (written == 0) firstUtc = head.UtcFor(head.Offset);

                if (_mixIsFloat)
                    Array.Copy(head.Float!, head.Offset * DeviceChannels, assembledFloat!,
                        written * DeviceChannels, take * DeviceChannels);
                else
                    Array.Copy(head.Short!, head.Offset * DeviceChannels, assembledInt!,
                        written * DeviceChannels, take * DeviceChannels);

                head.Offset += take;
                written += take;
                if (head.Offset >= head.Frames) _queue.Dequeue();
            }

            if (written == 0) return false;
            actual = written;
            _queuedFrames -= written;
            markFrameIdx = _writtenFrames;
            markUtc = firstUtc;
            _writtenFrames += written;
            _marks.Add((markFrameIdx, markUtc));

            // 只保留还没播过去的标记，避免长时间运行内存增长
            var played = Math.Max(0, _writtenFrames - _lastPadding);
            while (_marks.Count > 2 && _marks[1].FrameIdx <= played) _marks.RemoveAt(0);
        }

        var hr = _render!.GetBuffer((uint)actual, out var ptr);
        if (hr != 0 || ptr == IntPtr.Zero)
        {
            Logger.Warn("Audio", $"GetBuffer({actual}) 失败 (HRESULT {Wasapi.Describe(hr)})");
            return false;
        }
        try
        {
            if (_mixIsFloat) Marshal.Copy(_floatScratch, 0, ptr, actual * DeviceChannels);
            else Marshal.Copy(_intScratch, 0, ptr, actual * DeviceChannels);
        }
        finally
        {
            _render.ReleaseBuffer((uint)actual, 0);
        }
        return true;
    }

    private float[] EnsureFloat(int count)
    {
        if (_floatScratch.Length < count) _floatScratch = new float[count];
        return _floatScratch;
    }

    private short[] EnsureInt(int count)
    {
        if (_intScratch.Length < count) _intScratch = new short[count];
        return _intScratch;
    }

    /// <summary>一段已转成设备格式、待写入设备的音频</summary>
    private sealed class RenderChunk
    {
        public float[]? Float { get; }
        public short[]? Short { get; }
        public int Frames { get; }
        public int Offset;

        private readonly long _utc;
        private readonly int _rate;

        private RenderChunk(float[]? data, short[]? intData, int frames, long utc, int rate)
        {
            Float = data;
            Short = intData;
            Frames = frames;
            _utc = utc;
            _rate = rate;
        }

        public static RenderChunk FromFloat(float[] data, int frames, long utc, int rate)
            => new(data, null, frames, utc, rate);

        public static RenderChunk FromInt16(short[] data, int frames, long utc, int rate)
            => new(null, data, frames, utc, rate);

        /// <summary>
        /// 块内偏移 offset 帧处的采集时间戳。重采样不改变时长，
        /// 因此这里的 offset 用「设备采样率」换算秒数即可，与源采样率无关。
        /// </summary>
        public long UtcFor(int offset) =>
            _utc + (long)(offset * (double)TimeSpan.TicksPerSecond / Math.Max(1, _rate));
    }
}
