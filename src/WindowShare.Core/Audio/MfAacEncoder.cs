using System.Runtime.InteropServices;
using SharpGen.Runtime;
using Vortice.MediaFoundation;
using WindowShare.Core.Logging;

namespace WindowShare.Core.Audio;

/// <summary>
/// Media Foundation AAC-LC 音频编码器（同步 MFT，系统自带 "Microsoft AAC Audio Encoder MFT"）。
///
/// 输入：48 kHz / 立体声 / int16 PCM。
/// 输出：裸 AAC 帧，本地补 7 字节 ADTS 头（见 <see cref="Adts"/>）后发出。
/// 选 ADTS 而不是 RTP/裸流：ADTS 每帧自带采样率与声道数，Viewer 端无需带外协商，
/// 而且单个 ADTS 帧可独立解码，丢一包不会连累后续。
///
/// 输出媒体类型是从编码器自己枚举出来的 <c>GetOutputAvailableType</c> 里挑的
/// （AAC + 目标采样率/声道 + 最接近目标码率的一条），不是自己拼的：AAC 编码器对
/// 手工拼的输出类型经常直接拒绝（E_INVALIDARG）。
/// </summary>
public sealed class MfAacEncoder : IDisposable
{
    private const int MfETransformNeedMoreInput = unchecked((int)0xC00D6D72);
    private const int MfETransformStreamChange = unchecked((int)0xC00D6D61);
    private const int MfENotAccepting = unchecked((int)0xC00D36B5);
    private const int MftOutputStreamProvidesSamples = 0x1;

    private readonly IMFTransform _transform;
    private readonly bool _providesOutputSamples;
    private readonly object _gate = new();
    private readonly SampleTimeline _timeline;

    private long _inputSamples;      // 已送入编码器的绝对样本数（帧数，不是字节）
    private long _outputSamples;     // 已产出的绝对样本数
    private long _inputTime100Ns;    // 送入 MF 的单调时间线（100ns），与 UTC 无关
    private bool _disposed;
    private bool _drained;

    /// <summary>编码输出（编码器调用线程上触发，回调不要阻塞）</summary>
    public event Action<EncodedAudioFrame>? Encoded;

    /// <summary>实际选中的编码器名</summary>
    public string EncoderName { get; }

    /// <summary>实际生效的码率（编码器支持档位里最接近目标的那个）</summary>
    public int AppliedBitrateBps { get; }

    public int SampleRate { get; }
    public int Channels { get; }

    public long EncodedFrames { get; private set; }
    public long EncodedBytes { get; private set; }

    public MfAacEncoder(int sampleRate = AudioStreamInfo.SampleRate,
                        int channels = AudioStreamInfo.Channels,
                        int targetBitrateBps = AudioStreamInfo.TargetBitrateBps)
    {
        if (Adts.SamplingFrequencyIndex(sampleRate) < 0)
            throw new ArgumentOutOfRangeException(nameof(sampleRate), $"采样率 {sampleRate} 不被 ADTS 支持");
        if (channels is < 1 or > 7)
            throw new ArgumentOutOfRangeException(nameof(channels), $"声道数 {channels} 超出 1-7");

        MfRuntime.Startup();
        SampleRate = sampleRate;
        Channels = channels;
        _timeline = new SampleTimeline(sampleRate);
        EncoderName = "<unknown>";
        AppliedBitrateBps = targetBitrateBps;

        var failures = new List<string>();
        IMFTransform? chosen = null;
        var chosenName = "<unknown>";
        var chosenBitrate = targetBitrateBps;

        var activators = MftEnumerator.Enumerate(TransformCategoryGuids.AudioEncoder, MftEnumerator.EnumFlagAll);
        try
        {
            // 名字里带 AAC 的优先：其余（WMA/MP3）枚举不到 AAC 输出类型，试了也是白试
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
                    if (TryConfigure(transform, sampleRate, channels, targetBitrateBps, out var applied, out var provides, out var error))
                    {
                        chosen = transform;
                        chosenName = name;
                        chosenBitrate = applied;
                        _providesOutputSamples = provides;
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
                "本机没有可用的 AAC 音频编码器 MFT：" + string.Join(" | ", failures));
        }

        _transform = chosen;
        EncoderName = chosenName;
        AppliedBitrateBps = chosenBitrate;
        Logger.Info("Audio",
            $"AAC 编码器就绪: {EncoderName}, {sampleRate}Hz/{channels}ch, 码率 {AppliedBitrateBps / 1000}kbps, " +
            $"输出样本自带={_providesOutputSamples}");
    }

    /// <summary>枚举本机的 AAC 音频编码器名（诊断用）</summary>
    public static IReadOnlyList<string> ProbeEncoders()
    {
        var names = new List<string>();
        try
        {
            MfRuntime.Startup();
            foreach (var act in MftEnumerator.Enumerate(TransformCategoryGuids.AudioEncoder, MftEnumerator.EnumFlagAll))
            {
                names.Add(MftEnumerator.SafeName(act));
                act.Dispose();
            }
        }
        catch (Exception ex)
        {
            Logger.Warn("Audio", "枚举音频编码器失败: " + ex.Message);
        }
        return names;
    }

    /// <summary>
    /// 配置媒体类型：先设输入（PCM），再从编码器枚举出的输出类型里挑 AAC。
    /// 顺序不能反——AAC 编码器的可用输出类型取决于已设定的输入格式。
    /// </summary>
    private static bool TryConfigure(IMFTransform transform, int sampleRate, int channels, int targetBitrate,
        out int appliedBitrate, out bool providesOutputSamples, out string error)
    {
        appliedBitrate = 0;
        providesOutputSamples = false;
        error = string.Empty;

        var inType = MediaFactory.MFCreateMediaType();
        try
        {
            inType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Audio);
            inType.Set(MediaTypeAttributeKeys.Subtype, AudioFormatGuids.Pcm);
            inType.Set(MediaTypeAttributeKeys.AudioNumChannels, (uint)channels);
            inType.Set(MediaTypeAttributeKeys.AudioSamplesPerSecond, (uint)sampleRate);
            inType.Set(MediaTypeAttributeKeys.AudioBitsPerSample, 16u);
            inType.Set(MediaTypeAttributeKeys.AudioBlockAlignment, (uint)(channels * 2));
            inType.Set(MediaTypeAttributeKeys.AudioAvgBytesPerSecond, (uint)(sampleRate * channels * 2));
            inType.Set(MediaTypeAttributeKeys.AllSamplesIndependent, 1u);
            transform.SetInputType(0, inType, 0);
        }
        catch (SharpGenException ex)
        {
            error = $"SetInputType(PCM {sampleRate}Hz/{channels}ch) 0x{ex.HResult:X8}";
            return false;
        }
        catch (Exception ex)
        {
            error = "SetInputType " + ex.Message;
            return false;
        }
        finally
        {
            inType.Dispose();
        }

        // 挑一条 AAC + 同采样率/同声道 + 码率最接近目标的输出类型
        var bestIndex = -1;
        var bestDelta = int.MaxValue;
        var bestBitrate = 0;
        for (uint index = 0; index < 64; index++)
        {
            IMFMediaType? candidate = null;
            try { candidate = transform.GetOutputAvailableType(0, (int)index); }
            catch { break; }
            if (candidate == null) break;
            try
            {
                if (MftEnumerator.SafeGuid(candidate, MediaTypeAttributeKeys.Subtype) != AudioFormatGuids.Aac)
                    continue;
                var rate = MftEnumerator.SafeUInt32(candidate, MediaTypeAttributeKeys.AudioSamplesPerSecond);
                var ch = MftEnumerator.SafeUInt32(candidate, MediaTypeAttributeKeys.AudioNumChannels);
                if (rate != (uint)sampleRate || ch != (uint)channels) continue;

                var bitrate = (int)MftEnumerator.SafeUInt32(candidate, MediaTypeAttributeKeys.AvgBitrate);
                var delta = Math.Abs(bitrate - targetBitrate);
                if (bestIndex < 0 || delta < bestDelta)
                {
                    bestIndex = (int)index;
                    bestDelta = delta;
                    bestBitrate = bitrate;
                }
            }
            finally
            {
                candidate.Dispose();
            }
        }

        if (bestIndex < 0)
        {
            error = $"没有 {sampleRate}Hz/{channels}ch 的 AAC 输出类型";
            return false;
        }

        IMFMediaType? chosen = null;
        try
        {
            chosen = transform.GetOutputAvailableType(0, bestIndex);
            transform.SetOutputType(0, chosen, 0);
            appliedBitrate = bestBitrate > 0 ? bestBitrate : targetBitrate;
        }
        catch (SharpGenException ex)
        {
            error = $"SetOutputType(AAC idx={bestIndex}) 0x{ex.HResult:X8}";
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
            providesOutputSamples = (info.Flags & MftOutputStreamProvidesSamples) != 0;
        }
        catch
        {
            providesOutputSamples = false;
        }
        return true;
    }

    // ===== 输入 =====

    /// <summary>编码一段 PCM（int16 交织）。timestampUtc 是本段第一个样本的采集时间戳。</summary>
    public void Encode(short[] pcm, int frames, long timestampUtc)
    {
        if (_disposed) return;
        if (frames <= 0 || pcm.Length < frames * Channels) return;

        lock (_gate)
        {
            if (_disposed) return;

            _timeline.Mark(_inputSamples, timestampUtc);

            var byteLength = frames * Channels * 2;
            var duration = (long)frames * TimeSpan.TicksPerSecond / SampleRate;

            var buffer = MediaFactory.MFCreateMemoryBuffer(byteLength);
            var sample = MediaFactory.MFCreateSample();
            try
            {
                buffer.Lock(out var ptr, out _, out _);
                try
                {
                    Marshal.Copy(pcm, 0, ptr, frames * Channels);
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
                _inputSamples += frames;

                try
                {
                    _transform.ProcessInput(0, sample, 0);
                }
                catch (SharpGenException ex) when (ex.HResult == MfENotAccepting)
                {
                    // 编码器要求先抽干输出才肯收新输入
                    DrainOutputs();
                    _transform.ProcessInput(0, sample, 0);
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

    /// <summary>编码一个采集块（便捷重载）</summary>
    public void Encode(PcmChunk chunk) => Encode(chunk.Data, chunk.Frames, chunk.TimestampUtc);

    /// <summary>
    /// 冲刷编码器（停止共享时调用）：通知流结束并把内部滞留的最后几帧吐出来。
    /// AAC 编码器约有 2 帧（≈42ms）的 priming，不冲刷就会丢掉结尾。
    /// </summary>
    public int Drain()
    {
        if (_disposed) return 0;
        lock (_gate)
        {
            if (_disposed || _drained) return 0;
            _drained = true;
            var before = EncodedFrames;
            try
            {
                _transform.ProcessMessage(TMessageType.MessageNotifyEndOfStream, UIntPtr.Zero);
            }
            catch (Exception ex)
            {
                Logger.Debug("Audio", "AAC 编码器 NotifyEndOfStream 失败: " + ex.Message);
            }
            try { DrainOutputs(); }
            catch (Exception ex) { Logger.Warn("Audio", "AAC 编码器冲刷异常: " + ex.Message); }
            return (int)(EncodedFrames - before);
        }
    }

    // ===== 输出 =====

    private int SafeOutputBufferSize()
    {
        try
        {
            var size = _transform.GetOutputStreamInfo(0).Size;
            return size > 0 ? size : 16 * 1024;
        }
        catch
        {
            return 16 * 1024;
        }
    }

    private void DrainOutputs()
    {
        while (true)
        {
            var outBuf = new OutputDataBuffer { StreamID = 0 };
            Result result;
            try
            {
                if (!_providesOutputSamples)
                {
                    var outSample = MediaFactory.MFCreateSample();
                    var outBuffer = MediaFactory.MFCreateMemoryBuffer(SafeOutputBufferSize());
                    outSample.AddBuffer(outBuffer);
                    outBuffer.Dispose();
                    outBuf.Sample = outSample;
                }
                result = _transform.ProcessOutput(ProcessOutputFlags.None, 1, ref outBuf, out _);
            }
            catch (SharpGenException ex) when (ex.HResult == MfETransformNeedMoreInput ||
                                              ex.HResult == MfETransformStreamChange)
            {
                outBuf.Sample?.Dispose();
                if (ex.HResult == MfETransformStreamChange) continue;
                return;
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
                if (result.Code == MfETransformStreamChange) continue;
                Logger.Warn("Audio", $"AAC ProcessOutput 失败: 0x{result.Code:X8}");
                return;
            }

            var sample = outBuf.Sample;
            if (sample == null) return;
            try { EmitSample(sample); }
            catch (Exception ex) { Logger.Warn("Audio", "AAC 输出帧处理异常: " + ex.Message); }
            finally { sample.Dispose(); }
        }
    }

    private void EmitSample(IMFSample sample)
    {
        using var buffer = sample.ConvertToContiguousBuffer();
        buffer.Lock(out var ptr, out _, out var length);
        byte[] raw;
        try
        {
            if (length <= 0) return;
            raw = new byte[length];
            Marshal.Copy(ptr, raw, 0, length);
        }
        finally
        {
            try { buffer.Unlock(); } catch { }
        }

        // 输出块的时间戳按「已产出样本数」在时间线上定位，而不是用调用次数：
        // 输入 20ms 一块、输出固定 1024 样本（21.33ms）一块，两者边界不对齐。
        var frames = sample.SampleDuration > 0
            ? (int)(sample.SampleDuration * SampleRate / TimeSpan.TicksPerSecond)
            : AudioStreamInfo.SamplesPerAacFrame;
        if (frames <= 0) frames = AudioStreamInfo.SamplesPerAacFrame;
        var timestampUtc = _timeline.UtcFor(_outputSamples);
        _outputSamples += frames;

        if (raw.Length > Adts.MaxRawLength)
        {
            // 单个 AAC 帧不可能这么大，说明输出里被塞了多个访问单元，ADTS 封装不了
            Logger.Warn("Audio", $"AAC 输出 {raw.Length} 字节超出 ADTS 单帧上限，丢弃");
            return;
        }

        var adts = Adts.Wrap(raw, SampleRate, Channels);
        EncodedFrames++;
        EncodedBytes += adts.Length;
        Encoded?.Invoke(new EncodedAudioFrame
        {
            Data = adts,
            TimestampUtc = timestampUtc,
            SampleRate = SampleRate,
            Channels = Channels,
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
