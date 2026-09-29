using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace WindowShare.Core.Network;

/// <summary>本机网络辅助：枚举可直连的 LAN 端点（信令注册用）</summary>
public static class LocalEndpoints
{
    /// <summary>
    /// 枚举本机所有私网 IPv4 地址 + 端口（如 192.168.1.10:48750）。
    /// 信令服务器把这些端点转给观看者，同网段观看者可优先 TCP 直连。
    /// </summary>
    public static List<string> GetLanEndpoints(int port)
    {
        var result = new List<string>();
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;

                foreach (var addr in ni.GetIPProperties().UnicastAddresses)
                {
                    if (addr.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    var ip = addr.Address;
                    if (IPAddress.IsLoopback(ip)) continue;
                    // 只上报私网段（RFC1918 + CGNAT），避免泄漏公网地址到信令服务器
                    var b = ip.GetAddressBytes();
                    var isPrivate = b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                                    || (b[0] == 192 && b[1] == 168) || (b[0] == 100 && b[1] >= 64 && b[1] <= 127);
                    if (!isPrivate) continue;

                    var ep = $"{ip}:{port}";
                    if (!result.Contains(ep)) result.Add(ep);
                }
            }
        }
        catch (Exception ex)
        {
            Logging.Logger.Warn("Net", "枚举本机端点失败: " + ex.Message);
        }
        return result;
    }
}
