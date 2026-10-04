using System.Runtime.InteropServices;

namespace WindowShare.Core.Encoding;

/// <summary>
/// CODECAPI 属性 GUID（与 Windows SDK codecapi.h 一致）与 ICodecAPI 最小 COM 互操作。
/// 用于：低延迟模式、CBR、码率、GOP、强制关键帧、场景（DisplayRemoting）。
/// </summary>
public static class CodecApi
{
    /// <summary>CODECAPI_AVLowLatencyMode（低延迟模式，VT_UI4/VT_BOOL 均有 MFT 接受）</summary>
    public static readonly Guid AvLowLatencyMode = new("9C27891A-ED7A-40E1-88E8-B22727A024EE");

    /// <summary>CODECAPI_AVEncCommonRateControlMode（eAVEncCommonRateControlMode：0=CBR, 3=Quality）</summary>
    public static readonly Guid AvEncCommonRateControlMode = new("1C0608E9-370C-4710-8A58-CB6181C42423");

    /// <summary>CODECAPI_AVEncCommonMeanBitRate（平均码率 bit/s）</summary>
    public static readonly Guid AvEncCommonMeanBitRate = new("F7222374-2144-4815-B550-A37F8E12EE52");

    /// <summary>CODECAPI_AVEncCommonQuality（质量模式目标质量 0-100，仅 Quality 速率控制模式有效）</summary>
    public static readonly Guid AvEncCommonQuality = new("FCBF57A3-7EA5-4B0C-9644-69B40C39C391");

    /// <summary>CODECAPI_AVEncCommonQualityVsSpeed（质量/速度权衡 1-100，小=重质量）</summary>
    public static readonly Guid AvEncCommonQualityVsSpeed = new("98332DF8-03CD-476B-89FA-3F9E442DEC9F");

    /// <summary>CODECAPI_AVEncCommonMaxBitRate（峰值码率 bit/s，PeakConstrainedVBR 用）</summary>
    public static readonly Guid AvEncCommonMaxBitRate = new("9651EAE4-39B9-4EBF-85EF-D7F444EC7465");

    /// <summary>CODECAPI_AVEncMPVGOPSize（GOP 大小）</summary>
    public static readonly Guid AvEncMPVGopSize = new("95F31B26-95A4-41AA-9303-246A7FC6EEF1");

    /// <summary>CODECAPI_AVEncVideoForceKeyFrame（下一帧强制为关键帧）</summary>
    public static readonly Guid AvEncVideoForceKeyFrame = new("398C1B98-8353-475A-9EF2-8F265D260345");

    /// <summary>CODECAPI_AVScenarioInfo（1=DisplayRemoting，屏幕共享推荐场景）</summary>
    public static readonly Guid AvScenarioInfo = new("B28A6E64-3FF9-446A-8A4B-0D7A53413236");

    /// <summary>CODECAPI_AVEncCommonLowLatency</summary>
    public static readonly Guid AvEncCommonLowLatency = new("9D3ECD55-89E8-490A-970A-0C9548D5A56E");

    /// <summary>CODECAPI_AVEncCommonRealTime（实时编码提示）</summary>
    public static readonly Guid AvEncCommonRealTime = new("143A0FF6-A131-43DA-B81E-98FBB8EC378E");

    private const ushort VtUi4 = 23;
    private const ushort VtBool = 11;

    // ICodecAPI IID
    private const string CodecApiIid = "901db4c7-31ce-41a2-85dc-8fa0bf41b8da";

    /// <summary>
    /// ICodecAPI COM 接口（vtable 顺序必须与 icodecapi.h 完全一致；只实现会用到的方法）。
    /// </summary>
    [ComImport]
    [Guid(CodecApiIid)]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ICodecApiNative
    {
        [PreserveSig] int SetAllDefaults();

        [PreserveSig] int SetValueWithNotify(IntPtr api, IntPtr value, IntPtr changed, IntPtr numChanged);

        [PreserveSig] int SetValue(IntPtr api, IntPtr value);

        [PreserveSig] int GetValue(IntPtr api, IntPtr value);

        [PreserveSig] int GetParameterRange(IntPtr api, IntPtr min, IntPtr max, IntPtr step);

        [PreserveSig] int GetParameterValues(IntPtr api, IntPtr values, IntPtr count);

        [PreserveSig] int GetDefaultValue(IntPtr api, IntPtr value);

        [PreserveSig] int GetValueWithNotify(IntPtr api, IntPtr value, IntPtr changed, IntPtr numChanged);

        [PreserveSig] int SetAllDefaultsWithNotify(IntPtr changed, IntPtr numChanged);

        [PreserveSig] int IsAvailable(IntPtr api);

        [PreserveSig] int IsModifiable(IntPtr api);
    }

    /// <summary>
    /// 对某个 COM 对象（MFT）设置 CODECAPI UINT32 属性。
    /// 先按 VT_UI4 尝试，失败再按 VT_BOOL（部分硬件 MFT 只认其中一种）。
    /// </summary>
    public static bool TrySetUint32(IntPtr comObject, Guid api, uint value) =>
        SetUint32(comObject, api, value, out _) >= 0;

    /// <summary>
    /// 设置 UINT32 属性并返回最终 HRESULT 与逐次尝试的明细（VT_UI4 → VT_BOOL）。
    /// 供诊断用：编码器拒绝调参时能看到具体失败码（如 E_NOTIMPL），而不是一个静默的 false。
    /// COM 包装失败（无法拿到 ICodecAPI）返回 E_UNEXPECTED。
    /// </summary>
    public static unsafe int SetUint32(IntPtr comObject, Guid api, uint value, out string detail)
    {
        detail = "";
        try
        {
            var codecApi = (ICodecApiNative)Marshal.GetTypedObjectForIUnknown(comObject,
                typeof(ICodecApiNative));
            try
            {
                var pApi = Marshal.AllocHGlobal(16);
                Marshal.StructureToPtr(api, pApi, false);
                var pValue = Marshal.AllocHGlobal(24);
                try
                {
                    // VARIANT(VT_UI4)：vt=23, value 在偏移 8
                    *(ushort*)pValue = VtUi4;
                    *(uint*)(pValue + 8) = value;
                    var rcUi4 = codecApi.SetValue(pApi, pValue);
                    if (rcUi4 >= 0)
                    {
                        detail = "VT_UI4";
                        return rcUi4;
                    }

                    // VARIANT(VT_BOOL)：vt=11, boolValue 在偏移 8
                    *(ushort*)pValue = VtBool;
                    *(ushort*)(pValue + 8) = (ushort)(value != 0 ? 0xFFFF : 0);
                    var rcBool = codecApi.SetValue(pApi, pValue);
                    detail = rcBool >= 0 ? "VT_BOOL" : $"UI4={Hr(rcUi4)}/BOOL={Hr(rcBool)}";
                    return rcBool;
                }
                finally
                {
                    Marshal.FreeHGlobal(pApi);
                    Marshal.FreeHGlobal(pValue);
                }
            }
            finally
            {
                Marshal.ReleaseComObject(codecApi);
            }
        }
        catch (Exception ex)
        {
            Logging.Logger.Debug("CodecAPI", $"设置属性 {api} 失败: {ex.Message}");
            detail = ex.GetType().Name;
            return UncheckedHr.EUnexpected;
        }
    }

    /// <summary>属性是否可用（IsAvailable S_OK=可用）。返回 HRESULT。</summary>
    public static unsafe int IsAvailable(IntPtr comObject, Guid api)
    {
        try
        {
            var codecApi = (ICodecApiNative)Marshal.GetTypedObjectForIUnknown(comObject,
                typeof(ICodecApiNative));
            try
            {
                var pApi = Marshal.AllocHGlobal(16);
                Marshal.StructureToPtr(api, pApi, false);
                try
                {
                    return codecApi.IsAvailable(pApi);
                }
                finally
                {
                    Marshal.FreeHGlobal(pApi);
                }
            }
            finally
            {
                Marshal.ReleaseComObject(codecApi);
            }
        }
        catch (Exception ex)
        {
            Logging.Logger.Debug("CodecAPI", $"查询属性 {api} 可用性失败: {ex.Message}");
            return UncheckedHr.EUnexpected;
        }
    }

    /// <summary>HRESULT 格式化为 0xXXXXXXXX（诊断日志用）</summary>
    public static string Hr(int hr) => $"0x{hr & 0xFFFFFFFFL:X8}";

    /// <summary>常用失败码（避免到处写 unchecked 魔数）</summary>
    internal static class UncheckedHr
    {
        public const int EUnexpected = unchecked((int)0x8000FFFF);
    }
}
