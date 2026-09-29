namespace WindowShare.Core.Utils;

/// <summary>
/// NV12 → BGRA CPU 转换（Viewer 端解码后显示用，BT.709 窄范围 → 全范围）。
/// 指针循环 + 预计算查找表，1080p 约 2-3ms。
/// </summary>
public static unsafe class Nv12ToBgra
{
    private static readonly int[] YTable = new int[256]; // 1.164*(y-16)
    private static readonly int[] VToR = new int[256];   // 1.596*(v-128)
    private static readonly int[] VToG = new int[256];   // -0.813*(v-128)
    private static readonly int[] UToG = new int[256];   // -0.391*(u-128)
    private static readonly int[] UToB = new int[256];   // 2.018*(u-128)

    static Nv12ToBgra()
    {
        for (var i = 0; i < 256; i++)
        {
            YTable[i] = (int)Math.Round(1.164 * (i - 16) * 65536);
            VToR[i] = (int)Math.Round(1.596 * (i - 128) * 65536);
            VToG[i] = (int)Math.Round(-0.813 * (i - 128) * 65536);
            UToG[i] = (int)Math.Round(-0.391 * (i - 128) * 65536);
            UToB[i] = (int)Math.Round(2.018 * (i - 128) * 65536);
        }
    }

    private static byte Clip(int v) => (byte)(v > 0xFF0000 ? 0xFF : v < 0 ? 0 : v >> 16);

    /// <summary>
    /// 转换 NV12（stride=width）到 BGRA（stride=width*4）。
    /// nv12 长度需 ≥ width*height*3/2；bgra 长度需 ≥ width*height*4。
    /// </summary>
    public static void Convert(ReadOnlySpan<byte> nv12, int width, int height, Span<byte> bgra)
    {
        if (nv12.Length < width * height * 3 / 2)
            throw new ArgumentException($"NV12 缓冲不足: {nv12.Length} < {width * height * 3 / 2}");
        if (bgra.Length < width * height * 4)
            throw new ArgumentException($"BGRA 缓冲不足: {bgra.Length}");

        fixed (byte* pNv = nv12)
        fixed (byte* pOut = bgra)
        fixed (int* pY = YTable, pVR = VToR, pVG = VToG, pUG = UToG, pUB = UToB)
        {
            var pYPlane = pNv;
            var pUvPlane = pNv + (long)width * height;
            var uvStride = width / 2;

            for (var row = 0; row < height; row++)
            {
                var pYRow = pYPlane + (long)row * width;
                var pUvRow = pUvPlane + (long)(row / 2) * width; // UV 交错行
                var pOutRow = pOut + (long)row * width * 4;

                for (var col = 0; col < width; col++)
                {
                    var y = pYRow[col];
                    var uvIndex = (col / 2) * 2;
                    var u = pUvRow[uvIndex];
                    var v = pUvRow[uvIndex + 1];

                    var yt = pY[y];
                    var dst = pOutRow + col * 4;
                    dst[0] = Clip(yt + pUB[u]);            // B
                    dst[1] = Clip(yt + pVG[v] + pUG[u]);   // G
                    dst[2] = Clip(yt + pVR[v]);            // R
                    dst[3] = 0xFF;                         // A
                }
            }
        }
    }

    /// <summary>转换并直接写入 WriteableBitmap 后备缓冲（IntPtr 版本，避免额外拷贝）</summary>
    public static void ConvertToPointer(ReadOnlySpan<byte> nv12, int width, int height, IntPtr bgraTarget)
    {
        if (bgraTarget == IntPtr.Zero) throw new ArgumentNullException(nameof(bgraTarget));
        Convert(nv12, width, height,
            new Span<byte>((void*)bgraTarget, width * height * 4));
    }
}
