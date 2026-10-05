namespace WindowShare.Core.Utils;

/// <summary>
/// YUV420P（三平面 I420）→ BGRA CPU 转换（FFmpeg 软解输出显示用，BT.709 窄范围 → 全范围）。
/// 与 <see cref="Nv12ToBgra"/> 同一套系数与查表，差别仅在 U/V 是独立平面、各有行距。
/// 指针循环，1080p 约 2-3ms。
/// </summary>
public static unsafe class Yuv420pToBgra
{
    // 与 Nv12ToBgra 共用同一组系数：BT.709 窄范围 → 全范围
    private static readonly int[] YTable = Nv12ToBgraTable.Y;
    private static readonly int[] VToR = Nv12ToBgraTable.VToR;
    private static readonly int[] VToG = Nv12ToBgraTable.VToG;
    private static readonly int[] UToG = Nv12ToBgraTable.UToG;
    private static readonly int[] UToB = Nv12ToBgraTable.UToB;

    /// <summary>
    /// 转换 YUV420P 到 BGRA（stride=width*4）。
    /// 各平面行距独立（FFmpeg 帧的 linesize 通常 ≥ 有效宽度）；bgra 长度需 ≥ width*height*4。
    /// </summary>
    public static void Convert(ReadOnlySpan<byte> yPlane, int yStride,
                               ReadOnlySpan<byte> uPlane, int uvStride,
                               ReadOnlySpan<byte> vPlane,
                               int width, int height, Span<byte> bgra)
    {
        if (bgra.Length < width * height * 4)
            throw new ArgumentException($"BGRA 缓冲不足: {bgra.Length} < {width * height * 4}");

        fixed (byte* pY = yPlane, pU = uPlane, pV = vPlane)
        fixed (byte* pOut = bgra)
        fixed (int* pYT = YTable, pVR = VToR, pVG = VToG, pUG = UToG, pUB = UToB)
        {
            var chromaWidth = width / 2;
            for (var row = 0; row < height; row++)
            {
                var pYRow = pY + (long)row * yStride;
                var pURow = pU + (long)(row / 2) * uvStride;
                var pVRow = pV + (long)(row / 2) * uvStride;
                var pOutRow = pOut + (long)row * width * 4;

                for (var col = 0; col < width; col++)
                {
                    var yt = pYT[pYRow[col]];
                    var ci = col / 2;
                    var u = pURow[ci];
                    var v = pVRow[ci];

                    var dst = pOutRow + col * 4;
                    dst[0] = Clip(yt + pUB[u]);            // B
                    dst[1] = Clip(yt + pVG[v] + pUG[u]);   // G
                    dst[2] = Clip(yt + pVR[v]);            // R
                    dst[3] = 0xFF;                         // A
                }
            }
        }
    }

    private static byte Clip(int v) => (byte)(v > 0xFF0000 ? 0xFF : v < 0 ? 0 : v >> 16);
}

/// <summary>Nv12ToBgra 与 Yuv420pToBgra 共用的 BT.709 窄范围→全范围系数表</summary>
internal static class Nv12ToBgraTable
{
    internal static readonly int[] Y = Build(1.164, -16);
    internal static readonly int[] VToR = Build(1.596, -128);
    internal static readonly int[] VToG = Build(-0.813, -128);
    internal static readonly int[] UToG = Build(-0.391, -128);
    internal static readonly int[] UToB = Build(2.018, -128);

    private static int[] Build(double k, double bias)
    {
        var t = new int[256];
        for (var i = 0; i < 256; i++) t[i] = (int)Math.Round(k * (i + bias) * 65536);
        return t;
    }
}
