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
}
