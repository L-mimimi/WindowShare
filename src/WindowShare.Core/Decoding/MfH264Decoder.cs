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

    private const int MfETransformNeedMoreInput = unchecked((int)0xC00D6D72);
    private const int MfETransformStreamChange = unchecked((int)0xC00D6D61);

    private static readonly object MfGate = new();
    private static int _mfRefCount;

    private readonly IMFTransform _transform;
    private readonly object _gate = new();
    private bool _disposed;
    private long _decodedFrames;

    /// <summary>解码输出（解码线程上触发）</summary>
    public event Action<DecodedVideoFrame>? Decoded;

    /// <summary>解码输出宽高（首个输出后可知）</summary>
    public int OutputWidth { get; private set; }
    public int OutputHeight { get; private set; }

    public long DecodedFrames => Interlocked.Read(ref _decodedFrames);

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
        activate.ActivateObject(out IMFTransform transform).CheckError();
        activate.Dispose();
        _transform = transform;

        // 输入类型：从 MFT 枚举的可用类型中挑选 H264（解码器规范：先输入类型，后输出类型）
        var inType = PickAvailableType(isInput: true, VideoFormatGuids.H264);
        _transform.SetInputType(0, inType, 0);
        inType.Dispose();

        // 输出类型：NV12（分辨率未知时用默认值，STREAM_CHANGE 时 MFT 会给出实际值）
        var outType = PickAvailableType(isInput: false, VideoFormatGuids.NV12)
                      ?? CreateNv12Placeholder();
        _transform.SetOutputType(0, outType, 0);
        outType.Dispose();

        _transform.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, UIntPtr.Zero);
        _transform.ProcessMessage(TMessageType.MessageNotifyStartOfStream, UIntPtr.Zero);
        Logging.Logger.Info("Decoder", "H.264 解码器已就绪");
    }

    /// <summary>从 MFT 枚举的可用类型中挑选指定子类型</summary>
    private IMFMediaType? PickAvailableType(bool isInput, Guid subtype)
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
                if (t.GetGUID(MediaTypeAttributeKeys.Subtype) == subtype) return t;
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
            finally
            {
                sample.Dispose();
            }

            DrainOutputs(timestampUtc);
        }
    }

    private void DrainOutputs(long timestampUtc)
    {
        while (true)
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
                return;
            }
            catch (SharpGenException ex) when (ex.HResult == MfETransformStreamChange)
            {
                outBuf.Sample?.Dispose();
                // 分辨率已确定 → 重新协商输出类型
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
                if (result.Code == MfETransformNeedMoreInput) return;
                if (result.Code == MfETransformStreamChange) { RenegotiateOutput(); continue; }
                result.CheckError();
                return;
            }

            var sample = outBuf.Sample;
            if (sample == null) return;
            try
            {
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

    /// <summary>STREAM_CHANGE 后重新读取输出类型（获取实际分辨率）</summary>
    private void RenegotiateOutput()
    {
        // STREAM_CHANGE 时 MFT 的可用输出类型携带真实分辨率，必须用它（裸占位类型会被拒绝）
        var outType = PickAvailableType(isInput: false, VideoFormatGuids.NV12) ?? CreateNv12Placeholder();
        _transform.SetOutputType(0, outType, 0);
        outType.Dispose();

        try
        {
            var cur = _transform.GetOutputCurrentType(0);
            if (cur != null)
            {
                using var _ = cur;
                try
                {
                    var size = cur.GetUInt64(MediaTypeAttributeKeys.FrameSize);
                    // MF_MT_FRAME_SIZE：高 32 位=宽，低 32 位=高
                    OutputWidth = (int)(size >> 32);
                    OutputHeight = (int)(size & 0xFFFFFFFF);
                }
                catch
                {
                    OutputWidth = 0;
                    OutputHeight = 0;
                }
            }
        }
        catch { }
        if (OutputWidth > 0)
            Logging.Logger.Info("Decoder", $"解码输出分辨率: {OutputWidth}x{OutputHeight}");
    }

    private void Emit(IMFSample sample, long fallbackTimestamp)
    {
        using var buffer = sample.ConvertToContiguousBuffer();
        buffer.Lock(out var ptr, out _, out var length);
        try
        {
            if (length <= 0 || OutputWidth <= 0) return;
            var w = OutputWidth;
            var h = OutputHeight;
            var needed = w * h * 3 / 2;
            if (length < needed) return; // 数据不完整，丢弃

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
