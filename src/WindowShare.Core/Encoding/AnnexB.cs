namespace WindowShare.Core.Encoding;

/// <summary>
/// Annex-B 码流解析：起始码扫描、NAL 类型提取、关键帧判断。
/// Media Foundation 编码器输出即 Annex-B 格式（00 00 00 01 + NAL），H.264 与 HEVC 起始码一致，
/// 区别只在 NAL 头：H.264 一个字节（类型 = 低 5 位），HEVC 两个字节（类型 = 第一个字节高 6 位）。
/// </summary>
public static class AnnexB
{
    private const byte StartCode0 = 0x00;

    /// <summary>单个 NAL 单元（Type 已按 codec 解出）</summary>
    public readonly record struct NalUnit(int Offset, int Length, int Type);

    /// <summary>
    /// 扫描 NAL 单元（支持 3 字节与 4 字节起始码）。
    /// </summary>
    public static List<NalUnit> SplitNals(ReadOnlySpan<byte> data, VideoCodec codec = VideoCodec.H264)
    {
        var list = new List<NalUnit>();
        var i = 0;
        int headerLen;
        while (FindStartCode(data, i, out var start, out headerLen))
        {
            var nalStart = start + headerLen;
            var next = nalStart;
            FindStartCode(data, nalStart, out var nextStart, out _);
            var nalEnd = nextStart >= 0 ? nextStart : data.Length;
            if (nalEnd > nalStart)
            {
                var type = codec == VideoCodec.Hevc
                    ? (data[nalStart] >> 1) & 0x3F                    // HEVC：2 字节头，类型在首字节高 6 位
                    : data[nalStart] & 0x1F;                          // H.264：1 字节头，类型低 5 位
                list.Add(new NalUnit(nalStart, nalEnd - nalStart, type));
            }
            i = nalStart;
            if (nextStart < 0) break;
            next = nextStart;
            i = next;
        }
        return list;
    }

    /// <summary>从 offset 起查找起始码（00 00 01 或 00 00 00 01）</summary>
    private static bool FindStartCode(ReadOnlySpan<byte> data, int from, out int start, out int headerLen)
    {
        start = -1;
        headerLen = 0;
        if (from < 0) from = 0;
        for (var i = from; i + 2 < data.Length; i++)
        {
            if (data[i] == StartCode0 && data[i + 1] == StartCode0 && data[i + 2] == 0x01)
            {
                // 4 字节起始码（前面还有一个 00）
                if (i > from && data[i - 1] == StartCode0 && start != i - 1)
                {
                    start = i - 1;
                    headerLen = 4;
                    return true;
                }
                start = i;
                headerLen = 3;
                return true;
            }
        }
        return false;
    }

    /// <summary>是否关键帧：H.264 IDR（type=5）；HEVC IRAP（BLA/CRA/IDR，type 16–21）</summary>
    public static bool IsKeyframe(ReadOnlySpan<byte> data, VideoCodec codec = VideoCodec.H264)
    {
        foreach (var nal in SplitNals(data, codec))
        {
            if (codec == VideoCodec.Hevc)
            {
                if (nal.Type is >= 16 and <= 21) return true; // BLA_W_LP..CRA_N_LP（IRAP 图像）
            }
            else if (nal.Type == 5) return true;              // IDR
        }
        return false;
    }

    /// <summary>是否包含参数集：H.264 SPS=7/PPS=8；HEVC VPS=32/SPS=33/PPS=34</summary>
    public static bool ContainsParameterSets(ReadOnlySpan<byte> data, VideoCodec codec = VideoCodec.H264)
    {
        var hasVps = false;
        var hasSps = false;
        var hasPps = false;
        foreach (var nal in SplitNals(data, codec))
        {
            if (codec == VideoCodec.Hevc)
            {
                if (nal.Type == 32) hasVps = true;
                if (nal.Type == 33) hasSps = true;
                if (nal.Type == 34) hasPps = true;
            }
            else
            {
                if (nal.Type == 7) hasSps = true;
                if (nal.Type == 8) hasPps = true;
            }
        }
        return codec == VideoCodec.Hevc ? hasVps && hasSps && hasPps : hasSps && hasPps;
    }

    /// <summary>首帧合法性：必须以起始码开头且含参数集或关键帧 NAL</summary>
    public static bool IsValidStreamStart(ReadOnlySpan<byte> data) =>
        data.Length > 4 && data[0] == 0 && data[1] == 0 && (data[2] == 1 || (data[2] == 0 && data[3] == 1));
}
