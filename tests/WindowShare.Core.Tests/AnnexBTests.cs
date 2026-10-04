using WindowShare.Core.Encoding;
using Xunit;

namespace WindowShare.Core.Tests;

public class AnnexBTests
{
    private static byte[] WithStartCode(byte[] nal)
    {
        var buf = new byte[4 + nal.Length];
        buf[0] = buf[1] = buf[2] = 0;
        buf[3] = 1;
        nal.CopyTo(buf, 4);
        return buf;
    }

    // ===== H.264（1 字节 NAL 头，类型 = 低 5 位）=====

    [Fact]
    public void H264_IdrFrame_IsKeyframe()
    {
        // IDR (5) + PPS (8)
        var data = WithStartCode(new byte[] { 0x65, 0x01, 0x02 })
                 .Concat(WithStartCode(new byte[] { 0x68, 0x03 })).ToArray();
        Assert.True(AnnexB.IsKeyframe(data, VideoCodec.H264));
    }

    [Fact]
    public void H264_NonIdrFrame_NotKeyframe()
    {
        // 非 IDR 切片 (1)
        var data = WithStartCode(new byte[] { 0x41, 0x01, 0x02 });
        Assert.False(AnnexB.IsKeyframe(data, VideoCodec.H264));
    }

    [Fact]
    public void H264_SpsPps_Detected()
    {
        var data = WithStartCode(new byte[] { 0x67, 0x01 })
                 .Concat(WithStartCode(new byte[] { 0x68, 0x02 })).ToArray();
        Assert.True(AnnexB.ContainsParameterSets(data, VideoCodec.H264));
    }

    [Fact]
    public void H264_MissingPps_NotDetected()
    {
        var data = WithStartCode(new byte[] { 0x67, 0x01 });
        Assert.False(AnnexB.ContainsParameterSets(data, VideoCodec.H264));
    }

    [Fact]
    public void H264_SplitNals_ExtractsTypes()
    {
        var data = WithStartCode(new byte[] { 0x67, 0x01 })
                 .Concat(WithStartCode(new byte[] { 0x68, 0x02 }))
                 .Concat(WithStartCode(new byte[] { 0x65, 0x03 })).ToArray();
        var nals = AnnexB.SplitNals(data, VideoCodec.H264);
        Assert.Equal(3, nals.Count);
        Assert.Equal(7, nals[0].Type);
        Assert.Equal(8, nals[1].Type);
        Assert.Equal(5, nals[2].Type);
    }

    // ===== HEVC（2 字节 NAL 头，类型 = 首字节高 6 位）=====

    [Theory]
    [InlineData(16)] // BLA_W_LP
    [InlineData(19)] // IDR_W_RADL
    [InlineData(20)] // IDR_N_LP
    [InlineData(21)] // CRA_NUT
    public void Hevc_IrapTypes_AreKeyframe(int type)
    {
        // 首字节 = type << 1（LayerId 高位为 0）
        var data = WithStartCode(new byte[] { (byte)(type << 1), 0x01, 0x02 });
        Assert.True(AnnexB.IsKeyframe(data, VideoCodec.Hevc));
    }

    [Theory]
    [InlineData(0)]  // TRAIL_N
    [InlineData(1)]  // TRAIL_R
    [InlineData(32)] // VPS（不是图像）
    public void Hevc_NonIrapTypes_NotKeyframe(int type)
    {
        var data = WithStartCode(new byte[] { (byte)(type << 1), 0x01, 0x02 });
        Assert.False(AnnexB.IsKeyframe(data, VideoCodec.Hevc));
    }

    [Fact]
    public void Hevc_VpsSpsPps_Detected()
    {
        var data = WithStartCode(new byte[] { 0x40, 0x01 })  // VPS=32
                 .Concat(WithStartCode(new byte[] { 0x42, 0x01 }))  // SPS=33
                 .Concat(WithStartCode(new byte[] { 0x44, 0x01 })).ToArray(); // PPS=34
        Assert.True(AnnexB.ContainsParameterSets(data, VideoCodec.Hevc));
    }

    [Fact]
    public void Hevc_MissingVps_NotDetected()
    {
        var data = WithStartCode(new byte[] { 0x42, 0x01 })  // SPS
                 .Concat(WithStartCode(new byte[] { 0x44, 0x01 })).ToArray(); // PPS
        Assert.False(AnnexB.ContainsParameterSets(data, VideoCodec.Hevc));
    }

    [Fact]
    public void Hevc_SplitNals_ExtractsTypes()
    {
        var data = WithStartCode(new byte[] { 0x40, 0x01 })               // 32 VPS
                 .Concat(WithStartCode(new byte[] { 0x26, 0x01, 0x02 }))  // 19 IDR_W_RADL
                 .Concat(WithStartCode(new byte[] { 0x02, 0x01 })).ToArray(); // 1 TRAIL_R
        var nals = AnnexB.SplitNals(data, VideoCodec.Hevc);
        Assert.Equal(3, nals.Count);
        Assert.Equal(32, nals[0].Type);
        Assert.Equal(19, nals[1].Type);
        Assert.Equal(1, nals[2].Type);
    }

    // ===== 交叉污染防护：同一字节流按不同 codec 解析必须不同 =====

    [Fact]
    public void H264_IdrByte_InterpretedAsHevc_IsNotIrap()
    {
        // H.264 IDR（0x65 → 类型 5）；按 HEVC 解析类型 = 0x65>>1 = 50（非法/保留），不是 IRAP
        var data = WithStartCode(new byte[] { 0x65, 0x01, 0x02 });
        Assert.True(AnnexB.IsKeyframe(data, VideoCodec.H264));
        Assert.False(AnnexB.IsKeyframe(data, VideoCodec.Hevc));
    }

    // ===== Wire name 往返 =====

    [Fact]
    public void WireNames_Roundtrip()
    {
        Assert.Equal("h264", VideoCodec.H264.ToWireName());
        Assert.Equal("hevc", VideoCodec.Hevc.ToWireName());
        Assert.Equal(VideoCodec.Hevc, VideoCodecs.FromWireName("hevc"));
        Assert.Equal(VideoCodec.H264, VideoCodecs.FromWireName("h264"));
        // 旧端/未知值一律回退 H.264
        Assert.Equal(VideoCodec.H264, VideoCodecs.FromWireName(null));
        Assert.Equal(VideoCodec.H264, VideoCodecs.FromWireName(""));
        Assert.Equal(VideoCodec.H264, VideoCodecs.FromWireName("garbage"));
    }
}
