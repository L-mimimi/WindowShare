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

    /// <summary>CODECAPI_AVEncCommonRateControlMode（0=CBR）</summary>
    public static readonly Guid AvEncCommonRateControlMode = new("1C0608E9-370C-4710-8A58-CB6181C42423");

    /// <summary>CODECAPI_AVEncCommonMeanBitRate（平均码率 bit/s）</summary>
    public static readonly Guid AvEncCommonMeanBitRate = new("F7222374-2144-4815-B550-A37F8E12EE52");

    /// <summary>CODECAPI_AVEncMPVGOPSize（GOP 大小）</summary>
    public static readonly Guid AvEncMPVGopSize = new("95F31B26-95A4-41AA-9303-246A7FC6EEF1");

    /// <summary>CODECAPI_AVEncVideoForceKeyFrame（下一帧强制为关键帧）</summary>
    public static readonly Guid AvEncVideoForceKeyFrame = new("398C1B98-8353-475A-9EF2-8F265D260345");

    /// <summary>CODECAPI_AVScenarioInfo（1=DisplayRemoting，屏幕共享推荐场景）</summary>
    public static readonly Guid AvScenarioInfo = new("B28A6E64-3FF9-446A-8A4B-0D7A53413236");

    /// <summary>CODECAPI_AVEncCommonLowLatency</summary>
    public static readonly Guid AvEncCommonLowLatency = new("9D3ECD55-89E8-490A-970A-0C9548D5A56E");

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
    public static unsafe bool TrySetUint32(IntPtr comObject, Guid api, uint value)
    {
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
                    var rc = codecApi.SetValue(pApi, pValue);
                    if (rc >= 0) return true;

                    // VARIANT(VT_BOOL)：vt=11, boolValue 在偏移 8
                    *(ushort*)pValue = VtBool;
                    *(ushort*)(pValue + 8) = (ushort)(value != 0 ? 0xFFFF : 0);
                    rc = codecApi.SetValue(pApi, pValue);
                    return rc >= 0;
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
            return false;
        }
    }
}
