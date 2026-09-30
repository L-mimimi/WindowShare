using System.Runtime.InteropServices;
using SharpGen.Runtime;
using Vortice.MediaFoundation;
using WindowShare.Core.Logging;

namespace WindowShare.Core.Audio;

/// <summary>
/// Media Foundation AAC 音频解码器（同步 MFT，系统自带 "Microsoft AAC Audio Decoder MFT"）。
///
/// 输入：ADTS 封装的 AAC 帧（Host 端 <see cref="MfAacEncoder"/> 的产物）。
/// 输出：int16 交织 PCM，交给 <see cref="AudioRenderer"/> 播放。
/// 输出时间戳按「已产出样本数」在输入时间线上定位，供音画同步使用。
/// </summary>
public sealed class MfAacDecoder : IDisposable
{
    private const int MfETransformNeedMoreInput = unchecked((int)0xC00D6D72);
    private const int MfETransformStreamChange = unchecked((int)0xC00D6D61);
    private const int MfETransformTypeNotSet = unchecked((int)0xC00D6D60);
    private const int MfENotAccepting = unchecked((int)0xC00D36B5);
    private const int EInvalidArg = unchecked((int)0x80070057);
    /// <summary>单次抽干内允许的输出类型重协商次数（防止 TYPE_NOT_SET 反复出现时死循环）</summary>
    private const int MaxRenegotiations = 4;
    /// <summary>单次抽干内允许的「输出样本由谁分配」翻转次数：E_INVALIDARG 只翻一次，翻完就记住</summary>
    private const int MaxAllocationFlips = 1;
    /// <summary>单次抽干的轮数上限，纯粹是防跑飞的安全网（正常一轮 = 1024 样本 ≈ 21ms）</summary>
    private const int MaxDrainRounds = 4096;
    /// <summary>读不到 GetOutputStreamInfo 时的兜底输出缓冲区（1024 样本 × 2ch × int16 的 12 倍）</summary>
    private const int DefaultOutputBufferSize = 48 * 1024;

    private readonly IMFTransform _transform;
    private readonly object _gate = new();
    private readonly SampleTimeline _timeline;

    /// <summary>
    /// 输出样本由谁分配，默认 true（调用方分配）。
    ///
    /// 不能只看 MFT_OUTPUT_STREAM_INFO.dwFlags：本机 "Microsoft AAC Audio Decoder MFT" 把
    /// MFT_OUTPUT_STREAM_PROVIDES_SAMPLES(0x1) 报了上来，但传 pSample=NULL 会拿到
    /// E_INVALIDARG(0x80070057)，而且一次失败就永久卡在 MF_E_NOTACCEPTING——之后所有
    /// ProcessInput 全被拒收，Viewer 端一帧 PCM 都出不来。
    /// 按 MF 规范，自带样本的 MFT 会「忽略」传入的 pSample，所以调用方分配对两类 MFT 都安全，
    /// 这里一律先按调用方分配走；万一某个 MFT 反过来只肯自己分配，撞一次 E_INVALIDARG 就翻转并记住。
    /// </summary>
    private bool _callerAllocatesOutput = true;

    /// <summary>输出流 dwFlags 原值（诊断用；每轮抽干重读，变化时记日志）</summary>
    private int _outputStreamFlags;

    /// <summary>输出流建议的缓冲区大小（流变化后会跟着变）</summary>
    private int _outputBufferSize;

    private long _inputSamples;
    private long _outputSamples;
    private long _inputTime100Ns;
    private bool _disposed;

    /// <summary>解码输出（解码调用线程上触发）</summary>
    public event Action<DecodedAudioChunk>? Decoded;

    public string DecoderName { get; }
    public int SampleRate { get; }
    public int Channels { get; }

    public long DecodedChunks { get; private set; }
    public long DecodedSamples { get; private set; }

    /// <summary>解码器拒收（抽干后仍 MF_E_NOTACCEPTING）而丢掉的输入帧数，正常应恒为 0</summary>
    public long DroppedInputFrames { get; private set; }

    /// <summary>输出流 dwFlags 原值（诊断用）</summary>
    public int OutputStreamFlags => _outputStreamFlags;

    /// <summary>输出样本当前是否由调用方分配（诊断用）</summary>
    public bool CallerAllocatesOutput => _callerAllocatesOutput;

    public MfAacDecoder(int sampleRate = AudioStreamInfo.SampleRate, int channels = AudioStreamInfo.Channels)
    {
        MfRuntime.Startup();
        SampleRate = sampleRate;
        Channels = channels;
        _timeline = new SampleTimeline(sampleRate);
        DecoderName = "<unknown>";

        var failures = new List<string>();
        IMFTransform? chosen = null;
        var chosenName = "<unknown>";
        var chosenFlags = 0;
        var chosenSize = 0;

        var activators = MftEnumerator.Enumerate(TransformCategoryGuids.AudioDecoder, MftEnumerator.EnumFlagAll);
        try
        {
            var ordered = activators
                .Select(a => (Activate: a, Name: MftEnumerator.SafeName(a)))
                .OrderByDescending(x => x.Name.Contains("aac", StringComparison.OrdinalIgnoreCase))
                .ToList();

            foreach (var (activate, name) in ordered)
            {
                IMFTransform? transform = null;
                try
                {
                    transform = MftEnumerator.Activate(activate);
                    if (TryConfigure(transform, sampleRate, channels,
                            out var streamFlags, out var streamSize, out var error))
                    {
                        chosen = transform;
                        chosenName = name;
                        chosenFlags = streamFlags;
                        chosenSize = streamSize;
                        break;
                    }
                    failures.Add($"{name}: {error}");
                    transform.Dispose();
                    transform = null;
                }
                catch (Exception ex)
                {
                    failures.Add($"{name}: {ex.Message}");
                    try { transform?.Dispose(); } catch { }
                }
            }
        }
        finally
        {
            foreach (var a in activators) { try { a.Dispose(); } catch { } }
        }

        if (chosen == null)
        {
            MfRuntime.Shutdown();
            throw new InvalidOperationException(
                "本机没有可用的 AAC 音频解码器 MFT：" + string.Join(" | ", failures));
        }

        _transform = chosen;
        DecoderName = chosenName;
        _outputStreamFlags = chosenFlags;
        _outputBufferSize = chosenSize > 0 ? chosenSize : DefaultOutputBufferSize;
        _callerAllocatesOutput = true;
        Logger.Info("Audio",
            $"AAC 解码器就绪: {DecoderName}, 输出 {sampleRate}Hz/{channels}ch int16, " +
            $"输出流 flags=0x{chosenFlags:X}/建议缓冲 {_outputBufferSize} 字节, 输出样本由调用方分配");
    }

    private static bool TryConfigure(IMFTransform transform, int sampleRate, int channels,
        out int outputStreamFlags, out int outputBufferSize, out string error)
    {
        outputStreamFlags = 0;
        outputBufferSize = 0;
        error = string.Empty;

        // ADTS 输入类型：不同版本要求的属性集略有差异，从「最小集」到「补全字节率」依次尝试
        var attempts = new[] { false, true };
        var configured = false;
        foreach (var withByteRate in attempts)
        {
            var inType = MediaFactory.MFCreateMediaType();
            try
            {
                inType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Audio);
                inType.Set(MediaTypeAttributeKeys.Subtype, AudioFormatGuids.Adts);
                inType.Set(MediaTypeAttributeKeys.AudioSamplesPerSecond, (uint)sampleRate);
                inType.Set(MediaTypeAttributeKeys.AudioNumChannels, (uint)channels);
                if (withByteRate)
                {
                    inType.Set(MediaTypeAttributeKeys.AudioBitsPerSample, 16u);
                    inType.Set(MediaTypeAttributeKeys.AudioBlockAlignment, (uint)(channels * 2));
                    inType.Set(MediaTypeAttributeKeys.AudioAvgBytesPerSecond,
                        (uint)AudioStreamInfo.TargetBitrateBps / 8);
                }
                transform.SetInputType(0, inType, 0);
                configured = true;
                break;
            }
            catch (SharpGenException ex)
            {
                error = $"SetInputType(ADTS{(withByteRate ? "+字节率" : "")}) 0x{ex.HResult:X8}";
            }
            catch (Exception ex)
            {
                error = "SetInputType " + ex.Message;
            }
            finally
            {
                inType.Dispose();
            }
        }
        if (!configured) return false;

        // 输出挑 16 位 PCM（渲染端与音画同步都按 int16 处理）
        var bestIndex = SelectPcm16OutputIndex(transform);
        if (bestIndex < 0)
        {
            // 没有 PCM 输出可选时退回「不指定输出类型」：部分解码器会在首个输入后自行协商
            error = "未枚举到 PCM 输出类型";
            return false;
        }

        IMFMediaType? chosen = null;
        try
        {
            chosen = transform.GetOutputAvailableType(0, bestIndex);
            transform.SetOutputType(0, chosen, 0);
        }
        catch (SharpGenException ex)
        {
            error = $"SetOutputType(PCM idx={bestIndex}) 0x{ex.HResult:X8}";
            return false;
        }
        catch (Exception ex)
        {
            error = "SetOutputType " + ex.Message;
            return false;
        }
        finally
        {
            chosen?.Dispose();
        }

        try
        {
            var info = transform.GetOutputStreamInfo(0);
            outputStreamFlags = (int)info.Flags;
            outputBufferSize = info.Size;
        }
        catch
        {
            outputStreamFlags = 0;
            outputBufferSize = 0;
        }
        return true;
    }

    /// <summary>在输出类型候选里找第一个 int16 PCM，找不到返回 -1</summary>
    private static int SelectPcm16OutputIndex(IMFTransform transform)
    {
        for (uint index = 0; index < 32; index++)
        {
            IMFMediaType? candidate = null;
            try { candidate = transform.GetOutputAvailableType(0, (int)index); }
            catch { break; }
            if (candidate == null) break;
            try
            {
                if (MftEnumerator.SafeGuid(candidate, MediaTypeAttributeKeys.Subtype) != AudioFormatGuids.Pcm)
                    continue;
                var bits = MftEnumerator.SafeUInt32(candidate, MediaTypeAttributeKeys.AudioBitsPerSample);
                if (bits != 0 && bits != 16) continue;
                return (int)index;
            }
            finally
            {
                candidate.Dispose();
            }
        }
        return -1;
    }

    /// <summary>
    /// 输出流变化（MF_E_TRANSFORM_STREAM_CHANGE / MF_E_TRANSFORM_TYPE_NOT_SET）后重新绑定 PCM16。
    /// ADTS 头里带的采样率/声道数解码器要解出第一帧才知道，可能与我们配置时的假设不同；
    /// 此时必须重新 SetOutputType，否则后续每一次 ProcessOutput 都会失败。
    /// </summary>
    private bool RebindPcm16Output()
    {
        try
        {
            var index = SelectPcm16OutputIndex(_transform);
            if (index < 0)
            {
                Logger.Warn("Audio", "AAC 输出流变化后枚举不到 PCM16 输出类型");
                return false;
            }
            IMFMediaType? chosen = null;
            try
            {
                chosen = _transform.GetOutputAvailableType(0, index);
                _transform.SetOutputType(0, chosen, 0);
            }
            finally
            {
                chosen?.Dispose();
            }
            Logger.Info("Audio", $"AAC 输出流变化，已重新绑定 PCM16（候选 #{index}）");
            return true;
        }
        catch (Exception ex)
        {
            Logger.Warn("Audio", "AAC 重新绑定输出类型失败: " + ex.Message);
            return false;
        }
    }

    /// <summary>
    /// 解码一段 ADTS 数据。timestampUtc 是该段第一个 AAC 帧对应的采集时间戳
    /// （由帧头透传，是音画同步的基准）。缓冲里若粘了多个 ADTS 帧会逐个拆开喂入。
    /// </summary>
    public void Decode(ReadOnlySpan<byte> adts, long timestampUtc)
    {
        if (_disposed || adts.Length < Adts.HeaderLength) return;

        var offset = 0;
        var frameIndex = 0;
        while (offset + Adts.HeaderLength <= adts.Length)
        {
            if (!Adts.TryReadHeader(adts.Slice(offset), out var frameLength, out _, out _))
            {
                Logger.Warn("Audio", $"ADTS 头非法（偏移 {offset}），丢弃剩余 {adts.Length - offset} 字节");
                return;
            }

            // 一个网络包里粘了多帧时，后续帧的时间戳按 1024 样本推进
            var ts = timestampUtc + (long)frameIndex * AudioStreamInfo.SamplesPerAacFrame
                * TimeSpan.TicksPerSecond / SampleRate;
            // 输入类型是 MFAudioFormat_ADTS，样本里必须带完整的 ADTS 头：
            // 剥掉头喂裸 AAC 解码器认不出来，表现是 ProcessOutput 永远「要更多输入」、一帧 PCM 都出不来
            if (offset + frameLength > adts.Length)
            {
                Logger.Warn("Audio",
                    $"ADTS 帧长 {frameLength} 超出缓冲剩余 {adts.Length - offset} 字节，丢弃");
                return;
            }
            SubmitFrame(adts.Slice(offset, frameLength), ts);
            offset += frameLength;
            frameIndex++;
        }
    }

    /// <summary>喂一个完整的 ADTS 帧（含 7 字节头）给 MFT</summary>
    private void SubmitFrame(ReadOnlySpan<byte> adtsFrame, long timestampUtc)
    {
        lock (_gate)
        {
            if (_disposed) return;

            if (adtsFrame.Length == 0) return;
            _timeline.Mark(_inputSamples, timestampUtc);
            _inputSamples += AudioStreamInfo.SamplesPerAacFrame;

            var byteLength = adtsFrame.Length;
            var duration = (long)AudioStreamInfo.SamplesPerAacFrame * TimeSpan.TicksPerSecond / SampleRate;

            var buffer = MediaFactory.MFCreateMemoryBuffer(byteLength);
            var sample = MediaFactory.MFCreateSample();
            try
            {
                buffer.Lock(out var ptr, out _, out _);
                try
                {
                    unsafe
                    {
                        fixed (byte* src = adtsFrame)
                            Buffer.MemoryCopy(src, (void*)ptr, byteLength, byteLength);
                    }
                }
                finally
                {
                    buffer.Unlock();
                }
                buffer.CurrentLength = byteLength;

                sample.AddBuffer(buffer);
                sample.SampleTime = _inputTime100Ns;
                sample.SampleDuration = duration;
                _inputTime100Ns += duration;

                try
                {
                    _transform.ProcessInput(0, sample, 0);
                }
                catch (SharpGenException ex) when (ex.HResult == MfENotAccepting)
                {
                    // 输出队列满了：先抽干再喂。抽干后仍然拒收就丢这一帧——
                    // 这条路径跑在网络接收回调上，异常抛出去会打断整条音频链路
                    DrainOutputs();
                    try
                    {
                        _transform.ProcessInput(0, sample, 0);
                    }
                    catch (SharpGenException retry)
                    {
                        DroppedInputFrames++;
                        if (DroppedInputFrames is 1 or 10 or 100 || DroppedInputFrames % 500 == 0)
                            Logger.Warn("Audio",
                                $"AAC 解码器抽干后仍拒收输入 0x{retry.HResult:X8}，累计丢弃 {DroppedInputFrames} 帧");
                    }
                }
            }
            finally
            {
                sample.Dispose();
                buffer.Dispose();
            }

            DrainOutputs();
        }
    }

    /// <summary>
    /// 每轮抽干前重读输出流信息：重协商/流变化之后 cbSize 与 dwFlags 都会跟着变，
    /// 拿配置时的旧值分配缓冲区容易撞上 MF_E_BUFFERSIZETOOSMALL。
    /// 返回本轮该分配多大的输出缓冲区。
    /// </summary>
    private int RefreshOutputStreamInfo()
    {
        try
        {
            var info = _transform.GetOutputStreamInfo(0);
            var flags = (int)info.Flags;
            if (flags != _outputStreamFlags)
            {
                Logger.Info("Audio", $"AAC 输出流 flags 变化: 0x{_outputStreamFlags:X} → 0x{flags:X}");
                _outputStreamFlags = flags;
            }
            if (info.Size > 0) _outputBufferSize = info.Size;
        }
        catch
        {
            // 读不到就沿用上一轮的值，不影响解码
        }
        return _outputBufferSize > 0 ? _outputBufferSize : DefaultOutputBufferSize;
    }

    private void DrainOutputs()
    {
        var renegotiations = 0;
        var flips = 0;
        // 带上限：解码器异常返回时不至于在音频线程上死循环
        for (var round = 0; round < MaxDrainRounds; round++)
        {
            var bufferSize = RefreshOutputStreamInfo();
            var outBuf = new OutputDataBuffer { StreamID = 0 };
            Result result;
            try
            {
                if (_callerAllocatesOutput)
                {
                    var outSample = MediaFactory.MFCreateSample();
                    var outBuffer = MediaFactory.MFCreateMemoryBuffer(bufferSize);
                    outSample.AddBuffer(outBuffer);
                    outBuffer.Dispose();
                    outBuf.Sample = outSample;
                }
                result = _transform.ProcessOutput(ProcessOutputFlags.None, 1, ref outBuf, out _);
            }
            catch (SharpGenException ex) when (ex.HResult is MfETransformNeedMoreInput
                or MfETransformStreamChange or MfETransformTypeNotSet)
            {
                outBuf.Sample?.Dispose();
                if (ex.HResult == MfETransformNeedMoreInput) return;
                if (++renegotiations > MaxRenegotiations || !RebindPcm16Output()) return;
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
                if (result.Code == MfETransformNeedMoreInput) return;
                if (result.Code is MfETransformStreamChange or MfETransformTypeNotSet)
                {
                    if (++renegotiations > MaxRenegotiations || !RebindPcm16Output()) return;
                    continue;
                }
                if (result.Code == EInvalidArg && flips < MaxAllocationFlips)
                {
                    // 输出样本的分配方猜反了：翻一次并记住，避免之后每帧都白撞一次
                    flips++;
                    _callerAllocatesOutput = !_callerAllocatesOutput;
                    Logger.Warn("Audio",
                        $"AAC ProcessOutput 报 E_INVALIDARG，输出样本改由 {(_callerAllocatesOutput ? "调用方" : "MFT")} 分配");
                    continue;
                }
                // 音频是尽力而为的旁路：这里不抛异常，否则会把网络接收回调一起打断
                Logger.Warn("Audio", $"AAC ProcessOutput 失败: 0x{result.Code:X8}");
                return;
            }

            var sample = outBuf.Sample;
            if (sample == null) return;
            try { EmitSample(sample); }
            catch (Exception ex) { Logger.Warn("Audio", "AAC 解码输出处理异常: " + ex.Message); }
            finally { sample.Dispose(); }
        }
        Logger.Warn("Audio", $"AAC 单次抽干超过 {MaxDrainRounds} 轮，提前结束");
    }

    private void EmitSample(IMFSample sample)
    {
        using var buffer = sample.ConvertToContiguousBuffer();
        buffer.Lock(out var ptr, out _, out var length);
        byte[] pcmBytes;
        try
        {
            if (length <= 0) return;
            pcmBytes = new byte[length];
            Marshal.Copy(ptr, pcmBytes, 0, length);
        }
        finally
        {
            try { buffer.Unlock(); } catch { }
        }

        var samples = pcmBytes.Length / 2;
        var frames = samples / Channels;
        if (frames <= 0) return;

        var pcm = new short[frames * Channels];
        Buffer.BlockCopy(pcmBytes, 0, pcm, 0, frames * Channels * 2);

        var timestampUtc = _timeline.UtcFor(_outputSamples);
        _outputSamples += frames;
        DecodedChunks++;
        DecodedSamples += frames;

        Decoded?.Invoke(new DecodedAudioChunk
        {
            Data = pcm,
            Frames = frames,
            SampleRate = SampleRate,
            Channels = Channels,
            TimestampUtc = timestampUtc,
        });
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        lock (_gate)
        {
            try
            {
                _transform.ProcessMessage(TMessageType.MessageCommandFlush, UIntPtr.Zero);
            }
            catch { }
            try { _transform.Dispose(); } catch { }
        }
        MfRuntime.Shutdown();
    }
}
