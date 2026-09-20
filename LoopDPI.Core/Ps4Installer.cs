using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace LoopDPI.Core;

/// <summary>
/// PS4 install paths, ported from marcussacana/DirectPackageInstaller:
/// 1) Remote Package Installer API at 12800 (/api/install)
/// 2) etaHEN DPIv2 API at 12800 (/upload multipart)
/// 3) GoldHEN Payload Server at 9090 + bin injection on 9090/9021/9020.
/// Auto-detect order: RPI -> etaHEN -> GoldHEN.
/// </summary>
public static class Ps4Installer
{
    private static readonly HttpClient Http = new(new HttpClientHandler { UseProxy = false })
    {
        Timeout = TimeSpan.FromSeconds(15)
    };

    private static async Task<string?> GetBodyAsync(string url, int ms = 3000)
    {
        try
        {
            using var cts = new CancellationTokenSource(ms);
            using var resp = await Http.GetAsync(url, cts.Token);
            return await resp.Content.ReadAsStringAsync(cts.Token);
        }
        catch { return null; }
    }

    public static async Task<bool> IsRpiOnlineAsync(string ip)
    {
        string? body = await GetBodyAsync($"http://{ip}:12800/api");
        return body != null && body.Contains("Unsupported method") && body.Contains("fail");
    }

    public static async Task<bool> IsEtaHenOnlineAsync(string ip)
    {
        string? body = await GetBodyAsync($"http://{ip}:12800/");
        return body != null && body.Contains("etaHEN");
    }

    public static async Task<bool> IsGoldHenOnlineAsync(string ip)
    {
        string? body = await GetBodyAsync($"http://{ip}:9090/status");
        return body != null && body.Replace(" ", "").Contains("\"status\":\"ready\"");
    }

    public static async Task<string> DetectAsync(string ip)
    {
        if (await IsRpiOnlineAsync(ip)) return "rpi";
        if (await IsEtaHenOnlineAsync(ip)) return "etahen";
        if (await IsGoldHenOnlineAsync(ip)) return "goldhen";
        // raw binloader ports still count as goldhen-capable
        if (await CanConnectPayloadPortAsync(ip)) return "goldhen";
        return "offline";
    }

    public static async Task<(bool Ok, string Method, string Reply)> PushAutoAsync(
        string psIp, string pcIp, string fileUrl, PkgInfo pkg, int fileServerPort = 9898)
    {
        string method = await DetectAsync(psIp);
        switch (method)
        {
            case "rpi":
            {
                var (ok, reply) = await PushRpiAsync(psIp, fileUrl);
                return (ok, "rpi", reply);
            }
            case "etahen":
            {
                var (ok, reply) = await PushEtaHenAsync(psIp, fileUrl);
                return (ok, "etahen", reply);
            }
            case "goldhen":
            {
                var (ok, reply) = await PushGoldHenAsync(psIp, pcIp, fileUrl, pkg, fileServerPort);
                return (ok, "goldhen", reply);
            }
            default:
                return (false, "offline", "no reply on 12800/9090/9021/9020 — enable RPI or GoldHEN Payload Server");
        }
    }

    /// <summary>
    /// GoldHEN manifest JSON (same shape as DPI's RegisterJSON, single piece).
    /// The PS4-side payload fetches this and feeds pieces[] to BGFT —
    /// a raw PKG URL is NOT enough (BGFT 0x80990033).
    /// </summary>
    public static string BuildManifest(string fileUrl, long fileSize)
    {
        string eu = fileUrl.Replace("\\", "\\\\").Replace("\"", "\\\"");
        return "{\"originalFileSize\":" + fileSize
            + ",\"packageDigest\":\"\""
            + ",\"numberOfSplitFiles\":1"
            + ",\"pieces\":[{\"url\":\"" + eu + "\""
            + ",\"fileOffset\":0"
            + ",\"fileSize\":" + fileSize
            + ",\"hashValue\":\"0000000000000000000000000000000000000000\"}]}";
    }

    public static async Task<(bool Ok, string Reply)> PushRpiAsync(string psIp, string fileUrl)
    {
        try
        {
            string enc = Uri.EscapeDataString(fileUrl.Replace("https://", "http://"));
            string json = $"{{\"type\":\"direct\",\"packages\":[\"{enc}\"]}}";
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var resp = await Http.PostAsync($"http://{psIp}:12800/api/install", content);
            string body = await resp.Content.ReadAsStringAsync();
            return (body.Contains("\"success\""), body);
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    public static async Task<(bool Ok, string Reply)> PushEtaHenAsync(string psIp, string fileUrl)
    {
        try
        {
            string boundary = "------LoopDPI_" + Guid.NewGuid().ToString("N")[..16];
            using var content = new MultipartFormDataContent(boundary);
            content.Add(new StringContent("", Encoding.UTF8, "application/octet-stream"), "\"file\"", "\"\"");
            content.Add(new StringContent(fileUrl, Encoding.UTF8), "\"url\"");
            using var resp = await Http.PostAsync($"http://{psIp}:12800/upload", content);
            string body = await resp.Content.ReadAsStringAsync();
            if (body.Contains("0x80990085"))
                body += "\nVerify free space on the console.";
            return (body.Contains("SUCCESS:"), body);
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    // ---- GoldHEN path: inject ps4_dpi_payload.bin, then send PKG info ----

    private static byte[] LoadPayload()
    {
        // embedded resource first, file fallback (dev tree)
        var asm = typeof(Ps4Installer).Assembly;
        foreach (var name in asm.GetManifestResourceNames())
        {
            if (name.EndsWith("ps4_dpi_payload.bin", StringComparison.OrdinalIgnoreCase))
            {
                using var s = asm.GetManifestResourceStream(name)!;
                using var ms = new MemoryStream();
                s.CopyTo(ms);
                return ms.ToArray();
            }
        }
        string[] candidates =
        {
            Path.Combine(AppContext.BaseDirectory, "ps4_dpi_payload.bin"),
            "/app/pkg-sender/LoopDPI.Core/ps4_dpi_payload.bin",
            "D:\\OpenCode\\pkg-sender\\LoopDPI.Core\\ps4_dpi_payload.bin",
        };
        foreach (var p in candidates)
            if (File.Exists(p)) return File.ReadAllBytes(p);
        return Array.Empty<byte>();
    }

    private static async Task<Socket?> ConnectPayloadAsync(string ip)
    {
        foreach (int port in new[] { 9090, 9021, 9020 })
        {
            var sock = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
            {
                NoDelay = true,
                SendTimeout = 3000,
                ReceiveTimeout = 3000,
            };
            try
            {
                using var cts = new CancellationTokenSource(3000);
                await sock.ConnectAsync(new IPEndPoint(IPAddress.Parse(ip), port), cts.Token);
                if (sock.Connected) return sock;
            }
            catch { sock.Dispose(); }
        }
        return null;
    }

    private static async Task<bool> CanConnectPayloadPortAsync(string ip)
        => (await ConnectPayloadAsync(ip)) is Socket s ? CloseAnd(s, true) : false;

    private static bool CloseAnd(Socket s, bool v)
    {
        try { s.Shutdown(SocketShutdown.Both); } catch { }
        s.Close();
        return v;
    }

    /// <summary>
    /// GoldHEN install: local callback listener + payload inject + PKG info struct.
    /// fileUrl must already be the PC file-server URL (RangeFileServer.UrlFor).
    /// The binloader drops connections and goes deaf often, so this retries
    /// the whole inject a few times. Safe rule: once the console called back
    /// (an install started there) we NEVER re-inject — that would duplicate
    /// the install. Only silent attempts are retried.
    /// </summary>
    public static async Task<(bool Ok, string Reply)> PushGoldHenAsync(
        string psIp, string pcIp, string fileUrl, PkgInfo pkg, int fileServerPort = 9898, int timeoutSec = 15, int attempts = 3)
    {
        byte[] payload = LoadPayload();
        if (payload.Length == 0)
            return (false, "ps4_dpi_payload.bin missing");
        int marker = IndexOf(payload, new byte[] { 0xB4, 0xB4, 0xB4, 0xB4, 0xB4, 0xB4 });
        if (marker < 0)
            return (false, "payload marker not found");

        string lastErr = "";
        for (int a = 1; a <= attempts; a++)
        {
            var (ok, reply, callbackSeen) = await TryPushGoldHenOnceAsync(
                psIp, pcIp, fileUrl, pkg, timeoutSec, payload, marker, a, attempts);
            if (ok)
                return (true, reply);
            lastErr = reply;
            if (callbackSeen)
                return (false, reply); // started on console — never duplicate
            if (a < attempts)
            {
                try { await Task.Delay(2000 * a); } catch { }
            }
        }
        return (false, lastErr + $" (after {attempts} tries — re-enable the GoldHEN Payload Server / BinLoader on the console and retry)");
    }

    private static async Task<(bool Ok, string Reply, bool CallbackSeen)> TryPushGoldHenOnceAsync(
        string psIp, string pcIp, string fileUrl, PkgInfo pkg, int timeoutSec,
        byte[] payload, int marker, int attempt, int attempts)
    {
        string tag = attempts > 1 ? $" [try {attempt}/{attempts}]" : "";
        // callback listener (PS4 connects back with PKG info request)
        using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            listener.Bind(new IPEndPoint(IPAddress.Any, 0));
            listener.Listen(1);
        }
        catch (Exception ex)
        {
            return (false, "PC listener failed: " + ex.Message + tag, false);
        }
        int cbPort = ((IPEndPoint)listener.LocalEndPoint!).Port;

        // patch PC IP + callback port into payload
        byte[] patched = (byte[])payload.Clone();
        try
        {
            IPAddress.Parse(pcIp).GetAddressBytes().CopyTo(patched, marker);
        }
        catch
        {
            return (false, "bad PC IP for payload patch: " + pcIp + tag, false);
        }
        byte[] portBytes = BitConverter.GetBytes((ushort)cbPort);
        if (BitConverter.IsLittleEndian) Array.Reverse(portBytes);
        portBytes.CopyTo(patched, marker + 4);

        using var ps = await ConnectPayloadAsync(psIp);
        if (ps == null)
            return (false, "binloader closed on 9090/9021/9020 — re-enable the GoldHEN Payload Server / BinLoader" + tag, false);

        try { ps.SendBufferSize = patched.Length; } catch { }
        try
        {
            int sent = 0;
            while (sent < patched.Length)
                sent += ps.Send(patched, sent, patched.Length - sent, SocketFlags.None);
            if (sent != patched.Length)
                return (false, "payload short-send" + tag, false);
        }
        catch (Exception ex)
        {
            return (false, "payload send failed: " + ex.Message + tag, false);
        }
        finally
        {
            try { ps.Shutdown(SocketShutdown.Both); } catch { }
            ps.Close();
        }

        // wait for PS4 callback
        Socket? cb;
        try
        {
            using var cts = new CancellationTokenSource(timeoutSec * 1000);
            cb = await listener.AcceptAsync(cts.Token);
        }
        catch
        {
            return (false, "payload sent but console did not call back (PC IP / firewall?)" + tag, false);
        }
        using (cb)
        {
            cb.NoDelay = true;
            byte[] urlB = Encoding.UTF8.GetBytes(fileUrl);
            byte[] nameB = Encoding.UTF8.GetBytes(string.IsNullOrWhiteSpace(pkg.Title) ? pkg.TitleId : pkg.Title);
            byte[] idB = Encoding.UTF8.GetBytes(pkg.ContentId ?? "");
            // BGFT wants PS4-prefixed type (PS4GD/PS4AC...), like DPI's BGFTContentType.
            // Raw category ("gd") or empty (PS5 fallback) gives BGFT 0x80990033.
            string cat = (pkg.ContentType ?? "").Trim().ToUpperInvariant();
            if (cat.StartsWith("PS4")) { }
            else if (cat.Length == 0) cat = "PS4GD";
            else cat = "PS4" + cat;
            byte[] typeB = Encoding.UTF8.GetBytes(cat);
            byte[] sizeB = BitConverter.GetBytes(pkg.PackageSize);
            byte[] iconB = pkg.IconData ?? Array.Empty<byte>();

            using var ms = new MemoryStream();
            void U32(uint v) => ms.Write(BitConverter.GetBytes(v));
            void Blob(byte[] b) { U32((uint)b.Length); ms.Write(b, 0, b.Length); }
            U32(1); // new package
            Blob(urlB); Blob(nameB); Blob(idB); Blob(typeB);
            ms.Write(sizeB, 0, sizeB.Length);
            if (iconB.Length == 0) U32(0); else Blob(iconB);

            byte[] buf = ms.ToArray();
            try
            {
                int sent = 0;
                while (sent < buf.Length)
                    sent += cb.Send(buf, sent, buf.Length - sent, SocketFlags.None);
            }
            catch (Exception ex) { return (false, "callback send failed: " + ex.Message + tag, true); }
        }
        return (true, "Package Sent via GoldHEN" + tag, true);
    }

    private static int IndexOf(byte[] hay, byte[] needle)
    {
        for (int i = 0; i + needle.Length <= hay.Length; i++)
        {
            bool ok = true;
            for (int j = 0; j < needle.Length; j++)
                if (hay[i + j] != needle[j]) { ok = false; break; }
            if (ok) return i;
        }
        return -1;
    }
}
