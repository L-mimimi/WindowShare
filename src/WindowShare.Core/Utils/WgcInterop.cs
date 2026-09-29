using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using WinRT;

namespace WindowShare.Core.Utils;

/// <summary>
/// Windows Graphics Capture（WGC）互操作：
///   1) 通过 IGraphicsCaptureItemInterop 从 HWND/HMONITOR 创建 GraphicsCaptureItem；
///   2) 从 WinRT IDirect3DSurface 取出 ID3D11Texture2D（零拷贝进入编码管线）；
///   3) 从 ID3D11Device 创建 WinRT IDirect3DDevice（FramePool 需要）。
/// 兼容 Windows 10 1903+（不依赖 Windows 11 才有的 TryCreateFromWindow）。
/// </summary>
public static unsafe class WgcInterop
{
    // ===== COM 接口声明 =====

    /// <summary>GraphicsCaptureItem 的激活工厂互操作接口（undocumented but stable）</summary>
    [ComImport]
    [Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        /// <summary>从窗口句柄创建捕获项（iid 必须是 GraphicsCaptureItem 的 IID）</summary>
        [PreserveSig]
        int CreateForWindow(nint window, ref Guid iid, out nint item);

        /// <summary>从显示器句柄创建捕获项</summary>
        [PreserveSig]
        int CreateForMonitor(nint monitor, ref Guid iid, out nint item);
    }

    /// <summary>从 WinRT IDirect3DSurface 取 DXGI 接口的 COM 访问器</summary>
    [ComImport]
    [Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDirect3DDxgiInterfaceAccess
    {
        /// <summary>返回 AddRef 过的 DXGI 接口指针（调用方负责 Release）</summary>
        IntPtr GetInterface(ref Guid iid);
    }

    /// <summary>GraphicsCaptureItem 运行时类的 IID</summary>
    private static readonly Guid GraphicsCaptureItemGuid = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");

    /// <summary>ID3D11Texture2D 的 IID</summary>
    private static readonly Guid Texture2dGuid = new("6F15AAF2-D208-4E89-9AB4-489535D34F9C");

    // ===== P/Invoke =====

    [DllImport("combase.dll", PreserveSig = false)]
    private static extern void WindowsCreateString(
        [MarshalAs(UnmanagedType.LPWStr)] string source, int length, out nint hstring);

    [DllImport("combase.dll", PreserveSig = false)]
    private static extern void RoGetActivationFactory(nint activatableClassId, ref Guid iid, out nint factory);

    [DllImport("combase.dll")]
    private static extern void WindowsDeleteString(nint hstring);

    [DllImport("d3d11.dll", PreserveSig = false, ExactSpelling = true, CallingConvention = CallingConvention.StdCall)]
    private static extern void CreateDirect3D11DeviceFromDXGIDevice(nint dxgiDevice, out nint graphicsDevice);

    // ===== 公共 API =====

    /// <summary>是否支持 WGC（Windows 10 1903+ 且 GraphicsCaptureSession.IsSupported）</summary>
    public static bool IsSupported()
    {
        try
        {
            // 类型存在且系统支持捕获即可；真实能力在 Start 时再验证
            return OperatingSystem.IsWindowsVersionAtLeast(10, 0, 18362);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>从窗口句柄创建 GraphicsCaptureItem</summary>
    public static Windows.Graphics.Capture.GraphicsCaptureItem CreateItemForWindow(nint hwnd)
    {
        var interop = GetInteropFactory();
        var iidItem = GraphicsCaptureItemGuid;
        var hr = interop.CreateForWindow(hwnd, ref iidItem, out var ptr);
        if (hr < 0 || ptr == nint.Zero)
            throw new InvalidOperationException($"CreateForWindow 失败 (hwnd=0x{hwnd:X}, hr=0x{hr:X8})");
        try
        {
            return Windows.Graphics.Capture.GraphicsCaptureItem.FromAbi(ptr);
        }
        finally
        {
            Marshal.Release(ptr);
        }
    }

    /// <summary>从显示器句柄创建 GraphicsCaptureItem</summary>
    public static Windows.Graphics.Capture.GraphicsCaptureItem CreateItemForMonitor(nint hmonitor)
    {
        var interop = GetInteropFactory();
        var iidItem = GraphicsCaptureItemGuid;
        var hr = interop.CreateForMonitor(hmonitor, ref iidItem, out var ptr);
        if (hr < 0 || ptr == nint.Zero)
            throw new InvalidOperationException($"CreateForMonitor 失败 (hmon=0x{hmonitor:X}, hr=0x{hr:X8})");
        try
        {
            return Windows.Graphics.Capture.GraphicsCaptureItem.FromAbi(ptr);
        }
        finally
        {
            Marshal.Release(ptr);
        }
    }

    /// <summary>从捕获帧的 Surface 提取 D3D11 纹理（零拷贝：仅 AddRef，不复制像素）</summary>
    public static ID3D11Texture2D GetTextureFromSurface(Windows.Graphics.DirectX.Direct3D11.IDirect3DSurface surface)
    {
        // WinRT 对象 → 原生 IUnknown → QI IDirect3DDxgiInterfaceAccess
        var abi = ((IWinRTObject)surface).NativeObject.ThisPtr;
        var access = (IDirect3DDxgiInterfaceAccess)Marshal.GetObjectForIUnknown(abi);
        try
        {
            var iid = Texture2dGuid;
            var texPtr = access.GetInterface(ref iid);
            if (texPtr == nint.Zero)
                throw new InvalidOperationException("GetInterface(ID3D11Texture2D) 返回空指针");
            // Vortice 包装接管引用计数（GetInterface 返回 AddRef 后的指针）
            return new ID3D11Texture2D(texPtr);
        }
        finally
        {
            Marshal.ReleaseComObject(access);
        }
    }

    /// <summary>从 D3D11 设备创建 WinRT IDirect3DDevice（FramePool 需要）</summary>
    public static Windows.Graphics.DirectX.Direct3D11.IDirect3DDevice CreateDirect3DDevice(
        ID3D11Device d3dDevice)
    {
        using var dxgiDevice = d3dDevice.QueryInterface<Vortice.DXGI.IDXGIDevice>();
        Marshal.ThrowExceptionForHR(
            CreateDirect3D11DeviceFromDXGIDevice_Hr(dxgiDevice.NativePointer, out var inspectable));
        try
        {
            return WinRT.MarshalInterface<Windows.Graphics.DirectX.Direct3D11.IDirect3DDevice>.FromAbi(inspectable);
        }
        finally
        {
            Marshal.Release(inspectable);
        }
    }

    // CreateDirect3D11DeviceFromDXGIDevice 返回 HRESULT 而非 void，包一层保留 HR
    private static int CreateDirect3D11DeviceFromDXGIDevice_Hr(nint dxgiDevice, out nint graphicsDevice)
    {
        CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice, out graphicsDevice);
        return 0; // PreserveSig=false 已抛异常，正常返回即 S_OK
    }

    /// <summary>获取 GraphicsCaptureItem 激活工厂上的互操作接口</summary>
    private static IGraphicsCaptureItemInterop GetInteropFactory()
    {
        var className = "Windows.Graphics.Capture.GraphicsCaptureItem";
        WindowsCreateString(className, className.Length, out var hstr);
        try
        {
            var iid = new Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");
            RoGetActivationFactory(hstr, ref iid, out var factoryPtr);
            return (IGraphicsCaptureItemInterop)Marshal.GetObjectForIUnknown(factoryPtr);
        }
        finally
        {
            WindowsDeleteString(hstr);
        }
    }
}
