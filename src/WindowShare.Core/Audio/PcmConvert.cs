namespace WindowShare.Core.Audio;

/// <summary>
/// PCM 采样格式转换（纯函数，全部可单元测试）。
/// WASAPI 共享模式的混音格式是 float32、声道数与采样率由系统决定，而 AAC 编码器
/// 要求 int16、48 kHz、立体声，两端格式对不上，因此采集/渲染两侧都要做转换。
/// </summary>
public static class PcmConvert
{
    /// <summary>int16 → float32（-1.0 .. 1.0）</summary>
    public static void Int16ToFloat32(ReadOnlySpan<short> src, Span<float> dst)
    {
        var n = Math.Min(src.Length, dst.Length);
        for (var i = 0; i < n; i++)
            dst[i] = src[i] * (1f / 32768f);
    }

    /// <summary>float32 → int16（带饱和削顶，避免溢出翻转成爆音）</summary>
    public static void Float32ToInt16(ReadOnlySpan<float> src, Span<short> dst)
    {
        var n = Math.Min(src.Length, dst.Length);
        for (var i = 0; i < n; i++)
            dst[i] = ClampToInt16(src[i]);
    }

    /// <summary>单个 float 样本饱和到 int16</summary>
    public static short ClampToInt16(float sample)
    {
        var v = sample * 32768f;
        if (float.IsNaN(v)) return 0;
        if (v >= 32767f) return short.MaxValue;
        if (v <= -32768f) return short.MinValue;
        return (short)v;
    }

    /// <summary>
    /// 任意声道数的 float32 交织采样 → 立体声 int16。
    /// 返回实际写出的帧数（采样点组数）；单声道复制成两声道，多声道（5.1/7.1）把
    /// 后置/中置声道等权混入左右并做 -3dB 补偿，避免混音后削顶。
    /// </summary>
    public static int RemixFloat32ToInt16Stereo(ReadOnlySpan<float> src, int srcChannels, Span<short> dst)
    {
        if (srcChannels < 1) return 0;
        var srcFrames = src.Length / srcChannels;
        var outFrames = Math.Min(srcFrames, dst.Length / 2);
        for (var f = 0; f < outFrames; f++)
        {
            var b = f * srcChannels;
            float left, right;
            if (srcChannels == 1)
            {
                left = right = src[b];
            }
            else if (srcChannels == 2)
            {
                left = src[b];
                right = src[b + 1];
            }
            else
            {
                left = src[b];
                right = src[b + 1];
                var extra = 0f;
                var count = 0;
                for (var c = 2; c < srcChannels; c++) { extra += src[b + c]; count++; }
                if (count > 0)
                {
                    // 混合声道按等功率叠加（×0.7071），再整体压一次，避免多声道相加溢出
                    extra = extra / count * 0.7071f;
                    left = (left + extra) * 0.7071f;
                    right = (right + extra) * 0.7071f;
                }
            }
            dst[f * 2] = ClampToInt16(left);
            dst[f * 2 + 1] = ClampToInt16(right);
        }
        return outFrames;
    }

    /// <summary>
    /// 立体声 int16 线性重采样（任意比例）。src/dst 采样率相同时原样返回。
    /// 线性插值对语音与一般桌面音频足够，且不引入额外延迟（相比带 FIR 的重采样器）。
    /// </summary>
    public static short[] ResampleStereoInt16(ReadOnlySpan<short> src, int srcRate, int dstRate)
    {
        if (srcRate == dstRate) return src.ToArray();
        var srcFrames = src.Length / 2;
        if (srcFrames < 2) return src.ToArray();
        if (srcRate < 1 || dstRate < 1) return src.ToArray();

        var dstFrames = (int)Math.Max(1, Math.Round((long)srcFrames * dstRate / (double)srcRate));
        var dst = new short[dstFrames * 2];
        var step = (srcFrames - 1) / (double)Math.Max(1, dstFrames - 1);
        for (var i = 0; i < dstFrames; i++)
        {
            var pos = i * step;
            var i0 = (int)pos;
            if (i0 > srcFrames - 1) i0 = srcFrames - 1;
            var i1 = Math.Min(i0 + 1, srcFrames - 1);
            var frac = (float)(pos - i0);
            dst[i * 2] = (short)(src[i0 * 2] + (src[i1 * 2] - src[i0 * 2]) * frac);
            dst[i * 2 + 1] = (short)(src[i0 * 2 + 1] + (src[i1 * 2 + 1] - src[i0 * 2 + 1]) * frac);
        }
        return dst;
    }

    /// <summary>
    /// 立体声 int16 → 设备混音格式（float32、任意声道数）。返回写出的帧数。
    /// 声道数多于 2 时其余声道填静音；单声道设备做左右混音。
    /// </summary>
    public static int StereoInt16ToFloat32(ReadOnlySpan<short> src, int srcFrames, Span<float> dst, int dstChannels)
    {
        if (dstChannels < 1) return 0;
        var outFrames = Math.Min(srcFrames, dst.Length / dstChannels);
        for (var f = 0; f < outFrames; f++)
        {
            var l = src[f * 2] * (1f / 32768f);
            var r = src.Length > f * 2 + 1 ? src[f * 2 + 1] * (1f / 32768f) : l;
            var o = f * dstChannels;
            switch (dstChannels)
            {
                case 1:
                    dst[o] = (l + r) * 0.5f;
                    break;
                case 2:
                    dst[o] = l;
                    dst[o + 1] = r;
                    break;
                default:
                    dst[o] = l;
                    dst[o + 1] = r;
                    for (var c = 2; c < dstChannels; c++) dst[o + c] = 0f;
                    break;
            }
        }
        return outFrames;
    }

    /// <summary>
    /// 立体声 int16 → 设备混音格式（int16、任意声道数）。返回写出的帧数。
    /// 设备混音格式偶尔是 16 位（远端桌面/虚拟声卡），渲染端需要这一条路径。
    /// </summary>
    public static int StereoInt16ToInt16(ReadOnlySpan<short> src, int srcFrames, Span<short> dst, int dstChannels)
    {
        if (dstChannels < 1) return 0;
        var outFrames = Math.Min(srcFrames, dst.Length / dstChannels);
        for (var f = 0; f < outFrames; f++)
        {
            var l = src[f * 2];
            var r = src.Length > f * 2 + 1 ? src[f * 2 + 1] : l;
            var o = f * dstChannels;
            switch (dstChannels)
            {
                case 1:
                    dst[o] = (short)((l + r) / 2);
                    break;
                case 2:
                    dst[o] = l;
                    dst[o + 1] = r;
                    break;
                default:
                    dst[o] = l;
                    dst[o + 1] = r;
                    for (var c = 2; c < dstChannels; c++) dst[o + c] = 0;
                    break;
            }
        }
        return outFrames;
    }

    /// <summary>计算一段 int16 样本的 RMS 电平（0..1，UI 显示音量条用）</summary>
    public static float RmsLevel(ReadOnlySpan<short> samples)
    {
        if (samples.IsEmpty) return 0f;
        double sum = 0;
        for (var i = 0; i < samples.Length; i++)
        {
            var v = samples[i] / 32768.0;
            sum += v * v;
        }
        return (float)Math.Sqrt(sum / samples.Length);
    }
}
