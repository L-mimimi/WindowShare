using System.Runtime.InteropServices;
using SharpGen.Runtime;
using Vortice.MediaFoundation;
using WindowShare.Core.Logging;

namespace WindowShare.Core.Decoding;

/// <summary>解码输出帧（BGRA，可直接写 WPF WriteableBitmap）</summary>
public sealed class DecodedVideoFrame
{
    public required byte[] Bgra { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    /// <summary>捕获时间戳（Host 侧生成，随帧头传输）</summary>
    public required long TimestampUtc { get; init; }
}

/// <summary>
/// Media Foundation H.264 解码器（同步 MFT，系统自带 "Microsoft H264 Video Decoder MFT"）：
///   - 输入：H.264 Annex-B 帧序列（一个访问单元一次调用）；
///   - 输出：NV12 → CPU 转 BGRA 回调（Viewer 显示用）；
///   - 自动处理首帧 STREAM_CHANGE（SPS/PPS 解析出分辨率）。
/// </summary>
public sealed class MfH264Decoder : IDisposable
{
    private static readonly Guid TransformIid = new("bf94c121-5b05-4e6f-8000-ba598961414d");

    /// <summary>
    /// MF_LOW_LATENCY（与 CODECAPI_AVLowLatencyMode 同一 GUID），设在 MFT 自身的属性存储上。
    /// 不要用 ICodecAPI 在系统 H.264 解码器上设同名属性：实测会把 CLR 打崩（0x80131506）。
    /// </summary>
    private static readonly Guid LowLatencyKey = new("9c27891a-ed7a-40e1-88e8-b22727a024ee");

    private const int MfETransformNeedMoreInput = unchecked((int)0xC00D6D72);
    private const int MfETransformStreamChange = unchecked((int)0xC00D6D61);
    private const int MfETransformTypeNotSet = unchecked((int)0xC00D6D60);
    /// <summary>单次抽干内允许的输出类型重协商次数（TYPE_NOT_SET 反复出现时不至于死循环）</summary>
    private const int MaxRenegotiationsPerDrain = 3;

    private static readonly object MfGate = new();
    private static int _mfRefCount;

    private readonly IMFTransform _transform;
    private readonly object _gate = new();
    private bool _disposed;
    private long _decodedFrames;
    private bool _eosSent;
    private bool _outputTypeSet;
    private int _droppedUnknownSize;
    private long _inputFrames;
    private long _outputSamples;
    private int _droppedShortBuffer;

    /// <summary>解码输出（解码线程上触发）</summary>
    public event Action<DecodedVideoFrame>? Decoded;

    /// <summary>解码输出宽高（首个输出后可知）</summary>
    public int OutputWidth { get; private set; }
    public int OutputHeight { get; private set; }

    public long DecodedFrames => Interlocked.Read(ref _decodedFrames);

    /// <summary>因输出分辨率尚未确定而被丢弃的帧数（诊断用）</summary>
    public int DroppedUnknownSize => Volatile.Read(ref _droppedUnknownSize);

    /// <summary>已投喂的码流帧数（诊断用）</summary>
    public long InputFrames => Interlocked.Read(ref _inputFrames);

    /// <summary>解码器实际吐出的输出样本数（诊断用；与 <see cref="DecodedFrames"/> 之差即转换阶段丢弃数）</summary>
    public long OutputSamples => Interlocked.Read(ref _outputSamples);

    /// <summary>因输出缓冲长度不足而被丢弃的帧数（诊断用）</summary>
    public int DroppedShortBuffer => Volatile.Read(ref _droppedShortBuffer);

    public MfH264Decoder()
    {
        lock (MfGate)
        {
            if (_mfRefCount++ == 0)
            {
                try { MediaFactory.MFStartup(false); } catch { _mfRefCount--; }
            }
        }

        // 枚举 H.264 解码器（同步，系统自带）
        MediaFactory.MFTEnumEx(TransformCategoryGuids.VideoDecoder,
            0x01 | 0x10 | 0x40, // SYNCMFT | LOCALMFT | SORTANDFILTER
            new RegisterTypeInfo { GuidMajorType = MediaTypeGuids.Video, GuidSubtype = VideoFormatGuids.H264 },
            new RegisterTypeInfo { GuidMajorType = MediaTypeGuids.Video, GuidSubtype = VideoFormatGuids.NV12 },
            out var ptrs, out var count);
        if (count == 0)
        {
            Marshal.FreeCoTaskMem(ptrs);
            throw new InvalidOperationException("未找到 H.264 解码器 MFT");
        }

        var p = Marshal.ReadIntPtr(ptrs, 0 * IntPtr.Size);
        var activate = new IMFActivate(p);
        Marshal.FreeCoTaskMem(ptrs);
        activate.ActivateObject(out IMFTransform? transform).CheckError();
        activate.Dispose();
        _transform = transform ?? throw new InvalidOperationException("H.264 解码器 MFT 激活返回空对象");

        // 低延迟解码：MF_LOW_LATENCY 走 MFT 自身的属性存储。
        // 不要用 ICodecAPI 设同名属性——系统 H.264 解码器上会把 CLR 打崩（0x80131506）。
        var lowLatency = false;
        try
        {
            var attrs = _transform.Attributes;
            attrs?.Set(LowLatencyKey, 1u);
            lowLatency = true;
        }
        catch (Exception ex)
        {
            Logging.Logger.Debug("Decoder", "低延迟属性设置失败: " + ex.Message);
        }

        // 输入类型：从 MFT 枚举的可用类型中挑选 H264（解码器规范：先输入类型，后输出类型）
        var inType = PickAvailableType(isInput: true, VideoFormatGuids.H264)
                     ?? throw new InvalidOperationException("H.264 解码器未提供可用的输入媒体类型");
        _transform.SetInputType(0, inType, 0);
        inType.Dispose();

        // 输出类型：故意不在这里设置。
        // 投喂前解码器只能给出「默认 1920x1080」这类猜的分辨率；一旦按它设了输出类型，
        // 解码器会先攒满 28 帧内部缓冲才报 STREAM_CHANGE，换类型时那 28 帧被直接丢弃
        //（实测：观看者接入后约 1 秒黑屏，冒烟测试固定少解 28 帧）。
        // 留空后首个 ProcessOutput 会返回 TYPE_NOT_SET / STREAM_CHANGE，此时按真实分辨率协商；
        // 再配合上面的低延迟模式，实测投喂多少帧就解出多少帧（零丢帧）。
        _outputTypeSet = false;

        _transform.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, UIntPtr.Zero);
        _transform.ProcessMessage(TMessageType.MessageNotifyStartOfStream, UIntPtr.Zero);
        Logging.Logger.Info("Decoder", $"H.264 解码器已就绪（低延迟={(lowLatency ? "开" : "关")}）");
    }

    /// <summary>
    /// 从 MFT 枚举的可用类型中挑选指定子类型。
    /// requireFrameSize=true 时只接受带非零分辨率的类型（解码器解析出 SPS 后才有）。
    /// </summary>
    private IMFMediaType? PickAvailableType(bool isInput, Guid subtype, bool requireFrameSize = false)
    {
        for (var i = 0; i < 32; i++)
        {
            IMFMediaType? t = null;
            try
            {
                t = isInput ? _transform.GetInputAvailableType(0, i) : _transform.GetOutputAvailableType(0, i);
            }
            catch (SharpGenException)
            {
                return null;
            }
            try
            {
                if (t.GetGUID(MediaTypeAttributeKeys.Subtype) == subtype)
                {
                    if (!requireFrameSize) return t;
                    try
                    {
                        var size = t.GetUInt64(MediaTypeAttributeKeys.FrameSize);
                        if ((int)(size >> 32) > 0 && (int)(size & 0xFFFFFFFF) > 0) return t;
                    }
                    catch { }
                }
            }
            catch { }
            t.Dispose();
        }
        return null;
    }

    /// <summary>NV12 占位类型（枚举失败时的兜底）</summary>
    private IMFMediaType CreateNv12Placeholder()
    {
        var t = MediaFactory.MFCreateMediaType();
        t.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
        t.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.NV12);
        return t;
    }

    /// <summary>送入一帧 Annex-B 码流并抽干所有可用输出</summary>
    public void Decode(byte[] annexB, long timestampUtc)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(MfH264Decoder));

        lock (_gate)
        {
            // Flush() 之后继续投喂：重新进入流状态，解码器可复用（换源/重连）
            if (_eosSent)
            {
                try
                {
                    _transform.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, UIntPtr.Zero);
                    _transform.ProcessMessage(TMessageType.MessageNotifyStartOfStream, UIntPtr.Zero);
                }
                catch { }
                _eosSent = false;
            }

            var buffer = MediaFactory.MFCreateMemoryBuffer(annexB.Length);
            buffer.Lock(out var data, out _, out _);
            Marshal.Copy(annexB, 0, data, annexB.Length);
            buffer.Unlock();
            buffer.CurrentLength = annexB.Length;

            var sample = MediaFactory.MFCreateSample();
            sample.AddBuffer(buffer);
            buffer.Dispose();
            sample.SampleTime = timestampUtc;
            sample.SampleDuration = 0;

            try
            {
                _transform.ProcessInput(0, sample, 0);
            }
            catch (SharpGenException ex) when (ex.HResult == MfETransformStreamChange)
            {
                // 输入流格式变化：重设输出类型后继续
                Logging.Logger.Info("Decoder", "输入流格式变化");
            }
            catch (SharpGenException ex) when (!_outputTypeSet)
            {
                // 个别 MFT 要求先有输出类型才收帧：退回不带分辨率的 NV12 占位类型再投一次，
                // 之后仍会在 ProcessOutput 阶段按真实分辨率重新协商
                Logging.Logger.Debug("Decoder", $"未设输出类型时投喂被拒（0x{ex.HResult:X8}），回退占位类型");
                var fallback = PickAvailableType(isInput: false, VideoFormatGuids.NV12) ?? CreateNv12Placeholder();
                try
                {
                    _transform.SetOutputType(0, fallback, 0);
                    _outputTypeSet = true;
                }
                catch { }
                fallback.Dispose();
                try { _transform.ProcessInput(0, sample, 0); }
                catch (Exception retryEx)
                {
                    Logging.Logger.Debug("Decoder", "回退后重试投喂仍失败: " + retryEx.Message);
                }
            }
            finally
            {
                sample.Dispose();
            }

            Interlocked.Increment(ref _inputFrames);
            DrainOutputs(timestampUtc);
        }
    }

    private void DrainOutputs(long timestampUtc)
    {
        // 输出类型重协商次数上限（本轮抽干内）
        var renegotiations = 0;
        // 带上限：EOS 抽干时若解码器异常返回，不至于死循环
        for (var guard = 0; guard < 4096; guard++)
        {
            var outBuf = new OutputDataBuffer { StreamID = 0 };
            ProcessOutputStatus status = default;
            Result result;
            try
            {
                // 解码器要求调用方分配输出样本
                var info = _transform.GetOutputStreamInfo(0);
                var size = info.Size > 0 ? info.Size : 8 * 1024 * 1024;
                var outSample = MediaFactory.MFCreateSample();
                var outBuffer = MediaFactory.MFCreateMemoryBuffer(size);
                outSample.AddBuffer(outBuffer);
                outBuffer.Dispose();
                outBuf.Sample = outSample;

                result = _transform.ProcessOutput(ProcessOutputFlags.None, 1, ref outBuf, out var statusOut);
                status = statusOut;
            }
            catch (SharpGenException ex) when (ex.HResult == MfETransformNeedMoreInput)
            {
                outBuf.Sample?.Dispose();
                // 分辨率还没确定时，解码器可能一直「要更多输入」却不出帧：
                // 主动协商一次（此时 SPS 已解析），避免它攒满内部缓冲后把前面的帧全丢掉
                if (OutputWidth <= 0 && renegotiations++ < MaxRenegotiationsPerDrain)
                {
                    RenegotiateOutput();
                    continue;
                }
                return;
            }
            catch (SharpGenException ex) when (ex.HResult == MfETransformStreamChange ||
                                              ex.HResult == MfETransformTypeNotSet)
            {
                outBuf.Sample?.Dispose();
                // 分辨率已确定 → 重新协商输出类型
                if (renegotiations++ >= MaxRenegotiationsPerDrain) return;
                RenegotiateOutput();
                continue;
            }
            catch (Exception)
            {
                outBuf.Sample?.Dispose();
                throw;
            }

            if (result.Failure)
            {
                outBuf.Sample?.Dispose();
                if (result.Code == MfETransformNeedMoreInput)
                {
                    if (OutputWidth <= 0 && renegotiations++ < MaxRenegotiationsPerDrain)
                    {
                        RenegotiateOutput();
                        continue;
                    }
                    return;
                }
                if (result.Code == MfETransformStreamChange || result.Code == MfETransformTypeNotSet)
                {
                    if (renegotiations++ >= MaxRenegotiationsPerDrain) return;
                    RenegotiateOutput();
                    continue;
                }
                result.CheckError();
                return;
            }

            var sample = outBuf.Sample;
            if (sample == null) return;
            try
            {
                Interlocked.Increment(ref _outputSamples);
                Emit(sample, timestampUtc);
            }
            finally
            {
                sample.Dispose();
            }

            // FORMAT_CHANGED（0x2000000）：本样本有效，但输出格式已变化
            if (((int)status & 0x2000000) != 0)
            {
                RenegotiateOutput();
                continue;
            }
        }
    }

    /// <summary>STREAM_CHANGE（或分辨率未知）时重新协商输出类型，取得实际分辨率</summary>
    private void RenegotiateOutput()
    {
        // STREAM_CHANGE 时 MFT 的可用输出类型携带真实分辨率，优先挑带 FrameSize 的那个
        //（裸占位类型会被拒绝，且会让 OutputWidth 一直是 0）
        var outType = PickAvailableType(isInput: false, VideoFormatGuids.NV12, requireFrameSize: true)
                      ?? PickAvailableType(isInput: false, VideoFormatGuids.NV12)
                      ?? CreateNv12Placeholder();
        ulong typeSize = 0;
        try { typeSize = outType.GetUInt64(MediaTypeAttributeKeys.FrameSize); } catch { }
        try { _transform.SetOutputType(0, outType, 0); }
        catch (Exception ex) { Logging.Logger.Debug("Decoder", "重设输出类型失败: " + ex.Message); }
        outType.Dispose();
        _outputTypeSet = true;

        // 当前输出类型读不到分辨率时，退回刚才那份类型上的 FrameSize
        if (!TryReadOutputSize() && typeSize != 0)
        {
            OutputWidth = (int)(typeSize >> 32);
            OutputHeight = (int)(typeSize & 0xFFFFFFFF);
            if (OutputWidth > 0)
                Logging.Logger.Info("Decoder", $"解码输出分辨率: {OutputWidth}x{OutputHeight}");
        }
    }

    /// <summary>从「当前输出类型」读取分辨率（不重设类型）；读到有效值返回 true</summary>
    private bool TryReadOutputSize()
    {
        try
        {
            using var cur = _transform.GetOutputCurrentType(0);
            if (cur == null) return false;
            var size = cur.GetUInt64(MediaTypeAttributeKeys.FrameSize);
            // MF_MT_FRAME_SIZE：高 32 位=宽，低 32 位=高
            var w = (int)(size >> 32);
            var h = (int)(size & 0xFFFFFFFF);
            if (w <= 0 || h <= 0) return false;
            if (w != OutputWidth || h != OutputHeight)
            {
                OutputWidth = w;
                OutputHeight = h;
                Logging.Logger.Info("Decoder", $"解码输出分辨率: {w}x{h}");
            }
            return true;
        }
        catch { return false; }
    }

    private void Emit(IMFSample sample, long fallbackTimestamp)
    {
        using var buffer = sample.ConvertToContiguousBuffer();
        buffer.Lock(out var ptr, out _, out var length);
        try
        {
            if (OutputWidth <= 0)
            {
                // 首个输出样本时「当前输出类型」可能还没带上分辨率：此时直接丢帧会让观看者
                // 一直黑屏到下一个 IDR（实测约 1.3 秒 / 30 帧），因此主动补齐分辨率。
                if (!TryReadOutputSize()) RenegotiateOutput();
            }
            if (length <= 0 || OutputWidth <= 0)
            {
                if (Interlocked.Increment(ref _droppedUnknownSize) == 1)
                    Logging.Logger.Warn("Decoder", "输出分辨率未知，暂时丢弃解码帧（补齐后自动恢复）");
                return;
            }
            var w = OutputWidth;
            var h = OutputHeight;
            var needed = w * h * 3 / 2;
            if (length < needed)
            {
                // 数据不完整，丢弃（首次记一条，便于定位输出缓冲分配过小）
                if (Interlocked.Increment(ref _droppedShortBuffer) == 1)
                    Logging.Logger.Warn("Decoder", $"输出缓冲不足：{length} < {needed}（{w}x{h} NV12），丢弃该帧");
                return;
            }

            var nv12 = new byte[needed];
            Marshal.Copy(ptr, nv12, 0, needed);
            var bgra = new byte[w * h * 4];
            Utils.Nv12ToBgra.Convert(nv12, w, h, bgra);

            Interlocked.Increment(ref _decodedFrames);
            Decoded?.Invoke(new DecodedVideoFrame
            {
                Bgra = bgra,
                Width = w,
                Height = h,
                TimestampUtc = sample.SampleTime != 0 ? sample.SampleTime : fallbackTimestamp,
            });
        }
        finally
        {
            try { buffer.Unlock(); } catch { }
        }
    }

    /// <summary>
    /// 通知解码器码流结束并抽干内部滞留的输出帧，返回本次新增的解码帧数。
    /// H.264 解码器会滞留若干帧凑参考，停止投喂后它们不会自己出来；
    /// 断开连接 / 停止共享 / 统计收尾时调用可把尾部画面补齐。
    /// 之后仍可继续 <see cref="Decode"/>（会自动重新进入流状态）。
    /// </summary>
    public int Flush()
    {
        if (_disposed) return 0;
        lock (_gate)
        {
            var before = Interlocked.Read(ref _decodedFrames);
            try
            {
                _transform.ProcessMessage(TMessageType.MessageNotifyEndOfStream, UIntPtr.Zero);
                _eosSent = true;
                DrainOutputs(DateTime.UtcNow.Ticks);
            }
            catch (Exception ex)
            {
                Logging.Logger.Warn("Decoder", "抽干滞留输出帧失败: " + ex.Message);
            }
            return (int)(Interlocked.Read(ref _decodedFrames) - before);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        lock (_gate)
        {
            try
            {
                _transform.ProcessMessage(TMessageType.MessageNotifyEndOfStream, UIntPtr.Zero);
                _transform.ProcessMessage(TMessageType.MessageCommandFlush, UIntPtr.Zero);
            }
            catch { }
            _transform.Dispose();
        }
        lock (MfGate)
        {
            if (--_mfRefCount == 0)
            {
                try { MediaFactory.MFShutdown(); } catch { }
            }
        }
    }
}
