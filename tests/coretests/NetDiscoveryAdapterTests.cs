using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using LoopDPI.Core;
using Xunit;

namespace LoopDPI.Core.Tests;

/// <summary>
/// Multi-adapter PC-IP selection (issue #16): with LAN + Wi-Fi both up,
/// the wired adapter must win the default PC-IP pick and lead the list;
/// virtual switches never win; a console-IP hint always overrides.
/// </summary>
public class NetDiscoveryAdapterTests
{
    private static LanNetwork Net(string name, string ip, NetworkInterfaceType type,
        string mask = "255.255.255.0", string desc = "") =>
        new(name, desc, IPAddress.Parse(ip), IPAddress.Parse(mask), type);

    [Fact]
    public void BestPcIp_ConsoleHint_WinsOverEverything()
    {
        var nets = new List<LanNetwork>
        {
            Net("Ethernet", "192.168.1.10", NetworkInterfaceType.Ethernet),
            Net("Wi-Fi", "10.0.0.5", NetworkInterfaceType.Wireless80211),
        };
        // Console lives on the Wi-Fi subnet: that adapter must be picked
        // even though wired ranks first for the default.
        Assert.Equal("10.0.0.5", NetDiscovery.BestPcIpFor(nets, "10.0.0.200"));
    }

    [Fact]
    public void BestPcIp_NoHint_PrefersFirstNonVirtual()
    {
        // OS enumerated Wi-Fi first (pre-fix behavior: nets[0] won).
        var nets = new List<LanNetwork>
        {
            Net("Wi-Fi", "192.168.0.5", NetworkInterfaceType.Wireless80211),
            Net("Ethernet", "192.168.1.10", NetworkInterfaceType.Ethernet),
        };
        // No hint: BestPcIpFor skips nothing by rank itself, but callers
        // pass GetLanNetworks() output (sorted, wired first). Mimic that
        // by feeding the sorted order and asserting the wired address.
        var sorted = nets.OrderBy(n => n.Type == NetworkInterfaceType.Ethernet ? 0
            : n.Type == NetworkInterfaceType.Wireless80211 ? 1 : 2).ToList();
        Assert.Equal("192.168.1.10", NetDiscovery.BestPcIpFor(sorted, null));
    }

    [Fact]
    public void BestPcIp_SkipsVirtualAdapterForDefault()
    {
        // Only Wi-Fi + a Hyper-V virtual switch: the virtual one must
        // never become the default PC IP.
        var nets = new List<LanNetwork>
        {
            Net("vEthernet (Hyper-V)", "192.168.137.1", NetworkInterfaceType.Ethernet, desc: "Hyper-V Virtual Ethernet Adapter"),
            Net("Wi-Fi", "192.168.0.5", NetworkInterfaceType.Wireless80211),
        };
        Assert.Equal("192.168.0.5", NetDiscovery.BestPcIpFor(nets, null));
    }

    [Fact]
    public void BestPcIp_VirtualOnly_FallsBackToFirst()
    {
        var nets = new List<LanNetwork>
        {
            Net("WSL", "172.30.0.1", NetworkInterfaceType.Ethernet, desc: "WSL Virtual Switch"),
        };
        Assert.Equal("172.30.0.1", NetDiscovery.BestPcIpFor(nets, null));
    }

    [Fact]
    public void BestPcIp_EmptyList_ReturnsNull()
    {
        Assert.Null(NetDiscovery.BestPcIpFor(new List<LanNetwork>(), "192.168.1.50"));
    }

    [Fact]
    public void IsVirtual_DetectsKnownSwitches()
    {
        Assert.True(NetDiscovery.IsVirtual(Net("vEthernet (WSL)", "172.30.0.1", NetworkInterfaceType.Ethernet)));
        Assert.True(NetDiscovery.IsVirtual(Net("tailscale0", "100.2.3.4", NetworkInterfaceType.Tunnel)));
        Assert.False(NetDiscovery.IsVirtual(Net("Ethernet", "192.168.1.10", NetworkInterfaceType.Ethernet)));
        Assert.False(NetDiscovery.IsVirtual(Net("Wi-Fi", "192.168.0.5", NetworkInterfaceType.Wireless80211)));
    }

    [Fact]
    public void GetLanNetworks_RealAdapters_SortedWiredFirst()
    {
        // Runs against the machine's actual NICs. On a single-adapter host
        // this is trivially green; wherever multiple adapters exist the
        // invariant must hold: wired < wireless < other < virtual.
        var nets = NetDiscovery.GetLanNetworks();
        int Rank(LanNetwork n) => n.Type switch
        {
            NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet
                or NetworkInterfaceType.FastEthernetT or NetworkInterfaceType.FastEthernetFx => 0,
            NetworkInterfaceType.Wireless80211 => 1,
            _ => 2,
        };
        for (int i = 0; i < nets.Count; i++)
        for (int j = i + 1; j < nets.Count; j++)
        {
            int ri = NetDiscovery.IsVirtual(nets[i]) ? 3 : Rank(nets[i]);
            int rj = NetDiscovery.IsVirtual(nets[j]) ? 3 : Rank(nets[j]);
            Assert.True(ri <= rj,
                $"adapter order broken: {nets[i].InterfaceName}({ri}) before {nets[j].InterfaceName}({rj})");
        }
    }

    [Fact]
    public void GetLanNetworks_NoLoopbackOrLinkLocal()
    {
        var nets = NetDiscovery.GetLanNetworks();
        foreach (var n in nets)
        {
            Assert.False(IPAddress.IsLoopback(n.Address));
            byte[] b = n.Address.GetAddressBytes();
            Assert.False(b[0] == 169 && b[1] == 254, $"link-local leaked: {n.Address}");
        }
    }
}
