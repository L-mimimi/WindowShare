using WindowShare.Core.Logging;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace WindowShare.Core.Utils;

/// <summary>
/// 共享 D3D11 设备：捕获（WGC/DXGI）与编码（MF 硬件编码器）复用同一设备，
/// 避免 GPU 内存拷贝与设备间同步 —— 这是零拷贝路径的前提。
/// </summary>
public static class D3D11DevicePool
{
    private static readonly object Gate = new();
    private static ID3D11Device? _device;
    private static Windows.Graphics.DirectX.Direct3D11.IDirect3DDevice? _winrtDevice;

    /// <summary>获取或创建硬件 D3D11 设备（BGRA 支持，WPF/DXGI/WGC 互操作必需）</summary>
    public static ID3D11Device GetOrCreate()
    {
        lock (Gate)
        {
            if (_device != null)
                return _device;

            // 首选硬件；WARP 软件渲染作为最后兜底（远程桌面/无 GPU 环境）
            foreach (var driver in new[] { DriverType.Hardware, DriverType.Warp })
            {
                try
                {
                    // 不启用 Debug 层：需要可选安装的 Graphics Tools，缺失会导致创建失败
                    var flags = DeviceCreationFlags.BgraSupport;
                    var result = D3D11.D3D11CreateDevice(
                        IntPtr.Zero, driver, flags,
                        new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0, FeatureLevel.Level_10_1 },
                        out var device);
                    if (result.Success && device != null)
                    {
                        _device = device;
                        Logger.Info("D3D11", $"创建 D3D11 设备成功: driver={driver}, FL={device.FeatureLevel}");
                        return _device;
                    }
                    Logger.Warn("D3D11", $"D3D11CreateDevice({driver}) 返回失败: {result}");
                    device?.Dispose();
                }
                catch (Exception ex)
                {
                    Logger.Warn("D3D11", $"D3D11CreateDevice({driver}) 失败: {ex.Message}");
                }
            }
            throw new InvalidOperationException("无法创建 D3D11 设备（硬件与 WARP 均失败）");
        }
    }

    /// <summary>获取共享设备对应的 WinRT IDirect3DDevice（WGC FramePool 需要，单例缓存）</summary>
    public static Windows.Graphics.DirectX.Direct3D11.IDirect3DDevice GetOrCreateWinRTDevice()
    {
        lock (Gate)
        {
            var d3d = GetOrCreate();
            if (_winrtDevice == null)
                _winrtDevice = WgcInterop.CreateDirect3DDevice(d3d);
            return _winrtDevice;
        }
    }
}
