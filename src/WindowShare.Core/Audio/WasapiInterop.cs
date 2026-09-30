using System.Runtime.InteropServices;

namespace WindowShare.Core.Audio;

/// <summary>WAVEFORMATEX 的 wFormatTag 取值</summary>
internal static class WaveFormatTag
{
    /// <summary>未知/需要看扩展字段</summary>
    public const ushort Extensible = 0xFFFE;
    /// <summary>PCM（int16/int24/int32）</summary>
    public const ushort Pcm = 0x0001;
    /// <summary>IEEE float32</summary>
    public const ushort IeeeFloat = 0x0003;
}

/// <summary>
/// WAVEFORMATEX（Pack=2 必须：Windows 头文件里就是 #pragma pack(2)，
/// 少了 Pack 会多补 2 字节填充，nAvgBytesPerSec 之后全部错位）。
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 2)]
internal struct WaveFormatEx
{
    public ushort FormatTag;
    public ushort Channels;
    public uint SamplesPerSec;
    public uint AvgBytesPerSec;
    public ushort BlockAlign;
    public ushort BitsPerSample;
    public ushort ExtraSize;

    /// <summary>WAVEFORMATEX 本体大小（18 字节，含 cbSize）</summary>
    public const int StructSize = 18;

    /// <summary>每帧字节数（声道数 × 位深/8）</summary>
    public int BytesPerFrame => BlockAlign != 0 ? BlockAlign : Channels * BitsPerSample / 8;

    /// <summary>
    /// 是否 float32 采样。共享模式的混音格式通常是 WAVE_FORMAT_EXTENSIBLE，
    /// 真正的采样格式要看扩展头里的 SubFormat，不能只看 FormatTag。
    /// </summary>
    public bool IsFloat(IntPtr extensiblePtr)
    {
        if (FormatTag == WaveFormatTag.IeeeFloat) return true;
        if (FormatTag != WaveFormatTag.Extensible) return false;
        var extra = Marshal.PtrToStructure<WaveFormatExtensibleExtra>(extensiblePtr + StructSize);
        return extra.SubFormat == Wasapi.KsDataFormatSubtypeIeeeFloat;
    }
}

/// <summary>WAVEFORMATEXTENSIBLE 紧跟在 WAVEFORMATEX 之后的扩展部分</summary>
[StructLayout(LayoutKind.Sequential, Pack = 2)]
internal struct WaveFormatExtensibleExtra
{
    public ushort ValidBitsPerSample;
    public uint ChannelMask;
    public Guid SubFormat;
}

/// <summary>
/// WASAPI / Core Audio 的 COM 互操作。
/// 只用系统自带的 COM 接口（ole32 CoCreateInstance），不引入 NAudio 等第三方包，
/// 与仓库里 WgcInterop 的做法一致：保持零新增依赖、自包含。
/// </summary>
internal static class Wasapi
{
    public static readonly Guid ClsidMMDeviceEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    public static readonly Guid IidIMMDeviceEnumerator = new("A95664D2-9614-4F35-A746-DE8DB63617E6");
    public static readonly Guid IidIAudioClient = new("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2");
    public static readonly Guid IidIAudioCaptureClient = new("C8ADBD64-E71E-48A0-A4DE-185C395CD317");
    public static readonly Guid IidIAudioRenderClient = new("F294ACFC-3146-4483-A7BF-ADDCA7C260E2");

    /// <summary>KSDATAFORMAT_SUBTYPE_IEEE_FLOAT</summary>
    public static readonly Guid KsDataFormatSubtypeIeeeFloat = new("00000003-0000-0010-8000-00AA00389B71");
    /// <summary>KSDATAFORMAT_SUBTYPE_PCM</summary>
    public static readonly Guid KsDataFormatSubtypePcm = new("00000001-0000-0010-8000-00AA00389B71");

    /// <summary>EDataFlow：扬声器/耳机等渲染端点</summary>
    public const int DataFlowRender = 0;
    /// <summary>ERole：多媒体默认设备（不是通讯默认设备，避免共享到会议软件的虚拟声卡）</summary>
    public const int RoleMultimedia = 1;

    /// <summary>AUDCLNT_SHAREMODE_SHARED：与系统混音器共享设备</summary>
    public const int ShareModeShared = 0;
    /// <summary>AUDCLNT_STREAMFLAGS_LOOPBACK：捕获渲染端点正在播放的内容（系统声音，非麦克风）</summary>
    public const int StreamFlagsLoopback = 0x00020000;

    /// <summary>AUDCLNT_BUFFERFLAGS_SILENT：该数据包全是静音，WASAPI 不给数据指针内容</summary>
    public const int BufferFlagsSilent = 0x2;

    public const uint ClsCtxInprocServer = 0x1;

    /// <summary>AUDCLNT_E_UNSUPPORTED_FORMAT：共享模式只接受混音格式</summary>
    public const int AudclntEUnsupportedFormat = unchecked((int)0x88890008);
    /// <summary>AUDCLNT_E_BUFFER_SIZE_NOT_ALIGNED：需按设备周期对齐后重试</summary>
    public const int AudclntEBufferSizeNotAligned = unchecked((int)0x88890018);
    /// <summary>AUDCLNT_E_DEVICE_INVALIDATED：默认设备被拔掉/切换</summary>
    public const int AudclntEDeviceInvalidated = unchecked((int)0x8889001B);

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int CoCreateInstance(ref Guid rclsid, IntPtr pUnkOuter, uint dwClsContext,
        ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object ppv);

    /// <summary>创建 COM 对象（失败抛出带 HRESULT 的异常，便于日志定位）</summary>
    public static T CreateInstance<T>(Guid clsid) where T : class
    {
        var iid = typeof(T).GUID;
        var hr = CoCreateInstance(ref clsid, IntPtr.Zero, ClsCtxInprocServer, ref iid, out var obj);
        if (hr != 0)
            throw new InvalidOperationException(
                $"CoCreateInstance({clsid}) 失败: 0x{hr:X8}（音频服务 Audiosrv 可能未运行）");
        return (T)obj;
    }

    /// <summary>HRESULT → 可读文本（互操作调用不抛异常，由调用方决定怎么处理）</summary>
    public static string Describe(int hr) => hr == 0 ? "S_OK" : $"0x{hr:X8}";

    /// <summary>取默认渲染端点（系统声音从这里出，loopback 也从这里采）</summary>
    public static IMMDevice GetDefaultRenderDevice()
    {
        var enumerator = CreateInstance<IMMDeviceEnumerator>(ClsidMMDeviceEnumerator);
        var hr = enumerator.GetDefaultAudioEndpoint(DataFlowRender, RoleMultimedia, out var device);
        if (hr != 0 || device == null)
            throw new InvalidOperationException(
                $"没有可用的默认音频输出设备 (HRESULT {Describe(hr)})；系统声音共享需要先有播放设备");
        return device;
    }

    /// <summary>从 IMMDevice 取 IAudioClient</summary>
    public static IAudioClient ActivateAudioClient(IMMDevice device)
    {
        var iid = IidIAudioClient;
        var hr = device.Activate(ref iid, ClsCtxInprocServer, IntPtr.Zero, out var obj);
        if (hr != 0 || obj == null)
            throw new InvalidOperationException($"激活 IAudioClient 失败 (HRESULT {Describe(hr)})");
        return (IAudioClient)obj;
    }
}

/// <summary>IMMDeviceEnumerator（只用到取默认端点；前面的方法必须按 vtable 顺序声明）</summary>
[ComImport]
[Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceEnumerator
{
    [PreserveSig]
    int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);

    [PreserveSig]
    int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
}

/// <summary>IMMDevice</summary>
[ComImport]
[Guid("D666063F-1587-4E43-81F1-B948E807363F")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDevice
{
    [PreserveSig]
    int Activate(ref Guid iid, uint clsCtx, IntPtr activationParams,
        [MarshalAs(UnmanagedType.IUnknown)] out object iface);

    [PreserveSig]
    int OpenPropertyStore(int access, out IntPtr properties);

    [PreserveSig]
    int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);

    [PreserveSig]
    int GetState(out int state);
}

/// <summary>IAudioClient（方法顺序严格对应 audioclient.h 的 vtable）</summary>
[ComImport]
[Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioClient
{
    [PreserveSig]
    int Initialize(int shareMode, int streamFlags, long bufferDuration, long periodicity,
        IntPtr format, IntPtr audioSessionGuid);

    [PreserveSig]
    int GetBufferSize(out uint numBufferFrames);

    [PreserveSig]
    int GetStreamLatency(out long latency);

    [PreserveSig]
    int GetCurrentPadding(out uint numPaddingFrames);

    [PreserveSig]
    int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closestMatch);

    [PreserveSig]
    int GetMixFormat(out IntPtr format);

    [PreserveSig]
    int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);

    [PreserveSig]
    int Start();

    [PreserveSig]
    int Stop();

    [PreserveSig]
    int Reset();

    [PreserveSig]
    int SetEventHandle(IntPtr eventHandle);

    [PreserveSig]
    int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
}

/// <summary>IAudioCaptureClient（loopback 采集用）</summary>
[ComImport]
[Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioCaptureClient
{
    [PreserveSig]
    int GetBuffer(out IntPtr dataBuffer, out uint numFramesRead, out int bufferFlags,
        out ulong devicePosition, out ulong qpcPosition);

    [PreserveSig]
    int ReleaseBuffer(uint numFramesRead);

    [PreserveSig]
    int GetNextPacketSize(out uint numFramesInNextPacket);
}

/// <summary>IAudioRenderClient（Viewer 端播放用）</summary>
[ComImport]
[Guid("F294ACFC-3146-4483-A7BF-ADDCA7C260E2")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioRenderClient
{
    [PreserveSig]
    int GetBuffer(uint numFramesRequested, out IntPtr dataBuffer);

    [PreserveSig]
    int ReleaseBuffer(uint numFramesWritten, int flags);
}
