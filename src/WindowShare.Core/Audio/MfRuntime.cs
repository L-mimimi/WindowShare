using Vortice.MediaFoundation;
using WindowShare.Core.Logging;

namespace WindowShare.Core.Audio;

/// <summary>
/// Media Foundation 全局生命周期（引用计数）。
/// MFStartup/MFShutdown 由 MF 内部再计一次数，因此音频与视频各自持有一份计数是安全的：
/// 只要每次 Start 都配对一次 Shutdown，就不会出现「一方 Shutdown 把另一方打挂」的情况。
/// </summary>
internal static class MfRuntime
{
    private static readonly object Gate = new();
    private static int _refCount;

    public static void Startup()
    {
        lock (Gate)
        {
            if (_refCount++ == 0)
            {
                try
                {
                    // 必须完整初始化：LITE 模式下部分 MFT（含 AAC）会拒绝配置媒体类型
                    MediaFactory.MFStartup(false);
                    Logger.Debug("MF", "MFStartup(完整) 完成 [audio]");
                }
                catch
                {
                    _refCount--;
                }
            }
        }
    }

    public static void Shutdown()
    {
        lock (Gate)
        {
            if (--_refCount == 0)
            {
                try { MediaFactory.MFShutdown(); } catch { }
            }
        }
    }
}

/// <summary>枚举 MFT 激活对象的公共辅助（音频编解码共用）</summary>
internal static class MftEnumerator
{
    public const uint EnumFlagSyncMft = 0x01;
    public const uint EnumFlagHardware = 0x04;
    public const uint EnumFlagLocalMft = 0x10;
    public const uint EnumFlagSortAndFilter = 0x40;
    public const uint EnumFlagAll = 0x3F;

    /// <summary>枚举某一类 MFT 的激活对象（调用方负责 Dispose 每一个）</summary>
    public static List<IMFActivate> Enumerate(Guid category, uint flags)
    {
        var result = new List<IMFActivate>();
        MediaFactory.MFTEnumEx(category, flags, null, null, out var ptrs, out var count);
        for (var i = 0; i < count; i++)
        {
            var p = System.Runtime.InteropServices.Marshal.ReadIntPtr(ptrs, i * System.Runtime.InteropServices.Marshal.SizeOf<IntPtr>());
            if (p != IntPtr.Zero) result.Add(new IMFActivate(p));
        }
        System.Runtime.InteropServices.Marshal.FreeCoTaskMem(ptrs);
        return result;
    }

    /// <summary>读激活对象的友好名（失败返回 &lt;unknown&gt;）</summary>
    public static string SafeName(IMFActivate activate)
    {
        try { return activate.GetString(TransformAttributeKeys.MftFriendlyNameAttribute); }
        catch { return "<unknown>"; }
    }

    /// <summary>激活成 IMFTransform（失败抛异常）</summary>
    public static IMFTransform Activate(IMFActivate activate)
    {
        IMFTransform? transform = null;
        activate.ActivateObject(out transform).CheckError();
        return transform ?? throw new InvalidOperationException("激活 MFT 返回空对象");
    }

    /// <summary>读 UINT32 属性（缺失/类型不符时返回 0，不抛异常）</summary>
    public static uint SafeUInt32(IMFAttributes attrs, Guid key)
    {
        try { return attrs.GetUInt32(key); }
        catch { return 0; }
    }

    /// <summary>读 GUID 属性（缺失时返回 Empty）</summary>
    public static Guid SafeGuid(IMFAttributes attrs, Guid key)
    {
        try { return attrs.GetGUID(key); }
        catch { return Guid.Empty; }
    }
}
