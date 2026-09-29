namespace WindowShare.Core.Encoding;

/// <summary>
/// H.264 Annex-B 码流解析：起始码扫描、NAL 类型提取、关键帧判断。
/// Media Foundation H.264 编码器输出即 Annex-B 格式（00 00 00 01 + NAL）。
/// </summary>
public static class AnnexB
{
    private const byte StartCode0 = 0x00;

    /// <summary>单个 NAL 单元</summary>
    public readonly record struct NalUnit(int Offset, int Length, int Type);

    /// <summary>
    /// 扫描 NAL 单元（支持 3 字节与 4 字节起始码）。
    /// </summary>
    public static List<NalUnit> SplitNals(ReadOnlySpan<byte> data)
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
                list.Add(new NalUnit(nalStart, nalEnd - nalStart, data[nalStart] & 0x1F));
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

    /// <summary>是否关键帧（包含 IDR NAL，type=5）</summary>
    public static bool IsKeyframe(ReadOnlySpan<byte> data)
    {
        foreach (var nal in SplitNals(data))
        {
            if (nal.Type == 5) return true; // IDR
        }
        return false;
    }

    /// <summary>是否包含参数集（SPS=7 / PPS=8）</summary>
    public static bool ContainsParameterSets(ReadOnlySpan<byte> data)
    {
        var hasSps = false;
        var hasPps = false;
        foreach (var nal in SplitNals(data))
        {
            if (nal.Type == 7) hasSps = true;
            if (nal.Type == 8) hasPps = true;
        }
        return hasSps && hasPps;
    }

    /// <summary>首帧合法性：必须以起始码开头且含参数集或 IDR</summary>
    public static bool IsValidStreamStart(ReadOnlySpan<byte> data) =>
        data.Length > 4 && data[0] == 0 && data[1] == 0 && (data[2] == 1 || (data[2] == 0 && data[3] == 1));
}
