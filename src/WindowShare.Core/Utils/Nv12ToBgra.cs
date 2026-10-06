namespace WindowShare.Core.Utils;

/// <summary>
/// NV12 → BGRA CPU 转换（Viewer 端解码后显示用，BT.709 窄范围 → 全范围）。
/// 指针循环 + 预计算查找表，1080p 约 2-3ms。
/// </summary>
public static unsafe class Nv12ToBgra
{
    // 系数表与 Yuv420pToBgra 共用（见 Nv12ToBgraTable）
    private static readonly int[] YTable = Nv12ToBgraTable.Y;   // 1.164*(y-16)
    private static readonly int[] VToR = Nv12ToBgraTable.VToR;  // 1.596*(v-128)
    private static readonly int[] VToG = Nv12ToBgraTable.VToG;  // -0.813*(v-128)
    private static readonly int[] UToG = Nv12ToBgraTable.UToG;  // -0.391*(u-128)
    private static readonly int[] UToB = Nv12ToBgraTable.UToB;  // 2.018*(u-128)

    private static byte Clip(int v) => (byte)(v > 0xFF0000 ? 0xFF : v < 0 ? 0 : v >> 16);

    /// <summary>
    /// 转换 NV12（stride=width）到 BGRA（stride=width*4）。
    /// nv12 长度需 ≥ width*height*3/2；bgra 长度需 ≥ width*visibleRows*4。
    ///
    /// <paramref name="visibleRows"/> 小于 <paramref name="height"/> 时只写前若干行（宏块填充裁剪）：
    /// 超过 visibleRows 的行**不再写入**，同时读取也停在最后一行所需的范围——调用方可以用
    /// 按 visibleRows 分配的 BGRA 缓冲接住，而不必为填充行买单。
    /// </summary>
    public static void Convert(ReadOnlySpan<byte> nv12, int width, int height, Span<byte> bgra,
        int? visibleRows = null)
    {
        var rows = Math.Clamp(visibleRows ?? height, 1, height);
        if (nv12.Length < width * height * 3 / 2)
            throw new ArgumentException($"NV12 缓冲不足: {nv12.Length} < {width * height * 3 / 2}");
        if (bgra.Length < width * rows * 4)
            throw new ArgumentException($"BGRA 缓冲不足: {bgra.Length} < {width * rows * 4}");

        fixed (byte* pNv = nv12)
        fixed (byte* pOut = bgra)
        fixed (int* pY = YTable, pVR = VToR, pVG = VToG, pUG = UToG, pUB = UToB)
        {
            var pYPlane = pNv;
            var pUvPlane = pNv + (long)width * height;
            var uvStride = width / 2;

            for (var row = 0; row < rows; row++)
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
    public static void ConvertToPointer(ReadOnlySpan<byte> nv12, int width, int height, IntPtr bgraTarget,
        int? visibleRows = null)
    {
        if (bgraTarget == IntPtr.Zero) throw new ArgumentNullException(nameof(bgraTarget));
        var rows = Math.Clamp(visibleRows ?? height, 1, height);
        Convert(nv12, width, height,
            new Span<byte>((void*)bgraTarget, width * rows * 4), rows);
    }
}
