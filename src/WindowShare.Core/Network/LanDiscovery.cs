using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace WindowShare.Core.Network;

/// <summary>发现到的共享端（Host 广播的一条记录）</summary>
public sealed record DiscoveredHost(string Address, int Port, string Name, DateTimeOffset LastSeenUtc);

/// <summary>
/// LAN 组播发现（对标 RustDesk 的局域网设备发现）：
///   Host 共享期间周期性向组播组发 announce（设备名 + TCP 端口）；Viewer 被动监听即可。
///
/// 安全考量：announce 只暴露「这台机器正在共享，端口是多少」——与端口扫描等价的信息，
/// 不含密码/房间号/设备 ID，接入仍走完整的三步握手认证。
///
/// 传输参数：组播 239.255.87.83:48751（组织本地范围），TTL=1 不出网段，
/// 开回环以便同机冒烟测试与多网卡本机自见。
/// </summary>
public static class LanDiscovery
{
    /// <summary>组播组地址</summary>
    public static readonly IPAddress MulticastGroup = IPAddress.Parse("239.255.87.83");

    /// <summary>组播端口（TCP 共享端口 48750 + 1）</summary>
    public const int UdpPort = 48751;

    /// <summary>Host 广播间隔</summary>
    public static readonly TimeSpan AnnounceInterval = TimeSpan.FromSeconds(2);

    /// <summary>Viewer 判定条目过期的时长（约 3 个周期未见即认为已停止）</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(6);

    /// <summary>报文 magic：'W''S''H''1''D'，后接 1 字节协议版本 + UTF-8 JSON</summary>
    internal static readonly byte[] Magic = { 0x57, 0x53, 0x48, 0x31, 0x44 };

    internal const byte ProtocolVersion = 1;

    /// <summary>单包 JSON 上限（设备名会被裁剪，正常 < 200 字节）</summary>
    internal const int MaxPayloadBytes = 512;
}

/// <summary>announce 负载（纯逻辑，可单测）：与线上 JSON 字段一一对应</summary>
public sealed record DiscoveryPayload(string Name, int Port)
{
    /// <summary>编码为 JSON（裁剪设备名，防御异常输入）</summary>
    public string ToJson() =>
        JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["name"] = ClampName(Name),
            ["port"] = ClampPort(Port),
        });

    /// <summary>解析 JSON；字段缺失/类型不对返回 null</summary>
    public static DiscoveryPayload? FromJson(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (!root.TryGetProperty("name", out var nameEl) ||
                !root.TryGetProperty("port", out var portEl)) return null;
            if (nameEl.ValueKind != JsonValueKind.String ||
                portEl.ValueKind != JsonValueKind.Number) return null;
            if (!portEl.TryGetInt32(out var port)) return null;
            return new DiscoveryPayload(ClampName(nameEl.GetString() ?? ""), ClampPort(port));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal static string ClampName(string name)
    {
        name = name.Trim();
        return name.Length <= 64 ? name : name[..64];
    }

    internal static int ClampPort(int port) =>
        port is >= 1 and <= 65535 ? port : LanShareServer.DefaultPort;
}

/// <summary>报文编解码（纯逻辑，可单测）：magic + version + JSON</summary>
public static class DiscoveryPacket
{
    /// <summary>编码 announce 报文</summary>
    public static byte[] Encode(DiscoveryPayload payload)
    {
        var json = System.Text.Encoding.UTF8.GetBytes(payload.ToJson());
        var packet = new byte[LanDiscovery.Magic.Length + 1 + json.Length];
        Array.Copy(LanDiscovery.Magic, packet, LanDiscovery.Magic.Length);
        packet[LanDiscovery.Magic.Length] = LanDiscovery.ProtocolVersion;
        json.CopyTo(packet, LanDiscovery.Magic.Length + 1);
        return packet;
    }

    /// <summary>
    /// 解析报文：magic/版本/长度不合法一律返回 null（丢弃），不抛异常。
    /// </summary>
    public static DiscoveryPayload? Decode(byte[] data)
    {
        var magicLen = LanDiscovery.Magic.Length;
        if (data.Length <= magicLen + 1 || data.Length > magicLen + 1 + LanDiscovery.MaxPayloadBytes)
            return null;
        for (var i = 0; i < magicLen; i++)
        {
            if (data[i] != LanDiscovery.Magic[i]) return null;
        }
        if (data[magicLen] != LanDiscovery.ProtocolVersion) return null;
        return DiscoveryPayload.FromJson(System.Text.Encoding.UTF8.GetString(data, magicLen + 1, data.Length - magicLen - 1));
    }
}

/// <summary>
/// Host 端信标：共享开始时 <see cref="Start"/>，停止时 <see cref="Stop"/>。
/// 每 2 秒发一次 announce；发不出去（无组播路由等）静默降级——发现是锦上添花，
/// 手输 IP 直连永远可用，绝不能影响共享本身。
/// </summary>
public sealed class DiscoveryBeacon : IDisposable
{
    private readonly string _name;
    private readonly int _port;
    private UdpClient? _udp;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public DiscoveryBeacon(string name, int port)
    {
        _name = name;
        _port = port;
    }

    public void Start()
    {
        if (_loop != null) return;
        try
        {
            _udp = new UdpClient();
            _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _udp.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
            _udp.Ttl = 1;
            _udp.MulticastLoopback = true;   // 同机多端/冒烟测试需要回环
            _cts = new CancellationTokenSource();
            _loop = Task.Run(() => LoopAsync(_cts.Token));
        }
        catch (Exception)
        {
            _udp?.Dispose();
            _udp = null;
        }
    }

    public void Stop()
    {
        _cts?.Cancel();
        try { _loop?.Wait(500); } catch { /* 取消竞争无所谓 */ }
        _loop = null;
        _cts?.Dispose();
        _cts = null;
        _udp?.Dispose();
        _udp = null;
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        var target = new IPEndPoint(LanDiscovery.MulticastGroup, LanDiscovery.UdpPort);
        var packet = DiscoveryPacket.Encode(new DiscoveryPayload(_name, _port));
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await _udp!.SendAsync(packet, packet.Length, target);
            }
            catch (Exception)
            {
                // 组播不可用（AP 隔离/无路由）：本周期放弃，下周期再试
            }
            try { await Task.Delay(LanDiscovery.AnnounceInterval, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    public void Dispose() => Stop();
}

/// <summary>
/// Viewer 端监听器：加入组播组收 announce，按源 IP 去重，维护带过期时间的在线表。
/// 后台线程收包 + 按需快照；<see cref="Changed"/> 在收到新/更新条目时触发（后台线程）。
/// </summary>
public sealed class DiscoveryListener : IDisposable
{
    private readonly ConcurrentDictionary<string, (DiscoveredHost Host, long LastSeenTicks)> _hosts = new();
    private UdpClient? _udp;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private long _staleTicks = LanDiscovery.StaleAfter.Ticks;

    /// <summary>发现或刷新了某个共享端（工作线程触发，UI 自行调度）</summary>
    public event Action<DiscoveredHost>? HostSeen;

    public void Start()
    {
        if (_loop != null) return;
        try
        {
            _udp = new UdpClient();
            _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _udp.Client.Bind(new IPEndPoint(IPAddress.Any, LanDiscovery.UdpPort));
            // 在本机所有 IPv4 地址上加入组播组（多网卡都能收）
            foreach (var ip in LocalEndpoints.GetLocalIPv4Addresses())
            {
                try { _udp.JoinMulticastGroup(LanDiscovery.MulticastGroup, ip); }
                catch (Exception) { /* 个别接口不支持组播就跳过 */ }
            }
            _cts = new CancellationTokenSource();
            _loop = Task.Run(() => LoopAsync(_cts.Token));
        }
        catch (Exception)
        {
            _udp?.Dispose();
            _udp = null;
        }
    }

    public void Stop()
    {
        _cts?.Cancel();
        try { _loop?.Wait(500); } catch { }
        _loop = null;
        _cts?.Dispose();
        _cts = null;
        _udp?.Dispose();
        _udp = null;
        _hosts.Clear();
    }

    /// <summary>当前在线的共享端（已剔除过期条目，按设备名排序）</summary>
    public IReadOnlyList<DiscoveredHost> Snapshot() => Snapshot(DateTime.UtcNow.Ticks);

    /// <summary>Snapshot 的可测版本（注入时钟，避免用真实时间构造的条目被立即判过期）</summary>
    internal IReadOnlyList<DiscoveredHost> Snapshot(long nowTicks)
    {
        var list = new List<DiscoveredHost>();
        foreach (var kv in _hosts)
        {
            if (nowTicks - kv.Value.LastSeenTicks > _staleTicks)
            {
                _hosts.TryRemove(kv.Key, out _);
                continue;
            }
            list.Add(kv.Value.Host);
        }
        return list.OrderBy(h => h.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>纯逻辑：处理一包数据（可单测，注入时钟与来源）</summary>
    internal bool Process(byte[] data, string sourceAddress, long nowTicks)
    {
        var payload = DiscoveryPacket.Decode(data);
        if (payload == null) return false;
        var host = new DiscoveredHost(sourceAddress, payload.Port, payload.Name,
            new DateTimeOffset(nowTicks, TimeSpan.Zero));
        _hosts[sourceAddress] = (host, nowTicks);
        HostSeen?.Invoke(host);
        return true;
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var result = await _udp!.ReceiveAsync(ct);
                var address = (result.RemoteEndPoint as IPEndPoint)?.Address.ToString();
                if (address == null) continue;
                Process(result.Buffer, address, DateTime.UtcNow.Ticks);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception)
            {
                // 单包错误不致命；稍等避免异常风暴
                try { await Task.Delay(200, ct); } catch (OperationCanceledException) { break; }
            }
        }
    }

    public void Dispose() => Stop();
}
