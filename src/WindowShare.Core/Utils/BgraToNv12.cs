namespace WindowShare.Core.Utils;

/// <summary>
/// BGRA → NV12 CPU 转换（BT.709 全范围 → 窄范围）。
/// 作为 GPU VideoProcessor 不可用时的兜底转换路径。
/// </summary>
public static unsafe class BgraToNv12
{
    private static byte Clip(double v) => (byte)(v > 255 ? 255 : v < 0 ? 0 : v);

    /// <summary>
    /// 转换 BGRA（stride=width*4，自上而下）到 NV12（stride=width）。
    /// bgra 长度 ≥ width*height*4；nv12 长度 ≥ width*height*3/2。
    /// </summary>
    public static void Convert(ReadOnlySpan<byte> bgra, int width, int height, Span<byte> nv12)
    {
        if (bgra.Length < width * height * 4)
            throw new ArgumentException($"BGRA 缓冲不足: {bgra.Length}");
        if (nv12.Length < width * height * 3 / 2)
            throw new ArgumentException($"NV12 缓冲不足: {nv12.Length}");

        fixed (byte* pBgra = bgra, pNv = nv12)
        {
            var pY = pNv;
            var pUv = pNv + (long)width * height;

            for (var row = 0; row < height; row++)
            {
                var pSrc = pBgra + (long)row * width * 4;
                var pYRow = pY + (long)row * width;
                var pUvRow = pUv + (long)(row / 2) * width;

                for (var col = 0; col < width; col++)
                {
                    var b = pSrc[col * 4 + 0];
                    var g = pSrc[col * 4 + 1];
                    var r = pSrc[col * 4 + 2];

                    // BT.709：Y = 16 + 65.481R + 128.553G + 24.966B（R/G/B 归一化）
                    pYRow[col] = Clip(16.5 + (65.481 * r + 128.553 * g + 24.966 * b) / 255.0);

                    if ((row & 1) == 0 && (col & 1) == 0)
                    {
                        var u = Clip(128.5 + (-37.797 * r - 74.203 * g + 112.0 * b) / 255.0);
                        var v = Clip(128.5 + (112.0 * r - 93.786 * g - 18.214 * b) / 255.0);
                        pUvRow[(col / 2) * 2] = (byte)u;     // U
                        pUvRow[(col / 2) * 2 + 1] = (byte)v; // V
                    }
                }
            }
        }
    }
}
