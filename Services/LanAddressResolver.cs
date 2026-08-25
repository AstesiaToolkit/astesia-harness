using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace AstesiaHarness.Services;

/// <summary>
/// 局域网地址解析（T7 增强）：过滤虚拟网卡（Radmin VPN / Tailscale / WireGuard / Hyper-V / WSL / TUN 等）
/// 与虚拟网段（CGNAT 100.64.0.0/10、198.18.0.0/15），返回真实可达的 IPv4 地址（私有网段优先）。
///
/// 背景：DSH 的 resolveLanTrust 只显示 lanAddresses[0]（OS 枚举顺序任意），
/// 常把 Radmin VPN 等虚拟网卡 IP 排在最前，导致手机访问错误地址。本类自行枚举并过滤。
/// </summary>
public static class LanAddressResolver
{
    private static readonly string[] VirtualAdapterMarkers =
    {
        "radmin", "tailscale", "wireguard", "wintun", "tun", "tap-", "tap ",
        "virtual", "hyper-v", "wsl", "docker", "vmware", "vbox", "bluetooth",
        "loopback", "pseudo", "wi-fi direct", "wan miniport",
    };

    /// <summary>解析可达的局域网 IPv4 列表（私有网段优先、其余按字母序、去重）。</summary>
    public static List<string> ResolveLanAddresses()
    {
        var found = new List<string>();
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                var label = ni.Name + " " + ni.Description;
                if (VirtualAdapterMarkers.Any(m => label.Contains(m, StringComparison.OrdinalIgnoreCase))) continue;

                try
                {
                    foreach (var addr in ni.GetIPProperties().UnicastAddresses)
                    {
                        if (addr.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                        var ip = addr.Address.ToString();
                        if (IPAddress.IsLoopback(addr.Address)) continue;
                        if (IsVirtualOnlyRange(ip)) continue;
                        found.Add(ip);
                    }
                }
                catch (Exception) { }
            }
        }
        catch (Exception) { }

        return found
            .Distinct()
            .OrderByDescending(IsPrivateLanRange)
            .ThenBy(ip => ip, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>CGNAT 100.64.0.0/10（Tailscale 等）与 198.18.0.0/15（基准/TUN）等虚拟网段。</summary>
    private static bool IsVirtualOnlyRange(string ip)
    {
        if (!IPAddress.TryParse(ip, out var parsed)) return true;
        var bytes = parsed.GetAddressBytes();
        if (bytes.Length != 4) return true;
        var b0 = bytes[0];
        var b1 = bytes[1];
        return (b0 == 100 && b1 is >= 64 and <= 127)
            || (b0 == 198 && b1 is 18 or 19);
    }

    /// <summary>常见私有局域网网段（10/8、172.16/12、192.168/16）。</summary>
    private static bool IsPrivateLanRange(string ip)
    {
        if (!IPAddress.TryParse(ip, out var parsed)) return false;
        var bytes = parsed.GetAddressBytes();
        if (bytes.Length != 4) return false;
        var b0 = bytes[0];
        var b1 = bytes[1];
        return b0 == 10
            || (b0 == 172 && b1 is >= 16 and <= 31)
            || (b0 == 192 && b1 == 168);
    }
}
