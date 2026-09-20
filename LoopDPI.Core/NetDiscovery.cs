using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace LoopDPI.Core;

/// <summary>
/// One active IPv4 NIC with its REAL netmask. Network/broadcast/host range
/// are computed from address+mask bytes — never assume /24.
/// </summary>
public sealed record LanNetwork(string InterfaceName, string Description, IPAddress Address, IPAddress Mask)
{
    public IPAddress Network { get; } = ApplyMask(Address, Mask);
    public IPAddress Broadcast { get; } = ApplyBroadcast(Address, Mask);
    public int PrefixLength { get; } = CountBits(Mask);

    public bool Contains(IPAddress ip)
    {
        byte[] a = Network.GetAddressBytes();
        byte[] b = ApplyMask(ip, Mask).GetAddressBytes();
        return a.SequenceEqual(b);
    }

    /// <summary>Usable host addresses (skips network+broadcast, except /31+/32).</summary>
    public IEnumerable<IPAddress> EnumerateHosts(int maxHosts = 4096)
    {
        uint net = ToUint(Network);
        uint bc = ToUint(Broadcast);
        if (PrefixLength >= 31)
        {
            yield return FromUint(net);
            if (bc != net) yield return FromUint(bc);
            yield break;
        }
        uint yielded = 0;
        for (uint h = net + 1; h < bc; h++)
        {
            if (yielded >= maxHosts) yield break; // huge subnet safety cap
            yield return FromUint(h);
            yielded++;
        }
    }

    public bool WasTruncated(int maxHosts = 4096)
    {
        if (PrefixLength >= 31) return false;
        ulong total = (ulong)ToUint(Broadcast) - ToUint(Network) - 1;
        return total > (ulong)maxHosts;
    }

    private static IPAddress ApplyMask(IPAddress ip, IPAddress mask)
    {
        byte[] a = ip.GetAddressBytes();
        byte[] m = mask.GetAddressBytes();
        byte[] r = new byte[4];
        for (int i = 0; i < 4; i++) r[i] = (byte)(a[i] & m[i]);
        return new IPAddress(r);
    }

    private static IPAddress ApplyBroadcast(IPAddress ip, IPAddress mask)
    {
        byte[] a = ip.GetAddressBytes();
        byte[] m = mask.GetAddressBytes();
        byte[] r = new byte[4];
        for (int i = 0; i < 4; i++) r[i] = (byte)(a[i] | ~m[i]);
        return new IPAddress(r);
    }

    private static int CountBits(IPAddress mask)
    {
        int n = 0;
        foreach (byte b in mask.GetAddressBytes())
            for (int i = 0; i < 8; i++)
                if ((b & (1 << (7 - i))) != 0) n++;
        return n;
    }

    private static uint ToUint(IPAddress ip)
    {
        byte[] b = ip.GetAddressBytes();
        if (BitConverter.IsLittleEndian) Array.Reverse(b);
        return BitConverter.ToUInt32(b, 0);
    }

    private static IPAddress FromUint(uint v)
    {
        byte[] b = BitConverter.GetBytes(v);
        if (BitConverter.IsLittleEndian) Array.Reverse(b);
        return new IPAddress(b);
    }
}

public sealed record ConsoleFound(string Ip, bool? Busy, string Source);

/// <summary>
/// Console discovery: UDP beacon first (short timeout), TCP sweep as
/// fallback. All valid NICs are scanned in parallel, results merged and
/// deduped by IP. No /24 assumption anywhere.
/// </summary>
public static class NetDiscovery
{
    public const int ReceiverPort = 12800;
    public const int BeaconPort = 12801;
    public const string BeaconMagic = "PKGSENDER";
    /// <summary>PC announce (reverse direction): while Publish library is on,
    /// the PC broadcasts its catalog endpoint so the console browser finds
    /// it without manual IP entry. Browsers can't hear UDP, so the receiver
    /// listens and re-serves the last announcement over HTTP (/api/pc).</summary>
    public const int PcAnnouncePort = 12802;
    public const string PcAnnounceMagic = "PKGSENDER-PC";

    public static async Task AnnouncePcAsync(string pcIp, int catalogPort, CancellationToken ct)
    {
        try
        {
            using var udp = new UdpClient();
            udp.EnableBroadcast = true;
            var ep = new IPEndPoint(IPAddress.Broadcast, PcAnnouncePort);
            byte[] msg = Encoding.ASCII.GetBytes($"{PcAnnounceMagic} {pcIp}:{catalogPort}");
            while (!ct.IsCancellationRequested)
            {
                try { await udp.SendAsync(msg, msg.Length, ep); } catch { }
                await Task.Delay(3000, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch { }
    }
    public const int MaxHostsPerNic = 4096;

    /// <summary>Every active non-loopback IPv4 NIC with its own subnet.</summary>
    public static List<LanNetwork> GetLanNetworks()
    {
        var list = new List<LanNetwork>();
        NetworkInterface[] nics;
        try { nics = NetworkInterface.GetAllNetworkInterfaces(); }
        catch { return list; }
        foreach (var nic in nics)
        {
            try
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                var props = nic.GetIPProperties();
                foreach (var uni in props.UnicastAddresses)
                {
                    if (uni.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    if (IPAddress.IsLoopback(uni.Address)) continue;
                    if (uni.IPv4Mask == null) continue;
                    // Link-local (APIPA) has no DHCP router: a console is
                    // practically never reachable there; its /16 would also
                    // blow up any sweep. Skipped deliberately.
                    byte[] ab = uni.Address.GetAddressBytes();
                    if (ab[0] == 169 && ab[1] == 254) continue;
                    string desc = "";
                    try { desc = nic.Description ?? ""; } catch { }
                    list.Add(new LanNetwork(nic.Name, desc, uni.Address, uni.IPv4Mask));
                }
            }
            catch { }
        }
        return list;
    }

    /// <summary>
    /// Virtual switches (WSL/Hyper-V/VM/VPN) never carry a console.
    /// Skipped in sweeps (still listed for PC-IP choice).
    /// </summary>
    public static bool IsVirtual(LanNetwork n)
    {
        string s = ((n.InterfaceName ?? "") + " " + (n.Description ?? "")).ToLowerInvariant();
        string[] marks = { "virtual", "hyper-v", "hyperv", "wsl", "vmware",
            "virtualbox", "vpn", "pseudo", "wireguard", "tailscale", "tap-" };
        return marks.Any(s.Contains);
    }

    /// <summary>Pick the PC address on the same subnet as the console.</summary>
    public static string? BestPcIpFor(List<LanNetwork> nets, string? psIp)
    {
        if (nets.Count == 0) return null;
        if (!string.IsNullOrWhiteSpace(psIp) && IPAddress.TryParse(psIp.Trim(), out var ps))
        {
            var same = nets.FirstOrDefault(n => n.Contains(ps));
            if (same != null) return same.Address.ToString();
        }
        return nets[0].Address.ToString();
    }

    /// <summary>Listen for receiver UDP beacons for the given duration.</summary>
    public static async Task<List<string>> ListenForBeaconsAsync(
        TimeSpan duration, Action<string>? onBeacon = null, CancellationToken ct = default)
    {
        var found = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(duration);
        try
        {
            using var udp = new UdpClient(BeaconPort);
            udp.Client.ReceiveTimeout = 500;
            await Task.Run(() =>
            {
                while (!cts.Token.IsCancellationRequested)
                {
                    try
                    {
                        IPEndPoint ep = new IPEndPoint(IPAddress.Any, 0);
                        byte[] buf = udp.Receive(ref ep);
                        string msg = Encoding.ASCII.GetString(buf);
                        if (!msg.StartsWith(BeaconMagic, StringComparison.Ordinal)) continue;
                        if (IPAddress.IsLoopback(ep.Address)) continue;
                        string ip = ep.Address.ToString();
                        if (found.TryAdd(ip, 0))
                            try { onBeacon?.Invoke(ip); } catch { }
                    }
                    catch (SocketException) { }
                    catch (ObjectDisposedException) { break; }
                }
            }, cts.Token);
        }
        catch { }
        return found.Keys.ToList();
    }

    public static async Task<(bool ApiOk, bool? Busy)> ProbeAsync(string ip, int timeoutMs = 1500)
    {
        bool ok = await ConsoleClient.IsOnlineAsync(ip).ConfigureAwait(false);
        if (!ok) return (false, null);
        try
        {
            var (supported, busy) = await ConsoleClient.GetStatusAsync(ip).ConfigureAwait(false);
            return (true, supported ? busy : (bool?)null);
        }
        catch { return (true, null); }
    }

    private static async Task<bool> TcpOpenAsync(IPAddress ip, int port, int timeoutMs, CancellationToken ct)
    {
        try
        {
            using var cl = new TcpClient();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeoutMs);
            await cl.ConnectAsync(ip, port, cts.Token).ConfigureAwait(false);
            return true;
        }
        catch { return false; }
    }

    /// <summary>Sweep every NIC subnet for the receiver port. Merge+dedupe by IP.</summary>
    public static async Task<List<ConsoleFound>> SweepAsync(
        IEnumerable<LanNetwork> nets,
        ISet<string>? skipIps = null,
        Action<string>? progress = null,
        CancellationToken ct = default)
    {
        var results = new ConcurrentDictionary<string, ConsoleFound>(StringComparer.OrdinalIgnoreCase);
        var sem = new SemaphoreSlim(128);
        var tasks = new List<Task>();
        var skipped = nets.Where(IsVirtual).ToList();
        if (skipped.Count > 0)
            try { progress?.Invoke($"Skipping virtual adapter(s): {string.Join(", ", skipped.Select(n => n.InterfaceName))}"); } catch { }
        nets = nets.Where(n => !IsVirtual(n)).ToList();
        foreach (var net in nets)
        {
            if (net.WasTruncated(MaxHostsPerNic))
                try { progress?.Invoke($"Large subnet on {net.InterfaceName} (/{net.PrefixLength}) — scanning first {MaxHostsPerNic} hosts…"); } catch { }
            foreach (var host in net.EnumerateHosts(MaxHostsPerNic))
            {
                string ip = host.ToString();
                if (skipIps != null && skipIps.Contains(ip)) continue;
                tasks.Add(Task.Run(async () =>
                {
                    await sem.WaitAsync(ct).ConfigureAwait(false);
                    try
                    {
                        if (ct.IsCancellationRequested) return;
                        bool open12800 = await TcpOpenAsync(host, ReceiverPort, 300, ct).ConfigureAwait(false);
                        bool open9090 = !open12800 && await TcpOpenAsync(host, 9090, 300, ct).ConfigureAwait(false);
                        if (!open12800 && !open9090) return;
                        var (apiOk, busy) = await ProbeAsync(ip).ConfigureAwait(false);
                        if (apiOk)
                            results[ip] = new ConsoleFound(ip, busy, "sweep");
                        else
                            results[ip] = new ConsoleFound(ip, null, "port-open");
                    }
                    finally { sem.Release(); }
                }, ct));
            }
        }
        try { progress?.Invoke($"Scanning {tasks.Count} addresses…"); } catch { }
        try { await Task.WhenAll(tasks).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        return results.Values.OrderBy(r => r.Ip).ToList();
    }

    /// <summary>
    /// Beacon (short timeout) first, sweep as fallback. Beacon hits that
    /// verify via /api win; everything merged, deduped by IP.
    /// </summary>
    public static async Task<List<ConsoleFound>> FindConsolesAsync(
        List<LanNetwork> nets,
        TimeSpan beaconTimeout,
        Action<string>? progress = null,
        Action<string>? onBeacon = null,
        CancellationToken ct = default)
    {
        var merged = new Dictionary<string, ConsoleFound>(StringComparer.OrdinalIgnoreCase);
        void Report(string s) { try { progress?.Invoke(s); } catch { } }

        Report("Listening for receiver beacons…");
        var beacons = await ListenForBeaconsAsync(beaconTimeout, onBeacon, ct).ConfigureAwait(false);
        var verified = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var ip in beacons)
        {
            if (ct.IsCancellationRequested) break;
            var (apiOk, busy) = await ProbeAsync(ip).ConfigureAwait(false);
            merged[ip] = new ConsoleFound(ip, busy, apiOk ? "beacon" : "beacon-unverified");
            if (apiOk) verified.Add(ip);
        }

        if (beacons.Count == 0)
            Report("No beacon heard — sweeping LAN as fallback…");
        else
            Report($"Beacon heard from {beacons.Count} host(s) — sweeping to be sure…");

        var swept = await SweepAsync(nets, verified, progress, ct).ConfigureAwait(false);
        foreach (var c in swept)
            if (!merged.ContainsKey(c.Ip))
                merged[c.Ip] = c;

        return merged.Values.OrderBy(c => c.Ip).ToList();
    }
}
