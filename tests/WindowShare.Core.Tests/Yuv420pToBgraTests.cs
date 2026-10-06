using WindowShare.Core.Utils;
using Xunit;

namespace WindowShare.Core.Tests;

/// <summary>
/// YUV420P → BGRA 转换的像素正确性（FFmpeg 软解输出链）。
/// BT.709 窄范围：Y=235/U=V=128 → 全白，Y=16/U=V=128 → 全黑，Y=128/U=V=128 → 全灰。
/// </summary>
public class Yuv420pToBgraTests
{
    [Theory]
    [InlineData(16, 0, 0, 0)]        // 黑（窄范围基准黑）
    [InlineData(235, 254, 254, 254)] // 白（窄范围基准白；1.164*219 ≈ 254.9 → 254）
    [InlineData(128, 130, 130, 130)] // 灰（1.164*112 ≈ 130.4）
    public void NeutralChroma_MapsToGrayLevels(int y, int r, int g, int b)
    {
        const int width = 8, height = 8;
        var yPlane = Enumerable.Repeat((byte)y, width * height).ToArray();
        var uPlane = Enumerable.Repeat((byte)128, width * height / 4).ToArray();
        var vPlane = Enumerable.Repeat((byte)128, width * height / 4).ToArray();
        var bgra = new byte[width * height * 4];

        Yuv420pToBgra.Convert(yPlane, width, uPlane, width / 2, vPlane, width, height, bgra);

        for (var i = 0; i < width * height; i++)
        {
            Assert.Equal(r, bgra[i * 4 + 2]); // R
            Assert.Equal(g, bgra[i * 4 + 1]); // G
            Assert.Equal(b, bgra[i * 4 + 0]); // B
            Assert.Equal(0xFF, bgra[i * 4 + 3]); // A
        }
    }

    [Fact]
    public void UOnlyHigh_ShiftsTowardBlue()
    {
        const int width = 4, height = 4;
        var yPlane = Enumerable.Repeat((byte)128, width * height).ToArray();
        var uPlane = Enumerable.Repeat((byte)240, width * height / 4).ToArray(); // U 高 → 蓝
        var vPlane = Enumerable.Repeat((byte)128, width * height / 4).ToArray();
        var bgra = new byte[width * height * 4];

        Yuv420pToBgra.Convert(yPlane, width, uPlane, width / 2, vPlane, width, height, bgra);

        // 中间像素的蓝分量应显著高于红分量
        for (var i = 0; i < width * height; i++)
            Assert.True(bgra[i * 4 + 0] > bgra[i * 4 + 2],
                $"像素 {i}: B={bgra[i * 4 + 0]} 应大于 R={bgra[i * 4 + 2]}");
    }

    /// <summary>FFmpeg 帧的 linesize 含对齐填充（行距 &gt; 有效宽度）时转换仍正确</summary>
    [Fact]
    public void StridePadding_DoesNotAffectOutput()
    {
        const int width = 4, height = 4;
        var paddedWidth = 16; // 行距带填充
        var yPlane = new byte[paddedWidth * height];
        var uPlane = new byte[paddedWidth * height / 2];
        var vPlane = new byte[paddedWidth * height / 2];
        for (var row = 0; row < height; row++)
            for (var col = 0; col < width; col++)
            {
                yPlane[row * paddedWidth + col] = 235;           // 白
                uPlane[row * paddedWidth / 2 + col] = 128;
                vPlane[row * paddedWidth / 2 + col] = 128;
            }
        var bgra = new byte[width * height * 4];

        Yuv420pToBgra.Convert(yPlane, paddedWidth, uPlane, paddedWidth / 2, vPlane, width, height, bgra);

        for (var i = 0; i < width * height; i++)
        {
            Assert.Equal(254, bgra[i * 4 + 2]);
            Assert.Equal(254, bgra[i * 4 + 1]);
            Assert.Equal(254, bgra[i * 4 + 0]);
        }
    }

    /// <summary>
    /// NV12 宏块填充裁剪（v1.5.2 审计 A5）：1080 的流被硬件解码器报成 1088 时，
    /// 底部 8 行是填充，不该进画面。关键点是 **UV 平面偏移按缓冲高度 height 计算**，
    /// 只裁剪写入行数——若实现改成按可见高度定位 UV 平面，色度就会被整段错位读取。
    /// </summary>
    [Fact]
    public void Nv12_VisibleRows_CropsBottomPaddingAndKeepsChromaAligned()
    {
        const int width = 4;
        const int bufferHeight = 8;   // 宏块对齐后的缓冲高度（如 1088）
        const int visibleRows = 4;    // 真实画面高度（如 1080）
        var nv12 = new byte[width * bufferHeight * 3 / 2];
        // 前 4 行白（Y=235）、后 4 行黑（Y=16）
        for (var row = 0; row < bufferHeight; row++)
            for (var col = 0; col < width; col++)
                nv12[row * width + col] = row < visibleRows ? (byte)235 : (byte)16;
        // UV 平面（从中性色度起）全部填 128
        for (var i = width * bufferHeight; i < nv12.Length; i++) nv12[i] = 128;

        var bgra = new byte[width * visibleRows * 4];
        Nv12ToBgra.Convert(nv12, width, bufferHeight, bgra, visibleRows);

        for (var i = 0; i < width * visibleRows; i++)
        {
            Assert.Equal(254, bgra[i * 4 + 2]); // R：白
            Assert.Equal(254, bgra[i * 4 + 1]);
            Assert.Equal(254, bgra[i * 4 + 0]);
        }
    }

    /// <summary>不传 visibleRows 时行为与旧实现一致（整幅转换）</summary>
    [Fact]
    public void Nv12_WithoutVisibleRows_ConvertsWholeFrame()
    {
        const int width = 4, height = 4;
        var nv12 = new byte[width * height * 3 / 2];
        Array.Fill(nv12, (byte)128);
        for (var i = 0; i < width * height; i++) nv12[i] = 235; // Y 白
        for (var i = width * height; i < nv12.Length; i++) nv12[i] = 128;
        var bgra = new byte[width * height * 4];

        Nv12ToBgra.Convert(nv12, width, height, bgra);

        for (var i = 0; i < width * height; i++)
            Assert.Equal(254, bgra[i * 4 + 2]);
    }

    /// <summary>BGRA 缓冲按可见行数分配时不应抛异常，且越界请求必须被拒</summary>
    [Fact]
    public void Nv12_VisibleRows_RequiresOnlyVisibleSizeBuffer()
    {
        const int width = 8, height = 8;
        var nv12 = new byte[width * height * 3 / 2];
        var bgra = new byte[width * 4 * 4];   // 只够 4 行

        // 可见 4 行 → 够用
        Nv12ToBgra.Convert(nv12, width, height, bgra, 4);

        // 可见 8 行 → 缓冲不足，必须报错而不是越界写
        Assert.Throws<ArgumentException>(() => Nv12ToBgra.Convert(nv12, width, height, bgra, 8));
    }
}
